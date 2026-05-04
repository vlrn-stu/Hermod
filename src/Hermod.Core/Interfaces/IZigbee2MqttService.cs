using Hermod.Core.Models;

namespace Hermod.Core.Interfaces;

/// <summary>
/// Facade over the Zigbee2MQTT bridge API (topics under <c>zigbee/</c>).
/// Exposes the bridge's own state, parsed device/group inventories, raised
/// events for state/availability/bridge transitions, and typed request
/// methods for every bridge command the coordinator cares about.
/// </summary>
public interface IZigbee2MqttService
{
    #region Bridge State

    /// <summary>True when the last-observed bridge state was <c>online</c>.</summary>
    bool IsBridgeOnline { get; }

    /// <summary>Most recent bridge info payload, or null if none has been received.</summary>
    Zigbee2MqttBridgeInfo? BridgeInfo { get; }

    /// <summary>Snapshot of known devices as reported by the bridge.</summary>
    IReadOnlyList<Zigbee2MqttDevice> Devices { get; }

    /// <summary>Snapshot of known groups as reported by the bridge.</summary>
    IReadOnlyList<Zigbee2MqttGroup> Groups { get; }

    /// <summary>Fires on every bridge-state transition (online/offline).</summary>
    event EventHandler<Zigbee2MqttBridgeState>? BridgeStateChanged;

    /// <summary>Fires when the bridge re-publishes its info payload.</summary>
    event EventHandler<Zigbee2MqttBridgeInfo>? BridgeInfoUpdated;

    /// <summary>Fires when the bridge re-publishes the full device inventory.</summary>
    event EventHandler<IReadOnlyList<Zigbee2MqttDevice>>? DevicesUpdated;

    /// <summary>Fires when the bridge re-publishes the full group inventory.</summary>
    event EventHandler<IReadOnlyList<Zigbee2MqttGroup>>? GroupsUpdated;

    #endregion

    #region Device Events

    /// <summary>Fires when a device publishes a state update.</summary>
    event EventHandler<Zigbee2MqttDeviceStateEvent>? DeviceStateUpdated;

    /// <summary>Fires when a device transitions between online and offline.</summary>
    event EventHandler<Zigbee2MqttDeviceAvailabilityEvent>? DeviceAvailabilityChanged;

    /// <summary>Fires on bridge-scope events: device joined, left, interview complete, pairing failure.</summary>
    event EventHandler<Zigbee2MqttBridgeEvent>? BridgeEventReceived;

    /// <summary>Fires on every bridge log line (debug/info/warning/error).</summary>
    event EventHandler<Zigbee2MqttLogMessage>? LogMessageReceived;

    #endregion

    #region Device Control

    /// <summary>Publishes <paramref name="state"/> to <c>{device}/set</c>. <paramref name="state"/> is serialized as JSON.</summary>
    Task SetDeviceStateAsync(string deviceName, object state, CancellationToken cancellationToken = default);

    /// <summary>Sets a single property on the device (e.g. <c>state</c>, <c>brightness</c>).</summary>
    Task SetDevicePropertyAsync(string deviceName, string property, object value, CancellationToken cancellationToken = default);

    /// <summary>Turns a device's <c>state</c> property on or off.</summary>
    /// <param name="deviceName">Target device friendly name.</param>
    /// <param name="on">True for ON, false for OFF.</param>
    /// <param name="cancellationToken">Request-scoped cancellation.</param>
    Task SetDevicePowerAsync(string deviceName, bool on, CancellationToken cancellationToken = default);

    /// <summary>Sets light brightness on the Zigbee 0-254 scale (not 0-255).</summary>
    Task SetLightBrightnessAsync(string deviceName, int brightness, CancellationToken cancellationToken = default);

    /// <summary>Sets colour temperature in mireds.</summary>
    Task SetLightColorTempAsync(string deviceName, int colorTemp, CancellationToken cancellationToken = default);

    /// <summary>Sets colour using CIE 1931 xy coordinates (each in [0,1]).</summary>
    Task SetLightColorAsync(string deviceName, double x, double y, CancellationToken cancellationToken = default);

    /// <summary>Requests the bridge to republish the device's state. The reply is delivered via <see cref="DeviceStateUpdated"/>.</summary>
    Task GetDeviceStateAsync(string deviceName, string? property = null, CancellationToken cancellationToken = default);

    #endregion

    #region Bridge Requests

    /// <summary>Enables permit-join for <paramref name="timeSeconds"/> seconds. If <paramref name="device"/> is set, joining is gated through that router.</summary>
    Task<Zigbee2MqttResponse<Zigbee2MqttPermitJoinResponse>?> PermitJoinAsync(int timeSeconds, string? device = null, CancellationToken cancellationToken = default);

    /// <summary>Asks the bridge to run its self-check and reply on the response topic.</summary>
    Task<Zigbee2MqttResponse<Zigbee2MqttHealthCheckResponse>?> HealthCheckAsync(CancellationToken cancellationToken = default);

