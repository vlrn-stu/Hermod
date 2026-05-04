using Hermod.Coordinator.Controllers;
using Hermod.Coordinator.UnitTests.TestUtilities;
using Hermod.Core.Interfaces;
using Hermod.Core.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Hermod.Coordinator.UnitTests;

public class ActionsControllerTests
{
    private static ActionsController CreateController(FakeMqttService mqtt)
    {
        return new ActionsController(mqtt, NullLogger<ActionsController>.Instance);
    }

    // Pin the admin-only decoration on ActionsController. Without these
    // facts, a refactor that silently drops the class-level [Authorize]
    // would leave the behavioural tests passing but expose every action
    // endpoint to any authenticated user.

    [Fact]
    public void ActionsController_HasClassLevelAuthorizeAttribute()
        => ControllerAttributeAsserts.AssertHasClassAuthorize<ActionsController>();

    [Fact]
    public void ActionsController_RequiresAdminRole()
        => ControllerAttributeAsserts.AssertClassAuthorize<ActionsController>("admin");

    [Fact]
    public void ActionsController_EndpointsDoNotOverrideAuthWithAllowAnonymous()
        => ControllerAttributeAsserts.AssertNoAllowAnonymousOnEndpoints<ActionsController>();

    [Fact]
    public void ActionsController_ExpectedEndpointMethodsArePresent()
        => ControllerAttributeAsserts.AssertEndpointMethodsPresent<ActionsController>(
            "SendDeviceCommand", "Publish");

    [Fact]
    public async Task SendDeviceCommand_MissingProtocol_ReturnsBadRequest()
    {
        var mqtt = new FakeMqttService();
        var controller = CreateController(mqtt);

        var result = await controller.SendDeviceCommand("dev-01", protocol: null, command: new object());

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(mqtt.Published);
    }

    [Fact]
    public async Task SendDeviceCommand_UnknownProtocol_ReturnsBadRequest()
    {
        var mqtt = new FakeMqttService();
        var controller = CreateController(mqtt);

        var result = await controller.SendDeviceCommand("dev-01", protocol: "quantum", command: new object());

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(mqtt.Published);
    }

    [Fact]
    public async Task SendDeviceCommand_UnknownProtocolValue_ReturnsBadRequest()
    {
        var mqtt = new FakeMqttService();
        var controller = CreateController(mqtt);

        // "Unknown" is the enum zero. The validator rejects it explicitly.
        var result = await controller.SendDeviceCommand("dev-01", protocol: "Unknown", command: new object());

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Theory]
    [InlineData("../evil")]
    [InlineData("dev/set")]
    [InlineData("with space")]
    [InlineData("")]
    [InlineData("dev#wild")]
    [InlineData("dev+plus")]
    public async Task SendDeviceCommand_InvalidDeviceId_ReturnsBadRequest(string deviceId)
    {
        var mqtt = new FakeMqttService();
        var controller = CreateController(mqtt);

        var result = await controller.SendDeviceCommand(deviceId, protocol: "zigbee", command: new object());

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(mqtt.Published);
    }

    [Theory]
    [InlineData("dev-01")]
    [InlineData("sensor_42")]
    [InlineData("LampABC")]
    [InlineData("0123456789")]
    public async Task SendDeviceCommand_ValidInput_PublishesToProtocolTopic(string deviceId)
    {
        var mqtt = new FakeMqttService { IsConnected = true };
        var controller = CreateController(mqtt);

        var result = await controller.SendDeviceCommand(deviceId, protocol: "zigbee", command: new { state = "ON" });

        Assert.IsType<OkObjectResult>(result);
        var published = Assert.Single(mqtt.Published);
        // Z2M's base_topic is "zigbee" in every overlay; a Zigbee command
        // must land on zigbee/<device>/set (via Zigbee2MqttTopics.DeviceSet)
        // not zigbee2mqtt/<device>/set, which nobody subscribes to.
        Assert.Equal($"zigbee/{deviceId}/set", published.Topic);
    }

    [Fact]
    public async Task SendDeviceCommand_ProtocolMapsToTopicPrefix()
    {
        var mqtt = new FakeMqttService { IsConnected = true };
        var controller = CreateController(mqtt);

        await controller.SendDeviceCommand("dev-01", protocol: "ZIGBEE", command: new { state = "ON" });
        await controller.SendDeviceCommand("dev-02", protocol: "Lora", command: new { v = 1 });

        Assert.Equal(2, mqtt.Published.Count);
        // Zigbee uses the Z2M base_topic ("zigbee"); LoRa keeps its
        // protocol-prefix routing.
        Assert.Equal("zigbee/dev-01/set", mqtt.Published[0].Topic);
        Assert.Equal("lora/dev-02/set", mqtt.Published[1].Topic);
    }

    [Fact]
    public async Task SendDeviceCommand_BrokerDisconnected_Returns503()
    {
        var mqtt = new FakeMqttService { IsConnected = false };
        var controller = CreateController(mqtt);

        var result = await controller.SendDeviceCommand("dev-01", protocol: "zigbee", command: new object());

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(503, status.StatusCode);
    }

    [Fact]
    public async Task Publish_MissingTopic_ReturnsBadRequest()
    {
        var mqtt = new FakeMqttService { IsConnected = true };
        var controller = CreateController(mqtt);

        var result = await controller.Publish(new PublishRequest { Topic = "", Payload = new { v = 1 } });

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Publish_BrokerDisconnected_Returns503()
    {
        var mqtt = new FakeMqttService { IsConnected = false };
        var controller = CreateController(mqtt);

        var result = await controller.Publish(new PublishRequest { Topic = "hermod/test", Payload = new { v = 1 } });

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(503, status.StatusCode);
    }
}

// Test-only fake: captures published messages for later inspection.
internal sealed class FakeMqttService : IMqttService
{
    public List<(string Topic, string Payload, bool Retain, int Qos)> Published { get; } = new();
    public bool IsConnected { get; set; } = true;

    // Events are part of the IMqttService contract but the tests only
    // exercise publish paths, so they remain intentionally unraised.
#pragma warning disable CS0067
    public event EventHandler<MqttMessage>? MessageReceived;
    public event EventHandler<bool>? ConnectionStateChanged;
#pragma warning restore CS0067

    public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task SubscribeAsync(string topic, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task UnsubscribeAsync(string topic, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task PublishAsync(string topic, string payload, bool retain = false, int qos = 0, CancellationToken cancellationToken = default)
    {
        Published.Add((topic, payload, retain, qos));
        return Task.CompletedTask;
    }

    public IReadOnlyList<MqttMessage> GetMessageHistory() => Array.Empty<MqttMessage>();
}
