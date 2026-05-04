using System.Diagnostics.CodeAnalysis;
using Hermod.Core.Configuration;
using Hermod.Core.Interfaces;
using Hermod.Core.Telemetry;
using Hermod.Infrastructure.Database;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hermod.Infrastructure.Services;

/// <summary>
/// Hosted service that periodically flushes runtime metrics to PostgreSQL
/// so dashboards survive coordinator restarts and we get historical graphs.
///
/// On startup it seeds <see cref="IStatsService"/> from the persisted counters
/// so the lifetime "messages processed" and "rules executed" totals are
/// continuous across restarts.
///
/// On every flush tick (configured via <c>Hermod:Metrics:FlushIntervalSeconds</c>,
/// default 15 s) it:
///   1. Upserts current counter values into <c>metrics_counters</c>
///   2. Inserts a snapshot row into <c>metrics_snapshots</c> only if any of
///      (messages, rules, devices, dropped, rulesErrored, actionsErrored)
///      has changed since the previous flush. Error/drop counters are
///      included in the dedup key so failure bursts show on the historical
///      dashboard even when throughput is flat.
///
/// On graceful shutdown it flushes one final time.
/// </summary>
public class MetricsPersistenceService : BackgroundService
{
    private readonly TimeSpan _flushInterval;
    private readonly bool _enabled;

    private readonly IStatsService _stats;
    private readonly IMetricsRepository _metrics;
    private readonly HermodMetrics _hermodMetrics;
    private readonly ILogger<MetricsPersistenceService> _logger;

    // Dedup: skip snapshot inserts when nothing changed so the table
    // stops growing linearly during idle. Error/drop counters are part
    // of the key so a steady-throughput system with a new error burst
    // still registers a snapshot.
    private long _lastSnapshotMessages = -1;
    private long _lastSnapshotRules = -1;
    private int _lastSnapshotDevices = -1;
    private long _lastSnapshotDropped = -1;
    private long _lastSnapshotRulesErrored = -1;
    private long _lastSnapshotActionsErrored = -1;

    // Block counter upserts until we have successfully read the
    // persisted counters at least once. Otherwise a transient PG drop
    // at boot leaves in-memory counters at 0 and the first flush wipes
    // the real history.
    private bool _seedCompleted;

    internal void MarkSeedCompletedForTests() => _seedCompleted = true;

    /// <summary>
    /// Creates a service that seeds lifetime counters from Postgres on boot
    /// and flushes counter/snapshot rows on every configured interval.
    /// </summary>
    public MetricsPersistenceService(
        IStatsService stats,
        IMetricsRepository metrics,
        HermodMetrics hermodMetrics,
        IOptions<HermodSettings> settings,
        ILogger<MetricsPersistenceService> logger)
    {
        ArgumentNullException.ThrowIfNull(stats);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(hermodMetrics);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);
        _stats = stats;
        _metrics = metrics;
        _hermodMetrics = hermodMetrics;
        _logger = logger;

