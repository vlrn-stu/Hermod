using Hermod.Core.Interfaces;
using Hermod.Core.Models;
using Hermod.Rules;
using Xunit;

namespace Hermod.UnitTests;

public class FallbackProtocolHandlerTests
{
    [Fact]
    public void CanHandle_AnyTopic_ReturnsTrue()
    {
        var sut = new FallbackProtocolHandler();
        Assert.True(sut.CanHandle("zigbee2mqtt/whatever"));
        Assert.True(sut.CanHandle("completely/unknown/topic"));
    }

    [Fact]
    public async Task ProcessMessage_JsonPayload_ParsesFields()
    {
        var sut = new FallbackProtocolHandler();
        var message = new MqttMessage
        {
            Topic = "lora/sensor-01",
            Payload = "{\"temperature\":22.5,\"humidity\":60}"
        };

        var result = await sut.ProcessMessageAsync(message);

        Assert.NotNull(result);
        Assert.NotNull(result.ParsedPayload);
        Assert.Equal("sensor-01", result.DeviceName);
    }

    [Fact]
    public async Task ProcessMessage_NonJsonPayload_WrapsAsValue()
    {
        var sut = new FallbackProtocolHandler();
        var message = new MqttMessage
        {
            Topic = "device/status",
            Payload = "online"
        };

        var result = await sut.ProcessMessageAsync(message);

        Assert.NotNull(result);
        Assert.NotNull(result.ParsedPayload);
        Assert.Equal("online", result.ParsedPayload["value"]);
        Assert.Equal(true, result.ParsedPayload["raw"]);
    }

    [Fact]
    public async Task ProcessMessage_CommandSuffix_TagsAsCommand()
    {
        var sut = new FallbackProtocolHandler();
        var message = new MqttMessage
        {
            Topic = "zigbee2mqtt/lamp/set",
            Payload = "{\"state\":\"ON\"}"
        };

        var result = await sut.ProcessMessageAsync(message);

        Assert.Equal(MessageType.Command, result!.Type);
    }

    [Fact]
    public async Task ProcessMessage_BridgeTopic_DoesNotTriggerRules()
    {
        var sut = new FallbackProtocolHandler();
        var message = new MqttMessage
        {
            Topic = "zigbee2mqtt/bridge/state",
            Payload = "online"
        };

        var result = await sut.ProcessMessageAsync(message);

        Assert.False(result!.ShouldTriggerRules);
    }

    [Theory]
    [InlineData("zigbee2mqtt/sensor-01/state", "sensor-01")]
    [InlineData("lora/device-42", "device-42")]
    [InlineData("home/kitchen/lamp/state", "lamp")]
    public async Task ProcessMessage_ExtractsDeviceNameFromCommonPatterns(string topic, string expected)
    {
        var sut = new FallbackProtocolHandler();
        var message = new MqttMessage { Topic = topic, Payload = "{}" };

        var result = await sut.ProcessMessageAsync(message);

        Assert.Equal(expected, result!.DeviceName);
    }

    [Fact]
    public async Task ProcessMessage_JsonArrayPayload_ParsesAsTypedListUnderValueKey()
    {
        // A JSON array payload must parse as a typed list under `value`
        // with `raw = false`, not fall into the raw-wrapper catch block
        // (which would lie about the payload being unparseable just
        // because `Dictionary<string, object>` cannot hold a top-level
        // array).
        var sut = new FallbackProtocolHandler();
        var message = new MqttMessage { Topic = "lora/sensor-01", Payload = "[1,2,3]" };

        var result = await sut.ProcessMessageAsync(message);

        Assert.NotNull(result);
        Assert.NotNull(result.ParsedPayload);
        Assert.Contains("value", result.ParsedPayload);
        Assert.Contains("raw", result.ParsedPayload);
        Assert.Equal(false, result.ParsedPayload["raw"]);

        // The `value` field should be a typed list, not the raw string.
        var value = result.ParsedPayload["value"];
        Assert.IsType<List<object>>(value);
        var list = (List<object>)value;
        Assert.Equal(3, list.Count);
    }

    [Fact]
    public async Task ProcessMessage_EmptyJsonArrayPayload_ParsesAsEmptyList()
    {
        var sut = new FallbackProtocolHandler();
        var message = new MqttMessage { Topic = "lora/sensor-01", Payload = "[]" };

        var result = await sut.ProcessMessageAsync(message);

        Assert.NotNull(result);
        Assert.NotNull(result.ParsedPayload);
        Assert.Equal(false, result.ParsedPayload["raw"]);
        var list = Assert.IsType<List<object>>(result.ParsedPayload["value"]);
        Assert.Empty(list);
    }

    [Fact]
    public async Task ProcessMessage_MalformedJsonArrayPayload_FallsBackToRawWrapper()
    {
        // Regression guard: a payload that LOOKS like an array but
        // fails to parse (trailing comma, unterminated bracket) must
        // NOT crash and must produce the raw-wrapper shape so the
        // rules engine still sees SOMETHING. raw=true signals the
        // failure so rule authors can tell the difference.
        var sut = new FallbackProtocolHandler();
        var message = new MqttMessage { Topic = "lora/sensor-01", Payload = "[1,2,3" };

        var result = await sut.ProcessMessageAsync(message);

        Assert.NotNull(result);
        Assert.NotNull(result.ParsedPayload);
        Assert.Equal(true, result.ParsedPayload["raw"]);
        Assert.Equal("[1,2,3", result.ParsedPayload["value"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ProcessMessage_EmptyOrWhitespacePayload_DoesNotWrap(string payload)
    {
        // The recently-fixed non-JSON wrap branch is only supposed to fire
        // when there is actual content. Empty/whitespace payloads should
        // leave ParsedPayload null rather than synthesising a raw wrapper.
        var sut = new FallbackProtocolHandler();
        var message = new MqttMessage { Topic = "lora/sensor-01", Payload = payload };

        var result = await sut.ProcessMessageAsync(message);

        Assert.NotNull(result);
        Assert.Null(result.ParsedPayload);
    }

    [Fact]
    public async Task ProcessMessage_JsonPayload_FieldsReadable()
    {
        // Stronger version of ParsesFields: actually check that a known
        // field round-trips into ParsedPayload.
        var sut = new FallbackProtocolHandler();
        var message = new MqttMessage
        {
            Topic = "lora/sensor-01",
            Payload = "{\"temperature\":22.5}"
        };

        var result = await sut.ProcessMessageAsync(message);

        Assert.NotNull(result);
        Assert.NotNull(result.ParsedPayload);
        Assert.Contains("temperature", result.ParsedPayload);
    }

    [Fact]
    public async Task ProcessMessage_CommandTopic_StillTriggersRules()
    {
        var sut = new FallbackProtocolHandler();
        var message = new MqttMessage
        {
            Topic = "zigbee2mqtt/lamp/set",
            Payload = "{\"state\":\"ON\"}"
        };

        var result = await sut.ProcessMessageAsync(message);

        Assert.Equal(MessageType.Command, result!.Type);
        Assert.True(result.ShouldTriggerRules);
    }
}
