using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Hermod.Coordinator.Services;
using Hermod.Core.Configuration;
using Hermod.Core.Interfaces;
using Hermod.Core.Models;
using Hermod.Core.Models.Rules;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Hermod.Coordinator.Controllers;

/// <summary>Admin-only tiered JSON backup / import surface covering devices, rules, metrics and settings.</summary>
[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
[Authorize(Roles = "admin")]
public sealed class BackupController : ControllerBase
{
    private const long MaxImportBytes = 100L * 1024 * 1024;
    private const string BackupVersion = "3.0";
    private const int DefaultSnapshotHistoryLimit = 500;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly IDeviceService _deviceService;
    private readonly IRulesService _rulesService;
    private readonly IMetricsRepository? _metricsRepository;
    private readonly IOptions<HermodSettings>? _hermodSettings;
    private readonly ILogger<BackupController> _logger;

    /// <summary>Creates the backup controller with device/rule services and optional metrics/settings sources.</summary>
    /// <param name="deviceService">Device service used to list and upsert devices.</param>
    /// <param name="rulesService">Rules service used to list and upsert rules.</param>
    /// <param name="logger">Logger for backup lifecycle events.</param>
    /// <param name="metricsRepository">Optional metrics repository; enables counters and snapshot export/import when present.</param>
    /// <param name="hermodSettings">Optional live <see cref="HermodSettings"/>; enables settings slice export.</param>
    public BackupController(
        IDeviceService deviceService,
        IRulesService rulesService,
        ILogger<BackupController> logger,
        IMetricsRepository? metricsRepository = null,
        IOptions<HermodSettings>? hermodSettings = null)
    {
        ArgumentNullException.ThrowIfNull(deviceService);
        ArgumentNullException.ThrowIfNull(rulesService);
        ArgumentNullException.ThrowIfNull(logger);
        _deviceService = deviceService;
        _rulesService = rulesService;
        _logger = logger;
        _metricsRepository = metricsRepository;
        _hermodSettings = hermodSettings;
    }

    private string ActorName => User?.Identity?.Name ?? "unknown";

    /// <summary>
    /// Export a tiered JSON backup. Scope selects which slices are included:
    /// <c>devices</c>, <c>rules</c>, <c>settings</c> (HermodSettings snapshot
    /// with DB password stripped), <c>metrics-history</c> (recent snapshot
    /// rows, opt-in because the table can be large), or <c>full</c>
    /// (devices + rules + lifetime metrics counters). Default is <c>full</c>.
    /// </summary>
    /// <param name="scope">Backup slice name: <c>full</c> (default), <c>devices</c>, <c>rules</c>, <c>settings</c>, or <c>metrics-history</c>.</param>
    /// <param name="cancellationToken">Token to abort the export.</param>
    /// <returns>200 with a downloadable JSON file, 400 on bad scope, 500 on internal failure.</returns>
    [HttpGet("export")]
    [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Top-level export handler must shape every failure into an HTTP response without leaking details.")]
    public async Task<IActionResult> ExportDatabase(
        [FromQuery] string? scope = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseScope(scope, out var parsed, out var scopeError))
        {
            return BadRequest(new { message = scopeError });
        }

        try
        {
            _logger.LogInformation("Exporting database backup (scope={Scope}) by {Actor}", parsed, ActorName);

            await using var buffer = new MemoryStream();
            await using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WriteString("scope", parsed.ToWireName());
                writer.WriteString("exportedAt", DateTime.UtcNow);
                writer.WriteString("version", BackupVersion);

                if (parsed.IncludesDevices())
                {
                    writer.WriteStartArray("devices");
                    await foreach (var device in _deviceService.StreamAllDevicesAsync(cancellationToken: cancellationToken))
                    {
                        JsonSerializer.Serialize(writer, device, JsonOptions);
                    }
                    writer.WriteEndArray();
                }

                if (parsed.IncludesRules())
                {
                    writer.WriteStartArray("rules");
                    foreach (var rule in await _rulesService.GetAllRulesAsync(cancellationToken))
                    {
                        JsonSerializer.Serialize(writer, rule, JsonOptions);
                    }
                    writer.WriteEndArray();
                }

                if (parsed.IncludesMetricsCounters() && _metricsRepository is not null)
                {
                    var counters = await _metricsRepository.GetCountersAsync(cancellationToken);
                    writer.WritePropertyName("metricsCounters");
                    JsonSerializer.Serialize(writer, new BackupMetricsCounters
                    {
                        MessagesProcessed = counters?.MessagesProcessed ?? 0,
                        RulesExecuted = counters?.RulesExecuted ?? 0,
                        MessagesDropped = counters?.MessagesDropped ?? 0,
                        RulesErrored = counters?.RulesErrored ?? 0,
                        ActionsErrored = counters?.ActionsErrored ?? 0
                    }, JsonOptions);
                }

                if (parsed.IncludesMetricsHistory() && _metricsRepository is not null)
                {
                    writer.WriteStartArray("metricsSnapshots");
                    var snapshots = await _metricsRepository.GetRecentSnapshotsAsync(DefaultSnapshotHistoryLimit, cancellationToken);
                    foreach (var snapshot in snapshots)
                    {
                        JsonSerializer.Serialize(writer, snapshot, JsonOptions);
                    }
                    writer.WriteEndArray();
                }

                if (parsed.IncludesSettings() && _hermodSettings is not null)
                {
                    writer.WritePropertyName("settings");
                    JsonSerializer.Serialize(writer, BuildSettingsSnapshot(_hermodSettings.Value), JsonOptions);
                }

                writer.WriteEndObject();
            }

            var fileName = $"hermod_backup_{parsed.ToWireName()}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json";
            return File(buffer.ToArray(), "application/json", fileName);
        }
        catch (OperationCanceledException)
        {
            return StatusCode(499, new { message = "Export cancelled" });
        }
        catch (Exception ex)
        {
            // Never echo ex.Message to the client: it can leak SQL fragments,
            // paths, and connection-string bits.
            _logger.LogError(ex, "Failed to export database backup");
            return StatusCode(500, new { message = "Export failed" });
        }
    }

    /// <summary>Get export metadata without downloading.</summary>
    /// <param name="cancellationToken">Token to abort the call.</param>
    /// <returns>200 with device/rule counts and timestamp, 500 if collection fails.</returns>
    [HttpGet("export/info")]
    [ProducesResponseType(typeof(BackupInfo), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Top-level info handler must return 500 without leaking backing-store internals.")]
    public async Task<IActionResult> GetExportInfo(CancellationToken cancellationToken = default)
    {
        try
        {
            var counts = await _deviceService.GetCountsAsync(cancellationToken);
            var rules = await _rulesService.GetAllRulesAsync(cancellationToken);

            return Ok(new BackupInfo
            {
                FileName = "hermod_backup.json",
                DeviceCount = counts.Total,
                RuleCount = rules.Count(),
                LastModified = DateTime.UtcNow
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get export info");
            return StatusCode(500, new { message = "Failed to get info" });
        }
    }

    /// <summary>
    /// Import a tiered backup. Auto-detects which slices are present
    /// (<c>devices</c>, <c>rules</c>, <c>metricsCounters</c>,
    /// <c>metricsSnapshots</c>) and upserts each. <c>settings</c> is
    /// export-only and silently skipped on import.
    /// </summary>
    /// <param name="file">Uploaded .json backup file.</param>
    /// <param name="cancellationToken">Token to abort the import.</param>
    /// <returns>200 with per-slice counts, 400 on malformed/missing file.</returns>
    [HttpPost("import")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [RequestSizeLimit(MaxImportBytes)]
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Top-level import handler shapes all failures into a 400 without leaking backing-store details.")]
    public async Task<IActionResult> ImportDatabase(IFormFile? file, CancellationToken cancellationToken = default)
    {
        if (file is null || file.Length == 0)
        {
            return BadRequest(new { message = "No file uploaded" });
        }

        if (!file.FileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new { message = "Invalid file type. Please upload a .json backup file" });
        }

        try
        {
            await using var stream = file.OpenReadStream();
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return BadRequest(new { message = "Invalid backup file format" });
            }

            var root = doc.RootElement;

            // Reject backups with a different schema version up front. Without
            // this, a future-version backup imports "partially": the device
            // rows map, a new field on the rule row silently drops, and the
            // operator gets a success response for a half-right upsert that
            // cannot be undone. Missing version means pre-3.0 — accept with
            // a warning, since older backups are schema-compatible for the
            // tiered slices we upsert.
            if (root.TryGetProperty("version", out var versionNode) && versionNode.ValueKind == JsonValueKind.String)
            {
                var version = versionNode.GetString();
                if (!string.Equals(version, BackupVersion, StringComparison.Ordinal))
                {
                    _logger.LogWarning(
                        "Rejected import by {Actor}: backup version '{Version}' does not match expected '{Expected}'",
                        ActorName, version, BackupVersion);
                    return BadRequest(new
                    {
                        message = $"Backup version '{version}' is incompatible with this coordinator (expected '{BackupVersion}')."
                    });
                }
            }
            else
            {
                _logger.LogWarning(
                    "Importing unversioned backup by {Actor}; assumed compatible with {Expected}",
                    ActorName, BackupVersion);
            }

            var devicesImported = 0;
            var rulesImported = 0;
            var countersApplied = false;
            var snapshotsImported = 0;

            if (root.TryGetProperty("devices", out var devicesNode) && devicesNode.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in devicesNode.EnumerateArray())
                {
                    var device = element.Deserialize<Device>(JsonOptions);
                    if (device is null || string.IsNullOrEmpty(device.Id)) continue;
                    await _deviceService.AddOrUpdateDeviceAsync(device, cancellationToken);
                    devicesImported++;
                }
            }

            if (root.TryGetProperty("rules", out var rulesNode) && rulesNode.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in rulesNode.EnumerateArray())
                {
                    var rule = element.Deserialize<Rule>(JsonOptions);
                    if (rule is null || string.IsNullOrEmpty(rule.Id)) continue;
                    await _rulesService.AddOrUpdateRuleAsync(rule, cancellationToken);
                    rulesImported++;
                }
            }

            if (_metricsRepository is not null
                && root.TryGetProperty("metricsCounters", out var countersNode)
                && countersNode.ValueKind == JsonValueKind.Object)
            {
                var counters = countersNode.Deserialize<BackupMetricsCounters>(JsonOptions);
                if (counters is not null)
                {
                    await _metricsRepository.UpsertCountersAsync(
                        counters.MessagesProcessed,
                        counters.RulesExecuted,
                        counters.MessagesDropped,
                        counters.RulesErrored,
                        counters.ActionsErrored,
                        cancellationToken);
                    countersApplied = true;
                }
            }

            if (_metricsRepository is not null
                && root.TryGetProperty("metricsSnapshots", out var snapshotsNode)
                && snapshotsNode.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in snapshotsNode.EnumerateArray())
                {
                    var snapshot = element.Deserialize<SystemStats>(JsonOptions);
                    if (snapshot is null) continue;
                    await _metricsRepository.SaveSnapshotAsync(snapshot, cancellationToken);
                    snapshotsImported++;
                }
            }

            // Warning, not Information: import mutates production data
            // irreversibly. Worth a louder audit trail than a routine event.
            _logger.LogWarning(
                "Imported backup by {Actor} — devices: {Devices}, rules: {Rules}, counters: {Counters}, snapshots: {Snapshots}",
                ActorName, devicesImported, rulesImported, countersApplied, snapshotsImported);

            return Ok(new
            {
                message = "Backup imported successfully",
                devicesImported,
                rulesImported,
                countersApplied,
                snapshotsImported
            });
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Rejected import: malformed JSON");
            return BadRequest(new { message = "Failed to import database" });
        }
        catch (OperationCanceledException)
        {
            return BadRequest(new { message = "Import cancelled" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to import database");
            return BadRequest(new { message = "Failed to import database" });
        }
    }

    private static bool TryParseScope(string? raw, out BackupScope scope, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            scope = BackupScope.Full;
            return true;
        }

        var trimmed = raw.Trim();
        if (string.Equals(trimmed, "full", StringComparison.OrdinalIgnoreCase)) { scope = BackupScope.Full; return true; }
        if (string.Equals(trimmed, "devices", StringComparison.OrdinalIgnoreCase)) { scope = BackupScope.Devices; return true; }
        if (string.Equals(trimmed, "rules", StringComparison.OrdinalIgnoreCase)) { scope = BackupScope.Rules; return true; }
        if (string.Equals(trimmed, "settings", StringComparison.OrdinalIgnoreCase)) { scope = BackupScope.Settings; return true; }
        if (string.Equals(trimmed, "metrics-history", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "metricshistory", StringComparison.OrdinalIgnoreCase))
        {
            scope = BackupScope.MetricsHistory;
            return true;
        }

        scope = BackupScope.Full;
        error = $"Unknown scope '{raw}'. Valid: full, devices, rules, settings, metrics-history.";
        return false;
    }

    private static BackupSettings BuildSettingsSnapshot(HermodSettings live)
    {
        // Strip the DB password so operators can share config backups without
        // leaking the credential. The connection string itself is masked via
        // ConnectionStringMasker, and any populated override password is dropped.
        var database = new DatabaseSettings
        {
            ConnectionString = ConnectionStringMasker.Mask(live.Database.ConnectionString),
            DatabaseName = live.Database.DatabaseName,
            Password = null
        };

        return new BackupSettings
        {
            Mqtt = live.Mqtt,
            Database = database,
            ProtocolTranslators = live.ProtocolTranslators,
            Metrics = live.Metrics,
            Auth = live.Auth
        };
    }
}

/// <summary>Slice of the database the backup controller exports or imports.</summary>
public enum BackupScope
{
    /// <summary>Devices + rules + metrics counters (default).</summary>
    Full,

    /// <summary>Device records only.</summary>
    Devices,

    /// <summary>Rule definitions only.</summary>
    Rules,

    /// <summary>HermodSettings snapshot (DB password stripped). Export-only.</summary>
    Settings,

    /// <summary>Recent metrics snapshot rows (opt-in because the table can be large).</summary>
    MetricsHistory
}

internal static class BackupScopeExtensions
{
    public static bool IncludesDevices(this BackupScope s) => s is BackupScope.Full or BackupScope.Devices;
    public static bool IncludesRules(this BackupScope s) => s is BackupScope.Full or BackupScope.Rules;
    public static bool IncludesMetricsCounters(this BackupScope s) => s == BackupScope.Full;
    public static bool IncludesMetricsHistory(this BackupScope s) => s == BackupScope.MetricsHistory;
    public static bool IncludesSettings(this BackupScope s) => s is BackupScope.Settings or BackupScope.Full;

    // Wire format is lowercase kebab-case filename slug, legitimately case-narrowing.
    [SuppressMessage("Globalization", "CA1308:Normalize strings to uppercase", Justification = "Value is a public file-name slug; lowercase is the wire format.")]
    public static string ToWireName(this BackupScope s) => s switch
    {
        BackupScope.MetricsHistory => "metrics-history",
        _ => s.ToString().ToLowerInvariant()
    };
}

/// <summary>Summary returned by the backup info endpoint.</summary>
public sealed class BackupInfo
{
    /// <summary>Suggested file name for downloads.</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>Number of devices currently in the system.</summary>
    public int DeviceCount { get; set; }

    /// <summary>Number of rules currently in the system.</summary>
    public int RuleCount { get; set; }

    /// <summary>Timestamp the info was computed at.</summary>
    public DateTime LastModified { get; set; }
}

/// <summary>Lifetime counters included in a full backup.</summary>
public sealed class BackupMetricsCounters
{
    /// <summary>Rules that raised an error during evaluation.</summary>
    public long RulesErrored { get; set; }

    /// <summary>Actions that raised an error during execution.</summary>
    public long ActionsErrored { get; set; }

    /// <summary>MQTT messages successfully processed.</summary>
    public long MessagesProcessed { get; set; }

    /// <summary>Rules that matched and executed successfully.</summary>
    public long RulesExecuted { get; set; }

    /// <summary>MQTT messages dropped (queue full, unroutable, etc.).</summary>
    public long MessagesDropped { get; set; }
}

/// <summary>Settings snapshot embedded in a <c>settings</c>-scoped backup (DB password stripped).</summary>
public sealed class BackupSettings
{
    /// <summary>MQTT broker settings slice.</summary>
    public MqttSettings? Mqtt { get; set; }

    /// <summary>Database connection settings (password dropped, connection string masked).</summary>
    public DatabaseSettings? Database { get; set; }

    /// <summary>Protocol translator (Zigbee, LoRa, ...) configuration slice.</summary>
    public ProtocolTranslatorsSettings? ProtocolTranslators { get; set; }

    /// <summary>Metrics collector configuration slice.</summary>
    public MetricsSettings? Metrics { get; set; }

    /// <summary>Vault42 auth configuration slice.</summary>
    public AuthSettings? Auth { get; set; }
}
