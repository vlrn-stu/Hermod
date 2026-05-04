using System.Diagnostics.CodeAnalysis;
using Hermod.Core.Configuration;
using Hermod.Core.Interfaces;
using Hermod.Core.Models;
using Hermod.Core.Telemetry;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hermod.Infrastructure.Mqtt;

/// <summary>
/// Shards ingest work across N underlying <see cref="MqttService"/> instances
/// so the single-threaded MQTTnet receive callback stops being the 7 k msg/s
/// laptop ceiling. Enabled automatically when
/// <c>Hermod:Mqtt:ParallelClients</c> &gt; 1. Each inner client connects with
/// a <c>-&lt;index&gt;</c>-suffixed <c>ClientId</c>; subscribes are routed
/// to one client by topic-prefix hash so every inbound message lands on
/// exactly one receive thread. Publishes all go through client 0 for
/// deterministic order; merging events from the fan-out happens here.
/// </summary>
public sealed class ParallelMqttService : IMqttService, IAsyncDisposable
{
    private readonly MqttService[] _clients;
    private readonly ILogger<ParallelMqttService> _logger;

    // Topic-filter → owner shard, so Unsubscribe routes correctly.
    private readonly Dictionary<string, int> _ownership = new(StringComparer.Ordinal);
    private readonly object _ownershipLock = new();

    /// <inheritdoc/>
    public bool IsConnected
    {
        get
        {
            // All-or-nothing: a single reconnecting shard signals offline
            // so MessageProcessor can react, even if peers keep flowing.
            foreach (var c in _clients)
            {
                if (!c.IsConnected) return false;
            }
            return true;
        }
    }

    /// <inheritdoc/>
    public event EventHandler<MqttMessage>? MessageReceived;

    /// <inheritdoc/>
    public event EventHandler<bool>? ConnectionStateChanged;

    /// <summary>Creates <c>Mqtt.ParallelClients</c> inner clients; <c>n==1</c> is a pass-through.</summary>
    public ParallelMqttService(IOptions<HermodSettings> settings, HermodMetrics metrics,
                                 ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        _logger = loggerFactory.CreateLogger<ParallelMqttService>();
        var n = Math.Max(1, settings.Value.Mqtt.ParallelClients);
        _clients = new MqttService[n];

        for (var i = 0; i < n; i++)
        {
            // Suffixed ClientId: MQTT 3.1.1 kicks duplicates by ClientId,
            // so uniqueness across shards is load-bearing.
            var shardedSettings = CloneWithShardedClientId(settings.Value, i, n);
            var logger = loggerFactory.CreateLogger<MqttService>();
            var inner = new MqttService(new ShardedOptions(shardedSettings), metrics, logger);

            // Fan-in: handlers fire on the inner client's thread, so
            // subscribers see N-way parallelism on the hot path.
            inner.MessageReceived += (_, msg) => MessageReceived?.Invoke(this, msg);
            inner.ConnectionStateChanged += (_, state) => ConnectionStateChanged?.Invoke(this, state);

            _clients[i] = inner;
        }

        if (n > 1)
        {
            _logger.LogInformation("ParallelMqttService initialised with {N} client shards", n);
        }
    }

