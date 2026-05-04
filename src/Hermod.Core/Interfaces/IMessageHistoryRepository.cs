namespace Hermod.Core.Interfaces;

/// <summary>
/// Persistent MQTT message audit trail. Gated at the call site by
/// <c>Hermod:Features:MessagePersistence</c>; writes only happen when the
/// feature is on. Reads are not exposed here because the feature only needs
/// write-path cost to be measurable for the thesis — any ad-hoc querying
/// goes through direct SQL or a future reporting endpoint.
/// </summary>
public interface IMessageHistoryRepository
{
    /// <summary>
    /// Appends a row to the <c>message_history</c> table. The current
    /// timestamp is stamped by the DB via DEFAULT NOW().
    /// </summary>
    Task AppendAsync(
        string topic,
        string payload,
        int qos,
        bool retained,
        CancellationToken cancellationToken = default);
}
