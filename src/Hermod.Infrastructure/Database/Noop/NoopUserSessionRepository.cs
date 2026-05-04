using Hermod.Core.Interfaces;

namespace Hermod.Infrastructure.Database.Noop;

/// <summary>
/// Pass-through session store used when <c>Hermod:Storage:Mode</c> is
/// <c>Noop</c>. Creating a session returns a fresh GUID that no later
/// <c>TryUseAsync</c> can redeem, so anything requiring persistent login
/// effectively denies silently.
/// </summary>
internal sealed class NoopUserSessionRepository : IUserSessionRepository
{
    public Task<Guid> CreateAsync(
        string userId,
        string vaultRefreshToken,
        bool rememberMe,
        TimeSpan lifetime,
        CancellationToken cancellationToken = default)
        => Task.FromResult(Guid.NewGuid());

    public Task<UserSession?> TryUseAsync(Guid sessionId, CancellationToken cancellationToken = default)
        => Task.FromResult<UserSession?>(null);

    public Task RotateVaultTokenAsync(
        Guid sessionId,
        string newVaultRefreshToken,
        DateTimeOffset newExpiresAt,
        CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task RevokeAsync(Guid sessionId, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task RevokeAllForUserAsync(string userId, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
