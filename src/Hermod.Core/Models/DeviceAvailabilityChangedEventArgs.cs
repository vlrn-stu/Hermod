namespace Hermod.Core.Models;

/// <summary>
/// Raised when a device transitions between <see cref="DeviceStatus"/> values
/// (typically Online/Offline). Consumers can use this to dispatch rules
/// whose trigger is <c>TriggerType.OnAvailability</c>.
/// </summary>
public sealed class DeviceAvailabilityChangedEventArgs : EventArgs
{
    /// <summary>Id of the device whose status changed.</summary>
    public required string DeviceId { get; init; }

    /// <summary>Status before the transition.</summary>
    public required DeviceStatus PreviousStatus { get; init; }

    /// <summary>Status after the transition.</summary>
    public required DeviceStatus CurrentStatus { get; init; }

    /// <summary>Full device snapshot at the time of transition, when available.</summary>
    public Device? Device { get; init; }

    /// <summary>Transition timestamp (UTC).</summary>
    public DateTime ChangedAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Canonical MQTT-style topic used for topic-pattern matching against
    /// OnAvailability rule triggers. Example: <c>availability/lora/sensor-1</c>.
    /// </summary>
    public string Topic { get; init; } = string.Empty;
}
