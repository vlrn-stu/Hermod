using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using LoRa2MQTT.Service.Configuration;
using LoRa2MQTT.Service.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;

namespace LoRa2MQTT.Service.Services;

/// <summary>
/// Service for MQTT communication.
/// </summary>
public sealed class MqttService : IAsyncDisposable
{
    private readonly ILogger<MqttService> _logger;
    private readonly MqttOptions _options;
    private readonly LoRaOptions _loraOptions;
    private readonly IMqttClient _mqttClient;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false
    };

    // Aborts the auto-reconnect Task.Delay in OnDisconnectedAsync;
    // without it a racing DisconnectAsync would resurrect the client.
    private readonly CancellationTokenSource _shutdownCts = new();
    private int _disposed;

    /// <summary>
    /// Event raised when a command is received from MQTT.
    /// </summary>
    public event EventHandler<LoRaCommand>? CommandReceived;

    /// <summary>
    /// Event raised when a mock control message is received.
    /// </summary>
    public event EventHandler<(string Topic, string Payload)>? MockControlReceived;

    /// <summary>
    /// Gets a value indicating whether the client is connected.
    /// </summary>
    public bool IsConnected => _mqttClient.IsConnected;

    /// <summary>
    /// Initializes a new instance of <see cref="MqttService"/>.
    /// </summary>
    public MqttService(
        ILogger<MqttService> logger,
        IOptions<MqttOptions> options,
        IOptions<LoRaOptions> loraOptions)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(loraOptions);
        _logger = logger;
        _options = options.Value;
        _loraOptions = loraOptions.Value;
        _mqttClient = new MqttFactory().CreateMqttClient();

        _mqttClient.ApplicationMessageReceivedAsync += OnMessageReceivedAsync;
        _mqttClient.DisconnectedAsync += OnDisconnectedAsync;
    }

    /// <summary>
    /// Connects to the MQTT broker.
    /// </summary>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Connecting to MQTT broker at {Host}:{Port}",
            _options.Host, _options.Port);

        var optionsBuilder = new MqttClientOptionsBuilder()
            .WithTcpServer(_options.Host, _options.Port)
            .WithClientId(_options.ClientId)
            .WithCleanSession(_options.CleanSession)
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(_options.KeepAliveSeconds));

        if (_options.Tls.UseTls)
        {
            optionsBuilder = optionsBuilder.WithTlsOptions(BuildTlsOptions(_options.Tls));
        }

        if (!string.IsNullOrEmpty(_options.Username))
        {
            optionsBuilder.WithCredentials(_options.Username, _options.Password);
        }

        var mqttOptions = optionsBuilder.Build();

        var result = await _mqttClient.ConnectAsync(mqttOptions, cancellationToken);

        if (result.ResultCode != MqttClientConnectResultCode.Success)
        {
            throw new InvalidOperationException(
                $"Failed to connect to MQTT broker: {result.ResultCode}");
        }

        _logger.LogInformation("Connected to MQTT broker");

        await SubscribeToCommandsAsync(cancellationToken);
    }

    private async Task SubscribeToCommandsAsync(CancellationToken cancellationToken)
    {
        var topics = GetSubscriptionTopics(_options.BaseTopic, _loraOptions.MockMode);
        foreach (var topic in topics)
        {
            await _mqttClient.SubscribeAsync(
                new MqttTopicFilterBuilder()
                    .WithTopic(topic)
                    .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                    .Build(),
                cancellationToken);
            _logger.LogInformation("Subscribed to topic: {Topic}", topic);
        }

        if (!_loraOptions.MockMode)
        {
            _logger.LogInformation("Mock control topic skipped: LoRaOptions.MockMode is false");
        }
    }

    /// <summary>
    /// MQTT topic patterns this service subscribes to, gated by base topic
    /// and mock mode. Hardware mode returns only the device command topic;
    /// mock mode additionally adds the mock control topic. The mock control
    /// topic must NEVER leak into hardware mode — it is an unauthenticated
    /// back door into the adapter's internal state.
    /// </summary>
    internal static string[] GetSubscriptionTopics(string baseTopic, bool mockMode)
    {
        var commandTopic = $"{baseTopic}/+/set";
        if (!mockMode)
        {
            return new[] { commandTopic };
        }
        var mockTopic = $"{baseTopic}/mock/#";
        return new[] { commandTopic, mockTopic };
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "MQTT message handler must swallow every payload/deserialize failure; a throw here would tear down the broker client.")]
    private Task OnMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs args)
    {
        try
        {
            var topic = args.ApplicationMessage.Topic;
            var payload = Encoding.UTF8.GetString(args.ApplicationMessage.PayloadSegment);

            _logger.LogDebug("Received MQTT message on {Topic}: {Payload}", topic, payload);

            if (topic.Contains("/mock/", StringComparison.Ordinal))
            {
                MockControlReceived?.Invoke(this, (topic, payload));
            }
            else if (topic.EndsWith("/set", StringComparison.Ordinal))
            {
                var command = JsonSerializer.Deserialize<LoRaCommand>(payload, _jsonOptions);
                if (command is not null)
                {
                    CommandReceived?.Invoke(this, command);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing MQTT message");
        }

        return Task.CompletedTask;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Reconnect loop must absorb any MQTTnet/socket failure and keep retrying; propagating would crash the host.")]
    private async Task OnDisconnectedAsync(MqttClientDisconnectedEventArgs args)
    {
        _logger.LogWarning(
            "Disconnected from MQTT broker: {Reason}",
            args.Reason);

        if (args.Exception is not null)
        {
            _logger.LogError(args.Exception, "MQTT disconnect exception");
        }

        // Attempt to reconnect, honouring the shutdown signal so a
        // DisconnectAsync call aborts the retry instead of waiting out
        // the 5 s timer and then reconnecting.
        try
        {
            await Task.Delay(5000, _shutdownCts.Token);
            await ConnectAsync(_shutdownCts.Token);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Reconnect retry cancelled: bridge is shutting down");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reconnect to MQTT broker");
        }
    }

    /// <summary>
    /// Publishes a LoRa message to MQTT.
    /// Deserializes the payload JSON and merges it with RSSI/SNR metadata.
    /// </summary>
    public async Task PublishMessageAsync(
        string deviceId,
        LoRaMessage message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!_mqttClient.IsConnected)
        {
            _logger.LogWarning("Cannot publish - not connected to MQTT broker");
            return;
        }

        var topic = $"{_options.BaseTopic}/{deviceId}";
        var payload = BuildMergedPayload(message);

        var mqttMessage = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .WithRetainFlag(false)
            .Build();

        await _mqttClient.PublishAsync(mqttMessage, cancellationToken);

        _logger.LogDebug("Published to {Topic}: {Payload}", topic, payload);
    }

    /// <summary>
    /// Builds the merged payload by deserializing the payload JSON and adding RSSI/SNR.
    /// </summary>
    private string BuildMergedPayload(LoRaMessage message)
    {
        try
        {
            using var doc = JsonDocument.Parse(message.Payload);
            var mergedData = new Dictionary<string, object>();

            foreach (var property in doc.RootElement.EnumerateObject())
            {
                mergedData[property.Name] = GetJsonValue(property.Value);
            }

            if (message.Rssi.HasValue)
                mergedData["rssi"] = message.Rssi.Value;

            if (message.Snr.HasValue)
                mergedData["snr"] = message.Snr.Value;

            return JsonSerializer.Serialize(mergedData, _jsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Payload is not valid JSON, publishing raw with metadata");
            return JsonSerializer.Serialize(new
            {
                data = message.Payload,
                rssi = message.Rssi,
                snr = message.Snr
            }, _jsonOptions);
        }
    }

    /// <summary>
    /// Extracts the value from a JsonElement to an appropriate .NET type.
    /// </summary>
    private static object GetJsonValue(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.Number when element.TryGetInt32(out var i) => i,
            JsonValueKind.Number when element.TryGetInt64(out var l) => l,
            JsonValueKind.Number => element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null!,
            JsonValueKind.Array => element.EnumerateArray().Select(GetJsonValue).ToArray(),
            JsonValueKind.Object => element.EnumerateObject()
                .ToDictionary(p => p.Name, p => GetJsonValue(p.Value)),
            _ => element.GetRawText()
        };
    }

    /// <summary>
    /// Publishes bridge status to MQTT.
    /// </summary>
    public async Task PublishBridgeStatusAsync(
        bool online,
        CancellationToken cancellationToken = default)
    {
        if (!_mqttClient.IsConnected) return;

        var topic = $"{_options.BaseTopic}/bridge/state";
        var payload = JsonSerializer.Serialize(new { state = online ? "online" : "offline" });

        var mqttMessage = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .WithRetainFlag(true)
            .Build();

        await _mqttClient.PublishAsync(mqttMessage, cancellationToken);
    }

    /// <summary>
    /// Disconnects from the MQTT broker.
    /// </summary>
    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        // Abort any pending auto-reconnect retry. Idempotent on a
        // second call: Cancel on an already-cancelled CTS is a no-op.
        _shutdownCts.Cancel();

        if (_mqttClient.IsConnected)
        {
            await PublishBridgeStatusAsync(false, cancellationToken);
            await _mqttClient.DisconnectAsync(cancellationToken: cancellationToken);
            _logger.LogInformation("Disconnected from MQTT broker");
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        // Idempotent: a second dispose would hit the disposed CTS
        // inside DisconnectAsync's Cancel().
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        await DisconnectAsync();
        _mqttClient.Dispose();
        _shutdownCts.Dispose();
    }

    private static MqttClientTlsOptions BuildTlsOptions(MqttTlsOptions tls)
    {
        var builder = new MqttClientTlsOptionsBuilder()
            .UseTls()
            .WithAllowUntrustedCertificates(tls.AllowUntrustedCertificates)
            .WithIgnoreCertificateChainErrors(tls.IgnoreCertificateChainErrors)
            .WithIgnoreCertificateRevocationErrors(tls.IgnoreCertificateRevocationErrors);

        var certs = new List<X509Certificate2>();
        // Client leaf first; CA last (chain). Reverse order makes MQTTnet
        // present the CA root as the peer leaf → "Peer could not be authenticated".
        if (!string.IsNullOrEmpty(tls.ClientCertificatePath) && File.Exists(tls.ClientCertificatePath))
        {
            // PEM-loaded X509Certificate2 on Linux holds an ephemeral
            // private key that SslStream silently REFUSES to present during
            // mTLS handshake — broker then drops with "Connection closed".
            // Round-tripping through PKCS#12 materializes the key into
            // CryptoAPI/OpenSSL native form so SslStream uses it. Confirmed
            // by .NET docs (X509Certificate2 ephemeral-key gotcha).
            using var ephemeral = !string.IsNullOrEmpty(tls.ClientKeyPath) && File.Exists(tls.ClientKeyPath)
                ? X509Certificate2.CreateFromPemFile(tls.ClientCertificatePath, tls.ClientKeyPath)
                : X509Certificate2.CreateFromPemFile(tls.ClientCertificatePath);
            var clientCert = X509CertificateLoader.LoadPkcs12(
                ephemeral.Export(X509ContentType.Pkcs12), password: null);
            certs.Add(clientCert);
        }
        if (!string.IsNullOrEmpty(tls.CaBundlePath) && File.Exists(tls.CaBundlePath))
        {
            certs.Add(X509CertificateLoader.LoadCertificateFromFile(tls.CaBundlePath));
        }
        if (certs.Count > 0)
        {
            builder = builder.WithClientCertificates(certs);
        }

        return builder.Build();
    }
}
