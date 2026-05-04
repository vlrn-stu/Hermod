namespace Hermod.Core.Interfaces;

/// <summary>Scheduled item for delayed or periodic rule execution.</summary>
public class ScheduledItem
{
    /// <summary>Opaque schedule id used to cancel or inspect this item.</summary>
    public required string Id { get; init; }

    /// <summary>Rule to trigger when the item fires.</summary>
    public required string RuleId { get; init; }

    /// <summary>Absolute UTC time the item next fires. Cron items advance this in-place when rescheduled.</summary>
    public required DateTime ScheduledTime { get; set; }

    /// <summary>Optional payload exposed to the triggered rule as <c>{{chain.*}}</c>.</summary>
    public Dictionary<string, object>? ChainData { get; init; }

    /// <summary>5-field cron expression for periodic items; null for one-shot items.</summary>
    public string? CronExpression { get; init; }

    /// <summary>True when this item re-schedules itself on each firing (cron); false for one-shot items.</summary>
    public bool IsPeriodic => !string.IsNullOrEmpty(CronExpression);
}

/// <summary>Event args for an item that has reached its scheduled time.</summary>
public class ScheduledItemDueEventArgs : EventArgs
{
    /// <summary>Scheduled item whose time has elapsed.</summary>
    public required ScheduledItem Item { get; init; }
}

/// <summary>
/// Delayed/periodic rule scheduler. The engine hooks <see cref="ItemDue"/> to
/// trigger rules when their schedule elapses; cron items stay registered and
/// advance on every firing.
/// </summary>
public interface IScheduler
{
    /// <summary>Fires once per scheduled item whenever its time elapses.</summary>
    event EventHandler<ScheduledItemDueEventArgs>? ItemDue;

    /// <summary>One-shot execution after <paramref name="delay"/>. Returns the schedule id for cancellation.</summary>
    string ScheduleDelay(string ruleId, TimeSpan delay, Dictionary<string, object>? chainData = null);

    /// <summary>One-shot execution at <paramref name="scheduledTime"/>. Returns the schedule id for cancellation.</summary>
    string ScheduleAt(string ruleId, DateTime scheduledTime, Dictionary<string, object>? chainData = null);

    /// <summary>Periodic execution using a 5-field cron expression (e.g. <c>"0 * * * *"</c> hourly). Returns the schedule id for cancellation.</summary>
    string ScheduleCron(string ruleId, string cronExpression, Dictionary<string, object>? chainData = null);

    /// <summary>Cancels a single scheduled item. Returns false if the id was not found.</summary>
    bool Cancel(string scheduleId);

    /// <summary>Cancels every scheduled item for <paramref name="ruleId"/>. Returns the count removed.</summary>
    int CancelForRule(string ruleId);

    /// <summary>Snapshot of every scheduled item that has not yet fired (or, for cron items, is still registered).</summary>
    IReadOnlyList<ScheduledItem> GetPendingItems();

    /// <summary>Snapshot of scheduled items belonging to a single <paramref name="ruleId"/>.</summary>
    IReadOnlyList<ScheduledItem> GetItemsForRule(string ruleId);

    /// <summary>Starts the due-check loop.</summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>Stops the due-check loop; any further <see cref="ItemDue"/> firing is suppressed.</summary>
    Task StopAsync(CancellationToken cancellationToken = default);
}
