using System.Text.Json;
using Hermod.Core.Interfaces;
using Hermod.Core.Models;

namespace Hermod.Rules;

/// <summary>
/// Generic protocol handler that processes any MQTT message format.
/// Used when no specific protocol handler is available.
/// </summary>
public class FallbackProtocolHandler : IProtocolHandler
{
    private static readonly string[] SkipPrefixes =
        ["zigbee2mqtt", "lora", "bluetooth", "wifi", "home", "sensor", "actuator"];
    private static readonly string[] SkipSuffixes =
        ["state", "set", "get", "status", "availability", "config", "command", "response"];

    private readonly Dictionary<string, Dictionary<string, object>> _deviceStates = [];
    private readonly List<Device> _devices = [];
    private readonly object _lock = new();

    /// <inheritdoc />
    public Protocol Protocol => Protocol.Unknown;

    /// <inheritdoc />
    public string TopicPrefix => "#"; // Matches everything.

    /// <inheritdoc />
    public int Priority => int.MaxValue; // Lowest priority: only used as a last resort.

    /// <inheritdoc />
    public bool CanHandle(string topic) => true;

    /// <inheritdoc />
    public Task<ProcessedMessage?> ProcessMessageAsync(MqttMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        var deviceName = ExtractDeviceName(message.Topic);
        var messageType = DetermineMessageType(message.Topic);
        Dictionary<string, object>? parsedPayload = null;

        // Objects => dict; arrays => {value:[...], raw:false} for
        // source.value[i] access; anything else => {value:<raw>, raw:true}.
        var trimmed = message.Payload?.TrimStart() ?? string.Empty;
        var looksLikeJsonObject = trimmed.Length > 0 && trimmed[0] == '{';
        var looksLikeJsonArray = trimmed.Length > 0 && trimmed[0] == '[';

        if (looksLikeJsonObject)
        {
            try
            {
                parsedPayload = JsonSerializer.Deserialize<Dictionary<string, object>>(message.Payload!);
            }
            catch (JsonException)
            {
                parsedPayload = null;
            }
        }
        else if (looksLikeJsonArray)
        {
            try
            {
                var array = JsonSerializer.Deserialize<List<object>>(message.Payload!);
                if (array is not null)
                {
                    parsedPayload = new Dictionary<string, object>
                    {
                        ["value"] = array,
                        ["raw"] = false,
                    };
                }
            }
            catch (JsonException)
            {
                parsedPayload = null;
            }
        }

        if (parsedPayload is null && !string.IsNullOrWhiteSpace(message.Payload))
        {
            parsedPayload = new Dictionary<string, object>
            {
                ["value"] = message.Payload,
                ["raw"] = true,
            };
        }

        if (!string.IsNullOrEmpty(deviceName) && parsedPayload != null && messageType == MessageType.State)
        {
            UpdateDeviceState(deviceName, parsedPayload, message.SourceProtocol);
        }

        var processed = new ProcessedMessage
        {
            OriginalMessage = message,
            DeviceName = deviceName,
            ParsedPayload = parsedPayload,
            Type = messageType,
            ShouldTriggerRules = ShouldTriggerRules(message.Topic, messageType),
            Device = GetDevice(deviceName),
            Metadata = new Dictionary<string, object>
            {
                ["handler"] = "fallback",
                ["originalTopic"] = message.Topic,
            },
        };

        return Task.FromResult<ProcessedMessage?>(processed);
    }

    /// <inheritdoc />
    public IReadOnlyList<Device> GetDevices()
    {
        lock (_lock)
        {
            return _devices.ToList();
        }
    }

