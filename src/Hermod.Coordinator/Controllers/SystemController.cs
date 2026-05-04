using System.Diagnostics;
using Hermod.Core.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Hermod.Coordinator.Controllers;

/// <summary>
/// Exposes the resolved runtime configuration so the test harness can
/// record exactly which feature flags, storage backend, and engine knobs
/// were active for a given run. Anonymous only when
/// <see cref="DevSettings.Endpoints"/> is on (dev loops + matrix
/// harness run there); in prod the same Vault42 JWT the UI sends is
/// required and the harness authenticates via a service-account JWT.
/// </summary>
[ApiController]
[Route("api/system")]
public sealed class SystemController : ControllerBase
{
    private static readonly DateTimeOffset ProcessStart = GetProcessStart();

    private static DateTimeOffset GetProcessStart()
    {
        using var process = Process.GetCurrentProcess();
        return process.StartTime.ToUniversalTime();
    }

    private readonly HermodSettings _settings;

    /// <summary>Creates the controller with a bound <see cref="HermodSettings"/>.</summary>
    /// <param name="settings">Options accessor over the resolved settings.</param>
    public SystemController(IOptions<HermodSettings> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings.Value;
    }

    /// <summary>Returns the resolved feature flags, storage / engine knobs, build metadata and uptime.</summary>
    /// <returns>200 with the <see cref="SystemFeaturesResponse"/>, or 401 via the global fallback policy when <see cref="DevSettings.Endpoints"/> is off and no JWT was sent.</returns>
    [HttpGet("features")]
    [AllowAnonymous]
    public IActionResult GetFeatures()
    {
        // In dev mode the matrix harness hits this pre-JWT, so anonymous
        // is allowed; in prod we require the same authenticated user the
        // rest of the API expects. The UI's Vault42 JWT flows through
        // the standard bearer pipeline upstream; here we only check
        // IsAuthenticated because the signature validation happens in
        // the Vault middleware, not this handler.
        if (!_settings.Dev.Endpoints && !(User?.Identity?.IsAuthenticated ?? false))
        {
            return Unauthorized();
        }

        var now = DateTimeOffset.UtcNow;
        var uptime = now - ProcessStart;

        return Ok(new SystemFeaturesResponse(
            Features: new FeaturesView(
                DeviceStateTracking: _settings.Features.DeviceStateTracking,
                MessagePersistence: _settings.Features.MessagePersistence,
                RuleAuditLog: _settings.Features.RuleAuditLog,
                StatsRollup: _settings.Features.StatsRollup,
                RuleCache: _settings.Features.RuleCache,
                MetricsEndpoint: _settings.Features.MetricsEndpoint,
                UuidTrace: _settings.Features.UuidTrace),
            Storage: new StorageView(
                Mode: _settings.Storage.Mode.ToString(),
                WriteBatchSize: _settings.Storage.WriteBatchSize,
                WriteFlushIntervalMs: _settings.Storage.WriteFlushIntervalMs,
                WriteQueueCapacity: _settings.Storage.WriteQueueCapacity,
                MaxPoolSize: _settings.Storage.MaxPoolSize,
                MinPoolSize: _settings.Storage.MinPoolSize,
                CommandTimeoutSeconds: _settings.Storage.CommandTimeoutSeconds,
                KeepAliveSeconds: _settings.Storage.KeepAliveSeconds,
                MaxAutoPrepare: _settings.Storage.MaxAutoPrepare,
                SkipDeviceExistenceCheck: _settings.Storage.SkipDeviceExistenceCheck,
                FastDeviceUpserts: _settings.Storage.FastDeviceUpserts),
            Engine: new EngineView(
                Parallelism: _settings.Engine.Parallelism,
                BatchSize: _settings.Engine.BatchSize,
                QueueCapacity: _settings.Engine.QueueCapacity,
                LogBatching: _settings.Engine.LogBatching,
                LogBatchSize: _settings.Engine.LogBatchSize,
                LogBatchIntervalMs: _settings.Engine.LogBatchIntervalMs,
                RuleCacheRefreshSeconds: _settings.Engine.RuleCacheRefreshSeconds),
            Mqtt: new MqttView(
                ReconnectBufferSize: _settings.Mqtt.ReconnectBufferSize,
                ParallelClients: _settings.Mqtt.ParallelClients),
            Seed: new SeedView(
                Devices: _settings.Seed.Devices,
                Rules: _settings.Seed.Rules),
            Dev: new DevView(
                Endpoints: _settings.Dev.Endpoints),
            Telemetry: new TelemetryView(
                TimestampsCsvPath: _settings.Telemetry.TimestampsCsvPath,
                BufferCapacity: _settings.Telemetry.BufferCapacity),
            Build: new BuildView(
                GitSha: Environment.GetEnvironmentVariable("HERMOD_GIT_SHA") ?? "unknown",
                ImageDigest: Environment.GetEnvironmentVariable("HERMOD_IMAGE_DIGEST") ?? "unknown",
                AssemblyVersion: typeof(SystemController).Assembly.GetName().Version?.ToString() ?? "0.0.0.0"),
            Runtime: new RuntimeView(
                StartedAt: ProcessStart,
                UptimeSeconds: (long)uptime.TotalSeconds,
                Now: now)));
    }
}

