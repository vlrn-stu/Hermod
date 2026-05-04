namespace Hermod.Core.Models;

/// <summary>Transport protocol a device uses to reach the MQTT broker.</summary>
public enum Protocol
{
    /// <summary>Universal/virtual devices that don't map to a single transport.</summary>
    Unknown = 0,

    /// <summary>Zigbee (via zigbee2mqtt).</summary>
    Zigbee = 1,

    /// <summary>LoRa/LoRaWAN (via lora2mqtt).</summary>
    Lora = 2,

    /// <summary>Bluetooth / BLE (via ble2mqtt).</summary>
    Bluetooth = 3,

    /// <summary>WiFi (via wifi2mqtt or native MQTT).</summary>
    Wifi = 4
}

/// <summary>Mapping helpers between <see cref="Protocol"/> and MQTT topic prefixes.</summary>
public static class ProtocolExtensions
{
    /// <summary>
    /// UI-friendly name. <see cref="Protocol.Unknown"/> renders as "Universal"
    /// because that value represents virtual/bridged devices, not a literal
    /// unknown state.
    /// </summary>
    public static string ToDisplayName(this Protocol protocol) => protocol switch
    {
        Protocol.Zigbee => "ZigBee",
        Protocol.Lora => "LoRa",
        Protocol.Bluetooth => "Bluetooth",
        Protocol.Wifi => "WiFi",
        _ => "Universal"
    };

    /// <summary>
    /// Canonical MQTT topic prefix used to build <see cref="Device.TopicBase"/>
    /// and to route messages back to the correct protocol handler. Zigbee
    /// maps to <c>zigbee2mqtt</c> — the upstream base topic — not <c>zigbee</c>.
    /// </summary>
    public static string ToTopicPrefix(this Protocol protocol) => protocol switch
    {
        Protocol.Zigbee => "zigbee2mqtt",
        Protocol.Lora => "lora",
        Protocol.Bluetooth => "bluetooth",
        Protocol.Wifi => "wifi",
        _ => "universal"
    };

    /// <summary>
    /// Inverse of <see cref="ToTopicPrefix"/>, extended with aliases that appear
    /// in seed data or legacy topics (<c>zigbee</c>, <c>ble</c>). Unrecognized
    /// prefixes return <see cref="Protocol.Unknown"/>.
    /// </summary>
    /// <exception cref="ArgumentNullException">When <paramref name="prefix"/> is null.</exception>
    public static Protocol FromTopicPrefix(string prefix)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        return FromTopicPrefix(prefix.AsSpan());
    }

    /// <summary>Span overload of <see cref="FromTopicPrefix(string)"/> that avoids the substring allocation when callers already hold a topic span.</summary>
    public static Protocol FromTopicPrefix(ReadOnlySpan<char> prefix)
    {
        if (prefix.Equals("zigbee", StringComparison.OrdinalIgnoreCase)) return Protocol.Zigbee;
        if (prefix.Equals("zigbee2mqtt", StringComparison.OrdinalIgnoreCase)) return Protocol.Zigbee;
        if (prefix.Equals("lora", StringComparison.OrdinalIgnoreCase)) return Protocol.Lora;
        if (prefix.Equals("bluetooth", StringComparison.OrdinalIgnoreCase)) return Protocol.Bluetooth;
        if (prefix.Equals("ble", StringComparison.OrdinalIgnoreCase)) return Protocol.Bluetooth;
        if (prefix.Equals("wifi", StringComparison.OrdinalIgnoreCase)) return Protocol.Wifi;
        return Protocol.Unknown;
    }
}
