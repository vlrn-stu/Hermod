using Hermod.Core.Interfaces;
using Hermod.Core.Models.Rules;
using Hermod.Core.Mqtt;
using Hermod.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Hermod.UnitTests;

/// <summary>
/// Pins <see cref="RuleStatePersistenceService"/>'s dirty-tracking flush
/// semantics. The service is on the hot path to Postgres: a regression
/// that flushes every rule on every tick would multiply write load by
/// <c>rule-count</c>. A regression that never flushes loses state across
/// restarts.
/// </summary>
public class RuleStatePersistenceServiceTests
{
    private sealed class MinimalStateManager : IStateManager
    {
        public Dictionary<string, Dictionary<string, object>> RuleStates { get; } = new();

        public IReadOnlyDictionary<string, Dictionary<string, object>> SnapshotRuleStates()
            => RuleStates.ToDictionary(kv => kv.Key, kv => new Dictionary<string, object>(kv.Value));

        public Dictionary<string, object> GetRuleState(string ruleId)
            => RuleStates.TryGetValue(ruleId, out var s) ? s : (RuleStates[ruleId] = new());
        public void SetRuleState(string ruleId, string key, object value) => GetRuleState(ruleId)[key] = value;
        public void ClearRuleState(string ruleId) => RuleStates.Remove(ruleId);
        public bool HasRuleState(string ruleId) => RuleStates.ContainsKey(ruleId);
        public void ImportRuleState(string ruleId, Dictionary<string, object> state)
            => RuleStates[ruleId] = new Dictionary<string, object>(state);
        public T? GetGlobal<T>(string key) => default;
        public T GetGlobal<T>(string key, T defaultValue) => defaultValue;
        public void SetGlobal(string key, object value) { }
        public bool HasGlobal(string key) => false;
        public bool RemoveGlobal(string key) => false;
        public IEnumerable<string> GetGlobalKeys() => Array.Empty<string>();
        public IReadOnlyDictionary<string, object> GetGlobalSnapshot() => new Dictionary<string, object>();
        public Dictionary<string, object>? GetDeviceState(string deviceName) => null;
        public void SetDeviceState(string deviceName, Dictionary<string, object> state) { }
        public Dictionary<string, object>? GetPreviousDeviceState(string deviceName) => null;
        public Task PersistAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class RecordingRulesService : IRulesService
    {
        public Dictionary<string, Rule> Rules { get; } = new();
        public int UpdateCalls;
        public string? ThrowOnUpdateFor { get; set; }
        public string? ReturnFalseFor { get; set; }

        public Task<bool> UpdateRuleStateAsync(string id, Dictionary<string, object> state, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref UpdateCalls);
            if (id == ThrowOnUpdateFor) throw new InvalidOperationException("boom");
            if (id == ReturnFalseFor) return Task.FromResult(false);
            if (!Rules.TryGetValue(id, out var r)) return Task.FromResult(false);
            r.State = new Dictionary<string, object>(state);
            return Task.FromResult(true);
        }

