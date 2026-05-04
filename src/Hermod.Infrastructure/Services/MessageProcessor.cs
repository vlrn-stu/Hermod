using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Threading.Channels;
using Hermod.Core.Configuration;
using Hermod.Core.Interfaces;
using Hermod.Core.Models;
using Hermod.Core.Mqtt;
using Hermod.Core.Telemetry;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hermod.Infrastructure.Services;

/// <summary>
/// Hosted MQTT ingest pump: serialises broker callbacks into a bounded
/// channel and dispatches them sequentially to device tracking, the
/// Zigbee2MQTT bridge, and the rules engine.
/// </summary>
public sealed class MessageProcessor : BackgroundService
{
    private readonly IMqttService _mqttService;
    private readonly IDeviceService _deviceService;
    private readonly IRulesEngine _rulesEngine;
    private readonly IStatsService _statsService;
    private readonly IZigbee2MqttService _zigbee2MqttService;
    private readonly IMessageHistoryRepository _messageHistory;
    private readonly BatchedDeviceStateWriter? _deviceStateWriter;
    private readonly HermodMetrics _metrics;
    private readonly ITimestampRecorder _timestampRecorder;
    private readonly ITopicIngressLimiter _limiter;
    private readonly IProtocolFlowLimiter? _protocolLimiter;
    private readonly HermodSettings _settings;
    private readonly ILogger<MessageProcessor> _logger;

    private readonly Channel<MqttMessage> _messageQueue;
    private readonly int _queueCapacity;
    // Rate-limit the "queue full" warning: during sustained saturation the
    // per-message warn was firing at the same rate as ingest (5k/s +), and
    // the log pipeline pushed back on the MQTT I/O thread — making the drop
    // worse. 1 Hz is enough to surface the condition to operators.
    private long _lastDropWarnTicks;
    // Same 1 Hz throttle for the per-topic limiter rejection log; under a
    // sustained flood the rejection rate matches the attacker's send rate.
    private long _lastLimitWarnTicks;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Creates the ingest pump (bounded DropOldest queue, capacity clamped to >=128).</summary>
    public MessageProcessor(
        IMqttService mqttService,
        IDeviceService deviceService,
        IRulesEngine rulesEngine,
        IStatsService statsService,
        IZigbee2MqttService zigbee2MqttService,
        IMessageHistoryRepository messageHistory,
        HermodMetrics metrics,
        ITimestampRecorder timestampRecorder,
        ITopicIngressLimiter limiter,
        IOptions<HermodSettings> settings,
        ILogger<MessageProcessor> logger,
        BatchedDeviceStateWriter? deviceStateWriter = null,
        IProtocolFlowLimiter? protocolLimiter = null)
    {
        ArgumentNullException.ThrowIfNull(mqttService);
        ArgumentNullException.ThrowIfNull(deviceService);
        ArgumentNullException.ThrowIfNull(rulesEngine);
        ArgumentNullException.ThrowIfNull(statsService);
        ArgumentNullException.ThrowIfNull(zigbee2MqttService);
        ArgumentNullException.ThrowIfNull(messageHistory);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(timestampRecorder);
        ArgumentNullException.ThrowIfNull(limiter);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);
        _mqttService = mqttService;
        _deviceService = deviceService;
        _rulesEngine = rulesEngine;
        _statsService = statsService;
        _zigbee2MqttService = zigbee2MqttService;
        _messageHistory = messageHistory;
        _deviceStateWriter = deviceStateWriter;
        _metrics = metrics;
        _timestampRecorder = timestampRecorder;
        _limiter = limiter;
        _protocolLimiter = protocolLimiter;
        _settings = settings.Value;
        _logger = logger;

        _queueCapacity = Math.Max(128, _settings.Engine.QueueCapacity);
        // SingleReader=false only when Parallelism>1 (N drain tasks).
        // SingleWriter is always false: ParallelMqttService fans in.
        var parallel = Math.Max(1, _settings.Engine.Parallelism);
        _messageQueue = Channel.CreateBounded<MqttMessage>(
            new BoundedChannelOptions(_queueCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = parallel == 1,
                SingleWriter = false,
            });
        _metrics.IngestQueueDepthProvider = () => _messageQueue.Reader.Count;
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _mqttService.MessageReceived += OnMessageReceived;
        _mqttService.ConnectionStateChanged += OnConnectionStateChanged;

        // Detach producers + complete the writer on cancel so buffered
        // messages drain via channel completion, not token cancellation.
        using var shutdownRegistration = stoppingToken.Register(() =>
        {
            _mqttService.MessageReceived -= OnMessageReceived;
            _mqttService.ConnectionStateChanged -= OnConnectionStateChanged;
            _messageQueue.Writer.TryComplete();
            _logger.LogInformation("Message processor stopping; draining remaining messages");
        });

