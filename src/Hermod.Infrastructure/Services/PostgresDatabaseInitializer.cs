using Hermod.Core.Configuration;
using Hermod.Core.Interfaces;
using Hermod.Core.Models;
using Hermod.Core.Models.Rules;
using Hermod.Infrastructure.Database;
using Hermod.Infrastructure.Services.Seed;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hermod.Infrastructure.Services;

/// <summary>
/// Hosted service that brings the PostgreSQL database online: waits for the
/// server, applies the schema, and seeds the sample rule and device fixtures
/// (per-id upsert, so operator edits survive coordinator restarts).
/// </summary>
public sealed class PostgresDatabaseInitializer : IHostedService
{
    private readonly PostgresConnectionFactory _connectionFactory;
    private readonly PostgresSchemaInitializer _schemaInitializer;
    private readonly IRulesService _rulesService;
    private readonly IDeviceService _deviceService;
    private readonly IRateLimitOverridesStore? _topicOverrides;
    private readonly IProtocolFlowOverridesStore? _protocolOverrides;
    private readonly ILogger<PostgresDatabaseInitializer> _logger;
    private readonly SeedSettings _seed;

    /// <summary>
    /// Creates the initializer. All arguments are required; the
    /// <see cref="HermodSettings.Seed"/> flags gate the rule/device upserts.
    /// </summary>
    public PostgresDatabaseInitializer(
        PostgresConnectionFactory connectionFactory,
        PostgresSchemaInitializer schemaInitializer,
        IRulesService rulesService,
        IDeviceService deviceService,
        IOptions<HermodSettings> settings,
        ILogger<PostgresDatabaseInitializer> logger,
        IRateLimitOverridesStore? topicOverrides = null,
        IProtocolFlowOverridesStore? protocolOverrides = null)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        ArgumentNullException.ThrowIfNull(schemaInitializer);
        ArgumentNullException.ThrowIfNull(rulesService);
        ArgumentNullException.ThrowIfNull(deviceService);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);
        _connectionFactory = connectionFactory;
        _schemaInitializer = schemaInitializer;
        _rulesService = rulesService;
        _deviceService = deviceService;
        _seed = settings.Value.Seed;
        _logger = logger;
        _topicOverrides = topicOverrides;
        _protocolOverrides = protocolOverrides;
    }

    /// <inheritdoc/>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Initializing PostgreSQL database...");
        await _connectionFactory.WaitForReadyAsync(cancellationToken);
        await _schemaInitializer.InitializeAsync(cancellationToken);

        if (_seed.Rules)
        {
            await SeedRulesAsync(cancellationToken);
        }
        else
        {
            _logger.LogInformation("Rule seeding disabled via Hermod:Seed:Rules");
        }

        if (_seed.Devices)
        {
            await SeedDevicesAsync(cancellationToken);
        }
        else
        {
            _logger.LogInformation("Device seeding disabled via Hermod:Seed:Devices");
        }

        // Hydrate the rate-limit override caches from Postgres so an
        // operator's runtime overrides survive coordinator restarts.
        // Runs after schema init so the tables are guaranteed present.
        if (_topicOverrides is not null)
            await _topicOverrides.LoadAsync(cancellationToken);
        if (_protocolOverrides is not null)
            await _protocolOverrides.LoadAsync(cancellationToken);

        _logger.LogInformation("PostgreSQL database initialization complete");
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task SeedRulesAsync(CancellationToken cancellationToken)
    {
        var samples = SampleRules.All;
        var existing = (await _rulesService.GetAllRulesAsync(cancellationToken)).ToList();
        var existingIds = ToCaseInsensitiveSet(existing.Select(r => r.Id));

        _logger.LogInformation(
            "Seeding example rules (existing: {Existing}); will upsert missing ids only",
            existing.Count);

        var (seeded, skipped) = await UpsertMissingAsync(
            samples,
            r => r.Id,
            existingIds,
            r => _rulesService.AddOrUpdateRuleAsync(r, cancellationToken),
            cancellationToken);

        _logger.LogInformation(
            "Rules seed summary: {Seeded} new, {Skipped} already present, {Total} sample definitions ({EnabledCount} enabled in the sample set)",
            seeded,
            skipped,
            samples.Count,
            samples.Count(r => r.Enabled));
    }

    private async Task SeedDevicesAsync(CancellationToken cancellationToken)
    {
        var samples = SampleDevices.Build(DateTime.UtcNow);
        // Stream through existing devices so the seeder doesn't pull a
        // 220 k-row production registry into memory just to build a set.
        var existingIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await foreach (var d in _deviceService.StreamAllDevicesAsync(pageSize: 1000, cancellationToken: cancellationToken))
        {
            existingIds.Add(d.Id);
        }

        _logger.LogInformation(
            "Seeding device inventory (existing: {Existing}); will upsert missing ids only",
            existingIds.Count);

        var (seeded, skipped) = await UpsertMissingAsync(
            samples,
            d => d.Id,
            existingIds,
            d => _deviceService.AddOrUpdateDeviceAsync(d, cancellationToken),
            cancellationToken);

        _logger.LogInformation(
            "Devices seed summary: {Seeded} new, {Skipped} already present, {Total} sample definitions ({Zigbee} Zigbee, {Lora} LoRa, {Ble} BLE, {Wifi} Wi-Fi)",
            seeded,
            skipped,
            samples.Count,
            samples.Count(d => d.Protocol == Protocol.Zigbee),
            samples.Count(d => d.Protocol == Protocol.Lora),
            samples.Count(d => d.Protocol == Protocol.Bluetooth),
            samples.Count(d => d.Protocol == Protocol.Wifi));
    }

    private static HashSet<string> ToCaseInsensitiveSet(IEnumerable<string> ids) =>
        new(ids, StringComparer.OrdinalIgnoreCase);

    private static async Task<(int Seeded, int Skipped)> UpsertMissingAsync<T>(
        IReadOnlyList<T> items,
        Func<T, string> idOf,
        HashSet<string> existingIds,
        Func<T, Task> insert,
        CancellationToken cancellationToken)
    {
        var seeded = 0;
        var skipped = 0;

        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (existingIds.Contains(idOf(item)))
            {
                skipped++;
                continue;
            }

            await insert(item);
            seeded++;
        }

        return (seeded, skipped);
    }
}
