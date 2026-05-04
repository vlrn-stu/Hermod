using System.Diagnostics.CodeAnalysis;
using Hermod.Core.Configuration;
using Hermod.Core.Interfaces;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hermod.Infrastructure.Zigbee;

/// <summary>
/// Sweeps every known Zigbee device on an interval and pulls a fresh state
/// snapshot via <c>zigbee/{device}/get</c>. The reply lands on the
/// standard state topic and flows through <see cref="Services.MessageProcessor"/>
/// like any other state push, so no bespoke ingest path is needed here.
///
/// The sweep is paced (PerDeviceDelayMs) so a fleet of dozens of sensors does
/// not cause a thundering herd against the bridge. Coordinator devices and
/// disabled devices are skipped.
/// </summary>
public sealed class ZigbeeStatePoller : BackgroundService
{
    private readonly IZigbee2MqttService _zigbee;
    private readonly ZigbeeStatePollerSettings _settings;
    private readonly ILogger<ZigbeeStatePoller> _logger;

    /// <summary>
    /// Creates a poller that reads its pacing parameters from
    /// <see cref="HermodSettings.Zigbee"/>.StatePoller.
    /// </summary>
    public ZigbeeStatePoller(
        IZigbee2MqttService zigbee,
        IOptions<HermodSettings> settings,
        ILogger<ZigbeeStatePoller> logger)
    {
        ArgumentNullException.ThrowIfNull(zigbee);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);
        _zigbee = zigbee;
        _settings = settings.Value.Zigbee.StatePoller;
        _logger = logger;
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_settings.Enabled)
        {
            _logger.LogInformation("ZigbeeStatePoller disabled by configuration");
            return;
        }

        var interval = TimeSpan.FromSeconds(Math.Max(10, _settings.IntervalSeconds));
        var perDeviceDelay = TimeSpan.FromMilliseconds(Math.Max(0, _settings.PerDeviceDelayMs));

        _logger.LogInformation(
            "ZigbeeStatePoller started (interval={Interval}, per-device gap={Gap})",
            interval, perDeviceDelay);

        // Brief warm-up so we don't hammer a bridge that hasn't finished its
        // own startup. The first sweep is opportunistic; if the bridge isn't
        // online yet we just skip and try again next tick.
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        using var timer = new PeriodicTimer(interval);
        try
        {
            do
            {
                await SweepAsync(perDeviceDelay, stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Per-device poll failure must not kill the sweep; each device is independent.")]
    internal async Task SweepAsync(TimeSpan perDeviceDelay, CancellationToken cancellationToken)
    {
        if (!_zigbee.IsBridgeOnline)
        {
            _logger.LogDebug("ZigbeeStatePoller skipping sweep: bridge offline");
            return;
        }

        var devices = _zigbee.Devices;
        if (devices.Count == 0) return;

        var queried = 0;
        foreach (var device in devices)
        {
            if (cancellationToken.IsCancellationRequested) break;
            if (device.Type == "Coordinator" || device.Disabled) continue;
            if (string.IsNullOrEmpty(device.FriendlyName)) continue;

            try
            {
                await _zigbee.GetDeviceStateAsync(device.FriendlyName, property: null, cancellationToken);
                queried++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to poll Zigbee device {Name}", device.FriendlyName);
            }

            if (perDeviceDelay > TimeSpan.Zero)
            {
                await Task.Delay(perDeviceDelay, cancellationToken);
            }
        }

        if (queried > 0)
        {
            _logger.LogDebug("ZigbeeStatePoller sweep complete: {Queried} device(s) polled", queried);
        }
    }
}
