using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using LoRa2MQTT.Service.Configuration;
using LoRa2MQTT.Service.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LoRa2MQTT.Service.Adapters;

/// <summary>
/// Mock LoRa adapter for testing without hardware.
/// Simulates LoRa devices publishing sensor data.
/// Controllable via MQTT topics for UI integration.
/// </summary>
/// <remarks>
/// MQTT Control Topics:
/// - lora/mock/trigger/{deviceId} - Trigger a message from specific device
/// - lora/mock/trigger/all - Trigger messages from all devices
/// - lora/mock/inject - Inject a custom LoRa message (payload: LoRaMessage JSON)
/// - lora/mock/auto/start - Start auto-simulation
/// - lora/mock/auto/stop - Stop auto-simulation
/// - lora/mock/devices/add - Add a mock device dynamically
/// - lora/mock/devices/remove - Remove a mock device
/// - lora/mock/devices/clear - Clear all mock devices
/// </remarks>
public sealed class MockLoRaAdapter : ILoRaAdapter
{
    private static readonly JsonSerializerOptions DeviceJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ILogger<MockLoRaAdapter> _logger;
    private readonly LoRaOptions _loraOptions;
    private readonly int _intervalMs;
    private CancellationTokenSource? _cts;
    private Task? _simulationTask;
    private readonly Random _random = new();
    private readonly Dictionary<string, int> _deviceAddresses = [];
    private readonly List<MockDevice> _devices = [];
    private int _nextAddress = 1;

    /// <inheritdoc/>
    public event EventHandler<LoRaMessage>? MessageReceived;

    /// <inheritdoc/>
    public bool IsConnected { get; private set; }

    /// <summary>
    /// Gets or sets whether auto-simulation is running.
    /// </summary>
    public bool AutoSimulationRunning { get; private set; }

    /// <summary>
    /// Gets the registered mock devices.
    /// </summary>
    public IReadOnlyList<MockDevice> Devices => _devices;

    /// <summary>
    /// Initializes a new instance of <see cref="MockLoRaAdapter"/>.
    /// </summary>
    public MockLoRaAdapter(
        ILogger<MockLoRaAdapter> logger,
        IOptions<MockOptions> mockOptions,
        IOptions<LoRaOptions> loraOptions)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(mockOptions);
        ArgumentNullException.ThrowIfNull(loraOptions);
        _logger = logger;
        _loraOptions = loraOptions.Value;
        _intervalMs = mockOptions.Value.IntervalMs;