    /// <summary>Requests a network map from the coordinator. <paramref name="type"/> accepts <c>"graphviz"</c>, <c>"plantuml"</c>, or <c>"raw"</c>.</summary>
    Task<Zigbee2MqttResponse<Zigbee2MqttNetworkMapResponse>?> GetNetworkMapAsync(string type = "graphviz", bool routes = false, CancellationToken cancellationToken = default);

    /// <summary>Asks the bridge to restart. Bridge state will go offline/online around this call.</summary>
    Task RestartBridgeAsync(CancellationToken cancellationToken = default);

    #endregion

    #region Device Management

    /// <summary>Renames a device on the bridge. <paramref name="homeAssistantRename"/> also updates the Home Assistant entity id if HA integration is enabled.</summary>
    Task<bool> RenameDeviceAsync(string currentName, string newName, bool homeAssistantRename = false, CancellationToken cancellationToken = default);

    /// <summary>Unpairs and forgets a device. <paramref name="force"/>=true skips the graceful leave; <paramref name="block"/>=true adds it to the deny-list.</summary>
    Task<bool> RemoveDeviceAsync(string deviceName, bool force = false, bool block = false, CancellationToken cancellationToken = default);

    /// <summary>Re-runs the device configuration routine (binding + reporting). Useful after a bridge restart leaves a device misconfigured.</summary>
    Task ConfigureDeviceAsync(string deviceName, CancellationToken cancellationToken = default);

    /// <summary>Re-runs the device interview (capability discovery).</summary>
    Task InterviewDeviceAsync(string deviceName, CancellationToken cancellationToken = default);

    /// <summary>Writes bridge-level options for a single device (e.g. <c>friendly_name</c>, reporting cadences).</summary>
    Task SetDeviceOptionsAsync(string deviceName, Dictionary<string, object> options, CancellationToken cancellationToken = default);

    #endregion

    #region Group Management

    /// <summary>Creates a new Zigbee group. <paramref name="id"/> optionally pins the group id; null lets the bridge allocate.</summary>
    Task<bool> CreateGroupAsync(string friendlyName, int? id = null, CancellationToken cancellationToken = default);

    /// <summary>Removes a group. <paramref name="force"/>=true deletes even if the group contains devices.</summary>
    Task<bool> RemoveGroupAsync(string groupName, bool force = false, CancellationToken cancellationToken = default);

    /// <summary>Renames a group.</summary>
    Task<bool> RenameGroupAsync(string currentName, string newName, CancellationToken cancellationToken = default);

    /// <summary>Adds a device to a group. <paramref name="endpoint"/> selects a specific device endpoint; null = primary.</summary>
    Task AddDeviceToGroupAsync(string groupName, string deviceName, int? endpoint = null, CancellationToken cancellationToken = default);

    /// <summary>Removes a device from a group. <paramref name="endpoint"/> selects a specific device endpoint; null = primary.</summary>
    Task RemoveDeviceFromGroupAsync(string groupName, string deviceName, int? endpoint = null, CancellationToken cancellationToken = default);

    /// <summary>Broadcasts <paramref name="state"/> to every device in the group.</summary>
    Task SetGroupStateAsync(string groupName, object state, CancellationToken cancellationToken = default);

    #endregion

    #region Message Processing

    /// <summary>Feeds an inbound <c>zigbee/*</c> message into the service. Dispatches to the appropriate event(s).</summary>
    void ProcessMessage(MqttMessage message);

    /// <summary>Returns the tracked device with matching friendly name, or null if unknown.</summary>
    Zigbee2MqttDevice? GetDevice(string friendlyName);

    /// <summary>Lookup by IEEE 802.15.4 EUI-64 address (the device's permanent identifier).</summary>
    Zigbee2MqttDevice? GetDeviceByIeee(string ieeeAddress);

    /// <summary>Last observed state dict for <paramref name="friendlyName"/>, or null if never seen.</summary>
    Dictionary<string, object>? GetDeviceState(string friendlyName);

    #endregion
}

/// <summary>Payload for <see cref="IZigbee2MqttService.DeviceStateUpdated"/>.</summary>
public class Zigbee2MqttDeviceStateEvent
{
    /// <summary>Friendly name of the device that emitted the state update.</summary>
    public string DeviceName { get; init; } = string.Empty;

    /// <summary>Source MQTT topic the state was published on.</summary>
    public string Topic { get; init; } = string.Empty;

    /// <summary>Parsed state payload.</summary>
    public Dictionary<string, object> State { get; init; } = new();

    /// <summary>Arrival timestamp (UTC).</summary>
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

/// <summary>Payload for <see cref="IZigbee2MqttService.DeviceAvailabilityChanged"/>.</summary>
public class Zigbee2MqttDeviceAvailabilityEvent
{
    /// <summary>Friendly name of the device whose availability changed.</summary>
    public string DeviceName { get; init; } = string.Empty;

    /// <summary>True when the device is now online.</summary>
    public bool IsOnline { get; init; }

    /// <summary>Transition timestamp (UTC).</summary>
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}
