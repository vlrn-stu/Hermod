using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Hermod.TestHarness;

/// <summary>
/// Collects test results and flushes them to JSON and CSV.
/// Every row carries a Claim tag so operators can trace a failure back to the
/// thesis property being validated (see docs/TESTING_METHODOLOGY.md section 6).
/// </summary>
public sealed class MeasurementCollector
{
    // Statuses that are valid per docs/TESTING_HARNESS.md section 3.1.
    // Any other string thrown in here is rejected at Record time so a
    // misnamed status cannot hide a broken test.
    private static readonly HashSet<string> AllowedStatuses = new(StringComparer.Ordinal)
    {
        "PASS", "FAIL", "NOT_IMPLEMENTED", "INFO", "ERROR"
    };

    private readonly ILogger<MeasurementCollector> _logger;
    private readonly ConcurrentQueue<TestResult> _results = new();

    public MeasurementCollector(ILogger<MeasurementCollector> logger)
    {
        _logger = logger;
    }

    public void Record(TestResult result)
    {
        if (!AllowedStatuses.Contains(result.Status))
        {
            throw new ArgumentException(
                $"TestResult.Status must be one of {string.Join(", ", AllowedStatuses)}, got '{result.Status}'",
                nameof(result));
        }

        _results.Enqueue(result);
        _logger.LogDebug(
            "[{Category}/{Claim}] {Name}: {Status} ({LatencyMs}ms)",
            result.Category, result.Claim, result.Name, result.Status, result.LatencyMs);
    }

    public int Count => _results.Count;

    public async Task SaveResultsAsync()
    {
        var resultsDir = Environment.GetEnvironmentVariable("HERMOD_RESULTS_DIR")
            ?? Path.Combine(AppContext.BaseDirectory, "results");
        Directory.CreateDirectory(resultsDir);

        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        var results = _results.ToArray();

        // JSON payload
        var jsonPath = Path.Combine(resultsDir, $"results_{timestamp}.json");
        var envelope = new
        {
            generatedAt = DateTimeOffset.UtcNow,
            environment = Environment.MachineName,
            gitSha = Environment.GetEnvironmentVariable("GIT_SHA"),
            totalCount = results.Length,
            byStatus = results.GroupBy(r => r.Status).ToDictionary(g => g.Key, g => g.Count()),
            results
        };
        var json = JsonSerializer.Serialize(envelope, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(jsonPath, json);

        // CSV payload: strict dialect. All fields pass through CsvEscape so
        // newlines, commas, and quotes inside Details cannot fracture rows.
        var csvPath = Path.Combine(resultsDir, $"results_{timestamp}.csv");
        var sb = new StringBuilder();
        sb.AppendLine("Category,Claim,Name,Status,LatencyMs,Rssi,Snr,CorrelationId,Details,Timestamp");
        foreach (var r in results)
        {
            sb.Append(CsvEscape(r.Category)).Append(',');
            sb.Append(CsvEscape(r.Claim)).Append(',');
            sb.Append(CsvEscape(r.Name)).Append(',');
            sb.Append(CsvEscape(r.Status)).Append(',');
            sb.Append(r.LatencyMs?.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) ?? "").Append(',');
            sb.Append(r.Rssi?.ToString() ?? "").Append(',');
            sb.Append(r.Snr?.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) ?? "").Append(',');
            sb.Append(CsvEscape(r.CorrelationId)).Append(',');
            sb.Append(CsvEscape(r.Details)).Append(',');
            sb.Append(r.Timestamp.ToString("O"));
            sb.AppendLine();
        }
        await File.WriteAllTextAsync(csvPath, sb.ToString());

        _logger.LogInformation("Saved {Count} results to {JsonPath} and {CsvPath}",
            results.Length, jsonPath, csvPath);
    }

    private static string CsvEscape(string? value)
    {
        if (value is null) return "";
        var needsQuoting = value.Contains('"') || value.Contains(',') || value.Contains('\n') || value.Contains('\r');
        if (!needsQuoting) return value;
        return "\"" + value.Replace("\"", "\"\"").Replace("\r", " ").Replace("\n", " ") + "\"";
    }
}

/// <summary>
/// One row in a test run. Claim must tie the result back to a thesis property
/// or research objective per docs/TESTING_METHODOLOGY.md. Use "N/A" for rows
/// that carry pure diagnostic data (environment snapshots etc.).
///
/// LatencySamples lets latency-producing phases attach the raw per-message
/// distribution to the summary row. The CSV writer keeps the aggregate
/// columns only (a distribution column would blow up CSV line length); the
/// full list lives in the JSON envelope for downstream chart code. Rows
/// that do not carry raw samples leave LatencySamples null, which the JSON
/// writer emits as null.
/// </summary>
public sealed class TestResult
{
    public required string Category { get; init; }
    public required string Claim { get; init; }
    public required string Name { get; init; }
    public required string Status { get; init; }
    public double? LatencyMs { get; init; }
    public int? Rssi { get; init; }
    public double? Snr { get; init; }
    public string? Details { get; init; }
    public string? CorrelationId { get; init; }

    /// <summary>
    /// Optional raw per-message latency samples (milliseconds) that produced
    /// the aggregate in <see cref="LatencyMs"/>. When populated, chart code
    /// can recompute percentiles and draw full distributions. JSON-only; the
    /// CSV writer does not emit this field.
    /// </summary>
    public IReadOnlyList<double>? LatencySamples { get; init; }

    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}
