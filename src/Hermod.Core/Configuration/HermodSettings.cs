namespace Hermod.Core.Configuration;

/// <summary>Root configuration bound from the <c>"Hermod"</c> section in appsettings.</summary>
public class HermodSettings
{
    /// <summary>MQTT broker connection and topic-subscription settings.</summary>
    public MqttSettings Mqtt { get; set; } = new();

    /// <summary>PostgreSQL connection and pooling settings.</summary>
    public DatabaseSettings Database { get; set; } = new();

    /// <summary>Per-translator enable flags and health-probe URLs.</summary>
    public ProtocolTranslatorsSettings ProtocolTranslators { get; set; } = new();

    /// <summary>Runtime metrics flush cadence.</summary>
    public MetricsSettings Metrics { get; set; } = new();

    /// <summary>Authentication behaviour (Vault42 integration, 2FA overrides).</summary>
    public AuthSettings Auth { get; set; } = new();

    /// <summary>Coordinator hardening knobs (CORS, audit logging).</summary>
    public SecuritySettings Security { get; set; } = new();

    /// <summary>Dashboard presentation settings such as the live messages-per-second chart.</summary>
    public DashboardSettings Dashboard { get; set; } = new();

    /// <summary>Zigbee-specific behaviour layered on top of the base MQTT settings.</summary>
    public ZigbeeSettings Zigbee { get; set; } = new();

    /// <remarks>
    /// Each section below is an independently-toggleable control surface.
    /// Profiles in tests/profiles/*.yaml compose these. Defaults here match
    /// today's production behaviour so an unset config reproduces the
    /// current system exactly.
    /// </remarks>
    /// <summary>Per-feature on/off toggles exposed for profile-driven pairing runs.</summary>
    public FeaturesSettings Features { get; set; } = new();

    /// <summary>Repository-layer storage backend and write-side tuning.</summary>
    public StorageSettings Storage { get; set; } = new();

    /// <summary>Rules engine and ingest pump tuning (parallelism, batching, queue sizing).</summary>
    public EngineSettings Engine { get; set; } = new();

    /// <summary>Startup seeding toggles for rules/devices.</summary>
    public SeedSettings Seed { get; set; } = new();

    /// <summary>Dev-only endpoint exposure (off in prod profiles).</summary>
    public DevSettings Dev { get; set; } = new();

    /// <summary>Per-message timestamp emission for W-measurement matrix runs.</summary>
    public TelemetrySettings Telemetry { get; set; } = new();

    /// <summary>Per-topic ingress limiter (rate cap + dedup); both halves off by default.</summary>
    public RateLimitSettings RateLimit { get; set; } = new();
}

/// <summary>
/// Per-topic ingress hard limiter applied at
/// <c>MessageProcessor.OnMessageReceived</c>. Combines an independent
/// token-bucket rate cap and an exact-payload dedup window; either,
/// both, or neither can be active. Reject reasons surface as
/// <c>hermod_topic_limited_rate_total</c> and
/// <c>hermod_topic_limited_dedup_total</c> so a flood is distinguishable
/// from a replay.
/// </summary>
public class RateLimitSettings
{
    /// <summary>
    /// Master switch for the token-bucket rate cap. Independent of
    /// <see cref="DedupEnabled"/>: set both to run both, neither for a
    /// total no-op.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Master switch for the dedup window. Independent of <see cref="Enabled"/>,
    /// so a deployment can drop replays without engaging rate-limit math.
    /// Per-topic <see cref="DedupWindowSeconds"/> still has to be &gt; 0
    /// for any actual deduping to happen.
    /// </summary>
    public bool DedupEnabled { get; set; } = false;

    /// <summary>Sustained tokens per second per topic. Clamped to &gt; 0.</summary>
    public double DefaultRatePerSecond { get; set; } = 1.0;

    /// <summary>Per-topic token-bucket capacity; the bucket starts full.</summary>
    public int DefaultBurst { get; set; } = 10;

    /// <summary>
    /// Default dedup window in seconds. 0 disables dedup. Short by
    /// design — long enough to catch obvious replays, short enough
    /// that legit duplicate state messages don't get silently dropped.
    /// </summary>
    public int DedupWindowSeconds { get; set; } = 5;

    /// <summary>
    /// LRU cap on per-topic state objects. 4096 covers ~10× realistic
    /// prod-pi topic cardinality.
    /// </summary>
    public int MaxTrackedKeys { get; set; } = 4096;

    /// <summary>Per-topic overrides; sentinel-valued fields fall back to the defaults above.</summary>
    public Dictionary<string, TopicRateOverride> TopicOverrides { get; set; } = new();

