using Hermod.Core.Models;

namespace Hermod.Core.Interfaces;

/// <summary>
/// Protocol-specific message handler. Each handler owns discovery, state
/// tracking, and decoding for one transport (Zigbee/LoRa/BLE/WiFi/etc.) and
/// turns raw <see cref="MqttMessage"/>s into normalized <see cref="ProcessedMessage"/>s
/// that the rules engine can match against.
/// </summary>
public interface IProtocolHandler
{
    /// <summary>Protocol this handler owns (one handler per <see cref="Models.Protocol"/>).</summary>
    Protocol Protocol { get; }

    /// <summary>Leading MQTT topic segment this handler claims (e.g. <c>"zigbee2mqtt"</c>, <c>"lora"</c>, <c>"bluetooth"</c>).</summary>
    string TopicPrefix { get; }

    /// <summary>Handler-selection priority when multiple could match. Lower wins.</summary>
    int Priority { get; }

    /// <summary>True if this handler should be picked for <paramref name="topic"/>.</summary>
    bool CanHandle(string topic);

    /// <summary>
    /// Parses a message for the rules engine. Returns <c>null</c> if the handler
    /// claimed the topic but decided the message should be ignored (for example,
    /// malformed or not relevant to rules).
    /// </summary>
    Task<ProcessedMessage?> ProcessMessageAsync(MqttMessage message, CancellationToken cancellationToken = default);

    /// <summary>Snapshot of every device currently tracked by this handler.</summary>
    IReadOnlyList<Device> GetDevices();

    /// <summary>Returns the tracked device with id <paramref name="deviceId"/>, or null if unknown.</summary>
    Device? GetDevice(string deviceId);

    /// <summary>Last observed state for <paramref name="deviceId"/>, or null if the handler never saw the device.</summary>
    Dictionary<string, object>? GetDeviceState(string deviceId);
}

/// <summary>Normalized form of an inbound message as produced by an <see cref="IProtocolHandler"/>.</summary>
public class ProcessedMessage
{
    /// <summary>Raw message received from the MQTT client.</summary>
    public required MqttMessage OriginalMessage { get; init; }

    /// <summary>The source device, if the handler could identify it.</summary>
    public Device? Device { get; init; }

    /// <summary>Payload parsed into a key/value shape. Null if parsing failed or the payload is opaque.</summary>
    public Dictionary<string, object>? ParsedPayload { get; init; }

    /// <summary>Protocol-specific side-channel information (LQI, RSSI, battery, etc.).</summary>
    public Dictionary<string, object>? Metadata { get; init; }

    /// <summary>Set to false for control-plane traffic (bridge status, join events) that shouldn't drive rules.</summary>
    public bool ShouldTriggerRules { get; init; } = true;

    /// <summary>Device name extracted from the topic; used as the join key into the state manager.</summary>
    public string? DeviceName { get; init; }

    /// <summary>Semantic classification of the message (state update, command, event, etc.).</summary>
    public MessageType Type { get; init; } = MessageType.State;
}

/// <summary>Semantic classification attached to a <see cref="ProcessedMessage"/>.</summary>
public enum MessageType
{
    /// <summary>Device state update.</summary>
    State,

    /// <summary>Command/set message.</summary>
    Command,

    /// <summary>Query/get message.</summary>
    Query,

    /// <summary>Availability status.</summary>
    Availability,

    /// <summary>Bridge/system message.</summary>
    System,

    /// <summary>Event (e.g., button press, motion).</summary>
    Event,

    /// <summary>Unknown/other.</summary>
    Unknown
}

/// <summary>Convenience helpers over a collection of <see cref="IProtocolHandler"/> instances.</summary>
public static class ProtocolHandlerExtensions
{
    /// <summary>Returns the lowest-priority handler whose <see cref="IProtocolHandler.CanHandle"/> accepts <paramref name="topic"/>, or null.</summary>
    public static IProtocolHandler? FindHandler(this IEnumerable<IProtocolHandler> handlers, string topic)
    {
        return handlers
            .Where(h => h.CanHandle(topic))
            .OrderBy(h => h.Priority)
            .FirstOrDefault();
    }
}
