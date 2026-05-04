using System.Globalization;
using System.Text;
using Hermod.Core.Models;

namespace Hermod.Core.Telemetry;

/// <summary>
/// Lock-free counters, gauges, and one latency histogram exposed in
/// Prometheus text exposition format. Pairs with the <c>/metrics</c>
/// endpoint gated by <c>Hermod:Features:MetricsEndpoint</c>. Each
/// increment is a single <see cref="Interlocked"/> op so hot paths can
/// call into this freely.
/// </summary>
public sealed class HermodMetrics
{
    private long _messagesIngested;
    private long _messagesDropped;
    private long _deviceStateWrites;
    private long _deviceStateWriteDropped;
    private long _deviceStateFlushFailed;
    private long _messagePersistenceWrites;
    private long _messagePersistenceDropped;
    private long _messagePersistenceFlushFailed;
    private long _messageHistoryRetentionDeletes;
    private long _messageHistoryRetentionSweepFailed;
    private long _ruleCacheHits;
    private long _ruleCacheMisses;
    private long _ruleAuditWrites;
    private long _ruleAuditDropped;
    private long _ruleAuditFlushFailed;
    private long _statsRollupWrites;
    private long _mqttOutboxEnqueued;
    private long _mqttOutboxDropped;
    private long _mqttOutboxDrained;
    private long _mqttReconnects;
    private long _rulePublishAttempted;
    private long _rulePublishFailed;
    private long _topicLimitedRate;
    private long _topicLimitedDedup;
    // Unified "rate-limited anything" counter. Bumped on every rate-type
    // drop (per-topic rate, per-protocol ingress, per-protocol egress) so
    // the dashboard's red line has a single source of truth instead of
    // summing three separate atomics. Dedup stays out — that's a distinct
    // failure mode (replay, not rate) and gets its own orange line.
    private long _rateLimitedTotal;

    // One slot per Protocol enum value, indexed by (int)Protocol. Lock-free
    // increments via Interlocked; the labeled Prometheus render walks the
    // array. Skipping the dictionary keeps the egress path allocation-free.
    private readonly long[] _protocolLimitedIngress = new long[Enum.GetValues<Protocol>().Length];
    private readonly long[] _protocolLimitedEgress = new long[Enum.GetValues<Protocol>().Length];

    // Histogram state for rule evaluation latency. One atomic increment
    // per observation: the observed value lands in the smallest bucket
    // whose upper bound covers it (exclusive of smaller buckets); the
    // cumulative counts needed by Prometheus are reconstructed at render
    // time so write cost stays O(log n).
    private static readonly double[] RuleEvalBucketsSeconds =
    {
        0.0005, 0.001, 0.002, 0.005, 0.01, 0.025, 0.05,
        0.1, 0.25, 0.5, 1.0, 2.5, 5.0, 10.0,
    };
    private readonly long[] _ruleEvalBucketCounts = new long[RuleEvalBucketsSeconds.Length + 1];
    private long _ruleEvalObservations;
    private long _ruleEvalSumMicroseconds;

    /// <summary>
    /// Caller-supplied probe for the MQTT reconnect outbox depth. Set once
    /// by <c>MqttService</c>; read lazily on each <see cref="Render"/> call so
    /// the registry doesn't need to know about the channel internals.
    /// </summary>
    public Func<int>? MqttOutboxDepthProvider { get; set; }

    /// <summary>
    /// Probe for the message-ingest channel depth. Wired by
    /// <c>MessageProcessor</c>. Exposing it as a gauge lets the harness
    /// watch saturation arrive before drops start — the drop counter is
    /// still the authoritative loss signal, this is for predictive
    /// backpressure insight.
    /// </summary>
    public Func<int>? IngestQueueDepthProvider { get; set; }

    /// <summary>Counts one inbound MQTT message observed by the coordinator.</summary>
    public void IncMessagesIngested() => Interlocked.Increment(ref _messagesIngested);

    /// <summary>Counts one message dropped from the ingest channel under DropOldest overflow.
    /// Mirrors <c>StatsService._messagesDropped</c> so the Prometheus side sees it too — the
    /// dashboard reads that atomic through <c>/api/stats</c>, the scrape harness reads this.</summary>
    public void IncMessagesDropped() => Interlocked.Increment(ref _messagesDropped);

    /// <summary>Counts one write to <c>devices.state</c>.</summary>
    public void IncDeviceStateWrites() => Interlocked.Increment(ref _deviceStateWrites);