    /// <summary>Aggregate per-protocol bidirectional limiter; second clamp alongside the per-topic one.</summary>
    public ProtocolFlowSettings ProtocolLimits { get; set; } = new();
}

/// <summary>
/// Per-topic override values for <see cref="RateLimitSettings"/>. Any
/// field set to a non-positive sentinel is ignored and the default
/// from <see cref="RateLimitSettings"/> applies — letting an override
/// tweak just one knob without restating all three.
/// </summary>
public class TopicRateOverride
{
    /// <summary>Override sustained rate; &lt;= 0 falls back to default.</summary>
    public double RatePerSecond { get; set; } = 0;

    /// <summary>Override burst capacity; &lt;= 0 falls back to default.</summary>
    public int Burst { get; set; } = 0;

    /// <summary>Override dedup window in seconds; &lt; 0 falls back to default. 0 disables dedup for this topic.</summary>
    public int DedupWindowSeconds { get; set; } = -1;
}

/// <summary>
/// Aggregate per-protocol bidirectional limiter shape. Each protocol gets
/// up to two independent token buckets (one for ingress, one for egress);
/// a zero or negative rate/burst on either direction means "no limit on
/// that direction for this protocol", so an operator can clamp egress to
/// devices while leaving ingress alone, or vice versa.
/// </summary>
public class ProtocolFlowSettings
{
    /// <summary>Master switch. When false the limiter is bypassed entirely.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Per-protocol limits keyed by <see cref="Hermod.Core.Models.Protocol"/>
    /// enum name (case-insensitive: <c>"Zigbee"</c>, <c>"Lora"</c>,
    /// <c>"Bluetooth"</c>, <c>"Wifi"</c>). Missing keys mean no limit
    /// for that protocol.
    /// </summary>
    public Dictionary<string, ProtocolFlowOverride> Limits { get; set; } = new();
}

/// <summary>
/// Per-direction rate/burst pair for a single protocol. Each direction
/// is independent: zero on a direction disables the limit for that
/// direction (the bucket simply isn't created).
/// </summary>
public class ProtocolFlowOverride
{
    /// <summary>Sustained tokens per second for inbound traffic on this protocol; &lt;= 0 disables ingress limiting.</summary>
    public double IngressRatePerSecond { get; set; } = 0;

    /// <summary>Burst capacity for inbound traffic; &lt;= 0 disables ingress limiting.</summary>
    public int IngressBurst { get; set; } = 0;

    /// <summary>Sustained tokens per second for outbound publishes on this protocol; &lt;= 0 disables egress limiting.</summary>
    public double EgressRatePerSecond { get; set; } = 0;

    /// <summary>Burst capacity for outbound publishes; &lt;= 0 disables egress limiting.</summary>
    public int EgressBurst { get; set; } = 0;
}

/// <summary>
/// Per-message timestamp emission. When <see cref="TimestampsCsvPath"/> is
/// set the coordinator appends one CSV row per stage (<c>broker_rx</c>,
/// <c>rule_eval_done</c>, <c>action_publish</c>) keyed on the payload's
/// <c>_uuid</c> field. Load gen emits <c>publish_tx</c> for the same uuid.
/// Null or empty path = fully disabled (no parsing, no file I/O).
/// </summary>
public class TelemetrySettings
{
    /// <summary>
    /// Absolute path to the per-run <c>timestamps.csv</c> file. Overridden
    /// by <see cref="TimestampsCsvPathEnvVar"/> so matrix profiles can
    /// inject the run dir via env without a rebuild.
    /// </summary>
    public string? TimestampsCsvPath { get; set; }

    /// <summary>
    /// Environment-variable override name for <see cref="TimestampsCsvPath"/>.
    /// Matrix runs export <c>HERMOD_TIMESTAMPS_CSV=/results/&lt;run&gt;/timestamps.csv</c>.
    /// </summary>
    public const string TimestampsCsvPathEnvVar = "HERMOD_TIMESTAMPS_CSV";

    /// <summary>
    /// Max in-memory rows the recorder buffers before dropping new records.
    /// Bounds memory under a stuck filesystem; a 1 kHz ingest × 4 stages
    /// × a few seconds fits comfortably in the default 64k.
    /// </summary>
    public int BufferCapacity { get; set; } = 65_536;

    /// <summary>
    /// Applies environment-variable overrides for telemetry paths. Pure:
    /// callers pass <see cref="Environment.GetEnvironmentVariable(string)"/>
    /// in so tests can drive it.
    /// </summary>
    /// <param name="read">Callback that returns the value of a named env var, or null.</param>
    public void ApplyEnvironmentOverrides(Func<string, string?> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        var raw = read(TimestampsCsvPathEnvVar);
        if (!string.IsNullOrWhiteSpace(raw))
        {
            TimestampsCsvPath = raw.Trim();
        }
    }
}

