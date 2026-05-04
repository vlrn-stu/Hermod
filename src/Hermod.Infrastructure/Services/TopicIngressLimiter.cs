using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Hermod.Core.Configuration;
using Microsoft.Extensions.Options;

namespace Hermod.Infrastructure.Services;

/// <summary>
/// Outcome of a <see cref="ITopicIngressLimiter.TryAccept"/> call.
/// Reason is null when accepted; otherwise "rate" or "dedup" so the
/// caller can split metrics by cause.
/// </summary>
public readonly record struct LimiterVerdict(bool Accept, string? Reason)
{
    /// <summary>Singleton accept verdict.</summary>
    public static LimiterVerdict Allowed { get; } = new(true, null);
    /// <summary>Singleton reject for token-bucket exhaustion.</summary>
    public static LimiterVerdict RateRejected { get; } = new(false, "rate");
    /// <summary>Singleton reject for in-window payload replay.</summary>
    public static LimiterVerdict DedupRejected { get; } = new(false, "dedup");
}

/// <summary>
/// Per-topic ingress limiter. Token-bucket rate cap and exact-payload
/// dedup window run independently — either, both, or neither can be
/// active. LRU-bounded so a topic-spraying attacker cannot OOM the coord.
/// </summary>
public interface ITopicIngressLimiter
{
    /// <summary>Decide whether <paramref name="payload"/> on <paramref name="topic"/> at <paramref name="now"/> should be admitted.</summary>
    LimiterVerdict TryAccept(string topic, string payload, DateTimeOffset now);
}

/// <inheritdoc cref="ITopicIngressLimiter"/>
public sealed class TopicIngressLimiter : ITopicIngressLimiter
{
    private readonly IOptionsMonitor<HermodSettings> _settings;
    private readonly IRateLimitOverridesStore? _runtimeOverrides;
    private readonly TimeProvider _time;

    private readonly ConcurrentDictionary<string, TopicState> _states = new(StringComparer.Ordinal);

    /// <summary>Creates the limiter. <paramref name="time"/> is injectable for deterministic tests.</summary>
    public TopicIngressLimiter(
        IOptionsMonitor<HermodSettings> settings,
        TimeProvider? time = null,
        IRateLimitOverridesStore? runtimeOverrides = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        _runtimeOverrides = runtimeOverrides;
        _time = time ?? TimeProvider.System;
    }

    /// <inheritdoc/>
    public LimiterVerdict TryAccept(string topic, string payload, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(topic);
        ArgumentNullException.ThrowIfNull(payload);
        var cfg = _settings.CurrentValue.RateLimit;
        if (!cfg.Enabled && !cfg.DedupEnabled) return LimiterVerdict.Allowed;

        var (rate, burst, dedupSeconds) = ResolveLimits(cfg, topic, _runtimeOverrides);
        var dedupActive = cfg.DedupEnabled && dedupSeconds > 0;

        // SHA-256 truncated to 16 bytes — collision-resistant enough
        // for a few-second window that holds at most a few hundred
        // entries. Skip the hash entirely when dedup is off.
        string? key = null;
        if (dedupActive)
        {
            Span<byte> hash = stackalloc byte[32];
            var byteCount = Encoding.UTF8.GetByteCount(payload);
            byte[]? rented = null;
            try
            {
                Span<byte> payloadBytes = byteCount <= 256
                    ? stackalloc byte[256]
                    : (rented = System.Buffers.ArrayPool<byte>.Shared.Rent(byteCount));
                payloadBytes = payloadBytes[..byteCount];
                Encoding.UTF8.GetBytes(payload, payloadBytes);
                SHA256.HashData(payloadBytes, hash);
            }
            finally
            {
                if (rented is not null) System.Buffers.ArrayPool<byte>.Shared.Return(rented);
            }
            key = Convert.ToHexString(hash[..16]);
        }

        var state = _states.GetOrAdd(topic, _ => new TopicState(burst, _time.GetUtcNow()));

        lock (state.Sync)
        {
            state.LastTouchedTicks = now.UtcTicks;

            if (dedupActive)
            {
                state.PruneDedup(now);
                if (state.RecentHashes.Contains(key!))
                {
                    return LimiterVerdict.DedupRejected;
                }
            }

            if (cfg.Enabled && !state.Bucket.TryConsume(now, rate, burst))
            {
                return LimiterVerdict.RateRejected;
            }

            if (dedupActive)
            {
                state.RecentHashes.Add(key!);
                state.DedupOrder.Enqueue((key!, now.AddSeconds(dedupSeconds)));
            }
        }

        if (_states.Count > cfg.MaxTrackedKeys)
        {
            EvictOldest(cfg.MaxTrackedKeys);
        }

        return LimiterVerdict.Allowed;
    }