    /// <summary>Counts one device-state update evicted from the batched writer's DropOldest
    /// channel. Silent overflow here masked real load in prior runs — any non-zero value
    /// means the writer channel is saturating faster than Postgres can flush.</summary>
    public void IncDeviceStateWriteDropped() => Interlocked.Increment(ref _deviceStateWriteDropped);

    /// <summary>Counts one device-state batch that failed to flush to Postgres (entire batch
    /// discarded). Non-zero means PG is unreachable or errored on a batch — rows were lost
    /// with no queue overflow signal.</summary>
    public void IncDeviceStateFlushFailed() => Interlocked.Increment(ref _deviceStateFlushFailed);

    /// <summary>Counts one row actually written to the <c>message_history</c> table (post-flush).
    /// Previously this counter was bumped at enqueue time, which masked flush failures —
    /// a dropped batch still incremented writes. Now counter reflects successful DB writes only.</summary>
    public void IncMessagePersistenceWrites() => Interlocked.Increment(ref _messagePersistenceWrites);

    /// <summary>Post-flush bulk increment. One <see cref="Interlocked.Add(ref long, long)"/>
    /// for the whole batch instead of N per-row <see cref="Interlocked.Increment(ref long)"/>s.
    /// At batch=512 saves 511 atomic ops per flush.</summary>
    public void AddMessagePersistenceWrites(int count) => Interlocked.Add(ref _messagePersistenceWrites, count);

    /// <summary>Counts one row evicted from the message_history batched writer under DropOldest.</summary>
    public void IncMessagePersistenceDropped() => Interlocked.Increment(ref _messagePersistenceDropped);

    /// <summary>Counts one message_history batch that failed to flush to Postgres.</summary>
    public void IncMessagePersistenceFlushFailed() => Interlocked.Increment(ref _messagePersistenceFlushFailed);

    /// <summary>Bulk-adds rows deleted by the message_history retention sweeper. One Add per
    /// batch (500 rows by default) instead of N Increments.</summary>
    public void AddMessageHistoryRetentionDeletes(long count) => Interlocked.Add(ref _messageHistoryRetentionDeletes, count);

    /// <summary>Counts one retention sweep cycle that failed (transient pg outage etc.).</summary>
    public void IncMessageHistoryRetentionSweepFailed() => Interlocked.Increment(ref _messageHistoryRetentionSweepFailed);

    /// <summary>Counts one rule index lookup served from the in-memory cache.</summary>
    public void IncRuleCacheHits() => Interlocked.Increment(ref _ruleCacheHits);

    /// <summary>Counts one rule index lookup that missed the cache and hit Postgres.</summary>
    public void IncRuleCacheMisses() => Interlocked.Increment(ref _ruleCacheMisses);

    /// <summary>Counts one row actually written to <c>rule_audit_log</c> (post-flush). Same
    /// fix as IncMessagePersistenceWrites — was counting enqueues, masking flush failures.</summary>
    public void IncRuleAuditWrites() => Interlocked.Increment(ref _ruleAuditWrites);

    /// <summary>Post-flush bulk increment; see <see cref="AddMessagePersistenceWrites"/>.</summary>
    public void AddRuleAuditWrites(int count) => Interlocked.Add(ref _ruleAuditWrites, count);

    /// <summary>Counts one row evicted from the rule_audit_log batched writer under DropOldest.</summary>
    public void IncRuleAuditDropped() => Interlocked.Increment(ref _ruleAuditDropped);

    /// <summary>Counts one rule_audit_log batch that failed to flush to Postgres.</summary>
    public void IncRuleAuditFlushFailed() => Interlocked.Increment(ref _ruleAuditFlushFailed);

    /// <summary>Counts one counter/snapshot flush.</summary>
    public void IncStatsRollupWrites() => Interlocked.Increment(ref _statsRollupWrites);

    /// <summary>Counts one publish buffered because the MQTT client was disconnected.</summary>
    public void IncMqttOutboxEnqueued() => Interlocked.Increment(ref _mqttOutboxEnqueued);

    /// <summary>Counts one publish evicted from the outbox under DropOldest overflow.</summary>
    public void IncMqttOutboxDropped() => Interlocked.Increment(ref _mqttOutboxDropped);

    /// <summary>Counts one publish replayed from the outbox on reconnect.</summary>
    public void IncMqttOutboxDrained() => Interlocked.Increment(ref _mqttOutboxDrained);

    /// <summary>Counts one successful MQTT broker reconnect by the supervisor loop.</summary>
    public void IncMqttReconnects() => Interlocked.Increment(ref _mqttReconnects);