/// <summary>
/// Per-feature on/off toggles. Lets pairing runs attribute cost: baseline
/// with the feature on, one flag flipped, re-run, diff.
/// </summary>
public class FeaturesSettings
{
    /// <summary>
    /// Writes inbound telemetry into the <c>devices.state</c> JSONB column on
    /// every non-system topic. Dominant PG write volume under load. Off =
    /// device rows still discovered, but state column frozen at whatever the
    /// last tracked payload wrote.
    /// </summary>
    public bool DeviceStateTracking { get; set; } = true;

    /// <summary>
    /// Persists every inbound MQTT message to the <c>message_history</c>
    /// table. Off by default because the feature itself is new (no prior
    /// on-disk history existed); turn on in profiles that measure the cost
    /// of full audit-trail persistence.
    /// </summary>
    public bool MessagePersistence { get; set; } = false;

    /// <summary>
    /// Writes a row to <c>rule_audit_log</c> for every rule firing. Off by
    /// default; rule execution counters still update on the parent rule row
    /// either way via the existing stats flush loop.
    /// </summary>
    public bool RuleAuditLog { get; set; } = false;

    /// <summary>
    /// Periodic flush of in-memory counters to the <c>metrics_counters</c>
    /// table so dashboard totals survive coordinator restarts. Off =
    /// counters stay in-memory; frontend shows lifetime-since-process-start
    /// only.
    /// </summary>
    public bool StatsRollup { get; set; } = true;

    /// <summary>
    /// In-memory rule index refreshed on the <see cref="EngineSettings.RuleCacheRefreshSeconds"/>
    /// cadence. Off = every inbound message re-queries the rules table. Off
    /// is only useful for measuring the cache benefit; leave on in anything
    /// resembling prod.
    /// </summary>
    public bool RuleCache { get; set; } = true;

    /// <summary>
    /// Exposes Prometheus-format metrics at <c>/metrics</c>. Off = endpoint
    /// 404s. Off by default because adding the endpoint is net-new work;
    /// the harness scrapes this when profiles enable it.
    /// </summary>
    public bool MetricsEndpoint { get; set; } = false;

    /// <summary>
    /// Per-message UUID trace. Load gen stamps an opaque id into every
    /// outbound payload; the coordinator records the id plus the action
    /// (or fail reason) it produced into <c>trace.jsonl</c>. Off = no
    /// parsing, no file I/O — zero hot-path cost. On is only for the
    /// trace-baseline profile and the safety/liveness verifier.
    /// </summary>
    public bool UuidTrace { get; set; } = false;

    /// <summary>
    /// Environment-variable override name for <see cref="UuidTrace"/>.
    /// <c>HERMOD_UUID_TRACE_ENABLED=true</c> flips the flag on post-bind,
    /// so matrix profiles can toggle tracing without editing appsettings.
    /// </summary>
    public const string UuidTraceEnvVar = "HERMOD_UUID_TRACE_ENABLED";

    /// <summary>
    /// Applies environment-variable overrides that use single-underscore
    /// names outside the .NET <c>Hermod__Features__X</c> binding
    /// convention. Pure function — callers pass <see cref="Environment.GetEnvironmentVariable(string)"/>
    /// in, so tests can drive it without touching process env.
    /// </summary>
    /// <param name="read">Callback that returns the value of a named env var, or null.</param>
    public void ApplyEnvironmentOverrides(Func<string, string?> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        var raw = read(UuidTraceEnvVar);
        if (!string.IsNullOrWhiteSpace(raw) && bool.TryParse(raw.Trim(), out var parsed))
        {
            UuidTrace = parsed;
        }
    }
}

/// <summary>
/// Backend selection for the repository layer. Lets us isolate "coordinator
/// CPU cost" from "Postgres I/O cost" in throughput measurements.
/// </summary>
public class StorageSettings
{
    /// <summary>Which repository implementation the DI container binds.</summary>
    public StorageMode Mode { get; set; } = StorageMode.Postgres;

    /// <summary>
    /// Max rows per batched INSERT for audit-trail writers (message history,
    /// rule audit). Higher = fewer round-trips under heavy load; lower =
    /// bounded memory per flush window.
    /// </summary>
    public int WriteBatchSize { get; set; } = 256;

    /// <summary>
    /// Max time a row sits in the in-memory ingest queue before the flusher
    /// emits a partial batch. Keeps tail latency bounded on light load.
    /// </summary>
    public int WriteFlushIntervalMs { get; set; } = 500;

