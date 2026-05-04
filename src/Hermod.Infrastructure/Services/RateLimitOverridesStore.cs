using System.Collections.Concurrent;
using Hermod.Core.Configuration;
using Hermod.Infrastructure.Database;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Hermod.Infrastructure.Services;

/// <inheritdoc cref="IRateLimitOverridesStore"/>
/// <summary>
/// In-memory cache fronting the <c>rate_limit_topic_overrides</c> Postgres
/// table. <see cref="LoadAsync"/> hydrates the cache from disk on coord
/// boot; <see cref="SetAsync"/> and <see cref="RemoveAsync"/> write through
/// to Postgres before mutating the cache so a power-cut between the two
/// can't leave a stale in-memory entry. Reads (<see cref="Snapshot"/>,
/// <see cref="TryGet"/>) are hot-path-safe — they hit the cache only.
/// </summary>
public sealed class RateLimitOverridesStore : IRateLimitOverridesStore
{
    private readonly ConcurrentDictionary<string, TopicRateOverride> _overrides = new(StringComparer.Ordinal);
    private readonly PostgresConnectionFactory? _db;
    private readonly ILogger<RateLimitOverridesStore>? _logger;

    /// <summary>Creates a Postgres-backed store. <paramref name="db"/> may be null on test harnesses; reads/writes still work, just nothing persists.</summary>
    public RateLimitOverridesStore(
        PostgresConnectionFactory? db = null,
        ILogger<RateLimitOverridesStore>? logger = null)
    {
        _db = db;
        _logger = logger;
    }

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, TopicRateOverride> Snapshot()
        => _overrides.ToDictionary(kv => kv.Key, kv => Clone(kv.Value), StringComparer.Ordinal);

    /// <inheritdoc/>
    public TopicRateOverride? TryGet(string topic)
    {
        ArgumentNullException.ThrowIfNull(topic);
        return _overrides.TryGetValue(topic, out var value) ? Clone(value) : null;
    }

    /// <inheritdoc/>
    public async Task SetAsync(string topic, TopicRateOverride value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(topic);
        ArgumentNullException.ThrowIfNull(value);
        if (string.IsNullOrWhiteSpace(topic))
            throw new ArgumentException("Topic must be non-empty.", nameof(topic));

        if (_db is not null)
        {
            await using var conn = _db.CreateConnection();
            await conn.OpenAsync(cancellationToken);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO rate_limit_topic_overrides (topic, rate_per_second, burst, dedup_window_seconds, updated_at)
                VALUES (@topic, @rate, @burst, @dedup, NOW())
                ON CONFLICT (topic) DO UPDATE SET
                    rate_per_second = EXCLUDED.rate_per_second,
                    burst = EXCLUDED.burst,
                    dedup_window_seconds = EXCLUDED.dedup_window_seconds,
                    updated_at = NOW();
                """;
            cmd.Parameters.AddWithValue("@topic", topic);
            cmd.Parameters.AddWithValue("@rate", value.RatePerSecond);
            cmd.Parameters.AddWithValue("@burst", value.Burst);
            cmd.Parameters.AddWithValue("@dedup", value.DedupWindowSeconds);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        _overrides[topic] = Clone(value);
    }

    /// <inheritdoc/>
    public async Task<bool> RemoveAsync(string topic, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(topic);

        if (_db is not null)
        {
            await using var conn = _db.CreateConnection();
            await conn.OpenAsync(cancellationToken);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM rate_limit_topic_overrides WHERE topic = @topic";
            cmd.Parameters.AddWithValue("@topic", topic);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        return _overrides.TryRemove(topic, out _);
    }

    /// <inheritdoc/>
    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        if (_db is not null)
        {
            await using var conn = _db.CreateConnection();
            await conn.OpenAsync(cancellationToken);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "TRUNCATE TABLE rate_limit_topic_overrides";
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
            cmd.CommandText = "SELECT topic, rate_per_second, burst, dedup_window_seconds FROM rate_limit_topic_overrides";
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            var loaded = 0;
            while (await reader.ReadAsync(cancellationToken))
            {
                var topic = reader.GetString(0);
                _overrides[topic] = new TopicRateOverride
                {
                    RatePerSecond = reader.GetDouble(1),
                    Burst = reader.GetInt32(2),
                    DedupWindowSeconds = reader.GetInt32(3),
                };
                loaded++;
            }
            _logger?.LogInformation("Hydrated {Count} per-topic rate-limit overrides from Postgres", loaded);
        }
        catch (NpgsqlException ex)
        {
            _logger?.LogWarning(ex, "Failed to hydrate per-topic rate-limit overrides; cache stays empty");
        }
    }

    private static TopicRateOverride Clone(TopicRateOverride source) => new()
    {
        RatePerSecond = source.RatePerSecond,
        Burst = source.Burst,
        DedupWindowSeconds = source.DedupWindowSeconds,
    };
}
