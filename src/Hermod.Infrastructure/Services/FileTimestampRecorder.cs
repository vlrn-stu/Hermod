using System.Globalization;
using System.Text;
using System.Threading.Channels;
using Hermod.Core.Telemetry;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Hermod.Infrastructure.Services;

/// <summary>
/// Buffered CSV writer for per-message timestamps. Producers push
/// <c>(uuid, stage, ts_ns)</c> into a bounded channel; a single drain
/// task batches rows and flushes them to disk so the hot path takes at
/// most one lock-free channel write per observation. Drops silently on
/// a full buffer — this is observability, not an SLA.
/// </summary>
public sealed class FileTimestampRecorder : ITimestampRecorder, IHostedService, IAsyncDisposable
{
    private readonly string _path;
    private readonly Channel<Entry> _channel;
    private readonly ILogger<FileTimestampRecorder> _logger;
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _drainTask;
    private long _droppedCount;
    private int _disposed;

    private readonly record struct Entry(string Uuid, string Stage, long TsNs);

    /// <summary>
    /// Creates a recorder that appends rows to <paramref name="path"/>.
    /// Writes a header row the first time the file is created; appends
    /// without a header when the file already exists so repeats share
    /// one timestamps.csv.
    /// </summary>
    public FileTimestampRecorder(string path, int bufferCapacity, ILogger<FileTimestampRecorder> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(logger);
        if (bufferCapacity < 1)
        {
            bufferCapacity = 1;
        }
        _path = path;
        _logger = logger;
        _channel = Channel.CreateBounded<Entry>(
            new BoundedChannelOptions(bufferCapacity)
            {
                FullMode = BoundedChannelFullMode.DropWrite,
                SingleReader = true,
                SingleWriter = false,
            });
    }

    /// <inheritdoc/>
    public void Record(string uuid, string stage, long timestampNs)
    {
        if (string.IsNullOrEmpty(uuid) || string.IsNullOrEmpty(stage))
        {
            return;
        }
        if (!_channel.Writer.TryWrite(new Entry(uuid, stage, timestampNs)))
        {
            Interlocked.Increment(ref _droppedCount);
        }
    }

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        // Write header eagerly so a rapid Start/Stop still produces a
        // well-formed CSV even if the drainer never wakes.
        if (!File.Exists(_path) || new FileInfo(_path).Length == 0)
        {
            File.AppendAllText(_path, "uuid,stage,ts_ns\n");
        }
        _drainTask = Task.Run(() => DrainAsync(_shutdown.Token), CancellationToken.None);
        _logger.LogInformation("Timestamp recorder writing to {Path}", _path);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // Complete the writer first so the drain exits naturally after
        // the queue empties; cancel only on the caller's deadline.
        _channel.Writer.TryComplete();
        if (_drainTask is not null)
        {
            try
            {
                await _drainTask.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                _shutdown.Cancel();
                try { await _drainTask; } catch (OperationCanceledException) { }
            }
        }
        if (Interlocked.Read(ref _droppedCount) is var dropped && dropped > 0)
        {
            _logger.LogWarning("Timestamp recorder dropped {Dropped} rows due to buffer pressure", dropped);
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        // Idempotent: a second dispose would throw on the disposed CTS.
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _shutdown.Cancel();
        _shutdown.Dispose();
        if (_drainTask is not null)
        {
            try { await _drainTask; } catch (OperationCanceledException) { }
        }
    }

    // RFC 4180: quote when the field contains a delimiter char; fast
    // path hits an IndexOfAny miss when the uuid is well-formed.
    private static void AppendCsvField(StringBuilder buffer, string value)
    {
        if (value.AsSpan().IndexOfAny(",\"\n\r") < 0)
        {
            buffer.Append(value);
            return;
        }
        buffer.Append('"');
        foreach (var c in value)
        {
            if (c == '"') buffer.Append('"');
            buffer.Append(c);
        }
        buffer.Append('"');
    }

    private static void AppendRow(StringBuilder buffer, Entry entry)
    {
        AppendCsvField(buffer, entry.Uuid);
        buffer.Append(',');
        AppendCsvField(buffer, entry.Stage);
        buffer.Append(',');
        buffer.Append(entry.TsNs.ToString(CultureInfo.InvariantCulture));
        buffer.Append('\n');
    }

    private async Task DrainAsync(CancellationToken cancellationToken)
    {
        var buffer = new StringBuilder(capacity: 4_096);
        try
        {
            while (await _channel.Reader.WaitToReadAsync(cancellationToken))
            {
                buffer.Clear();
                while (_channel.Reader.TryRead(out var entry))
                {
                    AppendRow(buffer, entry);
                }
                await File.AppendAllTextAsync(_path, buffer.ToString(), cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown path; drain whatever is left.
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "Timestamp recorder failed to write {Path}", _path);
        }

        buffer.Clear();
        while (_channel.Reader.TryRead(out var entry))
        {
            AppendRow(buffer, entry);
        }
        if (buffer.Length > 0)
        {
            try
            {
                await File.AppendAllTextAsync(_path, buffer.ToString(), CancellationToken.None);
            }
            catch (IOException ex)
            {
                _logger.LogError(ex, "Timestamp recorder failed final flush to {Path}", _path);
            }
        }
    }
}
