using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Hermod.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Hermod.Infrastructure.Database;

/// <summary>
/// Produces live <see cref="NpgsqlConnection"/> instances and gates startup
/// on PostgreSQL becoming reachable. Schema DDL lives in
/// <see cref="PostgresSchemaInitializer"/>.
/// </summary>
public sealed class PostgresConnectionFactory
{
    private const int WaitForReadyAttempts = 30;
    private static readonly TimeSpan WaitForReadyInterval = TimeSpan.FromSeconds(2);

    private readonly string _connectionString;
    private readonly ILogger<PostgresConnectionFactory> _logger;

    /// <summary>
    /// Builds the Npgsql connection string from <see cref="HermodSettings"/>
    /// and validates it up front so typos surface at construction time rather
    /// than as a cryptic Npgsql error on the first query.
    /// </summary>
    public PostgresConnectionFactory(IOptions<HermodSettings> settings, ILogger<PostgresConnectionFactory> logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);
        _connectionString = BuildConnectionString(settings.Value.Database, settings.Value.Storage);
        _logger = logger;
    }

    /// <summary>
    /// Returns a new unopened <see cref="NpgsqlConnection"/> bound to the
    /// configured pool. Callers own the lifetime.
    /// </summary>
    public NpgsqlConnection CreateConnection() => new(_connectionString);

    /// <summary>
    /// Polls until Postgres accepts a connection or
    /// <paramref name="cancellationToken"/> fires; throws
    /// <see cref="InvalidOperationException"/> after 30 failed attempts.
    /// </summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Readiness probe must swallow every Npgsql/socket failure and retry; the final miss surfaces as InvalidOperationException.")]
    public async Task WaitForReadyAsync(CancellationToken cancellationToken = default)
    {
        Exception? lastFailure = null;
        for (var attempt = 1; attempt <= WaitForReadyAttempts; attempt++)
        {
            try
            {
                await using var conn = CreateConnection();
                await conn.OpenAsync(cancellationToken);
                _logger.LogInformation("PostgreSQL is ready");
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastFailure = ex;
                _logger.LogDebug(ex,
                    "Waiting for PostgreSQL (attempt {Attempt}/{Max})",
                    attempt, WaitForReadyAttempts);
            }

            await Task.Delay(WaitForReadyInterval, cancellationToken);
        }

        throw new InvalidOperationException(
            string.Format(
                CultureInfo.InvariantCulture,
                "PostgreSQL did not become ready after {0} attempts ({1:F0}s).",
                WaitForReadyAttempts,
                WaitForReadyAttempts * WaitForReadyInterval.TotalSeconds),
            lastFailure);
    }

    private static string BuildConnectionString(DatabaseSettings db, StorageSettings storage)
    {
        // Fail fast: a malformed value (e.g. a SQLite-style default leaking
        // through, or a typo in an overlay ConfigMap) is far easier to
        // diagnose at constructor time than as a cryptic Npgsql exception
        // on the first Open call.
        NpgsqlConnectionStringBuilder builder;
        try
        {
            builder = new NpgsqlConnectionStringBuilder(db.ConnectionString);
        }
        catch (Exception ex)
        {
            throw new ArgumentException(
                "Hermod:Database:ConnectionString is not a valid Npgsql connection string. " +
                "Check appsettings.json, the Hermod__Database__ConnectionString env var, " +
                "and any overlay ConfigMap.",
                nameof(db),
                ex);
        }

        if (string.IsNullOrWhiteSpace(builder.Host))
        {
            throw new ArgumentException(
                "Hermod:Database:ConnectionString is missing the Host field. " +
                "Example: Host=postgres;Port=5432;Database=hermod;Username=hermod_app.",
                nameof(db));
        }

        // Optional override merged from a Kubernetes Secret. Splicing it in
        // here keeps the password out of the ConfigMap-backed string.
        if (!string.IsNullOrEmpty(db.Password))
        {
            builder.Password = db.Password;
        }

        // Pool tuning: respect explicit overrides already in the connection
        // string; otherwise take values from Hermod:Storage:* so operators
        // can tune without rewriting the string. Sizes are validated so a
        // misconfigured 0/-1 doesn't silently disable pooling.
        builder.Pooling = true;

        if (!builder.ConnectionString.Contains("Maximum Pool Size", StringComparison.OrdinalIgnoreCase))
        {
            builder.MaxPoolSize = Math.Max(1, storage.MaxPoolSize);
        }
        if (!builder.ConnectionString.Contains("Minimum Pool Size", StringComparison.OrdinalIgnoreCase))
        {
            builder.MinPoolSize = Math.Max(0, Math.Min(storage.MinPoolSize, builder.MaxPoolSize));
        }
        if (!builder.ConnectionString.Contains("Command Timeout", StringComparison.OrdinalIgnoreCase))
        {
            builder.CommandTimeout = Math.Max(0, storage.CommandTimeoutSeconds);
        }
        if (!builder.ConnectionString.Contains("Keepalive", StringComparison.OrdinalIgnoreCase))
        {
            builder.KeepAlive = Math.Max(0, storage.KeepAliveSeconds);
        }
        if (!builder.ConnectionString.Contains("Max Auto Prepare", StringComparison.OrdinalIgnoreCase))
        {
            builder.MaxAutoPrepare = Math.Max(0, storage.MaxAutoPrepare);
        }

        return builder.ConnectionString;
    }
}
