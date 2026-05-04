using System.Collections.Concurrent;
using Hermod.Core.Models;
using Xunit;

namespace Hermod.UnitTests;

/// <summary>
/// Pins the thread-safety contract on <see cref="Device.State"/> and
/// <see cref="Device.Capabilities"/>: the declared type is
/// <c>IDictionary&lt;string, object&gt;</c> defaulting to
/// <c>ConcurrentDictionary</c>, and <c>PostgresDeviceService.MapToDevice</c>
/// wraps the deserialized dict in a fresh ConcurrentDictionary. Without
/// this, the Blazor dashboard enumerating for render while
/// <c>MessageProcessor</c> appended to the dict produced
/// <c>InvalidOperationException: Collection was modified</c>.
/// </summary>
public class DeviceStateConcurrencyTests
{
    [Fact]
    public void Device_DefaultState_IsConcurrentDictionary()
    {
        var device = new Device();
        Assert.IsType<ConcurrentDictionary<string, object>>(device.State);
        Assert.IsType<ConcurrentDictionary<string, object>>(device.Capabilities);
    }

    [Fact]
    public async Task Device_DefaultState_ToleratesConcurrentWriteDuringEnumeration()
    {
        // Reproduce the pre-cycle-79 failure mode: one task mutates the
        // dict in a tight loop while another enumerates it. Under the
        // plain-Dictionary backing this would throw
        // `InvalidOperationException: Collection was modified`. Under
        // the ConcurrentDictionary backing the enumeration sees a
        // consistent snapshot and the write succeeds.
        var device = new Device { Id = "d1" };
        device.State["initial"] = 0;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var writer = Task.Run(() =>
        {
            var i = 0;
            while (!cts.IsCancellationRequested)
            {
                device.State[$"k{i % 32}"] = i;
                i++;
            }
        });

        var reader = Task.Run(() =>
        {
            while (!cts.IsCancellationRequested)
            {
                foreach (var kvp in device.State)
                {
                    _ = kvp.Key;
                    _ = kvp.Value;
                }
            }
        });

        // Let the two race for 200 ms, then cancel and await. Any
        // `InvalidOperationException` from the reader would surface as a
        // task faulting, failing the test.
        await Task.Delay(200);
        cts.Cancel();
        await Task.WhenAll(writer, reader);
    }

    [Fact]
    public void Device_StateAssignmentFromPlainDictionary_StillCompilesAndWorks()
    {
        // Seed paths (e.g., PostgresDatabaseInitializer) still use
        // `State = new Dictionary<string, object> { ... }` initializers.
        // This test pins that the interface-typed property accepts a
        // plain dict without compile or runtime errors and that the
        // indexer access still works.
        var device = new Device
        {
            State = new Dictionary<string, object>
            {
                ["temperature"] = 22.5,
                ["humidity"] = 55
            }
        };

        Assert.Equal(22.5, device.State["temperature"]);
        Assert.Equal(55, device.State["humidity"]);
        Assert.Equal(2, device.State.Count);
    }
}
