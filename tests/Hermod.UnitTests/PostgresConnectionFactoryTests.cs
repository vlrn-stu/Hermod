using System.IO;
using Hermod.Core.Configuration;
using Hermod.Infrastructure.Database;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace Hermod.UnitTests;

/// <summary>
/// Pins the Password-merge behaviour that supports the Kubernetes Secret
/// backed <c>Hermod__Database__Password</c> env var. The factory reads
/// <c>Database.ConnectionString</c> (no password) and <c>Database.Password</c>
/// (from the secret) and composes them. Also pins the startup validation
/// fail-fast and the schema advisory-lock ordering (now enforced in
/// <see cref="PostgresSchemaInitializer"/>).
/// </summary>
public class PostgresConnectionFactoryTests
{
    private static PostgresConnectionFactory Build(DatabaseSettings db)
    {
        var settings = new HermodSettings { Database = db };
        return new PostgresConnectionFactory(
            Options.Create(settings),
            NullLogger<PostgresConnectionFactory>.Instance);
    }

    private static string PasswordOf(PostgresConnectionFactory factory)
    {
        // Easiest way to inspect: open a connection from the factory (which
        // will fail because no real postgres is running) and read the
        // ConnectionString field off NpgsqlConnection. NpgsqlConnection keeps
        // the original string until Open.
        using var conn = factory.CreateConnection();
        var builder = new NpgsqlConnectionStringBuilder(conn.ConnectionString);
        return builder.Password ?? string.Empty;
    }

    [Fact]
    public void NoPasswordOverride_UsesConnectionStringPasswordVerbatim()
    {
        var factory = Build(new DatabaseSettings
        {
            ConnectionString = "Host=db;Port=5432;Database=hermod;Username=u;Password=baseline"
        });
        Assert.Equal("baseline", PasswordOf(factory));
    }

    [Fact]
    public void PasswordOverride_ReplacesConnectionStringPassword()
    {
        var factory = Build(new DatabaseSettings
        {
            ConnectionString = "Host=db;Port=5432;Database=hermod;Username=u;Password=baseline",
            Password = "from-secret"
        });
        Assert.Equal("from-secret", PasswordOf(factory));
    }

    [Fact]
    public void PasswordOverride_AppliesWhenConnectionStringHasNoPassword()
    {
        var factory = Build(new DatabaseSettings
        {
            ConnectionString = "Host=db;Port=5432;Database=hermod;Username=u",
            Password = "from-secret"
        });
        Assert.Equal("from-secret", PasswordOf(factory));
    }

    [Fact]
    public void EmptyStringPassword_FallsThroughToConnectionString()
    {
        // `string.IsNullOrEmpty` treats "" the same as null, so the factory
        // must fall through to whatever ConnectionString carries.
        var factory = Build(new DatabaseSettings
        {
            ConnectionString = "Host=db;Port=5432;Database=hermod;Username=u;Password=baseline",
            Password = ""
        });
        Assert.Equal("baseline", PasswordOf(factory));
    }

    [Fact]
    public void NullPassword_FallsThroughToConnectionString()
    {
        var factory = Build(new DatabaseSettings
        {
            ConnectionString = "Host=db;Port=5432;Database=hermod;Username=u;Password=baseline",
            Password = null
        });
        Assert.Equal("baseline", PasswordOf(factory));
    }

    [Fact]
    public void PasswordOverride_PreservesOtherConnectionStringFields()
    {
        var factory = Build(new DatabaseSettings
        {
            ConnectionString = "Host=pg.example;Port=5433;Database=alt;Username=svc;Password=x;Pooling=true;Minimum Pool Size=2",
            Password = "rotated"
        });
        using var conn = factory.CreateConnection();
        var builder = new NpgsqlConnectionStringBuilder(conn.ConnectionString);
        Assert.Equal("pg.example", builder.Host);
        Assert.Equal(5433, builder.Port);
        Assert.Equal("alt", builder.Database);
        Assert.Equal("svc", builder.Username);
        Assert.Equal("rotated", builder.Password);
        Assert.True(builder.Pooling);
        Assert.Equal(2, builder.MinPoolSize);
    }

