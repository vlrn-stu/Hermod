using Hermod.Coordinator.Controllers;
using Hermod.Coordinator.UnitTests.TestUtilities;
using Hermod.Core.Interfaces;
using Hermod.Core.Models;
using Hermod.TestInfrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Hermod.Coordinator.UnitTests;

/// <summary>
/// Pins <see cref="DevicesController"/>'s REST contract. Covers the
/// authorization metadata, each endpoint's happy path, the 404 misses,
/// and the Zigbee-rename coordination with <see cref="IZigbee2MqttService"/>
/// (bridge-first so a bridge refusal never leaves the coordinator and
/// Z2M disagreeing on the friendly name).
/// </summary>
public class DevicesControllerTests
{
    [Fact]
    public void DevicesController_HasClassLevelAuthorize()
        => ControllerAttributeAsserts.AssertHasClassAuthorize<DevicesController>();

    [Fact]
    public void DevicesController_NoAllowAnonymousOnEndpoints()
        => ControllerAttributeAsserts.AssertNoAllowAnonymousOnEndpoints<DevicesController>();

    [Fact]
    public void DevicesController_ExpectedEndpointMethodsArePresent()
        => ControllerAttributeAsserts.AssertEndpointMethodsPresent<DevicesController>(
            "GetAll", "GetById", "CreateOrUpdate", "UpdateState", "UpdateStatus", "Rename", "Delete");

    private static DevicesController Build(
        InMemoryDeviceService? devices = null,
        StubZigbeeService? zigbee = null)
    {
        return new DevicesController(
            devices ?? new InMemoryDeviceService(),
            zigbee ?? new StubZigbeeService(),
            NullLogger<DevicesController>.Instance);
    }

    private static Device MakeDevice(string id, Protocol proto = Protocol.Zigbee) =>
        new() { Id = id, Name = id, Protocol = proto };

    [Fact]
    public async Task GetAll_NoFilter_ReturnsEveryDevice()
    {
        var devices = new InMemoryDeviceService(new[] { MakeDevice("a"), MakeDevice("b", Protocol.Lora) });
        var sut = Build(devices);

        var result = await sut.GetAll(protocol: null);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var page = Assert.IsType<DevicePage>(ok.Value);
        Assert.Equal(2, page.Total);
        Assert.Equal(2, page.Items.Count);
    }

    [Fact]
    public async Task GetAll_WithProtocolFilter_PassesThroughToService()
    {
        var devices = new InMemoryDeviceService(new[]
        {
            MakeDevice("lamp", Protocol.Zigbee),
            MakeDevice("sensor", Protocol.Lora),
        });
        var sut = Build(devices);

        var result = await sut.GetAll(protocol: Protocol.Lora);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var page = Assert.IsType<DevicePage>(ok.Value);
        var row = Assert.Single(page.Items);
        Assert.Equal("sensor", row.Id);
    }

