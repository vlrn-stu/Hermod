using System.Collections.Concurrent;
using Hermod.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace Hermod.Rules;

/// <summary>
/// Manages delayed and periodic (cron) rule scheduling. Tick callbacks are
/// serialised so a slow handler cannot double-fire a pending item.
/// </summary>
public sealed partial class Scheduler : IScheduler, IDisposable
{
    private readonly ConcurrentDictionary<string, ScheduledItemInternal> _items = new();
    private readonly ILogger<Scheduler>? _logger;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _tickInterval = TimeSpan.FromMilliseconds(100);
    private Timer? _timer;
    private volatile bool _running;
    private int _tickBusy;

    /// <summary>Raised on the scheduler tick thread when a scheduled item becomes due.</summary>
    public event EventHandler<ScheduledItemDueEventArgs>? ItemDue;

    private sealed class ScheduledItemInternal : ScheduledItem
    {
        public CancellationTokenSource? CancellationSource { get; set; }
        public DateTime? NextOccurrence { get; set; }
    }

    /// <summary>
    /// Creates a scheduler. <paramref name="timeProvider"/> is used for every
    /// "now" read so tests can inject a virtual clock.
    /// </summary>
    public Scheduler(ILogger<Scheduler>? logger = null, TimeProvider? timeProvider = null)
    {
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Schedules a rule to trigger once after <paramref name="delay"/> elapses.</summary>
    public string ScheduleDelay(string ruleId, TimeSpan delay, Dictionary<string, object>? chainData = null) =>
        ScheduleAt(ruleId, _timeProvider.GetUtcNow().UtcDateTime.Add(delay), chainData);

    /// <summary>Schedules a rule to trigger once at the absolute UTC <paramref name="scheduledTime"/>.</summary>
    public string ScheduleAt(string ruleId, DateTime scheduledTime, Dictionary<string, object>? chainData = null)
    {
        var id = NewId();
        var item = new ScheduledItemInternal
        {
            Id = id,
            RuleId = ruleId,
            ScheduledTime = scheduledTime,
            ChainData = chainData,
            CancellationSource = new CancellationTokenSource(),
        };

        _items[id] = item;
        _logger?.LogDebug("Scheduled one-time trigger for rule {RuleId} at {Time} (id: {Id})",
            ruleId, scheduledTime, id);

        return id;
    }

    /// <summary>
    /// Schedules a rule to fire periodically on <paramref name="cronExpression"/>.
    /// Throws <see cref="ArgumentException"/> if the cron string does not parse.
    /// </summary>
    public string ScheduleCron(string ruleId, string cronExpression, Dictionary<string, object>? chainData = null)
    {
        ArgumentNullException.ThrowIfNull(cronExpression);

        var nextOccurrence = GetNextCronOccurrence(cronExpression)
            ?? throw new ArgumentException($"Invalid cron expression: {cronExpression}", nameof(cronExpression));

        var id = NewId();
        var item = new ScheduledItemInternal
        {
            Id = id,
            RuleId = ruleId,
            ScheduledTime = nextOccurrence,
            ChainData = chainData,
            CronExpression = cronExpression,
            NextOccurrence = nextOccurrence,
            CancellationSource = new CancellationTokenSource(),
        };

        _items[id] = item;
        _logger?.LogDebug("Scheduled cron trigger for rule {RuleId} with expression '{Cron}' (id: {Id})",
            ruleId, cronExpression, id);

        return id;
    }

    /// <summary>Cancels the scheduled item with <paramref name="scheduleId"/>; returns whether it was found.</summary>
    public bool Cancel(string scheduleId)
    {
        if (!_items.TryRemove(scheduleId, out var item)) return false;

        item.CancellationSource?.Cancel();
        item.CancellationSource?.Dispose();
        _logger?.LogDebug("Cancelled scheduled item {Id}", scheduleId);
        return true;
    }

    /// <summary>Cancels every scheduled item belonging to <paramref name="ruleId"/>. Returns the count cancelled.</summary>
    public int CancelForRule(string ruleId)
    {
        var toCancel = _items.Where(x => x.Value.RuleId == ruleId).Select(x => x.Key).ToList();
        foreach (var id in toCancel) Cancel(id);
        return toCancel.Count;
    }

    /// <summary>Snapshot of every currently pending (un-fired, un-cancelled) item.</summary>
    public IReadOnlyList<ScheduledItem> GetPendingItems() =>
        _items.Values.Cast<ScheduledItem>().ToList();

    /// <summary>Snapshot of currently pending items for a single rule.</summary>
    public IReadOnlyList<ScheduledItem> GetItemsForRule(string ruleId) =>
        _items.Values.Where(x => x.RuleId == ruleId).Cast<ScheduledItem>().ToList();

    /// <summary>Starts the periodic tick that fires due items.</summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_running) return Task.CompletedTask;

        _running = true;
        _timer = new Timer(ProcessDueItems, null, _tickInterval, _tickInterval);
        _logger?.LogInformation("Scheduler started");
        return Task.CompletedTask;
    }

    /// <summary>Stops the tick; pending items remain in the queue but will not fire.</summary>
    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        _running = false;
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        _timer?.Dispose();
        _timer = null;
        _logger?.LogInformation("Scheduler stopped");
        return Task.CompletedTask;
    }

    /// <summary>Stops the scheduler and disposes every pending cancellation token.</summary>
    public void Dispose()
    {
        StopAsync().GetAwaiter().GetResult();

        foreach (var item in _items.Values)
        {
            item.CancellationSource?.Cancel();
            item.CancellationSource?.Dispose();
        }

        _items.Clear();
    }

    private void ProcessDueItems(object? state)
    {
        if (!_running) return;
        // Serialise ticks: a slow ItemDue handler must not overlap with the next tick.
        if (Interlocked.Exchange(ref _tickBusy, 1) == 1) return;

        try
        {
            var now = _timeProvider.GetUtcNow().UtcDateTime;

            foreach (var item in _items.Values)
            {
                if (item.ScheduledTime > now) continue;
                if (item.CancellationSource?.IsCancellationRequested == true) continue;

                FireItem(item);
            }
        }
        finally
        {
            Volatile.Write(ref _tickBusy, 0);
        }
    }

    private void FireItem(ScheduledItemInternal item)
    {
        // Bookkeeping runs in finally so a throwing handler cannot leave the
        // item at its past ScheduledTime — without this a poisoned ItemDue
        // subscriber would re-fire the same item on every tick forever.
        try
        {
#pragma warning disable CA1031 // top-level logger-and-continue: a faulty handler must not stop the scheduler tick
            try
            {
                ItemDue?.Invoke(this, new ScheduledItemDueEventArgs { Item = item });
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error processing scheduled item {Id} for rule {RuleId}",
                    item.Id, item.RuleId);
            }
#pragma warning restore CA1031
        }
        finally
        {
            if (item.IsPeriodic && !string.IsNullOrEmpty(item.CronExpression))
            {
                var next = GetNextCronOccurrence(item.CronExpression);
                if (next.HasValue)
                {
                    item.ScheduledTime = next.Value;
                    item.NextOccurrence = next;
                }
                else
                {
                    _items.TryRemove(item.Id, out _);
                }
            }
            else
            {
                _items.TryRemove(item.Id, out _);
            }
        }
    }

    private static string NewId() => Guid.NewGuid().ToString("N")[..8];
}
