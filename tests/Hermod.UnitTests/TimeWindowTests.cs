using Hermod.Core.Models.Rules;
using Xunit;

namespace Hermod.UnitTests;

/// <summary>
/// Regression guard for the TimeWindow default fix. Previously both
/// Start and End defaulted to 00:00:00, so an unset TimeWindow produced
/// an empty interval and the rule never fired. The current defaults are
/// Start=MinValue (00:00:00) and End=MaxValue (23:59:59.9999999), which
/// means "active all day".
/// </summary>
public class TimeWindowTests
{
    [Fact]
    public void Default_StartIsMinValue()
    {
        var w = new TimeWindow();
        Assert.Equal(TimeOnly.MinValue, w.Start);
    }

    [Fact]
    public void Default_EndIsMaxValue()
    {
        var w = new TimeWindow();
        Assert.Equal(TimeOnly.MaxValue, w.End);
    }

    [Fact]
    public void Default_IntervalCoversAllTimesOfDay()
    {
        // Spot-check a few representative times. The new defaults must
        // treat each of them as "inside the window" (Start <= t <= End).
        var w = new TimeWindow();
        var samples = new[]
        {
            new TimeOnly(0, 0, 1),    // just after midnight
            new TimeOnly(6, 30, 0),   // early morning
            new TimeOnly(12, 0, 0),   // noon
            new TimeOnly(18, 45, 0),  // evening
            new TimeOnly(23, 59, 58), // just before midnight
        };
        foreach (var t in samples)
        {
            Assert.True(
                t >= w.Start && t <= w.End,
                $"TimeOnly {t} must fall inside the default TimeWindow [{w.Start}, {w.End}]");
        }
    }

    [Fact]
    public void Default_MidnightSample_FallsInsideWindow()
    {
        // 00:00:00 exactly is both the Start and a very common TimeOnly
        // value. Pin it explicitly because this is the specific case
        // the pre-cycle-62 defaults were broken on (Start == End == 0).
        var w = new TimeWindow();
        var midnight = new TimeOnly(0, 0, 0);
        Assert.True(midnight >= w.Start && midnight <= w.End);
    }

    [Fact]
    public void Default_DaysIsNull_MeansAllDaysOfWeek()
    {
        // Not part of #101 but worth pinning: Days defaults to null,
        // which IsInActiveWindow in EnhancedRulesEngine interprets as
        // "no day restriction". Changing this default would silently
        // break every existing rule with an unset Days field.
        var w = new TimeWindow();
        Assert.Null(w.Days);
    }

    [Fact]
    public void ExplicitRestrictedWindow_NotCoveredByDefaultBehaviour()
    {
        // Regression guard that the defaults don't accidentally
        // override an explicit restriction. A window from 09:00 to
        // 17:00 must NOT include 03:00.
        var w = new TimeWindow
        {
            Start = new TimeOnly(9, 0, 0),
            End = new TimeOnly(17, 0, 0)
        };
        var at3am = new TimeOnly(3, 0, 0);
        Assert.False(at3am >= w.Start && at3am <= w.End);
    }
}
