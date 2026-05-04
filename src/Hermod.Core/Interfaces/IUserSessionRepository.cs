namespace Hermod.Core.Interfaces;

/// <summary>
/// A persistent user session that proxies the opaque Vault refresh token.
/// The coordinator never exposes <paramref name="VaultRefreshToken"/> to
/// the browser; the browser only sees the <c>hermod_session</c> cookie
/// carrying <paramref name="SessionId"/>.
/// </summary>
/// <param name="SessionId">Opaque session id carried by the <c>hermod_session</c> cookie.</param>
/// <param name="UserId">Vault42 user id this session authenticates as.</param>
/// <param name="VaultRefreshToken">Vault refresh token stored server-side; never exposed to the browser.</param>
/// <param name="RememberMe">Whether the cookie persists across browser restarts.</param>
/// <param name="ExpiresAt">Absolute expiry; after this point <c>TryUseAsync</c> returns null.</param>
/// <param name="CreatedAt">Session creation timestamp.</param>
/// <param name="LastUsedAt">Most recent successful <c>TryUseAsync</c> call; null if never used.</param>
/// <param name="RevokedAt">Revocation timestamp; null when the session is still live.</param>
public sealed record UserSession(
    Guid SessionId,
    string UserId,
    string VaultRefreshToken,
    bool RememberMe,
    DateTimeOffset ExpiresAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset? RevokedAt);

/// <summary>
/// Survives coordinator restarts so Remember-Me keeps working after a
/// pod cycle. The Vault refresh token is stored alongside the session so
/// /auth/refresh can forward it to Vault without the browser ever seeing
/// it.
/// </summary>
public interface IUserSessionRepository
{
    /// <summary>Creates a new session row and returns its opaque id (the value stored in the <c>hermod_session</c> cookie).</summary>
    /// <param name="userId">Vault42 user id the session authenticates as.</param>
    /// <param name="vaultRefreshToken">Refresh token returned by Vault; stored server-side, never seen by the browser.</param>
    /// <param name="rememberMe">Whether the cookie should persist across browser restarts.</param>
    /// <param name="lifetime">Session lifetime; seeds the initial <c>expires_at</c>.</param>
    /// <param name="cancellationToken">Request-scoped cancellation.</param>
    Task<Guid> CreateAsync(
        string userId,
        string vaultRefreshToken,
        bool rememberMe,
        TimeSpan lifetime,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the session if it exists AND has not been revoked AND has
    /// not expired. Bumps <c>last_used_at</c> as a side-effect so the
    /// admin side can identify stale sessions.
    /// </summary>
    Task<UserSession?> TryUseAsync(Guid sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Applied after a successful Vault-side refresh. Rotates the stored
    /// Vault refresh token (Vault issues a new one on every refresh) and
    /// extends the expiry.
    /// </summary>
    Task RotateVaultTokenAsync(
        Guid sessionId,
        string newVaultRefreshToken,
        DateTimeOffset newExpiresAt,
        CancellationToken cancellationToken = default);

    /// <summary>Marks the session as revoked so future <c>TryUseAsync</c> calls fail. Idempotent.</summary>
    Task RevokeAsync(Guid sessionId, CancellationToken cancellationToken = default);

    /// <summary>Revokes every session belonging to a user. Invoked on password change and logout-everywhere.</summary>
    Task RevokeAllForUserAsync(string userId, CancellationToken cancellationToken = default);
}
