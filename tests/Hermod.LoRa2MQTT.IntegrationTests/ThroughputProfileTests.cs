using System.Diagnostics;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace Hermod.LoRa2MQTT.IntegrationTests;

/// <summary>
/// Sweeps multiple payload sizes × send rates with real RF to build the
/// throughput table used in the report. Each data point is short (1-3 s)
/// so the whole profile runs in well under a minute. Actual numbers go
/// to the xunit output and are pulled by the REPORT.md generator.
/// </summary>
[Collection(nameof(HardwareCollection))]
[Trait("Category", "Hardware")]
[Trait("Category", "Throughput")]
public class ThroughputProfileTests
{
    private readonly HardwareFixture _hw;
    private readonly ITestOutputHelper _out;

    public ThroughputProfileTests(HardwareFixture hw, ITestOutputHelper output)
    {
        _hw = hw;
        _out = output;
    }

    [Theory]
    [InlineData(16, "synthetic-tiny")]
    [InlineData(64, "realistic-sensor")]
    [InlineData(128, "realistic-gateway")]
    [InlineData(220, "synthetic-near-cap")]
    public void UnpacedBurst_MeasureBlindFloodByPayloadSize(int payloadBytes, string profile)
    {
        if (!_hw.Available) { _out.WriteLine($"SKIP: {_hw.UnavailableReason}"); return; }
        _hw.Receiver.Drain();
        const int burst = 150;
        var tag = $"{profile}-{Guid.NewGuid():N}";

        var sw = Stopwatch.StartNew();
        for (var i = 0; i < burst; i++)
        {
            _hw.Sender.SendFrame(ProfiledPayload(profile, tag, i, payloadBytes));
        }
        var txElapsed = sw.Elapsed;

        var arrived = _hw.Receiver
            .Collect(TimeSpan.FromSeconds(8))
            .Where(m => m.Payload.Contains(tag))
            .ToList();

        var txRate = burst / txElapsed.TotalSeconds;
        var rxRate = arrived.Count / 8.0;
        var ratio = (double)arrived.Count / burst;

        _out.WriteLine($"PROFILE profile={profile} bytes={payloadBytes} tx_rate={txRate:F1}/s rx_rate={rxRate:F2}/s ratio={ratio:P1} received={arrived.Count}/{burst}");

        Assert.True(arrived.Count > 0, "no packets arrived — link down?");
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    [InlineData(5.0)]
    public void PacedAt_MeasuresSustainableDelivery(double hz)
    {
        if (!_hw.Available) { _out.WriteLine($"SKIP: {_hw.UnavailableReason}"); return; }
        _hw.Receiver.Drain();
        var tag = Guid.NewGuid().ToString("N")[..8];
        const int payloadBytes = 64;
        var count = Math.Max(12, (int)(hz * 5));
        var delayMs = (int)(1000.0 / hz);

        var sw = Stopwatch.StartNew();
        for (var i = 0; i < count; i++)
        {
            _hw.Sender.SendFrame(ProfiledPayload("paced", tag, i, payloadBytes));
            Thread.Sleep(delayMs);
        }
        sw.Stop();
        var arrived = _hw.Receiver
            .Collect(TimeSpan.FromSeconds(3))
            .Count(m => m.Payload.Contains(tag));

        var ratio = (double)arrived / count;
        _out.WriteLine($"PACED hz={hz} count={count} arrived={arrived} ratio={ratio:P1} wall={sw.Elapsed.TotalSeconds:F1}s");

        Assert.True(arrived > 0, "paced send lost everything — link down?");
    }

    [Fact]
    public void Sustained_FiveSecondsAt_OneHz_DeliversMostFrames()
    {
        if (!_hw.Available) { _out.WriteLine($"SKIP: {_hw.UnavailableReason}"); return; }
        _hw.Receiver.Drain();
        var tag = Guid.NewGuid().ToString("N")[..8];
        const int count = 5;

        for (var i = 0; i < count; i++)
        {
            _hw.Sender.SendFrame(ProfiledPayload("sustained", tag, i, 80));
            Thread.Sleep(1000);
        }
        var arrived = _hw.Receiver
            .Collect(TimeSpan.FromSeconds(2))
            .Count(m => m.Payload.Contains(tag));

        var ratio = (double)arrived / count;
        _out.WriteLine($"SUSTAINED_1HZ count={count} arrived={arrived} ratio={ratio:P1}");
        Assert.True(ratio >= 0.8, $"sustained 1 Hz should deliver ≥80%; got {ratio:P1}");
    }

    private static string ProfiledPayload(string profile, string tag, int seq, int targetBytes)
    {
        var baseline = JsonSerializer.Serialize(new { addr = 100, p = profile, tag, seq, pad = "" });
        var padLen = Math.Max(0, targetBytes - baseline.Length);
        return JsonSerializer.Serialize(new { addr = 100, p = profile, tag, seq, pad = new string('p', padLen) });
    }
}
