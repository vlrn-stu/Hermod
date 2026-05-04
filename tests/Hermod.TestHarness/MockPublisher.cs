using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Hermod.TestHarness;

/// <summary>
/// Publishes mock device messages for ZigBee, Bluetooth, and WiFi protocols
/// directly to the MQTT broker, simulating real device traffic.
/// </summary>
public sealed class MockPublisher
{
    private readonly ILogger<MockPublisher> _logger;
    private readonly MqttTestClient _mqtt;

    public MockPublisher(ILogger<MockPublisher> logger, MqttTestClient mqtt)
    {
        _logger = logger;
        _mqtt = mqtt;
    }

    public async Task PublishZigbeeAsync(string deviceId, object payload, CancellationToken ct = default)
    {
        // zigbee2mqtt is the actual base topic owned by the upstream
        // Zigbee2MQTT container; publishing to `zigbee/` bypasses the
        // coordinator's Zigbee2MqttService filter and drops the message
        // silently. See Hermod.Core.Mqtt.Zigbee2MqttTopics.BaseTopic.
        var topic = $"zigbee/{deviceId}";
        var json = JsonSerializer.Serialize(payload);
        await _mqtt.PublishAsync(topic, json, ct);
        _logger.LogDebug("[Mock:ZigBee] {DeviceId} -> {Payload}", deviceId, json);
    }

    public async Task PublishBluetoothAsync(string deviceId, object payload, CancellationToken ct = default)
    {
        var topic = $"bluetooth/{deviceId}";
        var json = JsonSerializer.Serialize(payload);
        await _mqtt.PublishAsync(topic, json, ct);
        _logger.LogDebug("[Mock:BLE] {DeviceId} -> {Payload}", deviceId, json);
    }

    public async Task PublishWifiAsync(string deviceId, object payload, CancellationToken ct = default)
    {
        var topic = $"wifi/{deviceId}";
        var json = JsonSerializer.Serialize(payload);
        await _mqtt.PublishAsync(topic, json, ct);
        _logger.LogDebug("[Mock:WiFi] {DeviceId} -> {Payload}", deviceId, json);
    }

    /// <summary>
    /// Publishes a raw message to any topic (useful for testing edge cases).
    /// </summary>
    public async Task PublishRawAsync(string topic, string payload, CancellationToken ct = default)
    {
        await _mqtt.PublishAsync(topic, payload, ct);
        _logger.LogDebug("[Mock:Raw] {Topic} -> {Payload}", topic, payload);
    }
}
