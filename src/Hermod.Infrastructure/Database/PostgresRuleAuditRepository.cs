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
/// Batched writer for <c>rule_audit_log</c>. Same shape as
/// <see cref="PostgresMessageHistoryRepository"/>: fire-and-forget append
/// into a bounded channel, background loop drains and emits one multi-row
/// INSERT per window. DropOldest overflow so a slow DB never stalls rule
/// evaluation.
/// </summary>
internal sealed class PostgresRuleAuditRepository
    : BackgroundService, IRuleAuditRepository
{
    private readonly PostgresConnectionFactory _connectionFactory;
    private readonly HermodMetrics _metrics;
    private readonly ILogger<PostgresRuleAuditRepository> _logger;
    private readonly Channel<Row> _queue;
    private readonly int _batchSize;
    private readonly int _queueCapacity;
    private readonly TimeSpan _flushInterval;

    public PostgresRuleAuditRepository(
        PostgresConnectionFactory connectionFactory,
        HermodMetrics metrics,
        IOptions<HermodSettings> settings,
        ILogger<PostgresRuleAuditRepository> logger)
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
        string ruleId,
        string? topic,
        double elapsedMs,
        bool success,
        string? error,
        int actionCount,
        CancellationToken cancellationToken = default)
    {
        if (_queue.Reader.Count >= _queueCapacity)
        {
            _metrics.IncRuleAuditDropped();
        }
        _queue.Writer.TryWrite(new Row(ruleId, topic, elapsedMs, success, error, actionCount));
        return Task.CompletedTask;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Per-batch flush failures must not kill the background loop; audit rows are best-effort by design.")]
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var buffer = new List<Row>(_batchSize);
        var reader = _queue.Reader;

        while (!stoppingToken.IsCancellationRequested)
        {
            buffer.Clear();

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
                _metrics.IncRuleAuditFlushFailed();
                _logger.LogWarning(ex, "rule_audit_log batch flush failed ({Count} rows dropped)", buffer.Count);
            }
        }

        // Final drain. Do NOT clear buffer here — if the outer loop exited
        // mid-flush it may still hold rows that never made it to disk.
        while (reader.TryRead(out var row))
        {
            buffer.Add(row);
            if (buffer.Count >= _batchSize)
            {
                try { await FlushAsync(buffer, CancellationToken.None); }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "rule_audit_log final-drain flush failed");
                }
                buffer.Clear();
            }
        }
        if (buffer.Count > 0)
        {
            try { await FlushAsync(buffer, CancellationToken.None); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "rule_audit_log final-drain flush failed");
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
            parameters.Add($"r{i}", r.RuleId);
            parameters.Add($"t{i}", r.Topic);
            parameters.Add($"e{i}", r.ElapsedMs);
            parameters.Add($"s{i}", r.Success);
            parameters.Add($"x{i}", r.Error);
            parameters.Add($"a{i}", r.ActionCount);
        }

        await using var conn = _connectionFactory.CreateConnection();
        await conn.OpenAsync(cancellationToken);
        await conn.ExecuteAsync(new CommandDefinition(sql, parameters, cancellationToken: cancellationToken));
        // Counter fires per row ONLY after commit; counting at enqueue
        // would let dropped or failed batches bump "writes". Bulk add
        // saves N-1 atomic ops.
        _metrics.AddRuleAuditWrites(rows.Count);
    }

    private static string BuildBatchInsert(int rowCount)
    {
        var sb = new StringBuilder("INSERT INTO rule_audit_log (rule_id, topic, elapsed_ms, success, error, action_count) VALUES ");
        for (var i = 0; i < rowCount; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(CultureInfo.InvariantCulture, $"(@r{i}, @t{i}, @e{i}, @s{i}, @x{i}, @a{i})");
        }
        sb.Append(';');
        return sb.ToString();
    }

    private readonly record struct Row(
        string RuleId,
        string? Topic,
        double ElapsedMs,
        bool Success,
        string? Error,
        int ActionCount);
}
