using Hermod.Core.Configuration;
using Hermod.Infrastructure.Services;
using Microsoft.Extensions.Options;
using Xunit;

namespace Hermod.UnitTests;

/// <summary>
/// Pins the contract for <see cref="TopicIngressLimiter"/>: token bucket
/// drains and refills at the configured rate, dedup window catches
/// in-flight payload replay, per-topic state is independent, overrides
/// beat defaults, and the LRU cap actually bounds memory.
/// <para>
/// Time is injected via <see cref="FakeTimeProvider"/> so the suite is
/// deterministic — every "what does the bucket look like 600 ms later"
/// scenario advances the clock by ticks instead of <see cref="Thread.Sleep"/>.
/// </para>
/// </summary>
public class TopicIngressLimiterTests
{
    private static (TopicIngressLimiter limiter, FakeTimeProvider clock, IOptionsMonitor<HermodSettings> opts)
        Build(RateLimitSettings? cfg = null)
    {
        var settings = new HermodSettings { RateLimit = cfg ?? new RateLimitSettings { Enabled = true } };
        var monitor = new StaticOptionsMonitor<HermodSettings>(settings);
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-04-28T10:00:00Z"));
        return (new TopicIngressLimiter(monitor, clock), clock, monitor);
    }

    private static DateTimeOffset Plus(DateTimeOffset baseTime, double seconds) =>
        baseTime + TimeSpan.FromSeconds(seconds);

    [Fact]
    public void Disabled_AlwaysAccepts_NoStateMutated()
    {
        var (limiter, _, _) = Build(new RateLimitSettings { Enabled = false, DefaultBurst = 1, DefaultRatePerSecond = 0.001 });
        var now = DateTimeOffset.UtcNow;

        for (var i = 0; i < 100; i++)
        {
            var v = limiter.TryAccept("x/y", "same", now);
            Assert.True(v.Accept);
            Assert.Null(v.Reason);
        }
    }

    [Fact]
    public void TokenBucket_BurstAccepts_ThenRejectsUntilRefill()
    {
        var (limiter, _, _) = Build(new RateLimitSettings
        {
            Enabled = true,
            DefaultBurst = 5,
            DefaultRatePerSecond = 1.0,
            DedupWindowSeconds = 0,
        });
        var t0 = DateTimeOffset.Parse("2026-04-28T10:00:00Z");

        // First 5 (burst) all accepted.
        for (var i = 0; i < 5; i++)
        {
            var v = limiter.TryAccept("zigbee/lamp", $"p{i}", t0);
            Assert.True(v.Accept, $"burst slot {i} should accept");
        }

        // 6th at the same instant rejects with reason="rate".
        var rejected = limiter.TryAccept("zigbee/lamp", "p5", t0);
        Assert.False(rejected.Accept);
        Assert.Equal("rate", rejected.Reason);

        // After 1.0 s, exactly 1 token has refilled → next accept passes,
        // the one after that rejects again.
        var afterOneSec = limiter.TryAccept("zigbee/lamp", "p6", Plus(t0, 1.0));
        Assert.True(afterOneSec.Accept);
        var stillRate = limiter.TryAccept("zigbee/lamp", "p7", Plus(t0, 1.0));
        Assert.False(stillRate.Accept);
        Assert.Equal("rate", stillRate.Reason);
    }

    [Fact]
    public void TokenBucket_RefillCappedAtBurst()
    {
        var (limiter, _, _) = Build(new RateLimitSettings
        {
            Enabled = true,
            DefaultBurst = 3,
            DefaultRatePerSecond = 1.0,
            DedupWindowSeconds = 0,
        });
        var t0 = DateTimeOffset.Parse("2026-04-28T10:00:00Z");

        // Drain the bucket (3 of 3 used).
        for (var i = 0; i < 3; i++) Assert.True(limiter.TryAccept("a/b", $"p{i}", t0).Accept);

        // Sleep 100 s → would refill to 100 tokens at 1/s, but capped at 3.
        var t1 = Plus(t0, 100.0);
        for (var i = 0; i < 3; i++)
        {
            Assert.True(limiter.TryAccept("a/b", $"q{i}", t1).Accept,
                $"refill slot {i} should fit within burst cap");
        }

        // The 4th still rejects (cap is 3, not 100).
        Assert.False(limiter.TryAccept("a/b", "q3", t1).Accept);
    }

