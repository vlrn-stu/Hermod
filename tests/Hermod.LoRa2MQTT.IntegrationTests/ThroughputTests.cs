using System.Diagnostics;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace Hermod.LoRa2MQTT.IntegrationTests;

[Collection(nameof(HardwareCollection))]
[Trait("Category", "Hardware")]
[Trait("Category", "Throughput")]
public class ThroughputTests
{
    private readonly HardwareFixture _hw;
    private readonly ITestOutputHelper _out;

    public ThroughputTests(HardwareFixture hw, ITestOutputHelper output)
    {
        _hw = hw;
        _out = output;
    }

    [Fact]
    public void Burst_MeasuresRawAirCapacityAndBlindFloodLossRatio()
    {
        if (!_hw.Available) { _out.WriteLine($"SKIP: {_hw.UnavailableReason}"); return; }
        _hw.Receiver.Drain();
        const int burst = 200;
        const int payloadBytes = 48;

        var sw = Stopwatch.StartNew();
        for (var i = 0; i < burst; i++)
        {
            _hw.Sender.SendFrame(BuildFixedPayload(i, payloadBytes));
        }
        var txElapsed = sw.Elapsed;

        var arrived = _hw.Receiver.Collect(TimeSpan.FromSeconds(10));
        var mine = arrived.Where(m => m.Payload.Contains("\"bench\":\"thru\"")).ToList();

        var txRate = burst / txElapsed.TotalSeconds;
        var rxRate = mine.Count / 10.0;
        var ratio = (double)mine.Count / burst;

        _out.WriteLine($"TX: {burst} frames × {payloadBytes}B in {txElapsed.TotalMilliseconds:F0} ms → {txRate:F1} msg/s at the serial port");
        _out.WriteLine($"RX: {mine.Count} arrived in the 10s collection window → {rxRate:F1} msg/s on-air");
        _out.WriteLine($"delivery ratio: {ratio:P1}");
        _out.WriteLine("INTERPRETATION: a blind sender that ignores airtime budget loses most packets;");
        _out.WriteLine($"the sustainable on-air rate (~{rxRate:F1} msg/s for {payloadBytes}B payloads at this SF) is the effective throughput attacker and defender both have to live with.");

        Assert.True(mine.Count > 0, "nothing arrived — antennas out of range or on different channels?");
        Assert.True(ratio < 1.0, "if delivery ratio is 100% at this burst rate, the test is no longer saturating — increase burst or payload");
    }

    [Fact]
    public void PacedSend_MeasuresSustainableDeliveryRatio()
    {
        if (!_hw.Available) { _out.WriteLine($"SKIP: {_hw.UnavailableReason}"); return; }
        _hw.Receiver.Drain();
        const int count = 25;
        const int intervalMs = 400;
        const int payloadBytes = 48;

        var sw = Stopwatch.StartNew();
        for (var i = 0; i < count; i++)
        {
            _hw.Sender.SendFrame(BuildFixedPayload(i, payloadBytes));
            Thread.Sleep(intervalMs);
        }
        var arrived = _hw.Receiver
            .Collect(TimeSpan.FromSeconds(3))
            .Count(m => m.Payload.Contains("\"bench\":\"thru\""));
        sw.Stop();

        var ratio = (double)arrived / count;
        _out.WriteLine($"paced: {count} frames @ {1000.0/intervalMs:F1} msg/s → {arrived}/{count} arrived ({ratio:P1})");
        Assert.True(ratio > 0.7, $"even paced at {1000.0/intervalMs:F1} msg/s we only got {ratio:P1} — RF setup is unhealthy");
    }

    [Fact]
    public void LargePayloadNearFrameCap_StillDelivers()
    {
        if (!_hw.Available) { _out.WriteLine($"SKIP: {_hw.UnavailableReason}"); return; }
        _hw.Receiver.Drain();
        var body = new string('X', 200);
        var payload = $"{{\"addr\":100,\"bench\":\"large\",\"body\":\"{body}\"}}";
        Assert.True(payload.Length < 240, "keep this case under the 240-byte parser cap");

        _hw.Sender.SendFrame(payload);

        Assert.True(_hw.Receiver.TryRead(out var msg, TimeSpan.FromSeconds(5)),
            $"{payload.Length}-byte frame did not round-trip");
        Assert.Equal(payload, msg.Payload);
        _out.WriteLine($"large-frame ok: {payload.Length} bytes");
    }

    private static string BuildFixedPayload(int seq, int targetBytes)
    {
        var baseline = JsonSerializer.Serialize(new { addr = 100, bench = "thru", seq, pad = "" });
        var padLen = Math.Max(0, targetBytes - baseline.Length);
        var padded = new { addr = 100, bench = "thru", seq, pad = new string('p', padLen) };
        return JsonSerializer.Serialize(padded);
    }
}