    /// <summary>
    /// Bounded in-memory queue capacity per writer. DropOldest on overflow
    /// so a stalled Postgres connection cannot OOM the coordinator.
    /// </summary>
    public int WriteQueueCapacity { get; set; } = 50_000;

    /// <summary>
    /// Npgsql connection pool upper bound. Spliced into the connection
    /// string so every repo sees the same cap. Sized so hot-path writers
    /// don't starve one another on a Pi-class target; raise for beefier
    /// hardware or heavy concurrency profiles.
    /// </summary>
    public int MaxPoolSize { get; set; } = 50;

    /// <summary>
    /// Npgsql connection pool lower bound. Keeps warm connections around
    /// so the first writes after an idle period don't eat a three-way
    /// handshake and auth round-trip.
    /// </summary>
    public int MinPoolSize { get; set; } = 5;

    /// <summary>Per-command timeout in seconds. 0 = no limit.</summary>
    public int CommandTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// TCP keepalive interval on pooled connections, in seconds. Detects
    /// a dead peer sooner than the OS default on Linux kind clusters,
    /// where an in-cluster Postgres restart can otherwise wedge writers
    /// on a half-open socket.
    /// </summary>
    public int KeepAliveSeconds { get; set; } = 30;

    /// <summary>
    /// Max distinct statements Npgsql will auto-prepare per connection.
    /// A hot-path coordinator repeats a handful of queries millions of
    /// times; prepare caches the plan and saves parse/plan/bind on every
    /// subsequent call. 0 disables auto-prepare. 20 comfortably covers
    /// the ~10 distinct statements this app issues.
    /// </summary>
    public int MaxAutoPrepare { get; set; } = 20;

    /// <summary>
    /// When true the device upsert hot path skips the pre-write SELECT
    /// that only exists to emit a one-time "adding new device" log line.
    /// The ON CONFLICT branch of the UPSERT already decides insert vs
    /// update. Flip OFF to measure the cost of the extra round-trip.
    /// </summary>
    public bool SkipDeviceExistenceCheck { get; set; } = true;

    /// <summary>
    /// When true the telemetry ingest path calls
    /// <c>IDeviceService.UpsertDeviceStateAsync</c> (one statement,
    /// server-side JSONB merge) instead of the legacy read-modify-write
    /// pattern. Flip OFF to measure the cost of the extra round-trip and
    /// the client-side state merge.
    /// </summary>
    public bool FastDeviceUpserts { get; set; } = true;

    /// <summary>
    /// Max rows per batched UPSERT flushed by <c>BatchedDeviceStateWriter</c>.
    /// The writer collapses multiple in-flight updates for the same device
    /// id down to the last-enqueued state per flush, so the effective row
    /// count on-wire is min(batch, unique-ids). 256 keeps the UNNEST payload
    /// under typical PG wire-buffer thresholds while still amortising the
    /// per-round-trip cost heavily.
    /// </summary>
    public int DeviceWriteBatchSize { get; set; } = 256;

    /// <summary>
    /// Max time a queued device-state update waits before the flusher emits
    /// a partial batch. Bounds tail latency on light load; on a saturated
    /// ingest hot path batches fill by size first and this timer is moot.
    /// </summary>
    public int DeviceWriteFlushIntervalMs { get; set; } = 50;

    /// <summary>
    /// Bounded channel capacity for the device-state writer. DropOldest on
    /// overflow so a stalled Postgres connection cannot OOM the coordinator
    /// or back-pressure the ingest reader. 50_000 covers ~50 s of 1 kHz
    /// load before eviction kicks in.
    /// </summary>
    public int DeviceWriteQueueCapacity { get; set; } = 50_000;

    /// <summary>
    /// Days to retain rows in <c>message_history</c> before the retention
    /// background sweeper deletes them. Sensible default for IoT telemetry
    /// where the audit window is "did this message land last month?", not
    /// long-term archival. Ignored when <see cref="FeaturesSettings.MessagePersistence"/>
    /// is false (no rows to age out). Set to 0 to disable retention.
    /// </summary>
    public int MessageHistoryRetentionDays { get; set; } = 30;

    /// <summary>
    /// Rows per DELETE statement issued by the retention sweeper. Bounded
    /// so a multi-million-row purge doesn't take a long write lock or
    /// blow the WAL — the sweeper just loops until the window is clean.
    /// </summary>
    public int MessageHistoryRetentionBatchSize { get; set; } = 500;

