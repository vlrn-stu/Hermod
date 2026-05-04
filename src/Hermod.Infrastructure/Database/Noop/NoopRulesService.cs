using Hermod.Core.Interfaces;
using Hermod.Core.Models.Rules;

namespace Hermod.Infrastructure.Database.Noop;

/// <summary>
/// Pass-through rules service used when <c>Hermod:Storage:Mode</c> is
/// <c>Noop</c>. The engine loads zero rules, so every message short-circuits
/// out of rule evaluation immediately. Useful for isolating ingest-pump
/// throughput from rule-engine cost.
/// </summary>
internal sealed class NoopRulesService : IRulesService
{
    public Task<IEnumerable<Rule>> GetAllRulesAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IEnumerable<Rule>>(Array.Empty<Rule>());

    public Task<Rule?> GetRuleAsync(string id, CancellationToken cancellationToken = default)
        => Task.FromResult<Rule?>(null);

    public Task<Rule> AddOrUpdateRuleAsync(Rule rule, CancellationToken cancellationToken = default)
        => Task.FromResult(rule);

    public Task<bool> RemoveRuleAsync(string id, CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    public Task<bool> EnableRuleAsync(string id, bool enabled, CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    public Task<bool> UpdateRuleStateAsync(string id, Dictionary<string, object> state, CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    public Task<IEnumerable<Rule>> GetMatchingRulesAsync(string topic, CancellationToken cancellationToken = default)
        => Task.FromResult<IEnumerable<Rule>>(Array.Empty<Rule>());

    public Task<IEnumerable<Rule>> GetRulesByTagAsync(string tag, CancellationToken cancellationToken = default)
        => Task.FromResult<IEnumerable<Rule>>(Array.Empty<Rule>());

    public Task<IEnumerable<Rule>> GetRulesByTriggerTypeAsync(TriggerType triggerType, CancellationToken cancellationToken = default)
        => Task.FromResult<IEnumerable<Rule>>(Array.Empty<Rule>());

    public Task<int> CountActiveAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(0);

    public Task BulkUpdateStatsAsync(
        IReadOnlyCollection<RuleStatsUpdate> updates,
        CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
