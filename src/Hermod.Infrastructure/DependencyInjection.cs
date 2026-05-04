using System.Diagnostics.CodeAnalysis;
using Hermod.Core.Configuration;
using Hermod.Core.Interfaces;
using Hermod.Infrastructure.Database;
using Hermod.Infrastructure.Database.Noop;
using Hermod.Infrastructure.Mqtt;
using Hermod.Infrastructure.Services;
using Hermod.Core.Telemetry;
using Hermod.Infrastructure.Zigbee;
using Hermod.Rules;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Hermod.Infrastructure;

/// <summary>
/// <see cref="IServiceCollection"/> composition root for Hermod's
/// infrastructure layer: MQTT, storage, metrics, Zigbee, rules engine.
/// </summary>
[SuppressMessage("Naming", "CA1724:Type names should not match namespaces", Justification = "The DI composition class is conventionally named DependencyInjection across our service boundary; the namespace clash is shadowed by the using directive.")]
public static class DependencyInjection
{
    /// <summary>
    /// Registers every Hermod infrastructure service (storage, MQTT, rules
    /// engine, Zigbee bridge, background workers) on the given
    /// <paramref name="services"/> collection using settings rooted at the
    /// <c>Hermod</c> configuration section.
    /// </summary>
    public static IServiceCollection AddHermodInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.Configure<HermodSettings>(configuration.GetSection("Hermod"));

        // Single-underscore env overlays (HERMOD_UUID_TRACE_ENABLED,
        // HERMOD_TIMESTAMPS_CSV) post-config so matrix profiles can
        // flip flags without editing appsettings.
        services.PostConfigure<HermodSettings>(s => s.Features.ApplyEnvironmentOverrides(Environment.GetEnvironmentVariable));
        services.PostConfigure<HermodSettings>(s => s.Telemetry.ApplyEnvironmentOverrides(Environment.GetEnvironmentVariable));

        // Always registered so hot-path Inc* calls need no null check;
        // the /metrics endpoint is what Features:MetricsEndpoint gates.
        services.AddSingleton<HermodMetrics>();

        // Per-topic ingress hard limiter — singleton so its bucket /
        // dedup state survives across MessageProcessor lifetime. Off
        // by default; prod overlays flip Hermod__RateLimit__Enabled=true.
        services.AddSingleton<IRateLimitOverridesStore, RateLimitOverridesStore>();
        services.AddSingleton<ITopicIngressLimiter, TopicIngressLimiter>();

        // Aggregate per-protocol bidirectional limiter — runs alongside
        // the per-topic one as a second clamp at both the ingress hook
        // (MessageProcessor) and the egress hook (MqttService.PublishAsync).
        services.AddSingleton<IProtocolFlowOverridesStore, ProtocolFlowOverridesStore>();
        services.AddSingleton<IProtocolFlowLimiter, ProtocolFlowLimiter>();

        // Null-object recorder when no path configured => hot-path
        // stamps are free when a run doesn't want tracing.
        var telemetry = configuration.GetSection("Hermod:Telemetry").Get<TelemetrySettings>() ?? new TelemetrySettings();
        telemetry.ApplyEnvironmentOverrides(Environment.GetEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(telemetry.TimestampsCsvPath))
        {
            services.AddSingleton<FileTimestampRecorder>(sp =>
                new FileTimestampRecorder(
                    telemetry.TimestampsCsvPath!,
                    telemetry.BufferCapacity,
                    sp.GetRequiredService<ILogger<FileTimestampRecorder>>()));
            services.AddSingleton<ITimestampRecorder>(sp =>
                sp.GetRequiredService<FileTimestampRecorder>());
            services.AddHostedService(sp =>
                sp.GetRequiredService<FileTimestampRecorder>());
        }
        else
        {
            services.AddSingleton<ITimestampRecorder>(NoopTimestampRecorder.Instance);
        }

        // Dapper: map snake_case columns to PascalCase properties.
        Dapper.DefaultTypeMap.MatchNamesWithUnderscores = true;

        // Postgres factory + schema initializer are always registered;
        // only the hosted-service wrapper pulls the initializer in under
        // the Postgres storage mode.
        var storageMode = ResolveStorageMode(configuration);

        services.AddSingleton<PostgresConnectionFactory>();
        services.AddSingleton<PostgresSchemaInitializer>();

