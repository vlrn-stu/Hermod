using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Hermod.Infrastructure.Database;

/// <summary>
/// Helpers for reading JSON columns via Dapper without letting malformed rows
/// poison an entire query. Failures are logged with a payload prefix for
/// triage and the column value falls back to <c>default(T)</c>.
/// </summary>
internal static class DapperJsonColumn
{
    /// <summary>
    /// Deserializes a nullable JSON string column into <typeparamref name="T"/>,
    /// returning <c>default</c> on null/empty input or on any deserialization
    /// failure. A warning is logged with the field name and a payload preview.
    /// </summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "JSON column deserialization must never abort a query; failures are logged and fall through to default.")]
    public static T? Deserialize<T>(string? json, JsonSerializerOptions options, ILogger logger, string field)
    {
        if (string.IsNullOrEmpty(json)) return default;
        try
        {
            return JsonSerializer.Deserialize<T>(json, options);
        }
        catch (Exception ex)
        {
            var preview = json.Length > 80
                ? string.Concat(json.AsSpan(0, 80), "...")
                : json;

            logger.LogWarning(
                ex,
                "Failed to deserialize JSON field {Field} as {Type}; falling back to default. Payload prefix: {Prefix}",
                field,
                typeof(T).Name,
                preview);
            return default;
        }
    }
}
