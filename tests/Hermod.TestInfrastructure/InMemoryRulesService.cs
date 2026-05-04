using Hermod.Core.Interfaces;
using Hermod.Core.Models.Rules;
using Hermod.Core.Mqtt;

namespace Hermod.TestInfrastructure;

/// <summary>
/// Unified in-memory <see cref="IRulesService"/> fake for test use.
/// Mirror of <see cref="InMemoryDeviceService"/>: supports both
/// construction-based seeding and post-construction mutation, plus
/// a <see cref="ThrowOnGetAll"/> exception hook.
///
/// </summary>
public sealed class InMemoryRulesService : IRulesService
{
    public Dictionary<string, Rule> Rules { get; } = new();

    public Exception? ThrowOnGetAll { get; set; }

    public InMemoryRulesService() { }

    public InMemoryRulesService(IEnumerable<Rule> seed)
    {
        foreach (var r in seed)
        {
            Rules[r.Id] = r;
        }
    }

    public Task<IEnumerable<Rule>> GetAllRulesAsync(CancellationToken cancellationToken = default)
    {
        if (ThrowOnGetAll is not null) throw ThrowOnGetAll;
        return Task.FromResult<IEnumerable<Rule>>(Rules.Values.ToList());
    }

    public Task<Rule?> GetRuleAsync(string id, CancellationToken cancellationToken = default)
        => Task.FromResult(Rules.TryGetValue(id, out var r) ? r : null);

    public Task<Rule> AddOrUpdateRuleAsync(Rule rule, CancellationToken cancellationToken = default)
    {
        Rules[rule.Id] = rule;
        return Task.FromResult(rule);
    }

    public Task<bool> RemoveRuleAsync(string id, CancellationToken cancellationToken = default)
        => Task.FromResult(Rules.Remove(id));

    public Task<bool> EnableRuleAsync(string id, bool enabled, CancellationToken cancellationToken = default)
    {
        if (!Rules.TryGetValue(id, out var r)) return Task.FromResult(false);
        r.Enabled = enabled;
        return Task.FromResult(true);
    }

    public Task<bool> UpdateRuleStateAsync(string id, Dictionary<string, object> state, CancellationToken cancellationToken = default)
    {
        if (!Rules.TryGetValue(id, out var r)) return Task.FromResult(false);
        r.State = new Dictionary<string, object>(state);
        return Task.FromResult(true);
    }

    public Task<IEnumerable<Rule>> GetMatchingRulesAsync(string topic, CancellationToken cancellationToken = default)
        => Task.FromResult<IEnumerable<Rule>>(
            Rules.Values
                .Where(r => r.Enabled && MqttTopicMatcher.IsMatch(r.Trigger.TopicPattern, topic))
                .OrderBy(r => r.Priority)
                .ToList());

    public Task<IEnumerable<Rule>> GetRulesByTagAsync(string tag, CancellationToken cancellationToken = default)
        => Task.FromResult<IEnumerable<Rule>>(
            Rules.Values.Where(r => r.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase)).ToList());

    public Task<IEnumerable<Rule>> GetRulesByTriggerTypeAsync(TriggerType triggerType, CancellationToken cancellationToken = default)
        => Task.FromResult<IEnumerable<Rule>>(
            Rules.Values.Where(r => r.Trigger.Type == triggerType).ToList());

    public Task<int> CountActiveAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(Rules.Values.Count(r => r.Enabled));

    public Task BulkUpdateStatsAsync(IReadOnlyCollection<RuleStatsUpdate> updates, CancellationToken cancellationToken = default)
    {
        foreach (var u in updates)
        {
            if (!Rules.TryGetValue(u.RuleId, out var rule)) continue;
            rule.ExecutionCount += u.DeltaExecutionCount;
            rule.LastExecutedAt = u.LastExecutedAt;
            if (u.LastError is not null)
            {
                rule.LastError = u.LastError;
                rule.LastErrorAt = u.LastErrorAt;
            }
        }
        return Task.CompletedTask;
    }
}
