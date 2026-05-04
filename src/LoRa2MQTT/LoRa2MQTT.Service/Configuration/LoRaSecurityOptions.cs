namespace LoRa2MQTT.Service.Configuration;

/// <summary>
/// Ingress guards for the LoRa message path. Messages that fail any of
/// these checks are dropped before reaching MQTT, so a flood/spoof/replay
/// attacker cannot trivially amplify through the rules engine or overwhelm
/// the broker.
/// </summary>
public sealed class LoRaSecurityOptions
{
    /// <summary>Configuration section name in appsettings.json.</summary>
    public const string SectionName = "LoRaSecurity";

    /// <summary>
    /// Hard cap on LoRa payload size in bytes. Anything larger is dropped.
    /// Default 256 (slightly above SX1262 max packet size of 240).
    /// Set to 0 to disable.
    /// </summary>
    public int MaxPayloadBytes { get; set; } = 256;

    /// <summary>
    /// Max messages accepted from a single source address over any 60 s
    /// sliding window. Once hit, further messages from that address are
    /// dropped until old entries age out. 0 disables the rate limit.
    /// Default 60 (~1 msg/s per device).
    /// </summary>
    public int MaxMessagesPerMinutePerAddress { get; set; } = 60;

    /// <summary>
    /// Duplicate-suppression window in seconds. A message with the same
    /// payload from the same address within this window is treated as a
    /// replay and dropped. 0 disables dedup. Default 5 seconds.
    /// </summary>
    public int DedupWindowSeconds { get; set; } = 5;

    /// <summary>
    /// Optional allowlist of source addresses. When non-empty, only
    /// messages from these addresses are accepted; everything else is
    /// dropped as a probable spoof or stray packet from a neighbouring
    /// LoRa network on the same channel. Empty disables the allowlist.
    /// </summary>
    public int[] AddressAllowlist { get; set; } = Array.Empty<int>();
}
