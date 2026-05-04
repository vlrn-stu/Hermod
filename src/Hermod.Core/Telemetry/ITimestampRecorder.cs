namespace Hermod.Core.Telemetry;

/// <summary>
/// Per-message timestamp sink for W-measurement matrix runs. Call sites
/// in the ingest and action-publish hot paths stamp
/// <c>(uuid, stage, ts_ns)</c> tuples; the implementation is responsible
/// for buffering and flushing to <c>tests/results/&lt;run&gt;/timestamps.csv</c>.
/// A null-object implementation is bound in DI when no path is configured,
/// so hot-path callers never need a null check.
/// </summary>
public interface ITimestampRecorder
{
    /// <summary>
    /// Records one stage observation for the given trace uuid. Must be
    /// safe to call concurrently from any thread. Drops silently if the
    /// buffer is full; the recorder is best-effort observability, not a
    /// reliability surface.
    /// </summary>
    /// <param name="uuid">Source trace uuid from the inbound payload.</param>
    /// <param name="stage">Canonical stage label (publish_tx, broker_rx, rule_eval_done, action_publish).</param>
    /// <param name="timestampNs">Wall-clock ns (<see cref="System.Diagnostics.Stopwatch.GetTimestamp"/> or <c>DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()*1e6</c>).</param>
    void Record(string uuid, string stage, long timestampNs);
}