/// <summary>Top-level payload returned by <see cref="SystemController.GetFeatures"/>.</summary>
/// <param name="Features">Feature flag snapshot.</param>
/// <param name="Storage">Storage tuning knobs.</param>
/// <param name="Engine">Rule engine tuning knobs.</param>
/// <param name="Mqtt">MQTT tuning knobs.</param>
/// <param name="Seed">Seed data flags.</param>
/// <param name="Dev">Development-only surface flags.</param>
/// <param name="Telemetry">Per-message timestamp recorder settings.</param>
/// <param name="Build">Build and deployment identifiers.</param>
/// <param name="Runtime">Process start time and uptime.</param>
public sealed record SystemFeaturesResponse(
    FeaturesView Features,
    StorageView Storage,
    EngineView Engine,
    MqttView Mqtt,
    SeedView Seed,
    DevView Dev,
    TelemetryView Telemetry,
    BuildView Build,
    RuntimeView Runtime);

/// <summary>Feature flag snapshot from <c>HermodSettings.Features</c>.</summary>
/// <param name="DeviceStateTracking">Whether device state is tracked.</param>
/// <param name="MessagePersistence">Whether raw messages are persisted.</param>
/// <param name="RuleAuditLog">Whether rule executions are audit-logged.</param>
/// <param name="StatsRollup">Whether background stats rollup runs.</param>
/// <param name="RuleCache">Whether the rule cache is active.</param>
/// <param name="MetricsEndpoint">Whether the Prometheus metrics endpoint is exposed.</param>
/// <param name="UuidTrace">Whether payload UUID tracing is on (driven by <c>HERMOD_UUID_TRACE_ENABLED</c>; only the trace-baseline profile and the safety/liveness verifier set this true).</param>
public sealed record FeaturesView(
    bool DeviceStateTracking,
    bool MessagePersistence,
    bool RuleAuditLog,
    bool StatsRollup,
    bool RuleCache,
    bool MetricsEndpoint,
    bool UuidTrace);

