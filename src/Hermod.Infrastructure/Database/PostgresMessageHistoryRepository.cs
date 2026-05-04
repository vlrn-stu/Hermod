using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Threading.Channels;
using Dapper;
using Hermod.Core.Configuration;
using Hermod.Core.Interfaces;
using Hermod.Core.Telemetry;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hermod.Infrastructure.Database;

/// <summary>
/// Batched writer for <c>message_history</c>. Callers enqueue onto a
/// bounded channel via <see cref="AppendAsync"/>; a background loop drains
/// up to <see cref="StorageSettings.WriteBatchSize"/> rows per flush and
/// emits a single multi-row INSERT. DropOldest overflow keeps memory
/// bounded when Postgres is slow — the thesis cares about measured cost,
/// not full audit integrity, and message ingest must never stall behind
/// the audit path.
/// </summary>
internal sealed class PostgresMessageHistoryRepository
    : BackgroundService, IMessageHistoryRepository
{
    private readonly PostgresConnectionFactory _connectionFactory;
    private readonly HermodMetrics _metrics;
    private readonly ILogger<PostgresMessageHistoryRepository> _logger;
    private readonly Channel<Row> _queue;
    private readonly int _batchSize;
    private readonly int _queueCapacity;
    private readonly TimeSpan _flushInterval;

    public PostgresMessageHistoryRepository(
        PostgresConnectionFactory connectionFactory,
        HermodMetrics metrics,
        IOptions<HermodSettings> settings,
        ILogger<PostgresMessageHistoryRepository> logger)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);
        _connectionFactory = connectionFactory;
        _metrics = metrics;
        _logger = logger;

        var storage = settings.Value.Storage;
        _batchSize = Math.Max(1, storage.WriteBatchSize);
        _flushInterval = TimeSpan.FromMilliseconds(Math.Max(10, storage.WriteFlushIntervalMs));
        _queueCapacity = Math.Max(_batchSize * 2, storage.WriteQueueCapacity);

        _queue = Channel.CreateBounded<Row>(new BoundedChannelOptions(_queueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    public Task AppendAsync(
        string topic,
        string payload,
        int qos,
        bool retained,
        CancellationToken cancellationToken = default)
    {
        // Count the eviction BEFORE the DropOldest TryWrite, so saturation
        // is no longer silent. Flusher emits the INSERT.
        if (_queue.Reader.Count >= _queueCapacity)
        {
            _metrics.IncMessagePersistenceDropped();
        }
        _queue.Writer.TryWrite(new Row(topic, payload, (short)qos, retained));
        return Task.CompletedTask;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Per-batch flush failures must not kill the background loop; next batch retries with fresh rows.")]
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var buffer = new List<Row>(_batchSize);
        var reader = _queue.Reader;

        while (!stoppingToken.IsCancellationRequested)
        {
            buffer.Clear();

            // Window CTS bounds the "wait for more rows" time so slow
            // trickles still flush within flushInterval.
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
                    // Either shutdown or window expired — fall through.
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
                // Drop-this-batch-only; next batch retries with fresh rows.
                // Counter makes PG-outage-induced loss visible — previously
                // the batch was silently discarded with only a log line.
                _metrics.IncMessagePersistenceFlushFailed();
                _logger.LogWarning(ex, "message_history batch flush failed ({Count} rows dropped)", buffer.Count);
            }
        }

        // Final drain. Do NOT Clear() — if the outer loop exited
        // mid-flush, buffer already holds rows we'd lose.
        while (reader.TryRead(out var row))
        {
            buffer.Add(row);
            if (buffer.Count >= _batchSize)
            {
                try { await FlushAsync(buffer, CancellationToken.None); }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "message_history final-drain flush failed");
                }
                buffer.Clear();
            }
        }
        if (buffer.Count > 0)
        {
            try { await FlushAsync(buffer, CancellationToken.None); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "message_history final-drain flush failed");
            }
        }
    }

    private async Task FlushAsync(List<Row> rows, CancellationToken cancellationToken)
    {
        var sql = BuildBatchInsert(rows.Count);
        var parameters = new DynamicParameters();
        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            parameters.Add($"t{i}", r.Topic);
            parameters.Add($"p{i}", r.Payload);
            parameters.Add($"q{i}", r.Qos);
            parameters.Add($"r{i}", r.Retained);
        }

        await using var conn = _connectionFactory.CreateConnection();
        await conn.OpenAsync(cancellationToken);
        await conn.ExecuteAsync(new CommandDefinition(sql, parameters, cancellationToken: cancellationToken));
        // Counter fires ONLY after the INSERT commits. Previously this was
        // incremented per enqueue in MessageProcessor, which meant a dropped
        // or failed batch still bumped "writes" — the counter lied.
        // Bulk add: one atomic op for the whole batch (saves N-1 Interlocked
        // ops at batch=256/512 scale).
        _metrics.AddMessagePersistenceWrites(rows.Count);
    }

    private static string BuildBatchInsert(int rowCount)
    {
        var sb = new StringBuilder("INSERT INTO message_history (topic, payload, qos, retained) VALUES ");
        for (var i = 0; i < rowCount; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(CultureInfo.InvariantCulture, $"(@t{i}, @p{i}, @q{i}, @r{i})");
        }
        sb.Append(';');
        return sb.ToString();
    }

    private readonly record struct Row(string Topic, string Payload, short Qos, bool Retained);
}
