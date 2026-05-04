using System.Text;
using System.Text.Json;
using Hermod.Coordinator.Controllers;
using Hermod.Coordinator.UnitTests.TestUtilities;
using Hermod.Core.Interfaces;
using Hermod.Core.Models;
using Hermod.Core.Models.Rules;
using Hermod.TestInfrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Hermod.Coordinator.UnitTests;

/// <summary>
/// Integration-style tests for <see cref="BackupController"/>. Closes
/// the STATE.md gap for the controller by pinning the export/import
/// contract: export format, exception-message leak guard, import
/// validation, round-trip via in-memory fakes.
///
/// Fakes here implement <see cref="IDeviceService"/> and
/// <see cref="IRulesService"/> with in-memory dicts. No DB, no MQTT.
/// </summary>
public class BackupControllerTests
{
    private static BackupController Build(
        InMemoryDeviceService devices,
        InMemoryRulesService rules,
        IMetricsRepository? metrics = null,
        Microsoft.Extensions.Options.IOptions<Hermod.Core.Configuration.HermodSettings>? settings = null) =>
        new(devices, rules, NullLogger<BackupController>.Instance, metrics, settings);

    [Fact]
    public void BackupController_HasClassLevelAuthorizeAttribute()
        => ControllerAttributeAsserts.AssertHasClassAuthorize<BackupController>();

    [Fact]
    public void BackupController_RequiresAdminRole()
        => ControllerAttributeAsserts.AssertClassAuthorize<BackupController>("admin");

    [Fact]
    public void BackupController_EndpointsDoNotOverrideAuthWithAllowAnonymous()
        => ControllerAttributeAsserts.AssertNoAllowAnonymousOnEndpoints<BackupController>();

    [Fact]
    public void BackupController_ExpectedEndpointMethodsArePresent()
        => ControllerAttributeAsserts.AssertEndpointMethodsPresent<BackupController>(
            "ExportDatabase", "GetExportInfo", "ImportDatabase");

    [Fact]
    public async Task ExportDatabase_EmptyStore_ReturnsFileWithEmptyCollections()
    {
        var sut = Build(new InMemoryDeviceService(), new InMemoryRulesService());

        var result = await sut.ExportDatabase();

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("application/json", file.ContentType);
        Assert.StartsWith("hermod_backup_", file.FileDownloadName);
        Assert.EndsWith(".json", file.FileDownloadName);

        var doc = JsonDocument.Parse(file.FileContents);
        Assert.Equal("3.0", doc.RootElement.GetProperty("version").GetString());
        Assert.Equal("full", doc.RootElement.GetProperty("scope").GetString());
        Assert.Equal(0, doc.RootElement.GetProperty("devices").GetArrayLength());
        Assert.Equal(0, doc.RootElement.GetProperty("rules").GetArrayLength());
        Assert.True(doc.RootElement.TryGetProperty("exportedAt", out _));
    }

