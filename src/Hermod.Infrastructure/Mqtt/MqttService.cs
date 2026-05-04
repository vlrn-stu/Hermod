using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Channels;
using Hermod.Core.Configuration;
using Hermod.Core.Interfaces;
using Hermod.Core.Models;
using Hermod.Core.Telemetry;
using Hermod.Infrastructure.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;

namespace Hermod.Infrastructure.Mqtt;

/// <summary>
/// MQTTnet-backed <see cref="IMqttService"/> with an exponential-backoff
/// reconnect supervisor and a bounded DropOldest outbox so publishes issued
/// during an outage replay on reconnect without unbounded memory growth.
/// </summary>
public class MqttService : IMqttService, IAsyncDisposable
{
    private readonly IMqttClient _mqttClient;
    private readonly MqttClientOptions _mqttOptions;
    private readonly ILogger<MqttService> _logger;
    private readonly MqttSettings _settings;
    private readonly HermodMetrics _metrics;
    private readonly IProtocolFlowLimiter? _protocolLimiter;
    // 1 Hz throttle on the egress limiter warn — under sustained drop the
    // log volume would otherwise rival the dropped publish rate itself.
    private long _lastEgressLimitWarnTicks;
    // Snapshot rebuilt lazily on read; rebuilding on every inbound
    // message dominated throughput at 10k msg/s.
    private readonly Queue<MqttMessage> _messageHistory = new();
    private readonly object _historyLock = new();
    private ImmutableArray<MqttMessage> _historySnapshot = ImmutableArray<MqttMessage>.Empty;
    private volatile bool _historySnapshotDirty;
    private const int MaxHistorySize = 500;

    private readonly CancellationTokenSource _lifetimeCts = new();
    private volatile bool _reconnectEnabled;
    private int _disposed;

    // Outbox for publishes issued while disconnected. Null when
    // Mqtt:ReconnectBufferSize=0 (default: throw on disconnected publish).
    private readonly Channel<PendingPublish>? _outbox;

    // Replayed on every OnConnected: CleanSession=true drops broker-side
    // filters, and some brokers silently discard session state on reconnect.
    private readonly HashSet<string> _subscriptions = new(StringComparer.Ordinal);
    private readonly object _subscriptionsLock = new();

    /// <inheritdoc/>
    public bool IsConnected => _mqttClient.IsConnected;

    /// <inheritdoc/>
    public event EventHandler<MqttMessage>? MessageReceived;

    /// <inheritdoc/>
    public event EventHandler<bool>? ConnectionStateChanged;

    /// <summary>
    /// Creates the MQTT client, builds its connect options from
    /// <see cref="HermodSettings.Mqtt"/>, and wires the outbox when
    /// <c>ReconnectBufferSize</c> is positive.
    /// </summary>
    public MqttService(
        IOptions<HermodSettings> settings,
        HermodMetrics metrics,
        ILogger<MqttService> logger,
        IProtocolFlowLimiter? protocolLimiter = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _settings = settings.Value.Mqtt;
        _metrics = metrics;
        _protocolLimiter = protocolLimiter;

        var factory = new MqttFactory();
        _mqttClient = factory.CreateMqttClient();

        var optionsBuilder = new MqttClientOptionsBuilder()
            .WithTcpServer(_settings.Host, _settings.Port)
            .WithClientId(_settings.ClientId)
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(_settings.KeepAliveSeconds))
            .WithCleanSession(_settings.CleanSession);

        if (!string.IsNullOrEmpty(_settings.Username))
        {
            optionsBuilder = optionsBuilder.WithCredentials(_settings.Username, _settings.Password ?? string.Empty);
        }

        if (_settings.Tls.UseTls)
        {
            optionsBuilder = optionsBuilder.WithTlsOptions(BuildTlsOptions(_settings.Tls));
        }

        _mqttOptions = optionsBuilder.Build();

        _mqttClient.ApplicationMessageReceivedAsync += OnMessageReceived;
        _mqttClient.ConnectedAsync += OnConnected;
        _mqttClient.DisconnectedAsync += OnDisconnected;

        if (_settings.ReconnectBufferSize > 0)
        {
            _outbox = Channel.CreateBounded<PendingPublish>(
                new BoundedChannelOptions(_settings.ReconnectBufferSize)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                    SingleWriter = false,
                });