    /// <inheritdoc />
    public Device? GetDevice(string? deviceId)
    {
        if (string.IsNullOrEmpty(deviceId))
            return null;

        lock (_lock)
        {
            return _devices.FirstOrDefault(d =>
                d.Id.Equals(deviceId, StringComparison.OrdinalIgnoreCase) ||
                d.Name.Equals(deviceId, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <inheritdoc />
    public Dictionary<string, object>? GetDeviceState(string deviceId)
    {
        ArgumentNullException.ThrowIfNull(deviceId);

        lock (_lock)
        {
            return _deviceStates.TryGetValue(deviceId.ToUpperInvariant(), out var state)
                ? new Dictionary<string, object>(state)
                : null;
        }
    }

    private void UpdateDeviceState(string deviceName, Dictionary<string, object> state, Protocol protocol)
    {
        lock (_lock)
        {
            var key = deviceName.ToUpperInvariant();

            if (_deviceStates.TryGetValue(key, out var existing))
            {
                foreach (var kvp in state)
                {
                    existing[kvp.Key] = kvp.Value;
                }
            }
            else
            {
                _deviceStates[key] = new Dictionary<string, object>(state);
            }

            var device = _devices.FirstOrDefault(d => d.Id.Equals(deviceName, StringComparison.OrdinalIgnoreCase));
            if (device is null)
            {
                device = new Device
                {
                    Id = deviceName,
                    Name = deviceName,
                    Protocol = protocol,
                    Status = DeviceStatus.Online,
                    CreatedAt = DateTime.UtcNow,
                };
                _devices.Add(device);
            }

            device.LastSeen = DateTime.UtcNow;
            device.Status = DeviceStatus.Online;

            foreach (var kvp in state)
            {
                device.State[kvp.Key] = kvp.Value;
            }
        }
    }

    /// <summary>
    /// Extract device name from topic. Handles common patterns like:
    /// <c>protocol/device/status</c>, <c>device/state</c>, and
    /// <c>home/room/device/state</c>.
    /// </summary>
    private static string? ExtractDeviceName(string topic)
    {
        var parts = topic.Split('/');

        if (parts.Length == 0)
            return null;

        if (parts.Length == 1)
            return parts[0];

        var filtered = parts
            .Where(p => !SkipPrefixes.Contains(p, StringComparer.OrdinalIgnoreCase))
            .Where(p => !SkipSuffixes.Contains(p, StringComparer.OrdinalIgnoreCase))
            .Where(p => !string.IsNullOrEmpty(p))
            .ToList();

        if (filtered.Count == 0)
        {
            // Fall back to second-to-last segment if available.
            return parts.Length >= 2 ? parts[^2] : parts[0];
        }

        return filtered.LastOrDefault() ?? parts[0];
    }

    /// <summary>Determine message type from topic patterns.</summary>
    private static MessageType DetermineMessageType(string topic)
    {
        if (topic.EndsWith("/set", StringComparison.OrdinalIgnoreCase) ||
            topic.Contains("/command", StringComparison.OrdinalIgnoreCase))
            return MessageType.Command;

        if (topic.EndsWith("/get", StringComparison.OrdinalIgnoreCase) ||
            topic.Contains("/query", StringComparison.OrdinalIgnoreCase))
            return MessageType.Query;

        if (topic.Contains("/availability", StringComparison.OrdinalIgnoreCase) ||
            topic.Contains("/status", StringComparison.OrdinalIgnoreCase))
            return MessageType.Availability;

        if (topic.Contains("/bridge", StringComparison.OrdinalIgnoreCase) ||
            topic.Contains("/system", StringComparison.OrdinalIgnoreCase))
            return MessageType.System;

        if (topic.Contains("/event", StringComparison.OrdinalIgnoreCase) ||
            topic.Contains("/action", StringComparison.OrdinalIgnoreCase))
            return MessageType.Event;

        return MessageType.State;
    }

    /// <summary>Determine if a message should trigger rules.</summary>
    private static bool ShouldTriggerRules(string topic, MessageType type)
    {
        if (type is MessageType.System or MessageType.Query)
            return false;

        if (topic.Contains("/bridge/", StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }
}
