using System.Text.Json;
using global::LoRa2MQTT.Service.Configuration;
using global::LoRa2MQTT.Service.Models;
using global::LoRa2MQTT.Service.Services;
using Microsoft.Extensions.Options;
using Xunit;
using Xunit.Abstractions;

namespace Hermod.LoRa2MQTT.IntegrationTests;

/// <summary>
/// End-to-end attack battery. Transmits hostile payloads over real LoRa
/// on ACM1, receives them on ACM0, then feeds each received message to
/// the production <see cref="LoRaMessageGuard"/>. The expectation is
/// that every attack we know how to catch gets rejected, and we also
/// document the ones we *don't* catch (notably LoRa-level source
/// spoofing) as verified gaps rather than assumed strengths.
/// </summary>
[Collection(nameof(HardwareCollection))]
[Trait("Category", "Hardware")]
[Trait("Category", "Security")]
public class AttackGuardTests
{
    private readonly HardwareFixture _hw;
    private readonly ITestOutputHelper _out;

    public AttackGuardTests(HardwareFixture hw, ITestOutputHelper output)
    {
        _hw = hw;
        _out = output;
    }

    [Fact]
    public void Oversize_JumboPayload_ZeroFragmentsReachTheGuard()
    {
        if (!_hw.Available) { _out.WriteLine($"SKIP: {_hw.UnavailableReason}"); return; }
        _hw.Receiver.Drain();

        const int attackSize = 600;
        var prefix = "{\"addr\":100,\"atk\":\"oversize\",\"fill\":\"";
        var suffix = "\"}";
        var jumbo = prefix + new string('A', attackSize - prefix.Length - suffix.Length) + suffix;

        _hw.Sender.SendFrame(jumbo);
        var received = _hw.Receiver.Collect(TimeSpan.FromSeconds(6));
        var attackFragments = received.Where(m => m.Payload.Contains("AAAA")).ToList();

        _out.WriteLine($"sent 1× {jumbo.Length}B jumbo; {_hw.Receiver.CapFlushCount} cap-flushes dropped pre-guard; {attackFragments.Count} fragment(s) reached guard");

        var accepted = 0;
        foreach (var m in attackFragments)
        {
            var v = _hw.Guard.Inspect(m);
            _out.WriteLine($"  fragment {m.Payload.Length}B → {(v.Accept ? "ACCEPT" : "DROP: " + v.Reason)}");
            if (v.Accept) accepted++;
        }

        Assert.True(_hw.Receiver.CapFlushCount > 0, "expected the cap-flush drop path to fire at least once");
        Assert.Equal(0, accepted);
    }

    [Fact]
    public void Oversize_PaddedJustOverCap_GuardStillDrops()
    {
        if (!_hw.Available) { _out.WriteLine($"SKIP: {_hw.UnavailableReason}"); return; }
        _hw.Receiver.Drain();
        var body = new string('Z', 260);
        var payload = $"{{\"addr\":100,\"atk\":\"just-over\",\"body\":\"{body}\"}}";
        Assert.True(payload.Length > _hw.SecurityOptions.MaxPayloadBytes);
        Assert.True(payload.Length < 350, "keep within one over-the-air frame so we don't conflate with fragmentation");

        _hw.Sender.SendFrame(payload);
        var received = _hw.Receiver
            .Collect(TimeSpan.FromSeconds(4))
            .Where(m => m.Payload.Contains("just-over"))
            .ToList();

        _out.WriteLine($"{received.Count} frame(s) reached receiver for a {payload.Length}B overcap attack");
        foreach (var m in received)
        {
            var v = _hw.Guard.Inspect(m);
            _out.WriteLine($"  {m.Payload.Length}B → {(v.Accept ? "ACCEPT" : "DROP: " + v.Reason)}");
            Assert.False(v.Accept, "guard must reject frames larger than MaxPayloadBytes");
        }
    }

