using LoRa2MQTT.Service;
using LoRa2MQTT.Service.Adapters;
using LoRa2MQTT.Service.Configuration;
using LoRa2MQTT.Service.Models;
using LoRa2MQTT.Service.Services;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Hermod.LoRa2MQTT.UnitTests;

/// <summary>
/// Pins the two LoRa2MQTT readiness probes (<see cref="LoRaHealthCheck"/>
/// and <see cref="MqttHealthCheck"/>). K8s polls <c>/health/ready</c>
/// every few seconds; a regression that reported Healthy on a disconnected
/// adapter or broker would leave traffic flowing to a dead pod.
/// </summary>
public class HealthCheckTests
{
    private sealed class StubLoRaAdapter : ILoRaAdapter
    {
        public bool IsConnected { get; set; }

#pragma warning disable CS0067
        public event EventHandler<LoRaMessage>? MessageReceived;
#pragma warning restore CS0067

        public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SendAsync(LoRaCommand command, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StartReceivingAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task LoRaHealthCheck_AdapterConnected_ReportsHealthy()
    {
        var sut = new LoRaHealthCheck(new StubLoRaAdapter { IsConnected = true });

        var result = await sut.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Contains("connected", result.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LoRaHealthCheck_AdapterDisconnected_ReportsUnhealthy()
    {
        var sut = new LoRaHealthCheck(new StubLoRaAdapter { IsConnected = false });

        var result = await sut.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    [Fact]
    public async Task MqttHealthCheck_FreshServiceNotConnected_ReportsUnhealthy()
    {
        // Constructing MqttService does NOT connect — IsConnected stays
        // false until ConnectAsync is called. That's the path the probe
        // exercises at pod start before the first broker handshake.
        var mqtt = new MqttService(
            NullLogger<MqttService>.Instance,
            Options.Create(new MqttOptions()),
            Options.Create(new LoRaOptions()));
        var sut = new MqttHealthCheck(mqtt);

        var result = await sut.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("not connected", result.Description, StringComparison.OrdinalIgnoreCase);
    }
}
