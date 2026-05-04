using System.Collections.Concurrent;

namespace Hermod.Core.Models;

/// <summary>Registered device: inventory, liveness, most-recent state, and capability map.</summary>
public class Device
{
    /// <summary>Device primary key; stable across renames.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>User-facing label; defaults to <see cref="Id"/> until a rename.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Transport protocol this device speaks.</summary>
    public Protocol Protocol { get; set; }

    /// <summary>Most recently observed liveness.</summary>
    public DeviceStatus Status { get; set; }

    /// <summary>Manufacturer string from protocol-level discovery, when known.</summary>
    public string? Manufacturer { get; set; }

    /// <summary>Model identifier from protocol-level discovery, when known.</summary>
    public string? Model { get; set; }

    /// <summary>Firmware version string from protocol-level discovery, when known.</summary>
    public string? FirmwareVersion { get; set; }

    /// <summary>Capability map populated during protocol-level discovery (exposes/endpoints).</summary>
    /// <remarks>
    /// Backed by ConcurrentDictionary because MessageProcessor mutates via the
    /// indexer on the MQTT hot path while Blazor renders enumerate these maps;
    /// a plain Dictionary produced intermittent "Collection was modified" on
    /// the live path. The IDictionary surface keeps seed code that assigns
    /// plain Dictionaries compiling; hydration from Postgres wraps the
    /// deserialized dict in a ConcurrentDictionary so live instances are safe.
    /// </remarks>
    public IDictionary<string, object> Capabilities { get; set; } = new ConcurrentDictionary<string, object>();

    /// <summary>Most recent telemetry state, merged key-by-key as new messages arrive.</summary>
    public IDictionary<string, object> State { get; set; } = new ConcurrentDictionary<string, object>();

    /// <summary>Last-seen timestamp (UTC); bumped on every inbound message.</summary>
    public DateTime LastSeen { get; set; }

    /// <summary>Registration timestamp.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>Last mutation timestamp.</summary>
    public DateTime UpdatedAt { get; set; }

    /// <summary>Canonical MQTT topic root for this device (<c>{protocol}/{id}</c>).</summary>
    public string TopicBase => $"{Protocol.ToTopicPrefix()}/{Id}";
}

/// <summary>Device liveness classifications surfaced on <see cref="Device.Status"/>.</summary>
public enum DeviceStatus
{
    /// <summary>Never observed or status indeterminate.</summary>
    Unknown = 0,

    /// <summary>Last telemetry/availability signal indicated the device is reachable.</summary>
    Online = 1,

    /// <summary>Last telemetry/availability signal indicated the device is unreachable.</summary>
    Offline = 2,

    /// <summary>Device is in the middle of pairing/joining the network.</summary>
    Pairing = 3,

    /// <summary>Protocol-level error state (e.g. interview failed, firmware faulted).</summary>
    Error = 4
}