        try
        {
            await _mqttService.ConnectAsync(stoppingToken);
            await SubscribeToTopicsAsync(stoppingToken);

            _logger.LogInformation("Message processor started with ordered queue");

            // CancellationToken.None is intentional — shutdown comes
            // via writer-completion above. Parallelism>1 fans out N
            // reader tasks sharing the same bounded channel.
            var batchSize = Math.Max(1, _settings.Engine.BatchSize);
            var parallel = Math.Max(1, _settings.Engine.Parallelism);
            var reader = _messageQueue.Reader;

            if (parallel == 1)
            {
                while (await reader.WaitToReadAsync(CancellationToken.None))
                {
                    var drained = 0;
                    while (drained < batchSize && reader.TryRead(out var message))
                    {
                        await ProcessMessageAsync(message);
                        drained++;
                    }
                }
            }
            else
            {
                var readers = new Task[parallel];
                for (var i = 0; i < parallel; i++)
                {
                    readers[i] = Task.Run(async () =>
                    {
                        while (await reader.WaitToReadAsync(CancellationToken.None))
                        {
                            var drained = 0;
                            while (drained < batchSize && reader.TryRead(out var message))
                            {
                                await ProcessMessageAsync(message);
                                drained++;
                            }
                        }
                    }, CancellationToken.None);
                }
                await Task.WhenAll(readers);
            }

            _logger.LogInformation("Message processor drained and stopped cleanly");
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Message processor cancelled before reader loop started");
        }
        finally
        {
            // Idempotent safety net for any cancellation path that bypasses
            // the registered callback above.
            _mqttService.MessageReceived -= OnMessageReceived;
            _mqttService.ConnectionStateChanged -= OnConnectionStateChanged;
            _messageQueue.Writer.TryComplete();
            await _mqttService.DisconnectAsync(CancellationToken.None);
        }
    }

    private async Task SubscribeToTopicsAsync(CancellationToken cancellationToken)
    {
        // Five protocol subtrees (not bare `#`) so parallel client
        // shards each get a slice. A single `#` prefix-hashes to one
        // shard and silences the rest.
        string[] subscriptionSet =
        {
            "zigbee/#",
            "lora/#",
            "wifi/#",
            "bluetooth/#",
            "hermod/#",
        };
        foreach (var topic in subscriptionSet)
        {
            await _mqttService.SubscribeAsync(topic, cancellationToken);
        }
        _logger.LogInformation("Subscribed to {Count} MQTT protocol subtrees", subscriptionSet.Length);
    }

    private void OnMessageReceived(object? sender, MqttMessage message)
    {
        // Substring probe short-circuits on the no-uuid production
        // path before JsonDocument.Parse.
        var trace = message.TraceUuid ?? PayloadUuidExtractor.TryExtract(message.Payload);
        MqttMessage stamped = trace is null || ReferenceEquals(trace, message.TraceUuid)
            ? message
            : new MqttMessage
            {
                Topic = message.Topic,
                Payload = message.Payload,
                ReceivedAt = message.ReceivedAt,
                Retained = message.Retained,
                QoS = message.QoS,
                TraceUuid = trace,
            };
        if (trace is not null)
        {
            _timestampRecorder.Record(trace, TimestampStages.BrokerRx, UnixNanoseconds());
        }

        // Per-(protocol, topic) hard limit. Runs upstream of the bounded
        // ingest channel so a flooding sender cannot fill the queue and
        // starve legit traffic via DropOldest eviction. No-op when
        // RateLimit:Enabled=false (dev compose, kind, unit tests).
        var verdict = _limiter.TryAccept(stamped.Topic, stamped.Payload, DateTimeOffset.UtcNow);
        if (!verdict.Accept)
        {
            if (verdict.Reason == "rate")
            {
                _metrics.IncTopicLimitedRate();
                _metrics.IncRateLimitedTotal();
            }
            else _metrics.IncTopicLimitedDedup();
            var nowTicksLim = DateTime.UtcNow.Ticks;
            var lastLim = Interlocked.Read(ref _lastLimitWarnTicks);
            if (nowTicksLim - lastLim >= TimeSpan.TicksPerSecond &&
                Interlocked.CompareExchange(ref _lastLimitWarnTicks, nowTicksLim, lastLim) == lastLim)
            {
                _logger.LogWarning("Ingress limiter dropped {Reason} on topic {Topic}", verdict.Reason, stamped.Topic);
            }
            return;
        }

        // Aggregate per-protocol limiter — second clamp after the per-topic
        // one. Catches "all of bluetooth misbehaving at once" even when no
        // single topic exceeds its own bucket. Bypassed when null (DI omits
        // it on test harnesses) or when ProtocolLimits.Enabled=false.
        if (_protocolLimiter is not null)
        {
            var ingressProtocol = TopicToProtocol(stamped.Topic);
            var protoVerdict = _protocolLimiter.TryAccept(ingressProtocol, FlowDirection.Ingress, DateTimeOffset.UtcNow);
            if (!protoVerdict.Accept)
            {
                _metrics.IncProtocolLimitedIngress(ingressProtocol);
                _metrics.IncRateLimitedTotal();
                var nowTicksProto = DateTime.UtcNow.Ticks;
                var lastProto = Interlocked.Read(ref _lastLimitWarnTicks);
                if (nowTicksProto - lastProto >= TimeSpan.TicksPerSecond &&
                    Interlocked.CompareExchange(ref _lastLimitWarnTicks, nowTicksProto, lastProto) == lastProto)
                {
                    _logger.LogWarning("Protocol limiter dropped ingress on {Protocol} (topic {Topic})", ingressProtocol, stamped.Topic);
                }
                return;
            }
        }

        // DropOldest makes Writer.TryWrite always return true (it silently
        // evicts the oldest item when full), so we detect drops by counting
        // the queue before the write. The count is monotonic enough for a
        // cumulative counter; a one-message race between Count and TryWrite
        // is acceptable. The only path that returns false from TryWrite is
        // writer-completion during shutdown; that is not an overflow drop
        // and does not deserve a warning.
        if (_messageQueue.Reader.Count >= _queueCapacity)
        {
            _statsService.IncrementMessagesDropped();
            _metrics.IncMessagesDropped();
            var nowTicks = DateTime.UtcNow.Ticks;
            var lastTicks = Interlocked.Read(ref _lastDropWarnTicks);
            if (nowTicks - lastTicks >= TimeSpan.TicksPerSecond &&
                Interlocked.CompareExchange(ref _lastDropWarnTicks, nowTicks, lastTicks) == lastTicks)
            {
                _logger.LogWarning("Message queue full, dropping oldest (incoming topic {Topic})", stamped.Topic);
            }
        }
        _messageQueue.Writer.TryWrite(stamped);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Ingest pump must tolerate any per-message error; poisoning one message cannot stall the queue.")]
    private async Task ProcessMessageAsync(MqttMessage message)
    {
        try
        {
            _statsService.IncrementMessagesProcessed();
            _statsService.IncrementMessagesByProtocol(TopicToProtocol(message.Topic));

            if (_settings.Features.MessagePersistence)
            {
                // Enqueue into the batched writer; a stalled DB drops
                // rows from its own channel, never blocks the ingest pump.
                // Don't bump a "writes" counter here — enqueue can still
                // be lost to channel overflow or a failed flush; the
                // batched writer counts post-commit.
                await _messageHistory.AppendAsync(
                    message.Topic, message.Payload, message.QoS, message.Retained);
            }

            if (message.Topic.StartsWith(Zigbee2MqttTopics.BaseTopic, StringComparison.Ordinal))
            {
                _zigbee2MqttService.ProcessMessage(message);

                if (message.IsBridgeMessage)
                {
                    await ProcessBridgeMessageAsync(message);
                    return;
                }
            }

            if (!IsSystemTopic(message.Topic) && _settings.Features.DeviceStateTracking)
            {
                await ProcessDeviceMessageAsync(message);
            }

            await _rulesEngine.ProcessMessageAsync(message);
            if (message.TraceUuid is { } trace)
            {
                _timestampRecorder.Record(trace, TimestampStages.RuleEvalDone, UnixNanoseconds());
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing message on topic {Topic}", message.Topic);
        }
    }

    private bool IsSystemTopic(string topic) => _settings.Mqtt.Topics.IsSystemTopic(topic);

    internal static Protocol TopicToProtocol(string topic)
    {
        var slash = topic.IndexOf('/', StringComparison.Ordinal);
        var prefix = slash < 0 ? topic.AsSpan() : topic.AsSpan(0, slash);
        return ProtocolExtensions.FromTopicPrefix(prefix);
    }

    // Wall-clock Unix ns (100 ns res). Matches load_gen's time.time_ns().
    private static long UnixNanoseconds()
    {
        return (DateTime.UtcNow.Ticks - DateTime.UnixEpoch.Ticks) * 100L;
    }

    private async Task ProcessBridgeMessageAsync(MqttMessage message)
    {
        var parts = message.Topic.Split('/');
        if (parts.Length < 3) return;

        switch (parts[2])
        {
            case "devices":
                await ProcessZigbee2MqttDevicesAsync(message.Payload);
                break;
            case "state":
                ProcessZigbee2MqttState(message.Payload);
                break;
            case "info":
                ProcessZigbee2MqttInfo(message.Payload);
                break;
            case "event":
                _logger.LogDebug("Zigbee2MQTT event: {Payload}", message.Payload);
                break;
        }
    }

    private async Task ProcessZigbee2MqttDevicesAsync(string payload)
    {
        try
        {
            var z2mDevices = JsonSerializer.Deserialize<List<Zigbee2MqttDevice>>(payload, JsonOptions);
            if (z2mDevices is null) return;

            _logger.LogInformation("Discovered {Count} devices from Zigbee2MQTT", z2mDevices.Count);

            // Two lookup maps: by friendly-name and by ieee_address.
            // Rename detection needs both — pre-rename rows from state
            // messages often lack an ieee stamp.
            var existing = (await _deviceService.GetDevicesByProtocolAsync(Protocol.Zigbee)).ToList();
            var existingById = new Dictionary<string, Device>(StringComparer.OrdinalIgnoreCase);
            var existingByIeee = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var d in existing)
            {
                existingById[d.Id] = d;
                if (d.Capabilities.TryGetValue("ieee_address", out var raw) &&
                    raw is string ieee && !string.IsNullOrEmpty(ieee))
                {
                    existingByIeee[ieee] = d.Id;
                }
            }

            var liveFriendlyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var z2mDevice in z2mDevices)
            {
                if (z2mDevice.Type == "Coordinator" || z2mDevice.Disabled) continue;
                if (string.IsNullOrEmpty(z2mDevice.FriendlyName)) continue;

                liveFriendlyNames.Add(z2mDevice.FriendlyName);

                // Same ieee, different friendly name => migrate the row.
                if (!existingById.ContainsKey(z2mDevice.FriendlyName) &&
                    !string.IsNullOrEmpty(z2mDevice.IeeeAddress) &&
                    existingByIeee.TryGetValue(z2mDevice.IeeeAddress, out var oldId) &&
                    !string.Equals(oldId, z2mDevice.FriendlyName, StringComparison.Ordinal))
                {
                    var renamed = await _deviceService.RenameDeviceAsync(oldId, z2mDevice.FriendlyName);
                    if (renamed)
                    {
                        _logger.LogInformation(
                            "Detected upstream Zigbee rename: {OldId} -> {NewId} (ieee {Ieee})",
                            oldId, z2mDevice.FriendlyName, z2mDevice.IeeeAddress);
                    }
                    else
                    {
                        _logger.LogWarning(
                            "Could not migrate renamed Zigbee device {OldId} -> {NewId}; target id already taken",
                            oldId, z2mDevice.FriendlyName);
                        continue;
                    }
                }

                await UpsertZigbeeDeviceAsync(z2mDevice);
            }

            await PruneUnpairedZigbeeDevicesAsync(liveFriendlyNames, existing);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse Zigbee2MQTT devices payload");
        }
    }

    private async Task PruneUnpairedZigbeeDevicesAsync(
        HashSet<string> liveFriendlyNames,
        IReadOnlyCollection<Device> allZigbeeDevices)
    {
        // Bridge/devices is authoritative; rows absent from the live
        // set are pruned. Empty snapshot => transient blip, skip the
        // prune rather than wipe the inventory.
        if (liveFriendlyNames.Count == 0) return;

        foreach (var device in allZigbeeDevices)
        {
            if (liveFriendlyNames.Contains(device.Id)) continue;

            var removed = await _deviceService.RemoveDeviceAsync(device.Id);
            if (removed)
            {
                _logger.LogInformation(
                    "Removed stale Zigbee device {DeviceId} (not in bridge/devices)",
                    device.Id);
            }
        }
    }

    private async Task UpsertZigbeeDeviceAsync(Zigbee2MqttDevice z2mDevice)
    {
        var device = await _deviceService.GetDeviceAsync(z2mDevice.FriendlyName) ?? new Device
        {
            Id = z2mDevice.FriendlyName,
            Protocol = Protocol.Zigbee,
            Status = DeviceStatus.Online,
            CreatedAt = DateTime.UtcNow,
        };

        device.LastSeen = DateTime.UtcNow;
        device.Name = z2mDevice.FriendlyName;
        device.Manufacturer = z2mDevice.Definition?.Vendor;
        device.Model = z2mDevice.Definition?.Model ?? z2mDevice.ModelId;
        device.FirmwareVersion = z2mDevice.DateCode;

        device.Capabilities["ieee_address"] = z2mDevice.IeeeAddress;
        device.Capabilities["type"] = z2mDevice.Type;
        device.Capabilities["power_source"] = z2mDevice.PowerSource ?? "Unknown";
        device.Capabilities["supported"] = z2mDevice.Supported;
        if (z2mDevice.Definition?.Description is not null)
        {
            device.Capabilities["description"] = z2mDevice.Definition.Description;
        }

        await _deviceService.AddOrUpdateDeviceAsync(device);
        _logger.LogDebug("Registered Zigbee device: {Name} ({Model})",
            device.Name, device.Model ?? "Unknown");
    }

    private void ProcessZigbee2MqttState(string payload)
    {
        try
        {
            var state = JsonSerializer.Deserialize<Zigbee2MqttBridgeState>(payload, JsonOptions);
            if (state is not null)
            {
                _logger.LogInformation("Zigbee2MQTT bridge state: {State}", state.State);
            }
        }
        catch (JsonException)
        {
            // Legacy bridge clients publish a bare "online"/"offline" string.
            _logger.LogInformation("Zigbee2MQTT bridge state: {State}", payload);
        }
    }

    private void ProcessZigbee2MqttInfo(string payload)
    {
        try
        {
            var info = JsonSerializer.Deserialize<Zigbee2MqttBridgeInfo>(payload, JsonOptions);
            if (info is not null)
            {
                _logger.LogInformation("Zigbee2MQTT v{Version} - Channel {Channel}, PAN ID {PanId}",
                    info.Version, info.Network?.Channel, info.Network?.PanId);
            }
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "Failed to parse Zigbee2MQTT bridge info");
        }
    }

    private async Task ProcessDeviceMessageAsync(MqttMessage message)
    {
        if (message.DeviceId is null) return;

        Dictionary<string, object>? state;
        try
        {
            state = JsonSerializer.Deserialize<Dictionary<string, object>>(message.Payload);
        }
        catch (JsonException)
        {
            state = null;
        }

        if (_settings.Storage.FastDeviceUpserts && _deviceStateWriter is not null)
        {
            // Fast path: fire-and-forget enqueue into the batched writer.
            // Completion is synchronous (channel TryWrite) — the reader
            // never awaits a Postgres round-trip, so the ingest pump
            // holds line rate under 1k msg/s × 1k devices. Metrics tick
            // happens inside the writer per row actually flushed.
            await _deviceStateWriter.EnqueueAsync(
                message.DeviceId,
                message.DeviceId,
                message.SourceProtocol,
                state);
            return;
        }

        if (_settings.Storage.FastDeviceUpserts)
        {
            // Fast path without the batched writer (Noop/InMemory modes):
            // single-statement UPSERT with server-side JSONB merge.
            await _deviceService.UpsertDeviceStateAsync(
                message.DeviceId,
                message.DeviceId,
                message.SourceProtocol,
                state ?? new Dictionary<string, object>(0));
            _metrics.IncDeviceStateWrites();
            return;
        }

        // Legacy read-modify-write path, preserved for A/B measurement.
        var device = await _deviceService.GetDeviceAsync(message.DeviceId) ?? new Device
        {
            Id = message.DeviceId,
            Name = message.DeviceId,
            Protocol = message.SourceProtocol,
            CreatedAt = DateTime.UtcNow,
        };

        device.LastSeen = DateTime.UtcNow;
        device.Status = DeviceStatus.Online;

        if (state is not null)
        {
            foreach (var kvp in state) device.State[kvp.Key] = kvp.Value;
        }

        await _deviceService.AddOrUpdateDeviceAsync(device);
        _metrics.IncDeviceStateWrites();
    }

    private void OnConnectionStateChanged(object? sender, bool connected)
    {
        if (!connected)
        {
            _logger.LogWarning("MQTT connection lost");
            return;
        }

        _logger.LogInformation("MQTT connection established");
        // CleanSession=true wipes broker-side filters on disconnect, so we
        // must reassert the wildcard subscription on every reconnect.
        _ = Task.Run(ResubscribeAsync);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Fire-and-forget resubscribe must log-and-return; a thrown exception on a Task.Run fiber would crash the process.")]
    private async Task ResubscribeAsync()
    {
        // Safety net: MqttService.ReplaySubscriptionsAsync already replays
        // filters on reconnect, but this covers the short-circuited path.
        try
        {
            await SubscribeToTopicsAsync(CancellationToken.None);
            _logger.LogInformation("Resubscribed protocol subtrees after reconnect");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to resubscribe after reconnect");
        }
    }
}
