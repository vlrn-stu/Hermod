using LoRa2MQTT.Service.Services;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace LoRa2MQTT.Service;

/// <summary>
/// Health check for MQTT broker connectivity.
/// </summary>
public sealed class MqttHealthCheck : IHealthCheck
{
    private readonly MqttService _mqttService;

    /// <summary>
    /// Initializes a new instance of <see cref="MqttHealthCheck"/>.
    /// </summary>
    public MqttHealthCheck(MqttService mqttService)
    {
        ArgumentNullException.ThrowIfNull(mqttService);
        _mqttService = mqttService;
    }

    /// <inheritdoc/>
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_mqttService.IsConnected
            ? HealthCheckResult.Healthy("MQTT broker is connected")
            : HealthCheckResult.Unhealthy("MQTT broker is not connected"));
    }
}
