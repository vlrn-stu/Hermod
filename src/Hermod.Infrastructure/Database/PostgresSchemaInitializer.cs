using Microsoft.Extensions.Logging;
using Npgsql;

namespace Hermod.Infrastructure.Database;

/// <summary>
/// Creates the Hermod schema on first boot. Wraps the DDL in a Postgres
/// transaction-scoped advisory lock so concurrent coordinator replicas
/// serialize their <c>CREATE TABLE IF NOT EXISTS</c> sequence instead of
/// racing each other.
/// </summary>
public sealed class PostgresSchemaInitializer
{
    // AdvisoryLockKey: hard-coded in SchemaDdl so concurrent replicas contend
    // on the SAME lock key. Inlined in the DDL text because the SQL builder
    // cannot parameterize pg_advisory_xact_lock arguments from a constant.
    private const string SchemaDdl = """
        CREATE TABLE IF NOT EXISTS devices (
            id TEXT PRIMARY KEY,
            name TEXT NOT NULL,
            protocol INTEGER NOT NULL,
            status INTEGER NOT NULL,
            manufacturer TEXT,
            model TEXT,
            firmware_version TEXT,
            capabilities JSONB NOT NULL DEFAULT '{}',
            state JSONB NOT NULL DEFAULT '{}',
            last_seen TIMESTAMPTZ,
            created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
            updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
        );

        CREATE TABLE IF NOT EXISTS rules (
            id TEXT PRIMARY KEY,
            name TEXT NOT NULL,
            description TEXT,
            enabled BOOLEAN NOT NULL DEFAULT TRUE,
            priority INTEGER NOT NULL DEFAULT 100,
            trigger JSONB NOT NULL DEFAULT '{}',
            conditions JSONB,
            actions JSONB NOT NULL DEFAULT '[]',
            state JSONB NOT NULL DEFAULT '{}',
            tags JSONB NOT NULL DEFAULT '[]',
            execution_count INTEGER NOT NULL DEFAULT 0,
            last_executed_at TIMESTAMPTZ,
            last_error_at TIMESTAMPTZ,
            last_error TEXT,
            created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
            updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
        );

        CREATE INDEX IF NOT EXISTS idx_devices_protocol ON devices(protocol);
        CREATE INDEX IF NOT EXISTS idx_devices_status   ON devices(status);
        CREATE INDEX IF NOT EXISTS idx_rules_enabled    ON rules(enabled);
        CREATE INDEX IF NOT EXISTS idx_rules_priority   ON rules(priority);

        CREATE TABLE IF NOT EXISTS metrics_counters (
            name TEXT PRIMARY KEY,
            value BIGINT NOT NULL DEFAULT 0,
            updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
        );

        CREATE TABLE IF NOT EXISTS metrics_snapshots (
            id BIGSERIAL PRIMARY KEY,
            snapshot_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
            messages_processed BIGINT NOT NULL,
            rules_executed BIGINT NOT NULL,
            total_devices INTEGER NOT NULL,
            online_devices INTEGER NOT NULL,
            active_rules INTEGER NOT NULL,
            messages_per_second DOUBLE PRECISION NOT NULL,
            devices_by_protocol JSONB NOT NULL DEFAULT '{}',
            uptime_seconds BIGINT NOT NULL DEFAULT 0,
            messages_dropped BIGINT NOT NULL DEFAULT 0,
            rules_errored BIGINT NOT NULL DEFAULT 0,
            actions_errored BIGINT NOT NULL DEFAULT 0
        );

        ALTER TABLE metrics_snapshots
            ADD COLUMN IF NOT EXISTS uptime_seconds BIGINT NOT NULL DEFAULT 0,
            ADD COLUMN IF NOT EXISTS messages_dropped BIGINT NOT NULL DEFAULT 0,
            ADD COLUMN IF NOT EXISTS rules_errored BIGINT NOT NULL DEFAULT 0,
            ADD COLUMN IF NOT EXISTS actions_errored BIGINT NOT NULL DEFAULT 0;

        CREATE INDEX IF NOT EXISTS idx_metrics_snapshots_at
            ON metrics_snapshots(snapshot_at DESC);

        -- Optional MQTT message audit trail, gated by
        -- Hermod:Features:MessagePersistence. The table exists whether or
        -- not the feature is enabled so the flag can flip at runtime
        -- without a schema migration.
        CREATE TABLE IF NOT EXISTS message_history (
            id BIGSERIAL PRIMARY KEY,
            received_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
            topic TEXT NOT NULL,
            payload TEXT NOT NULL,
            qos SMALLINT NOT NULL DEFAULT 0,
            retained BOOLEAN NOT NULL DEFAULT FALSE
        );
        CREATE INDEX IF NOT EXISTS idx_message_history_received
            ON message_history(received_at DESC);

        -- Per-firing rule audit log, gated by Hermod:Features:RuleAuditLog.
        -- Parallels the per-rule counters already maintained on the rules
        -- row, but keeps one row per firing for post-mortem analysis.
        CREATE TABLE IF NOT EXISTS rule_audit_log (
            id BIGSERIAL PRIMARY KEY,
            fired_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
            rule_id TEXT NOT NULL,
            topic TEXT,
            elapsed_ms DOUBLE PRECISION NOT NULL,
            success BOOLEAN NOT NULL,
            error TEXT,
            action_count INTEGER NOT NULL DEFAULT 0
        );
        CREATE INDEX IF NOT EXISTS idx_rule_audit_log_fired
            ON rule_audit_log(fired_at DESC);
        CREATE INDEX IF NOT EXISTS idx_rule_audit_log_rule
            ON rule_audit_log(rule_id, fired_at DESC);

        -- User session store for the Remember-Me cookie flow. Bound to a
        -- random opaque session_id surfaced as the hermod_session cookie on
        -- the coordinator's own domain; maps to the Vault refresh token
        -- the coordinator forwards to Vault on /auth/refresh. Persistent
        -- so a coordinator restart doesn't silently log everyone out.
        -- vault_refresh_token is an opaque credential — the DB role should
        -- never expose it, enforced at the Postgres grant level.
        CREATE TABLE IF NOT EXISTS user_sessions (
            session_id UUID PRIMARY KEY,
            user_id TEXT NOT NULL,
            vault_refresh_token TEXT NOT NULL,
            remember_me BOOLEAN NOT NULL DEFAULT FALSE,
            expires_at TIMESTAMPTZ NOT NULL,
            created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
            last_used_at TIMESTAMPTZ,
            revoked_at TIMESTAMPTZ
        );
        CREATE INDEX IF NOT EXISTS idx_user_sessions_user_id
            ON user_sessions(user_id)
            WHERE revoked_at IS NULL;
        CREATE INDEX IF NOT EXISTS idx_user_sessions_expires
            ON user_sessions(expires_at)
            WHERE revoked_at IS NULL;

        -- Operator-installed runtime rate-limit overrides. Hydrated on
        -- coord boot and written through on every Settings UI mutation,
        -- so an override survives pod restarts (the original in-memory
        -- store reset on every restart and was confusing operators who
        -- expected dashboard-set limits to stick).
        CREATE TABLE IF NOT EXISTS rate_limit_topic_overrides (
            topic TEXT PRIMARY KEY,
            rate_per_second DOUBLE PRECISION NOT NULL DEFAULT 0,
            burst INTEGER NOT NULL DEFAULT 0,
            dedup_window_seconds INTEGER NOT NULL DEFAULT -1,
            updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
        );

        -- Per-protocol bidirectional aggregate limits. Same persistence
        -- contract as rate_limit_topic_overrides; one row per Protocol
        -- enum value with separate ingress + egress knobs.
        CREATE TABLE IF NOT EXISTS rate_limit_protocol_overrides (
            protocol INTEGER PRIMARY KEY,
            ingress_rate DOUBLE PRECISION NOT NULL DEFAULT 0,
            ingress_burst INTEGER NOT NULL DEFAULT 0,
            egress_rate DOUBLE PRECISION NOT NULL DEFAULT 0,
            egress_burst INTEGER NOT NULL DEFAULT 0,
            updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
        );
        """;