    private static (double rate, int burst, int dedupSeconds) ResolveLimits(
        RateLimitSettings cfg,
        string topic,
        IRateLimitOverridesStore? runtimeStore)
    {
        var rate = cfg.DefaultRatePerSecond > 0 ? cfg.DefaultRatePerSecond : 1.0;
        var burst = cfg.DefaultBurst > 0 ? cfg.DefaultBurst : 1;
        var dedup = cfg.DedupWindowSeconds >= 0 ? cfg.DedupWindowSeconds : 0;

        // Runtime overrides win over static so the Settings UI can tighten
        // a topic without a restart. Both layers accept `foo/#` wildcards.
        var staticOv = MatchOverride(cfg.TopicOverrides, topic);
        if (staticOv is { } so)
        {
            if (so.RatePerSecond > 0) rate = so.RatePerSecond;
            if (so.Burst > 0) burst = so.Burst;
            if (so.DedupWindowSeconds >= 0) dedup = so.DedupWindowSeconds;
        }

        var runtimeOv = runtimeStore is null ? null : MatchOverride(runtimeStore.Snapshot(), topic);
        if (runtimeOv is { } rt)
        {
            if (rt.RatePerSecond > 0) rate = rt.RatePerSecond;
            if (rt.Burst > 0) burst = rt.Burst;
            if (rt.DedupWindowSeconds >= 0) dedup = rt.DedupWindowSeconds;
        }

        return (rate, burst, dedup);
    }

    // Tries exact match first, then the longest matching `foo/#` prefix
    // so `bluetooth/sensor/#` beats `bluetooth/#`.
    internal static TopicRateOverride? MatchOverride(IReadOnlyDictionary<string, TopicRateOverride> overrides, string topic)
    {
        if (overrides.Count == 0) return null;
        if (overrides.TryGetValue(topic, out var exact)) return exact;

        TopicRateOverride? best = null;
        var bestPrefixLength = -1;
        foreach (var kv in overrides)
        {
            var key = kv.Key;
            if (!key.EndsWith("/#", StringComparison.Ordinal)) continue;
            var prefix = key[..^2];
            var matches = topic.Equals(prefix, StringComparison.Ordinal)
                || (topic.Length > prefix.Length
                    && topic.StartsWith(prefix, StringComparison.Ordinal)
                    && topic[prefix.Length] == '/');
            if (matches && prefix.Length > bestPrefixLength)
            {
                best = kv.Value;
                bestPrefixLength = prefix.Length;
            }
        }
        return best;
    }

    private void EvictOldest(int targetCount)
    {
        // Drop the oldest 10% in one pass; cap is soft.
        var toEvict = (_states.Count - targetCount) + (targetCount / 10);
        if (toEvict <= 0) return;

        var snapshot = _states.ToArray();
        Array.Sort(snapshot, (a, b) => a.Value.LastTouchedTicks.CompareTo(b.Value.LastTouchedTicks));

        for (var i = 0; i < toEvict && i < snapshot.Length; i++)
        {
            _states.TryRemove(snapshot[i].Key, out _);
        }
    }

    private sealed class TopicState
    {
        public readonly object Sync = new();
        public TokenBucket Bucket;
        public long LastTouchedTicks;
        public readonly HashSet<string> RecentHashes = new(StringComparer.Ordinal);
        public readonly Queue<(string Hash, DateTimeOffset ExpiresAt)> DedupOrder = new();

        public TopicState(int initialCapacity, DateTimeOffset now)
        {
            Bucket = new TokenBucket(now, initialCapacity);
            LastTouchedTicks = now.UtcTicks;
        }

        public void PruneDedup(DateTimeOffset now)
        {
            // Hashes are enqueued in time order, so the oldest is always at the head.
            while (DedupOrder.Count > 0 && DedupOrder.Peek().ExpiresAt <= now)
            {
                var (oldHash, _) = DedupOrder.Dequeue();
                RecentHashes.Remove(oldHash);
            }
        }
    }

    private struct TokenBucket
    {
        public DateTimeOffset LastRefill;
        public double Tokens;

        public TokenBucket(DateTimeOffset now, int initialCapacity)
        {
            LastRefill = now;
            Tokens = initialCapacity;
        }

        public bool TryConsume(DateTimeOffset now, double ratePerSecond, int capacity)
        {
            var elapsed = (now - LastRefill).TotalSeconds;
            if (elapsed > 0)
            {
                Tokens = Math.Min(capacity, Tokens + (elapsed * ratePerSecond));
                LastRefill = now;
            }

            if (Tokens < 1.0) return false;
            Tokens -= 1.0;
            return true;
        }
    }
}
