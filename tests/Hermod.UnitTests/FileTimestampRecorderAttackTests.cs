using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hermod.Core.Telemetry;
using Hermod.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Hermod.UnitTests;

/// <summary>
/// Attack-surface tests for <see cref="FileTimestampRecorder"/>. The
/// recorder is observability plumbing, so the invariants under stress
/// are "never crash the producer" and "never corrupt the consumer",
/// not "never lose a row". These tests pin the drop-on-overflow
/// contract, the graceful degradation paths (Record after Stop,
/// double dispose, zero capacity), and record the CSV-escaping gap
/// so a future fix can turn the documented-behaviour test into a
/// real safety test without re-discovering the finding.
/// </summary>
public class FileTimestampRecorderAttackTests : IDisposable
{
    private readonly string _dir;

    public FileTimestampRecorderAttackTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "hermod-recorder-attack-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string NewPath() => Path.Combine(_dir, $"timestamps-{Guid.NewGuid():N}.csv");

    private FileTimestampRecorder Make(string path, int capacity = 16)
        => new(path, capacity, NullLogger<FileTimestampRecorder>.Instance);

    private static async Task FlushAndStopAsync(FileTimestampRecorder r)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await r.StopAsync(cts.Token);
    }

    [Fact]
    public async Task Record_OverflowBeyondCapacity_DropsExcessWithoutThrowing()
    {
        // Capacity=4 and the drain is paused because we never call
        // StartAsync before these writes. Entry 5 and 6 hit a full
        // channel and TryWrite returns false; the recorder swallows
        // the drop internally. Producers must never observe an
        // exception from Record — a trace-enabled build is cheap only
        // when the call never throws on the hot path.
        var path = NewPath();
        var r = Make(path, capacity: 4);

        for (var i = 0; i < 6; i++)
        {
            r.Record($"u{i}", TimestampStages.BrokerRx, i);
        }

        // Now start and stop; only the first four must reach disk.
        await r.StartAsync(CancellationToken.None);
        await FlushAndStopAsync(r);

        var lines = File.ReadAllLines(path).Skip(1).ToList();
        Assert.Equal(4, lines.Count);
    }

    [Fact]
    public async Task Record_AfterStopAsync_ReturnsWithoutThrowing()
    {
        // After StopAsync the writer is completed; TryWrite returns
        // false and the recorder simply increments the drop counter.
        // A service shutting down must not take out its producer.
        var path = NewPath();
        var r = Make(path);
        await r.StartAsync(CancellationToken.None);
        await FlushAndStopAsync(r);

        r.Record("late", TimestampStages.BrokerRx, 1);
        r.Record("later", TimestampStages.RuleEvalDone, 2);

        // No assertion on file content — the recorder's job on the
        // producer side is to return cleanly. Reaching this line with
        // no exception is the pass condition.
    }

    [Fact]
    public void Constructor_NegativeOrZeroCapacity_ClampedToOne()
    {
        // The constructor clamps capacity<1 to 1 so a misconfigured
        // profile doesn't throw at startup. This is documented
        // behaviour worth pinning because a zero-capacity channel is
        // pathological (drops every write) and the clamp is the only
        // thing keeping a typo-in-config from breaking the app.
        var r0 = Make(NewPath(), capacity: 0);
        var rNeg = Make(NewPath(), capacity: -5);

        r0.Record("u", TimestampStages.BrokerRx, 1);
        rNeg.Record("u", TimestampStages.BrokerRx, 1);
    }

    [Fact]
    public async Task Record_UuidWithComma_IsRfc4180Quoted()
    {
        // A uuid containing `,` is wrapped in double quotes so a
        // strict RFC-4180 reader sees a single field. Prevents a
        // malicious publisher from forging extra columns into the
        // safety/liveness trace.
        var path = NewPath();
        var r = Make(path);
        await r.StartAsync(CancellationToken.None);
        r.Record("mal,icious", TimestampStages.BrokerRx, 123);
        await FlushAndStopAsync(r);

        var content = File.ReadAllText(path);
        Assert.Contains("\"mal,icious\",broker_rx,123", content);
    }

    [Fact]
    public async Task Record_UuidWithNewline_IsRfc4180Quoted()
    {
        // The newline-in-uuid attack would fracture the CSV into
        // multiple logical rows. With quoting the whole field is
        // wrapped, so a reader that honours RFC-4180 quoted newlines
        // sees a single row.
        var path = NewPath();
        var r = Make(path);
        await r.StartAsync(CancellationToken.None);
        r.Record("bad\nrow", TimestampStages.BrokerRx, 1);
        await FlushAndStopAsync(r);

        var content = File.ReadAllText(path);
        Assert.Contains("\"bad\nrow\",broker_rx,1", content);
    }

    [Fact]
    public async Task Record_UuidWithEmbeddedDoubleQuote_IsDoubled()
    {
        // RFC-4180: embedded " must be doubled inside the quoted field.
        var path = NewPath();
        var r = Make(path);
        await r.StartAsync(CancellationToken.None);
        r.Record("a\"b", TimestampStages.BrokerRx, 9);
        await FlushAndStopAsync(r);

        var content = File.ReadAllText(path);
        Assert.Contains("\"a\"\"b\",broker_rx,9", content);
    }

    [Fact]
    public async Task Record_PlainUuid_IsNotQuoted()
    {
        // Fast path: uuids without delimiter chars are written as-is
        // so the common case (load_gen uuid4) pays no extra bytes.
        var path = NewPath();
        var r = Make(path);
        await r.StartAsync(CancellationToken.None);
        r.Record("plain-uuid", TimestampStages.BrokerRx, 5);
        await FlushAndStopAsync(r);

        var content = File.ReadAllText(path);
        Assert.Contains("plain-uuid,broker_rx,5", content);
        Assert.DoesNotContain("\"plain-uuid\"", content);
    }

    [Fact]
    public async Task DisposeAsync_WithoutStart_DoesNotThrow()
    {
        var r = Make(NewPath());

        await r.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_Twice_IsIdempotent()
    {
        // Double-dispose is a common shutdown race when DI container
        // drops references and another subsystem also calls Dispose.
        // Must not throw ObjectDisposedException on the second call.
        var r = Make(NewPath());
        await r.StartAsync(CancellationToken.None);
        await r.DisposeAsync();

        var exception = await Record.ExceptionAsync(async () => await r.DisposeAsync());

        Assert.Null(exception);
    }

    [Fact]
    public async Task Record_ConcurrentProducersUnderOverflow_CompletesWithoutCorruption()
    {
        // 32 producers each firing 200 writes into a capacity-256
        // channel: overflows are guaranteed. The recorder's
        // per-record work is TryWrite on a lock-free channel plus one
        // atomic increment on drop. All rows that DO land must be
        // complete and well-formed — no partial rows, no interleaved
        // bytes (the drain loop is single-reader so writes are
        // atomic per entry).
        var path = NewPath();
        var r = Make(path, capacity: 256);
        await r.StartAsync(CancellationToken.None);

        var tasks = Enumerable.Range(0, 32).Select(w => Task.Run(() =>
        {
            for (var i = 0; i < 200; i++)
            {
                r.Record($"w{w}-{i}", TimestampStages.BrokerRx, (w * 10_000) + i);
            }
        })).ToArray();
        await Task.WhenAll(tasks);
        await FlushAndStopAsync(r);

        var lines = File.ReadAllLines(path).Skip(1);
        foreach (var line in lines)
        {
            var parts = line.Split(',');
            Assert.Equal(3, parts.Length);
            Assert.NotEmpty(parts[0]);
            Assert.Equal("broker_rx", parts[1]);
            Assert.True(long.TryParse(parts[2], out _));
        }
    }

    [Fact]
    public async Task Record_VeryLargeUuid_DoesNotBlowMemory()
    {
        // 1 MB uuid — unusual but not prevented by the record shape.
        // Channel<Entry> holds references to the strings; as long as
        // the producer doesn't retain them after Record returns, GC
        // can collect once the drain flushes. Regression guard
        // against any buffer.Append-based allocation blow-up.
        var path = NewPath();
        var r = Make(path);
        await r.StartAsync(CancellationToken.None);

        var bigUuid = new string('x', 1_000_000);
        r.Record(bigUuid, TimestampStages.BrokerRx, 1);
        await FlushAndStopAsync(r);

        var size = new FileInfo(path).Length;
        // Header (~18 B) + the 1 MB uuid + metadata; bounded.
        Assert.InRange(size, 1_000_000, 1_100_000);
    }

    [Fact]
    public async Task Stop_WithCancelledDeadline_FallsBackToForcefulCancel()
    {
        // Already-cancelled token: StopAsync's await-with-token
        // raises OperationCanceledException, the fallback path
        // triggers _shutdown.Cancel() and waits for the drain to
        // exit. Any not-yet-flushed rows are lost — that's the
        // documented shutdown behaviour. The invariant is that the
        // method returns cleanly and does not leave the drain task
        // running.
        var path = NewPath();
        var r = Make(path);
        await r.StartAsync(CancellationToken.None);
        r.Record("u1", TimestampStages.BrokerRx, 1);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await r.StopAsync(cts.Token);

        // Re-invoking Stop after the deadline path completed must
        // itself be safe (channel already completed).
        await r.StopAsync(CancellationToken.None);
    }
}
