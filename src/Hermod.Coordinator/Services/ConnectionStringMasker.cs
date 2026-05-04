using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Npgsql;

namespace Hermod.Coordinator.Services;

/// <summary>
/// Masks the <c>Password</c> field of a Npgsql connection string so a
/// rendered value can be shown on the dashboard (or in a log line) without
/// leaking the DB credential.
/// </summary>
public static class ConnectionStringMasker
{
    private static readonly Regex FallbackPattern = new(
        @"Password\s*=\s*[^;]*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Returns the input connection string with its <c>Password</c> field
    /// replaced by <c>***</c>. Empty string for null/empty input; falls back
    /// to a regex mask if Npgsql's builder rejects the input.
    /// </summary>
    /// <param name="connectionString">Raw Npgsql connection string, or null.</param>
    /// <returns>The masked connection string, or empty string for null/empty input.</returns>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Any parse failure from NpgsqlConnectionStringBuilder must fall back to a regex mask; a thrown exception would leak the password we are trying to hide.")]
    public static string Mask(string? connectionString)
    {
        if (string.IsNullOrEmpty(connectionString)) return string.Empty;

        try
        {
            var builder = new NpgsqlConnectionStringBuilder(connectionString);
            if (!string.IsNullOrEmpty(builder.Password))
            {
                builder.Password = "***";
            }
            return builder.ConnectionString;
        }
        catch (Exception)
        {
            return FallbackPattern.Replace(connectionString, "Password=***");
        }
    }
}