    [Fact]
    public async Task GetById_Missing_Returns404WithMessage()
    {
        var sut = Build();

        var result = await sut.GetById("ghost");

        var nf = Assert.IsType<NotFoundObjectResult>(result.Result);
        Assert.Contains("ghost", nf.Value!.ToString() ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetById_Present_ReturnsDevice()
    {
        var devices = new InMemoryDeviceService(new[] { MakeDevice("lamp") });
        var sut = Build(devices);

        var result = await sut.GetById("lamp");

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var d = Assert.IsType<Device>(ok.Value);
        Assert.Equal("lamp", d.Id);
    }

    [Fact]
    public async Task CreateOrUpdate_EmptyId_ReturnsBadRequest()
    {
        var sut = Build();

        var result = await sut.CreateOrUpdate(new Device { Id = "" });

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task CreateOrUpdate_ValidDevice_Persists()
    {
        var devices = new InMemoryDeviceService();
        var sut = Build(devices);

        var result = await sut.CreateOrUpdate(MakeDevice("new-dev"));

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal("new-dev", Assert.IsType<Device>(ok.Value).Id);
        Assert.True(devices.Devices.ContainsKey("new-dev"));
    }

    [Fact]
    public async Task UpdateState_MissingDevice_Returns404()
    {
        var sut = Build();

        var result = await sut.UpdateState("ghost", new Dictionary<string, object>());

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task UpdateState_PresentDevice_MergesAndReturnsOk()
    {
        var devices = new InMemoryDeviceService(new[] { MakeDevice("lamp") });
        var sut = Build(devices);

        var result = await sut.UpdateState("lamp", new Dictionary<string, object> { ["on"] = true });

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(true, devices.Devices["lamp"].State["on"]);
    }

    [Fact]
    public async Task UpdateStatus_MissingDevice_Returns404()
    {
        var sut = Build();

        var result = await sut.UpdateStatus("ghost", new DeviceStatusUpdate { Status = DeviceStatus.Online });

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("bad/name with\tbackslash\\")]
    [InlineData("evil;shell$injection")]
    public async Task Rename_InvalidName_ReturnsBadRequest(string badName)
    {
        var devices = new InMemoryDeviceService(new[] { MakeDevice("lamp") });
        var sut = Build(devices);

        var result = await sut.Rename("lamp", new DeviceRenameRequest { NewName = badName });

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task Rename_SameName_NoOpReturnsDevice()
    {
        var devices = new InMemoryDeviceService(new[] { MakeDevice("lamp") });
        var sut = Build(devices);

        var result = await sut.Rename("lamp", new DeviceRenameRequest { NewName = "lamp" });

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal("lamp", Assert.IsType<Device>(ok.Value).Id);
    }

    [Fact]
    public async Task Rename_MissingDevice_Returns404()
    {
        var sut = Build();

        var result = await sut.Rename("ghost", new DeviceRenameRequest { NewName = "phantom" });

        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    [Fact]
    public async Task Rename_ZigbeeDevice_BridgeRefuses_ReturnsConflictAndDoesNotRenameLocally()
    {
        var devices = new InMemoryDeviceService(new[] { MakeDevice("lamp", Protocol.Zigbee) });
        var zigbee = new StubZigbeeService { RenameResult = false };
        var sut = Build(devices, zigbee);

        var result = await sut.Rename("lamp", new DeviceRenameRequest { NewName = "new-lamp" });

        Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.True(devices.Devices.ContainsKey("lamp"));
        Assert.False(devices.Devices.ContainsKey("new-lamp"));
    }

    [Fact]
    public async Task Rename_ZigbeeDevice_BridgeThrows_Returns502AndDoesNotRenameLocally()
    {
        var devices = new InMemoryDeviceService(new[] { MakeDevice("lamp", Protocol.Zigbee) });
        var zigbee = new StubZigbeeService { RenameThrows = new InvalidOperationException("bridge down") };
        var sut = Build(devices, zigbee);

        var result = await sut.Rename("lamp", new DeviceRenameRequest { NewName = "new-lamp" });

        var obj = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status502BadGateway, obj.StatusCode);
        Assert.True(devices.Devices.ContainsKey("lamp"));
    }

    [Fact]
    public async Task Rename_NonZigbeeDevice_RenamesLocallyWithoutBridgeCall()
    {
        var devices = new InMemoryDeviceService(new[] { MakeDevice("sensor", Protocol.Lora) });
        var zigbee = new StubZigbeeService();
        var sut = Build(devices, zigbee);

        var result = await sut.Rename("sensor", new DeviceRenameRequest { NewName = "new-sensor" });

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal("new-sensor", Assert.IsType<Device>(ok.Value).Id);
        Assert.False(devices.Devices.ContainsKey("sensor"));
        Assert.True(devices.Devices.ContainsKey("new-sensor"));
        Assert.Equal(0, zigbee.RenameCalls);
    }

    [Fact]
    public async Task Delete_MissingDevice_Returns404()
    {
        var sut = Build();

        var result = await sut.Delete("ghost");

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task Delete_PresentDevice_RemovesAndReturnsOk()
    {
        var devices = new InMemoryDeviceService(new[] { MakeDevice("lamp") });
        var sut = Build(devices);

        var result = await sut.Delete("lamp");

        Assert.IsType<OkObjectResult>(result);
        Assert.False(devices.Devices.ContainsKey("lamp"));
    }

    // Controller only calls RenameDeviceAsync; every other interface
    // member throws so an unexpected call surfaces immediately rather
    // than silently returning a default.
    private sealed class StubZigbeeService : IZigbee2MqttService
    {
        public bool RenameResult { get; set; } = true;
        public Exception? RenameThrows { get; set; }
        public int RenameCalls;

        public Task<bool> RenameDeviceAsync(string currentName, string newName, bool homeAssistantRename = false, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref RenameCalls);
            if (RenameThrows is not null) throw RenameThrows;
            return Task.FromResult(RenameResult);
        }

        public bool IsBridgeOnline => false;
        public Zigbee2MqttBridgeInfo? BridgeInfo => null;
        public IReadOnlyList<Zigbee2MqttDevice> Devices => Array.Empty<Zigbee2MqttDevice>();
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
        private static Task<T> Fail<T>() => throw new NotSupportedException("test stub: not part of the exercised path");

        public Task SetDeviceStateAsync(string deviceName, object state, CancellationToken cancellationToken = default) => Fail();
        public Task SetDevicePropertyAsync(string deviceName, string property, object value, CancellationToken cancellationToken = default) => Fail();
        public Task SetDevicePowerAsync(string deviceName, bool on, CancellationToken cancellationToken = default) => Fail();
        public Task SetLightBrightnessAsync(string deviceName, int brightness, CancellationToken cancellationToken = default) => Fail();
        public Task SetLightColorTempAsync(string deviceName, int colorTemp, CancellationToken cancellationToken = default) => Fail();
        public Task SetLightColorAsync(string deviceName, double x, double y, CancellationToken cancellationToken = default) => Fail();
        public Task GetDeviceStateAsync(string deviceName, string? property = null, CancellationToken cancellationToken = default) => Fail();
        public Task<Zigbee2MqttResponse<Zigbee2MqttPermitJoinResponse>?> PermitJoinAsync(int timeSeconds, string? device = null, CancellationToken cancellationToken = default) => Fail<Zigbee2MqttResponse<Zigbee2MqttPermitJoinResponse>?>();
        public Task<Zigbee2MqttResponse<Zigbee2MqttHealthCheckResponse>?> HealthCheckAsync(CancellationToken cancellationToken = default) => Fail<Zigbee2MqttResponse<Zigbee2MqttHealthCheckResponse>?>();
        public Task<Zigbee2MqttResponse<Zigbee2MqttNetworkMapResponse>?> GetNetworkMapAsync(string type = "graphviz", bool routes = false, CancellationToken cancellationToken = default) => Fail<Zigbee2MqttResponse<Zigbee2MqttNetworkMapResponse>?>();
        public Task RestartBridgeAsync(CancellationToken cancellationToken = default) => Fail();
        public Task<bool> RemoveDeviceAsync(string deviceName, bool force = false, bool block = false, CancellationToken cancellationToken = default) => Fail<bool>();
        public Task ConfigureDeviceAsync(string deviceName, CancellationToken cancellationToken = default) => Fail();
        public Task InterviewDeviceAsync(string deviceName, CancellationToken cancellationToken = default) => Fail();
        public Task SetDeviceOptionsAsync(string deviceName, Dictionary<string, object> options, CancellationToken cancellationToken = default) => Fail();
        public Task<bool> CreateGroupAsync(string friendlyName, int? id = null, CancellationToken cancellationToken = default) => Fail<bool>();
        public Task<bool> RemoveGroupAsync(string groupName, bool force = false, CancellationToken cancellationToken = default) => Fail<bool>();
        public Task<bool> RenameGroupAsync(string currentName, string newName, CancellationToken cancellationToken = default) => Fail<bool>();
        public Task AddDeviceToGroupAsync(string groupName, string deviceName, int? endpoint = null, CancellationToken cancellationToken = default) => Fail();
        public Task RemoveDeviceFromGroupAsync(string groupName, string deviceName, int? endpoint = null, CancellationToken cancellationToken = default) => Fail();
        public Task SetGroupStateAsync(string groupName, object state, CancellationToken cancellationToken = default) => Fail();
        public void ProcessMessage(Hermod.Core.Models.MqttMessage message) => throw new NotSupportedException("test stub");
        public Zigbee2MqttDevice? GetDevice(string friendlyName) => null;
        public Zigbee2MqttDevice? GetDeviceByIeee(string ieeeAddress) => null;
        public Dictionary<string, object>? GetDeviceState(string friendlyName) => null;
    }
}
