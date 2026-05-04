using LoRa2MQTT.Service.Adapters;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace LoRa2MQTT.Service;

/// <summary>
/// Health check for LoRa adapter connectivity.
/// </summary>
public sealed class LoRaHealthCheck : IHealthCheck
{
    private readonly ILoRaAdapter _adapter;

    /// <summary>
    /// Initializes a new instance of <see cref="LoRaHealthCheck"/>.
    /// </summary>
    public LoRaHealthCheck(ILoRaAdapter adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        _adapter = adapter;
    }

    /// <inheritdoc/>
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_adapter.IsConnected
            ? HealthCheckResult.Healthy("LoRa adapter is connected")
            : HealthCheckResult.Unhealthy("LoRa adapter is not connected"));
    }
}
