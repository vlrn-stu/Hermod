using Hermod.Core.Models;
using Hermod.Infrastructure.Services;
using Xunit;

namespace Hermod.UnitTests;

/// <summary>
/// Pins the topic-to-protocol mapping <see cref="MessageProcessor"/> uses
/// to tick per-protocol message counters. The hot path runs once per
/// ingested message, so regressions here would either skew per-protocol
/// dashboards or silently drop counts.
/// </summary>
public class MessageProcessorTopicToProtocolTests
{
    [Theory]
    [InlineData("zigbee2mqtt/kitchen_lamp/state", Protocol.Zigbee)]
    [InlineData("zigbee/kitchen_lamp/state", Protocol.Zigbee)]  // legacy alias
    [InlineData("lora/sensor-01", Protocol.Lora)]
    [InlineData("bluetooth/tag-42", Protocol.Bluetooth)]
    [InlineData("ble/tag-42", Protocol.Bluetooth)]              // legacy alias
    [InlineData("wifi/thermostat_living", Protocol.Wifi)]
    public void KnownProtocolPrefix_Maps(string topic, Protocol expected)
        => Assert.Equal(expected, MessageProcessor.TopicToProtocol(topic));

    [Theory]
    [InlineData("hermod/debug/anything", Protocol.Unknown)]
    [InlineData("alerts/foo", Protocol.Unknown)]
    [InlineData("$SYS/brokers/0/clients", Protocol.Unknown)]
    [InlineData("unknown-prefix/device", Protocol.Unknown)]
    public void NonProtocolTopics_ReturnUnknown(string topic, Protocol expected)
        => Assert.Equal(expected, MessageProcessor.TopicToProtocol(topic));

    [Fact]
    public void CaseInsensitive_PrefixMatch()
    {
        // The upstream broker may emit mixed-case; FromTopicPrefix
        // uppercases before the switch.
        Assert.Equal(Protocol.Zigbee, MessageProcessor.TopicToProtocol("ZIGBEE2MQTT/lamp"));
        Assert.Equal(Protocol.Lora, MessageProcessor.TopicToProtocol("Lora/sensor"));
    }

    [Fact]
    public void SingleSegmentTopic_TreatedAsPrefix()
    {
        // No slash: the whole topic IS the prefix. Hit this path on a
        // degenerate retained message like `zigbee` (bridge info).
        Assert.Equal(Protocol.Zigbee, MessageProcessor.TopicToProtocol("zigbee"));
        Assert.Equal(Protocol.Unknown, MessageProcessor.TopicToProtocol("gibberish"));
    }

    [Fact]
    public void EmptyTopic_Unknown()
        => Assert.Equal(Protocol.Unknown, MessageProcessor.TopicToProtocol(""));
}
