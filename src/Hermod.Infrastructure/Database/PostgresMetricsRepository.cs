using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using Dapper;
using Hermod.Core.Interfaces;
using Hermod.Core.Models;
using Microsoft.Extensions.Logging;

namespace Hermod.Infrastructure.Database;

/// <summary>
/// PostgreSQL-backed implementation of <see cref="IMetricsRepository"/>. Uses
/// a <c>metrics_counters</c> name/value table for lifetime totals and a
/// <c>metrics_snapshots</c> time-series table for historical rollups.
/// Transient Postgres drops (57P0x, IO faults) are logged at Debug and
/// surfaced as empty/zero results so dashboards stay responsive.
/// </summary>
public sealed class PostgresMetricsRepository : IMetricsRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private const string UpsertCountersSql = """
        INSERT INTO metrics_counters (name, value, updated_at)
        VALUES ('messages_processed', @Messages,    NOW()),
               ('rules_executed',     @Rules,       NOW()),
               ('messages_dropped',   @Dropped,     NOW()),
               ('rules_errored',      @RulesErr,    NOW()),
               ('actions_errored',    @ActionsErr,  NOW())
        ON CONFLICT (name) DO UPDATE
            SET value = EXCLUDED.value,
                updated_at = NOW();
        """;

    private const string InsertSnapshotSql = """
        INSERT INTO metrics_snapshots
            (snapshot_at, messages_processed, rules_executed,
             total_devices, online_devices, active_rules,
             messages_per_second, devices_by_protocol, uptime_seconds,
             messages_dropped, rules_errored, actions_errored)
        VALUES
            (NOW(), @Messages, @Rules,
             @Total, @Online, @ActiveRules,
             @Mps, @Protocols::jsonb, @UptimeSeconds,
             @Dropped, @RulesErr, @ActionsErr);
        """;

    private const string LoadCountersSql =
        "SELECT name, value FROM metrics_counters " +
        "WHERE name IN ('messages_processed', 'rules_executed', 'messages_dropped', 'rules_errored', 'actions_errored')";

    private const string RecentSnapshotsSql = """
        SELECT snapshot_at         AS SnapshotAt,
               messages_processed  AS MessagesProcessed,
               rules_executed      AS RulesExecuted,
               total_devices       AS TotalDevices,
               online_devices      AS OnlineDevices,
               active_rules        AS ActiveRules,
               messages_per_second AS MessagesPerSecond,
               devices_by_protocol AS DevicesByProtocolJson,
               uptime_seconds      AS UptimeSeconds,
               messages_dropped    AS MessagesDropped,
               rules_errored       AS RulesErrored,
               actions_errored     AS ActionsErrored
        FROM metrics_snapshots
        ORDER BY snapshot_at DESC
        LIMIT @Limit;
        """;

    private const string RateOverWindowSql = """
        SELECT
            COALESCE(
                CASE
                    WHEN COUNT(*) < 2 THEN 0
                    WHEN EXTRACT(EPOCH FROM (MAX(snapshot_at) - MIN(snapshot_at))) <= 0 THEN 0
                    ELSE (MAX(messages_processed) - MIN(messages_processed))::double precision
                         / EXTRACT(EPOCH FROM (MAX(snapshot_at) - MIN(snapshot_at)))
                END,
                0.0)
        FROM metrics_snapshots
        WHERE snapshot_at >= NOW() - (@Seconds || ' seconds')::interval;
        """;

    private readonly PostgresConnectionFactory _connectionFactory;
    private readonly ILogger<PostgresMetricsRepository> _logger;

    /// <summary>
    /// Creates a metrics repository bound to the supplied connection factory.
    /// </summary>
    public PostgresMetricsRepository(
        PostgresConnectionFactory connectionFactory,
        ILogger<PostgresMetricsRepository> logger)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        ArgumentNullException.ThrowIfNull(logger);
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    /// <inheritdoc/>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Counter load is best-effort at boot; any failure falls back to null and coordinator starts from zero.")]
    public async Task<(long MessagesProcessed, long RulesExecuted, long MessagesDropped,
                       long RulesErrored, long ActionsErrored)?> GetCountersAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var conn = _connectionFactory.CreateConnection();
            var rows = await conn.QueryAsync<(string Name, long Value)>(new CommandDefinition(
                LoadCountersSql,
                cancellationToken: cancellationToken));

            var dict = rows.ToDictionary(r => r.Name, r => r.Value);
            if (dict.Count == 0) return null;

            return (
                dict.GetValueOrDefault("messages_processed", 0),
                dict.GetValueOrDefault("rules_executed", 0),
                dict.GetValueOrDefault("messages_dropped", 0),
                dict.GetValueOrDefault("rules_errored", 0),
                dict.GetValueOrDefault("actions_errored", 0));
        }
        catch (Exception ex)
        {
            // Transient PG drops must propagate so MetricsPersistenceService
            // keeps _seedCompleted = false and does NOT overwrite the
            // persisted counters with in-memory zeros on the next flush.
            // Only genuine "no data" (missing table, parse bug) should
            // translate to a zero-start, and those surface as deserialization
            // or SELECT failures that the caller's warn-and-continue handles.
            if (PostgresErrorClassifier.IsTransientConnectionDrop(ex))
            {
                throw;
            }
            _logger.LogWarning(ex, "Failed to load persisted metrics counters; starting from zero");
            return null;
        }
    }

    /// <inheritdoc/>
    public async Task UpsertCountersAsync(
        long messagesProcessed,
        long rulesExecuted,
        long messagesDropped,
        long rulesErrored,
        long actionsErrored,
        CancellationToken cancellationToken = default)
    {
        await using var conn = _connectionFactory.CreateConnection();
        await conn.ExecuteAsync(new CommandDefinition(
            UpsertCountersSql,
            BuildCounterParams(messagesProcessed, rulesExecuted, messagesDropped, rulesErrored, actionsErrored),
            cancellationToken: cancellationToken));
    }

    /// <inheritdoc/>
    public async Task SaveSnapshotAsync(
        SystemStats stats,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stats);
        await using var conn = _connectionFactory.CreateConnection();
        await conn.ExecuteAsync(new CommandDefinition(
            InsertSnapshotSql,
            BuildSnapshotParams(stats),
            cancellationToken: cancellationToken));
    }

    /// <inheritdoc/>
    public async Task SaveCountersAndSnapshotAsync(
        long messagesProcessed,
        long rulesExecuted,
        long messagesDropped,
        long rulesErrored,
        long actionsErrored,
        SystemStats stats,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stats);
        // Both writes inside one transaction so a mid-flush connection drop
        // cannot leave counters and snapshots inconsistent.
        await using var conn = _connectionFactory.CreateConnection();
        await conn.OpenAsync(cancellationToken);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken);

        try
        {
            await conn.ExecuteAsync(new CommandDefinition(
                UpsertCountersSql,
                BuildCounterParams(messagesProcessed, rulesExecuted, messagesDropped, rulesErrored, actionsErrored),
                transaction: tx,
                cancellationToken: cancellationToken));

            await conn.ExecuteAsync(new CommandDefinition(
                InsertSnapshotSql,
                BuildSnapshotParams(stats),
                transaction: tx,
                cancellationToken: cancellationToken));

            await tx.CommitAsync(cancellationToken);
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<double> GetRateOverWindowAsync(
        TimeSpan window,
        CancellationToken cancellationToken = default)
    {
        var seconds = Math.Max(1, (int)window.TotalSeconds);
        try
        {
            await using var conn = _connectionFactory.CreateConnection();
            var rate = await conn.ExecuteScalarAsync<double>(new CommandDefinition(
                RateOverWindowSql,
                new { Seconds = seconds.ToString(CultureInfo.InvariantCulture) },
                cancellationToken: cancellationToken));
            return rate;
        }
        catch (Exception ex) when (PostgresErrorClassifier.IsTransientConnectionDrop(ex))
        {
            _logger.LogDebug(ex, "GetRateOverWindowAsync hit transient postgres drop");
            return 0;
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<SystemStats>> GetRecentSnapshotsAsync(
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        await using var conn = _connectionFactory.CreateConnection();
        var rows = await conn.QueryAsync<SnapshotRow>(new CommandDefinition(
            RecentSnapshotsSql,
            new { Limit = limit },
            cancellationToken: cancellationToken));

        return rows
            .Select(r => new SystemStats
            {
                MessagesProcessed = r.MessagesProcessed,
                RulesExecuted = r.RulesExecuted,
                TotalDevices = r.TotalDevices,
                OnlineDevices = r.OnlineDevices,
                ActiveRules = r.ActiveRules,
                MessagesPerSecond = r.MessagesPerSecond,
                LastUpdated = r.SnapshotAt,
                DevicesByProtocol = ParseProtocols(r.DevicesByProtocolJson),
                Uptime = TimeSpan.FromSeconds(r.UptimeSeconds),
                MessagesDropped = r.MessagesDropped,
                RulesErrored = r.RulesErrored,
                ActionsErrored = r.ActionsErrored
            })
            .ToList();
    }

    private static object BuildCounterParams(long messagesProcessed, long rulesExecuted,
        long messagesDropped, long rulesErrored, long actionsErrored) => new
        {
            Messages = messagesProcessed,
            Rules = rulesExecuted,
            Dropped = messagesDropped,
            RulesErr = rulesErrored,
            ActionsErr = actionsErrored
        };

    private static object BuildSnapshotParams(SystemStats stats) => new
    {
        Messages = stats.MessagesProcessed,
        Rules = stats.RulesExecuted,
        Total = stats.TotalDevices,
        Online = stats.OnlineDevices,
        ActiveRules = stats.ActiveRules,
        Mps = stats.MessagesPerSecond,
        Protocols = JsonSerializer.Serialize(
            stats.DevicesByProtocol.ToDictionary(
                kvp => kvp.Key.ToString(),
                kvp => kvp.Value),
            JsonOptions),
        UptimeSeconds = (long)stats.Uptime.TotalSeconds,
        Dropped = stats.MessagesDropped,
        RulesErr = stats.RulesErrored,
        ActionsErr = stats.ActionsErrored
    };

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Snapshot JSON is best-effort; malformed rows fall back to empty map rather than poisoning the UI.")]
    private static Dictionary<Protocol, int> ParseProtocols(string? json)
    {
        if (string.IsNullOrEmpty(json)) return [];

        try
        {
            var raw = JsonSerializer.Deserialize<Dictionary<string, int>>(json, JsonOptions);
            if (raw is null) return [];

            var result = new Dictionary<Protocol, int>(raw.Count);
            foreach (var (key, value) in raw)
            {
                if (Enum.TryParse<Protocol>(key, out var protocol))
                {
                    result[protocol] = value;
                }
            }
            return result;
        }
        catch
        {
            return [];
        }
    }

    private sealed class SnapshotRow
    {
        public DateTime SnapshotAt { get; set; }
        public long MessagesProcessed { get; set; }
        public long RulesExecuted { get; set; }
        public int TotalDevices { get; set; }
        public int OnlineDevices { get; set; }
        public int ActiveRules { get; set; }
        public double MessagesPerSecond { get; set; }
        public string? DevicesByProtocolJson { get; set; }
        public long UptimeSeconds { get; set; }
        public long MessagesDropped { get; set; }
        public long RulesErrored { get; set; }
        public long ActionsErrored { get; set; }
    }
}
