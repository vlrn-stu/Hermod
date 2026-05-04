using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Hermod.Core.Interfaces;
using Hermod.Core.Models;
using Hermod.Core.Mqtt;
using Microsoft.Extensions.Logging;

namespace Hermod.Infrastructure.Zigbee;

/// <summary>
/// Zigbee2MQTT bridge client. Owns the bridge/device/group state inferred
/// from MQTT messages and exposes it through events and snapshot queries.
/// Thread-safe: all multi-entry map refreshes use the immutable-swap
/// pattern (build a fresh map off to the side, then <see cref="Volatile.Write{T}"/>
/// the reference in) so readers never observe a transient empty map.
/// </summary>
public sealed class Zigbee2MqttService : IZigbee2MqttService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false
    };

    private readonly IMqttService _mqtt;
    private readonly ILogger<Zigbee2MqttService> _logger;

    private Dictionary<string, Zigbee2MqttDevice> _devicesByName = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, Zigbee2MqttDevice> _devicesByIeee = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, Zigbee2MqttGroup> _groups = new(StringComparer.OrdinalIgnoreCase);

    // Snapshot refs so dashboard polls are ref-reads, not allocations.
    private IReadOnlyList<Zigbee2MqttDevice> _devicesSnapshot = Array.Empty<Zigbee2MqttDevice>();
    private IReadOnlyList<Zigbee2MqttGroup> _groupsSnapshot = Array.Empty<Zigbee2MqttGroup>();

    private readonly ConcurrentDictionary<string, Dictionary<string, object>> _deviceStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, bool> _deviceAvailability = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _pendingRequests = new();

    private volatile bool _bridgeOnline;
    private Zigbee2MqttBridgeInfo? _bridgeInfo;
    private readonly object _bridgeInfoLock = new();

    /// <summary>
    /// Creates a bridge client that will process messages published on
    /// the supplied <paramref name="mqtt"/> service.
    /// </summary>
    public Zigbee2MqttService(IMqttService mqtt, ILogger<Zigbee2MqttService> logger)
    {
        ArgumentNullException.ThrowIfNull(mqtt);
        ArgumentNullException.ThrowIfNull(logger);
        _mqtt = mqtt;
        _logger = logger;
    }

    #region Bridge State Properties

    /// <inheritdoc/>
    public bool IsBridgeOnline => _bridgeOnline;

    /// <inheritdoc/>
    public Zigbee2MqttBridgeInfo? BridgeInfo
    {
        get
        {
            lock (_bridgeInfoLock)
            {
                return _bridgeInfo;
            }
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<Zigbee2MqttDevice> Devices => Volatile.Read(ref _devicesSnapshot);

    /// <inheritdoc/>
    public IReadOnlyList<Zigbee2MqttGroup> Groups => Volatile.Read(ref _groupsSnapshot);

    #endregion

    #region Events

    /// <inheritdoc/>
    public event EventHandler<Zigbee2MqttBridgeState>? BridgeStateChanged;
    /// <inheritdoc/>
    public event EventHandler<Zigbee2MqttBridgeInfo>? BridgeInfoUpdated;
    /// <inheritdoc/>
    public event EventHandler<IReadOnlyList<Zigbee2MqttDevice>>? DevicesUpdated;
    /// <inheritdoc/>
    public event EventHandler<IReadOnlyList<Zigbee2MqttGroup>>? GroupsUpdated;
    /// <inheritdoc/>
    public event EventHandler<Zigbee2MqttDeviceStateEvent>? DeviceStateUpdated;
    /// <inheritdoc/>
    public event EventHandler<Zigbee2MqttDeviceAvailabilityEvent>? DeviceAvailabilityChanged;
    /// <inheritdoc/>
    public event EventHandler<Zigbee2MqttBridgeEvent>? BridgeEventReceived;
    /// <inheritdoc/>
    public event EventHandler<Zigbee2MqttLogMessage>? LogMessageReceived;

    #endregion

    #region Message Processing

    /// <inheritdoc/>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Per-message processing must not crash the ingest pump; any error is logged and the pump continues.")]
    public void ProcessMessage(MqttMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!message.Topic.StartsWith(Zigbee2MqttTopics.BaseTopic, StringComparison.Ordinal)) return;

        try
        {
            if (Zigbee2MqttTopics.IsBridgeTopic(message.Topic))
            {
                ProcessBridgeMessage(message);
            }
            else
            {
                ProcessDeviceMessage(message);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing Zigbee2MQTT message on topic {Topic}", message.Topic);
        }
    }

    private void ProcessBridgeMessage(MqttMessage message)
    {
        switch (message.Topic)
        {
            case Zigbee2MqttTopics.Bridge.State:
                ProcessBridgeState(message.Payload);
                break;
            case Zigbee2MqttTopics.Bridge.Info:
                ProcessBridgeInfo(message.Payload);
                break;
            case Zigbee2MqttTopics.Bridge.Devices:
                ProcessBridgeDevices(message.Payload);
                break;
            case Zigbee2MqttTopics.Bridge.Groups:
                ProcessBridgeGroups(message.Payload);
                break;
            case Zigbee2MqttTopics.Bridge.Event:
                ProcessBridgeEvent(message.Payload);
                break;
            case Zigbee2MqttTopics.Bridge.Logging:
                ProcessBridgeLogging(message.Payload);
                break;
            default:
                if (message.Topic.Contains("/response/", StringComparison.Ordinal))
                {
                    ProcessResponse(message.Topic, message.Payload);
                }
                break;
        }
    }

    private void ProcessBridgeState(string payload)
    {
        // Z2M publishes bridge/state in three shapes across versions:
        //   1. {"state":"online"}                — fresh installs 1.30+
        //   2. "online" (a JSON string literal)  — some broker relays
        //   3. online (bare string)              — pre-1.30 legacy
        // All three accepted here. The '"' check handles (2) — previously
        // quoted strings fell into the bare-string branch and kept the
        // quotes, making IsOnline comparison fail ("\"online\"" != "online").
        try
        {
            Zigbee2MqttBridgeState? state = null;
            var trimmed = payload?.TrimStart() ?? string.Empty;
            if (trimmed.StartsWith('{'))
            {
                state = JsonSerializer.Deserialize<Zigbee2MqttBridgeState>(payload!);
            }
            else if (trimmed.StartsWith('"'))
            {
                var bare = JsonSerializer.Deserialize<string>(payload!);
                if (!string.IsNullOrWhiteSpace(bare))
                {
                    state = new Zigbee2MqttBridgeState { State = bare };
                }
            }
            else if (!string.IsNullOrWhiteSpace(payload))
            {
                state = new Zigbee2MqttBridgeState { State = payload.Trim() };
            }

            if (state is null) return;

            var wasOnline = _bridgeOnline;
            _bridgeOnline = state.IsOnline;

            if (wasOnline != _bridgeOnline)
            {
                _logger.LogInformation("Zigbee2MQTT bridge is now {State}", state.State);
                BridgeStateChanged?.Invoke(this, state);
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse bridge state");
        }
    }

    private void ProcessBridgeInfo(string payload)
    {
        try
        {
            var info = JsonSerializer.Deserialize<Zigbee2MqttBridgeInfo>(payload);
            if (info is null) return;

            lock (_bridgeInfoLock)
            {
                _bridgeInfo = info;
            }

            _logger.LogInformation("Zigbee2MQTT v{Version} on channel {Channel}",
                info.Version, info.Network?.Channel);
            BridgeInfoUpdated?.Invoke(this, info);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse bridge info");
        }
    }

    private void ProcessBridgeDevices(string payload)
    {
        try
        {
            var devices = JsonSerializer.Deserialize<List<Zigbee2MqttDevice>>(payload);
            if (devices is null) return;

            // Immutable-swap: build fresh maps, Volatile-swap both refs.
            var newByName = new Dictionary<string, Zigbee2MqttDevice>(
                devices.Count, StringComparer.OrdinalIgnoreCase);
            var newByIeee = new Dictionary<string, Zigbee2MqttDevice>(
                devices.Count, StringComparer.OrdinalIgnoreCase);

            foreach (var device in devices)
            {
                if (!string.IsNullOrEmpty(device.FriendlyName))
                {
                    newByName[device.FriendlyName] = device;
                }
                if (!string.IsNullOrEmpty(device.IeeeAddress))
                {
                    newByIeee[device.IeeeAddress] = device;
                }
            }

            Volatile.Write(ref _devicesByName, newByName);
            Volatile.Write(ref _devicesByIeee, newByIeee);
            // Unnamed devices are not surfaced via the Devices property.
            Volatile.Write(ref _devicesSnapshot, newByName.Values.ToList().AsReadOnly());

            _logger.LogInformation("Updated {Count} Zigbee devices from bridge", devices.Count);
            DevicesUpdated?.Invoke(this, _devicesSnapshot);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse bridge devices");
        }
    }

    private void ProcessBridgeGroups(string payload)
    {
        try
        {
            var groups = JsonSerializer.Deserialize<List<Zigbee2MqttGroup>>(payload);
            if (groups is null) return;

            var newGroups = new Dictionary<string, Zigbee2MqttGroup>(
                groups.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var group in groups)
            {
                if (!string.IsNullOrEmpty(group.FriendlyName))
                {
                    newGroups[group.FriendlyName] = group;
                }
            }
            Volatile.Write(ref _groups, newGroups);
            Volatile.Write(ref _groupsSnapshot, newGroups.Values.ToList().AsReadOnly());

            _logger.LogInformation("Updated {Count} Zigbee groups from bridge", groups.Count);
            GroupsUpdated?.Invoke(this, _groupsSnapshot);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse bridge groups");
        }
    }

    private void ProcessBridgeEvent(string payload)
    {
        try
        {
            var evt = JsonSerializer.Deserialize<Zigbee2MqttBridgeEvent>(payload);
            if (evt is null) return;

            _logger.LogInformation("Zigbee bridge event: {Type} for {Device}",
                evt.Type, evt.Data?.FriendlyName ?? evt.Data?.IeeeAddress);
            BridgeEventReceived?.Invoke(this, evt);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse bridge event");
        }
    }

    // Z2M bridge/logging emits ANSI colour escapes even over MQTT;
    // stripped at ingest so dashboards don't render garbage.
    private static readonly System.Text.RegularExpressions.Regex AnsiEscapeRegex = new(
        @"\x1b\[[0-9;]*[A-Za-z]",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    private static string StripAnsi(string? s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        var withoutAnsi = AnsiEscapeRegex.Replace(s, string.Empty);
        // Strip remaining C0 control bytes except tab/CR/LF.
        var sb = new System.Text.StringBuilder(withoutAnsi.Length);
        foreach (var c in withoutAnsi)
        {
            if (c >= 32 || c == '\t' || c == '\r' || c == '\n') sb.Append(c);
        }
        return sb.ToString();
    }

    private void ProcessBridgeLogging(string payload)
    {
        try
        {
            var log = JsonSerializer.Deserialize<Zigbee2MqttLogMessage>(payload);
            if (log is null) return;

            var clean = new Zigbee2MqttLogMessage
            {
                Level = StripAnsi(log.Level),
                Message = StripAnsi(log.Message),
                Namespace = StripAnsi(log.Namespace)
            };
            LogMessageReceived?.Invoke(this, clean);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse bridge log message");
        }
    }

    private void ProcessDeviceMessage(MqttMessage message)
    {
        var deviceName = Zigbee2MqttTopics.ExtractDeviceName(message.Topic);
        if (string.IsNullOrEmpty(deviceName)) return;

        switch (Zigbee2MqttTopics.GetTopicAction(message.Topic))
        {
            case Zigbee2MqttTopicAction.State:
                ProcessDeviceState(deviceName, message.Payload);
                break;
            case Zigbee2MqttTopicAction.Availability:
                ProcessDeviceAvailability(deviceName, message.Payload);
                break;
        }
    }

    private void ProcessDeviceState(string deviceName, string payload)
    {
        try
        {
            var state = JsonSerializer.Deserialize<Dictionary<string, object>>(payload);
            if (state is null) return;

            // Immutable merge via AddOrUpdate: readers holding the prior
            // ref cannot observe a mid-mutation state.
            _deviceStates.AddOrUpdate(
                deviceName,
                _ => state,
                (_, existing) =>
                {
                    var merged = new Dictionary<string, object>(existing);
                    foreach (var kvp in state)
                    {
                        merged[kvp.Key] = kvp.Value;
                    }
                    return merged;
                });

            DeviceStateUpdated?.Invoke(this, new Zigbee2MqttDeviceStateEvent
            {
                DeviceName = deviceName,
                Topic = Zigbee2MqttTopics.DeviceState(deviceName),
                State = state,
                Timestamp = DateTime.UtcNow
            });
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse device state for {Device}", deviceName);
        }
    }

    private void ProcessDeviceAvailability(string deviceName, string payload)
    {
        try
        {
            var availability = JsonSerializer.Deserialize<Zigbee2MqttAvailability>(payload);
            if (availability is null) return;

            // AddOrUpdate under contention: the factory + update delegate both
            // observe the prior value atomically, so only one concurrent write
            // for the same device wins. `fired` captures whether THIS call
            // saw a state transition — prior non-atomic TryGetValue + indexer
            // let two concurrent messages both observe the same prior state
            // and both emit the transition event with stale direction.
            var newState = availability.IsOnline;
            var fired = false;
            _deviceAvailability.AddOrUpdate(
                deviceName,
                _ =>
                {
                    // First-ever observation. Treat as transition only if
                    // the default assumption (online) doesn't match.
                    if (newState != true) fired = true;
                    return newState;
                },
                (_, prior) =>
                {
                    if (prior != newState) fired = true;
                    return newState;
                });

            if (!fired) return;

            _logger.LogInformation("Device {Device} is now {State}",
                deviceName, newState ? "online" : "offline");

            DeviceAvailabilityChanged?.Invoke(this, new Zigbee2MqttDeviceAvailabilityEvent
            {
                DeviceName = deviceName,
                IsOnline = newState,
                Timestamp = DateTime.UtcNow
            });
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse device availability for {Device}", deviceName);
        }
    }

    private void ProcessResponse(string topic, string payload)
    {
        var requestType = topic.Replace(Zigbee2MqttTopics.Request.BridgeResponsePrefix, "", StringComparison.Ordinal);

        if (_pendingRequests.TryRemove(requestType, out var tcs))
        {
            tcs.TrySetResult(payload);
        }
    }

    #endregion

    #region Device Control

    /// <inheritdoc/>
    public async Task SetDeviceStateAsync(string deviceName, object state, CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.Serialize(state, JsonOptions);
        await _mqtt.PublishAsync(Zigbee2MqttTopics.DeviceSet(deviceName), payload, cancellationToken: cancellationToken);
    }

    /// <inheritdoc/>
    public async Task SetDevicePropertyAsync(string deviceName, string property, object value, CancellationToken cancellationToken = default)
    {
        var state = new Dictionary<string, object> { [property] = value };
        await SetDeviceStateAsync(deviceName, state, cancellationToken);
    }

    /// <inheritdoc/>
    public Task SetDevicePowerAsync(string deviceName, bool on, CancellationToken cancellationToken = default) =>
        SetDevicePropertyAsync(deviceName, "state", on ? "ON" : "OFF", cancellationToken);

    /// <inheritdoc/>
    public Task SetLightBrightnessAsync(string deviceName, int brightness, CancellationToken cancellationToken = default) =>
        SetDevicePropertyAsync(deviceName, "brightness", Math.Clamp(brightness, 0, 254), cancellationToken);

    /// <inheritdoc/>
    public Task SetLightColorTempAsync(string deviceName, int colorTemp, CancellationToken cancellationToken = default) =>
        SetDevicePropertyAsync(deviceName, "color_temp", colorTemp, cancellationToken);

    /// <inheritdoc/>
    public Task SetLightColorAsync(string deviceName, double x, double y, CancellationToken cancellationToken = default) =>
        SetDeviceStateAsync(deviceName, new { color = new { x, y } }, cancellationToken);

    /// <inheritdoc/>
    public async Task GetDeviceStateAsync(string deviceName, string? property = null, CancellationToken cancellationToken = default)
    {
        var payload = property is not null
            ? JsonSerializer.Serialize(new Dictionary<string, string> { [property] = "" })
            : "{}";
        await _mqtt.PublishAsync(Zigbee2MqttTopics.DeviceGet(deviceName), payload, cancellationToken: cancellationToken);
    }

    #endregion

    #region Bridge Requests

    /// <inheritdoc/>
    public Task<Zigbee2MqttResponse<Zigbee2MqttPermitJoinResponse>?> PermitJoinAsync(int timeSeconds, string? device = null, CancellationToken cancellationToken = default) =>
        SendRequestAsync<Zigbee2MqttPermitJoinResponse>("permit_join",
            new Zigbee2MqttPermitJoinRequest { Time = timeSeconds, Device = device }, cancellationToken);

    /// <inheritdoc/>
    public Task<Zigbee2MqttResponse<Zigbee2MqttHealthCheckResponse>?> HealthCheckAsync(CancellationToken cancellationToken = default) =>
        SendRequestAsync<Zigbee2MqttHealthCheckResponse>("health_check", new { }, cancellationToken);

    /// <inheritdoc/>
    public Task<Zigbee2MqttResponse<Zigbee2MqttNetworkMapResponse>?> GetNetworkMapAsync(string type = "graphviz", bool routes = false, CancellationToken cancellationToken = default) =>
        SendRequestAsync<Zigbee2MqttNetworkMapResponse>("networkmap",
            new Zigbee2MqttNetworkMapRequest { Type = type, Routes = routes }, cancellationToken);

    /// <inheritdoc/>
    public Task RestartBridgeAsync(CancellationToken cancellationToken = default) =>
        _mqtt.PublishAsync(Zigbee2MqttTopics.Request.Restart, "{}", cancellationToken: cancellationToken);

    private async Task<Zigbee2MqttResponse<T>?> SendRequestAsync<T>(string requestType, object request, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<string>();
        // Zigbee2MQTT bridge responses carry no per-call correlation id,
        // so pending requests are keyed on type. A concurrent same-type
        // call would overwrite the first caller's tcs and the response
        // fulfilment would fire on the newer caller only; the first
        // hangs until its 30s timeout. Detect and warn.
        if (!_pendingRequests.TryAdd(requestType, tcs))
        {
            _logger.LogWarning(
                "Concurrent Zigbee bridge request for {RequestType}; earlier caller will time out",
                requestType);
            _pendingRequests[requestType] = tcs;
        }

        try
        {
            var payload = JsonSerializer.Serialize(request, JsonOptions);
            await _mqtt.PublishAsync($"{Zigbee2MqttTopics.Request.BridgeRequestPrefix}{requestType}", payload, cancellationToken: cancellationToken);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(30));

            var responsePayload = await tcs.Task.WaitAsync(cts.Token);
            return JsonSerializer.Deserialize<Zigbee2MqttResponse<T>>(responsePayload);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Request {Type} timed out", requestType);
            return null;
        }
        catch (JsonException ex)
        {
            // A malformed response from Z2M must not crash the caller's
            // higher-level operation. Callers already handle null.
            _logger.LogWarning(ex, "Request {Type} returned unparseable response", requestType);
            return null;
        }
        finally
        {
            // Conditional remove: only clear if the dict still holds OUR tcs.
            // Otherwise a concurrent-overwriter's pending request is wiped.
            _pendingRequests.TryRemove(new KeyValuePair<string, TaskCompletionSource<string>>(requestType, tcs));
        }
    }

    #endregion

    #region Device Management

    /// <inheritdoc/>
    public async Task<bool> RenameDeviceAsync(string currentName, string newName, bool homeAssistantRename = false, CancellationToken cancellationToken = default)
    {
        var request = new Zigbee2MqttDeviceRenameRequest
        {
            From = currentName,
            To = newName,
            HomeAssistantRename = homeAssistantRename
        };
        var response = await SendRequestAsync<object>("device/rename", request, cancellationToken);
        return response?.IsSuccess ?? false;
    }

    /// <inheritdoc/>
    public async Task<bool> RemoveDeviceAsync(string deviceName, bool force = false, bool block = false, CancellationToken cancellationToken = default)
    {
        var request = new Zigbee2MqttDeviceRemoveRequest
        {
            Id = deviceName,
            Force = force,
            Block = block
        };
        var response = await SendRequestAsync<object>("device/remove", request, cancellationToken);
        return response?.IsSuccess ?? false;
    }

    /// <inheritdoc/>
    public Task ConfigureDeviceAsync(string deviceName, CancellationToken cancellationToken = default) =>
        PublishDeviceRequestAsync(Zigbee2MqttTopics.Request.Device.Configure, new Zigbee2MqttDeviceRequest { Id = deviceName }, cancellationToken);

    /// <inheritdoc/>
    public Task InterviewDeviceAsync(string deviceName, CancellationToken cancellationToken = default) =>
        PublishDeviceRequestAsync(Zigbee2MqttTopics.Request.Device.Interview, new Zigbee2MqttDeviceRequest { Id = deviceName }, cancellationToken);

    /// <inheritdoc/>
    public Task SetDeviceOptionsAsync(string deviceName, Dictionary<string, object> options, CancellationToken cancellationToken = default) =>
        PublishDeviceRequestAsync(
            Zigbee2MqttTopics.Request.Device.Options,
            new Zigbee2MqttDeviceOptionsRequest { Id = deviceName, Options = options },
            cancellationToken);

    private Task PublishDeviceRequestAsync(string topic, object request, CancellationToken cancellationToken) =>
        _mqtt.PublishAsync(topic, JsonSerializer.Serialize(request, JsonOptions), cancellationToken: cancellationToken);

    #endregion

    #region Group Management

    /// <inheritdoc/>
    public async Task<bool> CreateGroupAsync(string friendlyName, int? id = null, CancellationToken cancellationToken = default)
    {
        var request = new Zigbee2MqttGroupAddRequest { FriendlyName = friendlyName, Id = id };
        var response = await SendRequestAsync<object>("group/add", request, cancellationToken);
        return response?.IsSuccess ?? false;
    }

    /// <inheritdoc/>
    public async Task<bool> RemoveGroupAsync(string groupName, bool force = false, CancellationToken cancellationToken = default)
    {
        var response = await SendRequestAsync<object>("group/remove", new { id = groupName, force }, cancellationToken);
        return response?.IsSuccess ?? false;
    }

    /// <inheritdoc/>
    public async Task<bool> RenameGroupAsync(string currentName, string newName, CancellationToken cancellationToken = default)
    {
        var response = await SendRequestAsync<object>("group/rename", new { from = currentName, to = newName }, cancellationToken);
        return response?.IsSuccess ?? false;
    }

    /// <inheritdoc/>
    public Task AddDeviceToGroupAsync(string groupName, string deviceName, int? endpoint = null, CancellationToken cancellationToken = default) =>
        PublishDeviceRequestAsync(
            Zigbee2MqttTopics.Request.Group.MembersAdd,
            new Zigbee2MqttGroupMemberRequest { Group = groupName, Device = deviceName, Endpoint = endpoint },
            cancellationToken);

    /// <inheritdoc/>
    public Task RemoveDeviceFromGroupAsync(string groupName, string deviceName, int? endpoint = null, CancellationToken cancellationToken = default) =>
        PublishDeviceRequestAsync(
            Zigbee2MqttTopics.Request.Group.MembersRemove,
            new Zigbee2MqttGroupMemberRequest { Group = groupName, Device = deviceName, Endpoint = endpoint },
            cancellationToken);

    /// <inheritdoc/>
    public async Task SetGroupStateAsync(string groupName, object state, CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.Serialize(state, JsonOptions);
        await _mqtt.PublishAsync($"{Zigbee2MqttTopics.BaseTopic}/{groupName}/set", payload, cancellationToken: cancellationToken);
    }

    #endregion

    #region State Queries

    /// <inheritdoc/>
    public Zigbee2MqttDevice? GetDevice(string friendlyName)
    {
        Volatile.Read(ref _devicesByName).TryGetValue(friendlyName, out var device);
        return device;
    }

    /// <inheritdoc/>
    public Zigbee2MqttDevice? GetDeviceByIeee(string ieeeAddress)
    {
        Volatile.Read(ref _devicesByIeee).TryGetValue(ieeeAddress, out var device);
        return device;
    }

    /// <inheritdoc/>
    public Dictionary<string, object>? GetDeviceState(string friendlyName)
    {
        // Defensive shallow copy: the stored reference is shared with the
        // AddOrUpdate writer, so handing it out directly would let callers
        // mutate our state or enumerate during a concurrent rewrite.
        if (!_deviceStates.TryGetValue(friendlyName, out var state))
        {
            return null;
        }
        return new Dictionary<string, object>(state);
    }

    #endregion
}
