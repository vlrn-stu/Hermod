using Hermod.Core.Interfaces;

namespace Hermod.Infrastructure.Database.Noop;

/// <summary>
/// Used when <c>Hermod:Storage:Mode</c> is <c>Noop</c> / <c>InMemory</c>, or
/// when <c>Hermod:Features:MessagePersistence</c> is off. Either way, the
/// write is discarded silently; the thesis cares about whether the call
/// happens at all, and that's gated at the caller.
/// </summary>
internal sealed class NoopMessageHistoryRepository : IMessageHistoryRepository
{
    public Task AppendAsync(
        string topic,
        string payload,
        int qos,
        bool retained,
        CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
