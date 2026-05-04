namespace LoRa2MQTT.Service.Configuration;

/// <summary>
/// Configuration options for the LoRa adapter.
/// </summary>
public sealed class LoRaOptions
{
    /// <summary>
    /// Configuration section name in appsettings.json.
    /// </summary>
    public const string SectionName = "LoRa";

    /// <summary>
    /// Gets or sets whether to use mock mode (no hardware).
    /// </summary>
    public bool MockMode { get; set; }

    /// <summary>
    /// Gets or sets the serial port name (e.g., COM3, /dev/ttyUSB0).
    /// </summary>
    public string SerialPort { get; set; } = "/dev/ttyUSB0";

    /// <summary>
    /// Gets or sets the baud rate for serial communication.
    /// </summary>
    public int BaudRate { get; set; } = 115200;

    /// <summary>
    /// Gets or sets the LoRa channel (0-80). Channel 18 = 868MHz for EU.
    /// </summary>
    public int Channel { get; set; } = 18;

    /// <summary>
    /// Gets or sets the device address (0-65535). 65535 = broadcast.
    /// </summary>
    public int Address { get; set; } = 0;

    /// <summary>
    /// Gets or sets the network ID (0-255).
    /// </summary>
    public int NetworkId { get; set; } = 0;

    /// <summary>
    /// Gets or sets the spreading factor (7-12). Higher = longer range, slower.
    /// </summary>
    public int SpreadingFactor { get; set; } = 7;

    /// <summary>
    /// Gets or sets the transmit power in dBm (10-22).
    /// </summary>
    public int TransmitPower { get; set; } = 22;

    /// <summary>
    /// Gets or sets whether to enable RSSI output.
    /// </summary>
    public bool EnableRssi { get; set; } = true;
}