    /// <summary>Counts one rule action attempting an MQTT publish. Closes
    /// the observability gap between "a rule matched" and "the action
    /// publish actually went out" — the bench surfaced silent failures
    /// in that segment that no existing counter caught.</summary>
    public void IncRulePublishAttempted() => Interlocked.Increment(ref _rulePublishAttempted);

    /// <summary>Counts one rule action whose publish threw or was dropped.
    /// Subtract from <see cref="IncRulePublishAttempted"/> for success rate.</summary>
    public void IncRulePublishFailed() => Interlocked.Increment(ref _rulePublishFailed);

    /// <summary>Counts one ingress message rejected by the per-topic
    /// token-bucket rate limit (sustained-rate cap reached). Pair with
    /// <see cref="IncTopicLimitedDedup"/> to tell flood from replay.</summary>
    public void IncTopicLimitedRate() => Interlocked.Increment(ref _topicLimitedRate);

    /// <summary>Counts one ingress message rejected because an exact
    /// payload copy arrived on the same topic inside the dedup window.</summary>
    public void IncTopicLimitedDedup() => Interlocked.Increment(ref _topicLimitedDedup);

    /// <summary>Lifetime total of ingress messages rejected by the per-topic token bucket.</summary>
    public long TopicLimitedRate => Interlocked.Read(ref _topicLimitedRate);

    /// <summary>Lifetime total of ingress messages rejected as in-window payload replays.</summary>
    public long TopicLimitedDedup => Interlocked.Read(ref _topicLimitedDedup);

    /// <summary>Counts one ingress message rejected by the per-protocol aggregate limiter.</summary>
    public void IncProtocolLimitedIngress(Protocol protocol)
    {
        var idx = (int)protocol;
        if ((uint)idx >= (uint)_protocolLimitedIngress.Length) return;
        Interlocked.Increment(ref _protocolLimitedIngress[idx]);
    }

    /// <summary>Counts one egress publish rejected by the per-protocol aggregate limiter.</summary>
    public void IncProtocolLimitedEgress(Protocol protocol)
    {
        var idx = (int)protocol;
        if ((uint)idx >= (uint)_protocolLimitedEgress.Length) return;
        Interlocked.Increment(ref _protocolLimitedEgress[idx]);
    }

    /// <summary>Aggregate count across all protocols of ingress messages dropped by the protocol-flow limiter. Surfaced for the dashboard.</summary>
    public long ProtocolLimitedIngressTotal
    {
        get
        {
            long sum = 0;
            for (var i = 0; i < _protocolLimitedIngress.Length; i++)
                sum += Interlocked.Read(ref _protocolLimitedIngress[i]);
            return sum;
        }
    }

    /// <summary>Aggregate count across all protocols of egress publishes dropped by the protocol-flow limiter. Surfaced for the dashboard.</summary>
    public long ProtocolLimitedEgressTotal
    {
        get
        {
            long sum = 0;
            for (var i = 0; i < _protocolLimitedEgress.Length; i++)
                sum += Interlocked.Read(ref _protocolLimitedEgress[i]);
            return sum;
        }
    }

    /// <summary>Counts one rate-type rejection across any limiter (per-topic rate, per-protocol ingress, per-protocol egress). Dedup is excluded — see <see cref="IncTopicLimitedDedup"/>.</summary>
    public void IncRateLimitedTotal() => Interlocked.Increment(ref _rateLimitedTotal);

    /// <summary>Lifetime total of rate-type rejections across every limiter. The dashboard's red line reads this directly.</summary>
    public long RateLimitedTotal => Interlocked.Read(ref _rateLimitedTotal);

