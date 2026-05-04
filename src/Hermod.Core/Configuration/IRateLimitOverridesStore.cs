namespace Hermod.Core.Configuration;

/// <summary>
/// Runtime-mutable store of per-topic rate-limit overrides. The
/// <see cref="HermodSettings.RateLimit"/> values bound from
/// configuration set the floor; overrides registered here at runtime
/// take precedence so an operator can throttle a noisy or sensitive
/// topic without restarting the coordinator. Overrides are in-memory
/// only and reset on pod restart by design — the static
/// <c>TopicOverrides</c> dictionary on <see cref="RateLimitSettings"/>
/// remains the source of truth across restarts.
/// </summary>
public interface IRateLimitOverridesStore
{
    /// <summary>Snapshot of the current overrides, keyed by full topic string. Reads from the in-memory cache that <see cref="LoadAsync"/> hydrated from the persistence layer at startup; safe on the limiter hot path.</summary>
    IReadOnlyDictionary<string, TopicRateOverride> Snapshot();

    /// <summary>
    /// Resolve the override for <paramref name="topic"/>, returning <c>null</c>
    /// when no runtime override is registered for it.
    /// </summary>
    TopicRateOverride? TryGet(string topic);

    /// <summary>Upsert a per-topic override and persist it to the backing store. The in-memory cache is updated only after the persistence write succeeds.</summary>
    Task SetAsync(string topic, TopicRateOverride value, CancellationToken cancellationToken = default);

    /// <summary>Drop the override for <paramref name="topic"/>, both in cache and in the backing store. Returns true when an override existed.</summary>
    Task<bool> RemoveAsync(string topic, CancellationToken cancellationToken = default);

    /// <summary>Drop every override across cache and persistence.</summary>
    Task ClearAsync(CancellationToken cancellationToken = default);

    /// <summary>Hydrate the in-memory cache from persistent storage. Called once on coord boot.</summary>
    Task LoadAsync(CancellationToken cancellationToken = default);
}
