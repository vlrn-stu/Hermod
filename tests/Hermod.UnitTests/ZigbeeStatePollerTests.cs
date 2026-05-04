using Hermod.Core.Configuration;
using Hermod.Core.Interfaces;
using Hermod.Core.Models;
using Hermod.Infrastructure.Zigbee;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Hermod.UnitTests;

/// <summary>
/// Pins <see cref="ZigbeeStatePoller"/>'s sweep logic: skip rules
/// (bridge offline, coordinator, disabled, unnamed) and continue-on-
/// error semantics so one misbehaving device can't starve the rest.
/// Private SweepAsync is promoted to internal for targeted testing;
/// ExecuteAsync's timer loop is not tested (it belongs to Microsoft).
/// </summary>
public class ZigbeeStatePollerTests
{
    private sealed class StubZigbee : IZigbee2MqttService
    {
        public bool IsBridgeOnline { get; set; } = true;
        public List<Zigbee2MqttDevice> DeviceList { get; } = new();
        public List<string> Polled { get; } = new();
        public string? ThrowForDevice { get; set; }
        public string? CancelForDevice { get; set; }

        public Task GetDeviceStateAsync(string deviceName, string? property = null, CancellationToken cancellationToken = default)
        {
            if (deviceName == CancelForDevice) throw new OperationCanceledException();
            if (deviceName == ThrowForDevice) throw new InvalidOperationException("bridge kicked us");
            Polled.Add(deviceName);
            return Task.CompletedTask;
        }

        public IReadOnlyList<Zigbee2MqttDevice> Devices => DeviceList;
        public Zigbee2MqttBridgeInfo? BridgeInfo => null;
        public IReadOnlyList<Zigbee2MqttGroup> Groups => Array.Empty<Zigbee2MqttGroup>();

#pragma warning disable CS0067
        public event EventHandler<Zigbee2MqttBridgeState>? BridgeStateChanged;
        public event EventHandler<Zigbee2MqttBridgeInfo>? BridgeInfoUpdated;
        public event EventHandler<IReadOnlyList<Zigbee2MqttDevice>>? DevicesUpdated;
        public event EventHandler<IReadOnlyList<Zigbee2MqttGroup>>? GroupsUpdated;
        public event EventHandler<Zigbee2MqttDeviceStateEvent>? DeviceStateUpdated;
        public event EventHandler<Zigbee2MqttDeviceAvailabilityEvent>? DeviceAvailabilityChanged;
        public event EventHandler<Zigbee2MqttBridgeEvent>? BridgeEventReceived;
        public event EventHandler<Zigbee2MqttLogMessage>? LogMessageReceived;
#pragma warning restore CS0067

        private static Task Fail() => throw new NotSupportedException("test stub: not part of the exercised path");
        private static T Fail<T>() => throw new NotSupportedException("test stub: not part of the exercised path");

        public Task SetDeviceStateAsync(string deviceName, object state, CancellationToken ct = default) => Fail();
        public Task SetDevicePropertyAsync(string deviceName, string property, object value, CancellationToken ct = default) => Fail();
        public Task SetDevicePowerAsync(string deviceName, bool on, CancellationToken ct = default) => Fail();
        public Task SetLightBrightnessAsync(string deviceName, int brightness, CancellationToken ct = default) => Fail();
        public Task SetLightColorTempAsync(string deviceName, int colorTemp, CancellationToken ct = default) => Fail();
        public Task SetLightColorAsync(string deviceName, double x, double y, CancellationToken ct = default) => Fail();
        public Task<Zigbee2MqttResponse<Zigbee2MqttPermitJoinResponse>?> PermitJoinAsync(int timeSeconds, string? device = null, CancellationToken ct = default) => Fail<Task<Zigbee2MqttResponse<Zigbee2MqttPermitJoinResponse>?>>();
        public Task<Zigbee2MqttResponse<Zigbee2MqttHealthCheckResponse>?> HealthCheckAsync(CancellationToken ct = default) => Fail<Task<Zigbee2MqttResponse<Zigbee2MqttHealthCheckResponse>?>>();
        public Task<Zigbee2MqttResponse<Zigbee2MqttNetworkMapResponse>?> GetNetworkMapAsync(string type = "graphviz", bool routes = false, CancellationToken ct = default) => Fail<Task<Zigbee2MqttResponse<Zigbee2MqttNetworkMapResponse>?>>();
        public Task RestartBridgeAsync(CancellationToken ct = default) => Fail();
        public Task<bool> RenameDeviceAsync(string currentName, string newName, bool homeAssistantRename = false, CancellationToken ct = default) => Fail<Task<bool>>();
        public Task<bool> RemoveDeviceAsync(string deviceName, bool force = false, bool block = false, CancellationToken ct = default) => Fail<Task<bool>>();
        public Task ConfigureDeviceAsync(string deviceName, CancellationToken ct = default) => Fail();
        public Task InterviewDeviceAsync(string deviceName, CancellationToken ct = default) => Fail();
        public Task SetDeviceOptionsAsync(string deviceName, Dictionary<string, object> options, CancellationToken ct = default) => Fail();
        public Task<bool> CreateGroupAsync(string friendlyName, int? id = null, CancellationToken ct = default) => Fail<Task<bool>>();
        public Task<bool> RemoveGroupAsync(string groupName, bool force = false, CancellationToken ct = default) => Fail<Task<bool>>();
        public Task<bool> RenameGroupAsync(string currentName, string newName, CancellationToken ct = default) => Fail<Task<bool>>();
        public Task AddDeviceToGroupAsync(string groupName, string deviceName, int? endpoint = null, CancellationToken ct = default) => Fail();
        public Task RemoveDeviceFromGroupAsync(string groupName, string deviceName, int? endpoint = null, CancellationToken ct = default) => Fail();
        public Task SetGroupStateAsync(string groupName, object state, CancellationToken ct = default) => Fail();
        public void ProcessMessage(MqttMessage message) => throw new NotSupportedException("test stub");
        public Zigbee2MqttDevice? GetDevice(string friendlyName) => null;
        public Zigbee2MqttDevice? GetDeviceByIeee(string ieeeAddress) => null;
        public Dictionary<string, object>? GetDeviceState(string friendlyName) => null;
    }

