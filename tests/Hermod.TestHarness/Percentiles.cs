namespace Hermod.TestHarness;

/// <summary>
/// Percentile helper. Per docs/TESTING_HARNESS.md section 3.3:
///
/// - Returns null for sample sizes below MinSamplesForPercentiles.
/// - Linear interpolation between adjacent samples.
/// - Always sorts a defensive copy; never mutates caller's list.
/// </summary>
public static class Percentiles
{
    public const int MinSamplesForPercentiles = 60;

    public static double? Compute(IReadOnlyList<double> samples, double percentile)
    {
        if (samples.Count < MinSamplesForPercentiles)
            return null;
        if (percentile < 0 || percentile > 1)
            throw new ArgumentOutOfRangeException(nameof(percentile), "Must be in [0, 1]");

        var sorted = samples.ToArray();
        Array.Sort(sorted);

        var rank = percentile * (sorted.Length - 1);
        var lo = (int)Math.Floor(rank);
        var hi = (int)Math.Ceiling(rank);
        if (lo == hi) return sorted[lo];

        var fraction = rank - lo;
        return sorted[lo] + fraction * (sorted[hi] - sorted[lo]);
    }

    public static PercentileSummary? Summarize(IReadOnlyList<double> samples)
    {
        if (samples.Count < MinSamplesForPercentiles)
            return null;

        return new PercentileSummary
        {
            SampleCount = samples.Count,
            Min = samples.Min(),
            P50 = Compute(samples, 0.50)!.Value,
            P95 = Compute(samples, 0.95)!.Value,
            P99 = Compute(samples, 0.99)!.Value,
            Max = samples.Max(),
            Mean = samples.Average()
        };
    }
}

public sealed class PercentileSummary
{
    public int SampleCount { get; init; }
    public double Min { get; init; }
    public double P50 { get; init; }
    public double P95 { get; init; }
    public double P99 { get; init; }
    public double Max { get; init; }
    public double Mean { get; init; }

    public override string ToString() =>
        $"n={SampleCount}, min={Min:F1}ms, p50={P50:F1}ms, p95={P95:F1}ms, p99={P99:F1}ms, max={Max:F1}ms, mean={Mean:F1}ms";
}
