using System.Collections.Concurrent;
using Hermod.LoRa2MQTT.DeviceMocker;
using global::LoRa2MQTT.Service.Adapters;
using global::LoRa2MQTT.Service.Configuration;
using global::LoRa2MQTT.Service.Models;
using global::LoRa2MQTT.Service.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Hermod.LoRa2MQTT.IntegrationTests;

[CollectionDefinition(nameof(HardwareCollection), DisableParallelization = true)]
public sealed class HardwareCollection : ICollectionFixture<HardwareFixture> { }

/// <summary>
/// Shared fixture that owns both USB LoRa modules for the whole test
/// session. The transmit side (ACM1 by default) is wrapped in a
/// <see cref="SerialLoRaSender"/> so tests can reuse the same bit of
/// transmit code the <c>DeviceMocker</c> pod runs. The receive side
/// (ACM0) reads directly with a background thread — we deliberately do
/// NOT go through the production <c>WaveshareLoRaAdapter</c>, because
/// that adapter writes AT config to the port on connect and those
/// bytes would transmit over the air, polluting the very channel we
/// are trying to observe. Instead we reuse the adapter's pure parser
/// (<see cref="WaveshareLoRaAdapter.IsFrameComplete"/> /
/// <see cref="WaveshareLoRaAdapter.ParseFrame"/>) and the real
/// <see cref="LoRaMessageGuard"/>, which is the actual object under
/// test for attack-suite assertions.
/// </summary>
public sealed class HardwareFixture : IDisposable
{
    public string TxPort { get; }
    public string RxPort { get; }
    public int BaudRate { get; }
    public bool EnableRssiSuffix { get; }
    public bool StripTrailingHexRssi { get; }

    public SerialLoRaSender Sender { get; } = default!;
    public SerialLoRaReceiver Receiver { get; } = default!;
    public LoRaMessageGuard Guard { get; } = default!;

    public LoRaSecurityOptions SecurityOptions { get; } = default!;

    /// <summary>
    /// True when both LoRa USB antennas are plugged in (or
    /// <c>LORAMOCK_TX_PORT</c> / <c>LORAMOCK_RX_PORT</c> point at real
    /// char devices). Tests in <see cref="HardwareCollection"/> open
    /// with <c>if (!_fx.Available) return;</c> so the suite reports
    /// green on a dev box that doesn't have the bench rig wired up,
    /// instead of failing every test on InvalidOperationException
    /// from the fixture constructor.
    /// </summary>
    public bool Available { get; }

    /// <summary>Reason <see cref="Available"/> is false; null when Available is true.</summary>
    public string? UnavailableReason { get; }

    public HardwareFixture()
    {
        TxPort = Environment.GetEnvironmentVariable("LORAMOCK_TX_PORT") ?? "/dev/ttyACM1";
        RxPort = Environment.GetEnvironmentVariable("LORAMOCK_RX_PORT") ?? "/dev/ttyACM0";
        BaudRate = int.TryParse(Environment.GetEnvironmentVariable("LORAMOCK_BAUD"), out var b) ? b : 115200;
        EnableRssiSuffix = Environment.GetEnvironmentVariable("LORAMOCK_RSSI") is "1" or "true";

        var stripHex = Environment.GetEnvironmentVariable("LORAMOCK_STRIP_HEX_RSSI");
        StripTrailingHexRssi = stripHex is null || stripHex is "1" or "true";

        if (!File.Exists(TxPort) && !IsCharDev(TxPort))
        {
            UnavailableReason = $"TX port {TxPort} not present — plug the second LoRa antenna or set LORAMOCK_TX_PORT";
            Available = false;
            return;
        }
        if (!File.Exists(RxPort) && !IsCharDev(RxPort))
        {
            UnavailableReason = $"RX port {RxPort} not present — plug the first LoRa antenna or set LORAMOCK_RX_PORT";
            Available = false;
            return;
        }

        Sender = new SerialLoRaSender(NullLogger<SerialLoRaSender>.Instance, TxPort, BaudRate, appendLf: true);
        Sender.Open();

        Receiver = new SerialLoRaReceiver(RxPort, BaudRate, EnableRssiSuffix)
        {
            StripTrailingHexRssi = StripTrailingHexRssi,
        };
        Receiver.Start();

        SecurityOptions = new LoRaSecurityOptions
        {
            MaxPayloadBytes = 256,
            MaxMessagesPerMinutePerAddress = 60,
            DedupWindowSeconds = 5,
            AddressAllowlist = Array.Empty<int>(),
        };
        Guard = new LoRaMessageGuard(Options.Create(SecurityOptions));

        Thread.Sleep(500);
        Receiver.Drain();
        Available = true;
    }

    public void Dispose()
    {
        Receiver?.Dispose();
        Sender?.Dispose();
    }

    private static bool IsCharDev(string path)
    {
        try
        {
            return new FileInfo(path).Exists;
        }
        catch { return false; }
    }
}

