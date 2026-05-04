using Hermod.Core;
using Hermod.Core.Interfaces;
using Hermod.Rules;
using Xunit;

namespace Hermod.UnitTests;

public class SchedulerTests
{
    [Theory]
    [InlineData("500ms", 500)]
    [InlineData("5s", 5_000)]
    [InlineData("2m", 120_000)]
    [InlineData("1h", 3_600_000)]
    [InlineData("1d", 86_400_000)]
    public void ParseDelay_AcceptsSuffixedUnits(string input, double expectedMs)
    {
        Assert.Equal(TimeSpan.FromMilliseconds(expectedMs), DelayParser.Parse(input));
    }

    [Fact]
    public void ParseDelay_EmptyString_ReturnsZero()
    {
        Assert.Equal(TimeSpan.Zero, DelayParser.Parse(""));
    }

    [Fact]
    public void ParseDelay_InvalidFormat_Throws()
    {
        Assert.Throws<ArgumentException>(() => DelayParser.Parse("not-a-delay"));
    }

    [Fact]
    public void ScheduleDelay_ReturnsNonEmptyId()
    {
        var sut = new Scheduler();
        var id = sut.ScheduleDelay("rule-1", TimeSpan.FromSeconds(10));
        Assert.False(string.IsNullOrEmpty(id));
    }

    [Fact]
    public void Cancel_ExistingSchedule_ReturnsTrue()
    {
        var sut = new Scheduler();
        var id = sut.ScheduleDelay("rule-1", TimeSpan.FromMinutes(1));

        Assert.True(sut.Cancel(id));
        Assert.False(sut.Cancel(id));
    }

    [Fact]
    public void CancelForRule_RemovesAllItemsForRule()
    {
        var sut = new Scheduler();
        sut.ScheduleDelay("rule-1", TimeSpan.FromMinutes(1));
        sut.ScheduleDelay("rule-1", TimeSpan.FromMinutes(2));
        sut.ScheduleDelay("rule-2", TimeSpan.FromMinutes(1));

        var cancelled = sut.CancelForRule("rule-1");

        Assert.Equal(2, cancelled);
        Assert.Single(sut.GetPendingItems());
    }

    [Fact]
    public async Task StartAsync_FiresDueItems()
    {
        var sut = new Scheduler();
        var fired = new List<string>();
        sut.ItemDue += (_, e) => fired.Add(e.Item.RuleId);

        await sut.StartAsync();
        sut.ScheduleDelay("rule-imminent", TimeSpan.FromMilliseconds(50));

        await Task.Delay(300);
        await sut.StopAsync();

        Assert.Contains("rule-imminent", fired);
    }

    [Fact]
    public void ScheduleCron_InvalidExpression_Throws()
    {
        var sut = new Scheduler();
        Assert.Throws<ArgumentException>(() =>
            sut.ScheduleCron("rule-1", "not a cron"));
    }

    [Fact]
    public void ScheduleCron_ValidExpression_RegistersItem()
    {
        var sut = new Scheduler();
        var id = sut.ScheduleCron("rule-1", "*/5 * * * *");
        var items = sut.GetItemsForRule("rule-1");

        Assert.Single(items);
        Assert.Equal(id, items[0].Id);
        Assert.True(items[0].IsPeriodic);
    }

    [Theory]
    [InlineData("60 * * * *")]       // minute out of range
    [InlineData("* 24 * * *")]       // hour out of range
    [InlineData("* * 32 * *")]       // day out of range
    [InlineData("* * * 13 *")]       // month out of range
    [InlineData("* * * * 8")]        // day-of-week out of range (7 is Sunday, 8 is not)
    public void ScheduleCron_UnreachableField_Throws(string expression)
    {
        var sut = new Scheduler();
        Assert.Throws<ArgumentException>(() => sut.ScheduleCron("r", expression));
    }

    [Theory]
    [InlineData("0 9 * * *")]        // daily at 09:00
    [InlineData("*/15 * * * *")]     // every 15 minutes
    [InlineData("0 9-17 * * 1-5")]   // weekday business hours
    [InlineData("0 0 1,15 * *")]     // 1st and 15th of each month
    [InlineData("0 0 * * 7")]        // Sunday as 7 (POSIX)
    public void ScheduleCron_AcceptsCommonForms(string expression)
    {
        var sut = new Scheduler();
        var id = sut.ScheduleCron("r", expression);
        Assert.NotNull(id);

        var items = sut.GetItemsForRule("r");
        Assert.Single(items);
        Assert.True(items[0].ScheduledTime > DateTime.UtcNow);
    }

