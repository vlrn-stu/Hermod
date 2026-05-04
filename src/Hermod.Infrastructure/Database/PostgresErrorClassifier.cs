using Npgsql;

namespace Hermod.Infrastructure.Database;

/// <summary>
/// Classifies Npgsql exceptions so callers can demote benign transient drops
/// (server restart, backend termination, server still starting up) to debug
/// logging without silencing genuine failures.
/// </summary>
public static class PostgresErrorClassifier
{
    /// <summary>
    /// True when the exception wraps a Postgres admin-initiated connection
    /// termination (SQLSTATE 57P01, 57P02, 57P03) or an Npgsql IO failure
    /// that self-heals on the next query.
    /// </summary>
    public static bool IsTransientConnectionDrop(Exception? ex)
    {
        for (var cur = ex; cur is not null; cur = cur.InnerException)
        {
            if (cur is PostgresException pg)
            {
                return pg.SqlState is "57P01" or "57P02" or "57P03";
            }

            if (cur is NpgsqlException { InnerException: System.IO.IOException })
            {
                return true;
            }
        }

        return false;
    }
}
