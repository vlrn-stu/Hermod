using Hermod.Core.Models;

namespace Hermod.Core.Configuration;

/// <summary>
/// Runtime-mutable store of per-protocol flow-limit overrides used by
/// <c>ProtocolFlowLimiter</c>. The static
/// <see cref="ProtocolFlowSettings.Limits"/> dictionary remains the
/// floor across restarts; overrides registered here at runtime take
/// precedence so an operator can throttle a noisy or sensitive
/// protocol without redeploying. Overrides are in-memory only and
/// reset on pod restart by design.
/// </summary>
public interface IProtocolFlowOverridesStore
{
    /// <summary>Snapshot of the current overrides keyed by <see cref="Protocol"/>. Hot-path safe — reads from the in-memory cache hydrated by <see cref="LoadAsync"/>.</summary>
    IReadOnlyDictionary<Protocol, ProtocolFlowOverride> Snapshot();

    /// <summary>
    /// Resolve the override for <paramref name="protocol"/>, returning
    /// <c>null</c> when no runtime override is registered for it.
    /// </summary>
    ProtocolFlowOverride? TryGet(Protocol protocol);

    /// <summary>Upsert a per-protocol override and persist it. In-memory cache updates only after the persistence write succeeds.</summary>
    Task SetAsync(Protocol protocol, ProtocolFlowOverride value, CancellationToken cancellationToken = default);

    /// <summary>Drop the override for <paramref name="protocol"/> across cache and persistence.</summary>
    Task<bool> RemoveAsync(Protocol protocol, CancellationToken cancellationToken = default);

    /// <summary>Drop every override across cache and persistence.</summary>
    Task ClearAsync(CancellationToken cancellationToken = default);

    /// <summary>Hydrate the in-memory cache from persistent storage. Called once on coord boot.</summary>
    Task LoadAsync(CancellationToken cancellationToken = default);
}