    [Fact]
    public void ScheduleCron_DomAndDowBothRestricted_UsesPosixOrSemantics()
    {
        // Cron POSIX rule: when both DoM and DoW are non-wildcard, a match
        // on EITHER fires. `0 0 13 * 5` means "midnight on the 13th of any
        // month OR any Friday at midnight". Regressing to AND would compute
        // a dramatically later next occurrence.
        var sut = new Scheduler();
        var id = sut.ScheduleCron("r", "0 0 13 * 5");
        var items = sut.GetItemsForRule("r");

        Assert.Single(items);
        // Next occurrence must be within the next ~31 days because either
        // the next Friday or the next 13th is always within a month.
        Assert.True(items[0].ScheduledTime <= DateTime.UtcNow.AddDays(32));
    }

    [Fact]
    public void ScheduleCron_SparseLeapDayPattern_FindsNextLeapYear()
    {
        // Regression guard for the smart-step refactor in
        // Scheduler.GetNextCronOccurrence. The sparse pattern "0 0 29 2 *"
        // (midnight on Feb 29) is only valid in leap years. The old
        // minute-by-minute walk capped at 525600 iterations (~1 year) and
        // would return null when the next leap year was more than ~11 months
        // away. With month-level skips the walk converges to the next
        // valid leap-year Feb 29 in O(months searched) iterations.
        var sut = new Scheduler();
        var id = sut.ScheduleCron("r", "0 0 29 2 *");
        var items = sut.GetItemsForRule("r");

        Assert.Single(items);
        var next = items[0].ScheduledTime;
        Assert.Equal(2, next.Month);
        Assert.Equal(29, next.Day);
        Assert.Equal(0, next.Hour);
        Assert.Equal(0, next.Minute);
        // The next occurrence must be strictly in the future and within
        // the next ~4 years (leap years repeat every 4 years except on
        // most century boundaries, and the current year cannot be more
        // than 4 years from the next leap year).
        Assert.True(next > DateTime.UtcNow);
        Assert.True(next <= DateTime.UtcNow.AddDays(366 * 5));
        // The computed year must actually be a leap year, otherwise we
        // would never reach Feb 29.
        Assert.True(DateTime.IsLeapYear(next.Year));
    }

    [Fact]
    public void ScheduleCron_SparseMonthlyPattern_NoRegression()
    {
        // "0 3 1 7 *" is "03:00 on July 1st each year". With month-level
        // skips, the walk should converge quickly regardless of the current
        // month. Regression guard for the smart-step refactor to ensure the
        // once-a-year case still returns a match inside the iteration cap.
        var sut = new Scheduler();
        var id = sut.ScheduleCron("r", "0 3 1 7 *");
        var items = sut.GetItemsForRule("r");

        Assert.Single(items);
        var next = items[0].ScheduledTime;
        Assert.Equal(7, next.Month);
        Assert.Equal(1, next.Day);
        Assert.Equal(3, next.Hour);
        Assert.Equal(0, next.Minute);
        Assert.True(next > DateTime.UtcNow);
    }

    [Fact]
    public void ScheduleAt_PassesChainDataThroughToItem()
    {
        var sut = new Scheduler();
        var chainData = new Dictionary<string, object> { ["k"] = 42, ["m"] = "hello" };

        var id = sut.ScheduleAt("r", DateTime.UtcNow.AddHours(1), chainData);
        var item = sut.GetItemsForRule("r").Single();

        Assert.Equal(id, item.Id);
        Assert.NotNull(item.ChainData);
        Assert.Equal(42, item.ChainData["k"]);
        Assert.Equal("hello", item.ChainData["m"]);
    }