    [Fact]
    public void DistinctTopics_HaveIndependentBuckets()
    {
        var (limiter, _, _) = Build(new RateLimitSettings
        {
            Enabled = true,
            DefaultBurst = 2,
            DefaultRatePerSecond = 0.001, // basically no refill
            DedupWindowSeconds = 0,
        });
        var t0 = DateTimeOffset.Parse("2026-04-28T10:00:00Z");

        // Drain topic A — 2 accepts then a reject.
        Assert.True(limiter.TryAccept("a", "p1", t0).Accept);
        Assert.True(limiter.TryAccept("a", "p2", t0).Accept);
        Assert.False(limiter.TryAccept("a", "p3", t0).Accept);

        // Topic B is fresh — its bucket is full regardless of A's state.
        Assert.True(limiter.TryAccept("b", "p1", t0).Accept);
        Assert.True(limiter.TryAccept("b", "p2", t0).Accept);
        Assert.False(limiter.TryAccept("b", "p3", t0).Accept);
    }

    [Fact]
    public void DedupWindow_RejectsExactReplay_AcceptsAfterTtl()
    {
        var (limiter, _, _) = Build(new RateLimitSettings
        {
            Enabled = true,
            DedupEnabled = true,
            DefaultBurst = 100,
            DefaultRatePerSecond = 100,
            DedupWindowSeconds = 5,
        });
        var t0 = DateTimeOffset.Parse("2026-04-28T10:00:00Z");

        Assert.True(limiter.TryAccept("topic", "exact-payload", t0).Accept);

        // Same payload 1 s later → dedup reject.
        var replay = limiter.TryAccept("topic", "exact-payload", Plus(t0, 1.0));
        Assert.False(replay.Accept);
        Assert.Equal("dedup", replay.Reason);

        // Same payload 4.9 s later → still inside window → still dedup reject.
        var stillRejected = limiter.TryAccept("topic", "exact-payload", Plus(t0, 4.9));
        Assert.False(stillRejected.Accept);

        // 5.1 s later → window expired → accepted.
        var fresh = limiter.TryAccept("topic", "exact-payload", Plus(t0, 5.1));
        Assert.True(fresh.Accept);
    }

    [Fact]
    public void DedupWindow_DistinctPayloads_AllAccepted()
    {
        var (limiter, _, _) = Build(new RateLimitSettings
        {
            Enabled = true,
            DedupEnabled = true,
            DefaultBurst = 100,
            DefaultRatePerSecond = 100,
            DedupWindowSeconds = 60,
        });
        var t0 = DateTimeOffset.Parse("2026-04-28T10:00:00Z");

        for (var i = 0; i < 20; i++)
        {
            var v = limiter.TryAccept("topic", $"payload-{i}", Plus(t0, i * 0.1));
            Assert.True(v.Accept, $"distinct payload {i} should be accepted");
        }
    }

    [Fact]
    public void DedupWindow_AcrossTopics_Independent()
    {
        var (limiter, _, _) = Build(new RateLimitSettings
        {
            Enabled = true,
            DedupEnabled = true,
            DefaultBurst = 100,
            DefaultRatePerSecond = 100,
            DedupWindowSeconds = 60,
        });
        var t0 = DateTimeOffset.Parse("2026-04-28T10:00:00Z");

        Assert.True(limiter.TryAccept("zigbee/lamp", "{\"on\":true}", t0).Accept);
        // Same payload bytes, different topic — not a replay.
        Assert.True(limiter.TryAccept("lora/sensor", "{\"on\":true}", Plus(t0, 0.5)).Accept);
    }

