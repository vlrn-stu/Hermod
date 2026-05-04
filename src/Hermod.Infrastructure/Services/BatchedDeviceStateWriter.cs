using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Threading.Channels;
using Dapper;
using Hermod.Core.Configuration;
using Hermod.Core.Models;
using Hermod.Core.Telemetry;
using Hermod.Infrastructure.Database;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace Hermod.Infrastructure.Services;

/// <summary>
/// Batched writer for <c>devices.state</c> upserts. Callers on the ingest
/// hot path enqueue updates via <see cref="EnqueueAsync"/>; a single
/// background loop drains the bounded channel, dedupes by device id
/// (keeping the last-enqueued state per id per flush), and emits one
/// UNNEST-driven <c>INSERT … ON CONFLICT DO UPDATE</c> per batch.
/// <para/>
/// Mirrors the pattern in <c>PostgresMessageHistoryRepository</c>: the
/// reader must never await a DB round-trip, so DropOldest overflow lets
/// a stalled Postgres connection drop old device updates instead of
/// stalling ingest. The most-recent update always wins because the
/// channel is drained in FIFO order and the final <c>GroupBy</c> keeps
/// the last row per id.
/// </summary>
public sealed class BatchedDeviceStateWriter : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly PostgresConnectionFactory _connectionFactory;
    private readonly HermodMetrics _metrics;
    private readonly ILogger<BatchedDeviceStateWriter> _logger;
    private readonly Channel<DeviceStateUpdate> _queue;
    private readonly int _batchSize;
    private readonly int _queueCapacity;
    private readonly TimeSpan _flushInterval;

    /// <summary>
    /// Creates the batched writer. Tuning comes from <see cref="StorageSettings"/>
    /// (<c>DeviceWriteBatchSize</c>, <c>DeviceWriteFlushIntervalMs</c>,
    /// <c>DeviceWriteQueueCapacity</c>).
    /// </summary>
    public BatchedDeviceStateWriter(
        PostgresConnectionFactory connectionFactory,
        HermodMetrics metrics,
        IOptions<HermodSettings> settings,
        ILogger<BatchedDeviceStateWriter> logger)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);
        _connectionFactory = connectionFactory;
        _metrics = metrics;
        _logger = logger;

        var storage = settings.Value.Storage;
        _batchSize = Math.Max(1, storage.DeviceWriteBatchSize);
        _flushInterval = TimeSpan.FromMilliseconds(Math.Max(5, storage.DeviceWriteFlushIntervalMs));
        _queueCapacity = Math.Max(_batchSize * 2, storage.DeviceWriteQueueCapacity);

        _queue = Channel.CreateBounded<DeviceStateUpdate>(new BoundedChannelOptions(_queueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    /// <summary>
    /// Non-blocking enqueue of a device-state update. Returns a completed
    /// task — the actual DB write happens on the background flush loop.
    /// Matches <see cref="Hermod.Core.Interfaces.IDeviceService.UpsertDeviceStateAsync"/>
    /// so callers can swap the await for this enqueue without other changes.
    /// </summary>
    public ValueTask EnqueueAsync(
        string deviceId,
        string name,
        Protocol protocol,
        Dictionary<string, object>? state)
    {
        // DropOldest guarantees TryWrite succeeds except on shutdown.
        // Count the eviction BEFORE writing so a non-zero dropped counter
        // actually surfaces channel saturation (was silent before).
        if (_queue.Reader.Count >= _queueCapacity)
        {
            _metrics.IncDeviceStateWriteDropped();
        }
        _queue.Writer.TryWrite(new DeviceStateUpdate(
            deviceId, name, protocol, state ?? new Dictionary<string, object>(0)));
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Per-batch flush failures must not kill the background loop; next batch retries with fresh rows.")]
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var buffer = new List<DeviceStateUpdate>(_batchSize);
        var reader = _queue.Reader;

        while (!stoppingToken.IsCancellationRequested)
        {
            buffer.Clear();

            // Bound the "wait for more rows" time so slow trickles still
            // flush within flushInterval. Mirrors the window-CTS pattern
            // from PostgresMessageHistoryRepository.
            using var windowCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            var windowArmed = false;

            while (buffer.Count < _batchSize && !windowCts.IsCancellationRequested)
            {
                if (reader.TryRead(out var row))
                {
                    buffer.Add(row);
                    if (!windowArmed)
                    {
                        windowCts.CancelAfter(_flushInterval);
                        windowArmed = true;
                    }
                    continue;
                }

                try
                {
                    if (!await reader.WaitToReadAsync(windowCts.Token)) return;
                }
                catch (OperationCanceledException)
                {
                    // Either shutdown or window expired — fall through to flush.
                    break;
                }
            }

            if (buffer.Count == 0) continue;

            try
            {
                await FlushAsync(buffer, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _metrics.IncDeviceStateFlushFailed();
                _logger.LogWarning(ex, "device-state batch flush failed ({Count} updates dropped)", buffer.Count);
            }
        }

        // Final drain. Do NOT Clear() — if the outer loop exited mid-flush,
        // buffer already holds updates we'd lose.
        while (reader.TryRead(out var row))
        {
            buffer.Add(row);
            if (buffer.Count >= _batchSize)
            {
                try { await FlushAsync(buffer, CancellationToken.None); }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "device-state final-drain flush failed");
                }
                buffer.Clear();
            }
        }
        if (buffer.Count > 0)
        {
            try { await FlushAsync(buffer, CancellationToken.None); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "device-state final-drain flush failed");
            }
        }
    }

    private async Task FlushAsync(List<DeviceStateUpdate> updates, CancellationToken cancellationToken)
    {
        // Dedupe by device id: if 20 updates for one device landed in the
        // batch, keep only the last one. Correctness note: the batch is
        // drained FIFO, so the last instance per id is the most recent
        // state the caller enqueued. Output list is therefore <= unique ids.
        // Grouping inline (no LINQ allocs per message) avoids churn on the
        // hot path.
        var latestById = new Dictionary<string, DeviceStateUpdate>(updates.Count);
        foreach (var u in updates)
        {
            latestById[u.DeviceId] = u;
        }

        var rows = latestById.Values;
        var count = rows.Count;
        if (count == 0) return;

        var ids = new string[count];
        var names = new string[count];
        var protocols = new int[count];
        var states = new string[count];
        var i = 0;
        foreach (var r in rows)
        {
            ids[i] = r.DeviceId;
            names[i] = r.Name;
            protocols[i] = (int)r.Protocol;
            states[i] = JsonSerializer.Serialize(r.State, JsonOptions);
            i++;
        }

        // Direct Npgsql rather than Dapper: Dapper auto-expands
        // IEnumerable parameters into IN-lists, but UNNEST needs the
        // arrays to land at the server as real PG arrays. Typed
        // NpgsqlParameters with NpgsqlDbType.Array | <elem> give us that.
        const string sql = """
            INSERT INTO devices (
                id, name, protocol, status, capabilities, state,
                last_seen, created_at, updated_at)
            SELECT
                u.id, u.name, u.protocol, @Status, '{}'::jsonb, u.state::jsonb,
                @Now, @Now, @Now
            FROM UNNEST(@Ids, @Names, @Protocols, @States)
                AS u(id, name, protocol, state)
            ON CONFLICT (id) DO UPDATE SET
                state      = COALESCE(devices.state, '{}'::jsonb) || EXCLUDED.state,
                last_seen  = EXCLUDED.last_seen,
                status     = EXCLUDED.status,
                updated_at = EXCLUDED.updated_at;
            """;

        await using var conn = _connectionFactory.CreateConnection();
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.Add(new NpgsqlParameter("Ids", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = ids });
        cmd.Parameters.Add(new NpgsqlParameter("Names", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = names });
        cmd.Parameters.Add(new NpgsqlParameter("Protocols", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = protocols });
        cmd.Parameters.Add(new NpgsqlParameter("States", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = states });
        cmd.Parameters.Add(new NpgsqlParameter("Status", NpgsqlDbType.Integer) { Value = (int)DeviceStatus.Online });
        cmd.Parameters.Add(new NpgsqlParameter("Now", NpgsqlDbType.TimestampTz) { Value = DateTime.UtcNow });
        await cmd.ExecuteNonQueryAsync(cancellationToken);

        // One metrics tick per DB row actually written — the post-dedupe
        // count, not the pre-dedupe enqueue count, so the counter tracks
        // real Postgres write pressure rather than ingest volume.
        for (var k = 0; k < count; k++)
        {
            _metrics.IncDeviceStateWrites();
        }
    }

    private readonly record struct DeviceStateUpdate(
        string DeviceId,
        string Name,
        Protocol Protocol,
        Dictionary<string, object> State);
}
