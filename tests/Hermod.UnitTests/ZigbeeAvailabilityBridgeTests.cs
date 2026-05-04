using Hermod.Core.Interfaces;
using Hermod.Core.Models;
using Hermod.Infrastructure.Zigbee;
using Hermod.TestInfrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Hermod.UnitTests;

/// <summary>
/// Pins the Zigbee2MQTT-to-IDeviceService availability bridge introduced
/// alongside the <c>IDeviceService.AvailabilityChanged</c> event so the
/// rules engine's <c>OnAvailability</c> trigger fires for Zigbee devices
/// going online/offline — the Z2M event previously dead-ended inside the
/// bridge client.
/// </summary>
public class ZigbeeAvailabilityBridgeTests
{
    [Fact]
    public async Task ZigbeeAvailability_OnlineEdge_ForwardsToDeviceService()
    {
        var z2m = new FakeZigbeeService();
        var devices = new InMemoryDeviceService();
        devices.Devices["aqara_motion_lr"] = new Device
        {
            Id = "aqara_motion_lr",
            Name = "Living Room Motion",
            Protocol = Protocol.Zigbee,
            Status = DeviceStatus.Offline
        };

        var bridge = new ZigbeeAvailabilityBridge(z2m, devices, NullLogger<ZigbeeAvailabilityBridge>.Instance);
        await bridge.StartAsync(default);

        z2m.Raise(new Zigbee2MqttDeviceAvailabilityEvent
        {
            DeviceName = "aqara_motion_lr",
            IsOnline = true
        });

        // Propagation is fire-and-forget; give it a moment to complete.
        await WaitForStatusAsync(devices, "aqara_motion_lr", DeviceStatus.Online);

        Assert.Equal(DeviceStatus.Online, devices.Devices["aqara_motion_lr"].Status);
    }

    [Fact]
    public async Task ZigbeeAvailability_OfflineEdge_ForwardsToDeviceService()
    {
        var z2m = new FakeZigbeeService();
        var devices = new InMemoryDeviceService();
        devices.Devices["aqara_motion_lr"] = new Device
        {
            Id = "aqara_motion_lr",
            Protocol = Protocol.Zigbee,
            Status = DeviceStatus.Online
        };

        var bridge = new ZigbeeAvailabilityBridge(z2m, devices, NullLogger<ZigbeeAvailabilityBridge>.Instance);
        await bridge.StartAsync(default);

        z2m.Raise(new Zigbee2MqttDeviceAvailabilityEvent
        {
            DeviceName = "aqara_motion_lr",
            IsOnline = false
        });

        await WaitForStatusAsync(devices, "aqara_motion_lr", DeviceStatus.Offline);
        Assert.Equal(DeviceStatus.Offline, devices.Devices["aqara_motion_lr"].Status);
    }

    [Fact]
    public async Task EmptyDeviceName_IsIgnored()
    {
        var z2m = new FakeZigbeeService();
        var devices = new InMemoryDeviceService();

        var bridge = new ZigbeeAvailabilityBridge(z2m, devices, NullLogger<ZigbeeAvailabilityBridge>.Instance);
        await bridge.StartAsync(default);

        z2m.Raise(new Zigbee2MqttDeviceAvailabilityEvent { DeviceName = "", IsOnline = true });

        // FakeZigbeeService.Raise synchronously invokes the handler, and the
        // empty-name branch returns before scheduling PropagateAsync — no
        // fire-and-forget work to wait on, so the assertion is deterministic.
        Assert.Empty(devices.Devices);
    }

    [Fact]
    public async Task StopAsync_UnsubscribesSoFurtherEventsAreIgnored()
    {
        var z2m = new FakeZigbeeService();
        var devices = new InMemoryDeviceService();
        devices.Devices["d1"] = new Device { Id = "d1", Protocol = Protocol.Zigbee, Status = DeviceStatus.Offline };

        var bridge = new ZigbeeAvailabilityBridge(z2m, devices, NullLogger<ZigbeeAvailabilityBridge>.Instance);
        await bridge.StartAsync(default);
        await bridge.StopAsync(default);

        z2m.Raise(new Zigbee2MqttDeviceAvailabilityEvent { DeviceName = "d1", IsOnline = true });

        await Task.Delay(50);
        Assert.Equal(DeviceStatus.Offline, devices.Devices["d1"].Status);
    }

