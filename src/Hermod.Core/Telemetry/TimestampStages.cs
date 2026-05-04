namespace Hermod.Core.Telemetry;

/// <summary>
/// Canonical stage labels for per-message timestamp emission. Load gen
/// stamps <see cref="PublishTx"/>; the coordinator stamps the rest on
/// the ingest and action-publish hot paths. The safety/liveness verifier
/// and the W-measurement aggregator read these strings literally, so
/// drift here silently invalidates correlation.
/// </summary>
public static class TimestampStages
{
    /// <summary>Load gen at send (<c>publish_tx</c>).</summary>
    public const string PublishTx = "publish_tx";

    /// <summary>Coordinator MQTT client receive callback (<c>broker_rx</c>).</summary>
    public const string BrokerRx = "broker_rx";

    /// <summary>Coordinator after rules engine returns (<c>rule_eval_done</c>).</summary>
    public const string RuleEvalDone = "rule_eval_done";

    /// <summary>Coordinator outbound publish in rule action (<c>action_publish</c>).</summary>
    public const string ActionPublish = "action_publish";
}
