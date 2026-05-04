using Hermod.Core.Interfaces;
using Hermod.Core.Models;
using Hermod.Core.Models.Rules;
using Hermod.Infrastructure.Services;
using Hermod.TestInfrastructure;
using Xunit;

namespace Hermod.UnitTests;

/// <summary>
/// Pins the consistency fix on DevicesByProtocol. Previously
/// `GetCurrentStatsAsync` counted Protocol.Unknown devices while
/// `GetProtocolStatsAsync` excluded them, producing two different
/// totals on the dashboard. Both views now filter Unknown uniformly.
/// </summary>
public class StatsServiceTests
{
    private static StatsService Build(IEnumerable<Device> devices, IEnumerable<Rule>? rules = null)
    {
        var devSvc = new InMemoryDeviceService(devices);
        var rulSvc = new InMemoryRulesService(rules ?? Enumerable.Empty<Rule>());
        return new StatsService(devSvc, rulSvc, new NoopMetricsRepository());
    }

    private sealed class NoopMetricsRepository : IMetricsRepository
    {
        public Task<(long, long, long, long, long)?> GetCountersAsync(CancellationToken ct = default)
            => Task.FromResult<(long, long, long, long, long)?>(null);
        public Task UpsertCountersAsync(long m, long r, long d, long re, long ae, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task SaveSnapshotAsync(SystemStats stats, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task SaveCountersAndSnapshotAsync(long m, long r, long d, long re, long ae, SystemStats stats, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task<IReadOnlyList<SystemStats>> GetRecentSnapshotsAsync(int limit = 100, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SystemStats>>(Array.Empty<SystemStats>());
        public Task<double> GetRateOverWindowAsync(TimeSpan window, CancellationToken ct = default)
            => Task.FromResult(0d);
    }

    private static Device Make(string id, Protocol p, DeviceStatus status = DeviceStatus.Online)
        => new() { Id = id, Name = id, Protocol = p, Status = status };

    [Fact]
    public async Task GetCurrentStatsAsync_ExcludesUnknownProtocolFromDevicesByProtocol()
    {
        var devices = new[]
        {
            Make("lamp", Protocol.Zigbee),
            Make("sensor", Protocol.Lora),
            Make("mystery_1", Protocol.Unknown),
            Make("mystery_2", Protocol.Unknown)
        };
        var sut = Build(devices);

        var stats = await sut.GetCurrentStatsAsync();

        Assert.DoesNotContain(Protocol.Unknown, stats.DevicesByProtocol);
        Assert.Equal(1, stats.DevicesByProtocol[Protocol.Zigbee]);
        Assert.Equal(1, stats.DevicesByProtocol[Protocol.Lora]);
    }

    [Fact]
    public async Task GetCurrentStatsAsync_DevicesByProtocolSumEqualsProtocolStatsSum()
    {
        // The core consistency assertion: the two views must agree on
        // "total devices per protocol". Summing DevicesByProtocol
        // values and summing GetProtocolStatsAsync DeviceCount values
        // must produce the same number.
        var devices = new[]
        {
            Make("lamp", Protocol.Zigbee),
            Make("sensor", Protocol.Lora),
            Make("fridge", Protocol.Bluetooth),
            Make("shelly", Protocol.Wifi),
            Make("mystery", Protocol.Unknown)
        };
        var sut = Build(devices);

        var stats = await sut.GetCurrentStatsAsync();
        var protocolStats = (await sut.GetProtocolStatsAsync()).ToList();

        var fromDict = stats.DevicesByProtocol.Values.Sum();
        var fromList = protocolStats.Sum(p => p.DeviceCount);

        Assert.Equal(fromList, fromDict);
        Assert.Equal(4, fromDict); // 4 non-Unknown devices
    }

    [Fact]
    public async Task IncrementMessagesByProtocol_Accumulates_PerProtocol()
    {
        // Per-protocol increments surface on GetProtocolStatsAsync;
        // Unknown is silently dropped (it has no slot in the dictionary).
        var sut = Build(new[] { Make("lamp", Protocol.Zigbee), Make("sensor", Protocol.Lora) });

        sut.IncrementMessagesByProtocol(Protocol.Zigbee);
        sut.IncrementMessagesByProtocol(Protocol.Zigbee);
        sut.IncrementMessagesByProtocol(Protocol.Lora);
        sut.IncrementMessagesByProtocol(Protocol.Unknown);

        var protocolStats = (await sut.GetProtocolStatsAsync()).ToDictionary(p => p.Protocol);

        Assert.Equal(2, protocolStats[Protocol.Zigbee].MessageCount);
        Assert.Equal(1, protocolStats[Protocol.Lora].MessageCount);
        Assert.Equal(0, protocolStats[Protocol.Bluetooth].MessageCount);
        Assert.Equal(0, protocolStats[Protocol.Wifi].MessageCount);
    }

    [Fact]
    public async Task GetCurrentStatsAsync_AllUnknownDevices_ProducesEmptyDevicesByProtocol()
    {
        // Degenerate case: every device has Protocol.Unknown. A
        // previous implementation produced `{Unknown: 3}`; the current
        // one emits an empty dict.
        var devices = new[]
        {
            Make("u1", Protocol.Unknown),
            Make("u2", Protocol.Unknown),
            Make("u3", Protocol.Unknown)
        };
        var sut = Build(devices);

        var stats = await sut.GetCurrentStatsAsync();

        Assert.Empty(stats.DevicesByProtocol);
        // But TotalDevices still counts all devices, including Unknown.
        // The dict is a per-protocol breakdown, not a "devices that
        // count" list.
        Assert.Equal(3, stats.TotalDevices);
    }

}
