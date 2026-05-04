using Hermod.Core.Configuration;
using Hermod.Core.Interfaces;
using Hermod.Core.Models;
using Hermod.Core.Telemetry;
using Hermod.Infrastructure.Database.Noop;
using Hermod.Infrastructure.Services;
using Hermod.TestInfrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Hermod.UnitTests;

/// <summary>
/// Pins the <see cref="MetricsPersistenceService"/> flush dedup key.
/// The commit that widened the dedup key to include dropped/rulesErrored/
/// actionsErrored (acbdcbd) shipped without direct coverage; this fixture
/// fills the gap by driving <c>FlushAsync</c> against a recording
/// repository fake and asserting the snapshot-insert cadence.
/// </summary>
public class MetricsPersistenceServiceTests
{
    private sealed class RecordingMetricsRepository : IMetricsRepository
    {
        public int SaveCountersAndSnapshotCalls;
        public int UpsertCountersOnlyCalls;

        public Task<(long MessagesProcessed, long RulesExecuted, long MessagesDropped,
                     long RulesErrored, long ActionsErrored)?> GetCountersAsync(CancellationToken ct = default)
            => Task.FromResult<(long, long, long, long, long)?>(null);

        public Task UpsertCountersAsync(long m, long r, long d, long re, long ae, CancellationToken ct = default)
        {
            Interlocked.Increment(ref UpsertCountersOnlyCalls);
            return Task.CompletedTask;
        }

        public Task SaveSnapshotAsync(SystemStats stats, CancellationToken ct = default) => Task.CompletedTask;

        public Task SaveCountersAndSnapshotAsync(long m, long r, long d, long re, long ae, SystemStats stats, CancellationToken ct = default)
        {
            Interlocked.Increment(ref SaveCountersAndSnapshotCalls);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<SystemStats>> GetRecentSnapshotsAsync(int limit = 100, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SystemStats>>(Array.Empty<SystemStats>());

        public Task<double> GetRateOverWindowAsync(TimeSpan window, CancellationToken ct = default)
            => Task.FromResult(0d);
    }

    private static (MetricsPersistenceService svc, StatsService stats, RecordingMetricsRepository repo) Build()
    {
        var stats = new StatsService(
            new InMemoryDeviceService(Array.Empty<Device>()),
            new InMemoryRulesService(),
            new NoopMetricsRepository());
        var repo = new RecordingMetricsRepository();
        var settings = Options.Create(new HermodSettings
        {
            Features = new FeaturesSettings { StatsRollup = true },
            Metrics = new MetricsSettings { FlushIntervalSeconds = 1 },
        });
        var svc = new MetricsPersistenceService(
            stats, repo, new HermodMetrics(), settings,
            NullLogger<MetricsPersistenceService>.Instance);
        svc.MarkSeedCompletedForTests();
        return (svc, stats, repo);
    }

    [Fact]
    public async Task FlushAsync_IdleTickAfterChange_DoesNotWriteAnotherSnapshot()
    {
        var (svc, stats, repo) = Build();

        stats.IncrementMessagesProcessed();
        await svc.FlushAsync(CancellationToken.None);
        await svc.FlushAsync(CancellationToken.None);
        await svc.FlushAsync(CancellationToken.None);

        Assert.Equal(1, repo.SaveCountersAndSnapshotCalls);
        Assert.Equal(2, repo.UpsertCountersOnlyCalls);
    }

    [Fact]
    public async Task FlushAsync_RulesErroredTick_TriggersSnapshotEvenOnFlatThroughput()
    {
        // The acbdcbd fix: before the dedup-key widening, this test would
        // only see 1 snapshot (the initial messages-processed tick). After
        // the fix it sees 2 — the error burst reopens the snapshot gate.
        var (svc, stats, repo) = Build();

        stats.IncrementMessagesProcessed();
        await svc.FlushAsync(CancellationToken.None);

        stats.IncrementRulesErrored();
        await svc.FlushAsync(CancellationToken.None);

        Assert.Equal(2, repo.SaveCountersAndSnapshotCalls);
    }

    [Fact]
    public async Task FlushAsync_MessagesDroppedTick_TriggersSnapshotEvenOnFlatThroughput()
    {
        var (svc, stats, repo) = Build();

        stats.IncrementMessagesProcessed();
        await svc.FlushAsync(CancellationToken.None);

        stats.IncrementMessagesDropped();
        await svc.FlushAsync(CancellationToken.None);

        Assert.Equal(2, repo.SaveCountersAndSnapshotCalls);
    }

    [Fact]
    public async Task FlushAsync_ActionsErroredTick_TriggersSnapshotEvenOnFlatThroughput()
    {
        var (svc, stats, repo) = Build();

        stats.IncrementMessagesProcessed();
        await svc.FlushAsync(CancellationToken.None);

        stats.IncrementActionsErrored();
        await svc.FlushAsync(CancellationToken.None);

        Assert.Equal(2, repo.SaveCountersAndSnapshotCalls);
    }

    [Fact]
    public async Task FlushAsync_UnseededService_SkipsBothCountersAndSnapshot()
    {
        var (svc, stats, repo) = Build();
        typeof(MetricsPersistenceService)
            .GetField("_seedCompleted", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(svc, false);

        stats.IncrementMessagesProcessed();
        await svc.FlushAsync(CancellationToken.None);

        Assert.Equal(0, repo.SaveCountersAndSnapshotCalls);
        Assert.Equal(0, repo.UpsertCountersOnlyCalls);
    }
}