    /// <summary>
    /// Zeroes every counter and histogram state. Paired with
    /// <c>StatsService.ResetCountersAsync</c> so <c>/metrics</c> and
    /// <c>/api/stats</c> stay aligned after an operator-triggered reset —
    /// before this existed, the two atomics diverged permanently on reset
    /// because <c>/metrics</c> was never touched.
    /// Technically breaks Prometheus "counters only increase" for <c>rate()</c>
    /// windows that straddle the reset, but this is explicit operator
    /// intent and matches pod-restart semantics.
    /// </summary>
    public void ResetAll()
    {
        Interlocked.Exchange(ref _messagesIngested, 0);
        Interlocked.Exchange(ref _messagesDropped, 0);
        Interlocked.Exchange(ref _deviceStateWrites, 0);
        Interlocked.Exchange(ref _deviceStateWriteDropped, 0);
        Interlocked.Exchange(ref _deviceStateFlushFailed, 0);
        Interlocked.Exchange(ref _messagePersistenceWrites, 0);
        Interlocked.Exchange(ref _messagePersistenceDropped, 0);
        Interlocked.Exchange(ref _messagePersistenceFlushFailed, 0);
        Interlocked.Exchange(ref _messageHistoryRetentionDeletes, 0);
        Interlocked.Exchange(ref _messageHistoryRetentionSweepFailed, 0);
        Interlocked.Exchange(ref _ruleCacheHits, 0);
        Interlocked.Exchange(ref _ruleCacheMisses, 0);
        Interlocked.Exchange(ref _ruleAuditWrites, 0);
        Interlocked.Exchange(ref _ruleAuditDropped, 0);
        Interlocked.Exchange(ref _ruleAuditFlushFailed, 0);
        Interlocked.Exchange(ref _statsRollupWrites, 0);
        Interlocked.Exchange(ref _mqttOutboxEnqueued, 0);
        Interlocked.Exchange(ref _mqttOutboxDropped, 0);
        Interlocked.Exchange(ref _mqttOutboxDrained, 0);
        Interlocked.Exchange(ref _mqttReconnects, 0);
        Interlocked.Exchange(ref _rulePublishAttempted, 0);
        Interlocked.Exchange(ref _rulePublishFailed, 0);
        Interlocked.Exchange(ref _topicLimitedRate, 0);
        Interlocked.Exchange(ref _topicLimitedDedup, 0);
        Interlocked.Exchange(ref _rateLimitedTotal, 0);
        for (var i = 0; i < _protocolLimitedIngress.Length; i++)
            Interlocked.Exchange(ref _protocolLimitedIngress[i], 0);
        for (var i = 0; i < _protocolLimitedEgress.Length; i++)
            Interlocked.Exchange(ref _protocolLimitedEgress[i], 0);
        Interlocked.Exchange(ref _ruleEvalObservations, 0);
        Interlocked.Exchange(ref _ruleEvalSumMicroseconds, 0);
        for (var i = 0; i < _ruleEvalBucketCounts.Length; i++)
        {
            Interlocked.Exchange(ref _ruleEvalBucketCounts[i], 0);
        }
    }

    /// <summary>Records a rule-evaluation latency observation for the histogram.</summary>
    /// <param name="seconds">Latency in seconds.</param>
    public void ObserveRuleEvalSeconds(double seconds)
    {
        Interlocked.Increment(ref _ruleEvalObservations);
        Interlocked.Add(ref _ruleEvalSumMicroseconds, (long)(seconds * 1_000_000));

        var bucketIndex = _ruleEvalBucketCounts.Length - 1;
        for (var i = 0; i < RuleEvalBucketsSeconds.Length; i++)
        {
            if (seconds <= RuleEvalBucketsSeconds[i])
            {
                bucketIndex = i;
                break;
            }
        }
        Interlocked.Increment(ref _ruleEvalBucketCounts[bucketIndex]);
    }

