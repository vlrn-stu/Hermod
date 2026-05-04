using System.Collections.Concurrent;
using Hermod.Core.Configuration;
using Hermod.Core.Models;
using Microsoft.Extensions.Options;

namespace Hermod.Infrastructure.Services;

/// <summary>
/// Direction tag for <see cref="IProtocolFlowLimiter.TryAccept"/>. Each
/// (protocol, direction) pair has its own token bucket so an operator
/// can clamp egress to devices independently of ingress from them.
/// </summary>
public enum FlowDirection
{
    /// <summary>Inbound: messages arriving from a translator/broker into the coordinator.</summary>
    Ingress,

    /// <summary>Outbound: messages the coordinator publishes towards a translator/device.</summary>
    Egress,
}

/// <summary>
/// Aggregate per-protocol bidirectional limiter. Sits alongside
/// <see cref="ITopicIngressLimiter"/> as a second line of defence:
/// where the per-topic limiter caps any single chatty topic, this one
/// caps the total inbound and outbound traffic budget for an entire
/// protocol family. Disabled by default; enabling per direction is
/// opt-in via <see cref="ProtocolFlowSettings.Limits"/> or runtime
/// overrides from the Settings page.
/// </summary>
public interface IProtocolFlowLimiter
{
    /// <summary>
    /// Decide whether a message on <paramref name="protocol"/> in the
    /// given <paramref name="direction"/> should be admitted. Returns
    /// <see cref="LimiterVerdict.Allowed"/> when the limiter is off,
    /// when the protocol has no configured limit for that direction,
    /// or when the bucket has tokens; otherwise
    /// <see cref="LimiterVerdict.RateRejected"/>.
    /// </summary>
    LimiterVerdict TryAccept(Protocol protocol, FlowDirection direction, DateTimeOffset now);
}

/// <inheritdoc cref="IProtocolFlowLimiter"/>
public sealed class ProtocolFlowLimiter : IProtocolFlowLimiter
{
    private readonly IOptionsMonitor<HermodSettings> _settings;
    private readonly IProtocolFlowOverridesStore? _runtimeOverrides;

    // One bucket per (protocol, direction). Each bucket has its own lock
    // so the hot path doesn't serialize across protocols. Keyed by a
    // packed long so dictionary lookups don't allocate a tuple key.
    private readonly ConcurrentDictionary<long, BucketState> _buckets = new();

    /// <summary>Creates the limiter with bound settings and an optional runtime-override store.</summary>
    public ProtocolFlowLimiter(
        IOptionsMonitor<HermodSettings> settings,
        IProtocolFlowOverridesStore? runtimeOverrides = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        _runtimeOverrides = runtimeOverrides;
    }

    /// <inheritdoc/>
    public LimiterVerdict TryAccept(Protocol protocol, FlowDirection direction, DateTimeOffset now)
    {
        var cfg = _settings.CurrentValue.RateLimit.ProtocolLimits;
        if (!cfg.Enabled) return LimiterVerdict.Allowed;

        if (!TryResolve(cfg, protocol, direction, out var rate, out var burst))
        {
            // No limit configured for this (protocol, direction) — allow.
            return LimiterVerdict.Allowed;
        }

        var key = PackKey(protocol, direction);
        var state = _buckets.GetOrAdd(key, _ => new BucketState(now, burst));

        lock (state.Sync)
        {
            return state.Bucket.TryConsume(now, rate, burst)
                ? LimiterVerdict.Allowed
                : LimiterVerdict.RateRejected;
        }
    }

    private bool TryResolve(
        ProtocolFlowSettings cfg,
        Protocol protocol,
        FlowDirection direction,
        out double rate,
        out int burst)
    {
        rate = 0;
        burst = 0;

        // Static config first; runtime override (if any) wins on a knob-by-knob basis.
        ProtocolFlowOverride? staticOv = null;
        if (cfg.Limits.TryGetValue(protocol.ToString(), out var found)) staticOv = found;

        var runtimeOv = _runtimeOverrides?.TryGet(protocol);

        // Pick the per-direction pair, preferring runtime knobs when set.
        var pickedRate = direction == FlowDirection.Ingress
            ? PickPositive(runtimeOv?.IngressRatePerSecond, staticOv?.IngressRatePerSecond)
            : PickPositive(runtimeOv?.EgressRatePerSecond, staticOv?.EgressRatePerSecond);
        var pickedBurst = direction == FlowDirection.Ingress
            ? PickPositiveInt(runtimeOv?.IngressBurst, staticOv?.IngressBurst)
            : PickPositiveInt(runtimeOv?.EgressBurst, staticOv?.EgressBurst);

        if (pickedRate is not double r || pickedBurst is not int b) return false;
        rate = r;
        burst = b;
        return true;
    }

    private static double? PickPositive(double? primary, double? fallback)
    {
        if (primary is double p && p > 0) return p;
        if (fallback is double f && f > 0) return f;
        return null;
    }

    private static int? PickPositiveInt(int? primary, int? fallback)
    {
        if (primary is int p && p > 0) return p;
        if (fallback is int f && f > 0) return f;
        return null;
    }

    // Pack (protocol, direction) into a single long so the dictionary key is allocation-free.
    private static long PackKey(Protocol protocol, FlowDirection direction)
        => ((long)(int)protocol << 8) | (long)(int)direction;

    private sealed class BucketState
    {
        public readonly object Sync = new();
        public TokenBucket Bucket;

        public BucketState(DateTimeOffset now, int initialCapacity)
        {
            Bucket = new TokenBucket(now, initialCapacity);
        }
    }

    /// <summary>
    /// Standard token bucket — same shape as <c>TopicIngressLimiter.TokenBucket</c>
    /// but kept private here so the two limiters' bucket invariants don't
    /// drift through a shared edit.
    /// </summary>
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
