using System.Text.Json;
using Hermod.Rules;
using Xunit;

namespace Hermod.UnitTests;

public class StateManagerTests
{
    [Fact]
    public void GetRuleState_ForNewRule_ReturnsEmptyDictionary()
    {
        var sut = new StateManager();
        var state = sut.GetRuleState("rule-1");
        Assert.NotNull(state);
        Assert.Empty(state);
    }

    [Fact]
    public void SetRuleState_PersistsValueAcrossGets()
    {
        var sut = new StateManager();
        sut.SetRuleState("rule-1", "counter", 5);

        var state = sut.GetRuleState("rule-1");
        Assert.Equal(5, state["counter"]);
    }

    [Fact]
    public void ClearRuleState_RemovesAllKeysForRule()
    {
        var sut = new StateManager();
        sut.SetRuleState("rule-1", "a", 1);
        sut.SetRuleState("rule-1", "b", 2);

        sut.ClearRuleState("rule-1");

        Assert.Empty(sut.GetRuleState("rule-1"));
    }

    [Fact]
    public void SetGlobal_AndGetGlobal_TypedRoundtrip()
    {
        var sut = new StateManager();
        sut.SetGlobal("mode", "auto");

        Assert.Equal("auto", sut.GetGlobal<string>("mode"));
    }

    [Fact]
    public void GetGlobal_Missing_ReturnsDefault()
    {
        var sut = new StateManager();
        Assert.Null(sut.GetGlobal<string>("missing"));
        Assert.Equal("fallback", sut.GetGlobal("missing", "fallback"));
    }

    [Fact]
    public void RemoveGlobal_ReturnsTrueOnlyOnce()
    {
        var sut = new StateManager();
        sut.SetGlobal("k", 1);

        Assert.True(sut.RemoveGlobal("k"));
        Assert.False(sut.RemoveGlobal("k"));
    }

    [Fact]
    public void SetDeviceState_FirstSet_HasNoPrevious()
    {
        var sut = new StateManager();
        sut.SetDeviceState("lamp", new Dictionary<string, object> { ["on"] = true });

        Assert.Null(sut.GetPreviousDeviceState("lamp"));
        Assert.Equal(true, sut.GetDeviceState("lamp")!["on"]);
    }

    [Fact]
    public void SetDeviceState_SecondSet_MovesCurrentToPrevious()
    {
        var sut = new StateManager();
        sut.SetDeviceState("lamp", new Dictionary<string, object> { ["on"] = false });
        sut.SetDeviceState("lamp", new Dictionary<string, object> { ["on"] = true });

        var previous = sut.GetPreviousDeviceState("lamp");
        Assert.NotNull(previous);
        Assert.Equal(false, previous["on"]);
        Assert.Equal(true, sut.GetDeviceState("lamp")!["on"]);
    }

    [Fact]
    public void GetDeviceState_ReturnsCopy_NotLiveReference()
    {
        var sut = new StateManager();
        sut.SetDeviceState("lamp", new Dictionary<string, object> { ["on"] = true });

        var snapshot = sut.GetDeviceState("lamp")!;
        snapshot["on"] = false;

        // Mutating the copy must not alter the manager's internal state.
        Assert.Equal(true, sut.GetDeviceState("lamp")!["on"]);
    }

    [Fact]
    public void SetDeviceState_DoesNotMutateCallerDict()
    {
        // Defensive copy of the input: after SetDeviceState returns,
        // mutating the dict the caller passed in must NOT leak into
        // the stored state. This pins the `new Dictionary<>(state)`
        // copy at the entry point.
        var sut = new StateManager();
        var input = new Dictionary<string, object> { ["on"] = true };
        sut.SetDeviceState("lamp", input);

        input["on"] = false;  // caller mutation after the fact

        Assert.Equal(true, sut.GetDeviceState("lamp")!["on"]);
    }

    [Fact]
    public void SetDeviceState_CapturedPreviousReference_SurvivesSubsequentUpdate()
    {
        // Regression guard: under a mutable-entry approach, a later
        // `SetDeviceState` call could reassign `existing.Previous`,
        // changing what a reader who already captured the previous
        // reference observed. Under the immutable-swap contract, the
        // old entry reference stays frozen for any reader that already
        // holds it.
        var sut = new StateManager();
        sut.SetDeviceState("lamp", new Dictionary<string, object> { ["v"] = 1 });
        sut.SetDeviceState("lamp", new Dictionary<string, object> { ["v"] = 2 });

        // Grab the "previous" value now: should be 1.
        var prev1 = sut.GetPreviousDeviceState("lamp");
        Assert.NotNull(prev1);
        Assert.Equal(1, prev1["v"]);

        // Now do another update: "previous" logically shifts to 2.
        sut.SetDeviceState("lamp", new Dictionary<string, object> { ["v"] = 3 });

        // A FRESH read of previous now returns 2 (the last Current).
        var prev2 = sut.GetPreviousDeviceState("lamp");
        Assert.NotNull(prev2);
        Assert.Equal(2, prev2["v"]);

        // And the prev1 captured earlier is untouched by the newer
        // update: `GetPreviousDeviceState` returns a defensive copy,
        // and even if it did not, the old entry's dict reference is
        // frozen.
        Assert.Equal(1, prev1["v"]);
    }