    /// <summary>
    /// Minutes between retention sweep cycles. The sweeper deletes everything
    /// older than the cutoff in 500-row chunks, then sleeps. Hourly is fine
    /// for a 30-day retention window — daily new-row volume is tiny relative
    /// to the cutoff.
    /// </summary>
    public int MessageHistoryRetentionSweepMinutes { get; set; } = 60;
}

/// <summary>
/// Selects which <c>IDeviceService</c>/<c>IRulesService</c>/<c>IMessageHistoryRepository</c>
/// implementation the DI container binds.
/// <list type="bullet">
///   <item><description><c>Postgres</c>: production behaviour, Dapper-backed repos.</description></item>
///   <item><description><c>Noop</c>: all repo writes succeed and discard; reads return empty.
///     PG pod may still be deployed; the coordinator never touches it.</description></item>
///   <item><description><c>InMemory</c>: in-process dictionaries survive only for the pod's
///     lifetime. Useful when PG is absent entirely (component-isolation profiles).</description></item>
/// </list>
/// </summary>
public enum StorageMode
{
    /// <summary>Dapper-backed Postgres repositories (production default).</summary>
    Postgres = 0,

    /// <summary>All writes succeed and discard; reads return empty. Used to measure pure coordinator CPU cost.</summary>
    Noop = 1,

    /// <summary>In-process dictionary-backed repos; data does not survive the pod.</summary>
    InMemory = 2,
}

/// <summary>
/// Rules engine and ingest pump tuning. Exposed so profiles can sweep
/// parallelism/batch parameters without recompiling.
/// </summary>
public class EngineSettings
{
    /// <summary>
    /// Degree of parallelism for <c>ExecuteParallelAsync</c>. 0 = let the
    /// runtime decide (unbounded; .NET picks based on core count).
    /// </summary>
    public int Parallelism { get; set; } = 0;

    /// <summary>
    /// How many queued messages the ingest reader drains per awaited
    /// iteration. Larger batches reduce per-message overhead at the cost of
    /// per-batch latency.
    /// </summary>
    public int BatchSize { get; set; } = 64;

    /// <summary>
    /// Bounded channel capacity for the MQTT ingest queue. DropOldest on
    /// overflow. 10_000 matches the historical hardcoded value.
    /// </summary>
    public int QueueCapacity { get; set; } = 10_000;

    /// <summary>
    /// When true, rule-execution and action-result log events are buffered
    /// and flushed on a fixed cadence/size; off = every action logs inline.
    /// </summary>
    public bool LogBatching { get; set; } = true;

    /// <summary>Flush trigger: emit aggregated log once this many events accumulate.</summary>
    public int LogBatchSize { get; set; } = 100;

    /// <summary>Flush trigger: emit aggregated log at least this often, regardless of size.</summary>
    public int LogBatchIntervalMs { get; set; } = 1000;

    /// <summary>
    /// Rule-cache TTL. 5s matches the prior hardcoded value. Clamped to
    /// &gt;=1s to avoid a CRUD-storm hammering the rules table.
    /// </summary>
    public int RuleCacheRefreshSeconds { get; set; } = 5;
}

/// <summary>
/// Startup seeding toggles. Off in prod-like profiles so a fresh coordinator
/// comes up empty and the operator provisions rules/devices explicitly.
/// </summary>
public class SeedSettings
{
    /// <summary>When true, seed the devices table from <c>appsettings</c>-embedded fixtures on first run.</summary>
    public bool Devices { get; set; } = true;

    /// <summary>When true, seed the rules table from bundled example rules on first run.</summary>
    public bool Rules { get; set; } = true;
}

/// <summary>Dev-only endpoint exposure. Off in prod profiles.</summary>
public class DevSettings
{
    /// <summary>
    /// When true, <c>/dev/**</c> routes and any in-dev diagnostic endpoints
    /// are registered. Off = requests to those paths 404 regardless of auth.
    /// </summary>
    public bool Endpoints { get; set; } = false;
}

/// <summary>Zigbee-specific behaviour layered on top of the base MQTT settings.</summary>
public class ZigbeeSettings
{
    /// <summary>Periodic active polling of Zigbee device state.</summary>
    public ZigbeeStatePollerSettings StatePoller { get; set; } = new();
}

/// <summary>
/// Periodic active polling of Zigbee device state. Z2M devices generally push
/// updates on change, but battery-powered sensors with long reporting
/// intervals can leave the dashboard showing stale state. The poller asks
/// each device for a fresh snapshot via <c>zigbee/{device}/get</c>;
/// the reply lands on the regular state topic and flows through the normal
/// processing pipeline.
/// </summary>
public class ZigbeeStatePollerSettings
{
    /// <summary>Master switch for the poller.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Cadence between full sweeps. Default 60s. Clamped to &gt;= 10s.</summary>
    public int IntervalSeconds { get; set; } = 60;

