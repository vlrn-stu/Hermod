using LoRa2MQTT.Service.Configuration;
using LoRa2MQTT.Service.Models;
using LoRa2MQTT.Service.Services;
using Microsoft.Extensions.Options;
using Xunit;

namespace Hermod.LoRa2MQTT.UnitTests;

/// <summary>
/// Pins the inbound LoRa guard contract: payload cap, per-address rate
/// limit, dedup window, and allowlist. Each guard is independently
/// enable/disable-able via LoRaSecurityOptions defaults.
/// </summary>
public class LoRaMessageGuardTests
{
    private static LoRaMessageGuard Build(LoRaSecurityOptions options) =>
        new(Options.Create(options));

    private static LoRaMessage Msg(int address, string payload) => new()
    {
        Address = address,
        Channel = 18,
        Payload = payload,
        Rssi = -90
    };

    [Fact]
    public void DefaultOptions_AcceptsReasonableMessage()
    {
        var guard = Build(new LoRaSecurityOptions());

        var verdict = guard.Inspect(Msg(7, "{\"temperature\":21.5}"));

        Assert.True(verdict.Accept);
        Assert.Null(verdict.Reason);
    }

    [Fact]
    public void OversizedPayload_IsDropped()
    {
        var guard = Build(new LoRaSecurityOptions { MaxPayloadBytes = 32 });

        var verdict = guard.Inspect(Msg(7, new string('A', 64)));

        Assert.False(verdict.Accept);
        Assert.Contains("MaxPayloadBytes", verdict.Reason);
    }

    [Fact]
    public void MaxPayloadBytesZero_DisablesSizeCap()
    {
        var guard = Build(new LoRaSecurityOptions { MaxPayloadBytes = 0 });

        var verdict = guard.Inspect(Msg(7, new string('A', 10000)));

        Assert.True(verdict.Accept);
    }

    [Fact]
    public void AddressAllowlist_BlocksNonListedAddress()
    {
        var guard = Build(new LoRaSecurityOptions
        {
            AddressAllowlist = new[] { 10, 20, 30 }
        });

        var verdict = guard.Inspect(Msg(99, "{}"));

        Assert.False(verdict.Accept);
        Assert.Contains("99", verdict.Reason);
    }

    [Fact]
    public void AddressAllowlist_AllowsListedAddress()
    {
        var guard = Build(new LoRaSecurityOptions
        {
            AddressAllowlist = new[] { 10, 20, 30 }
        });

        var verdict = guard.Inspect(Msg(20, "{}"));

        Assert.True(verdict.Accept);
    }

    [Fact]
    public void EmptyAllowlist_AcceptsAllAddresses()
    {
        var guard = Build(new LoRaSecurityOptions
        {
            AddressAllowlist = Array.Empty<int>()
        });

        Assert.True(guard.Inspect(Msg(1, "{}")).Accept);
        Assert.True(guard.Inspect(Msg(500, "{}")).Accept);
    }

    [Fact]
    public void DuplicatePayload_WithinWindow_IsDropped()
    {
        var guard = Build(new LoRaSecurityOptions { DedupWindowSeconds = 5 });
        var t0 = DateTimeOffset.Parse("2026-04-16T12:00:00Z");

        var first = guard.Inspect(Msg(7, "{\"value\":42}"), t0);
        var replay = guard.Inspect(Msg(7, "{\"value\":42}"), t0.AddSeconds(2));

        Assert.True(first.Accept);
        Assert.False(replay.Accept);
        Assert.Contains("duplicate", replay.Reason);
    }

    [Fact]
    public void DuplicatePayload_AfterWindow_IsAccepted()
    {
        var guard = Build(new LoRaSecurityOptions { DedupWindowSeconds = 5 });
        var t0 = DateTimeOffset.Parse("2026-04-16T12:00:00Z");

        guard.Inspect(Msg(7, "{\"value\":42}"), t0);
        var later = guard.Inspect(Msg(7, "{\"value\":42}"), t0.AddSeconds(10));

        Assert.True(later.Accept);
    }

    [Fact]
    public void DifferentPayload_SameAddress_IsAccepted()
    {
        var guard = Build(new LoRaSecurityOptions { DedupWindowSeconds = 5 });
        var t0 = DateTimeOffset.Parse("2026-04-16T12:00:00Z");

        var a = guard.Inspect(Msg(7, "{\"value\":1}"), t0);
        var b = guard.Inspect(Msg(7, "{\"value\":2}"), t0.AddSeconds(1));

        Assert.True(a.Accept);
        Assert.True(b.Accept);
    }

