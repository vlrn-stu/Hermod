using Dapper;
using Hermod.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace Hermod.Infrastructure.Database;

internal sealed class PostgresUserSessionRepository : IUserSessionRepository
{
    private readonly PostgresConnectionFactory _connectionFactory;
    private readonly ILogger<PostgresUserSessionRepository> _logger;

    public PostgresUserSessionRepository(
        PostgresConnectionFactory connectionFactory,
        ILogger<PostgresUserSessionRepository> logger)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        ArgumentNullException.ThrowIfNull(logger);
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    public async Task<Guid> CreateAsync(
        string userId,
        string vaultRefreshToken,
        bool rememberMe,
        TimeSpan lifetime,
        CancellationToken cancellationToken = default)
    {
        var sessionId = Guid.NewGuid();
        var expiresAt = DateTimeOffset.UtcNow.Add(lifetime);

        await using var conn = _connectionFactory.CreateConnection();
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO user_sessions (session_id, user_id, vault_refresh_token, remember_me, expires_at)
            VALUES (@SessionId, @UserId, @VaultRefreshToken, @RememberMe, @ExpiresAt);
            """,
            new
            {
                SessionId = sessionId,
                UserId = userId,
                VaultRefreshToken = vaultRefreshToken,
                RememberMe = rememberMe,
                ExpiresAt = expiresAt,
            },
            cancellationToken: cancellationToken));
        return sessionId;
    }

    public async Task<UserSession?> TryUseAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        // UPDATE … RETURNING lets the "read-and-stamp last_used_at" happen
        // in one round-trip while also filtering revoked / expired rows.
        const string sql = """
            UPDATE user_sessions
            SET last_used_at = NOW()
            WHERE session_id = @SessionId
              AND revoked_at IS NULL
              AND expires_at > NOW()
            RETURNING session_id, user_id, vault_refresh_token, remember_me,
                      expires_at, created_at, last_used_at, revoked_at;
            """;

        await using var conn = _connectionFactory.CreateConnection();
        var row = await conn.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            sql, new { SessionId = sessionId }, cancellationToken: cancellationToken));
        return row is null ? null : Map(row);
    }

    public async Task RotateVaultTokenAsync(
        Guid sessionId,
        string newVaultRefreshToken,
        DateTimeOffset newExpiresAt,
        CancellationToken cancellationToken = default)
    {
        await using var conn = _connectionFactory.CreateConnection();
        await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE user_sessions
            SET vault_refresh_token = @Token, expires_at = @ExpiresAt, last_used_at = NOW()
            WHERE session_id = @SessionId AND revoked_at IS NULL;
            """,
            new { SessionId = sessionId, Token = newVaultRefreshToken, ExpiresAt = newExpiresAt },
            cancellationToken: cancellationToken));
    }

    public async Task RevokeAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        await using var conn = _connectionFactory.CreateConnection();
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE user_sessions SET revoked_at = NOW() WHERE session_id = @SessionId AND revoked_at IS NULL;",
            new { SessionId = sessionId },
            cancellationToken: cancellationToken));
    }

    public async Task RevokeAllForUserAsync(string userId, CancellationToken cancellationToken = default)
    {
        await using var conn = _connectionFactory.CreateConnection();
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE user_sessions SET revoked_at = NOW() WHERE user_id = @UserId AND revoked_at IS NULL;",
            new { UserId = userId },
            cancellationToken: cancellationToken));
    }

    private static UserSession Map(Row r) => new(
        SessionId: r.Session_Id,
        UserId: r.User_Id,
        VaultRefreshToken: r.Vault_Refresh_Token,
        RememberMe: r.Remember_Me,
        ExpiresAt: r.Expires_At,
        CreatedAt: r.Created_At,
        LastUsedAt: r.Last_Used_At,
        RevokedAt: r.Revoked_At);

    private sealed class Row
    {
        public Guid Session_Id { get; set; }
        public string User_Id { get; set; } = string.Empty;
        public string Vault_Refresh_Token { get; set; } = string.Empty;
        public bool Remember_Me { get; set; }
        public DateTimeOffset Expires_At { get; set; }
        public DateTimeOffset Created_At { get; set; }
        public DateTimeOffset? Last_Used_At { get; set; }
        public DateTimeOffset? Revoked_At { get; set; }
    }
}