    private readonly PostgresConnectionFactory _connectionFactory;
    private readonly ILogger<PostgresSchemaInitializer> _logger;

    /// <summary>
    /// Creates an initializer that will run <c>CREATE TABLE IF NOT EXISTS</c>
    /// DDL under a transaction-scoped advisory lock.
    /// </summary>
    public PostgresSchemaInitializer(
        PostgresConnectionFactory connectionFactory,
        ILogger<PostgresSchemaInitializer> logger)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        ArgumentNullException.ThrowIfNull(logger);
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    /// <summary>
    /// Applies schema DDL idempotently. Safe to call on every boot; concurrent
    /// replicas serialize via <c>pg_advisory_xact_lock</c>.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = _connectionFactory.CreateConnection();
        await conn.OpenAsync(cancellationToken);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken);

        await using (var lockCmd = conn.CreateCommand())
        {
            lockCmd.Transaction = tx;
            // Fixed key (must stay stable across releases so every replica
            // contends for the SAME lock). pg_advisory_xact_lock is held
            // until tx commits or rolls back.
            lockCmd.CommandText = "SELECT pg_advisory_xact_lock(8734592347823401278);";
            await lockCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var ddlCmd = conn.CreateCommand())
        {
            ddlCmd.Transaction = tx;
            ddlCmd.CommandText = SchemaDdl;
            await ddlCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);

        var host = new NpgsqlConnectionStringBuilder(conn.ConnectionString).Host;
        _logger.LogInformation("PostgreSQL schema initialized at {Host}", host);
    }
}
