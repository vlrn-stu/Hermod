using Hermod.Coordinator.Controllers;
using Hermod.Coordinator.UnitTests.TestUtilities;
using Hermod.Core.Interfaces;
using Hermod.Core.Models;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Hermod.Coordinator.UnitTests;

/// <summary>
/// StatsController was previously untested. Covers the four endpoints
/// (current stats, per-protocol, history, aggregate health) plus the
/// authorization metadata so a refactor that silently drops [Authorize]
/// or swaps to AllowAnonymous fails a test instead of shipping.
/// </summary>
public class StatsControllerTests
{
    [Fact]
    public void StatsController_HasClassLevelAuthorizeAttribute()
        => ControllerAttributeAsserts.AssertHasClassAuthorize<StatsController>();

    [Fact]
    public void StatsController_EndpointsDoNotOverrideAuthWithAllowAnonymous()
        => ControllerAttributeAsserts.AssertNoAllowAnonymousOnEndpoints<StatsController>();

    [Fact]
    public void StatsController_ExpectedEndpointMethodsArePresent()
        => ControllerAttributeAsserts.AssertEndpointMethodsPresent<StatsController>(
            "GetStats", "GetProtocolStats", "GetHistory", "GetHealth");

    [Fact]
    public async Task GetStats_ReturnsCurrentSnapshot()
    {
        var stats = new StubStatsService
        {
            CurrentStats = new SystemStats
            {
                TotalDevices = 7,
                OnlineDevices = 5,
                ActiveRules = 3,
                MessagesProcessed = 1234,
            },
        };
        var sut = new StatsController(stats);

        var result = await sut.GetStats();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var body = Assert.IsType<SystemStats>(ok.Value);
        Assert.Equal(7, body.TotalDevices);
        Assert.Equal(1234, body.MessagesProcessed);
    }

    [Fact]
    public async Task GetProtocolStats_ReturnsStatsRows()
    {
        var stats = new StubStatsService
        {
            ProtocolStats = new[]
            {
                new ProtocolStats { Protocol = Protocol.Zigbee, DeviceCount = 4, MessageCount = 400 },
                new ProtocolStats { Protocol = Protocol.Lora, DeviceCount = 2, MessageCount = 20 },
            },
        };
        var sut = new StatsController(stats);

        var result = await sut.GetProtocolStats();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var rows = Assert.IsAssignableFrom<IEnumerable<ProtocolStats>>(ok.Value).ToList();
        Assert.Equal(2, rows.Count);
        Assert.Equal(400, rows[0].MessageCount);
    }

    [Theory]
    [InlineData(0, 1)]        // below min -> clamp up to 1
    [InlineData(-50, 1)]
    [InlineData(5000, 1000)]  // above max -> clamp down to 1000
    [InlineData(500, 500)]    // inside range -> passthrough
    public async Task GetHistory_ClampsLimitArgument(int requested, int expectedForwarded)
    {
        var stats = new StubStatsService();
        var metrics = new RecordingMetricsRepo();
        var sut = new StatsController(stats);

        await sut.GetHistory(metrics, limit: requested);

        Assert.Equal(expectedForwarded, metrics.LastLimit);
    }

    [Fact]
    public async Task GetHealth_MqttConnected_ReportsHealthy()
    {
        var stats = new StubStatsService
        {
            CurrentStats = new SystemStats { Uptime = TimeSpan.FromMinutes(3), TotalDevices = 2, OnlineDevices = 2, ActiveRules = 1, MessagesProcessed = 9 },
        };
        var mqtt = new StubMqttConnected(true);
        var sut = new StatsController(stats);

        var result = await sut.GetHealth(mqtt);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var health = Assert.IsType<HealthStatus>(ok.Value);
        Assert.Equal("healthy", health.Status);
        Assert.True(health.MqttConnected);
        Assert.Equal(TimeSpan.FromMinutes(3), health.Uptime);
    }

    [Fact]
    public async Task GetHealth_MqttDisconnected_ReportsDegraded()
    {
        var stats = new StubStatsService { CurrentStats = new SystemStats() };
        var mqtt = new StubMqttConnected(false);
        var sut = new StatsController(stats);

        var result = await sut.GetHealth(mqtt);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var health = Assert.IsType<HealthStatus>(ok.Value);
        Assert.Equal("degraded", health.Status);
        Assert.False(health.MqttConnected);
    }

    private sealed class StubStatsService : IStatsService
    {
        public SystemStats CurrentStats { get; set; } = new();
        public IEnumerable<ProtocolStats> ProtocolStats { get; set; } = Array.Empty<ProtocolStats>();

        public Task<SystemStats> GetCurrentStatsAsync(CancellationToken ct = default) => Task.FromResult(CurrentStats);
        public Task<IEnumerable<ProtocolStats>> GetProtocolStatsAsync(CancellationToken ct = default) => Task.FromResult(ProtocolStats);
        public void IncrementMessagesProcessed() { }
        public void IncrementMessagesByProtocol(Protocol protocol) { }
        public void IncrementRulesExecuted() { }
        public void IncrementMessagesDropped() { }
        public void IncrementRulesErrored() { }
        public void IncrementActionsErrored() { }
        public void SeedCounters(long m, long r, long d = 0, long re = 0, long ae = 0) { }
        public (long, long, long, long, long) GetCounters() => (0, 0, 0, 0, 0);
        public Task ResetCountersAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RecordingMetricsRepo : IMetricsRepository
    {
        public int LastLimit = -1;

        public Task<(long, long, long, long, long)?> GetCountersAsync(CancellationToken ct = default)
            => Task.FromResult<(long, long, long, long, long)?>(null);
        public Task UpsertCountersAsync(long m, long r, long d, long re, long ae, CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveSnapshotAsync(SystemStats stats, CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveCountersAndSnapshotAsync(long m, long r, long d, long re, long ae, SystemStats stats, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<SystemStats>> GetRecentSnapshotsAsync(int limit = 100, CancellationToken ct = default)
        {
            LastLimit = limit;
            return Task.FromResult<IReadOnlyList<SystemStats>>(Array.Empty<SystemStats>());
        }
        public Task<double> GetRateOverWindowAsync(TimeSpan window, CancellationToken ct = default) => Task.FromResult(0d);
    }

    private sealed class StubMqttConnected : IMqttService
    {
        public StubMqttConnected(bool connected) => IsConnected = connected;
        public bool IsConnected { get; }

#pragma warning disable CS0067
        public event EventHandler<MqttMessage>? MessageReceived;
        public event EventHandler<bool>? ConnectionStateChanged;
#pragma warning restore CS0067

        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SubscribeAsync(string topic, CancellationToken ct = default) => Task.CompletedTask;
        public Task UnsubscribeAsync(string topic, CancellationToken ct = default) => Task.CompletedTask;
        public Task PublishAsync(string topic, string payload, bool retain = false, int qos = 0, CancellationToken ct = default) => Task.CompletedTask;
        public IReadOnlyList<MqttMessage> GetMessageHistory() => Array.Empty<MqttMessage>();
    }
}