        public Task<IEnumerable<Rule>> GetAllRulesAsync(CancellationToken ct = default)
            => Task.FromResult<IEnumerable<Rule>>(Rules.Values.ToList());
        public Task<Rule?> GetRuleAsync(string id, CancellationToken ct = default)
            => Task.FromResult(Rules.TryGetValue(id, out var r) ? r : null);
        public Task<Rule> AddOrUpdateRuleAsync(Rule rule, CancellationToken ct = default)
        { Rules[rule.Id] = rule; return Task.FromResult(rule); }
        public Task<bool> RemoveRuleAsync(string id, CancellationToken ct = default)
            => Task.FromResult(Rules.Remove(id));
        public Task<bool> EnableRuleAsync(string id, bool enabled, CancellationToken ct = default)
        { if (!Rules.TryGetValue(id, out var r)) return Task.FromResult(false); r.Enabled = enabled; return Task.FromResult(true); }
        public Task<IEnumerable<Rule>> GetMatchingRulesAsync(string topic, CancellationToken ct = default)
            => Task.FromResult<IEnumerable<Rule>>(Rules.Values.Where(r => r.Enabled && MqttTopicMatcher.IsMatch(r.Trigger.TopicPattern, topic)).ToList());
        public Task<IEnumerable<Rule>> GetRulesByTagAsync(string tag, CancellationToken ct = default)
            => Task.FromResult<IEnumerable<Rule>>(Rules.Values.Where(r => r.Tags.Contains(tag)).ToList());
        public Task<IEnumerable<Rule>> GetRulesByTriggerTypeAsync(TriggerType t, CancellationToken ct = default)
            => Task.FromResult<IEnumerable<Rule>>(Rules.Values.Where(r => r.Trigger.Type == t).ToList());
        public Task<int> CountActiveAsync(CancellationToken ct = default)
            => Task.FromResult(Rules.Values.Count(r => r.Enabled));
        public Task BulkUpdateStatsAsync(IReadOnlyCollection<RuleStatsUpdate> updates, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private static (RuleStatePersistenceService svc, MinimalStateManager state, RecordingRulesService rules) Build(params string[] seededRuleIds)
    {
        var state = new MinimalStateManager();
        var rules = new RecordingRulesService();
        foreach (var id in seededRuleIds)
        {
            rules.Rules[id] = new Rule { Id = id, Name = id };
        }
        var svc = new RuleStatePersistenceService(state, rules, NullLogger<RuleStatePersistenceService>.Instance);
        return (svc, state, rules);
    }

    [Fact]
    public async Task FlushAsync_DirtyRule_WritesOnce()
    {
        var (svc, state, rules) = Build("r1");
        state.RuleStates["r1"] = new Dictionary<string, object> { ["counter"] = 1 };

        await svc.FlushAsync(CancellationToken.None);

        Assert.Equal(1, rules.UpdateCalls);
        Assert.Equal(1, rules.Rules["r1"].State["counter"]);
    }

    [Fact]
    public async Task FlushAsync_UnchangedState_SkipsSecondWrite()
    {
        var (svc, state, rules) = Build("r1");
        state.RuleStates["r1"] = new Dictionary<string, object> { ["counter"] = 1 };

        await svc.FlushAsync(CancellationToken.None);
        await svc.FlushAsync(CancellationToken.None);
        await svc.FlushAsync(CancellationToken.None);

        Assert.Equal(1, rules.UpdateCalls);
    }

    [Fact]
    public async Task FlushAsync_StateChangedAfterFlush_WritesAgain()
    {
        var (svc, state, rules) = Build("r1");
        state.RuleStates["r1"] = new Dictionary<string, object> { ["counter"] = 1 };
        await svc.FlushAsync(CancellationToken.None);

        state.RuleStates["r1"]["counter"] = 2;
        await svc.FlushAsync(CancellationToken.None);

        Assert.Equal(2, rules.UpdateCalls);
        Assert.Equal(2, rules.Rules["r1"].State["counter"]);
    }

    [Fact]
    public async Task FlushAsync_MultipleRules_OnlyDirtyRulesWrite()
    {
        var (svc, state, rules) = Build("r1", "r2", "r3");
        state.RuleStates["r1"] = new Dictionary<string, object> { ["v"] = 1 };
        state.RuleStates["r2"] = new Dictionary<string, object> { ["v"] = 2 };
        state.RuleStates["r3"] = new Dictionary<string, object> { ["v"] = 3 };

        await svc.FlushAsync(CancellationToken.None);
        Assert.Equal(3, rules.UpdateCalls);

        state.RuleStates["r2"]["v"] = 99;
        await svc.FlushAsync(CancellationToken.None);

        Assert.Equal(4, rules.UpdateCalls);
    }

    [Fact]
    public async Task FlushAsync_UpdateReturnsFalse_DoesNotMarkHashAsFlushed()
    {
        // If UpdateRuleStateAsync returns false (rule gone), the dedup hash
        // must NOT be stored — otherwise a later retry after the rule
        // re-appears would be skipped on equal content.
        var (svc, state, rules) = Build();
        state.RuleStates["ghost"] = new Dictionary<string, object> { ["x"] = 1 };

        await svc.FlushAsync(CancellationToken.None);
        await svc.FlushAsync(CancellationToken.None);

        Assert.Equal(2, rules.UpdateCalls);
    }

    [Fact]
    public async Task FlushAsync_PerRuleException_DoesNotStopSweep()
    {
        var (svc, state, rules) = Build("r2");
        state.RuleStates["throws"] = new Dictionary<string, object> { ["x"] = 1 };
        state.RuleStates["r2"] = new Dictionary<string, object> { ["x"] = 1 };
        rules.ThrowOnUpdateFor = "throws";

        await svc.FlushAsync(CancellationToken.None);

        Assert.Equal(1, rules.Rules["r2"].State["x"]);
    }
}