    [Fact]
    public void PartialFrame_NoTerminator_ReceiverDiscardsSilently()
    {
        if (!_hw.Available) { _out.WriteLine($"SKIP: {_hw.UnavailableReason}"); return; }
        _hw.Receiver.Drain();
        var pre = _hw.Receiver.CapFlushCount;
        _hw.Sender.SendRaw(System.Text.Encoding.ASCII.GetBytes(new string('x', 260)));

        Thread.Sleep(1500);

        var messages = _hw.Receiver.Snapshot();
        _out.WriteLine($"partial-frame attack: {messages.Count} emitted messages, cap-flush count {pre} → {_hw.Receiver.CapFlushCount}");
        Assert.Empty(messages);
        Assert.True(_hw.Receiver.CapFlushCount > pre, "cap-flush drop path must fire for no-terminator transmissions");
    }

    [Fact]
    public void ControlCharPayload_RaisesNoException_AndIsGuardable()
    {
        if (!_hw.Available) { _out.WriteLine($"SKIP: {_hw.UnavailableReason}"); return; }
        _hw.Receiver.Drain();
        var ugly = "{\"addr\":100,\"atk\":\"ctrl\",\"bad\":\"\x01\x02\x07\x1b[31mRED\"}";
        _hw.Sender.SendFrame(ugly);

        Assert.True(_hw.Receiver.TryRead(out var msg, TimeSpan.FromSeconds(3)),
            "control-char frame should still round-trip, the parser does not filter bytes");
        var v = _hw.Guard.Inspect(msg);
        _out.WriteLine($"control-char frame: len={msg.Payload.Length}, guard={v.Accept}");
        Assert.True(v.Accept, "guard is byte-agnostic by design, it only checks length/address/rate/dedup");
    }

    [Fact]
    public void MultiAddressFlood_OneAddressHitsLimit_OthersKeepFlowing()
    {
        var secOpts = new LoRaSecurityOptions
        {
            MaxPayloadBytes = 256,
            MaxMessagesPerMinutePerAddress = 10,
            DedupWindowSeconds = 0,
            AddressAllowlist = Array.Empty<int>(),
        };
        var guard = new LoRaMessageGuard(Options.Create(secOpts));

        var now = DateTimeOffset.UtcNow;
        var attackerAccepts = 0;
        var legitimateAccepts = 0;

        for (var i = 0; i < 20; i++)
        {
            var atk = new LoRaMessage { Address = 100, Payload = $"p-{i}" };
            if (guard.Inspect(atk, now.AddSeconds(0.1 * i)).Accept) attackerAccepts++;

            var legit = new LoRaMessage { Address = 200, Payload = $"l-{i}" };
            if (guard.Inspect(legit, now.AddSeconds(0.1 * i)).Accept) legitimateAccepts++;
        }

        _out.WriteLine($"per-address isolation: attacker(addr=100) accepted {attackerAccepts}/20, legit(addr=200) accepted {legitimateAccepts}/20");
        Assert.Equal(10, attackerAccepts);
        Assert.Equal(10, legitimateAccepts);
    }

    [Fact]
    public void AllowlistEnforced_UnknownAddressDroppedEvenIfWellFormed()
    {
        var secOpts = new LoRaSecurityOptions
        {
            MaxPayloadBytes = 256,
            MaxMessagesPerMinutePerAddress = 0,
            DedupWindowSeconds = 0,
            AddressAllowlist = new[] { 10, 20, 30 },
        };
        var guard = new LoRaMessageGuard(Options.Create(secOpts));

        var accepted = new LoRaMessage { Address = 20, Payload = "ok" };
        var rejected = new LoRaMessage { Address = 999, Payload = "ok" };
        Assert.True(guard.Inspect(accepted).Accept);
        var v = guard.Inspect(rejected);
        Assert.False(v.Accept);
        _out.WriteLine($"allowlist reject reason: {v.Reason}");
    }

    [Fact]
    public void Replay_IdenticalPayloadWithinWindow_GuardKeepsFirstDropsRest()
    {
        if (!_hw.Available) { _out.WriteLine($"SKIP: {_hw.UnavailableReason}"); return; }
        _hw.Receiver.Drain();
        var nonce = Guid.NewGuid().ToString("N")[..6];
        var payload = $"{{\"addr\":100,\"atk\":\"replay\",\"nonce\":\"{nonce}\"}}";

        for (var i = 0; i < 6; i++)
        {
            _hw.Sender.SendFrame(payload);
            Thread.Sleep(250);
        }

        var received = _hw.Receiver
            .Collect(TimeSpan.FromSeconds(3))
            .Where(m => m.Payload.Contains(nonce))
            .ToList();

        _out.WriteLine($"received {received.Count} replayed frames, now asking the guard");

        var guard = new LoRaMessageGuard(Options.Create(_hw.SecurityOptions));
        var accepted = 0;
        var rejected = 0;
        foreach (var m in received)
        {
            m.Address = 100;
            var v = guard.Inspect(m);
            if (v.Accept) accepted++; else rejected++;
            _out.WriteLine($"  {(v.Accept ? "ACCEPT" : "DROP: " + v.Reason)}");
        }

        Assert.True(received.Count >= 3, $"replay attack requires >=3 arrivals to be meaningful (got {received.Count})");
        Assert.Equal(1, accepted);
        Assert.True(rejected >= 2, $"expected >=2 drops for replays, got {rejected}");
    }

