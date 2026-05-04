using Hermod.Core.Models;
using Xunit;

namespace Hermod.UnitTests;

/// <summary>
/// Tests for <see cref="MqttMessage"/> topic-parsing surface:
/// <see cref="MqttMessage.DeviceId"/>, <see cref="MqttMessage.IsBridgeMessage"/>,
/// and <see cref="MqttMessage.SourceProtocol"/>. A fourth property
/// `ActionType` previously existed but was removed after grep confirmed
/// zero production readers; it and its tests were deleted.
///
/// The codebase carries three separate notions of "system topic":
/// the `MessageProcessor.IsSystemTopic` prefix check (tested in
/// `MqttTopicsSettingsTests.cs`), the `MqttMessage.SystemTopicSegments`
/// path-segment check (tested here), and the `HermodSettings.System`
/// display string. This file pins the SECOND one.
/// </summary>
public class MqttMessageTests
{
    [Fact]
    public void DeviceId_PlainDeviceTopic_ExtractsSecondSegment()
    {
        var sut = new MqttMessage { Topic = "zigbee2mqtt/aqara_motion_lr" };
        Assert.Equal("aqara_motion_lr", sut.DeviceId);
        Assert.False(sut.IsBridgeMessage);
    }

    [Fact]
    public void DeviceId_DeviceWithEndpoint_StillExtractsDeviceSegment()
    {
        // parts[1] is the device; parts[2..] is the endpoint path.
        var sut = new MqttMessage { Topic = "zigbee2mqtt/aqara_motion_lr/state" };
        Assert.Equal("aqara_motion_lr", sut.DeviceId);
        Assert.False(sut.IsBridgeMessage);
    }

    [Fact]
    public void DeviceId_BridgeTopicSegment_ReturnsNull()
    {
        // `zigbee2mqtt/bridge/devices` must not be treated as a device
        // because `bridge` is a reserved SystemTopicSegment.
        var sut = new MqttMessage { Topic = "zigbee2mqtt/bridge/devices" };
        Assert.Null(sut.DeviceId);
        Assert.True(sut.IsBridgeMessage);
    }

    [Fact]
    public void DeviceId_NestedBridgeResponse_AlsoReturnsNull()
    {
        var sut = new MqttMessage { Topic = "zigbee2mqtt/bridge/response/device/configure" };
        Assert.Null(sut.DeviceId);
        Assert.True(sut.IsBridgeMessage);
    }

    [Fact]
    public void DeviceId_MockControlSegment_ReturnsNull()
    {
        // `lora/mock/inject` is the LoRa2MQTT mock adapter's control channel.
        var sut = new MqttMessage { Topic = "lora/mock/inject" };
        Assert.Null(sut.DeviceId);
        Assert.True(sut.IsBridgeMessage);
    }

    [Fact]
    public void DeviceId_DollarSysBrokerTopic_ReturnsNull()
    {
        // Any `$SYS/...` topic is an MQTT system topic per spec.
        var sut = new MqttMessage { Topic = "$SYS/broker/clients" };
        Assert.Null(sut.DeviceId);
        Assert.True(sut.IsBridgeMessage);
    }

    [Fact]
    public void DeviceId_DollarSysBrokersConnected_ReturnsNull()
    {
        // The connect/disconnect topics observed by the coordinator
        // live under $SYS/brokers/*.
        var sut = new MqttMessage { Topic = "$SYS/brokers/connected" };
        Assert.Null(sut.DeviceId);
        Assert.True(sut.IsBridgeMessage);
    }

    [Fact]
    public void DeviceId_SinglePrefixOnly_ReturnsNull()
    {
        // Degenerate input: only the prefix, no second segment.
        var sut = new MqttMessage { Topic = "lora" };
        Assert.Null(sut.DeviceId);
    }

    [Fact]
    public void DeviceId_EmptyTopic_ReturnsNull()
    {
        var sut = new MqttMessage { Topic = string.Empty };
        Assert.Null(sut.DeviceId);
    }

    [Fact]
    public void IsBridgeMessage_StatsSegment_ReturnsTrue()
    {
        // Any path where parts[1] hits the reserved segments set is a
        // bridge/system message. `stats` is one of them.
        var sut = new MqttMessage { Topic = "whatever/stats/by_protocol" };
        Assert.True(sut.IsBridgeMessage);
        Assert.Null(sut.DeviceId);
    }

    [Fact]
    public void IsBridgeMessage_ConfigSegment_ReturnsTrue()
    {
        var sut = new MqttMessage { Topic = "anything/config/x" };
        Assert.True(sut.IsBridgeMessage);
    }

    [Fact]
    public void IsBridgeMessage_RequestResponseSegments_ReturnTrue()
    {
        Assert.True(new MqttMessage { Topic = "x/request/y" }.IsBridgeMessage);
        Assert.True(new MqttMessage { Topic = "x/response/y" }.IsBridgeMessage);
    }

