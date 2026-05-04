using System.Collections.Concurrent;
using System.Text.Json;
using Dapper;
using Hermod.Core.Configuration;
using Hermod.Core.Interfaces;
using Hermod.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hermod.Infrastructure.Database;

/// <summary>
/// PostgreSQL-backed implementation of <see cref="IDeviceService"/>. Every
/// mutation is a single-statement SQL op; all JSON columns are merged
/// server-side via JSONB <c>||</c> so concurrent state pushes never lose a
/// field. Optional <c>Storage.SkipDeviceExistenceCheck</c> elides the
/// pre-read on the upsert path for benchmarking.
/// </summary>
public sealed class PostgresDeviceService : IDeviceService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private const string UpsertDeviceSql = """
        INSERT INTO devices (id, name, protocol, status, manufacturer, model, firmware_version,
                             capabilities, state, last_seen, created_at, updated_at)
        VALUES (@Id, @Name, @Protocol, @Status, @Manufacturer, @Model, @FirmwareVersion,
                @Capabilities::jsonb, @State::jsonb, @LastSeen, @CreatedAt, @UpdatedAt)
        ON CONFLICT(id) DO UPDATE SET
            name = EXCLUDED.name,
            protocol = EXCLUDED.protocol,
            status = EXCLUDED.status,
            manufacturer = EXCLUDED.manufacturer,
            model = EXCLUDED.model,
            firmware_version = EXCLUDED.firmware_version,
            capabilities = EXCLUDED.capabilities,
            state = EXCLUDED.state,
            last_seen = EXCLUDED.last_seen,
            updated_at = EXCLUDED.updated_at
        """;

    private readonly PostgresConnectionFactory _connectionFactory;
    private readonly ILogger<PostgresDeviceService> _logger;
    private readonly bool _skipExistenceCheck;

    /// <summary>
    /// Creates a device service bound to the supplied connection factory.
    /// The <c>Storage.SkipDeviceExistenceCheck</c> flag trades the pre-read
    /// diagnostic log line for one fewer round-trip on the upsert path.
    /// </summary>
    public PostgresDeviceService(
        PostgresConnectionFactory connectionFactory,
        IOptions<HermodSettings> settings,
        ILogger<PostgresDeviceService> logger)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);
        _connectionFactory = connectionFactory;
        _logger = logger;
        _skipExistenceCheck = settings.Value.Storage.SkipDeviceExistenceCheck;
    }

    /// <inheritdoc/>
    public event EventHandler<DeviceAvailabilityChangedEventArgs>? AvailabilityChanged;

    /// <inheritdoc/>
    public async IAsyncEnumerable<Device> StreamAllDevicesAsync(
        int pageSize = 500,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        var safePageSize = Math.Clamp(pageSize, 1, 1000);
        var offset = 0;
        while (true)
        {
            var page = await GetDevicesPageAsync(offset, safePageSize, filter: null, protocol: null, cancellationToken: cancellationToken);
            foreach (var d in page.Items) yield return d;
            if (page.Items.Count < page.Limit) yield break;
            offset += page.Items.Count;
            if (offset >= page.Total) yield break;
        }
    }

    /// <inheritdoc/>
    public async Task<DevicePage> GetDevicesPageAsync(
        int offset,
        int limit,
        string? filter = null,
        Protocol? protocol = null,
        CancellationToken cancellationToken = default)
    {
        // Clamp to protect the coordinator RAM: a client asking
        // limit=1_000_000 would reintroduce the OOM this method exists to avoid.
        var safeOffset = Math.Max(0, offset);
        var safeLimit = Math.Clamp(limit, 1, 1000);
        var likePattern = string.IsNullOrWhiteSpace(filter) ? null : $"%{filter.Trim()}%";
        var protocolInt = protocol.HasValue ? (int?)(int)protocol.Value : null;

        // Parameters left at null become no-op predicates (the COALESCE-on-null
        // turns each filter into identity when that filter isn't requested), so
        // one SQL string covers every combination of (free-text, protocol).
        const string listSql = """
            SELECT * FROM devices
            WHERE (@Like::text IS NULL OR id ILIKE @Like OR name ILIKE @Like)
              AND (@Protocol::int IS NULL OR protocol = @Protocol)
            ORDER BY last_seen DESC NULLS LAST, id
            LIMIT @Limit OFFSET @Offset
            """;
        const string countSql = """
            SELECT COUNT(*)::int FROM devices
            WHERE (@Like::text IS NULL OR id ILIKE @Like OR name ILIKE @Like)
              AND (@Protocol::int IS NULL OR protocol = @Protocol)
            """;

        await using var conn = _connectionFactory.CreateConnection();

        var rows = await conn.QueryAsync<DeviceRow>(new CommandDefinition(
            listSql,
            new { Limit = safeLimit, Offset = safeOffset, Like = likePattern, Protocol = protocolInt },
            cancellationToken: cancellationToken));

        var total = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            countSql,
            new { Like = likePattern, Protocol = protocolInt },
            cancellationToken: cancellationToken));

        var items = rows.Select(MapToDevice).ToList();
        return new DevicePage(items, total, safeOffset, safeLimit);
    }

    /// <inheritdoc/>
    public async Task<DeviceCounts> GetCountsAsync(CancellationToken cancellationToken = default)
    {
        // Two aggregations in a single round-trip: the totals row and one
        // row per protocol. The snake_case aliases let Dapper map into
        // DeviceCountRow without reflecting on the anonymous type.
        const string sql = """
            SELECT 'total' AS bucket, -1 AS protocol, COUNT(*)::int AS cnt
                FROM devices
            UNION ALL
            SELECT 'online' AS bucket, -1 AS protocol, COUNT(*)::int AS cnt
                FROM devices WHERE status = 1
            UNION ALL
            SELECT 'by_protocol' AS bucket, protocol, COUNT(*)::int AS cnt
                FROM devices GROUP BY protocol;
            """;

        await using var conn = _connectionFactory.CreateConnection();
        var rows = (await conn.QueryAsync<CountRow>(new CommandDefinition(
            sql, cancellationToken: cancellationToken))).ToList();

        var total = 0;
        var online = 0;
        var byProtocol = new Dictionary<Protocol, int>();
        foreach (var r in rows)
        {
            switch (r.Bucket)
            {
                case "total": total = r.Cnt; break;
                case "online": online = r.Cnt; break;
                case "by_protocol":
                    var p = (Protocol)r.Protocol;
                    if (p != Protocol.Unknown) byProtocol[p] = r.Cnt;
                    break;
            }
        }
        return new DeviceCounts(total, online, byProtocol);
    }

    private sealed class CountRow
    {
        public string Bucket { get; set; } = string.Empty;
        public int Protocol { get; set; }
        public int Cnt { get; set; }
    }

    /// <inheritdoc/>
    public async Task<Device?> GetDeviceAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var conn = _connectionFactory.CreateConnection();
        var row = await conn.QuerySingleOrDefaultAsync<DeviceRow>(new CommandDefinition(
            "SELECT * FROM devices WHERE id = @Id",
            new { Id = id },
            cancellationToken: cancellationToken));
        return row is null ? null : MapToDevice(row);
    }

    /// <inheritdoc/>
    public async Task<Device> AddOrUpdateDeviceAsync(Device device, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        var now = DateTime.UtcNow;
        device.UpdatedAt = now;

        if (!_skipExistenceCheck)
        {
            // Legacy path: read before write so we can log a distinct
            // "adding new device" line the first time an id appears. Kept
            // behind a flag so profiles can measure the extra round-trip.
            var existing = await GetDeviceAsync(device.Id, cancellationToken);
            if (existing is null)
            {
                device.CreatedAt = now;
                _logger.LogInformation("Adding new device: {DeviceId} ({Protocol})", device.Id, device.Protocol);
            }
        }
        else if (device.CreatedAt == default)
        {
            // Fast path: skip the pre-read entirely. The UPSERT's ON CONFLICT
            // branch ignores created_at on update via the SET list, so it's
            // safe to stamp NOW() here; the DB keeps the original on an
            // existing row. Callers that already populated CreatedAt keep it.
            device.CreatedAt = now;
        }

        await using var conn = _connectionFactory.CreateConnection();
        await conn.ExecuteAsync(new CommandDefinition(
            UpsertDeviceSql,
            new
            {
                device.Id,
                device.Name,
                Protocol = (int)device.Protocol,
                Status = (int)device.Status,
                device.Manufacturer,
                device.Model,
                device.FirmwareVersion,
                Capabilities = JsonSerializer.Serialize(device.Capabilities, JsonOptions),
                State = JsonSerializer.Serialize(device.State, JsonOptions),
                device.LastSeen,
                device.CreatedAt,
                device.UpdatedAt
            },
            cancellationToken: cancellationToken));

        return device;
    }

    /// <inheritdoc/>
    public async Task<bool> RemoveDeviceAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var conn = _connectionFactory.CreateConnection();
        var affected = await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM devices WHERE id = @Id",
            new { Id = id },
            cancellationToken: cancellationToken));

        if (affected <= 0) return false;
        _logger.LogInformation("Removed device: {DeviceId}", id);
        return true;
    }

    /// <inheritdoc/>
    public async Task<bool> RenameDeviceAsync(string oldId, string newId, CancellationToken cancellationToken = default)
    {
        if (string.Equals(oldId, newId, StringComparison.Ordinal)) return true;

        await using var conn = _connectionFactory.CreateConnection();
        await conn.OpenAsync(cancellationToken);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken);

        try
        {
            // Lookup both sides in one query so we can distinguish "genuine
            // clash" from "race with the bridge-devices reconciliation loop,
            // which already moved the row for us".
            var counts = await conn.QuerySingleAsync<(int Old, int New)>(new CommandDefinition(
                """
                SELECT
                    COUNT(*) FILTER (WHERE id = @OldId) AS "Old",
                    COUNT(*) FILTER (WHERE id = @NewId) AS "New"
                FROM devices
                WHERE id IN (@OldId, @NewId);
                """,
                new { OldId = oldId, NewId = newId },
                transaction: tx,
                cancellationToken: cancellationToken));

            if (counts.New > 0)
            {
                await tx.RollbackAsync(cancellationToken);
                if (counts.Old == 0)
                {
                    // Idempotent success: someone (almost certainly the
                    // z2m bridge-devices reconciler) already renamed the
                    // row to newId while the caller's rename was mid-
                    // flight. Old id is gone, new id is present — the
                    // end state is exactly what the caller wanted.
                    _logger.LogDebug(
                        "Rename {Old} -> {New}: already applied by reconciliation",
                        oldId, newId);
                    return true;
                }
                _logger.LogWarning("Rename refused: device {NewId} already exists", newId);
                return false;
            }

            if (counts.Old == 0)
            {
                await tx.RollbackAsync(cancellationToken);
                return false;
            }

            // Single-statement PK update; bump Name to match the new id so the
            // dashboard label tracks immediately, and stamp updated_at.
            var affected = await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE devices SET id = @NewId, name = @NewId, updated_at = @Now WHERE id = @OldId",
                new { OldId = oldId, NewId = newId, Now = DateTime.UtcNow },
                transaction: tx,
                cancellationToken: cancellationToken));

            if (affected <= 0)
            {
                await tx.RollbackAsync(cancellationToken);
                return false;
            }

            await tx.CommitAsync(cancellationToken);
            _logger.LogInformation("Renamed device {OldId} -> {NewId}", oldId, newId);
            return true;
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<IEnumerable<Device>> GetDevicesByProtocolAsync(Protocol protocol, CancellationToken cancellationToken = default)
    {
        await using var conn = _connectionFactory.CreateConnection();
        var rows = await conn.QueryAsync<DeviceRow>(new CommandDefinition(
            "SELECT * FROM devices WHERE protocol = @Protocol ORDER BY last_seen DESC NULLS LAST",
            new { Protocol = (int)protocol },
            cancellationToken: cancellationToken));
        return rows.Select(MapToDevice).ToList();
    }

    /// <inheritdoc/>
    public async Task UpdateDeviceStateAsync(string deviceId, Dictionary<string, object> state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        // Single-statement JSONB merge. A read-modify-write split across two
        // connections loses concurrent updates on the MQTT hot path.
        await using var conn = _connectionFactory.CreateConnection();
        await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE devices
            SET state = COALESCE(state, '{}'::jsonb) || @Delta::jsonb,
                last_seen = @Now,
                status = @Status,
                updated_at = @Now
            WHERE id = @Id
            """,
            new
            {
                Id = deviceId,
                Delta = JsonSerializer.Serialize(state, JsonOptions),
                Now = DateTime.UtcNow,
                Status = (int)DeviceStatus.Online
            },
            cancellationToken: cancellationToken));
    }

    /// <inheritdoc/>
    public async Task UpsertDeviceStateAsync(
        string deviceId,
        string name,
        Protocol protocol,
        Dictionary<string, object> state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        // One INSERT with ON CONFLICT that merges the inbound JSONB delta
        // into the existing state column. Zero reads, zero client-side
        // merge logic — the server does both the existence check and the
        // delta application in a single statement.
        var now = DateTime.UtcNow;
        var delta = JsonSerializer.Serialize(state, JsonOptions);

        await using var conn = _connectionFactory.CreateConnection();
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO devices (
                id, name, protocol, status, capabilities, state,
                last_seen, created_at, updated_at)
            VALUES (
                @Id, @Name, @Protocol, @Status, '{}'::jsonb, @Delta::jsonb,
                @Now, @Now, @Now)
            ON CONFLICT (id) DO UPDATE SET
                state      = COALESCE(devices.state, '{}'::jsonb) || EXCLUDED.state,
                last_seen  = EXCLUDED.last_seen,
                status     = EXCLUDED.status,
                updated_at = EXCLUDED.updated_at;
            """,
            new
            {
                Id = deviceId,
                Name = name,
                Protocol = (int)protocol,
                Status = (int)DeviceStatus.Online,
                Delta = delta,
                Now = now,
            },
            cancellationToken: cancellationToken));
    }

    /// <inheritdoc/>
    public async Task UpdateDeviceStatusAsync(string deviceId, DeviceStatus status, CancellationToken cancellationToken = default)
    {
        var previous = await GetDeviceAsync(deviceId, cancellationToken);
        var previousStatus = previous?.Status ?? DeviceStatus.Unknown;

        // last_seen only advances on a real Online signal; resetting it on an
        // Offline transition would misreport time-since-last-contact.
        var sql = status == DeviceStatus.Online
            ? "UPDATE devices SET status = @Status, last_seen = @Now, updated_at = @Now WHERE id = @Id"
            : "UPDATE devices SET status = @Status, updated_at = @Now WHERE id = @Id";

        await using var conn = _connectionFactory.CreateConnection();
        var affected = await conn.ExecuteAsync(new CommandDefinition(
            sql,
            new { Id = deviceId, Status = (int)status, Now = DateTime.UtcNow },
            cancellationToken: cancellationToken));

        // Don't raise a transition event when the UPDATE no-ops because the
        // device isn't in the DB. Without this guard a caller that passes a
        // stale id would push a ghost transition into subscribers.
        if (affected > 0 && previousStatus != status)
        {
            RaiseAvailabilityChanged(deviceId, previous, previousStatus, status);
        }
    }

    private void RaiseAvailabilityChanged(string deviceId, Device? previous, DeviceStatus previousStatus, DeviceStatus currentStatus)
    {
        var topic = previous is null
            ? $"availability/{deviceId}"
            : $"availability/{previous.Protocol.ToTopicPrefix()}/{deviceId}";

        AvailabilityChanged?.Invoke(this, new DeviceAvailabilityChangedEventArgs
        {
            DeviceId = deviceId,
            PreviousStatus = previousStatus,
            CurrentStatus = currentStatus,
            Device = previous,
            Topic = topic,
        });
    }

    private Device MapToDevice(DeviceRow row) => new()
    {
        Id = row.Id,
        Name = row.Name,
        Protocol = (Protocol)row.Protocol,
        Status = (DeviceStatus)row.Status,
        Manufacturer = row.Manufacturer,
        Model = row.Model,
        FirmwareVersion = row.Firmware_Version,
        // Wrap in ConcurrentDictionary so live-path readers (Blazor render)
        // and writers (MessageProcessor) cannot collide on the same instance.
        Capabilities = WrapConcurrent(row.Capabilities, $"capabilities of device {row.Id}"),
        State = WrapConcurrent(row.State, $"state of device {row.Id}"),
        // NULL => MinValue (not UtcNow) so `!= default` and UtcNow-diff
        // consumers treat the device as never-seen, not just-seen.
        LastSeen = row.Last_Seen ?? DateTime.MinValue,
        CreatedAt = row.Created_At,
        UpdatedAt = row.Updated_At
    };

    private ConcurrentDictionary<string, object> WrapConcurrent(string? json, string field)
    {
        var source = DapperJsonColumn.Deserialize<Dictionary<string, object>>(json, JsonOptions, _logger, field);
        return source is null
            ? new ConcurrentDictionary<string, object>()
            : new ConcurrentDictionary<string, object>(source);
    }

    private sealed class DeviceRow
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public int Protocol { get; set; }
        public int Status { get; set; }
        public string? Manufacturer { get; set; }
        public string? Model { get; set; }
        public string? Firmware_Version { get; set; }
        public string? Capabilities { get; set; }
        public string? State { get; set; }
        public DateTime? Last_Seen { get; set; }
        public DateTime Created_At { get; set; }
        public DateTime Updated_At { get; set; }
    }
}
