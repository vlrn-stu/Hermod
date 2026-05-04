using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;

namespace Hermod.TestHarness;

/// <summary>
/// MQTT client for Node 2 to drive Node 1's NanoMQ broker.
///
/// Contract (see docs/TESTING_HARNESS.md section 2):
///
/// 1. Idempotent Connect: calling ConnectAsync twice is a no-op.
/// 2. Correlation-id waiters: PublishAndWaitAsync embeds a GUID into the source
///    payload under the reserved "__corr" field and matches the forwarded
///    payload by that GUID, not by topic identity. Two concurrent waiters on
///    the same topic never collide.
/// 3. Pre-subscribed wait topics: repeated SubscribeAsync calls for the same
///    topic are deduplicated.
/// 4. Ordered shutdown: DisposeAsync unsubscribes the handler, cancels pending
///    waiters, then disposes the underlying client.
/// 5. Received snapshot: ReceivedMessages returns a defensive array copy.
/// </summary>
public sealed class MqttTestClient : IAsyncDisposable
{
    public const string CorrelationField = "__corr";

    private readonly ILogger<MqttTestClient> _logger;
    private readonly string _host;
    private readonly int _port;
    private readonly string? _username;
    private readonly string? _password;

    private IMqttClient? _client;
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly ConcurrentDictionary<string, byte> _subscribed = new();

    // Correlation-id waiters: keyed by __corr GUID, resolved when a message
    // arrives on any topic carrying the same correlation id. Topic collisions
    // cannot happen because the key is the GUID, not the topic.
    private readonly ConcurrentDictionary<string, TaskCompletionSource<MqttApplicationMessage>> _correlationWaiters = new();

    private readonly ConcurrentQueue<(string Topic, string Payload, DateTimeOffset Timestamp)> _received = new();
    private int _inFlightPublishes;

    public MqttTestClient(ILogger<MqttTestClient> logger, IConfiguration config)
    {
        _logger = logger;
        _host = config["Mqtt:Host"] ?? Environment.GetEnvironmentVariable("MQTT_HOST") ?? "localhost";
        _port = int.TryParse(config["Mqtt:Port"] ?? Environment.GetEnvironmentVariable("MQTT_PORT"), out var p) ? p : 1883;
        _username = config["Mqtt:Username"] ?? Environment.GetEnvironmentVariable("MQTT_USERNAME");
        _password = config["Mqtt:Password"] ?? Environment.GetEnvironmentVariable("MQTT_PASSWORD");
    }

    public bool IsConnected => _client is { IsConnected: true };

    public IReadOnlyList<(string Topic, string Payload, DateTimeOffset Timestamp)> ReceivedMessages
        => _received.ToArray();

    public void ClearReceived()
    {
        while (_received.TryDequeue(out _)) { }
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        await _connectLock.WaitAsync(ct);
        try
        {
            if (_client is { IsConnected: true })
                return;

            _client?.Dispose();

            var factory = new MqttFactory();
            _client = factory.CreateMqttClient();

            _client.ApplicationMessageReceivedAsync += OnMessageAsync;

            var builder = new MqttClientOptionsBuilder()
                .WithTcpServer(_host, _port)
                .WithClientId($"hermod-test-{Environment.MachineName}-{Guid.NewGuid():N}"[..23])
                .WithCleanSession()
                .WithTimeout(TimeSpan.FromSeconds(10));

            if (!string.IsNullOrEmpty(_username))
            {
                builder = builder.WithCredentials(_username, _password ?? string.Empty);
            }

            await _client.ConnectAsync(builder.Build(), ct);
            _logger.LogInformation("Connected to MQTT broker at {Host}:{Port}", _host, _port);

            // Re-subscribe to any topics that were previously tracked. This
            // keeps the client idempotent across reconnects during a test run.
            foreach (var topic in _subscribed.Keys.ToList())
            {
                await _client.SubscribeAsync(
                    new MqttTopicFilterBuilder().WithTopic(topic).Build(), ct);
            }
        }
        finally
        {
            _connectLock.Release();
        }
    }

    public async Task SubscribeAsync(string topic, CancellationToken ct = default)
    {
        // Auto-reconnect if the MQTTnet client silently dropped its TCP
        // session. This happens during perf suites: ConnectAsync returns
        // success, harness idles while the HTTP rule-seed call runs, MQTT
        // keepalive drops, next Subscribe would throw "Not connected" and
        // wedge the whole suite. ConnectAsync is idempotent (locks, no-ops
        // if already connected) and re-subscribes the known topics.
        if (_client is null || !_client.IsConnected)
        {
            _logger.LogWarning("MQTT stale at SubscribeAsync({Topic}); reconnecting", topic);
            await ConnectAsync(ct);
            if (_client is null || !_client.IsConnected)
                throw new InvalidOperationException("Not connected (reconnect failed)");
        }

        if (!_subscribed.TryAdd(topic, 0))
            return; // already subscribed

        await _client.SubscribeAsync(
            new MqttTopicFilterBuilder().WithTopic(topic).Build(), ct);
    }

