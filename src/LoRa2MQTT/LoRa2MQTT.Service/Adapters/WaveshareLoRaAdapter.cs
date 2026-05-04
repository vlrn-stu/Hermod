using System.Diagnostics.CodeAnalysis;
using System.IO.Ports;
using System.Text;
using LoRa2MQTT.Service.Configuration;
using LoRa2MQTT.Service.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LoRa2MQTT.Service.Adapters;

/// <summary>
/// LoRa adapter implementation for Waveshare USB-TO-LoRa-HF module.
/// Uses SX1262 chip with AT command configuration and stream mode data transfer.
/// </summary>
public sealed class WaveshareLoRaAdapter : ILoRaAdapter
{
    private readonly ILogger<WaveshareLoRaAdapter> _logger;
    private readonly LoRaOptions _options;
    private SerialPort? _serialPort;
    private CancellationTokenSource? _receiveCts;
    private Task? _receiveTask;
    private readonly StringBuilder _buffer = new();

    /// <inheritdoc/>
    public event EventHandler<LoRaMessage>? MessageReceived;

    /// <inheritdoc/>
    public bool IsConnected => _serialPort?.IsOpen ?? false;

    /// <summary>
    /// Initializes a new instance of <see cref="WaveshareLoRaAdapter"/>.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    /// <param name="options">LoRa configuration options.</param>
    public WaveshareLoRaAdapter(
        ILogger<WaveshareLoRaAdapter> logger,
        IOptions<LoRaOptions> options)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(options);
        _logger = logger;
        _options = options.Value;
    }

    /// <inheritdoc/>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (IsConnected)
        {
            _logger.LogWarning("Already connected to LoRa adapter");
            return;
        }

        _logger.LogInformation(
            "Connecting to Waveshare LoRa adapter on {Port} at {BaudRate} baud",
            _options.SerialPort, _options.BaudRate);

        _serialPort = new SerialPort(_options.SerialPort, _options.BaudRate)
        {
            DataBits = 8,
            Parity = Parity.None,
            StopBits = StopBits.One,
            ReadTimeout = 1000,
            WriteTimeout = 1000,
            Encoding = Encoding.ASCII
        };

        try
        {
            _serialPort.Open();
            _logger.LogInformation("Serial port opened successfully");

            await Task.Delay(500, cancellationToken);
            await ConfigureModuleAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to LoRa adapter");
            _serialPort?.Dispose();
            _serialPort = null;
            throw;
        }
    }

    private async Task ConfigureModuleAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Configuring LoRa module...");

        await SendAtCommandAsync("+++", expectResponse: false, cancellationToken);
        await Task.Delay(200, cancellationToken);

        await SendAtCommandAsync($"AT+ADDR={_options.Address}", cancellationToken: cancellationToken);
        await SendAtCommandAsync($"AT+NETID={_options.NetworkId}", cancellationToken: cancellationToken);
        await SendAtCommandAsync($"AT+TXCH={_options.Channel}", cancellationToken: cancellationToken);
        await SendAtCommandAsync($"AT+RXCH={_options.Channel}", cancellationToken: cancellationToken);
        await SendAtCommandAsync($"AT+SF={_options.SpreadingFactor}", cancellationToken: cancellationToken);
        await SendAtCommandAsync($"AT+PWR={_options.TransmitPower}", cancellationToken: cancellationToken);
        await SendAtCommandAsync($"AT+RSSI={((_options.EnableRssi) ? 1 : 0)}", cancellationToken: cancellationToken);
        await SendAtCommandAsync("AT+MODE=1", cancellationToken: cancellationToken); // Stream mode

        await SendAtCommandAsync("AT+EXIT", cancellationToken: cancellationToken);

        _logger.LogInformation(
            "LoRa module configured: Channel={Channel}, Address={Address}, SF={SF}",
            _options.Channel, _options.Address, _options.SpreadingFactor);
    }

    private async Task SendAtCommandAsync(
        string command,
        bool expectResponse = true,
        CancellationToken cancellationToken = default)
    {
        if (_serialPort is null || !_serialPort.IsOpen)
            throw new InvalidOperationException("Serial port is not open");

        var fullCommand = command.EndsWith("\r\n", StringComparison.Ordinal) ? command : command + "\r\n";
        _logger.LogDebug("Sending AT command: {Command}", command);

        await Task.Run(() => _serialPort.Write(fullCommand), cancellationToken);

        if (expectResponse)
        {
            await Task.Delay(100, cancellationToken);
            try
            {
                var response = _serialPort.ReadLine();
                _logger.LogDebug("AT response: {Response}", response);
            }
            catch (TimeoutException)
            {
                _logger.LogDebug("No response to AT command (timeout)");
            }
        }
    }

    /// <inheritdoc/>
    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Disconnecting from LoRa adapter");

        if (_receiveCts is not null)
        {
            await _receiveCts.CancelAsync();
            if (_receiveTask is not null)
            {
                try
                {
                    await _receiveTask.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
                }
                catch (TimeoutException)
                {
                    _logger.LogWarning("Receive task did not complete in time");
                }
            }
            _receiveCts.Dispose();
            _receiveCts = null;
        }

        if (_serialPort is not null)
        {
            if (_serialPort.IsOpen)
                _serialPort.Close();
            _serialPort.Dispose();
            _serialPort = null;
        }

        _logger.LogInformation("Disconnected from LoRa adapter");
    }

    /// <inheritdoc/>
    public async Task SendAsync(LoRaCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (_serialPort is null || !_serialPort.IsOpen)
            throw new InvalidOperationException("Not connected to LoRa adapter");

        string payload;
        if (!string.IsNullOrEmpty(command.PayloadHex))
        {
            if (!TryHexToString(command.PayloadHex, out payload))
            {
                throw new ArgumentException(
                    $"Invalid LoRa PayloadHex '{command.PayloadHex}': must be even-length hex",
                    nameof(command));
            }
        }
        else
        {
            payload = command.Payload;
        }

        _logger.LogDebug("Sending LoRa message to address {Address}: {Payload}", command.Address, payload);

        await Task.Run(() => _serialPort.Write(payload), cancellationToken);

        _logger.LogInformation("Sent LoRa message: {Length} bytes", payload.Length);
    }

    /// <inheritdoc/>
    public Task StartReceivingAsync(CancellationToken cancellationToken = default)
    {
        if (_serialPort is null || !_serialPort.IsOpen)
            throw new InvalidOperationException("Not connected to LoRa adapter");

        // Guard against double-start: stomping _receiveCts without disposing
        // the previous one leaks it and leaves the prior receive task
        // running against an inaccessible handle.
        if (_receiveTask is { IsCompleted: false })
        {
            _logger.LogWarning("Receive loop already running; ignoring StartReceivingAsync");
            return Task.CompletedTask;
        }

        _receiveCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _receiveTask = SupervisedReceiveLoopAsync(_receiveCts.Token);

        _logger.LogInformation("Started receiving LoRa messages");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Outer supervisor around <see cref="ReceiveLoopAsync"/>. When the
    /// inner loop exits because the serial port closed (USB re-enumerate,
    /// physical disconnect, device reset), this reopens the port with
    /// exponential backoff and resumes receiving — a USB flap otherwise
    /// turns the adapter into dead weight until process restart.
    /// </summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Supervisor loop must survive any inner exception — receive failures, stale port disposal, and reconnect errors all retry with backoff instead of killing the adapter.")]
    private async Task SupervisedReceiveLoopAsync(CancellationToken cancellationToken)
    {
        // Exponential backoff 1s→30s to avoid a hot-loop on unplug.
        var backoffSeconds = 1;
        const int maxBackoffSeconds = 30;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await ReceiveLoopAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Receive loop crashed with an unexpected exception; supervisor will attempt reconnect");
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            _logger.LogWarning(
                "Serial port closed unexpectedly. Attempting reconnect in {Seconds}s",
                backoffSeconds);

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(backoffSeconds), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                // Tear down first — ConnectAsync early-returns on a still-open port.
                if (_serialPort is not null)
                {
                    try { _serialPort.Dispose(); } catch { /* best-effort */ }
                    _serialPort = null;
                }

                // Drop any partial-frame bytes buffered before the outage.
                // Otherwise the first frame after reconnect fuses stale
                // pre-disconnect bytes with the fresh stream.
                _buffer.Clear();

                await ConnectAsync(cancellationToken);
                _logger.LogInformation("Serial port reconnected; resuming receive loop");
                backoffSeconds = 1; // reset on successful reconnect
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Reconnect attempt failed; will retry");
                backoffSeconds = Math.Min(backoffSeconds * 2, maxBackoffSeconds);
            }
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Inner receive loop must log and back off on any serial read failure; supervisor loop handles escalation.")]
    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        _logger.LogDebug("Receive loop started");

        while (!cancellationToken.IsCancellationRequested && _serialPort?.IsOpen == true)
        {
            try
            {
                if (_serialPort.BytesToRead > 0)
                {
                    var data = new byte[_serialPort.BytesToRead];
                    var bytesRead = await Task.Run(
                        () => _serialPort.Read(data, 0, data.Length),
                        cancellationToken);

                    if (bytesRead > 0)
                    {
                        ProcessReceivedData(data, bytesRead);
                    }
                }
                else
                {
                    await Task.Delay(50, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in receive loop");
                await Task.Delay(1000, cancellationToken);
            }
        }

        _logger.LogDebug("Receive loop ended");
    }

    private void ProcessReceivedData(byte[] data, int length)
    {
        var text = Encoding.ASCII.GetString(data, 0, length);
        _buffer.Append(text);

        var content = _buffer.ToString();

        // Wait for a terminator or cap; parsing on every chunk used to
        // emit split RSSI values (e.g. "-5" then "5") as two events.
        if (!IsFrameComplete(content))
        {
            return;
        }

        _buffer.Clear();

        // No terminator at cap = oversize/unframed transmission. Drop
        // the whole frame so under-cap slices don't slip past the
        // MaxPayloadBytes guard (Oversize_JumboPayload_* integration tests).
        if (!content.Contains('\n', StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "Discarded {Bytes}-byte frame flushed by hard cap without terminator — malformed or oversize transmission",
                content.Length);
            return;
        }

        var parse = ParseFrame(content, _options.EnableRssi);
        if (!parse.Emit)
        {
            return;
        }

        var message = new LoRaMessage
        {
            Address = _options.Address,
            Channel = _options.Channel,
            Payload = parse.Payload,
            PayloadHex = StringToHex(parse.Payload),
            Rssi = parse.Rssi,
            Timestamp = DateTimeOffset.UtcNow
        };

        _logger.LogDebug(
            "Received LoRa message: {Payload} (RSSI: {Rssi})",
            parse.Payload, parse.Rssi);

        MessageReceived?.Invoke(this, message);
    }

    /// <summary>
    /// Returns true when the buffered serial content is ready to parse.
    /// A frame is complete when a newline has arrived OR the buffer has
    /// exceeded the hard cap (240 bytes) which is our "something is wrong,
    /// flush what we have" safety valve. Extracted as an internal static
    /// for direct unit testing.
    /// </summary>
    internal static bool IsFrameComplete(string bufferedContent)
    {
        // Real hardware (AT+RSSI=1) sends `<payload>\n<rssi>\r\n`, so
        // the terminator is the trailing CR-LF, not the inner LF.
        // Trailing lone LF is also accepted (mock translator, ASCII peer).
        // 240-byte cap is a last-resort flush.
        if (bufferedContent.Length > 240) return true;
        if (bufferedContent.Contains("\r\n", StringComparison.Ordinal)) return true;
        if (bufferedContent.Length > 0 && bufferedContent[^1] == '\n') return true;
        return false;
    }

    /// <summary>
    /// Parses a complete raw LoRa frame (trailing CR/LF included) into a
    /// payload plus optional trailing RSSI. Returns <see cref="LoRaFrameParseResult"/>
    /// with <c>Emit=false</c> when the payload is whitespace-only and the
    /// caller should skip emission. Extracted as an internal static so the
    /// parser can be unit-tested directly without the full adapter state.
    /// </summary>
    internal static LoRaFrameParseResult ParseFrame(string rawFrame, bool enableRssi)
    {
        var payload = rawFrame.TrimEnd('\r', '\n');
        int? rssi = null;

        if (enableRssi)
        {
            // Two firmware variants: "<payload>\n<rssi>" on our real
            // dongles (tried first) and "<payload>,<rssi>" per vendor doc.
            var nlIdx = payload.LastIndexOf('\n');
            if (nlIdx >= 0 && int.TryParse(payload.AsSpan(nlIdx + 1), out var rssiNl))
            {
                rssi = rssiNl;
                payload = payload[..nlIdx].TrimEnd('\r');
            }
            else if (payload.Contains(',', StringComparison.Ordinal))
            {
                var parts = payload.Split(',');
                if (parts.Length >= 2 && int.TryParse(parts[^1], out var rssiValue))
                {
                    rssi = rssiValue;
                    payload = string.Join(',', parts[..^1]);
                }
            }
        }

        if (string.IsNullOrWhiteSpace(payload))
        {
            return new LoRaFrameParseResult(false, string.Empty, null);
        }

        return new LoRaFrameParseResult(true, payload, rssi);
    }

    internal readonly record struct LoRaFrameParseResult(
        bool Emit,
        string Payload,
        int? Rssi);

    private static string StringToHex(string input)
        => Convert.ToHexString(Encoding.ASCII.GetBytes(input));

    private static bool TryHexToString(string hex, out string result)
    {
        // Manual validation so callers get a false return rather than an
        // exception that an upstream handler would silently swallow.
        result = string.Empty;
        if (string.IsNullOrEmpty(hex) || (hex.Length & 1) != 0) return false;
        foreach (var c in hex)
        {
            var ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
            if (!ok) return false;
        }
        try
        {
            result = Encoding.ASCII.GetString(Convert.FromHexString(hex));
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
    }
}
