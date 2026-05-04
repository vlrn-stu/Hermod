using LoRa2MQTT.Service.Adapters;
using LoRa2MQTT.Service.Configuration;
using LoRa2MQTT.Service.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Hermod.LoRa2MQTT.UnitTests;

/// <summary>
/// Pins the fail-closed guard on <see cref="MockLoRaAdapter.StartAutoSimulation"/>:
/// when <c>_cts</c> is still null (an early <c>lora/mock/auto/start</c> MQTT
/// control message arriving before <see cref="MockLoRaAdapter.StartReceivingAsync"/>),
/// the request is refused with a warning and <see cref="MockLoRaAdapter.AutoSimulationRunning"/>
/// stays false — instead of silently falling back to <c>CancellationToken.None</c>
/// and running the simulation loop unbounded past bridge shutdown.
/// </summary>
public class MockLoRaAdapterLifecycleTests
{
    private static MockLoRaAdapter Build(params MockDevice[] devices)
    {
        var mockOpts = Options.Create(new MockOptions
        {
            IntervalMs = 10000,
            Devices = devices.ToList()
        });
        var loraOpts = Options.Create(new LoRaOptions
        {
            Channel = 7
        });
        return new MockLoRaAdapter(
            NullLogger<MockLoRaAdapter>.Instance,
            mockOpts,
            loraOpts);
    }

    [Fact]
    public void StartAutoSimulation_BeforeStartReceivingAsync_FailsClosed()
    {
        // Race shape: a mock-control MQTT message for
        // `lora/mock/auto/start` arrives and invokes StartAutoSimulation
        // BEFORE the bridge worker has called StartReceivingAsync. The
        // _cts field is still null; guard refuses the request and
        // AutoSimulationRunning stays false.
        var sut = Build(new MockDevice { Id = "d1", Type = "weather" });

        sut.StartAutoSimulation();

        Assert.False(sut.AutoSimulationRunning);
    }

    [Fact]
    public async Task StartAutoSimulation_AfterStartReceivingAsync_SucceedsAndStoppable()
    {
        // Happy path: StartReceivingAsync initializes _cts first, so
        // StartAutoSimulation succeeds. StopAutoSimulation flips the
        // flag off and the secondary stop condition inside
        // SimulateDevicesAsync terminates the loop.
        var sut = Build(new MockDevice { Id = "d1", Type = "weather" });

        await sut.StartReceivingAsync();
        sut.StartAutoSimulation();

        Assert.True(sut.AutoSimulationRunning);

        sut.StopAutoSimulation();

        Assert.False(sut.AutoSimulationRunning);
    }

    [Fact]
    public void StartAutoSimulation_NoDevicesRegistered_FailsClosed()
    {
        // Pre-existing guard: auto-sim with zero devices was always
        // refused. Pinning this alongside the new guard so a future
        // refactor can't drop one without dropping the other.
        var sut = Build();

        sut.StartAutoSimulation();

        Assert.False(sut.AutoSimulationRunning);
    }
}