    [Fact]
    public async Task ExportDatabase_WithDevicesAndRules_SerializesBoth()
    {
        var devices = new InMemoryDeviceService();
        devices.Devices["light-1"] = new Device
        {
            Id = "light-1",
            Name = "Kitchen Light",
            Protocol = Protocol.Zigbee,
            Status = DeviceStatus.Online
        };
        var rules = new InMemoryRulesService();
        rules.Rules["rule-1"] = new Rule
        {
            Id = "rule-1",
            Name = "Motion turns on light",
            Enabled = true
        };

        var sut = Build(devices, rules);
        var result = await sut.ExportDatabase();
        var file = Assert.IsType<FileContentResult>(result);

        var doc = JsonDocument.Parse(file.FileContents);
        Assert.Equal(1, doc.RootElement.GetProperty("devices").GetArrayLength());
        Assert.Equal(1, doc.RootElement.GetProperty("rules").GetArrayLength());
        // Property naming policy is camelCase.
        Assert.Equal("light-1", doc.RootElement.GetProperty("devices")[0].GetProperty("id").GetString());
        Assert.Equal("rule-1", doc.RootElement.GetProperty("rules")[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task ExportDatabase_DeviceServiceThrows_Returns500WithGenericMessage()
    {
        // Security contract: the controller must NOT echo exception
        // text. The response body says "Export failed" and nothing more.
        var devices = new InMemoryDeviceService { ThrowOnGetAll = new InvalidOperationException("SECRET: connection to db1.internal failed: password=topsecret") };
        var sut = Build(devices, new InMemoryRulesService());

        var result = await sut.ExportDatabase();

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(500, status.StatusCode);
        var payload = JsonSerializer.Serialize(status.Value);
        Assert.Contains("Export failed", payload);
        Assert.DoesNotContain("SECRET", payload);
        Assert.DoesNotContain("password", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("db1.internal", payload);
    }

    [Fact]
    public async Task GetExportInfo_ReturnsCounts()
    {
        var devices = new InMemoryDeviceService();
        devices.Devices["d1"] = new Device { Id = "d1" };
        devices.Devices["d2"] = new Device { Id = "d2" };
        devices.Devices["d3"] = new Device { Id = "d3" };
        var rules = new InMemoryRulesService();
        rules.Rules["r1"] = new Rule { Id = "r1" };
        rules.Rules["r2"] = new Rule { Id = "r2" };

        var sut = Build(devices, rules);
        var result = await sut.GetExportInfo();

        var ok = Assert.IsType<OkObjectResult>(result);
        var info = Assert.IsType<BackupInfo>(ok.Value);
        Assert.Equal(3, info.DeviceCount);
        Assert.Equal(2, info.RuleCount);
        Assert.Equal("hermod_backup.json", info.FileName);
    }

    [Fact]
    public async Task GetExportInfo_RulesServiceThrows_Returns500WithGenericMessage()
    {
        var rules = new InMemoryRulesService { ThrowOnGetAll = new InvalidOperationException("db-password: hunter2") };
        var sut = Build(new InMemoryDeviceService(), rules);

        var result = await sut.GetExportInfo();

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(500, status.StatusCode);
        var payload = JsonSerializer.Serialize(status.Value);
        Assert.Contains("Failed to get info", payload);
        Assert.DoesNotContain("hunter2", payload);
    }

    [Fact]
    public async Task ImportDatabase_NullFile_ReturnsBadRequest()
    {
        var sut = Build(new InMemoryDeviceService(), new InMemoryRulesService());
        var result = await sut.ImportDatabase(null!);
        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("No file uploaded", JsonSerializer.Serialize(bad.Value));
    }

    [Fact]
    public async Task ImportDatabase_EmptyFile_ReturnsBadRequest()
    {
        var sut = Build(new InMemoryDeviceService(), new InMemoryRulesService());
        var file = MakeFormFile(Array.Empty<byte>(), "backup.json");
        var result = await sut.ImportDatabase(file);
        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("No file uploaded", JsonSerializer.Serialize(bad.Value));
    }

    [Fact]
    public async Task ImportDatabase_NonJsonFileName_ReturnsBadRequest()
    {
        var sut = Build(new InMemoryDeviceService(), new InMemoryRulesService());
        var file = MakeFormFile(Encoding.UTF8.GetBytes("{\"version\":\"2.0\"}"), "backup.txt");
        var result = await sut.ImportDatabase(file);
        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("Invalid file type", JsonSerializer.Serialize(bad.Value));
    }

    [Fact]
    public async Task ImportDatabase_GarbledJson_ReturnsBadRequest()
    {
        var sut = Build(new InMemoryDeviceService(), new InMemoryRulesService());
        var file = MakeFormFile(Encoding.UTF8.GetBytes("not json at all {"), "backup.json");
        var result = await sut.ImportDatabase(file);
        var bad = Assert.IsType<BadRequestObjectResult>(result);
        // Either the generic "Failed to import database" (thrown path) or
        // "Invalid backup file format" (null deserialization result).
        var body = JsonSerializer.Serialize(bad.Value);
        Assert.True(body.Contains("Failed to import database") || body.Contains("Invalid backup file format"),
            $"Expected failure message, got: {body}");
    }

    [Fact]
    public async Task ImportDatabase_ValidBackup_PopulatesServices()
    {
        var devices = new InMemoryDeviceService();
        var rules = new InMemoryRulesService();
        var sut = Build(devices, rules);

        var backupJson = JsonSerializer.Serialize(new
        {
            exportedAt = DateTime.UtcNow,
            version = "3.0",
            devices = new[]
            {
                new { id = "imported-1", name = "Imported Device", protocol = Protocol.Wifi, status = DeviceStatus.Online }
            },
            rules = new[]
            {
                new { id = "imported-r1", name = "Imported Rule", enabled = true }
            }
        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        var file = MakeFormFile(Encoding.UTF8.GetBytes(backupJson), "backup.json");
        var result = await sut.ImportDatabase(file);

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = JsonSerializer.Serialize(ok.Value);
        Assert.Contains("Backup imported successfully", payload);
        Assert.Contains("\"devicesImported\":1", payload);
        Assert.Contains("\"rulesImported\":1", payload);

        Assert.Contains("imported-1", devices.Devices);
        Assert.Contains("imported-r1", rules.Rules);
    }

    [Fact]
    public async Task ImportDatabase_EmptyBackupBody_ReturnsBadRequest()
    {
        var sut = Build(new InMemoryDeviceService(), new InMemoryRulesService());
        // "null" deserializes to a null BackupData, which the controller
        // explicitly rejects with "Invalid backup file format".
        var file = MakeFormFile(Encoding.UTF8.GetBytes("null"), "backup.json");
        var result = await sut.ImportDatabase(file);
        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("Invalid backup file format", JsonSerializer.Serialize(bad.Value));
    }

    [Fact]
    public async Task ImportDatabase_MissingDevicesAndRules_ReturnsZeroCounts()
    {
        // Valid backup envelope with no devices and no rules arrays.
        // The controller's null guards (`backup.Devices is not null`)
        // must skip both loops without errors.
        var devices = new InMemoryDeviceService();
        var rules = new InMemoryRulesService();
        var sut = Build(devices, rules);

        var file = MakeFormFile(
            Encoding.UTF8.GetBytes("{\"exportedAt\":\"2026-04-11T00:00:00Z\",\"version\":\"3.0\"}"),
            "backup.json");
        var result = await sut.ImportDatabase(file);

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = JsonSerializer.Serialize(ok.Value);
        Assert.Contains("\"devicesImported\":0", payload);
        Assert.Contains("\"rulesImported\":0", payload);
        Assert.Empty(devices.Devices);
        Assert.Empty(rules.Rules);
    }

    [Fact]
    public async Task ExportDatabase_DevicesScope_OnlyIncludesDevices()
    {
        var devices = new InMemoryDeviceService();
        devices.Devices["d1"] = new Device { Id = "d1", Name = "One", Protocol = Protocol.Wifi };
        var rules = new InMemoryRulesService();
        rules.Rules["r1"] = new Rule { Id = "r1", Name = "R" };

        var sut = Build(devices, rules);
        var result = await sut.ExportDatabase(scope: "devices");
        var file = Assert.IsType<FileContentResult>(result);
        var doc = JsonDocument.Parse(file.FileContents);

        Assert.Equal("devices", doc.RootElement.GetProperty("scope").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("devices").GetArrayLength());
        Assert.False(doc.RootElement.TryGetProperty("rules", out _));
    }

    [Fact]
    public async Task ExportDatabase_RulesScope_OnlyIncludesRules()
    {
        var devices = new InMemoryDeviceService();
        devices.Devices["d1"] = new Device { Id = "d1", Protocol = Protocol.Wifi };
        var rules = new InMemoryRulesService();
        rules.Rules["r1"] = new Rule { Id = "r1", Name = "R" };

        var sut = Build(devices, rules);
        var result = await sut.ExportDatabase(scope: "rules");
        var file = Assert.IsType<FileContentResult>(result);
        var doc = JsonDocument.Parse(file.FileContents);

        Assert.Equal("rules", doc.RootElement.GetProperty("scope").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("rules").GetArrayLength());
        Assert.False(doc.RootElement.TryGetProperty("devices", out _));
    }

    [Fact]
    public async Task ExportDatabase_SettingsScope_IncludesMaskedDatabasePassword()
    {
        var settings = Microsoft.Extensions.Options.Options.Create(new Hermod.Core.Configuration.HermodSettings
        {
            Database = new Hermod.Core.Configuration.DatabaseSettings
            {
                ConnectionString = "Host=db;Port=5432;Database=hermod;Username=u;Password=SECRET",
                Password = "override-secret"
            }
        });

        var sut = Build(new InMemoryDeviceService(), new InMemoryRulesService(), settings: settings);
        var result = await sut.ExportDatabase(scope: "settings");
        var file = Assert.IsType<FileContentResult>(result);
        var body = Encoding.UTF8.GetString(file.FileContents);

        Assert.Contains("\"scope\": \"settings\"", body);
        Assert.DoesNotContain("SECRET", body);
        Assert.DoesNotContain("override-secret", body);
        Assert.Contains("Password=***", body);
    }

    [Fact]
    public async Task ExportDatabase_FullScope_IncludesCountersWhenRepositoryAvailable()
    {
        var metrics = new FakeMetricsRepository(42, 7, 3);

        var sut = Build(new InMemoryDeviceService(), new InMemoryRulesService(), metrics: metrics);
        var result = await sut.ExportDatabase(scope: "full");
        var file = Assert.IsType<FileContentResult>(result);
        var doc = JsonDocument.Parse(file.FileContents);

        var counters = doc.RootElement.GetProperty("metricsCounters");
        Assert.Equal(42, counters.GetProperty("messagesProcessed").GetInt64());
        Assert.Equal(7, counters.GetProperty("rulesExecuted").GetInt64());
        Assert.Equal(3, counters.GetProperty("messagesDropped").GetInt64());
    }

    [Fact]
    public async Task ExportDatabase_UnknownScope_ReturnsBadRequest()
    {
        var sut = Build(new InMemoryDeviceService(), new InMemoryRulesService());
        var result = await sut.ExportDatabase(scope: "garbage");
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task ImportDatabase_WithMetricsCounters_UpsertsThroughRepository()
    {
        var metrics = new FakeMetricsRepository(0, 0, 0);
        var sut = Build(new InMemoryDeviceService(), new InMemoryRulesService(), metrics: metrics);

        var backupJson = JsonSerializer.Serialize(new
        {
            exportedAt = DateTime.UtcNow,
            version = "3.0",
            scope = "full",
            metricsCounters = new { messagesProcessed = 100, rulesExecuted = 50, messagesDropped = 5 }
        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        var file = MakeFormFile(Encoding.UTF8.GetBytes(backupJson), "backup.json");
        var result = await sut.ImportDatabase(file);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(100L, metrics.LastMessagesProcessed);
        Assert.Equal(50L, metrics.LastRulesExecuted);
        Assert.Equal(5L, metrics.LastMessagesDropped);
    }

    [Fact]
    public async Task ExportImport_RoundTrip_Preserves_Core_Fields()
    {
        // Export from one store, import into a fresh store, verify the
        // ids and names make it across. Uses FileContentResult bytes so
        // the whole path is exercised (SerializeToUtf8Bytes on export,
        // DeserializeAsync on import, with identical JsonSerializerOptions).
        var src = new InMemoryDeviceService();
        src.Devices["rt-1"] = new Device { Id = "rt-1", Name = "Roundtrip", Protocol = Protocol.Bluetooth };
        var srcRules = new InMemoryRulesService();
        srcRules.Rules["rt-r"] = new Rule { Id = "rt-r", Name = "Roundtrip Rule", Enabled = true };

        var exporter = Build(src, srcRules);
        var exportResult = Assert.IsType<FileContentResult>(await exporter.ExportDatabase());

        var dest = new InMemoryDeviceService();
        var destRules = new InMemoryRulesService();
        var importer = Build(dest, destRules);

        var file = MakeFormFile(exportResult.FileContents, "backup.json");
        var importResult = await importer.ImportDatabase(file);

        Assert.IsType<OkObjectResult>(importResult);
        Assert.Contains("rt-1", dest.Devices);
        Assert.Equal("Roundtrip", dest.Devices["rt-1"].Name);
        Assert.Contains("rt-r", destRules.Rules);
        Assert.Equal("Roundtrip Rule", destRules.Rules["rt-r"].Name);
    }

    private sealed class FakeMetricsRepository : IMetricsRepository
    {
        public FakeMetricsRepository(long processed, long rules, long dropped)
        {
            LastMessagesProcessed = processed;
            LastRulesExecuted = rules;
            LastMessagesDropped = dropped;
        }

        public long LastMessagesProcessed { get; private set; }
        public long LastRulesExecuted { get; private set; }
        public long LastMessagesDropped { get; private set; }
        public long LastRulesErrored { get; private set; }
        public long LastActionsErrored { get; private set; }
        public List<SystemStats> Snapshots { get; } = new();

        public Task<(long MessagesProcessed, long RulesExecuted, long MessagesDropped,
                     long RulesErrored, long ActionsErrored)?> GetCountersAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<(long, long, long, long, long)?>(
                (LastMessagesProcessed, LastRulesExecuted, LastMessagesDropped, LastRulesErrored, LastActionsErrored));

        public Task UpsertCountersAsync(long messagesProcessed, long rulesExecuted, long messagesDropped,
            long rulesErrored, long actionsErrored, CancellationToken cancellationToken = default)
        {
            LastMessagesProcessed = messagesProcessed;
            LastRulesExecuted = rulesExecuted;
            LastMessagesDropped = messagesDropped;
            LastRulesErrored = rulesErrored;
            LastActionsErrored = actionsErrored;
            return Task.CompletedTask;
        }

        public Task SaveSnapshotAsync(SystemStats stats, CancellationToken cancellationToken = default)
        {
            Snapshots.Add(stats);
            return Task.CompletedTask;
        }

        public Task SaveCountersAndSnapshotAsync(long messagesProcessed, long rulesExecuted, long messagesDropped,
            long rulesErrored, long actionsErrored, SystemStats stats, CancellationToken cancellationToken = default)
        {
            UpsertCountersAsync(messagesProcessed, rulesExecuted, messagesDropped, rulesErrored, actionsErrored, cancellationToken);
            Snapshots.Add(stats);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<SystemStats>> GetRecentSnapshotsAsync(int limit = 100, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SystemStats>>(Snapshots.Take(limit).ToList());

        public Task<double> GetRateOverWindowAsync(TimeSpan window, CancellationToken cancellationToken = default)
            => Task.FromResult(0d);
    }

    private static IFormFile MakeFormFile(byte[] content, string fileName)
    {
        var stream = new MemoryStream(content);
        return new FormFile(stream, 0, content.Length, "file", fileName)
        {
            Headers = new HeaderDictionary(),
            ContentType = "application/json"
        };
    }

}
