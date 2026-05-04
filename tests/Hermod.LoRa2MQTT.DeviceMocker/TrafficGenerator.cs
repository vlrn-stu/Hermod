using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Hermod.LoRa2MQTT.DeviceMocker;

/// <summary>
/// Emits LoRa traffic according to <see cref="MockerOptions.Mode"/>.
/// Each mode corresponds to either a legitimate device pattern (Normal,
/// Burst) or an attack the <c>LoRaMessageGuard</c> is expected to drop
/// (Flood, Replay, Oversize, Spoof). Sweep runs the full attack set
/// sequentially so an integration test can assert that ALL of them are
/// filtered.
/// </summary>
public sealed class TrafficGenerator
{
    private readonly ILogger<TrafficGenerator> _logger;
    private readonly SerialLoRaSender _sender;
    private readonly MockerOptions _options;
    private long _sequence;

    public TrafficGenerator(ILogger<TrafficGenerator> logger, SerialLoRaSender sender, MockerOptions options)
    {
        _logger = logger;
        _sender = sender;
        _options = options;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        _logger.LogInformation(
            "Traffic generator starting: mode={Mode} addr={Address} port={Port}",
            _options.Mode, _options.Address, _options.SerialPort);

        do
        {
            switch (_options.Mode)
            {
                case MockerMode.Normal: await NormalAsync(ct); break;
                case MockerMode.Burst: BurstOnce(); break;
                case MockerMode.Flood: await FloodAsync(ct); break;
                case MockerMode.Replay: ReplayOnce(); break;
                case MockerMode.Oversize: OversizeOnce(); break;
                case MockerMode.Spoof: SpoofOnce(); break;
                case MockerMode.Sweep: SweepOnce(); break;
                case MockerMode.Silence: await Task.Delay(TimeSpan.FromSeconds(5), ct); break;
                default: throw new InvalidOperationException($"Unknown mode {_options.Mode}");
            }

            if (_options.RunOnce) return;

            if (_options.Mode is MockerMode.Burst or MockerMode.Replay
                or MockerMode.Oversize or MockerMode.Spoof or MockerMode.Sweep)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
        } while (!ct.IsCancellationRequested);
    }

    private async Task NormalAsync(CancellationToken ct)
    {
        var payload = BuildSensorPayload(_options.Address, _options.DeviceName, _sequence++);
        _sender.SendFrameLogged(payload);
        await Task.Delay(_options.IntervalMs, ct);
    }

    private void BurstOnce()
    {
        _logger.LogInformation("burst: {Count} messages back-to-back", _options.BurstCount);
        var start = DateTimeOffset.UtcNow;
        for (var i = 0; i < _options.BurstCount; i++)
        {
            var payload = BuildSensorPayload(_options.Address, _options.DeviceName, _sequence++);
            _sender.SendFrame(payload);
            if (_options.BurstDelayMicros > 0)
            {
                SpinWaitMicros(_options.BurstDelayMicros);
            }
        }
        var elapsed = DateTimeOffset.UtcNow - start;
        var rate = _options.BurstCount / Math.Max(elapsed.TotalSeconds, 1e-6);
        _logger.LogInformation("burst done in {Ms} ms ({Rate:F1} msg/s)", elapsed.TotalMilliseconds, rate);
    }

    private async Task FloodAsync(CancellationToken ct)
    {
        var perSec = Math.Max(_options.FloodMessagesPerMinute / 60.0, 0.1);
        var delayMs = (int)Math.Round(1000.0 / perSec);
        _logger.LogInformation(
            "flood: {Per}/min (~{Delay} ms between messages) — expect guard to drop everything above MaxMessagesPerMinutePerAddress",
            _options.FloodMessagesPerMinute, delayMs);
        var end = DateTimeOffset.UtcNow.AddSeconds(70);
        while (!ct.IsCancellationRequested && DateTimeOffset.UtcNow < end)
        {
            var payload = BuildSensorPayload(_options.Address, _options.DeviceName, _sequence++);
            _sender.SendFrame(payload);
            await Task.Delay(delayMs, ct);
        }
    }

    private void ReplayOnce()
    {
        var payload = BuildSensorPayload(_options.Address, _options.DeviceName, seq: 9999);
        _logger.LogInformation(
            "replay: sending identical payload {Count}x inside dedup window — guard should keep only the first",
            _options.ReplayCount);
        for (var i = 0; i < _options.ReplayCount; i++)
        {
            _sender.SendFrame(payload);
            Thread.Sleep(200);
        }
    }

    private void OversizeOnce()
    {
        var prefix = $"{{\"addr\":{_options.Address},\"fill\":\"";
        var suffix = "\"}";
        var fillLen = Math.Max(0, _options.OversizePayloadBytes - prefix.Length - suffix.Length);
        var payload = prefix + new string('A', fillLen) + suffix;
        _logger.LogInformation(
            "oversize: transmitting {Bytes}-byte payload — exceeds MaxPayloadBytes and the 240-byte hard cap",
            payload.Length);
        _sender.SendFrame(payload);
    }

    private void SpoofOnce()
    {
        var payload = BuildSensorPayload(_options.SpoofAddress, "spoofed", _sequence++);
        _logger.LogInformation(
            "spoof: claiming to be address {Claimed} (real module addr {Real})",
            _options.SpoofAddress, _options.Address);
        _sender.SendFrame(payload);
    }

    private void SweepOnce()
    {
        _logger.LogInformation("sweep: running every attack mode once");
        OversizeOnce(); Thread.Sleep(500);
        ReplayOnce();   Thread.Sleep(500);
        SpoofOnce();    Thread.Sleep(500);
        BurstOnce();
    }

    private static string BuildSensorPayload(int address, string device, long seq)
    {
        var obj = new
        {
            addr = address,
            dev = device,
            seq,
            ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            temp = 20.0 + (seq % 100) / 10.0,
            humidity = 40.0 + (seq % 60) / 2.0,
        };
        return JsonSerializer.Serialize(obj);
    }

    private static void SpinWaitMicros(int micros)
    {
        var ticks = Stopwatch.Frequency / 1_000_000L * micros;
        var end = Stopwatch.GetTimestamp() + ticks;
        while (Stopwatch.GetTimestamp() < end) { /* spin */ }
    }
}

internal static class Stopwatch
{
    public static readonly long Frequency = System.Diagnostics.Stopwatch.Frequency;
    public static long GetTimestamp() => System.Diagnostics.Stopwatch.GetTimestamp();
}
