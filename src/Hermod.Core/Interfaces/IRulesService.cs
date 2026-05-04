using Hermod.Core.Models.Rules;

namespace Hermod.Core.Interfaces;

/// <summary>
/// Atomic per-rule stats update used by the rules engine flush loop.
/// Avoids the read-modify-write pattern that did two queries per rule.
/// </summary>
/// <param name="RuleId">Rule whose counters are being updated.</param>
/// <param name="DeltaExecutionCount">Increment applied to <see cref="Rule.ExecutionCount"/>.</param>
/// <param name="LastExecutedAt">Timestamp of the most recent firing in this batch, or null if the batch contains only errors.</param>
/// <param name="LastError">Exception message from the most recent failed firing, or null on success.</param>
/// <param name="LastErrorAt">Timestamp that pairs with <paramref name="LastError"/>; null when no error occurred.</param>
public sealed record RuleStatsUpdate(
    string RuleId,
    int DeltaExecutionCount,
    DateTime? LastExecutedAt,
    string? LastError,
    DateTime? LastErrorAt);

/// <summary>
/// Persistent CRUD and query surface for <see cref="Rule"/>. Backed by the
/// Postgres <c>rules</c> table in production. The rules engine loads from
/// here on startup and on explicit reload.
/// </summary>
public interface IRulesService
{
    /// <summary>Returns every rule, enabled and disabled, in insertion order.</summary>
    Task<IEnumerable<Rule>> GetAllRulesAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns the rule with the given <paramref name="id"/>, or null if none exists.</summary>
    Task<Rule?> GetRuleAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Upsert by <see cref="Rule.Id"/>. Stamps <c>UpdatedAt</c> always and
    /// <c>CreatedAt</c> on insert. Returns the stored rule.
    /// </summary>
    Task<Rule> AddOrUpdateRuleAsync(Rule rule, CancellationToken cancellationToken = default);

    /// <summary>Returns false if no rule with <paramref name="id"/> existed.</summary>
    Task<bool> RemoveRuleAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Toggles <see cref="Rule.Enabled"/>. Returns false if no rule with <paramref name="id"/> existed.</summary>
    Task<bool> EnableRuleAsync(string id, bool enabled, CancellationToken cancellationToken = default);

    /// <summary>
    /// Overwrites the <see cref="Rule.State"/> JSONB column. Flushed periodically
    /// from <see cref="IStateManager"/> so rule counters survive coordinator
    /// restarts. Returns false if no rule with <paramref name="id"/> existed.
    /// </summary>
    Task<bool> UpdateRuleStateAsync(string id, Dictionary<string, object> state, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns enabled rules whose trigger topic pattern matches
    /// <paramref name="topic"/> (MQTT <c>+</c>/<c>#</c> wildcards), ordered by
    /// <see cref="Rule.Priority"/> ascending. Disabled rules are filtered out.
    /// </summary>
    Task<IEnumerable<Rule>> GetMatchingRulesAsync(string topic, CancellationToken cancellationToken = default);

    /// <summary>Returns rules carrying the given free-form <paramref name="tag"/>. Useful for filtered dashboards.</summary>
    Task<IEnumerable<Rule>> GetRulesByTagAsync(string tag, CancellationToken cancellationToken = default);

    /// <summary>Returns rules whose trigger is of the given <paramref name="triggerType"/>.</summary>
    Task<IEnumerable<Rule>> GetRulesByTriggerTypeAsync(TriggerType triggerType, CancellationToken cancellationToken = default);

    /// <summary>
    /// Server-side <c>COUNT(*) WHERE enabled = TRUE</c> for the stats
    /// dashboard. Avoids loading every rule row just to count them on
    /// the client.
    /// </summary>
    Task<int> CountActiveAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies a batch of stats deltas in a single round-trip. Replaces
    /// the old read-modify-write loop that fetched each rule, mutated
    /// counters in memory, and wrote it back (2 queries per rule).
    /// </summary>
    Task BulkUpdateStatsAsync(
        IReadOnlyCollection<RuleStatsUpdate> updates,
        CancellationToken cancellationToken = default);
}