    /// <summary>Renders every counter, gauge, and the histogram in Prometheus text exposition format.</summary>
    /// <returns>A newline-delimited <c># HELP / # TYPE / value</c> document.</returns>
    public string Render()
    {
        var sb = new StringBuilder(4096);
        AppendCounter(sb, "hermod_messages_ingested_total",
            "MQTT messages received by the coordinator (feature-flag independent).",
            Interlocked.Read(ref _messagesIngested));
        AppendCounter(sb, "hermod_messages_dropped_total",
            "Messages evicted from the ingest channel under DropOldest overflow. Mirror of SystemStats.MessagesDropped.",
            Interlocked.Read(ref _messagesDropped));
        AppendCounter(sb, "hermod_device_state_writes_total",
            "Device state rows written to Postgres after MQTT telemetry.",
            Interlocked.Read(ref _deviceStateWrites));
        AppendCounter(sb, "hermod_device_state_writes_dropped_total",
            "Device state updates evicted from the batched writer channel (pre-flush). Non-zero means the writer cannot keep up with incoming upserts.",
            Interlocked.Read(ref _deviceStateWriteDropped));
        AppendCounter(sb, "hermod_device_state_flush_failed_total",
            "Device-state batches that failed to flush to Postgres (entire batch lost). Non-zero = PG is unreachable or the INSERT errored; silently dropped rows with no queue-overflow signal.",
            Interlocked.Read(ref _deviceStateFlushFailed));
        AppendCounter(sb, "hermod_message_persistence_writes_total",
            "Rows actually written to message_history (post-flush). Previously counted at enqueue time which masked flush failures.",
            Interlocked.Read(ref _messagePersistenceWrites));
        AppendCounter(sb, "hermod_message_persistence_dropped_total",
            "Rows evicted from the message_history writer channel under DropOldest.",
            Interlocked.Read(ref _messagePersistenceDropped));
        AppendCounter(sb, "hermod_message_persistence_flush_failed_total",
            "message_history batches that failed to flush to Postgres (entire batch lost).",
            Interlocked.Read(ref _messagePersistenceFlushFailed));
        AppendCounter(sb, "hermod_message_history_retention_deletes_total",
            "Rows deleted by the message_history retention sweeper (post-cutoff age-out, batched DELETE).",
            Interlocked.Read(ref _messageHistoryRetentionDeletes));
        AppendCounter(sb, "hermod_message_history_retention_sweep_failed_total",
            "Retention sweep cycles that errored (transient PG outage; next interval retries).",
            Interlocked.Read(ref _messageHistoryRetentionSweepFailed));
        AppendCounter(sb, "hermod_rule_cache_hits_total",
            "Rule index lookups served from the in-memory cache.",
            Interlocked.Read(ref _ruleCacheHits));
        AppendCounter(sb, "hermod_rule_cache_misses_total",
            "Rule index lookups that had to refetch from Postgres.",
            Interlocked.Read(ref _ruleCacheMisses));
        AppendCounter(sb, "hermod_rule_audit_writes_total",
            "Rows enqueued to the rule_audit_log batched writer.",
            Interlocked.Read(ref _ruleAuditWrites));
        AppendCounter(sb, "hermod_rule_audit_dropped_total",
            "Rows evicted from the rule_audit_log writer channel under DropOldest.",
            Interlocked.Read(ref _ruleAuditDropped));
        AppendCounter(sb, "hermod_rule_audit_flush_failed_total",
            "rule_audit_log batches that failed to flush to Postgres (entire batch lost).",
            Interlocked.Read(ref _ruleAuditFlushFailed));
        AppendCounter(sb, "hermod_stats_rollup_writes_total",
            "Counter/snapshot flushes to metrics_counters and metrics_snapshots.",
            Interlocked.Read(ref _statsRollupWrites));
        AppendCounter(sb, "hermod_mqtt_outbox_enqueued_total",
            "Publishes buffered because the MQTT client was disconnected.",
            Interlocked.Read(ref _mqttOutboxEnqueued));
        AppendCounter(sb, "hermod_mqtt_outbox_dropped_total",
            "Publishes evicted from the outbox under DropOldest overflow.",
            Interlocked.Read(ref _mqttOutboxDropped));
        AppendCounter(sb, "hermod_mqtt_outbox_drained_total",
            "Publishes replayed from the outbox on reconnect.",
            Interlocked.Read(ref _mqttOutboxDrained));
        AppendCounter(sb, "hermod_mqtt_reconnects_total",
            "Successful broker reconnects by the MqttService supervisor (a flaky broker shows here as a rising slope).",
            Interlocked.Read(ref _mqttReconnects));
        AppendCounter(sb, "hermod_rule_publish_attempted_total",
            "Rule actions that attempted an MQTT publish.",
            Interlocked.Read(ref _rulePublishAttempted));
        AppendCounter(sb, "hermod_rule_publish_failed_total",
            "Rule actions whose publish threw or was dropped (bounded-channel overflow, MQTT disconnect mid-publish, serializer failure).",
            Interlocked.Read(ref _rulePublishFailed));
        AppendCounter(sb, "hermod_topic_limited_rate_total",
            "Ingress messages rejected by the per-topic token-bucket rate limiter (sustained-rate cap reached). Non-zero under attack scenarios.",
            Interlocked.Read(ref _topicLimitedRate));
        AppendCounter(sb, "hermod_topic_limited_dedup_total",
            "Ingress messages rejected as exact-payload duplicates within the dedup window (replay attack signal).",
            Interlocked.Read(ref _topicLimitedDedup));
        AppendCounter(sb, "hermod_rate_limited_total",
            "Aggregate count of rate-type rejections (per-topic rate + per-protocol ingress + per-protocol egress) — feeds the dashboard red line.",
            Interlocked.Read(ref _rateLimitedTotal));

        // Per-protocol bidirectional aggregate limiter — labeled with the
        // protocol so dashboards can split "all bluetooth blocked" from
        // "all zigbee blocked" at scrape time. Prometheus conventions
        // require lowercase label values; ToUpperInvariant would break
        // Grafana queries that assume the canonical lower-case form.
#pragma warning disable CA1308 // Lowercase required for Prometheus label conformance.
        sb.Append("# HELP hermod_protocol_limited_ingress_total Ingress messages rejected by the per-protocol aggregate limiter, by protocol.\n");
        sb.Append("# TYPE hermod_protocol_limited_ingress_total counter\n");
        foreach (Protocol p in Enum.GetValues<Protocol>())
        {
            sb.Append("hermod_protocol_limited_ingress_total{protocol=\"")
              .Append(p.ToString().ToLowerInvariant())
              .Append("\"} ")
              .Append(Interlocked.Read(ref _protocolLimitedIngress[(int)p]).ToString(CultureInfo.InvariantCulture))
              .Append('\n');
        }
        sb.Append("# HELP hermod_protocol_limited_egress_total Egress publishes rejected by the per-protocol aggregate limiter, by protocol.\n");
        sb.Append("# TYPE hermod_protocol_limited_egress_total counter\n");
        foreach (Protocol p in Enum.GetValues<Protocol>())
        {
            sb.Append("hermod_protocol_limited_egress_total{protocol=\"")
              .Append(p.ToString().ToLowerInvariant())
              .Append("\"} ")
              .Append(Interlocked.Read(ref _protocolLimitedEgress[(int)p]).ToString(CultureInfo.InvariantCulture))
              .Append('\n');
        }
#pragma warning restore CA1308

        var depth = MqttOutboxDepthProvider?.Invoke() ?? 0;
        AppendGauge(sb, "hermod_mqtt_outbox_depth",
            "Current number of publishes buffered in the MQTT reconnect outbox.",
            depth);

        var ingestDepth = IngestQueueDepthProvider?.Invoke() ?? 0;
        AppendGauge(sb, "hermod_ingest_queue_depth",
            "Current number of messages queued in the ingest channel (saturation arrives before drops).",
            ingestDepth);

        AppendRuleEvalHistogram(sb);
        return sb.ToString();
    }

