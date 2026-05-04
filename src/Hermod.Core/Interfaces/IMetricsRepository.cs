using Hermod.Core.Models;

namespace Hermod.Core.Interfaces;

/// <summary>
/// Persists runtime metrics so dashboards survive coordinator restarts.
/// Counters (<c>messages_processed</c>, <c>rules_executed</c>,
/// <c>messages_dropped</c>, <c>rules_errored</c>, <c>actions_errored</c>) are
/// tracked as lifetime totals; snapshots capture full stats over time for
/// graphing.
/// </summary>
public interface IMetricsRepository
{
    /// <summary>Loads the persisted lifetime counters, if any.</summary>
    Task<(long MessagesProcessed, long RulesExecuted, long MessagesDropped,
          long RulesErrored, long ActionsErrored)?> GetCountersAsync(
        CancellationToken cancellationToken = default);

    /// <summary>Atomically upserts the lifetime counters.</summary>
    Task UpsertCountersAsync(
        long messagesProcessed,
        long rulesExecuted,
        long messagesDropped,
        long rulesErrored,
        long actionsErrored,
        CancellationToken cancellationToken = default);

    /// <summary>Appends a snapshot of current stats.</summary>
    Task SaveSnapshotAsync(
        SystemStats stats,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Upserts the lifetime counters and appends a snapshot inside one
    /// database transaction, so a mid-flush connection drop cannot leave
    /// the counters and snapshots tables inconsistent.
    /// </summary>
    Task SaveCountersAndSnapshotAsync(
        long messagesProcessed,
        long rulesExecuted,
        long messagesDropped,
        long rulesErrored,
        long actionsErrored,
        SystemStats stats,
        CancellationToken cancellationToken = default);

    /// <summary>Returns recent snapshots, newest first.</summary>
    Task<IReadOnlyList<SystemStats>> GetRecentSnapshotsAsync(
        int limit = 100,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Messages-per-second computed from the persisted snapshots table
    /// over the given window. Implemented as
    /// <c>(max_counter - min_counter) / seconds_spanned</c> across the
    /// snapshots that fall inside the window. Returns 0 when fewer than
    /// two snapshots exist within the window. Safe across process
    /// restarts because the snapshot timeline is durable.
    /// </summary>
    Task<double> GetRateOverWindowAsync(
        TimeSpan window,
        CancellationToken cancellationToken = default);
}
