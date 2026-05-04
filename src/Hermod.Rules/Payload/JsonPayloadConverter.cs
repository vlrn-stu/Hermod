using System.Text.Json;
using Hermod.Core.Interfaces;

namespace Hermod.Rules.Payload;

/// <summary>
/// Converts rule-action payload values into their CLR equivalents, expanding
/// <c>{{...}}</c> mustache expressions through the injected
/// <see cref="IExpressionEvaluator"/>. Preserves the numeric form (integer
/// vs fractional) implied by the source JSON text and recurses through
/// nested arrays and objects so passthrough publishes keep structure.
/// </summary>
public sealed class JsonPayloadConverter
{
    private readonly IExpressionEvaluator _evaluator;

    /// <summary>
    /// Creates a converter that expands <c>{{...}}</c> expressions via the
    /// supplied <paramref name="evaluator"/>.
    /// </summary>
    public JsonPayloadConverter(IExpressionEvaluator evaluator)
    {
        ArgumentNullException.ThrowIfNull(evaluator);
        _evaluator = evaluator;
    }

    /// <summary>
    /// Evaluates a top-level payload value. Mustache strings are expanded;
    /// <see cref="JsonElement"/> values are unwrapped; other CLR types pass
    /// through unchanged. A JSON <c>null</c> at the top level maps to the
    /// empty string to preserve the downstream MQTT contract.
    /// </summary>
    public object EvaluatePayloadValue(object value, ExpressionContext context)
    {
        if (value is string s && s.Contains("{{", StringComparison.Ordinal))
        {
            return _evaluator.Evaluate(s, context) ?? "";
        }

        if (value is JsonElement je)
        {
            return ConvertJsonElement(je, context) ?? "";
        }

        return value;
    }

    /// <summary>
    /// Deep-clones a dictionary via a JSON round-trip so nested dictionaries,
    /// lists, and <see cref="JsonElement"/> values detach from the source.
    /// Passthrough publishes use this so transforms cannot mutate aliased
    /// state that is still reachable from the caller's context.
    /// </summary>
    public static Dictionary<string, object> DeepCloneDictionary(Dictionary<string, object> source)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (source.Count == 0) return [];
        try
        {
            var json = JsonSerializer.Serialize(source);
            var clone = JsonSerializer.Deserialize<Dictionary<string, object>>(json);
            return clone ?? new Dictionary<string, object>(source);
        }
        catch (JsonException)
        {
            return new Dictionary<string, object>(source);
        }
    }

    private object? ConvertJsonElement(JsonElement je, ExpressionContext context)
    {
        switch (je.ValueKind)
        {
            case JsonValueKind.String:
                var s = je.GetString() ?? "";
                return s.Contains("{{", StringComparison.Ordinal) ? (_evaluator.Evaluate(s, context) ?? "") : s;

            case JsonValueKind.Number:
                return ConvertNumber(je);

            case JsonValueKind.True:
                return true;

            case JsonValueKind.False:
                return false;

            case JsonValueKind.Null:
                return null;

            case JsonValueKind.Array:
                var list = new List<object?>(je.GetArrayLength());
                foreach (var element in je.EnumerateArray())
                {
                    list.Add(ConvertJsonElement(element, context));
                }
                return list;

            case JsonValueKind.Object:
                var dict = new Dictionary<string, object?>();
                foreach (var prop in je.EnumerateObject())
                {
                    dict[prop.Name] = ConvertJsonElement(prop.Value, context);
                }
                return dict;

            default:
                return je.GetRawText();
        }
    }

    private static object ConvertNumber(JsonElement je)
    {
        var raw = je.GetRawText();
        // CA2249: Contains is preferred over IndexOf>=0 and CA1307 asks for an explicit comparison.
        var looksFractional = raw.Contains('.', StringComparison.Ordinal)
            || raw.Contains('e', StringComparison.Ordinal)
            || raw.Contains('E', StringComparison.Ordinal);

        if (looksFractional)
        {
            if (je.TryGetDouble(out var d)) return d;
            if (je.TryGetDecimal(out var m)) return m;
        }
        else
        {
            if (je.TryGetInt64(out var l)) return l;
            if (je.TryGetDouble(out var d)) return d;
        }

        return raw;
    }
}
