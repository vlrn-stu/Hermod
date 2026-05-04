using System.Text.Json;
using Hermod.Core.Interfaces;
using Hermod.Rules.Coercion;

namespace Hermod.Rules;

public sealed partial class ExpressionEvaluator
{
    private bool TryEvaluateFunction(string expression, ExpressionContext context, out object? result)
    {
        result = null;

        var parenIdx = expression.IndexOf('(', StringComparison.Ordinal);
        if (parenIdx <= 0 || !expression.EndsWith(')')) return false;

        // CA1308: function names are dispatched in uppercase form (compared against uppercase literals).
        var funcName = expression[..parenIdx].Trim().ToUpperInvariant();
        var argsStr = expression[(parenIdx + 1)..^1];
        var args = ParseArguments(argsStr);

        var matched = true;
        result = funcName switch
        {
            "NOW" => context.Now,
            "UTCNOW" => DateTime.UtcNow,
            "DATE" => context.Now.Date,
            "TIME" => context.Now.TimeOfDay,
            "TIMESTAMP" => DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            "YEAR" => context.Now.Year,
            "MONTH" => context.Now.Month,
            "DAY" => context.Now.Day,
            "HOUR" => context.Now.Hour,
            "MINUTE" => context.Now.Minute,
            "DAYOFWEEK" => (int)context.Now.DayOfWeek,

            "ROUND" when args.Count >= 1 => Round(EvaluateExpression(args[0], context),
                args.Count > 1 ? NumericCoercion.ToInt(EvaluateExpression(args[1], context)) : null),
            "FLOOR" when args.Count >= 1 => Math.Floor(NumericCoercion.ToDoubleOrDefault(EvaluateExpression(args[0], context))),
            "CEIL" when args.Count >= 1 => Math.Ceiling(NumericCoercion.ToDoubleOrDefault(EvaluateExpression(args[0], context))),
            "ABS" when args.Count >= 1 => Math.Abs(NumericCoercion.ToDoubleOrDefault(EvaluateExpression(args[0], context))),
            "MIN" when args.Count >= 2 => Math.Min(
                NumericCoercion.ToDoubleOrDefault(EvaluateExpression(args[0], context)),
                NumericCoercion.ToDoubleOrDefault(EvaluateExpression(args[1], context))),
            "MAX" when args.Count >= 2 => Math.Max(
                NumericCoercion.ToDoubleOrDefault(EvaluateExpression(args[0], context)),
                NumericCoercion.ToDoubleOrDefault(EvaluateExpression(args[1], context))),

            "UPPER" when args.Count >= 1 => EvaluateExpression(args[0], context)?.ToString()?.ToUpperInvariant(),
            // User-facing DSL function: LOWER must produce actual lowercase output, so ToLowerInvariant is the requirement, not a smell.
#pragma warning disable CA1308
            "LOWER" when args.Count >= 1 => EvaluateExpression(args[0], context)?.ToString()?.ToLowerInvariant(),
#pragma warning restore CA1308
            "TRIM" when args.Count >= 1 => EvaluateExpression(args[0], context)?.ToString()?.Trim(),
            "LENGTH" when args.Count >= 1 => EvaluateExpression(args[0], context)?.ToString()?.Length ?? 0,
            "SUBSTRING" when args.Count >= 2 => Substring(
                EvaluateExpression(args[0], context)?.ToString(),
                NumericCoercion.ToInt(EvaluateExpression(args[1], context)),
                args.Count > 2 ? NumericCoercion.ToInt(EvaluateExpression(args[2], context)) : null),
            "REPLACE" when args.Count >= 3 => EvaluateExpression(args[0], context)?.ToString()?.Replace(
                ResolveStringArg(args[1], context), ResolveStringArg(args[2], context), StringComparison.Ordinal),
            "CONTAINS" when args.Count >= 2 => EvaluateExpression(args[0], context)?.ToString()
                ?.Contains(ResolveStringArg(args[1], context), StringComparison.OrdinalIgnoreCase) ?? false,
            "STARTSWITH" when args.Count >= 2 => EvaluateExpression(args[0], context)?.ToString()
                ?.StartsWith(ResolveStringArg(args[1], context), StringComparison.OrdinalIgnoreCase) ?? false,
            "ENDSWITH" when args.Count >= 2 => EvaluateExpression(args[0], context)?.ToString()
                ?.EndsWith(ResolveStringArg(args[1], context), StringComparison.OrdinalIgnoreCase) ?? false,

            "IF" when args.Count >= 3 => EvaluateCondition(args[0], context)
                ? EvaluateExpression(args[1], context)
                : EvaluateExpression(args[2], context),
            "COALESCE" => Coalesce(args, context),
            "DEFAULT" when args.Count >= 2 => EvaluateExpression(args[0], context) ?? EvaluateExpression(args[1], context),

            "DEVICE" when args.Count >= 1 => GetDeviceState(EvaluateExpression(args[0], context)?.ToString(), context),
            "JSON" when args.Count >= 1 => ParseJson(EvaluateExpression(args[0], context)?.ToString()),

            _ => NotMatched(out matched),
        };

        return matched;
    }

    private static object? NotMatched(out bool matched)
    {
        matched = false;
        return null;
    }

    // Resolves a function string argument. Evaluates as an expression first
    // (so `"quoted"` and `source.var` both work), then falls back to the raw
    // argument text so historical unquoted-literal uses like
    // `contains(msg, world)` continue to match the literal "world".
    private string ResolveStringArg(string arg, ExpressionContext context) =>
        EvaluateExpression(arg, context)?.ToString() ?? arg;

    private static object? Round(object? value, int? decimals)
    {
        if (!NumericCoercion.TryToDouble(value, out var num)) return value;
        // Math.Round throws if decimals is outside 0..15; clamp so a
        // user-authored ROUND(x, -1) degrades instead of crashing.
        var places = decimals.HasValue ? Math.Clamp(decimals.Value, 0, 15) : -1;
        return places < 0 ? Math.Round(num) : Math.Round(num, places);
    }

    private static string? Substring(string? value, int? start, int? length)
    {
        if (value is null || !start.HasValue) return value;
        // Clamp start: negative indexes throw on range conversion, and a
        // start past the end means "empty tail". Length must be >=0 for the
        // same reason; a negative length would make the slice end earlier
        // than the start and throw ArgumentOutOfRangeException.
        var s = Math.Clamp(start.Value, 0, value.Length);
        if (!length.HasValue) return value[s..];

        var len = Math.Max(0, length.Value);
        var end = Math.Min(s + len, value.Length);
        return value[s..end];
    }

    private object? Coalesce(List<string> args, ExpressionContext context)
    {
        foreach (var arg in args)
        {
            var value = EvaluateExpression(arg, context);
            if (value is not null) return value;
        }
        return null;
    }

    private static Dictionary<string, object>? GetDeviceState(string? deviceName, ExpressionContext context)
    {
        if (string.IsNullOrEmpty(deviceName) || context.GetDeviceState is null) return null;
        return context.GetDeviceState(deviceName);
    }

    private static Dictionary<string, object>? ParseJson(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, object>>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
