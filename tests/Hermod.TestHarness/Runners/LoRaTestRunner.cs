using LoRa2MQTT.Service.Adapters;
using LoRa2MQTT.Service.Configuration;
using LoRa2MQTT.Service.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hermod.TestHarness.Runners;

/// <summary>
/// LoRa-specific tests using real RF via the Waveshare SX1262 module.
/// Sends packets over the air to Node 1 and measures RF characteristics.
/// </summary>
public sealed class LoRaTestRunner
{
    private readonly ILogger<LoRaTestRunner> _logger;
    private readonly MqttTestClient _mqtt;
    private readonly MeasurementCollector _collector;
    private readonly IConfiguration _config;

    public LoRaTestRunner(
        ILogger<LoRaTestRunner> logger,
        MqttTestClient mqtt,
        MeasurementCollector collector,
        IConfiguration config)
    {
        _logger = logger;
        _mqtt = mqtt;
        _collector = collector;
        _config = config;
    }

    /// <summary>
    /// Runs LoRa RF tests. Requires a Waveshare USB-TO-LoRa-HF module on this node.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        _logger.LogInformation("=== LoRa RF Tests ===");

        var options = new LoRaOptions
        {
            SerialPort = _config["LoRa:SerialPort"] ?? "/dev/ttyUSB0",
            BaudRate = int.TryParse(_config["LoRa:BaudRate"], out var br) ? br : 115200,
            Channel = int.TryParse(_config["LoRa:Channel"], out var ch) ? ch : 18,
            Address = int.TryParse(_config["LoRa:Address"], out var addr) ? addr : 1,
            NetworkId = int.TryParse(_config["LoRa:NetworkId"], out var nid) ? nid : 0,
            SpreadingFactor = int.TryParse(_config["LoRa:SpreadingFactor"], out var sf) ? sf : 7,
            TransmitPower = int.TryParse(_config["LoRa:TransmitPower"], out var pwr) ? pwr : 22,
            EnableRssi = true
        };

        var adapter = new WaveshareLoRaAdapter(
            _logger as ILogger<WaveshareLoRaAdapter> ??
                LoggerFactory.Create(b => b.AddConsole()).CreateLogger<WaveshareLoRaAdapter>(),
            Options.Create(options));

        try
        {
            await adapter.ConnectAsync(ct);
            await _mqtt.ConnectAsync(ct);

            // Subscribe to lora topics on the broker to verify reception
            await _mqtt.SubscribeAsync("lora/#", ct);

            await TestPacketDelivery(adapter, options, ct);
            await TestLatencyOverRf(adapter, ct);
        }
        finally
        {
            await adapter.DisposeAsync();
        }
    }

    /// <summary>
    /// Sends N packets and measures delivery ratio + RSSI/SNR.
    /// </summary>
    private async Task TestPacketDelivery(WaveshareLoRaAdapter adapter, LoRaOptions options, CancellationToken ct)
    {
        _logger.LogInformation("Test: LoRa packet delivery ratio");

        const int packetCount = 50;
        _mqtt.ClearReceived();

        for (int i = 0; i < packetCount && !ct.IsCancellationRequested; i++)
        {
            var payload = $"{{\"test\":\"delivery\",\"seq\":{i},\"temp\":{20 + i % 10}}}";

            await adapter.SendAsync(new LoRaCommand
            {
                Address = options.Address,
                Payload = payload
            }, ct);

            // LoRa duty cycle: wait between transmissions
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }

        // Wait for any remaining packets in flight
        await Task.Delay(TimeSpan.FromSeconds(5), ct);

        var received = _mqtt.ReceivedMessages
            .Where(m => m.Topic.StartsWith("lora/") && m.Payload.Contains("delivery"))
            .ToList();

        var deliveryRatio = (double)received.Count / packetCount * 100;

        _collector.Record(new TestResult
        {
            Category = "LoRa",
                Claim = "O5",
            Name = "PacketDeliveryRatio",
            Status = deliveryRatio > 90 ? "PASS" : deliveryRatio > 50 ? "DEGRADED" : "FAIL",
            Details = $"Sent {packetCount}, received {received.Count} ({deliveryRatio:F1}%)"
        });
    }

    /// <summary>
    /// Measures round-trip: LoRa TX -> Node 1 LoRa2MQTT -> MQTT broker -> back to us via MQTT.
    /// </summary>
    private async Task TestLatencyOverRf(WaveshareLoRaAdapter adapter, CancellationToken ct)
    {
        _logger.LogInformation("Test: LoRa end-to-end latency");

        const int iterations = 20;
        var latencies = new List<double>();

        for (int i = 0; i < iterations && !ct.IsCancellationRequested; i++)
        {
            _mqtt.ClearReceived();

            var marker = Guid.NewGuid().ToString("N")[..8];
            var payload = $"{{\"test\":\"latency\",\"marker\":\"{marker}\"}}";

            var sw = System.Diagnostics.Stopwatch.StartNew();

            await adapter.SendAsync(new LoRaCommand
            {
                Address = 0,
                Payload = payload
            }, ct);

            // Poll for the message to appear on MQTT
            var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
            bool found = false;

            while (DateTimeOffset.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                if (_mqtt.ReceivedMessages.Any(m => m.Payload.Contains(marker)))
                {
                    sw.Stop();
                    latencies.Add(sw.Elapsed.TotalMilliseconds);
                    found = true;
                    break;
                }
                await Task.Delay(50, ct);
            }

            _collector.Record(new TestResult
            {
                Category = "LoRa",
                Claim = "O5",
                Name = $"RfLatency_Iteration_{i}",
                Status = found ? "PASS" : "TIMEOUT",
                LatencyMs = found ? sw.Elapsed.TotalMilliseconds : null,
                Details = found ? $"RF->MQTT latency: {sw.Elapsed.TotalMilliseconds:F1}ms" : "Packet not received"
            });

            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }

        if (latencies.Count > 0)
        {
            latencies.Sort();
            _collector.Record(new TestResult
            {
                Category = "LoRa",
                Claim = "O5",
                Name = "RfLatency_Summary",
                Status = "INFO",
                LatencyMs = latencies.Average(),
                Details = $"min={latencies[0]:F1}ms, median={latencies[latencies.Count / 2]:F1}ms, " +
                          $"max={latencies[^1]:F1}ms, count={latencies.Count}/{iterations}"
            });
        }
    }
}
