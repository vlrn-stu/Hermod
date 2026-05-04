using System.Diagnostics.CodeAnalysis;
using Hermod.Core.Interfaces;
using Hermod.Core.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Hermod.Infrastructure.Zigbee;

/// <summary>
/// Forwards Zigbee2MQTT per-device availability events into the central
/// <see cref="IDeviceService"/> registry so the rules engine's OnAvailability
/// dispatch fires for Zigbee devices.
/// </summary>
public sealed class ZigbeeAvailabilityBridge : IHostedService, IDisposable
{
    private readonly IZigbee2MqttService _zigbee;
    private readonly IDeviceService _deviceService;
    private readonly ILogger<ZigbeeAvailabilityBridge> _logger;
    // A CTS gives a single Token whose cancellation state is visible to
    // every PropagateAsync call that captured it. The earlier version kept
    // _shutdown as a CancellationToken field and reassigned it on Stop; any
    // in-flight call that had already read CancellationToken.None would
    // never observe the subsequent "cancelled" replacement.
    private readonly CancellationTokenSource _shutdownCts = new();

    /// <summary>
    /// Creates a bridge that subscribes to
    /// <see cref="IZigbee2MqttService.DeviceAvailabilityChanged"/> on start and
    /// unsubscribes on stop.
    /// </summary>
    public ZigbeeAvailabilityBridge(
        IZigbee2MqttService zigbee,
        IDeviceService deviceService,
        ILogger<ZigbeeAvailabilityBridge> logger)
    {
        ArgumentNullException.ThrowIfNull(zigbee);
        ArgumentNullException.ThrowIfNull(deviceService);
        ArgumentNullException.ThrowIfNull(logger);
        _zigbee = zigbee;
        _deviceService = deviceService;
        _logger = logger;
    }

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _zigbee.DeviceAvailabilityChanged += OnDeviceAvailabilityChanged;
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _zigbee.DeviceAvailabilityChanged -= OnDeviceAvailabilityChanged;
        _shutdownCts.Cancel();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public void Dispose() => _shutdownCts.Dispose();

    private void OnDeviceAvailabilityChanged(object? sender, Zigbee2MqttDeviceAvailabilityEvent e)
    {
        if (string.IsNullOrEmpty(e.DeviceName)) return;

        var status = e.IsOnline ? DeviceStatus.Online : DeviceStatus.Offline;
        // Fire-and-forget: availability messages arrive on the MQTT callback
        // thread and we must not block it on a database write.
        _ = PropagateAsync(e.DeviceName, status);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Fire-and-forget event propagation from MQTT callback; any failure must log-and-continue so later events are not lost.")]
    private async Task PropagateAsync(string deviceName, DeviceStatus status)
    {
        try
        {
            await _deviceService.UpdateDeviceStatusAsync(deviceName, status, _shutdownCts.Token);
        }
        catch (OperationCanceledException)
        {
            // Shutdown in progress.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to propagate Zigbee availability for {Device} -> {Status}",
                deviceName, status);
        }
    }
}
