using Hermod.Core.Interfaces;
using Hermod.Core.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Hermod.Coordinator.Controllers;

/// <summary>Read-only surface over <see cref="IStatsService"/> with historical and health endpoints.</summary>
[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
[Authorize]
public sealed class StatsController : ControllerBase
{
    private const int DefaultHistoryLimit = 100;
    private const int MaxHistoryLimit = 1000;

    private readonly IStatsService _statsService;

    /// <summary>Creates the controller with the live stats service.</summary>
    /// <param name="statsService">Service producing live system stats.</param>
    public StatsController(IStatsService statsService)
    {
        ArgumentNullException.ThrowIfNull(statsService);
        _statsService = statsService;
    }

    /// <summary>Get current system statistics.</summary>
    /// <param name="cancellationToken">Token to abort the query.</param>
    /// <returns>200 with the current <see cref="SystemStats"/>.</returns>
    [HttpGet]
    [ProducesResponseType(typeof(SystemStats), StatusCodes.Status200OK)]
    public async Task<ActionResult<SystemStats>> GetStats(CancellationToken cancellationToken = default)
    {
        var stats = await _statsService.GetCurrentStatsAsync(cancellationToken);
        return Ok(stats);
    }

    /// <summary>Zero every lifetime counter (messages, rules, dropped, errors) in-memory and in the persisted counters table.</summary>
    /// <param name="cancellationToken">Token to abort the reset.</param>
    /// <returns>204 on success; the Settings page re-polls /api/stats to refresh tiles.</returns>
    [HttpPost("reset")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult> ResetCounters(CancellationToken cancellationToken = default)
    {
        await _statsService.ResetCountersAsync(cancellationToken);
        return NoContent();
    }

    /// <summary>Get statistics broken down by protocol.</summary>
    /// <param name="cancellationToken">Token to abort the query.</param>
    /// <returns>200 with per-protocol stats.</returns>
    [HttpGet("protocols")]
    [ProducesResponseType(typeof(IEnumerable<ProtocolStats>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<ProtocolStats>>> GetProtocolStats(CancellationToken cancellationToken = default)
    {
        var stats = await _statsService.GetProtocolStatsAsync(cancellationToken);
        return Ok(stats);
    }

    /// <summary>
    /// Get historical metrics snapshots persisted to PostgreSQL.
    /// Newest first. Default limit 100, capped at 1000.
    /// </summary>
    /// <param name="metrics">Metrics repository (injected).</param>
    /// <param name="limit">Maximum number of snapshots (clamped to 1-1000).</param>
    /// <param name="cancellationToken">Token to abort the query.</param>
    /// <returns>200 with the recent snapshots, newest first.</returns>
    [HttpGet("history")]
    [ProducesResponseType(typeof(IEnumerable<SystemStats>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<SystemStats>>> GetHistory(
        [FromServices] IMetricsRepository metrics,
        [FromQuery] int limit = DefaultHistoryLimit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        limit = Math.Clamp(limit, 1, MaxHistoryLimit);
        var snapshots = await metrics.GetRecentSnapshotsAsync(limit, cancellationToken);
        return Ok(snapshots);
    }

    /// <summary>Get system health status.</summary>
    /// <param name="mqttService">MQTT service used to check broker connectivity.</param>
    /// <param name="cancellationToken">Token to abort the query.</param>
    /// <returns>200 with a <see cref="HealthStatus"/>.</returns>
    [HttpGet("health")]
    [ProducesResponseType(typeof(HealthStatus), StatusCodes.Status200OK)]
    public async Task<ActionResult<HealthStatus>> GetHealth(
        [FromServices] IMqttService mqttService,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mqttService);
        var stats = await _statsService.GetCurrentStatsAsync(cancellationToken);

        return Ok(new HealthStatus
        {
            Status = mqttService.IsConnected ? "healthy" : "degraded",
            MqttConnected = mqttService.IsConnected,
            Uptime = stats.Uptime,
            TotalDevices = stats.TotalDevices,
            OnlineDevices = stats.OnlineDevices,
            ActiveRules = stats.ActiveRules,
            MessagesProcessed = stats.MessagesProcessed,
            Timestamp = DateTime.UtcNow
        });
    }
}

/// <summary>Aggregate health summary returned by the <c>/api/stats/health</c> endpoint.</summary>
public sealed class HealthStatus
{
    /// <summary>Overall health word: <c>healthy</c>, <c>degraded</c>, or <c>unknown</c>.</summary>
    public string Status { get; set; } = "unknown";

    /// <summary>Whether the MQTT broker connection is currently up.</summary>
    public bool MqttConnected { get; set; }

    /// <summary>Process uptime.</summary>
    public TimeSpan Uptime { get; set; }

    /// <summary>Total number of known devices.</summary>
    public int TotalDevices { get; set; }

    /// <summary>Number of devices currently reported online.</summary>
    public int OnlineDevices { get; set; }

    /// <summary>Number of enabled rules.</summary>
    public int ActiveRules { get; set; }

    /// <summary>Total messages processed since startup.</summary>
    public long MessagesProcessed { get; set; }

    /// <summary>UTC timestamp the snapshot was taken.</summary>
    public DateTime Timestamp { get; set; }
}
