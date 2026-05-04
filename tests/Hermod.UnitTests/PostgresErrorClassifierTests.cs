using System.IO;
using Hermod.Infrastructure.Database;
using Npgsql;
using Xunit;

namespace Hermod.UnitTests;

/// <summary>
/// Pins the transient-connection-drop classification so
/// MetricsPersistenceService can demote known benign postgres
/// restart errors to Debug level.
/// </summary>
public class PostgresErrorClassifierTests
{
    [Fact]
    public void IsTransientConnectionDrop_Null_False()
    {
        Assert.False(PostgresErrorClassifier.IsTransientConnectionDrop(null));
    }

    [Fact]
    public void IsTransientConnectionDrop_UnrelatedException_False()
    {
        Assert.False(PostgresErrorClassifier.IsTransientConnectionDrop(new InvalidOperationException("oops")));
    }

    [Fact]
    public void IsTransientConnectionDrop_57P01_True()
    {
        var ex = MakePgEx("57P01", "terminating connection due to administrator command");
        Assert.True(PostgresErrorClassifier.IsTransientConnectionDrop(ex));
    }

    [Fact]
    public void IsTransientConnectionDrop_57P02_True()
    {
        // crash_shutdown: backend crashed and the server came back.
        var ex = MakePgEx("57P02", "crash shutdown");
        Assert.True(PostgresErrorClassifier.IsTransientConnectionDrop(ex));
    }

    [Fact]
    public void IsTransientConnectionDrop_57P03_True()
    {
        // cannot_connect_now: server is starting up.
        var ex = MakePgEx("57P03", "the database system is starting up");
        Assert.True(PostgresErrorClassifier.IsTransientConnectionDrop(ex));
    }

    [Fact]
    public void IsTransientConnectionDrop_NonTransientSqlState_False()
    {
        // 23505 is unique_violation, a real bug not a transient drop.
        var ex = MakePgEx("23505", "duplicate key value violates unique constraint");
        Assert.False(PostgresErrorClassifier.IsTransientConnectionDrop(ex));
    }

    [Fact]
    public void IsTransientConnectionDrop_WrappedInInvalidOperationException_True()
    {
        // EF / Polly / Dapper wrap pg exceptions in outer types.
        var pg = MakePgEx("57P01", "admin shutdown");
        var outer = new InvalidOperationException("db call failed", pg);
        Assert.True(PostgresErrorClassifier.IsTransientConnectionDrop(outer));
    }

    [Fact]
    public void IsTransientConnectionDrop_NpgsqlExceptionWithIoInner_True()
    {
        var inner = new IOException("Connection reset by peer");
        var npgsql = new NpgsqlException("Exception while reading from stream", inner);
        Assert.True(PostgresErrorClassifier.IsTransientConnectionDrop(npgsql));
    }

    [Fact]
    public void IsTransientConnectionDrop_NpgsqlExceptionWithNonIoInner_False()
    {
        var npgsql = new NpgsqlException("some other error", new ArgumentException("bad arg"));
        Assert.False(PostgresErrorClassifier.IsTransientConnectionDrop(npgsql));
    }

    // PostgresException has no public constructor that accepts SqlState,
    // so build it through reflection. This keeps the test lib-free and
    // still exercises the real exception type.
    private static PostgresException MakePgEx(string sqlState, string message)
    {
        // PostgresException uses an internal ctor that takes a
        // `PostgresErrorType` enum or the parsed fields directly.
        // Use the public ctor taking (message, severity, invariantSeverity, sqlState).
        return new PostgresException(
            messageText: message,
            severity: "FATAL",
            invariantSeverity: "FATAL",
            sqlState: sqlState);
    }
}