    [Fact]
    public void SetDeviceState_CurrentAndPreviousCoherentAfterMultipleUpdates()
    {
        // Sequence: set v=1, v=2, v=3. At each step, current and
        // previous should correctly track the last two values.
        var sut = new StateManager();

        sut.SetDeviceState("lamp", new Dictionary<string, object> { ["v"] = 1 });
        Assert.Equal(1, sut.GetDeviceState("lamp")!["v"]);
        Assert.Null(sut.GetPreviousDeviceState("lamp"));

        sut.SetDeviceState("lamp", new Dictionary<string, object> { ["v"] = 2 });
        Assert.Equal(2, sut.GetDeviceState("lamp")!["v"]);
        Assert.Equal(1, sut.GetPreviousDeviceState("lamp")!["v"]);

        sut.SetDeviceState("lamp", new Dictionary<string, object> { ["v"] = 3 });
        Assert.Equal(3, sut.GetDeviceState("lamp")!["v"]);
        Assert.Equal(2, sut.GetPreviousDeviceState("lamp")!["v"]);
    }

    [Fact]
    public void GetGlobalKeys_EnumeratesAllKeys()
    {
        var sut = new StateManager();
        sut.SetGlobal("a", 1);
        sut.SetGlobal("b", 2);

        var keys = sut.GetGlobalKeys().ToHashSet();
        Assert.Equal(new HashSet<string> { "a", "b" }, keys);
    }

    [Fact]
    public async Task PersistAsync_LoadAsync_Roundtrip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"state-{Guid.NewGuid():N}.json");
        try
        {
            var writer = new StateManager(null, path);
            writer.SetGlobal("mode", "auto");
            writer.SetGlobal("count", 42);
            writer.SetRuleState("rule-1", "counter", 7);

            await writer.PersistAsync();

            var reader = new StateManager(null, path);
            await reader.LoadAsync();

            Assert.Equal("auto", reader.GetGlobal<string>("mode"));
            // JSON round-trip coerces numeric types via JsonElement; the
            // current implementation converts back through deserialize, so
            // the type may differ from the original but the value should
            // parse via the typed accessor.
            Assert.Equal(42, reader.GetGlobal<int>("count"));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task ConcurrentSetDeviceState_DoesNotThrow()
    {
        // Stress the write path: many concurrent writers on different
        // devices must not throw. Per-device contention is acceptable to
        // lose-the-last-writer on the current design, but the manager must
        // not corrupt its internal ConcurrentDictionary.
        var sut = new StateManager();

        var tasks = Enumerable.Range(0, 64).Select(i => Task.Run(() =>
        {
            for (var j = 0; j < 100; j++)
            {
                sut.SetDeviceState(
                    $"device-{i % 8}",
                    new Dictionary<string, object> { ["n"] = j, ["w"] = i });
            }
        }));

        await Task.WhenAll(tasks);

        // Every device should have a final state visible.
        for (var i = 0; i < 8; i++)
        {
            var state = sut.GetDeviceState($"device-{i}");
            Assert.NotNull(state);
            Assert.Contains("n", state);
        }
    }

    [Fact]
    public void GetGlobal_JsonElement_DeserialisesThroughTyped()
    {
        // After a Persist/Load cycle, globals come back as JsonElement. The
        // typed accessor must handle that transparently.
        var sut = new StateManager();
        var element = JsonDocument.Parse("{\"nested\":{\"value\":7}}").RootElement;
        sut.SetGlobal("blob", element);

        var typed = sut.GetGlobal<Dictionary<string, object>>("blob");
        Assert.NotNull(typed);
    }

    [Fact]
    public void GetGlobal_IncompatibleCast_ReturnsDefault()
    {
        // Storing a string and asking for it typed as int should fall
        // through the JSON coerce path and return default(int)=0 rather
        // than throwing.
        var sut = new StateManager();
        sut.SetGlobal("mode", "not-a-number");

        var result = sut.GetGlobal<int>("mode");
        Assert.Equal(0, result);
    }

    [Fact]
    public async Task LoadAsync_CorruptFile_DoesNotThrow()
    {
        // Corrupt persisted state should be swallowed so the process can
        // start with empty state rather than crash-looping.
        var path = Path.Combine(Path.GetTempPath(), $"state-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(path, "{ not valid json");
            var sut = new StateManager(null, path);

            await sut.LoadAsync();

            // Empty is fine; no exception is the assertion here.
            Assert.Empty(sut.GetGlobalKeys());
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task LoadAsync_MissingFile_DoesNotThrow()
    {
        var path = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.json");
        var sut = new StateManager(null, path);

        await sut.LoadAsync();

        Assert.Empty(sut.GetGlobalKeys());
    }
}