    [Fact]
    public void Flood_BurstAboveRateLimit_GuardDropsOnceCapReached()
    {
        if (!_hw.Available) { _out.WriteLine($"SKIP: {_hw.UnavailableReason}"); return; }
        _hw.Receiver.Drain();

        var secOpts = new LoRaSecurityOptions
        {
            MaxPayloadBytes = 256,
            MaxMessagesPerMinutePerAddress = 20,
            DedupWindowSeconds = 0,
            AddressAllowlist = Array.Empty<int>(),
        };
        var guard = new LoRaMessageGuard(Options.Create(secOpts));

        const int burst = 80;
        for (var i = 0; i < burst; i++)
        {
            _hw.Sender.SendFrame($"{{\"addr\":100,\"atk\":\"flood\",\"seq\":{i}}}");
        }

        var received = _hw.Receiver
            .Collect(TimeSpan.FromSeconds(6))
            .Where(m => m.Payload.Contains("\"atk\":\"flood\""))
            .ToList();

        _out.WriteLine($"received {received.Count}/{burst} flood frames");

        var accepted = 0;
        var dropped = 0;
        var now = DateTimeOffset.UtcNow;
        foreach (var m in received)
        {
            m.Address = 100;
            var v = guard.Inspect(m, now);
            if (v.Accept) accepted++; else dropped++;
        }

        _out.WriteLine($"guard verdict: {accepted} accepted / {dropped} dropped (limit 20/min)");
        Assert.True(received.Count > 20, $"flood did not generate enough traffic ({received.Count}); SF may be too high");
        Assert.True(accepted <= 20, $"guard accepted {accepted} messages but rate cap was 20");
        Assert.True(dropped > 0, "guard should drop something once the rate limit is hit");
    }

    [Fact]
    public void Spoof_ClaimsDifferentAddressInPayload_DocumentsTheAllowlistGap()
    {
        if (!_hw.Available) { _out.WriteLine($"SKIP: {_hw.UnavailableReason}"); return; }
        _hw.Receiver.Drain();
        var spoofed = new { addr = 1, atk = "spoof", dev = "i-am-legit", temp = 21.0 };
        var payload = JsonSerializer.Serialize(spoofed);
        _hw.Sender.SendFrame(payload);

        Assert.True(_hw.Receiver.TryRead(out var msg, TimeSpan.FromSeconds(3)),
            "spoofed frame did not arrive on the receiver");

        var secOpts = new LoRaSecurityOptions
        {
            MaxPayloadBytes = 256,
            MaxMessagesPerMinutePerAddress = 0,
            DedupWindowSeconds = 0,
            AddressAllowlist = new[] { 1 },
        };
        var guard = new LoRaMessageGuard(Options.Create(secOpts));

        msg.Address = 0;
        var verdictReceiverAddr = guard.Inspect(msg);

        msg.Address = 1;
        var verdictClaimedAddr = guard.Inspect(msg);

        _out.WriteLine($"payload claims addr=1; message.Address assigned by adapter = receiver_addr(0) → {verdictReceiverAddr.Reason}");
        _out.WriteLine($"if a future bridge trusts payload addr=1 → {(verdictClaimedAddr.Accept ? "ACCEPTED" : "DROP: " + verdictClaimedAddr.Reason)}");

        Assert.False(verdictReceiverAddr.Accept, "allowlist should drop when assigned address is not in allowlist");
        Assert.True(verdictClaimedAddr.Accept, "if the bridge ever trusts the in-payload address, an attacker can spoof — flag this as a design issue");
    }
}