        switch (storageMode)
        {
            case StorageMode.Noop:
                services.AddSingleton<IDeviceService, NoopDeviceService>();
                services.AddSingleton<IRulesService, NoopRulesService>();
                services.AddSingleton<IMetricsRepository, NoopMetricsRepository>();
                services.AddSingleton<IMessageHistoryRepository, NoopMessageHistoryRepository>();
                services.AddSingleton<IRuleAuditRepository, NoopRuleAuditRepository>();
                services.AddSingleton<IUserSessionRepository, NoopUserSessionRepository>();
                // No Postgres initializer: schema/seed are skipped entirely.
                break;
            case StorageMode.InMemory:
                // InMemory currently aliases Noop; separate enum value
                // preserves profile intent for when an in-memory impl ships.
                services.AddSingleton<IDeviceService, NoopDeviceService>();
                services.AddSingleton<IRulesService, NoopRulesService>();
                services.AddSingleton<IMetricsRepository, NoopMetricsRepository>();
                services.AddSingleton<IMessageHistoryRepository, NoopMessageHistoryRepository>();
                services.AddSingleton<IRuleAuditRepository, NoopRuleAuditRepository>();
                services.AddSingleton<IUserSessionRepository, NoopUserSessionRepository>();
                break;
            case StorageMode.Postgres:
            default:
                services.AddSingleton<IDeviceService, PostgresDeviceService>();
                services.AddSingleton<IRulesService, PostgresRulesService>();
                services.AddSingleton<IMetricsRepository, PostgresMetricsRepository>();
                services.AddSingleton<IUserSessionRepository, PostgresUserSessionRepository>();

                // Batched writer: one singleton backs the interface + the
                // hosted-service so flush loop + AppendAsync share state.
                services.AddSingleton<PostgresMessageHistoryRepository>();
                services.AddSingleton<IMessageHistoryRepository>(sp =>
                    sp.GetRequiredService<PostgresMessageHistoryRepository>());
                services.AddHostedService(sp =>
                    sp.GetRequiredService<PostgresMessageHistoryRepository>());

                // Retention sweeper: ages out rows older than the cutoff in
                // bounded batches. Always registered under Postgres mode —
                // self-skips when MessageHistoryRetentionDays=0 OR the table
                // is empty (sweep-until-clean exits on first 0-row batch),
                // so the cost when MessagePersistence is off is one query
                // per sweep interval.
                services.AddHostedService<PostgresMessageHistoryRetentionWorker>();

                services.AddSingleton<PostgresRuleAuditRepository>();
                services.AddSingleton<IRuleAuditRepository>(sp =>
                    sp.GetRequiredService<PostgresRuleAuditRepository>());
                services.AddHostedService(sp =>
                    sp.GetRequiredService<PostgresRuleAuditRepository>());

                // Batched device-state writer: one singleton backs the
                // MessageProcessor ctor-injected reference + the hosted
                // service so the flush loop and EnqueueAsync share state.
                // Only registered under Postgres mode — Noop/InMemory use
                // the direct IDeviceService path; MessageProcessor's
                // optional ctor parameter takes null and falls back.
                services.AddSingleton<BatchedDeviceStateWriter>();
                services.AddHostedService(sp =>
                    sp.GetRequiredService<BatchedDeviceStateWriter>());

                services.AddHostedService<PostgresDatabaseInitializer>();
                break;
        }

        // ParallelMqttService shards ingest across N inner MqttService
        // instances when Mqtt:ParallelClients > 1; N=1 is pass-through.
        services.AddSingleton<IMqttService, ParallelMqttService>();
        services.AddSingleton<IStatsService, StatsService>();
        services.AddHostedService<MetricsPersistenceService>();

        // Rules Engine and related services
        services.AddSingleton<IExpressionEvaluator, ExpressionEvaluator>();
        services.AddSingleton<IStateManager, StateManager>();
        services.AddSingleton<IScheduler, Scheduler>();
        services.AddHttpClient(EnhancedRulesEngine.WebhookHttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
        });
        services.AddSingleton<IRulesEngine, EnhancedRulesEngine>();
        services.AddHostedService<RuleStatePersistenceService>();

        // Protocol handlers (fallback is built into EnhancedRulesEngine)
        services.AddSingleton<IZigbee2MqttService, Zigbee2MqttService>();
        services.AddHostedService<ZigbeeAvailabilityBridge>();
        services.AddHostedService<ZigbeeStatePoller>();

        // Translator liveness probe (used by the Health dashboard).
        services.AddHttpClient();
        services.AddSingleton<IProtocolTranslatorHealth, ProtocolTranslatorHealthChecker>();

        // Background services
        services.AddHostedService<MessageProcessor>();

        return services;
    }

    /// <summary>
    /// Resolves the <see cref="StorageMode"/> from configuration at startup.
    /// Parsing is case-insensitive and falls back to <see cref="StorageMode.Postgres"/>
    /// on unknown values so a typo can't silently flip a live coordinator
    /// into Noop.
    /// </summary>
    private static StorageMode ResolveStorageMode(IConfiguration configuration)
    {
        var raw = configuration["Hermod:Storage:Mode"];
        if (string.IsNullOrWhiteSpace(raw)) return StorageMode.Postgres;
        return Enum.TryParse<StorageMode>(raw, ignoreCase: true, out var parsed)
            ? parsed
            : StorageMode.Postgres;
    }
}
