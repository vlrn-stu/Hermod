using Hermod.Core.Interfaces;

namespace Hermod.Infrastructure.Database.Noop;

/// <summary>
/// Used when <c>Hermod:Storage:Mode</c> is <c>Noop</c> / <c>InMemory</c>, or
/// when <c>Hermod:Features:RuleAuditLog</c> is off. Silent discard.
/// </summary>
internal sealed class NoopRuleAuditRepository : IRuleAuditRepository
{
    public Task AppendAsync(
        string ruleId,
        string? topic,
        double elapsedMs,
        bool success,
        string? error,
        int actionCount,
        CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
