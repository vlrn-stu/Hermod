using System.Globalization;
using System.Text.RegularExpressions;

namespace Hermod.Core;

/// <summary>
/// Parses compact duration strings used by rule authors (<c>"5s"</c>, <c>"2m"</c>,
/// <c>"1h"</c>) with an ISO-like <see cref="TimeSpan"/> fallback (<c>"00:01:30"</c>).
/// Empty input returns <see cref="TimeSpan.Zero"/>; unparseable input throws
/// <see cref="ArgumentException"/>.
/// </summary>
public static partial class DelayParser
{
    [GeneratedRegex(@"^(\d+)(ms|s|m|h|d)$", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex DelayPatternRegex();

    /// <summary>
    /// Parses <paramref name="delay"/> into a <see cref="TimeSpan"/>. Accepts the compact
    /// <c>Nunit</c> form (<c>ms</c>/<c>s</c>/<c>m</c>/<c>h</c>/<c>d</c>) and, as a fallback,
    /// standard <see cref="TimeSpan"/> parsing.
    /// </summary>
    /// <param name="delay">Duration string such as <c>"5s"</c>, <c>"2m"</c>, or <c>"00:01:30"</c>.</param>
    /// <returns><see cref="TimeSpan.Zero"/> when <paramref name="delay"/> is null/whitespace; otherwise the parsed duration.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="delay"/> matches neither the compact form nor <see cref="TimeSpan"/>.</exception>
    public static TimeSpan Parse(string delay)
    {
        if (string.IsNullOrWhiteSpace(delay)) return TimeSpan.Zero;

        var match = DelayPatternRegex().Match(delay.Trim());
        if (!match.Success)
        {
            if (TimeSpan.TryParse(delay, out var ts)) return ts;
            throw new ArgumentException($"Invalid delay format: {delay}", nameof(delay));
        }

        var value = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        return match.Groups[2].Value.ToUpperInvariant() switch
        {
            "MS" => TimeSpan.FromMilliseconds(value),
            "S" => TimeSpan.FromSeconds(value),
            "M" => TimeSpan.FromMinutes(value),
            "H" => TimeSpan.FromHours(value),
            "D" => TimeSpan.FromDays(value),
            _ => throw new ArgumentException("Unknown time unit", nameof(delay)),
        };
    }
}
