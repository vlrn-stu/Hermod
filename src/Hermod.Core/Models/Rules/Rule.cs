namespace Hermod.Core.Models.Rules;

/// <summary>
/// A persisted rule: what triggers it, the conditions it gates on, the
/// actions it dispatches, and runtime metadata (counters, last error).
/// </summary>
public class Rule
{
    /// <summary>Primary key (GUID string by default).</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Operator-facing rule name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Optional long-form description surfaced in the dashboard.</summary>
    public string? Description { get; set; }

    /// <summary>When false the engine skips this rule entirely.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Lower values run first. Default 100 leaves room on both sides for seeded and user-authored rules.</summary>
    public int Priority { get; set; } = 100;

    /// <summary>Activation conditions (topic pattern, trigger type, schedule, debounce, windows).</summary>
    public RuleTrigger Trigger { get; set; } = new();

    /// <summary>Boolean tree evaluated after the trigger fires. Null means "no gating".</summary>
    public RuleConditionGroup? Conditions { get; set; }

    /// <summary>Actions dispatched in order. Chain/Parallel actions may fan out further.</summary>
    public List<RuleAction> Actions { get; set; } = new();

    /// <summary>Per-rule state bag that persists between executions (exposed as <c>{{state.*}}</c>).</summary>
    public Dictionary<string, object> State { get; set; } = new();

    /// <summary>Free-form tags used for dashboard filtering and bulk operations.</summary>
    public List<string> Tags { get; set; } = new();

    /// <summary>Creation timestamp.</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Most recent mutation timestamp.</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Lifetime firing count (including failed firings).</summary>
    public int ExecutionCount { get; set; }

    /// <summary>Timestamp of the most recent firing, or null if the rule never fired.</summary>
    public DateTime? LastExecutedAt { get; set; }

    /// <summary>Timestamp of the most recent error, or null if the rule has never errored.</summary>
    public DateTime? LastErrorAt { get; set; }

    /// <summary>Message from the most recent error, or null.</summary>
    public string? LastError { get; set; }
}

/// <summary>Activation conditions for a <see cref="Rule"/>.</summary>
public class RuleTrigger
{
    /// <summary>MQTT topic pattern. <c>+</c> matches one level, <c>#</c> matches the tail. Default <c>#</c> matches everything.</summary>
    public string TopicPattern { get; set; } = "#";

    /// <summary>Trigger semantics (on-message, on-change, on-schedule, etc.).</summary>
    public TriggerType Type { get; set; } = TriggerType.OnMessage;

    /// <summary>5-field cron expression for <see cref="TriggerType.OnSchedule"/> (e.g. <c>"0 * * * *"</c> hourly).</summary>
    public string? Schedule { get; set; }

    /// <summary>Minimum interval between firings (e.g. <c>"5s"</c>, <c>"1m"</c>). Bursts within the window are coalesced.</summary>
    public string? Debounce { get; set; }

    /// <summary>Optional whitelist of time windows; outside of any window the rule will not fire.</summary>
    public List<TimeWindow>? ActiveWindows { get; set; }
}

/// <summary>Activation semantics for a <see cref="RuleTrigger"/>.</summary>
public enum TriggerType
{
    /// <summary>Fire on every matching message.</summary>
    OnMessage,

    /// <summary>Fire only when the payload differs from the device's previous state.</summary>
    OnChange,

    /// <summary>Fire on a schedule (<see cref="RuleTrigger.Schedule"/>).</summary>
    OnSchedule,

    /// <summary>Fire only when invoked by another rule's Chain action.</summary>
    OnChain,

    /// <summary>Fire once during coordinator startup.</summary>
    OnStartup,

    /// <summary>Fire on device online/offline transitions.</summary>
    OnAvailability
}

/// <summary>
/// Time window for restricting when a rule can fire. Defaults
/// (<c>Start = TimeOnly.MinValue</c>, <c>End = TimeOnly.MaxValue</c>) describe
/// an always-active window. When <c>Start &gt; End</c> the window wraps midnight.
/// </summary>
public class TimeWindow
{
    /// <summary>Window start (local time). Defaults to midnight.</summary>
    public TimeOnly Start { get; set; } = TimeOnly.MinValue;

    /// <summary>Window end (local time). Defaults to end-of-day.</summary>
    public TimeOnly End { get; set; } = TimeOnly.MaxValue;

    /// <summary>Days the window applies on; null = every day.</summary>
    public List<DayOfWeek>? Days { get; set; }
}
