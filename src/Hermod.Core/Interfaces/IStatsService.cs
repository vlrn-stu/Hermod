using Hermod.Core.Models;

namespace Hermod.Core.Interfaces;

/// <summary>
/// Runtime counter surface for the coordinator. Counters live in-process and
/// are persisted/seeded by a separate background flush so they survive restarts.
/// </summary>
public interface IStatsService
{
    /// <summary>Aggregated snapshot (device counts, active rules, counter totals).</summary>
    Task<SystemStats> GetCurrentStatsAsync(CancellationToken cancellationToken = default);

    /// <summary>Counts one successfully processed inbound MQTT message.</summary>
    void IncrementMessagesProcessed();

    /// <summary>
    /// Attributes one processed message to its transport protocol. Feeds
    /// <see cref="ProtocolStats.MessageCount"/>; <see cref="Protocol.Unknown"/>
    /// calls are dropped because the /api/stats/protocols surface filters
    /// Unknown out uniformly.
    /// </summary>
    void IncrementMessagesByProtocol(Protocol protocol);

    /// <summary>Counts one successful rule firing (post-conditions, post-actions).</summary>
    void IncrementRulesExecuted();

    /// <summary>
    /// Increments the drop counter. Exercised when the bounded MQTT intake channel
    /// rejects a message due to backpressure, or any other stage intentionally
    /// drops a message. Feeds <see cref="SystemStats.MessagesDropped"/>.
    /// </summary>
    void IncrementMessagesDropped();

    /// <summary>Counts a rule whose evaluation or action dispatch raised an exception.</summary>
    void IncrementRulesErrored();

    /// <summary>Counts an individual action that returned a non-success result.</summary>
    void IncrementActionsErrored();

    /// <summary>Per-protocol breakdown: device count, message count, translator liveness, last activity.</summary>
    Task<IEnumerable<ProtocolStats>> GetProtocolStatsAsync(CancellationToken cancellationToken = default);

    /// <summary>Seeds the in-memory counters from a persisted source (called once on startup).</summary>
    void SeedCounters(long messagesProcessed, long rulesExecuted, long messagesDropped = 0,
        long rulesErrored = 0, long actionsErrored = 0);

    /// <summary>Returns the current counter values for persistence flushes.</summary>
    (long MessagesProcessed, long RulesExecuted, long MessagesDropped,
     long RulesErrored, long ActionsErrored) GetCounters();

    /// <summary>
    /// Zeros every lifetime counter tracked by the service (in-memory
    /// atomic ints AND the persisted <c>metrics_counters</c> row) so
    /// the dashboard tiles drop back to 0. The windowed rates
    /// (MessagesPerSecond{1m,5m,1h}) are computed from the snapshot
    /// table and stay until the 1h/5m window rolls off.
    /// </summary>
    Task ResetCountersAsync(CancellationToken cancellationToken = default);
}