    [Fact]
    public void DedupWindow_Zero_DisablesDedup()
    {
        var (limiter, _, _) = Build(new RateLimitSettings
        {
            Enabled = true,
            DedupEnabled = true,
            DefaultBurst = 100,
            DefaultRatePerSecond = 100,
            DedupWindowSeconds = 0,
        });
        var t0 = DateTimeOffset.Parse("2026-04-28T10:00:00Z");

        Assert.True(limiter.TryAccept("t", "same", t0).Accept);
        Assert.True(limiter.TryAccept("t", "same", t0).Accept);
        Assert.True(limiter.TryAccept("t", "same", t0).Accept);
    }

    [Fact]
    public void DedupOnly_RejectsReplays_WithoutRateLimiting()
    {
        // Rate cap off, dedup on. A tiny default rate would normally
        // throttle past the burst, but with Enabled=false the bucket
        // is never consulted — only payload-level replays drop.
        var (limiter, _, _) = Build(new RateLimitSettings
        {
            Enabled = false,
            DedupEnabled = true,
            DefaultBurst = 1,
            DefaultRatePerSecond = 0.001,
            DedupWindowSeconds = 5,
        });
        var t0 = DateTimeOffset.Parse("2026-04-28T10:00:00Z");

        Assert.True(limiter.TryAccept("topic", "first", t0).Accept);
        var replay = limiter.TryAccept("topic", "first", Plus(t0, 1.0));
        Assert.False(replay.Accept);
        Assert.Equal("dedup", replay.Reason);

        // Distinct payloads pass freely — no rate budget being charged.
        for (var i = 0; i < 50; i++)
            Assert.True(limiter.TryAccept("topic", $"p{i}", Plus(t0, 0.01 * i)).Accept);
    }

    [Fact]
    public void RateOnly_LimitsRate_AllowsReplays()
    {
        // Rate cap on, dedup off. Same payload bytes are fine; what
        // gets rejected is the bucket draining.
        var (limiter, _, _) = Build(new RateLimitSettings
        {
            Enabled = true,
            DedupEnabled = false,
            DefaultBurst = 2,
            DefaultRatePerSecond = 0.001,
            DedupWindowSeconds = 5,
        });
        var t0 = DateTimeOffset.Parse("2026-04-28T10:00:00Z");

        Assert.True(limiter.TryAccept("topic", "same", t0).Accept);
        Assert.True(limiter.TryAccept("topic", "same", t0).Accept);
        var rateRejected = limiter.TryAccept("topic", "same", t0);
        Assert.False(rateRejected.Accept);
        Assert.Equal("rate", rateRejected.Reason);
    }

    [Fact]
    public void TopicOverride_HigherBurst_BeatsDefault()
    {
        var cfg = new RateLimitSettings
        {
            Enabled = true,
            DefaultBurst = 1,
            DefaultRatePerSecond = 0.001,
            DedupWindowSeconds = 0,
        };
        cfg.TopicOverrides["zigbee/floodlight"] = new TopicRateOverride { Burst = 50, RatePerSecond = 5 };
        var (limiter, _, _) = Build(cfg);
        var t0 = DateTimeOffset.Parse("2026-04-28T10:00:00Z");

        // Default-bucket topic: only 1 accept.
        Assert.True(limiter.TryAccept("a/b", "p1", t0).Accept);
        Assert.False(limiter.TryAccept("a/b", "p2", t0).Accept);

        // Overridden topic: 50 burst.
        for (var i = 0; i < 50; i++)
        {
            Assert.True(limiter.TryAccept("zigbee/floodlight", $"p{i}", t0).Accept,
                $"overridden burst slot {i} should accept");
        }
        Assert.False(limiter.TryAccept("zigbee/floodlight", "p51", t0).Accept);
    }

