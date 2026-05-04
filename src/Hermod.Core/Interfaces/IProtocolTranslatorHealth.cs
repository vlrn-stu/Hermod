namespace Hermod.Core.Interfaces;

/// <summary>
/// Liveness probe for every configured protocol translator (Zigbee2MQTT,
/// LoRa2MQTT, BLE2MQTT, WiFi2MQTT). Normalizes the per-translator health
/// endpoint behind a single call so dashboards and controllers don't have
/// to hardcode paths.
/// </summary>
public interface IProtocolTranslatorHealth
{
    /// <summary>Probes every configured translator in parallel and returns their liveness snapshots.</summary>
    Task<IReadOnlyList<TranslatorHealth>> CheckAllAsync(CancellationToken cancellationToken = default);
}

/// <summary>Liveness snapshot for a single translator.</summary>
/// <param name="Name">Translator name (e.g. <c>"Zigbee2Mqtt"</c>).</param>
/// <param name="Url">Translator base URL, or null when not configured.</param>
/// <param name="Configured">True when a URL was configured for the translator.</param>
/// <param name="Reachable">True when the probe returned a success status in time.</param>
/// <param name="Error">Short error string when unreachable; null on success.</param>
public sealed record TranslatorHealth(
    string Name,
    string? Url,
    bool Configured,
    bool Reachable,
    string? Error);
