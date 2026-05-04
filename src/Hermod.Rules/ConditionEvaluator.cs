using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Hermod.Core.Interfaces;
using Hermod.Core.Models.Rules;
using Hermod.Rules.Coercion;

namespace Hermod.Rules;

/// <summary>
/// Evaluates rule conditions with nested All/Any/None groups, case-insensitive
/// string comparisons, dotted property paths, and a regex cache with fail-closed
/// semantics on invalid patterns or match-time timeouts.
/// </summary>
public sealed class ConditionEvaluator(IExpressionEvaluator expressionEvaluator)
{
    private const StringComparison StringCompare = StringComparison.OrdinalIgnoreCase;

    private readonly IExpressionEvaluator _expressionEvaluator = expressionEvaluator;

    /// <summary>
    /// Evaluate a condition group against the given context. All/Any/None
    /// short-circuit on the first deciding result. Empty groups return true.
    /// </summary>
    public bool Evaluate(RuleConditionGroup? group, ExpressionContext context)
    {
        if (group is null) return true;
        if (group.Conditions.Count == 0 && group.Groups.Count == 0) return true;

        return group.Logic switch
        {
            LogicOperator.All => EvaluateAll(group, context),
            LogicOperator.Any => EvaluateAny(group, context),
            LogicOperator.None => EvaluateNone(group, context),
            _ => true,
        };
    }

    private bool EvaluateAll(RuleConditionGroup group, ExpressionContext context)
    {
        foreach (var condition in group.Conditions)
        {
            if (!EvaluateSingle(condition, context)) return false;
        }
        foreach (var nested in group.Groups)
        {
            if (!Evaluate(nested, context)) return false;
        }
        return true;
    }

    private bool EvaluateAny(RuleConditionGroup group, ExpressionContext context)
    {
        foreach (var condition in group.Conditions)
        {
            if (EvaluateSingle(condition, context)) return true;
        }
        foreach (var nested in group.Groups)
        {
            if (Evaluate(nested, context)) return true;
        }
        return false;
    }

    private bool EvaluateNone(RuleConditionGroup group, ExpressionContext context)
    {
        foreach (var condition in group.Conditions)
        {
            if (EvaluateSingle(condition, context)) return false;
        }
        foreach (var nested in group.Groups)
        {
            if (Evaluate(nested, context)) return false;
        }
        return true;
    }

    /// <summary>Evaluate a single condition against <paramref name="context"/>.</summary>
    public bool EvaluateSingle(RuleCondition condition, ExpressionContext context)
    {
        ArgumentNullException.ThrowIfNull(condition);
        ArgumentNullException.ThrowIfNull(context);

        var leftValue = ResolveLeft(condition, context);
        if (leftValue is null && condition.Operator != ComparisonOperator.NotExists &&
            condition.Operator != ComparisonOperator.Exists &&
            condition.Operator != ComparisonOperator.Changed &&
            string.IsNullOrEmpty(condition.Expression) && string.IsNullOrEmpty(condition.Property))
        {
            return true;
        }

        var rightValue = condition.Value;
        if (rightValue is string strValue && strValue.Contains("{{", StringComparison.Ordinal))
        {
            rightValue = _expressionEvaluator.Evaluate(strValue, context);
        }

        return condition.Operator switch
        {
            ComparisonOperator.Equals => NumericCoercion.LooseEquals(leftValue, rightValue, StringCompare),
            ComparisonOperator.NotEquals => !NumericCoercion.LooseEquals(leftValue, rightValue, StringCompare),
            ComparisonOperator.GreaterThan => NumericCoercion.Compare(leftValue, rightValue, StringCompare) > 0,
            ComparisonOperator.LessThan => NumericCoercion.Compare(leftValue, rightValue, StringCompare) < 0,
            ComparisonOperator.GreaterThanOrEquals => NumericCoercion.Compare(leftValue, rightValue, StringCompare) >= 0,
            ComparisonOperator.LessThanOrEquals => NumericCoercion.Compare(leftValue, rightValue, StringCompare) <= 0,
            ComparisonOperator.Contains => StringContains(leftValue, rightValue),
            ComparisonOperator.NotContains => !StringContains(leftValue, rightValue),
            ComparisonOperator.StartsWith => StringStartsWith(leftValue, rightValue),
            ComparisonOperator.EndsWith => StringEndsWith(leftValue, rightValue),
            ComparisonOperator.Matches => MatchesPattern(leftValue, rightValue),
            ComparisonOperator.Exists => leftValue is not null,
            ComparisonOperator.NotExists => leftValue is null,
            ComparisonOperator.In => IsInCollection(leftValue, condition.Values),
            ComparisonOperator.NotIn => !IsInCollection(leftValue, condition.Values),
            ComparisonOperator.Between => IsBetween(leftValue, condition.Values),
            ComparisonOperator.Changed => HasChanged(condition.Property, context),
            ComparisonOperator.IsTrue => IsTruthy(leftValue),
            ComparisonOperator.IsFalse => !IsTruthy(leftValue),
            _ => true,
        };
    }

