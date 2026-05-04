using Dapper;
using Hermod.Core.Configuration;
using Hermod.Core.Telemetry;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hermod.Infrastructure.Database;

/// <summary>
/// Background sweeper that ages out rows from <c>message_history</c>. Runs
/// on a fixed cadence and deletes everything older than
/// <see cref="StorageSettings.MessageHistoryRetentionDays"/> in fixed-size
/// batches so a multi-million-row purge cannot take a long write lock or
/// blow up the WAL. Loops until the cutoff window is clean, then sleeps.
/// </summary>
internal sealed class PostgresMessageHistoryRetentionWorker : BackgroundService
{
    private const string DeleteSql = @"
        DELETE FROM message_history
        WHERE id IN (
            SELECT id FROM message_history
            WHERE received_at < NOW() - make_interval(days => @days)
            ORDER BY id
            LIMIT @batch
            FOR UPDATE SKIP LOCKED
        );";

    private readonly PostgresConnectionFactory _connectionFactory;
    private readonly HermodMetrics _metrics;
    private readonly ILogger<PostgresMessageHistoryRetentionWorker> _logger;
    private readonly int _retentionDays;
    private readonly int _batchSize;
    private readonly TimeSpan _sweepInterval;

    public PostgresMessageHistoryRetentionWorker(
        PostgresConnectionFactory connectionFactory,
        HermodMetrics metrics,
        IOptions<HermodSettings> settings,
        ILogger<PostgresMessageHistoryRetentionWorker> logger)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);

        _connectionFactory = connectionFactory;
        _metrics = metrics;
        _logger = logger;

        var s = settings.Value.Storage;
        _retentionDays = Math.Max(0, s.MessageHistoryRetentionDays);
        _batchSize = Math.Max(1, s.MessageHistoryRetentionBatchSize);
        _sweepInterval = TimeSpan.FromMinutes(Math.Max(1, s.MessageHistoryRetentionSweepMinutes));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_retentionDays == 0)
        {
            _logger.LogInformation("message_history retention disabled (RetentionDays=0)");
            return;
        }

        _logger.LogInformation(
            "message_history retention sweeper started: days={Days} batch={Batch} interval={Interval}",
            _retentionDays, _batchSize, _sweepInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var deleted = await SweepUntilCleanAsync(stoppingToken);
                if (deleted > 0)
                {
                    _logger.LogInformation(
                        "message_history retention sweep deleted {Deleted} rows older than {Days}d",
                        deleted, _retentionDays);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                // A transient pg outage must not kill the worker; the next
                // tick retries. Counter surfaces the failure to ops dashboards.
                _metrics.IncMessageHistoryRetentionSweepFailed();
                _logger.LogWarning(ex, "message_history retention sweep failed; retrying next interval");
            }

            try
            {
                await Task.Delay(_sweepInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task<long> SweepUntilCleanAsync(CancellationToken cancellationToken)
    {
        long total = 0;
        await using var conn = _connectionFactory.CreateConnection();
        await conn.OpenAsync(cancellationToken);

        // Loop: delete a batch, count it, repeat until a batch returns 0
        // (i.e. nothing left older than the cutoff). Bounded loop guards
        // against pathological cases by capping at 200 batches per sweep
        // = 100k rows per cycle — anything bigger waits for the next tick.
        for (var i = 0; i < 200; i++)
        {
            var rows = await conn.ExecuteAsync(new CommandDefinition(
                DeleteSql,
                new { days = _retentionDays, batch = _batchSize },
                cancellationToken: cancellationToken));
            if (rows == 0) break;
            total += rows;
            _metrics.AddMessageHistoryRetentionDeletes(rows);
        }
        return total;
    }
}
