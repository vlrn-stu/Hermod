using Hermod.Core.Models;
using Hermod.Core.Models.Rules;

namespace Hermod.Core.Interfaces;

/// <summary>Payload fired after a rule's actions complete successfully.</summary>
public class RuleExecutedEventArgs : EventArgs
{
    /// <summary>Rule that just executed.</summary>
    public required Rule Rule { get; init; }

    /// <summary>Source message that triggered the rule.</summary>
    public required ProcessedMessage Message { get; init; }

    /// <summary>One entry per action node dispatched by this firing.</summary>
    public required List<RuleActionResult> ActionResults { get; init; }

    /// <summary>Wall-clock cost of the firing, end-to-end.</summary>
    public TimeSpan ExecutionTime { get; init; }
}

/// <summary>Payload fired when a rule raised an exception during evaluation or action dispatch.</summary>
public class RuleErrorEventArgs : EventArgs
{
    /// <summary>Rule that raised.</summary>
    public required Rule Rule { get; init; }

    /// <summary>Source message, or null if the error fired before a message was bound (e.g. cron trigger).</summary>
    public ProcessedMessage? Message { get; init; }

    /// <summary>Exception thrown by the rule engine or an action.</summary>
    public required Exception Exception { get; init; }

    /// <summary>Id of the action that failed, or null if the error occurred before action dispatch.</summary>
    public string? ActionId { get; init; }
}

/// <summary>Outcome of a single rule action inside a rule execution.</summary>
public class RuleActionResult
{
    /// <summary>Action definition this result corresponds to.</summary>
    public required RuleAction Action { get; init; }

    /// <summary>True when the action completed without raising and returned a non-error result.</summary>
    public bool Success { get; init; }

    /// <summary>Error message when <see cref="Success"/> is false.</summary>
    public string? Error { get; init; }

    /// <summary>Optional result payload (webhook response body, chain outputs, etc.).</summary>
    public object? Result { get; init; }

    /// <summary>Wall-clock cost of the action.</summary>
    public TimeSpan ExecutionTime { get; init; }

    /// <summary>Returns a copy with <see cref="ExecutionTime"/> replaced (for post-hoc timing).</summary>
    public RuleActionResult WithExecutionTime(TimeSpan executionTime) =>
        new()
        {
            Action = Action,
            Success = Success,
            Error = Error,
            Result = Result,
            ExecutionTime = executionTime
        };
}

/// <summary>
/// Rules engine that matches incoming messages against stored rules, evaluates
/// conditions, and dispatches actions. Supports triggers, chainable actions,
/// scheduling, and global state.
/// </summary>
public interface IRulesEngine
{
    /// <summary>Fires after every successful rule execution.</summary>
    event EventHandler<RuleExecutedEventArgs>? RuleExecuted;

    /// <summary>Fires when a rule raises during evaluation or action dispatch. Execution of other rules continues.</summary>
    event EventHandler<RuleErrorEventArgs>? RuleError;

    /// <summary>Matches and dispatches a pre-processed message against the rule index.</summary>
    Task ProcessMessageAsync(ProcessedMessage message, CancellationToken cancellationToken = default);

    /// <summary>
    /// Primary MQTT entry point. Runs the protocol handler chain (falls back to the
    /// generic handler), records device state into the state manager, then dispatches
    /// through the <see cref="ProcessedMessage"/> overload.
    /// </summary>
    Task ProcessMessageAsync(MqttMessage message, CancellationToken cancellationToken = default);

    /// <summary>
    /// Triggers a specific rule by id without a source message. Used by chain actions
    /// to invoke downstream rules; <paramref name="chainData"/> is exposed to the rule
    /// as <c>{{chain.*}}</c>.
    /// </summary>
    Task TriggerRuleAsync(string ruleId, Dictionary<string, object>? chainData = null,
        ProcessedMessage? sourceMessage = null, CancellationToken cancellationToken = default);

    /// <summary>Schedules a delayed rule trigger. Returns the schedule id (see <see cref="IScheduler"/>).</summary>
    string ScheduleRuleTrigger(string ruleId, TimeSpan delay, Dictionary<string, object>? chainData = null);

    /// <summary>Cancels a trigger previously scheduled via <see cref="ScheduleRuleTrigger"/>. Returns false if the id is unknown.</summary>
    bool CancelScheduledTrigger(string scheduleId);

    /// <summary>Runs every enabled rule whose trigger type is <see cref="TriggerType.OnStartup"/>.</summary>
    Task ExecuteStartupRulesAsync(CancellationToken cancellationToken = default);

    /// <summary>Reads rule-engine global state coerced to <typeparamref name="T"/>; returns <c>default</c> if missing.</summary>
    T? GetGlobalState<T>(string key);

    /// <summary>Writes a value into rule-engine global state (visible to every rule).</summary>
    void SetGlobalState(string key, object value);

    /// <summary>Drops the cached rule index so the next message fetch reloads from <see cref="IRulesService"/>. Call on rule add/update/delete.</summary>
    void InvalidateCache();

    /// <summary>
    /// Total rules observed at the last cache rebuild. Cheap O(1) read used by
    /// the API layer to enforce <see cref="Configuration.HermodLimits.MaxRules"/>
    /// without a per-request COUNT(*) round-trip. Slightly stale on bursts (the
    /// cache may not have rebuilt yet), so the limit can be exceeded by a small
    /// transient amount before the next refresh closes the window.
    /// </summary>
    int TotalRuleCount { get; }
}