    private object? ResolveLeft(RuleCondition condition, ExpressionContext context)
    {
        if (!string.IsNullOrEmpty(condition.Expression))
        {
            return _expressionEvaluator.Evaluate(condition.Expression, context);
        }
        if (!string.IsNullOrEmpty(condition.Property))
        {
            return GetPropertyValue(condition.Property, context);
        }
        return null;
    }

    private static readonly HashSet<string> KnownPrefixes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "source", "state", "global", "chain", "previous", "variables",
        };

    private enum PathNamespace
    {
        SourceOrVariables = 0,
        Source,
        State,
        Global,
        Chain,
        Previous,
        Variables,
    }

    private readonly record struct CachedPath(PathNamespace Namespace, string[] Segments, bool SingleSegment);

    private const int MaxCachedPaths = 512;
    private static readonly ConcurrentDictionary<string, CachedPath> PathCache = new();

    internal static void ResetPathCacheForTests() => PathCache.Clear();
    internal static int PathCacheCountForTests => PathCache.Count;

    private static CachedPath BuildCachedPath(string property)
    {
        var parts = property.Split('.');
        if (parts.Length == 1)
        {
            return new CachedPath(PathNamespace.SourceOrVariables, [], SingleSegment: true);
        }

        if (KnownPrefixes.Contains(parts[0]))
        {
            var ns = parts[0].ToUpperInvariant() switch
            {
                "SOURCE" => PathNamespace.Source,
                "STATE" => PathNamespace.State,
                "GLOBAL" => PathNamespace.Global,
                "CHAIN" => PathNamespace.Chain,
                "PREVIOUS" => PathNamespace.Previous,
                "VARIABLES" => PathNamespace.Variables,
                _ => PathNamespace.Source,
            };
            return new CachedPath(ns, parts[1..], SingleSegment: false);
        }

        return new CachedPath(PathNamespace.Source, parts, SingleSegment: false);
    }

    private object? GetPropertyValue(string property, ExpressionContext context)
    {
        // Bounded cache: at the cap, compute fresh rather than Clear()
        // (which races with GetOrAdd under churn).
        CachedPath cached;
        if (PathCache.TryGetValue(property, out var hit))
        {
            cached = hit;
        }
        else if (PathCache.Count >= MaxCachedPaths)
        {
            cached = BuildCachedPath(property);
        }
        else
        {
            cached = PathCache.GetOrAdd(property, static p => BuildCachedPath(p));
        }

        if (cached.SingleSegment)
        {
            if (context.Source.TryGetValue(property, out var sourceVal)) return UnwrapJsonElement(sourceVal);
            if (context.Variables.TryGetValue(property, out var varVal)) return UnwrapJsonElement(varVal);
            return null;
        }

        var dict = SelectNamespace(cached.Namespace, context);
        return dict is null ? null : GetNestedValue(dict, cached.Segments);
    }

    private static Dictionary<string, object>? SelectNamespace(PathNamespace ns, ExpressionContext context) =>
        ns switch
        {
            PathNamespace.Source => context.Source,
            PathNamespace.State => context.State,
            PathNamespace.Global => context.Global,
            PathNamespace.Chain => context.ChainData,
            PathNamespace.Previous => context.Previous,
            PathNamespace.Variables => context.Variables,
            _ => null,
        };

    private static object? GetNestedValue(Dictionary<string, object> dict, string[] path)
    {
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
                    current = prop;
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

        return UnwrapJsonElement(current);
    }

    private static object? UnwrapJsonElement(object? value)
    {
        if (value is not JsonElement je) return value;
        return je.ValueKind switch
        {
            JsonValueKind.String => je.GetString(),
            JsonValueKind.Number when je.TryGetInt64(out var l) => l,
            JsonValueKind.Number when je.TryGetDouble(out var d) => d,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => je.ToString(),
        };
    }

    private static bool StringContains(object? left, object? right) =>
        left is not null && right is not null &&
        (left.ToString() ?? "").Contains(right.ToString() ?? "", StringCompare);

    private static bool StringStartsWith(object? left, object? right) =>
        left is not null && right is not null &&
        (left.ToString() ?? "").StartsWith(right.ToString() ?? "", StringCompare);

    private static bool StringEndsWith(object? left, object? right) =>
        left is not null && right is not null &&
        (left.ToString() ?? "").EndsWith(right.ToString() ?? "", StringCompare);

    // Compile cache: Regex.Compile dominates evaluator time under load.
    // Null entries cache fail-closed results for malformed patterns.
    private const int MaxCachedPatterns = 256;
    private static readonly ConcurrentDictionary<string, Lazy<Regex?>> PatternCache = new();

    private static Regex? GetOrCompileRegex(string pattern)
    {
        // Bounded cache: at the cap, compute fresh rather than Clear()
        // (refetch storms under churn).
        if (PatternCache.TryGetValue(pattern, out var cached))
        {
            return cached.Value;
        }
        if (PatternCache.Count >= MaxCachedPatterns)
        {
            return CompileRegex(pattern);
        }

        var lazy = PatternCache.GetOrAdd(pattern, static p => new Lazy<Regex?>(() =>
        {
            try
            {
                return new Regex(
                    p,
                    RegexOptions.IgnoreCase | RegexOptions.NonBacktracking | RegexOptions.CultureInvariant,
                    TimeSpan.FromMilliseconds(100));
            }
            catch (ArgumentException)
            {
                // Fail-closed: malformed pattern caches as null.
                return null;
            }
        }, LazyThreadSafetyMode.ExecutionAndPublication));

        return lazy.Value;
    }

    /// <summary>Overflow path: compile without caching.</summary>
    private static Regex? CompileRegex(string pattern)
    {
        try
        {
            return new Regex(
                pattern,
                RegexOptions.IgnoreCase | RegexOptions.NonBacktracking | RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(100));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    internal static void ResetPatternCacheForTests() => PatternCache.Clear();
    internal static int PatternCacheCountForTests => PatternCache.Count;

    private static bool MatchesPattern(object? left, object? right)
    {
        if (left is null || right is null) return false;

        var regex = GetOrCompileRegex(right.ToString() ?? "");
        if (regex is null) return false;

        try
        {
            return regex.IsMatch(left.ToString() ?? "");
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static bool IsInCollection(object? value, List<object>? collection)
    {
        if (value is null || collection is null || collection.Count == 0) return false;

        var valueStr = value.ToString();
        foreach (var item in collection)
        {
            if (string.Equals(valueStr, item?.ToString(), StringCompare)) return true;
        }
        return false;
    }

    private static bool IsBetween(object? value, List<object>? bounds)
    {
        if (value is null || bounds is null || bounds.Count < 2) return false;
        if (!NumericCoercion.TryToDouble(value, out var num)) return false;
        if (!NumericCoercion.TryToDouble(bounds[0], out var min) ||
            !NumericCoercion.TryToDouble(bounds[1], out var max)) return false;
        return num >= min && num <= max;
    }

    /// <summary>
    /// SEMANTIC LIMITATION (documented for rule authors): the `Changed`
    /// operator only detects transitions on <c>source.*</c> paths — the
    /// message payload. It CANNOT observe changes to <c>state.*</c>,
    /// <c>global.*</c>, or <c>variables.*</c> because those live in dicts
    /// that are not snapshotted per-message; the "previous" context
    /// reads the current namespace on both sides of the comparison,
    /// so LooseEquals always returns true → Changed always returns false.
    /// If you need to detect state changes, use a rule that writes a
    /// prior-value into source.* (via a Transform action) before the
    /// Changed check, or compare explicit expressions like
    /// <c>state.counter != state.counter_prev</c>.
    /// </summary>
    private bool HasChanged(string? property, ExpressionContext context)
    {
        if (string.IsNullOrEmpty(property) || context.Previous is null) return true;

        // Only the source dict is swapped; paths with explicit
        // namespace prefixes (global., state., variables.) will read
        // the current namespace on both sides — see xmldoc above.
        var currentValue = GetPropertyValue(property, context);
        var previousContext = context with { Source = context.Previous };
        var previousValue = GetPropertyValue(property, previousContext);

        return !NumericCoercion.LooseEquals(currentValue, previousValue, StringCompare);
    }

    private static bool IsTruthy(object? value) => value switch
    {
        null => false,
        bool b => b,
        string s => !string.IsNullOrEmpty(s) && !s.Equals("false", StringComparison.OrdinalIgnoreCase) && s != "0",
        int i => i != 0,
        long l => l != 0,
        double d => d != 0,
        float f => f != 0,
        decimal m => m != 0,
        _ => true,
    };
}
