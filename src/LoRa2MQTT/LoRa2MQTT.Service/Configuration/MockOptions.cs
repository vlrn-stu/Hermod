namespace LoRa2MQTT.Service.Configuration;

/// <summary>
/// Configuration options for mock mode.
/// </summary>
public sealed class MockOptions
{
    /// <summary>
    /// Configuration section name in appsettings.json.
    /// </summary>
    public const string SectionName = "Mock";

    /// <summary>
    /// Gets or sets the interval between mock messages in milliseconds.
    /// </summary>
    public int IntervalMs { get; set; } = 10000;

    /// <summary>
    /// Gets or sets the mock devices to simulate.
    /// </summary>
    public List<MockDevice> Devices { get; set; } = [];
}

/// <summary>
/// Configuration for a mock LoRa device.
/// </summary>
public sealed class MockDevice
{
    /// <summary>
    /// Gets or sets the device ID.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the device type (e.g., weather, soil, meter, gps).
    /// </summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the device manufacturer.
    /// </summary>
    public string Manufacturer { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the device model.
    /// </summary>
    public string Model { get; set; } = string.Empty;
}