        // Clamp to >=1s to avoid busy-spinning against Postgres.
        var configured = Math.Max(1, settings.Value.Metrics.FlushIntervalSeconds);
        _flushInterval = TimeSpan.FromSeconds(configured);
        _enabled = settings.Value.Features.StatsRollup;
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            _logger.LogInformation("StatsRollup disabled; metrics persistence service idle");
            return;
        }

        // Schema is guaranteed by PostgresDatabaseInitializer's earlier StartAsync.
        await SeedCountersAsync(stoppingToken);

        _logger.LogInformation(
            "Metrics persistence flush interval: {Seconds}s",
            (int)_flushInterval.TotalSeconds);

        using var timer = new PeriodicTimer(_flushInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                // Retry seed on every tick until success; a transient PG
                // drop at boot would otherwise leave counters unseeded
                // and the first flush would overwrite history with zeros.
                if (!_seedCompleted)
                {
                    await SeedCountersAsync(stoppingToken);
                }
                await FlushAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on graceful shutdown
        }
        finally
        {
            // Final flush on shutdown so we don't lose the last 15s of activity.
            await FlushAsync(CancellationToken.None);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Seed is best-effort at boot; any failure falls back to in-memory zeros and logs.")]
    private async Task SeedCountersAsync(CancellationToken cancellationToken)
    {
        try
        {
            var persisted = await _metrics.GetCountersAsync(cancellationToken);
            if (persisted.HasValue)
            {
                _stats.SeedCounters(
                    persisted.Value.MessagesProcessed,
                    persisted.Value.RulesExecuted,
                    persisted.Value.MessagesDropped,
                    persisted.Value.RulesErrored,
                    persisted.Value.ActionsErrored);
                _logger.LogInformation(
                    "Seeded stats counters from PostgreSQL: messages={Messages}, rules={Rules}, dropped={Dropped}, ruleErr={RuleErr}, actionErr={ActionErr}",
                    persisted.Value.MessagesProcessed,
                    persisted.Value.RulesExecuted,
                    persisted.Value.MessagesDropped,
                    persisted.Value.RulesErrored,
                    persisted.Value.ActionsErrored);
            }
            else
            {
                _logger.LogInformation("No persisted metrics counters found, starting from zero");
            }
            _seedCompleted = true;
        }
        catch (Exception ex) when (PostgresErrorClassifier.IsTransientConnectionDrop(ex))
        {
            // Self-healing: next flush tick reconnects. Stay at Debug.
            _logger.LogDebug(ex, "Failed to seed metrics counters: transient postgres connection drop");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to seed metrics counters; continuing with in-memory zeros");
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Transient Postgres errors are logged and the next flush tick retries; must not kill the background service.")]
    internal async Task FlushAsync(CancellationToken cancellationToken)
    {
        try
        {
            var stats = await _stats.GetCurrentStatsAsync(cancellationToken);
            var (messages, rules, dropped, rulesErr, actionsErr) = _stats.GetCounters();

            var changed =
                messages != _lastSnapshotMessages ||
                rules != _lastSnapshotRules ||
                stats.TotalDevices != _lastSnapshotDevices ||
                dropped != _lastSnapshotDropped ||
                rulesErr != _lastSnapshotRulesErrored ||
                actionsErr != _lastSnapshotActionsErrored;

            // Pre-seed: do not touch the counters row. Snapshots can
            // still record current-state readings (additive, no history loss).
            if (!_seedCompleted)
            {
                return;
            }

            if (changed)
            {
                // Single-tx upsert+snapshot so a mid-flush PG drop can't
                // leave counters and snapshot out of sync.
                await _metrics.SaveCountersAndSnapshotAsync(
                    messages, rules, dropped, rulesErr, actionsErr, stats, cancellationToken);

                _lastSnapshotMessages = messages;
                _lastSnapshotRules = rules;
                _lastSnapshotDevices = stats.TotalDevices;
                _lastSnapshotDropped = dropped;
                _lastSnapshotRulesErrored = rulesErr;
                _lastSnapshotActionsErrored = actionsErr;

                _logger.LogDebug(
                    "Flushed metrics: messages={Messages}, rules={Rules}, devices={Devices}",
                    messages, rules, stats.TotalDevices);
            }
            else
            {
                // Idle tick: upsert counters to keep the heartbeat row
                // fresh but skip the snapshot insert.
                await _metrics.UpsertCountersAsync(messages, rules, dropped, rulesErr, actionsErr, cancellationToken);
            }

            _hermodMetrics.IncStatsRollupWrites();
        }
        catch (Exception ex) when (PostgresErrorClassifier.IsTransientConnectionDrop(ex))
        {
            _logger.LogDebug(ex, "Metrics flush hit transient postgres connection drop; will retry next tick");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Metrics flush failed; will retry next tick");
        }
    }
}