    /// <summary>Per-device gap inside a sweep so the bridge isn't hit with N requests at once. Default 100ms.</summary>
    public int PerDeviceDelayMs { get; set; } = 100;
}

/// <summary>Dashboard presentation settings.</summary>
public class DashboardSettings
{
    /// <summary>Live messages-per-second chart shown on the Home dashboard.</summary>
    public LiveChartSettings LiveChart { get; set; } = new();
}

/// <summary>
/// Live messages/sec chart shown on the Home dashboard. Each sample is the
/// count of messages that arrived in the previous <see cref="RefreshMs"/>
/// window, derived from cumulative <c>MessagesProcessed</c> deltas.
/// </summary>
public class LiveChartSettings
{
    /// <summary>Master switch. When false the chart component renders nothing.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Poll cadence in milliseconds. 1000 = one sample per second.</summary>
    public int RefreshMs { get; set; } = 1000;

    /// <summary>Rolling window length in seconds. Older samples scroll off.</summary>
    public int WindowSeconds { get; set; } = 120;
}

/// <summary>Coordinator-side hardening knobs. CORS allowlist, audit logging, future rate limits.</summary>
public class SecuritySettings
{
    /// <summary>
    /// Cross-origin allowlist for the coordinator's API surface. Defaults to
    /// the coordinator's own origin (<c>http://localhost:42069</c>); production
    /// overlays should add the public dashboard URL(s). Wildcards are not
    /// supported because they would expose the Authorization header to every
    /// site in the browser.
    /// </summary>
    public string[] AllowedCorsOrigins { get; set; } = { "http://localhost:42069" };

    /// <summary>When true, security-relevant actions (logins, rule writes, backup downloads) are emitted to a structured audit logger.</summary>
    public bool AuditLogEnabled { get; set; } = false;

    /// <summary>
    /// Path to a PEM-encoded internal-CA bundle. When set, outbound
    /// HTTPS calls to Vault42 (login/refresh/logout) and the YARP
    /// translator clusters pin server cert chains to this CA instead
    /// of the OS trust store. Empty/null disables pinning entirely
    /// (dev compose / kind path).
    /// </summary>
    public string? InternalCAPath { get; set; }

    /// <summary>
    /// Path to a PEM-encoded client certificate the coordinator
    /// presents on outbound TLS calls to internal services that
    /// require mutual TLS (Vault42, translators). Pair with
    /// <see cref="ClientKeyPath"/>. Empty/null = no client cert sent.
    /// </summary>
    public string? ClientCertPath { get; set; }

    /// <summary>Path to the PEM-encoded private key for <see cref="ClientCertPath"/>.</summary>
    public string? ClientKeyPath { get; set; }
}

/// <summary>Authentication behaviour, primarily the Vault42 integration handshakes.</summary>
public class AuthSettings
{
    /// <summary>
    /// Dev escape hatch: when true, the Login page suppresses the 2FA prompt.
    /// If Vault42 still signals <c>requires_2fa</c> the user sees a clear
    /// diagnostic so the Hermod and Vault configs stay in lockstep. Default
    /// false (honour Vault's 2FA decision).
    /// </summary>
    public bool DisableTwoFactor { get; set; } = false;

    /// <summary>
    /// Role string Hermod treats as the admin tier. Vault42 emits this on
    /// the JWT's <c>roles</c> claim for mid-tier admin accounts; Hermod's
    /// admin policy gates <c>/admin/*</c> on it. Configurable so deployments
    /// running Vault42 with a renamed role can rebind without a rebuild.
    /// </summary>
    public string AdminRole { get; set; } = "admin";

    /// <summary>
    /// Role string Hermod treats as super-admin (strictly higher than
    /// <see cref="AdminRole"/>). Vault42 emits this for AdminUser-tier
    /// accounts; Hermod's admin policy accepts both. Configurable for the
    /// same reason as <see cref="AdminRole"/>.
    /// </summary>
    public string SuperAdminRole { get; set; } = "super_admin";
}

/// <summary>Runtime metrics flush cadence.</summary>
public class MetricsSettings
{
    /// <summary>
    /// How often runtime counters and snapshots are flushed to PostgreSQL.
    /// Snapshot inserts are deduped by value, so an idle coordinator does
    /// not grow the snapshots table at short intervals. Values below 1 s
    /// are clamped to 1 to prevent a busy loop.
    /// </summary>
    public int FlushIntervalSeconds { get; set; } = 15;
}

