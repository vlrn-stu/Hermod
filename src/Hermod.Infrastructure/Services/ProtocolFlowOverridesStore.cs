using System.Collections.Concurrent;
using Hermod.Core.Configuration;
using Hermod.Core.Models;
using Hermod.Infrastructure.Database;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Hermod.Infrastructure.Services;

/// <inheritdoc cref="IProtocolFlowOverridesStore"/>
/// <summary>
/// In-memory cache fronting the <c>rate_limit_protocol_overrides</c>
/// Postgres table. Same persistence contract as
/// <see cref="RateLimitOverridesStore"/>: hydrate on boot, write through
/// before mutating cache, hot-path reads stay synchronous and in-memory.
/// </summary>
public sealed class ProtocolFlowOverridesStore : IProtocolFlowOverridesStore
{
    private readonly ConcurrentDictionary<Protocol, ProtocolFlowOverride> _overrides = new();
    private readonly PostgresConnectionFactory? _db;
    private readonly ILogger<ProtocolFlowOverridesStore>? _logger;

    /// <summary>Creates a Postgres-backed store. Null <paramref name="db"/> = no persistence (test harnesses).</summary>
    public ProtocolFlowOverridesStore(
        PostgresConnectionFactory? db = null,
        ILogger<ProtocolFlowOverridesStore>? logger = null)
    {
        _db = db;
        _logger = logger;
    }

    /// <inheritdoc/>
    public IReadOnlyDictionary<Protocol, ProtocolFlowOverride> Snapshot()
        => _overrides.ToDictionary(kv => kv.Key, kv => Clone(kv.Value));

    /// <inheritdoc/>
    public ProtocolFlowOverride? TryGet(Protocol protocol)
        => _overrides.TryGetValue(protocol, out var value) ? Clone(value) : null;

    /// <inheritdoc/>
    public async Task SetAsync(Protocol protocol, ProtocolFlowOverride value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (_db is not null)
        {
            await using var conn = _db.CreateConnection();
            await conn.OpenAsync(cancellationToken);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO rate_limit_protocol_overrides (protocol, ingress_rate, ingress_burst, egress_rate, egress_burst, updated_at)
                VALUES (@protocol, @ir, @ib, @er, @eb, NOW())
                ON CONFLICT (protocol) DO UPDATE SET
                    ingress_rate = EXCLUDED.ingress_rate,
                    ingress_burst = EXCLUDED.ingress_burst,
                    egress_rate = EXCLUDED.egress_rate,
                    egress_burst = EXCLUDED.egress_burst,
                    updated_at = NOW();
                """;
            cmd.Parameters.AddWithValue("@protocol", (int)protocol);
            cmd.Parameters.AddWithValue("@ir", value.IngressRatePerSecond);
            cmd.Parameters.AddWithValue("@ib", value.IngressBurst);
            cmd.Parameters.AddWithValue("@er", value.EgressRatePerSecond);
            cmd.Parameters.AddWithValue("@eb", value.EgressBurst);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        _overrides[protocol] = Clone(value);
    }

    /// <inheritdoc/>
    public async Task<bool> RemoveAsync(Protocol protocol, CancellationToken cancellationToken = default)
    {
        if (_db is not null)
        {
            await using var conn = _db.CreateConnection();
            await conn.OpenAsync(cancellationToken);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM rate_limit_protocol_overrides WHERE protocol = @protocol";
            cmd.Parameters.AddWithValue("@protocol", (int)protocol);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        return _overrides.TryRemove(protocol, out _);
    }

    /// <inheritdoc/>
    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        if (_db is not null)
        {
            await using var conn = _db.CreateConnection();
            await conn.OpenAsync(cancellationToken);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "TRUNCATE TABLE rate_limit_protocol_overrides";
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        _overrides.Clear();
    }

    /// <inheritdoc/>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (_db is null) return;
        try
        {
            await using var conn = _db.CreateConnection();
            await conn.OpenAsync(cancellationToken);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT protocol, ingress_rate, ingress_burst, egress_rate, egress_burst FROM rate_limit_protocol_overrides";
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            var loaded = 0;
            while (await reader.ReadAsync(cancellationToken))
            {
                var protocol = (Protocol)reader.GetInt32(0);
                _overrides[protocol] = new ProtocolFlowOverride
                {
                    IngressRatePerSecond = reader.GetDouble(1),
                    IngressBurst = reader.GetInt32(2),
                    EgressRatePerSecond = reader.GetDouble(3),
                    EgressBurst = reader.GetInt32(4),
                };
                loaded++;
            }
            _logger?.LogInformation("Hydrated {Count} per-protocol flow-limit overrides from Postgres", loaded);
        }
        catch (NpgsqlException ex)
        {
            _logger?.LogWarning(ex, "Failed to hydrate per-protocol flow overrides; cache stays empty");
        }
    }

    private static ProtocolFlowOverride Clone(ProtocolFlowOverride source) => new()
    {
        IngressRatePerSecond = source.IngressRatePerSecond,
        IngressBurst = source.IngressBurst,
        EgressRatePerSecond = source.EgressRatePerSecond,
        EgressBurst = source.EgressBurst,
    };
}
