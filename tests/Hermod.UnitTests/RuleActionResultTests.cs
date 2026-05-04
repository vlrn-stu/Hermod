using Hermod.Core.Interfaces;
using Hermod.Core.Models.Rules;
using Xunit;

namespace Hermod.UnitTests;

/// <summary>
/// Pins the `WithExecutionTime` helper on <see cref="RuleActionResult"/>.
/// `ExecutionTime` was previously the only settable property while every
/// other member was `init`; it is now `init` too, with this copy helper
/// so the outer `ExecuteActionAsync` dispatcher in `EnhancedRulesEngine`
/// can still attach a measured duration to results produced by the inner
/// Execute* methods.
/// </summary>
public class RuleActionResultTests
{
    private static RuleActionResult MakeResult(string? error = null, bool success = true)
    {
        return new RuleActionResult
        {
            Action = new RuleAction { Topic = "test/topic" },
            Success = success,
            Error = error,
            Result = error is null ? "ok" : null,
            ExecutionTime = TimeSpan.Zero
        };
    }

    [Fact]
    public void WithExecutionTime_ReplacesElapsed_ReturnsNewInstance()
    {
        var original = MakeResult();
        var elapsed = TimeSpan.FromMilliseconds(42);

        var copy = original.WithExecutionTime(elapsed);

        Assert.NotSame(original, copy);
        Assert.Equal(elapsed, copy.ExecutionTime);
        Assert.Equal(TimeSpan.Zero, original.ExecutionTime);
    }

    [Fact]
    public void WithExecutionTime_PreservesActionAndSuccessAndResult()
    {
        var original = MakeResult();
        var copy = original.WithExecutionTime(TimeSpan.FromMilliseconds(10));

        Assert.Same(original.Action, copy.Action);
        Assert.Equal(original.Success, copy.Success);
        Assert.Equal(original.Result, copy.Result);
        Assert.Equal(original.Error, copy.Error);
    }

    [Fact]
    public void WithExecutionTime_PreservesErrorOnFailureCase()
    {
        var original = MakeResult(error: "connection refused", success: false);
        var copy = original.WithExecutionTime(TimeSpan.FromSeconds(5));

        Assert.False(copy.Success);
        Assert.Equal("connection refused", copy.Error);
        Assert.Null(copy.Result);
        Assert.Equal(TimeSpan.FromSeconds(5), copy.ExecutionTime);
    }

    [Fact]
    public void WithExecutionTime_MultipleCalls_EachProduceFreshCopy()
    {
        var original = MakeResult();
        var copy1 = original.WithExecutionTime(TimeSpan.FromMilliseconds(1));
        var copy2 = original.WithExecutionTime(TimeSpan.FromMilliseconds(2));

        Assert.Equal(TimeSpan.FromMilliseconds(1), copy1.ExecutionTime);
        Assert.Equal(TimeSpan.FromMilliseconds(2), copy2.ExecutionTime);
        Assert.NotSame(copy1, copy2);
        // Original is untouched.
        Assert.Equal(TimeSpan.Zero, original.ExecutionTime);
    }
}
