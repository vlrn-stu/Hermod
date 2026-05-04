using System.Diagnostics.CodeAnalysis;
using LoRa2MQTT.Service.Adapters;
using LoRa2MQTT.Service.Configuration;
using LoRa2MQTT.Service.Models;
using LoRa2MQTT.Service.Services;
using Microsoft.Extensions.Options;

namespace LoRa2MQTT.Service;

/// <summary>
/// Background worker that bridges LoRa messages to MQTT.
/// </summary>
public sealed class LoRaBridgeWorker : BackgroundService
{
    private readonly ILogger<LoRaBridgeWorker> _logger;
    private readonly ILoRaAdapter _loraAdapter;
    private readonly MqttService _mqttService;
    private readonly LoRaMessageGuard _guard;
    private readonly LoRaOptions _loraOptions;

    /// <summary>
    /// Initializes a new instance of <see cref="LoRaBridgeWorker"/>.
    /// </summary>
    public LoRaBridgeWorker(
        ILogger<LoRaBridgeWorker> logger,
        ILoRaAdapter loraAdapter,
        MqttService mqttService,
        LoRaMessageGuard guard,
        IOptions<LoRaOptions> loraOptions)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(loraAdapter);
        ArgumentNullException.ThrowIfNull(mqttService);
        ArgumentNullException.ThrowIfNull(guard);
        ArgumentNullException.ThrowIfNull(loraOptions);
        _logger = logger;
        _loraAdapter = loraAdapter;
        _mqttService = mqttService;
        _guard = guard;
        _loraOptions = loraOptions.Value;
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "LoRa2MQTT Bridge starting (Mode: {Mode})...",
            _loraOptions.MockMode ? "MOCK" : "HARDWARE");

        try
        {
            await _mqttService.ConnectAsync(stoppingToken);
            await _loraAdapter.ConnectAsync(stoppingToken);

            _loraAdapter.MessageReceived += OnLoRaMessageReceived;
            _mqttService.CommandReceived += OnMqttCommandReceived;

            if (_loraOptions.MockMode && _loraAdapter is MockLoRaAdapter mockAdapter)
            {
                _mqttService.MockControlReceived += (_, args) =>
                    mockAdapter.HandleMockControl(args.Topic, args.Payload);
                _logger.LogInformation("Mock control enabled via MQTT topics: lora/mock/#");
            }

            await _mqttService.PublishBridgeStatusAsync(true, stoppingToken);
            await _loraAdapter.StartReceivingAsync(stoppingToken);

            _logger.LogInformation("LoRa2MQTT Bridge started successfully");

            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("LoRa2MQTT Bridge stopping...");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fatal error in LoRa2MQTT Bridge");
            throw;
        }
        finally
        {
            _loraAdapter.MessageReceived -= OnLoRaMessageReceived;
            _mqttService.CommandReceived -= OnMqttCommandReceived;

            // CancellationToken.None: stoppingToken has fired, but the
            // terminal offline-status publish needs a live token or
            // subscribers stay convinced the bridge is up.
            await _mqttService.PublishBridgeStatusAsync(false, CancellationToken.None);
            await _loraAdapter.DisconnectAsync(CancellationToken.None);
            await _mqttService.DisconnectAsync(CancellationToken.None);

            _logger.LogInformation("LoRa2MQTT Bridge stopped");
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Async void event handler must absorb every failure; an uncaught exception here crashes the process.")]
    private async void OnLoRaMessageReceived(object? sender, LoRaMessage message)
    {
        try
        {
            var verdict = _guard.Inspect(message);
            if (!verdict.Accept)
            {
                _logger.LogWarning(
                    "Dropped LoRa message from address {Address} ({Bytes} bytes): {Reason}",
                    message.Address, message.Payload.Length, verdict.Reason);
                return;
            }

            var deviceId = ExtractDeviceId(message);

            _logger.LogInformation(
                "LoRa message from {DeviceId}: RSSI={Rssi}, SNR={Snr}",
                deviceId, message.Rssi, message.Snr);

            await _mqttService.PublishMessageAsync(deviceId, message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error publishing LoRa message to MQTT");
        }
    }

    private const int MaxDeviceIdLength = 64;

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Any JsonDocument failure falls back to synthesized device id; propagating would lose the message.")]
    private static string ExtractDeviceId(LoRaMessage message)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(message.Payload);
            if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("device_id", out var deviceIdProp) &&
                deviceIdProp.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                var id = deviceIdProp.GetString();
                if (IsValidDeviceId(id))
                {
                    return id!;
                }
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // Non-JSON payload; fall through.
        }
        catch (Exception)
        {
            // Any other parse failure; fall through.
        }

        return $"device_{message.Address}";
    }

    // Topic-safe device id. Reject empty, whitespace, control chars,
    // and MQTT topic separators ('/') or wildcards so an adversarial
    // LoRa peer cannot inject topic segments downstream.
    internal static bool IsValidDeviceId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > MaxDeviceIdLength)
        {
            return false;
        }
        foreach (var c in id)
        {
            if (c < 32 || c == '/' || c == '+' || c == '#')
            {
                return false;
            }
        }
        return true;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Async void event handler must absorb every failure; an uncaught exception here crashes the process.")]
    private async void OnMqttCommandReceived(object? sender, LoRaCommand command)
    {
        try
        {
            _logger.LogInformation(
                "MQTT command for address {Address}: {Payload}",
                command.Address, command.Payload);

            await _loraAdapter.SendAsync(command);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending command to LoRa device");
        }
    }
}