    [Fact]
    public void DeviceId_NestedZ2mFriendlyName_ReturnsJoinedPath()
    {
        // A Zigbee2MQTT device with a grouping prefix is published at
        // `zigbee2mqtt/kitchen/light` (retained state). DeviceId must
        // return the full nested name `kitchen/light`, NOT just the
        // first segment `kitchen`. A previous implementation returned
        // just `kitchen` and the rules engine dropped or misrouted
        // messages for every Z2M device with a grouping prefix.
        var sut = new MqttMessage { Topic = "zigbee2mqtt/kitchen/light" };
        Assert.Equal("kitchen/light", sut.DeviceId);
    }

    [Fact]
    public void DeviceId_NestedZ2mFriendlyNameWithSet_StripsActionSuffix()
    {
        var sut = new MqttMessage { Topic = "zigbee2mqtt/kitchen/light/set" };
        Assert.Equal("kitchen/light", sut.DeviceId);
    }

    [Fact]
    public void DeviceId_NestedZ2mFriendlyNameWithAvailability_StripsActionSuffix()
    {
        var sut = new MqttMessage { Topic = "zigbee2mqtt/kitchen/light/availability" };
        Assert.Equal("kitchen/light", sut.DeviceId);
    }

    [Fact]
    public void DeviceId_NestedZ2mFriendlyNameWithState_StripsStateSuffix()
    {
        // `state` is treated as an action suffix too, matching the
        // single-level behaviour: `zigbee2mqtt/aqara_motion_lr/state`
        // yields `aqara_motion_lr`. With nesting: `kitchen/light/state`
        // yields `kitchen/light`.
        var sut = new MqttMessage { Topic = "zigbee2mqtt/kitchen/light/state" };
        Assert.Equal("kitchen/light", sut.DeviceId);
    }

    [Fact]
    public void DeviceId_DeeplyNestedZ2mFriendlyName_ReturnsAllSegmentsBeforeAction()
    {
        // Three-level grouping prefix: `kitchen/island/pendant`. Still
        // terminates at the action suffix.
        var sut = new MqttMessage { Topic = "zigbee2mqtt/kitchen/island/pendant/set" };
        Assert.Equal("kitchen/island/pendant", sut.DeviceId);
    }

    [Fact]
    public void SourceProtocol_Zigbee2MqttPrefix_ReturnsZigbee()
    {
        var sut = new MqttMessage { Topic = "zigbee2mqtt/aqara_motion_lr/state" };
        Assert.Equal(Protocol.Zigbee, sut.SourceProtocol);
    }

    [Fact]
    public void SourceProtocol_LegacyZigbeePrefix_AlsoReturnsZigbee()
    {
        // Protocol.FromTopicPrefix is deliberately tolerant: accepts
        // both `zigbee2mqtt` (canonical) and `zigbee` (legacy) prefixes.
        var sut = new MqttMessage { Topic = "zigbee/sensor_01/temp" };
        Assert.Equal(Protocol.Zigbee, sut.SourceProtocol);
    }

    [Fact]
    public void SourceProtocol_BluetoothPrefix_ReturnsBluetooth()
    {
        var sut = new MqttMessage { Topic = "bluetooth/govee_therm_fridge" };
        Assert.Equal(Protocol.Bluetooth, sut.SourceProtocol);
    }

    [Fact]
    public void SourceProtocol_BleLegacyPrefix_AlsoReturnsBluetooth()
    {
        var sut = new MqttMessage { Topic = "ble/sensor_01/reading" };
        Assert.Equal(Protocol.Bluetooth, sut.SourceProtocol);
    }

    [Fact]
    public void SourceProtocol_LoraPrefix_ReturnsLora()
    {
        var sut = new MqttMessage { Topic = "lora/weather_01/payload" };
        Assert.Equal(Protocol.Lora, sut.SourceProtocol);
    }

    [Fact]
    public void SourceProtocol_WifiPrefix_ReturnsWifi()
    {
        var sut = new MqttMessage { Topic = "wifi/shelly_plug_dryer" };
        Assert.Equal(Protocol.Wifi, sut.SourceProtocol);
    }

    [Fact]
    public void SourceProtocol_UnknownPrefix_ReturnsUnknown()
    {
        var sut = new MqttMessage { Topic = "random/whatever" };
        Assert.Equal(Protocol.Unknown, sut.SourceProtocol);
    }

    [Fact]
    public void SourceProtocol_EmptyTopic_ReturnsUnknown()
    {
        var sut = new MqttMessage { Topic = string.Empty };
        Assert.Equal(Protocol.Unknown, sut.SourceProtocol);
    }

    [Fact]
    public void SourceProtocol_DollarSysTopic_ReturnsUnknown()
    {
        // `$SYS/...` does not match any protocol prefix, so the
        // SourceProtocol is Unknown even though IsBridgeMessage is true.
        var sut = new MqttMessage { Topic = "$SYS/brokers/connected" };
        Assert.Equal(Protocol.Unknown, sut.SourceProtocol);
        Assert.True(sut.IsBridgeMessage);
    }
}