        foreach (var device in mockOptions.Value.Devices)
        {
            AddDevice(device);
        }
    }

    /// <inheritdoc/>
    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Mock LoRa adapter connected");
        IsConnected = true;
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Mock LoRa adapter disconnecting");
        StopAutoSimulation();
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        IsConnected = false;
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task SendAsync(LoRaCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        _logger.LogInformation(
            "Mock: Received command for address {Address}: {Payload}",
            command.Address, command.Payload);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task StartReceivingAsync(CancellationToken cancellationToken = default)
    {
        // Guard against double-start: stomping _cts without disposing the
        // previous one orphans it, and any simulation task launched from
        // the first call still holds the old token.
        if (_cts is not null)
        {
            _logger.LogWarning("Mock LoRa adapter already started; ignoring StartReceivingAsync");
            return Task.CompletedTask;
        }
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Do not auto-start simulation - wait for devices to be registered via MQTT
        _logger.LogInformation("Mock LoRa adapter ready. Devices: {Count}", _devices.Count);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Handles MQTT control messages for mock operations.
    /// Called by MqttService when messages arrive on lora/mock/# topics.
    /// </summary>
    /// <param name="topic">The MQTT topic.</param>
    /// <param name="payload">The message payload.</param>
    public void HandleMockControl(string topic, string payload)
    {
        ArgumentNullException.ThrowIfNull(topic);
        _logger.LogDebug("Mock control: {Topic} -> {Payload}", topic, payload);

        var parts = topic.Split('/');
        if (parts.Length < 3 || parts[1] != "mock") return;

        var action = parts[2];

        switch (action)
        {
            case "trigger":
                if (parts.Length >= 4)
                {
                    var deviceId = parts[3];
                    if (deviceId == "all")
                        TriggerAllDevices();
                    else
                        TriggerDevice(deviceId);
                }
                break;

            case "inject":
                InjectMessage(payload);
                break;

            case "auto":
                if (parts.Length >= 4)
                {
                    if (parts[3] == "start") StartAutoSimulation();
                    else if (parts[3] == "stop") StopAutoSimulation();
                }
                break;

            case "devices":
                if (parts.Length >= 4)
                {
                    switch (parts[3])
                    {
                        case "add":
                            AddDeviceFromJson(payload);
                            break;
                        case "remove":
                            RemoveDeviceFromJson(payload);
                            break;
                        case "clear":
                            ClearDevices();
                            break;
                    }
                }
                break;
        }
    }

    /// <summary>
    /// Triggers a message from a specific device.
    /// </summary>
    public void TriggerDevice(string deviceId)
    {
        var device = _devices.FirstOrDefault(d => d.Id == deviceId);
        if (device is null)
        {
            _logger.LogWarning("Mock device not found: {DeviceId}", deviceId);
            return;
        }

        var message = GenerateMessage(device);
        _logger.LogInformation("Triggered mock message from {DeviceId}", deviceId);
        MessageReceived?.Invoke(this, message);
    }

    /// <summary>
    /// Triggers messages from all registered devices.
    /// </summary>
    public void TriggerAllDevices()
    {
        foreach (var device in _devices.ToList())
        {
            var message = GenerateMessage(device);
            MessageReceived?.Invoke(this, message);
        }
        _logger.LogInformation("Triggered mock messages from all {Count} devices", _devices.Count);
    }

    /// <summary>
    /// Injects a custom message (from JSON payload).
    /// </summary>
    public void InjectMessage(string jsonPayload)
    {
        try
        {
            var message = JsonSerializer.Deserialize<LoRaMessage>(jsonPayload);
            if (message is not null)
            {
                message.Timestamp = DateTimeOffset.UtcNow;
                _logger.LogInformation("Injected custom LoRa message");
                MessageReceived?.Invoke(this, message);
            }
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse injected message");
        }
        catch (ArgumentNullException ex)
        {
            _logger.LogError(ex, "Failed to parse injected message");
        }
    }

    /// <summary>
    /// Starts auto-simulation of devices.
    /// </summary>
    public void StartAutoSimulation()
    {
        if (AutoSimulationRunning) return;
        if (_devices.Count == 0)
        {
            _logger.LogWarning("Cannot start auto-simulation: no devices registered");
            return;
        }

        // Fail-closed if auto-sim is requested before StartReceivingAsync
        // has initialized the linked CancellationTokenSource. A token-less
        // fallback would kick off a simulation loop that outlives bridge
        // shutdown; bail out and warn so the ordering violation is visible.
        if (_cts is null)
        {
            _logger.LogWarning(
                "Cannot start auto-simulation before StartReceivingAsync has run; ignoring request");
            return;
        }

        AutoSimulationRunning = true;
        _simulationTask = SimulateDevicesAsync(_cts.Token);
        _logger.LogInformation("Auto-simulation started (interval: {Interval}ms, devices: {Count})", _intervalMs, _devices.Count);
    }

    /// <summary>
    /// Stops auto-simulation.
    /// </summary>
    public void StopAutoSimulation()
    {
        AutoSimulationRunning = false;
        _logger.LogInformation("Auto-simulation stopped");
    }

    /// <summary>
    /// Adds a mock device dynamically.
    /// </summary>
    public void AddDevice(MockDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (_devices.Any(d => d.Id == device.Id))
        {
            _logger.LogWarning("Device {DeviceId} already exists", device.Id);
            return;
        }

        _devices.Add(device);
        _deviceAddresses[device.Id] = _nextAddress++;
        _logger.LogInformation("Added mock device: {DeviceId} ({Type})", device.Id, device.Type);
    }

    /// <summary>
    /// Removes a mock device.
    /// </summary>
    public void RemoveDevice(string deviceId)
    {
        var removed = _devices.RemoveAll(d => d.Id == deviceId);
        if (removed > 0)
        {
            _deviceAddresses.Remove(deviceId);
            _logger.LogInformation("Removed mock device: {DeviceId}", deviceId);
        }
    }

    /// <summary>
    /// Clears all mock devices.
    /// </summary>
    public void ClearDevices()
    {
        _devices.Clear();
        _deviceAddresses.Clear();
        _nextAddress = 1;
        _logger.LogInformation("Cleared all mock devices");
    }

    private void AddDeviceFromJson(string payload)
    {
        try
        {
            var device = JsonSerializer.Deserialize<MockDevice>(payload, DeviceJsonOptions);
            if (device is not null && !string.IsNullOrEmpty(device.Id))
            {
                AddDevice(device);
            }
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse device JSON");
        }
        catch (ArgumentNullException ex)
        {
            _logger.LogError(ex, "Failed to parse device JSON");
        }
    }

    private void RemoveDeviceFromJson(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.TryGetProperty("id", out var idProp))
            {
                RemoveDevice(idProp.GetString() ?? "");
            }
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse remove device JSON");
        }
        catch (ArgumentNullException ex)
        {
            _logger.LogError(ex, "Failed to parse remove device JSON");
        }
    }

    private async Task SimulateDevicesAsync(CancellationToken cancellationToken)
    {
        var interval = _intervalMs > 0 ? _intervalMs : 10000;

        while (!cancellationToken.IsCancellationRequested && AutoSimulationRunning)
        {
            foreach (var device in _devices.ToList())
            {
                if (cancellationToken.IsCancellationRequested || !AutoSimulationRunning) break;

                var message = GenerateMessage(device);
                MessageReceived?.Invoke(this, message);

                await Task.Delay(100, cancellationToken);
            }

            await Task.Delay(interval, cancellationToken);
        }
    }

    private LoRaMessage GenerateMessage(MockDevice device)
    {
        var payload = GenerateDevicePayload(device);
        return new LoRaMessage
        {
            Address = _deviceAddresses.GetValueOrDefault(device.Id, 0),
            Channel = _loraOptions.Channel,
            Payload = payload,
            PayloadHex = Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(payload)),
            Rssi = _random.Next(-120, -60),
            Snr = Math.Round(_random.NextDouble() * 15 - 5, 1),
            Timestamp = DateTimeOffset.UtcNow
        };
    }

    [SuppressMessage("Globalization", "CA1308:Normalize strings to uppercase", Justification = "Switch arms use lowercase literals by design; ToLowerInvariant is the matching normalization.")]
    private string GenerateDevicePayload(MockDevice device)
    {
        object data = device.Type.ToLowerInvariant() switch
        {
            "weather" => new
            {
                device_id = device.Id,
                type = device.Type,
                manufacturer = device.Manufacturer,
                model = device.Model,
                temperature = Math.Round(15 + _random.NextDouble() * 20, 1),
                humidity = _random.Next(30, 90),
                pressure = Math.Round(1000 + _random.NextDouble() * 30, 2),
                wind_speed = Math.Round(_random.NextDouble() * 30, 1),
                wind_direction = _random.Next(0, 360),
                rain_1h = Math.Round(_random.NextDouble() * 5, 1),
                battery = Math.Round(3.0 + _random.NextDouble() * 0.6, 2)
            },
            "soil" => new
            {
                device_id = device.Id,
                type = device.Type,
                manufacturer = device.Manufacturer,
                model = device.Model,
                moisture = _random.Next(20, 80),
                temperature = Math.Round(10 + _random.NextDouble() * 20, 1),
                ec = Math.Round(0.5 + _random.NextDouble() * 2, 2),
                battery = Math.Round(3.0 + _random.NextDouble() * 0.6, 2)
            },
            "meter" => new
            {
                device_id = device.Id,
                type = device.Type,
                manufacturer = device.Manufacturer,
                model = device.Model,
                consumption = Math.Round(1000 + _random.NextDouble() * 5000, 2),
                flow_rate = Math.Round(_random.NextDouble() * 2, 2),
                battery = _random.Next(70, 100)
            },
            "gps" => new
            {
                device_id = device.Id,
                type = device.Type,
                manufacturer = device.Manufacturer,
                model = device.Model,
                latitude = Math.Round(48.1 + _random.NextDouble() * 0.1, 6),
                longitude = Math.Round(17.1 + _random.NextDouble() * 0.1, 6),
                altitude = _random.Next(100, 500),
                speed = Math.Round(_random.NextDouble() * 50, 1),
                battery = _random.Next(50, 100)
            },
            _ => new
            {
                device_id = device.Id,
                type = device.Type,
                manufacturer = device.Manufacturer,
                model = device.Model,
                value = _random.Next(0, 100),
                battery = _random.Next(50, 100)
            }
        };

        return JsonSerializer.Serialize(data);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
    }
}
