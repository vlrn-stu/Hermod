using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Hermod.Core.Interfaces;
using Hermod.Rules.Coercion;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hermod.Rules;

/// <summary>
/// Evaluates template expressions like <c>{{source.temperature}}</c> or
/// <c>{{now()}}</c>. Supports nested property access, arithmetic and logical
/// operators with standard precedence, comparisons, built-in functions, and
/// literals. Produces culture-invariant string output for MQTT payloads.
/// </summary>
public sealed partial class ExpressionEvaluator : IExpressionEvaluator
{
    private static readonly Regex TemplatePattern = GenerateTemplateRegex();

    [GeneratedRegex(@"\{\{(.+?)\}\}", RegexOptions.Compiled)]
    private static partial Regex GenerateTemplateRegex();

    private readonly ILogger<ExpressionEvaluator> _logger;

    /// <summary>Creates an evaluator using a null logger; intended for tests and simple hosts.</summary>
    public ExpressionEvaluator() : this(NullLogger<ExpressionEvaluator>.Instance) { }

    /// <summary>Creates an evaluator that logs coercion failures to <paramref name="logger"/>.</summary>
    public ExpressionEvaluator(ILogger<ExpressionEvaluator> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <summary>
    /// Evaluates <paramref name="template"/> against <paramref name="context"/>. Whole-string
    /// templates return the raw expression result; mixed templates return an invariant string.
    /// </summary>
    public object? Evaluate(string template, ExpressionContext context)
    {
        if (string.IsNullOrEmpty(template)) return template;

        var fullMatch = TemplatePattern.Match(template);
        if (fullMatch.Success && fullMatch.Value == template)
        {
            return EvaluateExpression(fullMatch.Groups[1].Value.Trim(), context);
        }

        return TemplatePattern.Replace(template, match =>
        {
            var expression = match.Groups[1].Value.Trim();
            var value = EvaluateExpression(expression, context);
            return FormatValueInvariant(value);
        });
    }

    /// <summary>
    /// Evaluates <paramref name="expression"/> as a boolean. An empty expression
    /// is treated as <c>true</c> so rules can omit the field to mean "always".
    /// </summary>
    public bool EvaluateCondition(string expression, ExpressionContext context)
    {
        if (string.IsNullOrEmpty(expression)) return true;
        return CoerceToBool(Evaluate(expression, context));
    }

    /// <summary>
    /// Evaluates <paramref name="template"/> and coerces the result to <typeparamref name="T"/>.
    /// Falls back to <c>default</c> when the value is null or coercion throws.
    /// </summary>
    public T? Evaluate<T>(string template, ExpressionContext context)
    {
        var result = Evaluate(template, context);

        if (result is null) return default;
        if (result is T typed) return typed;

        try
        {
            if (typeof(T) == typeof(string))
            {
                return (T)(object)FormatValueInvariant(result);
            }
            if (typeof(T) == typeof(int) && result is IConvertible)
            {
                return (T)(object)Convert.ToInt32(result, CultureInfo.InvariantCulture);
            }
            if (typeof(T) == typeof(double) && result is IConvertible)
            {
                return (T)(object)Convert.ToDouble(result, CultureInfo.InvariantCulture);
            }
            if (typeof(T) == typeof(bool))
            {
                return (T)(object)CoerceToBool(result);
            }
            return JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(result));
        }
#pragma warning disable CA1031 // broad catch intentional: any conversion/serialization failure degrades gracefully with a Debug log
        catch (Exception ex)
#pragma warning restore CA1031
        {
            // Debug-logged so "rule just never fires" stops being a silent swallow.
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex,
                    "Expression coercion to {TargetType} failed for template '{Template}' (value type {ActualType})",
                    typeof(T).Name, template, result.GetType().Name);
            }
            return default;
        }
    }

    private object? EvaluateExpression(string expression, ExpressionContext context)
    {
        expression = StripOuterParens(expression);

        // Precedence (low → high): logical → comparison → arithmetic → function → literal.
        if (TryEvaluateLogical(expression, context, out var logicalResult)) return logicalResult;
        if (TryEvaluateComparison(expression, context, out var compResult)) return compResult;
        if (TryEvaluateArithmetic(expression, context, out var arithResult)) return arithResult;
        if (TryEvaluateFunction(expression, context, out var funcResult)) return funcResult;

        return ResolveValue(expression, context);
    }

    private object? ResolveValue(string path, ExpressionContext context)
    {
        // Length>=2 guard: a single `"` or `'` is both start and end, and
        // `path[1..^1]` would then be `path[1..0]` (throws ArgumentOutOfRangeException).
        if (path.Length >= 2 && path[0] == '"' && path[^1] == '"') return path[1..^1];
        if (path.Length >= 2 && path[0] == '\'' && path[^1] == '\'') return path[1..^1];
        if (double.TryParse(path, NumberStyles.Any, CultureInfo.InvariantCulture, out var num)) return num;
        if (path.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
        if (path.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
        if (path.Equals("null", StringComparison.OrdinalIgnoreCase)) return null;

        var parts = path.Split('.');
        var root = parts[0].ToUpperInvariant();

        return root switch
        {
            "SOURCE" => GetNestedValue(context.Source, parts[1..]),
            "TOPIC" => context.Topic,
            "DEVICENAME" => context.DeviceName,
            "STATE" => GetNestedValue(context.State, parts[1..]),
            "GLOBAL" => GetNestedValue(context.Global, parts[1..]),
            "CHAIN" => GetNestedValue(context.ChainData, parts[1..]),
            "PREVIOUS" => GetNestedValue(context.Previous, parts[1..]),
            "VARIABLES" => GetNestedValue(context.Variables, parts[1..]),
            _ => GetNestedValue(context.Source, parts) ?? GetNestedValue(context.Variables, parts),
        };
    }

    private static object? GetNestedValue(Dictionary<string, object>? dict, string[] path)
    {
        if (dict is null || path.Length == 0) return dict;

        object? current = dict;

        foreach (var key in path)
        {
            if (current is Dictionary<string, object> d)
            {
                if (!d.TryGetValue(key, out current)) return null;
            }
            else if (current is JsonElement je)
            {
                if (je.ValueKind == JsonValueKind.Object && je.TryGetProperty(key, out var prop))
                {
                    current = GetJsonValue(prop);
                }
                else
                {
                    return null;
                }
            }
            else
            {
                return null;
            }
        }

        return current;
    }

    private static object? GetJsonValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number when element.TryGetInt64(out var l) => l,
        JsonValueKind.Number when element.TryGetDouble(out var d) => d,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => element,
    };

    /// <summary>Culture-invariant conversion so MQTT payloads carry no locale-dependent separators.</summary>
    private static string FormatValueInvariant(object? value) => value switch
    {
        null => string.Empty,
        double d => d.ToString(CultureInfo.InvariantCulture),
        float f => f.ToString(CultureInfo.InvariantCulture),
        decimal m => m.ToString(CultureInfo.InvariantCulture),
        IFormattable fmt => fmt.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    private static bool CoerceToBool(object? value) => value switch
    {
        bool b => b,
        string s when bool.TryParse(s, out var boolVal) => boolVal,
        string s => !string.IsNullOrEmpty(s) && !s.Equals("false", StringComparison.OrdinalIgnoreCase) && s != "0",
        int i => i != 0,
        long l => l != 0,
        double d => d != 0,
        float f => f != 0,
        decimal m => m != 0,
        null => false,
        _ => true,
    };
}