            var outbox = _outbox;
            _metrics.MqttOutboxDepthProvider = () => outbox.Reader.Count;
        }
    }

    /// <inheritdoc/>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Connecting to MQTT broker at {Host}:{Port}", _settings.Host, _settings.Port);
            await _mqttClient.ConnectAsync(_mqttOptions, cancellationToken);
            _reconnectEnabled = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to MQTT broker");
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        // Disable the supervisor before the explicit disconnect so the
        // reconnect loop does not race us back online. A reconnect iteration
        // that is already inside ConnectAsync can still finish; if it wins
        // the race we disconnect a second time. Bounded to 3 attempts so a
        // pathologically fast reconnect loop cannot spin us forever.
        _reconnectEnabled = false;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (!_mqttClient.IsConnected) return;
            _logger.LogInformation("Disconnecting from MQTT broker");
            await _mqttClient.DisconnectAsync(cancellationToken: cancellationToken);
        }
    }

    /// <inheritdoc/>
    public async Task SubscribeAsync(string topic, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Subscribing to topic: {Topic}", topic);
        lock (_subscriptionsLock)
        {
            _subscriptions.Add(topic);
        }

        var subscribeOptions = new MqttClientSubscribeOptionsBuilder()
            .WithTopicFilter(topic)
            .Build();

        await _mqttClient.SubscribeAsync(subscribeOptions, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task UnsubscribeAsync(string topic, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Unsubscribing from topic: {Topic}", topic);
        lock (_subscriptionsLock)
        {
            _subscriptions.Remove(topic);
        }
        await _mqttClient.UnsubscribeAsync(topic, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task PublishAsync(string topic, string payload, bool retain = false, int qos = 0, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(topic);
        ArgumentNullException.ThrowIfNull(payload);
        // Egress side of the per-protocol aggregate limiter. Caps how
        // many publishes per second can leave the coordinator for a
        // protocol family — protects translators / radios from being
        // swamped by a runaway rule fan-out, and balances the ingress
        // hook so "limit zigbee to N msg/s" actually means N total in
        // each direction. Bypassed when the limiter is null (tests) or
        // when ProtocolLimits.Enabled=false. Outbox-drain replays don't
        // re-trigger this — those messages already passed it on the
        // way in, throttling them again on reconnect would double-bill.
        if (_protocolLimiter is not null)
        {
            var slash = topic.IndexOf('/', StringComparison.Ordinal);
            var prefix = slash < 0 ? topic.AsSpan() : topic.AsSpan(0, slash);
            var protocol = ProtocolExtensions.FromTopicPrefix(prefix);
            var verdict = _protocolLimiter.TryAccept(protocol, FlowDirection.Egress, DateTimeOffset.UtcNow);
            if (!verdict.Accept)
            {
                _metrics.IncProtocolLimitedEgress(protocol);
                _metrics.IncRateLimitedTotal();
                var nowTicks = DateTime.UtcNow.Ticks;
                var lastTicks = Interlocked.Read(ref _lastEgressLimitWarnTicks);
                if (nowTicks - lastTicks >= TimeSpan.TicksPerSecond &&
                    Interlocked.CompareExchange(ref _lastEgressLimitWarnTicks, nowTicks, lastTicks) == lastTicks)
                {
                    _logger.LogWarning("Protocol limiter dropped egress to {Topic} ({Protocol})", topic, protocol);
                }
                return;
            }
        }

        if (!_mqttClient.IsConnected && _outbox is not null)
        {
            // DropOldest TryWrite always succeeds; depth-before-write
            // approximates whether an oldest item was evicted.
            var beforeDepth = _outbox.Reader.Count;
            _outbox.Writer.TryWrite(new PendingPublish(topic, payload, retain, qos));
            if (beforeDepth >= _settings.ReconnectBufferSize)
            {
                _metrics.IncMqttOutboxDropped();
            }
            _metrics.IncMqttOutboxEnqueued();
            _logger.LogDebug("MQTT outbox queued {Topic} (disconnected)", topic);
            return;
        }

        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithRetainFlag(retain)
            .WithQualityOfServiceLevel((MqttQualityOfServiceLevel)qos)
            .Build();

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("Publishing to {Topic}: {Payload}", topic, payload);
        }
        await _mqttClient.PublishAsync(message, cancellationToken);
    }

    private Task OnMessageReceived(MqttApplicationMessageReceivedEventArgs e)
    {
        _metrics.IncMessagesIngested();
        var message = new MqttMessage
        {
            Topic = e.ApplicationMessage.Topic,
            Payload = Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment),
            ReceivedAt = DateTime.UtcNow,
            Retained = e.ApplicationMessage.Retain,
            QoS = (int)e.ApplicationMessage.QualityOfServiceLevel
        };

        // Queue for O(1) eviction; snapshot rebuilt lazily by readers.
        lock (_historyLock)
        {
            _messageHistory.Enqueue(message);
            while (_messageHistory.Count > MaxHistorySize)
            {
                _messageHistory.Dequeue();
            }
            _historySnapshotDirty = true;
        }

        // IsEnabled guard: avoids the param-array box on the hot path.
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("Received message on {Topic}", message.Topic);
        }
        MessageReceived?.Invoke(this, message);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public IReadOnlyList<MqttMessage> GetMessageHistory()
    {
        // Fast path: no writes since last snapshot.
        if (!_historySnapshotDirty) return _historySnapshot;

        lock (_historyLock)
        {
            if (!_historySnapshotDirty) return _historySnapshot;
            _historySnapshot = _messageHistory.ToImmutableArray();
            _historySnapshotDirty = false;
            return _historySnapshot;
        }
    }

    private static MqttClientTlsOptions BuildTlsOptions(MqttTlsSettings tls)
    {
        var builder = new MqttClientTlsOptionsBuilder()
            .UseTls()
            .WithAllowUntrustedCertificates(tls.AllowUntrustedCertificates)
            .WithIgnoreCertificateChainErrors(tls.IgnoreCertificateChainErrors)
            .WithIgnoreCertificateRevocationErrors(tls.IgnoreCertificateRevocationErrors);

        var certs = new List<X509Certificate2>();

        // Client cert FIRST so MQTTnet/SslStream sees it as the leaf;
        // adding the CA cert before would make NanoMQ treat the CA root
        // as the peer leaf and reject ("Peer could not be authenticated").
        if (!string.IsNullOrEmpty(tls.ClientCertificatePath) && File.Exists(tls.ClientCertificatePath))
        {
            // PEM-loaded X509Certificate2 on Linux holds an ephemeral
            // private key that SslStream silently refuses to present during
            // mTLS handshake — broker then drops with "Connection closed".
            // Round-tripping through PKCS#12 materializes the key into the
            // OpenSSL store so SslStream actually uses it.
            using var ephemeral = !string.IsNullOrEmpty(tls.ClientKeyPath) && File.Exists(tls.ClientKeyPath)
                ? X509Certificate2.CreateFromPemFile(tls.ClientCertificatePath, tls.ClientKeyPath)
                : X509Certificate2.CreateFromPemFile(tls.ClientCertificatePath);
            var clientCert = X509CertificateLoader.LoadPkcs12(
                ephemeral.Export(X509ContentType.Pkcs12), password: null);
            certs.Add(clientCert);
        }

        if (!string.IsNullOrEmpty(tls.CaBundlePath) && File.Exists(tls.CaBundlePath))
        {
            // CreateFromPemFile(path) expects cert+key; the CA bundle has no
            // key. Use the cert-only loader. CA goes last in the list so it's
            // an intermediate/chain cert, not the presented leaf.
            certs.Add(X509CertificateLoader.LoadCertificateFromFile(tls.CaBundlePath));
        }

        if (certs.Count > 0)
        {
            builder = builder.WithClientCertificates(certs);
        }

        return builder.Build();
    }

    private Task OnConnected(MqttClientConnectedEventArgs e)
    {
        _logger.LogInformation("Connected to MQTT broker");

        // Replay as a continuation: some brokers (nanomq) DISCONNECT
        // on SUBSCRIBE before the ConnectedAsync handler returns.
        _ = Task.Run(ReplaySubscriptionsAsync);

        ConnectionStateChanged?.Invoke(this, true);

        if (_outbox is not null)
        {
            _ = Task.Run(DrainOutboxAsync);
        }

        return Task.CompletedTask;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Per-filter resubscribe failure must log-and-continue; one poisoned filter must not block the rest.")]
    private async Task ReplaySubscriptionsAsync()
    {
        string[] snapshot;
        lock (_subscriptionsLock)
        {
            if (_subscriptions.Count == 0) return;
            snapshot = new string[_subscriptions.Count];
            _subscriptions.CopyTo(snapshot);
        }

        var replayed = 0;
        foreach (var topic in snapshot)
        {
            try
            {
                var opts = new MqttClientSubscribeOptionsBuilder()
                    .WithTopicFilter(topic)
                    .Build();
                await _mqttClient.SubscribeAsync(opts, _lifetimeCts.Token);
                replayed++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Replay subscribe failed for {Topic}", topic);
            }
        }

        if (replayed > 0)
        {
            _logger.LogInformation("Replayed {Replayed} MQTT subscription(s) after (re)connect", replayed);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Mid-drain publish failure must be survivable: we requeue the pending row and let the next OnConnected retry the whole loop.")]
    private async Task DrainOutboxAsync()
    {
        if (_outbox is null) return;

        var drained = 0;
        while (_mqttClient.IsConnected && _outbox.Reader.TryRead(out var pending))
        {
            try
            {
                var message = new MqttApplicationMessageBuilder()
                    .WithTopic(pending.Topic)
                    .WithPayload(pending.Payload)
                    .WithRetainFlag(pending.Retain)
                    .WithQualityOfServiceLevel((MqttQualityOfServiceLevel)pending.Qos)
                    .Build();
                await _mqttClient.PublishAsync(message, _lifetimeCts.Token);
                drained++;
                _metrics.IncMqttOutboxDrained();
            }
            catch (Exception ex)
            {
                // Requeue and bail; the next OnConnected retries.
                _outbox.Writer.TryWrite(pending);
                _logger.LogWarning(ex, "MQTT outbox drain interrupted after {Drained}", drained);
                return;
            }
        }

        if (drained > 0)
        {
            _logger.LogInformation("MQTT outbox drained {Drained} buffered messages", drained);
        }
    }

    private Task OnDisconnected(MqttClientDisconnectedEventArgs e)
    {
        _logger.LogWarning("Disconnected from MQTT broker: {Reason}", e.Reason);
        ConnectionStateChanged?.Invoke(this, false);

        if (_reconnectEnabled && !_lifetimeCts.IsCancellationRequested)
        {
            _ = Task.Run(ReconnectLoopAsync);
        }

        return Task.CompletedTask;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Reconnect supervisor must tolerate every transient broker error and keep exponentially backing off until connected.")]
    private async Task ReconnectLoopAsync()
    {
        var delay = TimeSpan.FromSeconds(1);
        var maxDelay = TimeSpan.FromSeconds(60);

        while (_reconnectEnabled && !_lifetimeCts.IsCancellationRequested && !_mqttClient.IsConnected)
        {
            try
            {
                await Task.Delay(delay, _lifetimeCts.Token);

                // Defensive disconnect: MQTTnet's state can lag the
                // OnDisconnected event briefly, making ConnectAsync
                // throw "already connected".
                if (_mqttClient.IsConnected)
                {
                    try { await _mqttClient.DisconnectAsync(cancellationToken: _lifetimeCts.Token); }
                    catch { /* swallow; about to reconnect */ }
                }

                _logger.LogInformation("Reconnecting to MQTT broker at {Host}:{Port}", _settings.Host, _settings.Port);
                await _mqttClient.ConnectAsync(_mqttOptions, _lifetimeCts.Token);
                _metrics.IncMqttReconnects();
                _logger.LogInformation("MQTT reconnect succeeded");
                // OnConnected replays subscriptions as a continuation.
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (InvalidOperationException ioe) when (ioe.Message.Contains("already", StringComparison.OrdinalIgnoreCase))
            {
                // Already connected — race with a concurrent OnConnected.
                _logger.LogDebug("MQTT reconnect race: client was already connected, exiting loop");
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MQTT reconnect attempt failed; next retry in {Delay}", delay);
                delay = TimeSpan.FromSeconds(Math.Min(maxDelay.TotalSeconds, delay.TotalSeconds * 2));
            }
        }
    }

    /// <inheritdoc/>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "A failing DisconnectAsync must not leak the underlying client or CTS; the dispose path is the last line of defence.")]
    public async ValueTask DisposeAsync()
    {
        // Idempotent: a second dispose would throw on the disposed CTS.
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _reconnectEnabled = false;
        _lifetimeCts.Cancel();
        try
        {
            await DisconnectAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MQTT disconnect during dispose failed; releasing resources anyway");
        }
        finally
        {
            _mqttClient.Dispose();
            _lifetimeCts.Dispose();
            GC.SuppressFinalize(this);
        }
    }

    private readonly record struct PendingPublish(string Topic, string Payload, bool Retain, int Qos);
}