    public async Task PublishAsync(string topic, string payload, CancellationToken ct = default)
    {
        // Same reconnect-on-stale dance as SubscribeAsync.
        if (_client is null || !_client.IsConnected)
        {
            _logger.LogWarning("MQTT stale at PublishAsync({Topic}); reconnecting", topic);
            await ConnectAsync(ct);
            if (_client is null || !_client.IsConnected)
                throw new InvalidOperationException("Not connected (reconnect failed)");
        }

        Interlocked.Increment(ref _inFlightPublishes);
        try
        {
            var msg = new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(payload)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .Build();

            await _client.PublishAsync(msg, ct);
        }
        finally
        {
            Interlocked.Decrement(ref _inFlightPublishes);
        }
    }

    /// <summary>
    /// Publishes a payload with an embedded correlation id and waits for any
    /// incoming message whose payload carries the same id. The wait topic is
    /// pre-subscribed so multiple concurrent callers cannot collide on it.
    /// </summary>
    public async Task<CorrelatedResponse> PublishAndWaitAsync(
        string publishTopic,
        string publishPayload,
        string waitTopic,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        var corrId = Guid.NewGuid().ToString("N");
        var correlated = InjectCorrelationId(publishPayload, corrId);

        var tcs = new TaskCompletionSource<MqttApplicationMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _correlationWaiters[corrId] = tcs;

        await SubscribeAsync(waitTopic, ct);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await PublishAsync(publishTopic, correlated, ct);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);

            try
            {
                var msg = await tcs.Task.WaitAsync(timeoutCts.Token);
                sw.Stop();
                var payload = Encoding.UTF8.GetString(msg.PayloadSegment);
                return new CorrelatedResponse(corrId, msg.Topic, payload, sw.Elapsed.TotalMilliseconds);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                sw.Stop();
                return new CorrelatedResponse(corrId, null, null, null);
            }
        }
        finally
        {
            _correlationWaiters.TryRemove(corrId, out _);
        }
    }

    private static string InjectCorrelationId(string payload, string corrId)
    {
        if (string.IsNullOrWhiteSpace(payload))
            payload = "{}";

        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                // Non-object payloads cannot carry an inline correlation id,
                // so we wrap them.
                return JsonSerializer.Serialize(new
                {
                    __corr = corrId,
                    value = payload
                });
            }

            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteString(CorrelationField, corrId);
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (prop.NameEquals(CorrelationField)) continue;
                    prop.WriteTo(writer);
                }
                writer.WriteEndObject();
            }
            return Encoding.UTF8.GetString(stream.ToArray());
        }
        catch (JsonException)
        {
            // Malformed JSON input: wrap it so the correlation field still
            // makes it to the broker for tests that deliberately publish
            // garbage payloads.
            return JsonSerializer.Serialize(new
            {
                __corr = corrId,
                value = payload
            });
        }
    }

    private Task OnMessageAsync(MqttApplicationMessageReceivedEventArgs msg)
    {
        var topic = msg.ApplicationMessage.Topic;
        var payload = Encoding.UTF8.GetString(msg.ApplicationMessage.PayloadSegment);
        var ts = DateTimeOffset.UtcNow;

        _received.Enqueue((topic, payload, ts));

        // Check for an embedded correlation id. Forwarded payloads that
        // propagate the __corr field resolve their waiter immediately.
        var corrId = TryReadCorrelationId(payload);
        if (corrId is not null && _correlationWaiters.TryRemove(corrId, out var tcs))
        {
            tcs.TrySetResult(msg.ApplicationMessage);
        }

        return Task.CompletedTask;
    }

    private static string? TryReadCorrelationId(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload) || payload[0] != '{')
            return null;

        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return null;
            if (!doc.RootElement.TryGetProperty(CorrelationField, out var prop))
                return null;
            return prop.ValueKind == JsonValueKind.String ? prop.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Cancel any still-pending waiters so tests in the same process that
        // held a Task reference observe a clean cancellation instead of a
        // dangling await.
        foreach (var waiter in _correlationWaiters.Values)
        {
            waiter.TrySetCanceled();
        }
        _correlationWaiters.Clear();

        // Wait up to 2 seconds for in-flight publishes to drain before
        // disposing the underlying client.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (Volatile.Read(ref _inFlightPublishes) > 0 && sw.Elapsed < TimeSpan.FromSeconds(2))
        {
            await Task.Delay(10);
        }

        if (_client is not null)
        {
            _client.ApplicationMessageReceivedAsync -= OnMessageAsync;
            if (_client.IsConnected)
            {
                try { await _client.DisconnectAsync(); }
                catch { /* best effort */ }
            }
            _client.Dispose();
            _client = null;
        }

        _connectLock.Dispose();
    }
}

/// <summary>
/// Result of a correlated round trip. Topic and Payload are null on timeout.
/// </summary>
public sealed record CorrelatedResponse(
    string CorrelationId,
    string? ForwardedTopic,
    string? ForwardedPayload,
    double? LatencyMs);