    [Fact]
    public void AlternatingPayloads_BothGetDeduplicatedAcrossRepeats()
    {
        var guard = Build(new LoRaSecurityOptions { DedupWindowSeconds = 5 });
        var t0 = DateTimeOffset.Parse("2026-04-16T12:00:00Z");
        var a = "{\"value\":1}";
        var b = "{\"value\":2}";

        Assert.True(guard.Inspect(Msg(7, a), t0).Accept);
        Assert.True(guard.Inspect(Msg(7, b), t0.AddSeconds(1)).Accept);
        Assert.False(guard.Inspect(Msg(7, a), t0.AddSeconds(2)).Accept);
        Assert.False(guard.Inspect(Msg(7, b), t0.AddSeconds(3)).Accept);
        Assert.False(guard.Inspect(Msg(7, a), t0.AddSeconds(4)).Accept);
    }

    [Fact]
    public void RecentHashes_AgeOutOfDedupWindow_IndependentlyPerPayload()
    {
        var guard = Build(new LoRaSecurityOptions { DedupWindowSeconds = 5 });
        var t0 = DateTimeOffset.Parse("2026-04-16T12:00:00Z");
        var a = "{\"value\":1}";
        var b = "{\"value\":2}";

        Assert.True(guard.Inspect(Msg(7, a), t0).Accept);
        Assert.True(guard.Inspect(Msg(7, b), t0.AddSeconds(4)).Accept);
        Assert.True(guard.Inspect(Msg(7, a), t0.AddSeconds(6)).Accept);
        Assert.False(guard.Inspect(Msg(7, b), t0.AddSeconds(8)).Accept);
        Assert.True(guard.Inspect(Msg(7, b), t0.AddSeconds(10)).Accept);
    }

    [Fact]
    public void DedupWindowZero_DisablesReplayCheck()
    {
        var guard = Build(new LoRaSecurityOptions { DedupWindowSeconds = 0 });
        var t0 = DateTimeOffset.Parse("2026-04-16T12:00:00Z");
        for (var i = 0; i < 10; i++)
        {
            Assert.True(guard.Inspect(Msg(7, "same"), t0.AddMilliseconds(i)).Accept);
        }
    }

    [Fact]
    public void RateLimit_DropsWhenExceeded()
    {
        var guard = Build(new LoRaSecurityOptions
        {
            MaxMessagesPerMinutePerAddress = 3,
            DedupWindowSeconds = 0
        });
        var t0 = DateTimeOffset.Parse("2026-04-16T12:00:00Z");

        Assert.True(guard.Inspect(Msg(7, "{}"), t0).Accept);
        Assert.True(guard.Inspect(Msg(7, "{}"), t0.AddMilliseconds(10)).Accept);
        Assert.True(guard.Inspect(Msg(7, "{}"), t0.AddMilliseconds(20)).Accept);

        var fourth = guard.Inspect(Msg(7, "{}"), t0.AddMilliseconds(30));

        Assert.False(fourth.Accept);
        Assert.Contains("rate limit", fourth.Reason);
    }

    [Fact]
    public void RateLimit_SlidesAfter60Seconds()
    {
        var guard = Build(new LoRaSecurityOptions
        {
            MaxMessagesPerMinutePerAddress = 2,
            DedupWindowSeconds = 0
        });
        var t0 = DateTimeOffset.Parse("2026-04-16T12:00:00Z");

        Assert.True(guard.Inspect(Msg(7, "{}"), t0).Accept);
        Assert.True(guard.Inspect(Msg(7, "{}"), t0.AddSeconds(1)).Accept);
        Assert.False(guard.Inspect(Msg(7, "{}"), t0.AddSeconds(2)).Accept);

        // After the 60 s window slides forward, earlier timestamps age out.
        Assert.True(guard.Inspect(Msg(7, "{}"), t0.AddSeconds(61)).Accept);
    }

    [Fact]
    public void RateLimit_IsPerAddress()
    {
        var guard = Build(new LoRaSecurityOptions
        {
            MaxMessagesPerMinutePerAddress = 1,
            DedupWindowSeconds = 0
        });
        var t0 = DateTimeOffset.Parse("2026-04-16T12:00:00Z");

        Assert.True(guard.Inspect(Msg(7, "{}"), t0).Accept);
        // Address 7 is exhausted; address 8 starts fresh.
        Assert.False(guard.Inspect(Msg(7, "{}"), t0.AddMilliseconds(100)).Accept);
        Assert.True(guard.Inspect(Msg(8, "{}"), t0.AddMilliseconds(100)).Accept);
    }

    [Fact]
    public void RateLimitZero_DisablesRateLimit()
    {
        var guard = Build(new LoRaSecurityOptions
        {
            MaxMessagesPerMinutePerAddress = 0,
            DedupWindowSeconds = 0
        });
        var t0 = DateTimeOffset.Parse("2026-04-16T12:00:00Z");

        for (var i = 0; i < 1000; i++)
        {
            Assert.True(guard.Inspect(Msg(7, "{}"), t0.AddMilliseconds(i)).Accept);
        }
    }
}