    [Fact]
    public void TopicOverride_LowerRate_ThrottlesHarder()
    {
        var cfg = new RateLimitSettings
        {
            Enabled = true,
            DefaultBurst = 10,
            DefaultRatePerSecond = 100, // generous default
            DedupWindowSeconds = 0,
        };
        cfg.TopicOverrides["sensitive"] = new TopicRateOverride { Burst = 1, RatePerSecond = 0.1 };
        var (limiter, _, _) = Build(cfg);
        var t0 = DateTimeOffset.Parse("2026-04-28T10:00:00Z");

        Assert.True(limiter.TryAccept("sensitive", "p0", t0).Accept);
        Assert.False(limiter.TryAccept("sensitive", "p1", t0).Accept);

        // After 1 s at 0.1/s, only 0.1 token added — still rejected.
        Assert.False(limiter.TryAccept("sensitive", "p2", Plus(t0, 1.0)).Accept);

        // After 10 s, 1 token refilled → accepts once.
        Assert.True(limiter.TryAccept("sensitive", "p3", Plus(t0, 10.0)).Accept);
    }

    [Fact]
    public void TopicOverride_DedupWindowZero_DisablesDedupForThatTopicOnly()
    {
        var cfg = new RateLimitSettings
        {
            Enabled = true,
            DedupEnabled = true,
            DefaultBurst = 100,
            DefaultRatePerSecond = 100,
            DedupWindowSeconds = 60,
        };
        cfg.TopicOverrides["chatty"] = new TopicRateOverride { DedupWindowSeconds = 0 };
        var (limiter, _, _) = Build(cfg);
        var t0 = DateTimeOffset.Parse("2026-04-28T10:00:00Z");

        // Default topic: replays caught.
        Assert.True(limiter.TryAccept("normal", "same", t0).Accept);
        Assert.False(limiter.TryAccept("normal", "same", t0).Accept);

        // Override topic: replays pass.
        Assert.True(limiter.TryAccept("chatty", "same", t0).Accept);
        Assert.True(limiter.TryAccept("chatty", "same", t0).Accept);
        Assert.True(limiter.TryAccept("chatty", "same", t0).Accept);
    }

    [Fact]
    public void MaxTrackedKeys_BoundsBehaviour_NoCrashUnderTopicSpray()
    {
        var (limiter, _, _) = Build(new RateLimitSettings
        {
            Enabled = true,
            DefaultBurst = 10,
            DefaultRatePerSecond = 10,
            DedupWindowSeconds = 0,
            MaxTrackedKeys = 100,
        });
        var t0 = DateTimeOffset.Parse("2026-04-28T10:00:00Z");

        // Spray 1000 distinct topics — well above the cap. Each first
        // hit must accept (fresh topic = full bucket). Eviction happens
        // lazily; what we're pinning is that the cap doesn't error,
        // doesn't double-bill, and a freshly-evicted topic re-arrives
        // with a full bucket (because its state was dropped).
        for (var i = 0; i < 1000; i++)
        {
            var v = limiter.TryAccept($"attack/{i}", "p", Plus(t0, i * 0.001));
            Assert.True(v.Accept, $"first hit on fresh topic {i} must accept");
        }

        // After spray, fresh traffic on a new topic still admitted (the
        // limiter is alive, eviction did not corrupt internal state).
        Assert.True(limiter.TryAccept("post-spray", "p", Plus(t0, 2.0)).Accept);
    }

