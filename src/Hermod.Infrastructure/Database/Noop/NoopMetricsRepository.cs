using Hermod.Core.Interfaces;
using Hermod.Core.Models;

namespace Hermod.Infrastructure.Database.Noop;

/// <summary>
/// Pass-through metrics repository used when <c>Hermod:Storage:Mode</c> is
/// <c>Noop</c>. Discards counter upserts and snapshots; returns null/empty
/// for reads so the coordinator starts from a clean slate each boot.
/// </summary>
internal sealed class NoopMetricsRepository : IMetricsRepository
{
    private static readonly IReadOnlyList<SystemStats> EmptySnapshots = Array.Empty<SystemStats>();

    public Task<(long MessagesProcessed, long RulesExecuted, long MessagesDropped,
                 long RulesErrored, long ActionsErrored)?> GetCountersAsync(
        CancellationToken cancellationToken = default)
        => Task.FromResult<(long, long, long, long, long)?>(null);

    public Task UpsertCountersAsync(
        long messagesProcessed,
        long rulesExecuted,
        long messagesDropped,
        long rulesErrored,
        long actionsErrored,
        CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task SaveSnapshotAsync(
        SystemStats stats,
        CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task SaveCountersAndSnapshotAsync(
        long messagesProcessed,
        long rulesExecuted,
        long messagesDropped,
        long rulesErrored,
        long actionsErrored,
        SystemStats stats,
        CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task<IReadOnlyList<SystemStats>> GetRecentSnapshotsAsync(
        int limit = 100,
        CancellationToken cancellationToken = default)
        => Task.FromResult(EmptySnapshots);

    public Task<double> GetRateOverWindowAsync(
        TimeSpan window,
        CancellationToken cancellationToken = default)
        => Task.FromResult(0d);
}
