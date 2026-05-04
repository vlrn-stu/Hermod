using System.Text.Json;
using global::LoRa2MQTT.Service.Configuration;
using global::LoRa2MQTT.Service.Services;
using Microsoft.Extensions.Options;
using Xunit;
using Xunit.Abstractions;

namespace Hermod.LoRa2MQTT.IntegrationTests;

[Collection(nameof(HardwareCollection))]
[Trait("Category", "Hardware")]
[Trait("Category", "Security")]
public class DedupIntegrationTests
{
    private readonly HardwareFixture _hw;
    private readonly ITestOutputHelper _out;

    public DedupIntegrationTests(HardwareFixture hw, ITestOutputHelper output)
    {
        _hw = hw;
        _out = output;
    }

    [Fact]
    public void OverTheAirAlternatingReplay_NowGetsCaught()
    {
        if (!_hw.Available) { _out.WriteLine($"SKIP: {_hw.UnavailableReason}"); return; }
        _hw.Receiver.Drain();
        var nonce = Guid.NewGuid().ToString("N")[..6];
        var payloadA = $"{{\"addr\":100,\"atk\":\"alt\",\"val\":\"A\",\"n\":\"{nonce}\"}}";
        var payloadB = $"{{\"addr\":100,\"atk\":\"alt\",\"val\":\"B\",\"n\":\"{nonce}\"}}";

        for (var i = 0; i < 8; i++)
        {
            _hw.Sender.SendFrame(i % 2 == 0 ? payloadA : payloadB);
            Thread.Sleep(300);
        }

        var received = _hw.Receiver
            .Collect(TimeSpan.FromSeconds(3))
            .Where(m => m.Payload.Contains(nonce))
            .ToList();

        var guard = new LoRaMessageGuard(Options.Create(new LoRaSecurityOptions
        {
            MaxPayloadBytes = 256,
            MaxMessagesPerMinutePerAddress = 0,
            DedupWindowSeconds = 5,
            AddressAllowlist = Array.Empty<int>(),
        }));

        var acceptedA = 0;
        var acceptedB = 0;
        foreach (var m in received)
        {
            m.Address = 100;
            var v = guard.Inspect(m);
            if (!v.Accept) continue;
            if (m.Payload.Contains("\"val\":\"A\"")) acceptedA++;
            else if (m.Payload.Contains("\"val\":\"B\"")) acceptedB++;
        }

        _out.WriteLine($"alternating replay of {received.Count} arrivals → guard accepted A×{acceptedA}, B×{acceptedB}");
        Assert.Equal(1, acceptedA);
        Assert.Equal(1, acceptedB);
    }
}
