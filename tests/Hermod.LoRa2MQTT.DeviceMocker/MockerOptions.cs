namespace Hermod.LoRa2MQTT.DeviceMocker;

public sealed class MockerOptions
{
    public const string SectionName = "Mocker";

    public string SerialPort { get; set; } = "/dev/ttyACM1";
    public int BaudRate { get; set; } = 115200;

    public MockerMode Mode { get; set; } = MockerMode.Normal;

    public int Address { get; set; } = 100;
    public string DeviceName { get; set; } = "mock-sensor-1";

    public int IntervalMs { get; set; } = 1000;

    public int BurstCount { get; set; } = 1000;
    public int BurstDelayMicros { get; set; } = 0;

    public int FloodMessagesPerMinute { get; set; } = 600;

    public int OversizePayloadBytes { get; set; } = 512;
    public int ReplayCount { get; set; } = 10;
    public int SpoofAddress { get; set; } = 0;

    public bool AppendLf { get; set; } = true;
    public bool RunOnce { get; set; } = false;
}

public enum MockerMode
{
    Normal,
    Burst,
    Flood,
    Replay,
    Oversize,
    Spoof,
    Sweep,
    Silence,
}
