using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Hermod.Core.Telemetry;
using Hermod.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Hermod.UnitTests;

/// <summary>
/// End-to-end tests for <see cref="FileTimestampRecorder"/>. Covers
/// header emission, append-only growth across repeats, concurrent
/// producers, and graceful drain on shutdown. The hot path pushes
/// through a bounded channel so these tests wait on the drain loop
/// before asserting file contents.
/// </summary>
public class FileTimestampRecorderTests : IDisposable
{
    private readonly string _dir;

    public FileTimestampRecorderTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "hermod-recorder-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string CsvPath() => Path.Combine(_dir, "timestamps.csv");

    private static async Task FlushAndStopAsync(FileTimestampRecorder recorder)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await recorder.StopAsync(cts.Token);
    }

    [Fact]
    public async Task StartStop_WritesHeaderOnce()
    {
        var path = CsvPath();
        var recorder = new FileTimestampRecorder(path, bufferCapacity: 16, NullLogger<FileTimestampRecorder>.Instance);
        await recorder.StartAsync(CancellationToken.None);
        recorder.Record("u1", TimestampStages.BrokerRx, 1_000);
        recorder.Record("u1", TimestampStages.RuleEvalDone, 2_000);
        await FlushAndStopAsync(recorder);

        var lines = File.ReadAllLines(path);
        Assert.Equal("uuid,stage,ts_ns", lines[0]);
        Assert.Equal("u1,broker_rx,1000", lines[1]);
        Assert.Equal("u1,rule_eval_done,2000", lines[2]);
    }

    [Fact]
    public async Task SecondStart_AppendsWithoutNewHeader()
    {
        var path = CsvPath();
        var first = new FileTimestampRecorder(path, bufferCapacity: 16, NullLogger<FileTimestampRecorder>.Instance);
        await first.StartAsync(CancellationToken.None);
        first.Record("u1", TimestampStages.BrokerRx, 1);
        await FlushAndStopAsync(first);

        var second = new FileTimestampRecorder(path, bufferCapacity: 16, NullLogger<FileTimestampRecorder>.Instance);
        await second.StartAsync(CancellationToken.None);
        second.Record("u2", TimestampStages.BrokerRx, 2);
        await FlushAndStopAsync(second);

        var lines = File.ReadAllLines(path);
        Assert.Equal("uuid,stage,ts_ns", lines[0]);
        Assert.Equal(3, lines.Length);
        var headerCount = 0;
        foreach (var l in lines)
        {
            if (l == "uuid,stage,ts_ns") headerCount++;
        }
        Assert.Equal(1, headerCount);
    }

    [Fact]
    public async Task Record_ConcurrentWrites_AllRowsLand()
    {
        var path = CsvPath();
        var recorder = new FileTimestampRecorder(path, bufferCapacity: 8_192, NullLogger<FileTimestampRecorder>.Instance);
        await recorder.StartAsync(CancellationToken.None);

        const int writers = 8;
        const int perWriter = 200;
        var tasks = new Task[writers];
        for (var w = 0; w < writers; w++)
        {
            var wId = w;
            tasks[w] = Task.Run(() =>
            {
                for (var i = 0; i < perWriter; i++)
                {
                    recorder.Record($"w{wId}-{i}", TimestampStages.BrokerRx, (wId * 1_000) + i);
                }
            });
        }
        await Task.WhenAll(tasks);
        await FlushAndStopAsync(recorder);

        var lines = File.ReadAllLines(path);
        Assert.Equal("uuid,stage,ts_ns", lines[0]);
        Assert.Equal(writers * perWriter + 1, lines.Length);
    }

    [Fact]
    public async Task Record_EmptyUuidOrStage_IgnoresSilently()
    {
        var path = CsvPath();
        var recorder = new FileTimestampRecorder(path, bufferCapacity: 16, NullLogger<FileTimestampRecorder>.Instance);
        await recorder.StartAsync(CancellationToken.None);
        recorder.Record(string.Empty, TimestampStages.BrokerRx, 1);
        recorder.Record("u1", string.Empty, 2);
        recorder.Record("u1", TimestampStages.BrokerRx, 3);
        await FlushAndStopAsync(recorder);

        var lines = File.ReadAllLines(path);
        Assert.Equal(2, lines.Length);
        Assert.Equal("u1,broker_rx,3", lines[1]);
    }

    [Fact]
    public async Task Constructor_EmptyPath_Throws()
    {
        await Task.CompletedTask;
        Assert.Throws<ArgumentException>(() =>
            new FileTimestampRecorder(string.Empty, 16, NullLogger<FileTimestampRecorder>.Instance));
    }
}