    private static void AppendCounter(StringBuilder sb, string name, string help, long value)
    {
        sb.Append("# HELP ").Append(name).Append(' ').Append(help).Append('\n');
        sb.Append("# TYPE ").Append(name).Append(" counter\n");
        sb.Append(name).Append(' ').Append(value.ToString(CultureInfo.InvariantCulture)).Append('\n');
    }

    private static void AppendGauge(StringBuilder sb, string name, string help, long value)
    {
        sb.Append("# HELP ").Append(name).Append(' ').Append(help).Append('\n');
        sb.Append("# TYPE ").Append(name).Append(" gauge\n");
        sb.Append(name).Append(' ').Append(value.ToString(CultureInfo.InvariantCulture)).Append('\n');
    }

    private void AppendRuleEvalHistogram(StringBuilder sb)
    {
        const string name = "hermod_rule_eval_seconds";
        sb.Append("# HELP ").Append(name).Append(" Rule evaluation latency in seconds.\n");
        sb.Append("# TYPE ").Append(name).Append(" histogram\n");

        long cumulative = 0;
        for (var i = 0; i < RuleEvalBucketsSeconds.Length; i++)
        {
            cumulative += Interlocked.Read(ref _ruleEvalBucketCounts[i]);
            sb.Append(name).Append("_bucket{le=\"")
                .Append(RuleEvalBucketsSeconds[i].ToString("0.######", CultureInfo.InvariantCulture))
                .Append("\"} ")
                .Append(cumulative.ToString(CultureInfo.InvariantCulture))
                .Append('\n');
        }
        cumulative += Interlocked.Read(ref _ruleEvalBucketCounts[^1]);
        sb.Append(name).Append("_bucket{le=\"+Inf\"} ")
            .Append(cumulative.ToString(CultureInfo.InvariantCulture))
            .Append('\n');

        var sumSeconds = Interlocked.Read(ref _ruleEvalSumMicroseconds) / 1_000_000.0;
        sb.Append(name).Append("_sum ")
            .Append(sumSeconds.ToString("0.######", CultureInfo.InvariantCulture))
            .Append('\n');
        sb.Append(name).Append("_count ")
            .Append(Interlocked.Read(ref _ruleEvalObservations).ToString(CultureInfo.InvariantCulture))
            .Append('\n');
    }
}