/// <summary>
/// Pulls frames off the receiving serial port on a dedicated thread and
/// buffers them for the tests. Uses the extracted
/// <c>WaveshareLoRaAdapter</c> parser so the production frame-split +
/// RSSI-extraction behaviour is what gets exercised.
/// </summary>
public sealed class SerialLoRaReceiver : IDisposable
{
    private readonly System.IO.Ports.SerialPort _port;
    private readonly bool _rssi;
    private readonly ConcurrentQueue<LoRaMessage> _messages = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly System.Text.StringBuilder _buffer = new();
    private Thread? _reader;

    public event Action<LoRaMessage>? MessageReceived;
    public int Address { get; set; } = 0;
    public int Channel { get; set; } = 18;

    /// <summary>
    /// One of the two antennas (in our bench setup: ACM0) ships with
    /// factory firmware that appends an RSSI byte as two lowercase hex
    /// chars AFTER the <c>\n</c> terminator, with no separator and no
    /// trailing newline. Without this flag, those two chars leak into
    /// the next frame and every other payload parses as garbage. When
    /// true, any 1-2 hex chars sitting at the head of the buffer right
    /// after a frame boundary are silently discarded.
    /// </summary>
    public bool StripTrailingHexRssi { get; set; }

    public int CapFlushCount { get; private set; }

    public SerialLoRaReceiver(string port, int baud, bool rssi)
    {
        _rssi = rssi;
        _port = new System.IO.Ports.SerialPort(port, baud)
        {
            DataBits = 8,
            Parity = System.IO.Ports.Parity.None,
            StopBits = System.IO.Ports.StopBits.One,
            ReadTimeout = 200,
            Encoding = System.Text.Encoding.ASCII,
        };
    }

    public void Start()
    {
        _port.Open();
        _port.DiscardInBuffer();
        _reader = new Thread(Loop) { IsBackground = true, Name = "loramock-rx" };
        _reader.Start();
    }

    public void Drain()
    {
        while (_messages.TryDequeue(out _)) { }
        _buffer.Clear();
        if (_port.IsOpen) _port.DiscardInBuffer();
    }

    public bool TryRead(out LoRaMessage message, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (_messages.TryDequeue(out message!)) return true;
            Thread.Sleep(10);
        }
        message = null!;
        return false;
    }

    public int Count => _messages.Count;

    public IReadOnlyList<LoRaMessage> Snapshot()
    {
        var list = new List<LoRaMessage>();
        while (_messages.TryDequeue(out var m)) list.Add(m);
        return list;
    }

    public IReadOnlyList<LoRaMessage> Collect(TimeSpan duration)
    {
        Thread.Sleep(duration);
        return Snapshot();
    }

    private void Loop()
    {
        var buf = new byte[512];
        while (!_cts.IsCancellationRequested && _port.IsOpen)
        {
            try
            {
                if (_port.BytesToRead > 0)
                {
                    var read = _port.Read(buf, 0, Math.Min(buf.Length, _port.BytesToRead));
                    if (read > 0) ProcessChunk(buf, read);
                }
                else
                {
                    Thread.Sleep(20);
                }
            }
            catch (TimeoutException) { }
            catch when (_cts.IsCancellationRequested) { return; }
            catch (Exception)
            {
                Thread.Sleep(50);
            }
        }
    }

    private void ProcessChunk(byte[] data, int length)
    {
        var text = System.Text.Encoding.ASCII.GetString(data, 0, length);
        _buffer.Append(text);

        while (true)
        {
            if (StripTrailingHexRssi) TrimLeadingRssiNoise();

            var content = _buffer.ToString();
            if (!WaveshareLoRaAdapter.IsFrameComplete(content))
                return;

            int split;
            if (content.Length > 240 && !content.Contains('\n'))
            {
                split = content.Length;
            }
            else
            {
                split = content.IndexOf('\n') + 1;
                if (split <= 0) return;
            }

            var frame = content[..split];
            _buffer.Remove(0, split);

            if (!frame.Contains('\n'))
            {
                CapFlushCount++;
                continue;
            }

            var parsed = WaveshareLoRaAdapter.ParseFrame(frame, _rssi);
            if (!parsed.Emit) continue;

            var message = new LoRaMessage
            {
                Address = Address,
                Channel = Channel,
                Payload = parsed.Payload,
                PayloadHex = Convert.ToHexString(System.Text.Encoding.ASCII.GetBytes(parsed.Payload)),
                Rssi = parsed.Rssi,
                Timestamp = DateTimeOffset.UtcNow,
            };
            _messages.Enqueue(message);
            MessageReceived?.Invoke(message);
        }
    }

    private void TrimLeadingRssiNoise()
    {
        var peeled = 0;
        while (peeled < 2 && _buffer.Length > 0)
        {
            var c = _buffer[0];
            if (IsHexDigit(c) || c == '\r') { _buffer.Remove(0, 1); peeled++; continue; }
            break;
        }
    }

    private static bool IsHexDigit(char c)
        => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

    public void Dispose()
    {
        _cts.Cancel();
        try { _reader?.Join(TimeSpan.FromSeconds(2)); } catch { }
        try { if (_port.IsOpen) _port.Close(); } catch { }
        _port.Dispose();
        _cts.Dispose();
    }
}