/// <summary>MQTT broker connection and topic-subscription settings.</summary>
public class MqttSettings
{
    /// <summary>Broker hostname or IP.</summary>
    public string Host { get; set; } = "localhost";

    /// <summary>Broker port. 1883 = plaintext, 8883 = TLS.</summary>
    public int Port { get; set; } = 1883;

    /// <summary>MQTT client identifier sent in the CONNECT packet. Must be unique across concurrent sessions on the same broker.</summary>
    public string ClientId { get; set; } = "hermod-coordinator";

    /// <summary>MQTT keepalive interval in seconds.</summary>
    public int KeepAliveSeconds { get; set; } = 60;

    /// <summary>When true, the broker discards any previous session state for this client on connect.</summary>
    public bool CleanSession { get; set; } = true;

    /// <summary>Username credential; null = anonymous.</summary>
    public string? Username { get; set; }

    /// <summary>Password credential; null = anonymous.</summary>
    public string? Password { get; set; }

    /// <summary>Topic subscription patterns and system-topic filtering.</summary>
    public MqttTopicsSettings Topics { get; set; } = new();

    /// <summary>TLS configuration applied when <see cref="MqttTlsSettings.UseTls"/> is true.</summary>
    public MqttTlsSettings Tls { get; set; } = new();

    /// <summary>
    /// Outbox buffer size for messages published while the client is
    /// reconnecting. 0 = off (publishes during a disconnect throw
    /// immediately, matching today's behaviour). &gt;0 = bounded channel with
    /// drop-oldest eviction; buffered publishes replay in order once the
    /// connection is restored.
    /// </summary>
    public int ReconnectBufferSize { get; set; } = 0;

    /// <summary>
    /// Number of parallel MQTT client instances that share the ingest
    /// load by subscribing to disjoint topic shards. 1 = one client
    /// with the full wildcard subscription (today's behaviour). N &gt; 1
    /// creates N clients whose CONNECT packets use <see cref="ClientId"/>
    /// with a <c>-&lt;index&gt;</c> suffix; callers of
    /// <c>SubscribeAsync</c> get their filter routed to one client by
    /// topic-prefix hash so each client handles a disjoint slice of
    /// traffic. The serial MQTTnet receive callback is the ingest
    /// bottleneck at ~7 k msg/s on one x64 core; sharding across N
    /// clients lifts the ceiling roughly N-fold until the downstream
    /// queue reader becomes the limit. Recommended on Pi 5 8 GB:
    /// <c>ParallelClients = 4</c> (matches core count).
    /// </summary>
    public int ParallelClients { get; set; } = 1;
}

/// <summary>
/// TLS configuration for the MQTT client. Applied when <see cref="UseTls"/> is
/// true; port must match (typically 8883). Defaults leave TLS off so existing
/// dev configs keep working.
/// </summary>
public class MqttTlsSettings
{
    /// <summary>Enables TLS on the MQTT client. Pair with <see cref="MqttSettings.Port"/>=8883.</summary>
    public bool UseTls { get; set; } = false;

    /// <summary>Dev-only: skip server certificate validation. Never enable in production.</summary>
    public bool AllowUntrustedCertificates { get; set; } = false;

    /// <summary>Dev-only: skip chain-of-trust checks.</summary>
    public bool IgnoreCertificateChainErrors { get; set; } = false;

    /// <summary>Dev-only: skip revocation checks.</summary>
    public bool IgnoreCertificateRevocationErrors { get; set; } = false;

    /// <summary>Path to a PEM-encoded CA bundle used to verify the broker.</summary>
    public string? CaBundlePath { get; set; }

    /// <summary>Path to a PEM-encoded client certificate for mutual TLS.</summary>
    public string? ClientCertificatePath { get; set; }

    /// <summary>Path to the PEM-encoded private key for the client certificate.</summary>
    public string? ClientKeyPath { get; set; }
}

/// <summary>MQTT topic subscription patterns and system-topic classification.</summary>
public class MqttTopicsSettings
{
    /// <summary>Subscription pattern for Zigbee-bridged traffic.</summary>
    public string Zigbee { get; set; } = "zigbee/#";

    /// <summary>Subscription pattern for LoRa-bridged traffic.</summary>
    public string Lora { get; set; } = "lora/#";

    /// <summary>Subscription pattern for Bluetooth-bridged traffic.</summary>
    public string Bluetooth { get; set; } = "bluetooth/#";

    /// <summary>Subscription pattern for WiFi-bridged traffic.</summary>
    public string Wifi { get; set; } = "wifi/#";

