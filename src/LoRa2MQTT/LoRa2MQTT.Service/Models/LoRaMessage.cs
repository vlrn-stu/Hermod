using System.Text.Json.Serialization;

namespace LoRa2MQTT.Service.Models;

/// <summary>
/// Represents a LoRa message received from a device.
/// </summary>
public sealed class LoRaMessage
{
    /// <summary>
    /// Gets or sets the source device address.
    /// </summary>
    [JsonPropertyName("address")]
    public int Address { get; set; }

    /// <summary>
    /// Gets or sets the channel the message was received on.
    /// </summary>
    [JsonPropertyName("channel")]
    public int Channel { get; set; }

    /// <summary>
    /// Gets or sets the raw payload data.
    /// </summary>
    [JsonPropertyName("payload")]
    public string Payload { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the payload as hex string.
    /// </summary>
    [JsonPropertyName("payload_hex")]
    public string PayloadHex { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the RSSI value in dBm.
    /// </summary>
    [JsonPropertyName("rssi")]
    public int? Rssi { get; set; }

    /// <summary>
    /// Gets or sets the SNR value in dB.
    /// </summary>
    [JsonPropertyName("snr")]
    public double? Snr { get; set; }

    /// <summary>
    /// Gets or sets the timestamp when the message was received.
    /// </summary>
    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Represents a command to send to a LoRa device.
/// </summary>
public sealed class LoRaCommand
{
    /// <summary>
    /// Gets or sets the target device address.
    /// </summary>
    [JsonPropertyName("address")]
    public int Address { get; set; }

    /// <summary>
    /// Gets or sets the payload to send.
    /// </summary>
    [JsonPropertyName("payload")]
    public string Payload { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the payload as hex string (alternative to payload).
    /// </summary>
    [JsonPropertyName("payload_hex")]
    public string? PayloadHex { get; set; }
}