    private static ZigbeeStatePoller Build(StubZigbee zigbee)
    {
        var settings = Options.Create(new HermodSettings
        {
            Zigbee = new ZigbeeSettings
            {
                StatePoller = new ZigbeeStatePollerSettings { Enabled = true, IntervalSeconds = 60, PerDeviceDelayMs = 0 },
            },
        });
        return new ZigbeeStatePoller(zigbee, settings, NullLogger<ZigbeeStatePoller>.Instance);
    }

    private static Zigbee2MqttDevice Dev(string name, string type = "EndDevice", bool disabled = false) =>
        new() { FriendlyName = name, Type = type, Disabled = disabled };

    [Fact]
    public async Task Sweep_BridgeOffline_SkipsPoll()
    {
        var zb = new StubZigbee { IsBridgeOnline = false };
        zb.DeviceList.Add(Dev("lamp"));
        var sut = Build(zb);

        await sut.SweepAsync(TimeSpan.Zero, CancellationToken.None);

        Assert.Empty(zb.Polled);
    }

    [Fact]
    public async Task Sweep_NoDevices_NoCalls()
    {
        var zb = new StubZigbee();
        var sut = Build(zb);

        await sut.SweepAsync(TimeSpan.Zero, CancellationToken.None);

        Assert.Empty(zb.Polled);
    }

    [Fact]
    public async Task Sweep_CoordinatorDevice_Skipped()
    {
        var zb = new StubZigbee();
        zb.DeviceList.Add(Dev("coord", type: "Coordinator"));
        zb.DeviceList.Add(Dev("lamp"));
        var sut = Build(zb);

        await sut.SweepAsync(TimeSpan.Zero, CancellationToken.None);

        Assert.Equal(new[] { "lamp" }, zb.Polled);
    }

    [Fact]
    public async Task Sweep_DisabledDevice_Skipped()
    {
        var zb = new StubZigbee();
        zb.DeviceList.Add(Dev("broken", disabled: true));
        zb.DeviceList.Add(Dev("lamp"));
        var sut = Build(zb);

        await sut.SweepAsync(TimeSpan.Zero, CancellationToken.None);

        Assert.Equal(new[] { "lamp" }, zb.Polled);
    }

    [Fact]
    public async Task Sweep_EmptyFriendlyName_Skipped()
    {
        var zb = new StubZigbee();
        zb.DeviceList.Add(Dev(""));
        zb.DeviceList.Add(Dev("lamp"));
        var sut = Build(zb);

        await sut.SweepAsync(TimeSpan.Zero, CancellationToken.None);

        Assert.Equal(new[] { "lamp" }, zb.Polled);
    }

    [Fact]
    public async Task Sweep_PollsEveryEligibleDeviceOnce()
    {
        var zb = new StubZigbee();
        zb.DeviceList.Add(Dev("lamp"));
        zb.DeviceList.Add(Dev("motion"));
        zb.DeviceList.Add(Dev("contact"));
        var sut = Build(zb);

        await sut.SweepAsync(TimeSpan.Zero, CancellationToken.None);

        Assert.Equal(3, zb.Polled.Count);
        Assert.Contains("lamp", zb.Polled);
        Assert.Contains("motion", zb.Polled);
        Assert.Contains("contact", zb.Polled);
    }

    [Fact]
    public async Task Sweep_PerDeviceException_ContinuesToNextDevice()
    {
        var zb = new StubZigbee { ThrowForDevice = "broken" };
        zb.DeviceList.Add(Dev("broken"));
        zb.DeviceList.Add(Dev("lamp"));
        var sut = Build(zb);

        await sut.SweepAsync(TimeSpan.Zero, CancellationToken.None);

        // "broken" throws before Polled.Add; "lamp" still gets polled.
        Assert.Equal(new[] { "lamp" }, zb.Polled);
    }

    [Fact]
    public async Task Sweep_OperationCanceled_Propagates()
    {
        // Cancellation must NOT be swallowed as a per-device exception —
        // the sweep should stop and the host can cancel the whole service.
        var zb = new StubZigbee { CancelForDevice = "cancel-me" };
        zb.DeviceList.Add(Dev("cancel-me"));
        zb.DeviceList.Add(Dev("lamp"));
        var sut = Build(zb);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => sut.SweepAsync(TimeSpan.Zero, CancellationToken.None));
    }

    [Fact]
    public async Task Sweep_UpfrontTokenCanceled_ExitsWithoutPolling()
    {
        var zb = new StubZigbee();
        zb.DeviceList.Add(Dev("lamp"));
        zb.DeviceList.Add(Dev("motion"));
        var sut = Build(zb);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await sut.SweepAsync(TimeSpan.Zero, cts.Token);

        Assert.Empty(zb.Polled);
    }
}