    [Fact]
    public async Task StartAsync_DoesNotDoubleFireWhenTickBusy()
    {
        // Register a handler that blocks the first tick long enough for the
        // second tick to land. The double-fire gate must ensure only one
        // ItemDue event is raised for a single scheduled item.
        var sut = new Scheduler();
        var fireCount = 0;
        var gate = new TaskCompletionSource();

        sut.ItemDue += (_, _) =>
        {
            Interlocked.Increment(ref fireCount);
            // Block the first handler so the next 100 ms tick overlaps it.
            gate.Task.Wait(TimeSpan.FromMilliseconds(250));
        };

        await sut.StartAsync();
        sut.ScheduleDelay("r", TimeSpan.FromMilliseconds(10));

        await Task.Delay(400);
        gate.TrySetResult();
        await sut.StopAsync();

        Assert.Equal(1, fireCount);
    }

    [Fact]
    public async Task OneShot_HandlerThrows_RemovedExactlyOnce()
    {
        // Regression guard: before the finally-block fix, a throwing handler
        // would leave the item at its past ScheduledTime and every subsequent
        // 100 ms tick would re-fire it. The item must be removed even when
        // the handler throws.
        var sut = new Scheduler();
        var fireCount = 0;
        sut.ItemDue += (_, _) =>
        {
            Interlocked.Increment(ref fireCount);
            throw new InvalidOperationException("handler blew up");
        };

        await sut.StartAsync();
        sut.ScheduleDelay("r", TimeSpan.FromMilliseconds(10));

        await Task.Delay(400);
        await sut.StopAsync();

        Assert.Equal(1, fireCount);
        Assert.Empty(sut.GetItemsForRule("r"));
    }

    [Fact]
    public async Task CronItem_HandlerThrows_AdvancesToNextOccurrence()
    {
        // A throwing handler on a periodic item must still advance the
        // ScheduledTime — otherwise the item re-fires every tick instead of
        // waiting for the next cron occurrence.
        var sut = new Scheduler();
        var fireCount = 0;
        sut.ItemDue += (_, _) =>
        {
            Interlocked.Increment(ref fireCount);
            throw new InvalidOperationException("cron handler blew up");
        };

        await sut.StartAsync();
        sut.ScheduleCron("r", "* * * * *");
        var item = sut.GetItemsForRule("r").Single();
        item.ScheduledTime = DateTime.UtcNow.AddSeconds(-5);

        await Task.Delay(400);
        await sut.StopAsync();

        Assert.Equal(1, fireCount);
        var remaining = sut.GetItemsForRule("r");
        Assert.Single(remaining);
        Assert.True(remaining[0].ScheduledTime > DateTime.UtcNow);
    }

    [Fact]
    public async Task CronItem_AfterFiring_RemainsInItemsWithAdvancedTime()
    {
        // The fire-and-advance branch at Scheduler.ProcessDueItems must
        // re-arm cron items in place. A regression to one-shot semantics
        // would leave _items empty after the first fire.
        var sut = new Scheduler();
        var fired = new TaskCompletionSource();
        sut.ItemDue += (_, _) => fired.TrySetResult();

        await sut.StartAsync();

        // `* * * * *` fires every minute. To avoid a real minute-wide wait,
        // manually add a cron item whose ScheduledTime is already in the past.
        var id = sut.ScheduleCron("r", "* * * * *");
        var item = sut.GetItemsForRule("r").Single();
        item.ScheduledTime = DateTime.UtcNow.AddSeconds(-5);

        var winner = await Task.WhenAny(fired.Task, Task.Delay(1000));
        await sut.StopAsync();

        Assert.Same(fired.Task, winner);
        var remaining = sut.GetItemsForRule("r");
        Assert.Single(remaining);
        Assert.Equal(id, remaining[0].Id);
        Assert.True(remaining[0].ScheduledTime > DateTime.UtcNow);
    }

    [Fact]
    public void Dispose_WithLiveItems_DoesNotThrow()
    {
        var sut = new Scheduler();
        sut.ScheduleDelay("r1", TimeSpan.FromMinutes(5));
        sut.ScheduleDelay("r2", TimeSpan.FromMinutes(10));

        sut.Dispose();

        // No assertion on state; the requirement is "does not throw and
        // leaves the scheduler in a disposed-but-safe state".
    }

    [Fact]
    public void ParseDelay_AcceptsTimeSpanFallback()
    {
        Assert.Equal(TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(30),
            DelayParser.Parse("00:01:30"));
    }

