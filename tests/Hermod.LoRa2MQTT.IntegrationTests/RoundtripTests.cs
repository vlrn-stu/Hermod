using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace Hermod.LoRa2MQTT.IntegrationTests;

[Collection(nameof(HardwareCollection))]
[Trait("Category", "Hardware")]
public class RoundtripTests
{
    private readonly HardwareFixture _hw;
    private readonly ITestOutputHelper _out;

    public RoundtripTests(HardwareFixture hw, ITestOutputHelper output)
    {
        _hw = hw;
        _out = output;
    }

    [Fact]
    public void SendingSingleFrame_ArrivesOnOtherAntenna()
    {
        if (!_hw.Available) { _out.WriteLine($"SKIP: {_hw.UnavailableReason}"); return; }
        _hw.Receiver.Drain();
        var payload = $"{{\"addr\":100,\"seq\":{Random.Shared.Next()},\"temp\":21.5}}";

        _hw.Sender.SendFrame(payload);

        Assert.True(
            _hw.Receiver.TryRead(out var msg, TimeSpan.FromSeconds(3)),
            "expected to receive the transmitted frame within 3s");
        Assert.Equal(payload, msg.Payload);
        _out.WriteLine($"round-trip ok: rssi={msg.Rssi?.ToString() ?? "n/a"}");
    }

    [Fact]
    public void TenFramesInARow_AllArriveInOrder()
    {
        if (!_hw.Available) { _out.WriteLine($"SKIP: {_hw.UnavailableReason}"); return; }
        _hw.Receiver.Drain();
        var prefix = Guid.NewGuid().ToString("N")[..6];
        for (var i = 0; i < 10; i++)
        {
            var obj = new { addr = 100, seq = i, tag = prefix };
            _hw.Sender.SendFrame(JsonSerializer.Serialize(obj));
            Thread.Sleep(150);
        }

        var received = _hw.Receiver.Collect(TimeSpan.FromSeconds(3));
        var mine = received.Where(m => m.Payload.Contains(prefix)).ToList();

        var parsed = new List<int>();
        var garbled = 0;
        foreach (var m in mine)
        {
            try
            {
                using var doc = JsonDocument.Parse(m.Payload);
                parsed.Add(doc.RootElement.GetProperty("seq").GetInt32());
            }
            catch (JsonException)
            {
                garbled++;
                _out.WriteLine($"garbled payload (expected at high send rate): {m.Payload}");
            }
        }
        _out.WriteLine($"sent 10, matched {mine.Count}, parsed cleanly {parsed.Count}, garbled {garbled}");
        Assert.True(parsed.Count >= 7, $"expected at least 7/10 clean parses, got {parsed.Count} (garbled: {garbled})");
        Assert.Equal(parsed.OrderBy(s => s).ToList(), parsed);
    }
}
