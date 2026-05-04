using System.Text.Json;

namespace Hermod.Core.Mqtt;

/// <summary>
/// Extracts the <c>_uuid</c> field from a JSON MQTT payload without paying
/// a full deserialize when the field is absent. Load gen stamps
/// <c>_uuid</c> on every outbound message when a matrix run wants
/// end-to-end latency traces; production traffic has no <c>_uuid</c> and
/// should not pay a parse tax. A cheap substring probe gates the real
/// JSON read so the ingest hot path stays free when nobody's tracing.
/// </summary>
public static class PayloadUuidExtractor
{
    private const string UuidKey = "_uuid";
    private const string UuidKeyJson = "\"_uuid\"";

    /// <summary>
    /// Returns the string value of the top-level <c>_uuid</c> field, or
    /// null if the payload does not contain one, is not valid JSON, or
    /// the value is not a string.
    /// </summary>
    public static string? TryExtract(string? payload)
    {
        if (string.IsNullOrEmpty(payload))
        {
            return null;
        }

        if (payload.IndexOf(UuidKeyJson, StringComparison.Ordinal) < 0)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            if (!doc.RootElement.TryGetProperty(UuidKey, out var value))
            {
                return null;
            }
            return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