    [Fact]
    public async Task ScheduleAt_AbsoluteTimeInPast_FiresImmediately()
    {
        // ScheduleAt with a past timestamp should fire on the next tick
        // without waiting. This guards the absolute-time code path the
        // prior suite only covered via ScheduleDelay.
        var sut = new Scheduler();
        var fired = new TaskCompletionSource();
        sut.ItemDue += (_, e) =>
        {
            if (e.Item.RuleId == "past") fired.TrySetResult();
        };

        await sut.StartAsync();
        sut.ScheduleAt("past", DateTime.UtcNow.AddSeconds(-1));

        var winner = await Task.WhenAny(fired.Task, Task.Delay(1000));
        await sut.StopAsync();

        Assert.Same(fired.Task, winner);
    }

    [Fact]
    public void ScheduleAt_FutureTime_AppearsInPendingItems()
    {
        var sut = new Scheduler();
        var when = DateTime.UtcNow.AddHours(3);

        var id = sut.ScheduleAt("r", when);
        var pending = sut.GetPendingItems();

        var item = pending.Single(p => p.Id == id);
        Assert.Equal(when, item.ScheduledTime);
        Assert.Equal("r", item.RuleId);
    }

    // Exercises the optional TimeProvider parameter so `ScheduleDelay`
    // and the cron occurrence computation see a controlled clock rather
    // than `DateTime.UtcNow`. The existing `StartAsync_FiresDueItems`
    // test was wall-clock flaky; the full fix (FakeTimeProvider-driven
    // timer) requires the `Microsoft.Extensions.TimeProvider.Testing`
    // package which is not yet added to the test project. Until then,
    // the injection path is covered here through the non-timer call
    // sites.

    [Fact]
    public void ScheduleDelay_WithFixedTimeProvider_ProducesDeterministicScheduledTime()
    {
        // Fix the clock at an arbitrary point in 2026. ScheduleDelay
        // should compute ScheduledTime = fixedNow + delay, not
        // DateTime.UtcNow + delay.
        var fixedNow = new DateTimeOffset(2026, 4, 11, 10, 0, 0, TimeSpan.Zero);
        var timeProvider = new FixedClockTimeProvider(fixedNow);
        var sut = new Scheduler(logger: null, timeProvider: timeProvider);

        var id = sut.ScheduleDelay("rule-deterministic", TimeSpan.FromMinutes(30));

        var item = sut.GetPendingItems().Single(p => p.Id == id);
        Assert.Equal(fixedNow.UtcDateTime.AddMinutes(30), item.ScheduledTime);
    }

    [Fact]
    public void ScheduleDelay_DefaultConstructor_UsesWallClockTimeProvider()
    {
        // Sanity: the parameterless constructor path still works and
        // produces a ScheduledTime near real wall clock now + delay.
        // Tolerance of 5 seconds to absorb CI jitter.
        var before = DateTime.UtcNow;
        var sut = new Scheduler();
        var id = sut.ScheduleDelay("rule-wallclock", TimeSpan.FromSeconds(60));
        var after = DateTime.UtcNow;

        var item = sut.GetPendingItems().Single(p => p.Id == id);
        Assert.InRange(
            item.ScheduledTime,
            before.AddSeconds(60).AddSeconds(-5),
            after.AddSeconds(60).AddSeconds(5));
    }

    [Fact]
    public void ScheduleCron_WithFixedTimeProvider_ComputesDeterministicNextOccurrence()
    {
        // Cron "0 12 * * *" fires at 12:00 UTC every day. With a fixed
        // clock at 2026-04-11 10:00 UTC, the next occurrence is
        // 2026-04-11 12:00 UTC. Without TimeProvider injection this
        // test would depend on wall clock and be flaky depending on
        // CI time of day.
        var fixedNow = new DateTimeOffset(2026, 4, 11, 10, 0, 0, TimeSpan.Zero);
        var sut = new Scheduler(logger: null, timeProvider: new FixedClockTimeProvider(fixedNow));

        var id = sut.ScheduleCron("rule-noon", "0 12 * * *");

        var item = sut.GetPendingItems().Single(p => p.Id == id);
        Assert.Equal(new DateTime(2026, 4, 11, 12, 0, 0, DateTimeKind.Utc), item.ScheduledTime);
    }

    private sealed class FixedClockTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FixedClockTimeProvider(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
