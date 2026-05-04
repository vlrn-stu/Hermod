using Hermod.Core.Mqtt;

namespace Hermod.Core.Models;

/// <summary>
/// Normalized inbound MQTT message. Immutable by design: <see cref="Interfaces.IMqttService"/>
/// implementations retain snapshots in a history buffer and hand them out to
/// consumers, so mutating <see cref="Topic"/>/<see cref="Payload"/> post-hoc
/// would corrupt shared state.
/// </summary>
public class MqttMessage
{
    /// <summary>Source MQTT topic the message arrived on.</summary>
    public string Topic { get; init; } = string.Empty;

    /// <summary>Raw payload bytes as UTF-8 text.</summary>
    public string Payload { get; init; } = string.Empty;

    /// <summary>Coordinator-side arrival timestamp (UTC).</summary>
    public DateTime ReceivedAt { get; init; } = DateTime.UtcNow;

    /// <summary>True when the broker delivered this message as a retained fetch.</summary>
    public bool Retained { get; init; }

    /// <summary>MQTT QoS level the message was delivered at (0, 1, or 2).</summary>
    public int QoS { get; init; }

    /// <summary>Protocol inferred from the topic's leading segment.</summary>
    public Protocol SourceProtocol
    {
        get
        {
            var prefix = Topic.Split('/').FirstOrDefault() ?? string.Empty;
            return ProtocolExtensions.FromTopicPrefix(prefix);
        }
    }

    /// <summary>Device id extracted from <see cref="Topic"/>. See <see cref="DeviceTopicParser"/> for the full algorithm.</summary>
    public string? DeviceId => DeviceTopicParser.Parse(Topic);

    /// <summary>True for bridge/system/$SYS topics that should bypass device + rules processing.</summary>
    public bool IsBridgeMessage => DeviceTopicParser.IsSystemOrBridge(Topic);

    /// <summary>
    /// Opaque per-message trace id extracted from the payload's <c>_uuid</c>
    /// field when present. Null when the producer didn't stamp one (normal
    /// production traffic). Set once at ingest via
    /// <see cref="Mqtt.PayloadUuidExtractor"/>; the W-measurement CSV
    /// correlates broker_rx / rule_eval_done / action_publish by this id.
    /// </summary>
    public string? TraceUuid { get; init; }
}