    /// <summary>Display-only label for the Health dashboard. Runtime filtering uses <see cref="SystemTopicPrefixes"/>.</summary>
    public string System { get; set; } = "hermod/#";

    /// <summary>
    /// Topic prefixes treated as system traffic (alerting, internal signalling,
    /// debug). Messages whose topic starts with any of these bypass device
    /// discovery. Comparison is ordinal per MQTT case-sensitivity.
    /// </summary>
    public string[] SystemTopicPrefixes { get; set; } = { "alerts/", "hermod/" };

    /// <summary>
    /// True when <paramref name="topic"/> starts with any configured
    /// <see cref="SystemTopicPrefixes"/>. Empty/null inputs and an empty
    /// prefix list both return false.
    /// </summary>
    /// <param name="topic">The MQTT topic to classify.</param>
    /// <returns><c>true</c> if <paramref name="topic"/> matches any configured system prefix; otherwise <c>false</c>.</returns>
    public bool IsSystemTopic(string? topic)
    {
        if (string.IsNullOrEmpty(topic)) return false;

        var prefixes = SystemTopicPrefixes;
        if (prefixes is null || prefixes.Length == 0) return false;

        foreach (var prefix in prefixes)
        {
            if (!string.IsNullOrEmpty(prefix) &&
                topic.StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>PostgreSQL connection and credential-split settings.</summary>
public class DatabaseSettings
{
    /// <summary>
    /// Carries Host/Port/Database/Username; the password is intentionally NOT
    /// embedded here. <see cref="Password"/> is supplied separately (from a
    /// Kubernetes Secret in prod) and merged by the connection factory at
    /// build time. Splitting them lets ops rotate the password without
    /// touching the routing ConfigMap and keeps the plaintext password out
    /// of the dashboard's settings view.
    /// </summary>
    public string ConnectionString { get; set; } =
        "Host=localhost;Port=5432;Database=hermod;Username=hermod";

    /// <summary>Optional override merged into <see cref="ConnectionString"/> by the connection factory. Empty/null means "use whatever ConnectionString already specifies".</summary>
    public string? Password { get; set; }

    /// <summary>Display-only label. Routing comes from the <c>Database=</c> token inside <see cref="ConnectionString"/>.</summary>
    public string DatabaseName { get; set; } = "hermod";
}

/// <summary>Per-translator enable flags and health-probe URLs.</summary>
public class ProtocolTranslatorsSettings
{
    /// <summary>Zigbee2MQTT translator settings.</summary>
    public TranslatorSettings Zigbee2Mqtt { get; set; } = new();

    /// <summary>BLE2MQTT translator settings.</summary>
    public TranslatorSettings Ble2Mqtt { get; set; } = new();

    /// <summary>LoRa2MQTT translator settings.</summary>
    public TranslatorSettings Lora2Mqtt { get; set; } = new();

    /// <summary>WiFi2MQTT translator settings.</summary>
    public TranslatorSettings Wifi2Mqtt { get; set; } = new();
}

/// <summary>Enable flag and liveness-probe URL for a single protocol translator.</summary>
public class TranslatorSettings
{
    /// <summary>When true the translator is considered part of the system.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Base URL of the translator; null = not deployed.</summary>
    public string? Url { get; set; }

    /// <summary>Liveness probe path appended to <see cref="Url"/>. Default <c>/health/live</c>.</summary>
    public string HealthEndpoint { get; set; } = "/health/live";
}

/// <summary>
/// Hard ceilings on rule + device counts. Constants (compile-time) — these are
/// architectural limits validated against the prod resource budget on the Pi5
/// 8GB target. Chosen for headroom under the matrix-prep test data:
///
///   * MaxRules = 10,000 — at the engine-par4 baseline (4 worker threads),
///     a 10k-rule cache fits in coord's 1 GB memory limit with ~30% headroom
///     for transient rule-eval allocation. Confirmed by the no-cache vs
///     baseline matrix runs (no-cache @ 805m CPU = 11.2× baseline; cache
///     keeps 10k rules in working set without GC pressure).
///   * MaxDevices = 5,000 — the saturate-10k-flat profile (2,500 devices ×
///     4 Hz = 10k msg/s) found the broker/coord ceiling at ~12.5k/s on Pi.
///     5k devices @ 1 Hz fits comfortably in the steady-state baseline (0%
///     loss across all midstress runs at 1k/2k/4k rates).
/// </summary>
public static class HermodLimits
{
    /// <summary>Maximum number of rules the coord will accept (enforced on POST /api/rules).</summary>
    public const int MaxRules = 10_000;

    /// <summary>Maximum number of devices the coord will track (enforced on device discovery).</summary>
    public const int MaxDevices = 5_000;
}
