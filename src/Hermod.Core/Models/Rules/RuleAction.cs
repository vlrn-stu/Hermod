namespace Hermod.Core.Models.Rules;

/// <summary>
/// One step a rule executes when it fires. The active fields depend on
/// <see cref="Type"/> — each region below documents which fields apply.
/// </summary>
public class RuleAction
{
    /// <summary>Selects which region of fields below applies at runtime.</summary>
    public ActionType Type { get; set; } = ActionType.Publish;

    #region Publish Action

    /// <summary>Target MQTT topic. May contain template substitutions.</summary>
    public string? Topic { get; set; }

    /// <summary>Payload key/value map. Values may contain templates such as <c>{{source.temperature}}</c>.</summary>
    public Dictionary<string, object>? Payload { get; set; }

    /// <summary>If true, merge the source message payload into the outgoing payload (this overrides on key conflict).</summary>
    public bool PassthroughPayload { get; set; }

    /// <summary>Optional per-field transforms applied during payload assembly.</summary>
    public List<PayloadTransform>? Transforms { get; set; }

    /// <summary>MQTT QoS level (0, 1, or 2).</summary>
    public int QoS { get; set; } = 0;

    /// <summary>When true, the broker retains this message as initial state for future subscribers.</summary>
    public bool Retain { get; set; }

    #endregion

    #region Chain Action

    /// <summary>Id of the downstream rule to invoke.</summary>
    public string? ChainToRule { get; set; }

    /// <summary>Delay before invoking the chained rule (e.g. <c>"5s"</c>, <c>"1m"</c>, <c>"1h"</c>). Zero/null fires synchronously.</summary>
    public string? Delay { get; set; }

    /// <summary>Data forwarded to the chained rule, accessible there as <c>{{chain.*}}</c>.</summary>
    public Dictionary<string, object>? ChainData { get; set; }

    #endregion

    #region State Action

    /// <summary>Rule-local state writes. Keys may use dot notation; values may contain templates.</summary>
    public Dictionary<string, object>? SetState { get; set; }

    /// <summary>Global state writes (visible to every rule). Same key/value rules as <see cref="SetState"/>.</summary>
    public Dictionary<string, object>? GlobalState { get; set; }

    #endregion

    #region Conditional Action

    /// <summary>Predicate group for the if/then/else branch.</summary>
    public RuleConditionGroup? If { get; set; }

    /// <summary>Branch executed when <see cref="If"/> evaluates to true.</summary>
    public List<RuleAction>? Then { get; set; }

    /// <summary>Branch executed when <see cref="If"/> evaluates to false.</summary>
    public List<RuleAction>? Else { get; set; }

    #endregion

    #region Other Actions

    /// <summary>Message body for <see cref="ActionType.Log"/>; may contain templates.</summary>
    public string? LogMessage { get; set; }

    /// <summary>Log level: Debug, Info, Warning, Error. Defaults to Information.</summary>
    public string? LogLevel { get; set; }

    /// <summary>Webhook target URL for <see cref="ActionType.Webhook"/>.</summary>
    public string? WebhookUrl { get; set; }

    /// <summary>HTTP method for the webhook (defaults to POST).</summary>
    public string? WebhookMethod { get; set; }

    /// <summary>Sub-actions executed concurrently for <see cref="ActionType.Parallel"/>.</summary>
    public List<RuleAction>? ParallelActions { get; set; }

    #endregion
}

/// <summary>Discriminator for <see cref="RuleAction"/> fields; each value selects a region of fields on the parent.</summary>
public enum ActionType
{
    /// <summary>Publish MQTT message.</summary>
    Publish,

    /// <summary>Trigger another rule (optionally with delay).</summary>
    Chain,

    /// <summary>Update rule or global state.</summary>
    SetState,

    /// <summary>Conditional if/then/else.</summary>
    Conditional,

    /// <summary>Wait before continuing to next action.</summary>
    Delay,

    /// <summary>Execute multiple actions in parallel.</summary>
    Parallel,

    /// <summary>Log a message for debugging.</summary>
    Log,

    /// <summary>Call external webhook.</summary>
    Webhook,

    /// <summary>Stop processing further actions in this rule.</summary>
    Stop,

    /// <summary>Cancel a pending delayed action.</summary>
    CancelDelay
}

/// <summary>Field-level mapping rule applied while assembling a published payload.</summary>
public class PayloadTransform
{
    /// <summary>Source property to read from the incoming message payload.</summary>
    public string? SourceProperty { get; set; }

    /// <summary>Property name to write into the outgoing payload.</summary>
    public string? TargetProperty { get; set; }

    /// <summary>Optional template expression applied to the source value; the result lands in <see cref="TargetProperty"/>.</summary>
    public string? Expression { get; set; }

    /// <summary>Fallback value when the source property is missing.</summary>
    public object? DefaultValue { get; set; }
}
