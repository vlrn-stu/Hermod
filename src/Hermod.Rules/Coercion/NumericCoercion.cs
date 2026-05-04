using System.Globalization;

namespace Hermod.Rules.Coercion;

/// <summary>
/// Numeric parsing and loose-equality helpers used by the rules engine,
/// expression evaluator, and condition evaluator. Invariant culture is
/// always used so rule authors get consistent behaviour regardless of
/// the host's regional settings.
/// </summary>
public static class NumericCoercion
{
    private const double DefaultEpsilon = 0.0001;

    /// <summary>
    /// Attempts to convert <paramref name="value"/> to a <see cref="double"/> using
    /// invariant culture when the input is a string.
    /// </summary>
    /// <param name="value">Value to coerce; may be any numeric type, bool, string, or null.</param>
    /// <param name="result">Parsed double, or zero when the conversion fails.</param>
    /// <returns><c>true</c> when <paramref name="value"/> was interpreted as a number.</returns>
    public static bool TryToDouble(object? value, out double result)
    {
        switch (value)
        {
            case null:
                result = 0;
                return false;
            case double d:
                result = d;
                return true;
            case float f:
                result = f;
                return true;
            case int i:
                result = i;
                return true;
            case long l:
                result = l;
                return true;
            case short s:
                result = s;
                return true;
            case byte b:
                result = b;
                return true;
            case decimal dec:
                result = (double)dec;
                return true;
            case bool flag:
                result = flag ? 1 : 0;
                return true;
            default:
                return double.TryParse(
                    value.ToString(),
                    NumberStyles.Any,
                    CultureInfo.InvariantCulture,
                    out result);
        }
    }

    /// <summary>
    /// Coerces <paramref name="value"/> to a <see cref="double"/>, returning
    /// <paramref name="defaultValue"/> when coercion fails.
    /// </summary>
    public static double ToDoubleOrDefault(object? value, double defaultValue = 0) =>
        TryToDouble(value, out var result) ? result : defaultValue;

    /// <summary>
    /// Coerces <paramref name="value"/> to a 32-bit integer by truncation, or
    /// returns <c>null</c> when the value cannot be parsed as a number.
    /// </summary>
    public static int? ToInt(object? value) =>
        TryToDouble(value, out var result) ? (int)result : null;

    /// <summary>
    /// Equality that prefers numeric comparison when both sides parse as
    /// numbers (so <c>1 == 1.0</c> and <c>"42" == 42</c> hold) and falls
    /// back to string comparison using the requested
    /// <paramref name="stringComparison"/> otherwise.
    /// </summary>
    public static bool LooseEquals(
        object? a,
        object? b,
        StringComparison stringComparison = StringComparison.Ordinal,
        double epsilon = DefaultEpsilon)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;

        if (TryToDouble(a, out var aNum) && TryToDouble(b, out var bNum))
        {
            return Math.Abs(aNum - bNum) < epsilon;
        }

        return string.Equals(a.ToString(), b.ToString(), stringComparison);
    }

    /// <summary>
    /// Numeric-or-lexical compare. Returns a negative, zero, or positive
    /// integer following the <see cref="IComparable"/> convention. Null
    /// sorts before non-null.
    /// </summary>
    public static int Compare(
        object? a,
        object? b,
        StringComparison stringComparison = StringComparison.Ordinal)
    {
        if (a is null && b is null) return 0;
        if (a is null) return -1;
        if (b is null) return 1;

        if (TryToDouble(a, out var aNum) && TryToDouble(b, out var bNum))
        {
            return aNum.CompareTo(bNum);
        }

        return string.Compare(a.ToString(), b.ToString(), stringComparison);
    }
}
