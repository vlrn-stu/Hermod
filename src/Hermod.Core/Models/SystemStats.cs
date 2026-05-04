namespace Hermod.Core.Models;

/// <summary>
/// Aggregated coordinator counters and inventory. Populated live by the
/// stats service and persisted as snapshots for history.
/// </summary>
public class SystemStats
{
    /// <summary>Total registered devices across all protocols.</summary>
    public int TotalDevices { get; set; }

    /// <summary>Devices currently reporting <see cref="DeviceStatus.Online"/>.</summary>
    public int OnlineDevices { get; set; }

    /// <summary>Rules with <c>Enabled = true</c>.</summary>
    public int ActiveRules { get; set; }

    /// <summary>Lifetime count of MQTT messages successfully processed.</summary>
    public long MessagesProcessed { get; set; }

    /// <summary>Lifetime count of rule firings that completed successfully.</summary>
    public long RulesExecuted { get; set; }

    /// <summary>
    /// Messages dropped at an intake stage, typically because a bounded channel
    /// was full (backpressure). Mirrors the warn-level drop log so dashboards
    /// can surface the same signal.
    /// </summary>
    public long MessagesDropped { get; set; }

    /// <summary>Rules whose evaluation or action dispatch raised an exception.</summary>
    public long RulesErrored { get; set; }

    /// <summary>Individual rule actions that returned a non-success result (failed publish, webhook 500, etc.).</summary>
    public long ActionsErrored { get; set; }

    /// <summary>Per-protocol device-count breakdown.</summary>
    public Dictionary<Protocol, int> DevicesByProtocol { get; set; } = new();

    /// <summary>Timestamp at which this snapshot was built.</summary>
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;

    /// <summary>Process uptime at snapshot time.</summary>
    public TimeSpan Uptime { get; set; }

    /// <summary>
    /// Lifetime average: total messages processed divided by process
    /// uptime. Becomes misleading after a restart because the counter is
    /// seeded from the persisted total while uptime restarts at zero —
    /// dashboards should prefer <see cref="MessagesPerSecond1m"/>.
    /// </summary>
    public double MessagesPerSecond { get; set; }

    /// <summary>Sliding-window rate over the last minute. Safe across restarts.</summary>
    public double MessagesPerSecond1m { get; set; }

    /// <summary>Sliding-window rate over the last five minutes.</summary>
    public double MessagesPerSecond5m { get; set; }

    /// <summary>Sliding-window rate over the last hour.</summary>
    public double MessagesPerSecond1h { get; set; }

    /// <summary>Lifetime ingress messages rejected by the per-topic token bucket. Surfaced for the dashboard's red "rate-limited" line.</summary>
    public long TopicLimitedRate { get; set; }

    /// <summary>Lifetime ingress messages rejected as in-window payload replays. Surfaced for the dashboard's orange "deduped" line.</summary>
    public long TopicLimitedDedup { get; set; }

    /// <summary>Aggregate count of inbound messages dropped by the per-protocol limiter across every protocol.</summary>
    public long ProtocolLimitedIngressTotal { get; set; }

    /// <summary>Aggregate count of outbound publishes dropped by the per-protocol limiter across every protocol.</summary>
    public long ProtocolLimitedEgressTotal { get; set; }

    /// <summary>Single counter incremented on every rate-type drop (per-topic rate + per-protocol ingress + per-protocol egress). The dashboard red line reads this so it can't desync from the underlying limiters.</summary>
    public long RateLimitedTotal { get; set; }
}

/// <summary>Per-protocol rollup surfaced alongside <see cref="SystemStats"/>.</summary>
public class ProtocolStats
{
    /// <summary>Protocol this row describes.</summary>
    public Protocol Protocol { get; set; }

    /// <summary>Device count reported for the protocol.</summary>
    public int DeviceCount { get; set; }

    /// <summary>Lifetime messages observed on this protocol.</summary>
    public long MessageCount { get; set; }

    /// <summary>True if the upstream translator is reachable.</summary>
    public bool TranslatorOnline { get; set; }

    /// <summary>Timestamp of the most recent message on this protocol.</summary>
    public DateTime LastActivity { get; set; }
}