    [Fact]
    public void Constructor_MalformedConnectionString_ThrowsArgumentException()
    {
        // A legacy SQLite-ish default that Npgsql refuses to parse.
        // Without fail-fast this would surface later as a cryptic Npgsql
        // exception on the first Open call.
        var ex = Assert.Throws<ArgumentException>(() => Build(new DatabaseSettings
        {
            ConnectionString = "Data Source=hermod.db;Mode=Memory="
        }));
        Assert.Contains("Hermod:Database:ConnectionString", ex.Message);
        Assert.NotNull(ex.InnerException);
    }

    [Fact]
    public void Constructor_ConnectionStringMissingHost_ThrowsArgumentException()
    {
        // Well-formed Npgsql syntax but no Host. Parses cleanly but
        // would fail at Open. Validation catches this at startup.
        var ex = Assert.Throws<ArgumentException>(() => Build(new DatabaseSettings
        {
            ConnectionString = "Database=hermod;Username=hermod_app"
        }));
        Assert.Contains("Host", ex.Message);
    }

    [Fact]
    public void Constructor_ConnectionStringWithJustHost_IsValid()
    {
        // Minimum viable shape. Builder accepts, Host is non-empty,
        // factory constructs without throwing.
        var factory = Build(new DatabaseSettings
        {
            ConnectionString = "Host=postgres"
        });
        Assert.NotNull(factory);
    }

    // The schema initializer wraps DDL in a Postgres transaction and
    // acquires a transaction-scoped advisory lock (pg_advisory_xact_lock)
    // before running CREATE TABLE IF NOT EXISTS + CREATE INDEX IF NOT
    // EXISTS, so concurrent coordinator replicas serialize instead of
    // racing. Running a real transactional Postgres in unit tests is
    // out of scope, so coverage here is structural: read the source
    // file and assert the guard statements exist with the exact lock
    // key.

    private static string ReadSchemaInitializerSource()
    {
        var assemblyDir = Path.GetDirectoryName(typeof(PostgresSchemaInitializer).Assembly.Location)!;
        var repoDir = Directory.GetParent(assemblyDir)!.Parent!.Parent!.Parent!.Parent!.FullName;
        var srcPath = Path.Combine(repoDir, "src", "Hermod.Infrastructure", "Database", "PostgresSchemaInitializer.cs");
        Assert.True(File.Exists(srcPath), $"expected PostgresSchemaInitializer.cs at {srcPath}");
        return File.ReadAllText(srcPath);
    }

    [Fact]
    public void SchemaInitializer_UsesTransactionScope_ForDDL()
    {
        var source = ReadSchemaInitializerSource();
        Assert.Contains("BeginTransactionAsync", source);
        Assert.Contains("tx.CommitAsync", source);
    }

    [Fact]
    public void SchemaInitializer_AcquiresAdvisoryLockBeforeDDL()
    {
        // Pins the exact lock key. Changing this value requires a
        // coordinated rollout across all running replicas, because the
        // advisory lock only serializes callers that contend for the
        // SAME key — a silent key change would let two replicas run the
        // DDL concurrently again.
        var source = ReadSchemaInitializerSource();
        Assert.Contains("pg_advisory_xact_lock(8734592347823401278)", source);
    }

    [Fact]
    public void SchemaInitializer_LockAcquiredBeforeDdlExecutes()
    {
        // Structural ordering: inside InitializeAsync, the lock command
        // must be assigned and (implicitly) executed before the DDL
        // command runs. A refactor that reorders them reopens the race.
        var source = ReadSchemaInitializerSource();
        var lockIdx = source.IndexOf(
            "lockCmd.CommandText = \"SELECT pg_advisory_xact_lock",
            StringComparison.Ordinal);
        var ddlIdx = source.IndexOf(
            "ddlCmd.CommandText = SchemaDdl;",
            StringComparison.Ordinal);
        Assert.True(lockIdx > 0, "advisory lock statement must be present in code");
        Assert.True(ddlIdx > 0, "DDL command assignment must be present in code");
        Assert.True(lockIdx < ddlIdx,
            $"advisory lock code (at {lockIdx}) must precede DDL command code (at {ddlIdx})");
    }
}
