namespace Hermod.Core.Mqtt;

/// <summary>
/// Extracts a device identifier from an MQTT topic that follows the
/// canonical <c>{protocol}/{device_id}/{endpoint}</c> schema. Walks forward
/// from the second segment, collecting path parts until an action suffix
/// terminates the walk; so <c>zigbee/kitchen/light/set</c> yields
/// <c>kitchen/light</c>. Returns null for $SYS topics, bridge/control
/// messages, and topics with fewer than two segments.
///
/// For unstructured topics that don't match the schema (e.g. home-automation
/// hub layouts like <c>home/room/device/state</c>), a best-effort fuzzy
/// parser lives in <c>FallbackProtocolHandler</c>; that is intentionally
/// lossier and should not be used for routing.
/// </summary>
public static class DeviceTopicParser
{
    /// <summary>Second-hop segments that indicate bridge/system traffic, never a device id.</summary>
    public static readonly IReadOnlySet<string> SystemTopicSegments = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "bridge",   // zigbee/bridge/*, lora/bridge/*
        "mock",     // lora/mock/*, zigbee/mock/*
        "brokers",  // $SYS/brokers/*
        "clients",  // $SYS/clients/*
        "stats",    // $SYS/stats/*
        "metrics",
        "status",
        "system",
        "config",
        "request",
        "response"
    };

    /// <summary>Suffixes that terminate the device-name walk.</summary>
    public static readonly IReadOnlySet<string> ActionSuffixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "set",
        "get",
        "availability",
        // Zigbee2MQTT publishes retained state at <device>/state; the walk
        // must stop here so aqara_motion_lr/state yields aqara_motion_lr.
        "state"
    };

    /// <summary>
    /// Extracts the device id from <paramref name="topic"/>, or null if the
    /// topic is a system/bridge/$SYS topic or otherwise does not identify
    /// a device.
    /// </summary>
    public static string? Parse(string? topic)
    {
        if (string.IsNullOrEmpty(topic)) return null;
        if (topic.StartsWith('$')) return null;

        var parts = topic.Split('/');
        if (parts.Length < 2) return null;

        if (SystemTopicSegments.Contains(parts[1])) return null;

        // Reject two-segment topics whose second part is itself an action
        // suffix (zigbee/state, zigbee/set, zigbee/availability). Without
        // this guard the suffix is returned as a fabricated device id.
        if (parts.Length == 2)
        {
            return ActionSuffixes.Contains(parts[1]) ? null : parts[1];
        }

        var deviceParts = new List<string>();
        for (var i = 1; i < parts.Length; i++)
        {
            if (ActionSuffixes.Contains(parts[i])) break;
            deviceParts.Add(parts[i]);
        }

        return deviceParts.Count > 0 ? string.Join("/", deviceParts) : null;
    }

    /// <summary>True if <paramref name="topic"/> is a bridge/system/$SYS topic.</summary>
    public static bool IsSystemOrBridge(string? topic)
    {
        if (string.IsNullOrEmpty(topic)) return false;
        if (topic.StartsWith('$')) return true;

        var parts = topic.Split('/');
        return parts.Length >= 2 && SystemTopicSegments.Contains(parts[1]);
    }
}