    /// <inheritdoc/>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Unwind must absorb any per-shard disconnect failure so the original connect exception still propagates cleanly.")]
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        // Sequential: fail-fast on a broken shard beats half-up status.
        // On failure, unwind any already-connected shards so the caller is
        // not left with stranded clients.
        var connected = 0;
        try
        {
            foreach (var c in _clients)
            {
                await c.ConnectAsync(cancellationToken);
                connected++;
            }
        }
        catch
        {
            for (var i = 0; i < connected; i++)
            {
                try { await _clients[i].DisconnectAsync(CancellationToken.None); }
                catch (Exception ex) { _logger.LogWarning(ex, "Unwind disconnect failed for shard {Index}", i); }
            }
            throw;
        }
    }

    /// <inheritdoc/>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Per-shard disconnect failures are aggregated into an AggregateException so one shard cannot block the others from closing.")]
    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        // Best-effort across every shard: one failing client must not
        // block the others from disconnecting. Aggregate exceptions so
        // the caller still sees failures.
        List<Exception>? errors = null;
        foreach (var c in _clients)
        {
            try
            {
                await c.DisconnectAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                (errors ??= new List<Exception>()).Add(ex);
            }
        }
        if (errors is not null) throw new AggregateException("One or more shards failed to disconnect", errors);
    }

    /// <inheritdoc/>
    public Task SubscribeAsync(string topic, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(topic);
        var idx = OwnerIndex(topic);
        lock (_ownershipLock) _ownership[topic] = idx;
        return _clients[idx].SubscribeAsync(topic, cancellationToken);
    }

    /// <inheritdoc/>
    public Task UnsubscribeAsync(string topic, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(topic);
        int idx;
        lock (_ownershipLock)
        {
            if (!_ownership.TryGetValue(topic, out idx))
            {
                idx = OwnerIndex(topic);
            }
            _ownership.Remove(topic);
        }
        return _clients[idx].UnsubscribeAsync(topic, cancellationToken);
    }

    /// <inheritdoc/>
    public Task PublishAsync(string topic, string payload, bool retain = false, int qos = 0,
                              CancellationToken cancellationToken = default)
        // Always publish on client 0: preserves publish order and keeps
        // the reconnect outbox (which lives inside the client) the same
        // one consistently across the app. Publishes are a minor fraction
        // of the hot path; sharding publishes too would buy little and
        // cost ordering guarantees.
        => _clients[0].PublishAsync(topic, payload, retain, qos, cancellationToken);

    /// <inheritdoc/>
    public IReadOnlyList<MqttMessage> GetMessageHistory()
    {
        var all = new List<MqttMessage>();
        foreach (var c in _clients)
        {
            all.AddRange(c.GetMessageHistory());
        }
        all.Sort((a, b) => a.ReceivedAt.CompareTo(b.ReceivedAt));
        return all;
    }

    /// <summary>Routes a topic filter to one of <see cref="_clients"/>.</summary>
    private int OwnerIndex(string topic)
    {
        if (_clients.Length == 1) return 0;

        // First-segment routing keeps related messages on one shard
        // for cache locality in the rule engine's TopicTrie.
        var slash = topic.IndexOf('/', StringComparison.Ordinal);
        var key = slash > 0 ? topic[..slash] : topic;
        return (int)((uint)StringHash(key) % (uint)_clients.Length);
    }

    private static int StringHash(ReadOnlySpan<char> s)
    {
        // FNV-1a 32-bit; stable across runs, unlike String.GetHashCode.
        var h = unchecked((int)2166136261u);
        for (var i = 0; i < s.Length; i++)
        {
            h = unchecked((h ^ s[i]) * 16777619);
        }
        return h;
    }

    private static HermodSettings CloneWithShardedClientId(HermodSettings original, int index, int total)
    {
        // Every section copied by reference so an inner client reading
        // any future-added field sees the real configured value, not a
        // default-initialised stand-in.
        var clone = new HermodSettings
        {
            Mqtt = new MqttSettings
            {
                Host = original.Mqtt.Host,
                Port = original.Mqtt.Port,
                ClientId = total == 1 ? original.Mqtt.ClientId : $"{original.Mqtt.ClientId}-{index}",
                KeepAliveSeconds = original.Mqtt.KeepAliveSeconds,
                CleanSession = original.Mqtt.CleanSession,
                Username = original.Mqtt.Username,
                Password = original.Mqtt.Password,
                Topics = original.Mqtt.Topics,
                Tls = original.Mqtt.Tls,
                ReconnectBufferSize = original.Mqtt.ReconnectBufferSize,
                ParallelClients = 1,  // inner client is always singular
            },
            Database = original.Database,
            ProtocolTranslators = original.ProtocolTranslators,
            Metrics = original.Metrics,
            Auth = original.Auth,
            Security = original.Security,
            Dashboard = original.Dashboard,
            Zigbee = original.Zigbee,
            Features = original.Features,
            Storage = original.Storage,
            Engine = original.Engine,
            Seed = original.Seed,
            Dev = original.Dev,
            Telemetry = original.Telemetry,
        };
        return clone;
    }

    /// <summary>Thin <see cref="IOptions{T}"/> adapter for the sharded clone.</summary>
    private sealed class ShardedOptions : IOptions<HermodSettings>
    {
        public ShardedOptions(HermodSettings value) { Value = value; }
        public HermodSettings Value { get; }
    }

    /// <inheritdoc/>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Dispose must swallow per-shard failures to guarantee the rest get released; a thrown Dispose leaks native handles.")]
    public async ValueTask DisposeAsync()
    {
        // Best-effort: one failing shard must not leave the rest leaked.
        foreach (var c in _clients)
        {
            try { await c.DisposeAsync(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Shard dispose failed"); }
        }
    }
}