    private static async Task WaitForStatusAsync(
        InMemoryDeviceService devices,
        string id,
        DeviceStatus expected,
        int timeoutMs = 1000)
    {
        var start = DateTime.UtcNow;
        while ((DateTime.UtcNow - start).TotalMilliseconds < timeoutMs)
        {
            if (devices.Devices.TryGetValue(id, out var d) && d.Status == expected) return;
            await Task.Delay(10);
        }
    }

#pragma warning disable CS0067 // events are required by the interface but unused in this stub
    private sealed class FakeZigbeeService : IZigbee2MqttService
    {
        public event EventHandler<Zigbee2MqttBridgeState>? BridgeStateChanged;
        public event EventHandler<Zigbee2MqttBridgeInfo>? BridgeInfoUpdated;
        public event EventHandler<IReadOnlyList<Zigbee2MqttDevice>>? DevicesUpdated;
        public event EventHandler<IReadOnlyList<Zigbee2MqttGroup>>? GroupsUpdated;
        public event EventHandler<Zigbee2MqttDeviceStateEvent>? DeviceStateUpdated;
        public event EventHandler<Zigbee2MqttDeviceAvailabilityEvent>? DeviceAvailabilityChanged;
        public event EventHandler<Zigbee2MqttBridgeEvent>? BridgeEventReceived;
        public event EventHandler<Zigbee2MqttLogMessage>? LogMessageReceived;

        public bool IsBridgeOnline => true;
        public Zigbee2MqttBridgeInfo? BridgeInfo => null;
        public IReadOnlyList<Zigbee2MqttDevice> Devices => Array.Empty<Zigbee2MqttDevice>();
        public IReadOnlyList<Zigbee2MqttGroup> Groups => Array.Empty<Zigbee2MqttGroup>();

        public void Raise(Zigbee2MqttDeviceAvailabilityEvent e) =>
            DeviceAvailabilityChanged?.Invoke(this, e);

        public void ProcessMessage(MqttMessage message) { }
        public Task SetDeviceStateAsync(string deviceName, object state, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetDevicePropertyAsync(string deviceName, string property, object value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetDevicePowerAsync(string deviceName, bool on, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetLightBrightnessAsync(string deviceName, int brightness, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetLightColorTempAsync(string deviceName, int colorTemp, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetLightColorAsync(string deviceName, double x, double y, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task GetDeviceStateAsync(string deviceName, string? property = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<Zigbee2MqttResponse<Zigbee2MqttPermitJoinResponse>?> PermitJoinAsync(int timeSeconds, string? device = null, CancellationToken cancellationToken = default) => Task.FromResult<Zigbee2MqttResponse<Zigbee2MqttPermitJoinResponse>?>(null);
        public Task<Zigbee2MqttResponse<Zigbee2MqttHealthCheckResponse>?> HealthCheckAsync(CancellationToken cancellationToken = default) => Task.FromResult<Zigbee2MqttResponse<Zigbee2MqttHealthCheckResponse>?>(null);
        public Task<Zigbee2MqttResponse<Zigbee2MqttNetworkMapResponse>?> GetNetworkMapAsync(string type = "graphviz", bool routes = false, CancellationToken cancellationToken = default) => Task.FromResult<Zigbee2MqttResponse<Zigbee2MqttNetworkMapResponse>?>(null);
        public Task RestartBridgeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> RenameDeviceAsync(string currentName, string newName, bool homeAssistantRename = false, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> RemoveDeviceAsync(string deviceName, bool force = false, bool block = false, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task ConfigureDeviceAsync(string deviceName, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task InterviewDeviceAsync(string deviceName, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetDeviceOptionsAsync(string deviceName, Dictionary<string, object> options, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> CreateGroupAsync(string friendlyName, int? id = null, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> RemoveGroupAsync(string groupName, bool force = false, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> RenameGroupAsync(string currentName, string newName, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task AddDeviceToGroupAsync(string groupName, string deviceName, int? endpoint = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RemoveDeviceFromGroupAsync(string groupName, string deviceName, int? endpoint = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetGroupStateAsync(string groupName, object state, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Zigbee2MqttDevice? GetDevice(string friendlyName) => null;
        public Zigbee2MqttDevice? GetDeviceByIeee(string ieeeAddress) => null;
        public Dictionary<string, object>? GetDeviceState(string friendlyName) => null;
    }
#pragma warning restore CS0067
}
