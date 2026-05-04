namespace Hermod.Core.Interfaces;

/// <summary>
/// Per-firing rule audit log. Gated at the call site by
/// <c>Hermod:Features:RuleAuditLog</c>. Separate from the in-memory rule
/// stats flush (which updates counters on the <c>rules</c> row) because
/// this table keeps the full history, one row per firing, for after-the-
/// fact analysis.
/// </summary>
public interface IRuleAuditRepository
{
    /// <summary>Appends one audit row for a single rule firing.</summary>
    /// <param name="ruleId">Rule id that fired.</param>
    /// <param name="topic">Source MQTT topic, or null for non-message triggers (cron, chain, startup).</param>
    /// <param name="elapsedMs">Wall-clock cost of the firing, including action dispatch, in milliseconds.</param>
    /// <param name="success">True when no action raised and every action reported success.</param>
    /// <param name="error">Exception message when <paramref name="success"/> is false; null otherwise.</param>
    /// <param name="actionCount">Number of action nodes the engine executed for this firing.</param>
    /// <param name="cancellationToken">Request-scoped cancellation.</param>
    Task AppendAsync(
        string ruleId,
        string? topic,
        double elapsedMs,
        bool success,
        string? error,
        int actionCount,
        CancellationToken cancellationToken = default);
}