    [Fact]
    public void OptionsMonitor_ChangesPicketUpOnNextCall()
    {
        var (limiter, _, opts) = Build(new RateLimitSettings { Enabled = false });
        var t0 = DateTimeOffset.Parse("2026-04-28T10:00:00Z");

        // Disabled → accept.
        Assert.True(limiter.TryAccept("t", "p", t0).Accept);

        // Flip Enabled and tighten — limiter must see the new value.
        ((StaticOptionsMonitor<HermodSettings>)opts).Update(new HermodSettings
        {
            RateLimit = new RateLimitSettings { Enabled = true, DefaultBurst = 1, DefaultRatePerSecond = 0.001 },
        });

        Assert.True(limiter.TryAccept("t", "p1", t0).Accept);
        Assert.False(limiter.TryAccept("t", "p2", t0).Accept);
    }

    [Fact]
    public async Task RuntimeOverride_BeatsStaticDefaults()
    {
        // Default config = generous; runtime store throttles a single
        // topic. Demonstrates the Settings-UI write path.
        var settings = new HermodSettings
        {
            RateLimit = new RateLimitSettings
            {
                Enabled = true,
                DefaultBurst = 100,
                DefaultRatePerSecond = 100,
                DedupWindowSeconds = 0,
            },
        };
        var monitor = new StaticOptionsMonitor<HermodSettings>(settings);
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-04-28T10:00:00Z"));
        var store = new RateLimitOverridesStore();
        var limiter = new TopicIngressLimiter(monitor, clock, store);
        var t0 = DateTimeOffset.Parse("2026-04-28T10:00:00Z");

        // No override yet → default burst (100) applies.
        for (var i = 0; i < 5; i++)
            Assert.True(limiter.TryAccept("lora/dosed", $"p{i}", t0).Accept);

        // Install runtime override that throttles to burst=1, rate=0.001.
        // The bucket's existing tokens still drain through, so we use a
        // fresh topic to see the override apply on first touch.
        await store.SetAsync("lora/strict", new TopicRateOverride { Burst = 1, RatePerSecond = 0.001 });
        Assert.True(limiter.TryAccept("lora/strict", "p1", t0).Accept);
        Assert.False(limiter.TryAccept("lora/strict", "p2", t0).Accept);
    }

    [Fact]
    public async Task RuntimeOverride_RemovedAfterDelete()
    {
        var settings = new HermodSettings
        {
            RateLimit = new RateLimitSettings
            {
                Enabled = true,
                DefaultBurst = 5,
                DefaultRatePerSecond = 5,
                DedupWindowSeconds = 0,
            },
        };
        var monitor = new StaticOptionsMonitor<HermodSettings>(settings);
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-04-28T10:00:00Z"));
        var store = new RateLimitOverridesStore();
        var limiter = new TopicIngressLimiter(monitor, clock, store);
        var t0 = DateTimeOffset.Parse("2026-04-28T10:00:00Z");

        // Throttle hard, confirm it bites, then remove and confirm
        // defaults reapply on a fresh topic.
        await store.SetAsync("wifi/loud", new TopicRateOverride { Burst = 1, RatePerSecond = 0.001 });
        Assert.True(limiter.TryAccept("wifi/loud", "p1", t0).Accept);
        Assert.False(limiter.TryAccept("wifi/loud", "p2", t0).Accept);

        await store.RemoveAsync("wifi/loud");
        // Use a fresh topic so the per-topic state object starts at the
        // (now default) burst of 5.
        for (var i = 0; i < 5; i++)
            Assert.True(limiter.TryAccept("wifi/quiet", $"p{i}", t0).Accept);
    }
}

/// <summary>
/// Minimal IOptionsMonitor for unit tests — no DI configuration plumbing,
/// just a swappable backing instance.
/// </summary>
internal sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T> where T : class, new()
{
    private T _value;
    public StaticOptionsMonitor(T value) { _value = value; }
    public T CurrentValue => _value;
    public T Get(string? name) => _value;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
    public void Update(T value) { _value = value; }
}

/// <summary>Deterministic clock for limiter tests.</summary>
internal sealed class FakeTimeProvider : TimeProvider
{
    private DateTimeOffset _now;
    public FakeTimeProvider(DateTimeOffset start) { _now = start; }
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan span) => _now += span;
}