/// <summary>Storage tuning knobs from <c>HermodSettings.Storage</c>.</summary>
/// <param name="Mode">Storage mode (<c>Postgres</c>, <c>InMemory</c>, ...).</param>
/// <param name="WriteBatchSize">Write batch size.</param>
/// <param name="WriteFlushIntervalMs">Write flush interval in milliseconds.</param>
/// <param name="WriteQueueCapacity">Bounded write queue capacity.</param>
/// <param name="MaxPoolSize">Npgsql maximum pool size.</param>
/// <param name="MinPoolSize">Npgsql minimum pool size.</param>
/// <param name="CommandTimeoutSeconds">Per-command Npgsql timeout in seconds.</param>
/// <param name="KeepAliveSeconds">TCP keepalive interval in seconds.</param>
/// <param name="MaxAutoPrepare">Auto-prepared-statement cache size.</param>
/// <param name="SkipDeviceExistenceCheck">Whether FK-safety checks are skipped on the device upsert path.</param>
/// <param name="FastDeviceUpserts">Whether the minimal-columns fast-path device upsert is enabled.</param>
public sealed record StorageView(
    string Mode,
    int WriteBatchSize,
    int WriteFlushIntervalMs,
    int WriteQueueCapacity,
    int MaxPoolSize,
    int MinPoolSize,
    int CommandTimeoutSeconds,
    int KeepAliveSeconds,
    int MaxAutoPrepare,
    bool SkipDeviceExistenceCheck,
    bool FastDeviceUpserts);

/// <summary>Rule engine tuning knobs from <c>HermodSettings.Engine</c>.</summary>
/// <param name="Parallelism">Worker parallelism.</param>
/// <param name="BatchSize">Evaluation batch size.</param>
/// <param name="QueueCapacity">Bounded inbound queue capacity.</param>
/// <param name="LogBatching">Whether rule logs are batched before flush.</param>
/// <param name="LogBatchSize">Log batch size.</param>
/// <param name="LogBatchIntervalMs">Log batch flush interval in milliseconds.</param>
/// <param name="RuleCacheRefreshSeconds">Rule cache refresh interval in seconds.</param>
public sealed record EngineView(
    int Parallelism,
    int BatchSize,
    int QueueCapacity,
    bool LogBatching,
    int LogBatchSize,
    int LogBatchIntervalMs,
    int RuleCacheRefreshSeconds);

/// <summary>MQTT tuning knobs.</summary>
/// <param name="ReconnectBufferSize">Size of the offline publish buffer used while reconnecting.</param>
/// <param name="ParallelClients">Number of parallel MQTT publisher clients (1 = single client; higher values shard outbound publishes across clients to match broker core count).</param>
public sealed record MqttView(int ReconnectBufferSize, int ParallelClients);

/// <summary>Seed-data flags.</summary>
/// <param name="Devices">Whether demo devices are seeded.</param>
/// <param name="Rules">Whether demo rules are seeded.</param>
public sealed record SeedView(bool Devices, bool Rules);

/// <summary>Development-only surface flags.</summary>
/// <param name="Endpoints">Whether dev-only endpoints are enabled.</param>
public sealed record DevView(bool Endpoints);

/// <summary>Per-message timestamp recorder settings from <c>HermodSettings.Telemetry</c>.</summary>
/// <param name="TimestampsCsvPath">Absolute path to the per-run <c>timestamps.csv</c> file; null means the recorder is not registered. Driven by <c>HERMOD_TIMESTAMPS_CSV</c>.</param>
/// <param name="BufferCapacity">Max in-memory rows the recorder buffers before dropping new records.</param>
public sealed record TelemetryView(string? TimestampsCsvPath, int BufferCapacity);

/// <summary>Build and deployment identifiers.</summary>
/// <param name="GitSha">Git SHA of the build (from <c>HERMOD_GIT_SHA</c>).</param>
/// <param name="ImageDigest">Container image digest (from <c>HERMOD_IMAGE_DIGEST</c>).</param>
/// <param name="AssemblyVersion">Assembly version of the coordinator.</param>
public sealed record BuildView(string GitSha, string ImageDigest, string AssemblyVersion);

/// <summary>Process timing view.</summary>
/// <param name="StartedAt">UTC timestamp the process started.</param>
/// <param name="UptimeSeconds">Uptime in whole seconds.</param>
/// <param name="Now">UTC time the sample was taken.</param>
public sealed record RuntimeView(
    DateTimeOffset StartedAt,
    long UptimeSeconds,
    DateTimeOffset Now);
