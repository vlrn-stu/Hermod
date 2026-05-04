using System.Text;
using System.Text.Json;
using Hermod.Coordinator.Models;
using Hermod.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Vault42.AspNetCore;

namespace Hermod.Coordinator.Controllers;

/// <summary>
/// Proxy to Vault42 that keeps the refresh-token round-trip entirely
/// server-side. The browser never sees Vault's refresh token; it only
/// carries the coordinator's own <c>hermod_session</c> cookie, which
/// points at a row in <c>user_sessions</c> holding the Vault token.
/// </summary>
[ApiController]
[Route("api/auth")]
[Produces("application/json")]
[IgnoreAntiforgeryToken]
public sealed class AuthProxyController : ControllerBase
{
    private const string Vault42ClientName = "Vault42Api";

    // Vault's refresh cookie name; __Host- prefix is load-bearing on
    // the browser side, opaque here (HttpClient-to-Vault traffic).
    private const string VaultRefreshCookie = "__Host-refresh_token";
    private const string VaultRefreshCookiePrefix = VaultRefreshCookie + "=";

    private const string HermodSessionCookie = "hermod_session";
    // HermodTokenCookie carries the Vault42 access JWT; HttpOnly so JS
    // can't exfiltrate. Read by the Blazor auth state provider AND by
    // the cookie→bearer middleware that gates [Authorize] endpoints.
    private const string HermodTokenCookie = "hermod_token";

    // Default lifetimes; overridden by Vault's Set-Cookie MaxAge when present.
    private static readonly TimeSpan RememberMeLifetime = TimeSpan.FromDays(30);
    private static readonly TimeSpan SessionOnlyLifetime = TimeSpan.FromHours(24);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IUserSessionRepository _sessions;
    private readonly ILogger<AuthProxyController> _logger;

    /// <summary>Creates the auth proxy with its HTTP client factory, session store and logger.</summary>
    /// <param name="httpClientFactory">Factory producing the <c>Vault42Api</c> named client.</param>
    /// <param name="sessions">Store holding Vault refresh tokens keyed by hermod_session UUID.</param>
    /// <param name="logger">Logger for auth failure diagnostics.</param>
    public AuthProxyController(
        IHttpClientFactory httpClientFactory,
        IUserSessionRepository sessions,
        ILogger<AuthProxyController> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(logger);
        _httpClientFactory = httpClientFactory;
        _sessions = sessions;
        _logger = logger;
    }

    /// <summary>Logs a user in against Vault42 and issues a hermod_session cookie.</summary>
    /// <param name="request">Username / password / remember-me payload.</param>
    /// <param name="cancellationToken">Token to abort the call.</param>
    /// <returns>200 with the Vault login response (tokens or 2FA challenge), 502 if Vault is unreachable.</returns>
    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<ActionResult> Login([FromBody] Vault42LoginRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var response = await ForwardToVaultAsync(
            HttpMethod.Post,
            "/auth/login",
            JsonContent(request),
            attachAuth: false,
            cancellationToken);

        if (response is null)
        {
            return StatusCode(502, new Vault42ErrorResponse { Error = "Authentication service unavailable" });
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var error = TryDeserialize<Vault42ErrorResponse>(body);
            _logger.LogWarning(
                "Login failed for {Email}: Vault returned {Status}",
                request.Email, (int)response.StatusCode);
            return StatusCode((int)response.StatusCode,
                new Vault42ErrorResponse { Error = error?.Error ?? "Login failed" });
        }

        var loginResponse = TryDeserialize<Vault42LoginResponse>(body);
        if (loginResponse is null)
        {
            return StatusCode(502, new Vault42ErrorResponse { Error = "Invalid response from authentication service" });
        }

        // 2FA path: no tokens yet, pass the challenge back to the client.
        if (loginResponse.Requires2fa)
        {
            _logger.LogInformation("Login for {Email} advanced to 2FA challenge", request.Email);
            return Ok(loginResponse);
        }

        await IssueSessionAsync(response, loginResponse, request.RememberMe, cancellationToken);
        _logger.LogInformation("Login succeeded for {Email}", request.Email);
        // Strip access_token from the response body — it's already set as
        // an HttpOnly hermod_token cookie by IssueSessionAsync. Returning
        // the JWT in JSON would defeat the cookie's exfiltration guard
        // (any XSS could read it from the fetch response). Reply with a
        // minimal success envelope so the bearer never reaches the browser.
        return Ok(new Vault42LoginResponse
        {
            TokenType = loginResponse.TokenType,
            ExpiresIn = loginResponse.ExpiresIn,
        });
    }

    /// <summary>Forwards a password change to Vault and revokes other sessions on success.</summary>
    /// <param name="request">Current and new password.</param>
    /// <param name="cancellationToken">Token to abort the call.</param>
    /// <returns>200 with a message, 400 on missing fields, 502 on Vault unavailability.</returns>
    [HttpPost("change-password")]
    [Authorize]
    public async Task<ActionResult> ChangePassword(
        [FromBody] Vault42ChangePasswordRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.CurrentPassword) ||
            string.IsNullOrWhiteSpace(request.NewPassword))
        {
            return BadRequest(new Vault42ErrorResponse { Error = "current_password and new_password are required" });
        }

        using var message = new HttpRequestMessage(HttpMethod.Post, "/auth/change-password")
        {
            Content = JsonContent(request),
        };
        var bearer = GetBearerToken();
        if (!string.IsNullOrEmpty(bearer))
        {
            message.Headers.Add("Authorization", $"Bearer {bearer}");
        }

        var response = await SendToVaultAsync(message, cancellationToken);
        if (response is null)
        {
            return StatusCode(502, new Vault42ErrorResponse { Error = "Authentication service unavailable" });
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var error = TryDeserialize<Vault42ErrorResponse>(body);
            return StatusCode((int)response.StatusCode,
                new Vault42ErrorResponse { Error = error?.Error ?? "Password change failed" });
        }

        // Revoke all other sessions so no stale refresh cookie outlives
        // the credential rotation.
        var userId = User.GetUserId();
        if (!string.IsNullOrEmpty(userId))
        {
            await _sessions.RevokeAllForUserAsync(userId, cancellationToken);
        }
        ClearSessionCookie();
        _logger.LogWarning("Password changed for user {UserId}; all other sessions revoked", userId ?? "unknown");
        return Ok(new { message = "Password changed" });
    }

    /// <summary>Completes a 2FA challenge against Vault and issues a session cookie on success.</summary>
    /// <param name="request">Challenge token from the login step plus the TOTP code.</param>
    /// <param name="cancellationToken">Token to abort the call.</param>
    /// <returns>200 with the issued tokens, 400 on missing inputs, 502 on Vault unavailability.</returns>
    [HttpPost("verify-2fa")]
    [AllowAnonymous]
    public async Task<ActionResult> VerifyTwoFactor(
        [FromBody] Vault42VerifyTwoFactorRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.ChallengeToken) || string.IsNullOrWhiteSpace(request.Code))
        {
            return BadRequest(new Vault42ErrorResponse { Error = "challenge_token and code are required" });
        }

        var response = await ForwardToVaultAsync(
            HttpMethod.Post,
            "/auth/2fa/verify",
            JsonContent(request),
            attachAuth: false,
            cancellationToken);

        if (response is null)
        {
            return StatusCode(502, new Vault42ErrorResponse { Error = "Authentication service unavailable" });
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var error = TryDeserialize<Vault42ErrorResponse>(body);
            _logger.LogWarning("2FA verification failed: Vault returned {Status}", (int)response.StatusCode);
            return StatusCode((int)response.StatusCode,
                new Vault42ErrorResponse { Error = error?.Error ?? "Two-factor verification failed" });
        }

        var loginResponse = TryDeserialize<Vault42LoginResponse>(body);
        if (loginResponse is null)
        {
            return StatusCode(502, new Vault42ErrorResponse { Error = "Invalid response from authentication service" });
        }

        // 2FA verify lacks remember_me; default to session-only.
        var rememberMe = false;
        await IssueSessionAsync(response, loginResponse, rememberMe, cancellationToken);
        _logger.LogInformation("2FA verification succeeded");
        // Same access_token strip as /login — cookie holds the JWT, body
        // doesn't.
        return Ok(new Vault42LoginResponse
        {
            TokenType = loginResponse.TokenType,
            ExpiresIn = loginResponse.ExpiresIn,
        });
    }

    /// <summary>Rotates the Vault refresh token using the hermod_session cookie and returns a fresh access token.</summary>
    /// <param name="cancellationToken">Token to abort the call.</param>
    /// <returns>200 with the raw Vault refresh response body, 401 if no/invalid session, 502 on Vault error.</returns>
    [HttpPost("refresh")]
    [AllowAnonymous]
    public async Task<ActionResult> Refresh(CancellationToken cancellationToken = default)
    {
        // [AllowAnonymous] by design: refresh is the path when the
        // access token has expired; [Authorize] would be a catch-22.
        if (!TryReadSessionCookie(out var sessionId))
        {
            return Unauthorized(new Vault42ErrorResponse { Error = "No session cookie" });
        }

        var session = await _sessions.TryUseAsync(sessionId, cancellationToken);
        if (session is null)
        {
            ClearSessionCookie();
            return Unauthorized(new Vault42ErrorResponse { Error = "Session expired or revoked" });
        }

        using var message = new HttpRequestMessage(HttpMethod.Post, "/auth/refresh");
        message.Headers.Add("Cookie", $"{VaultRefreshCookiePrefix}{session.VaultRefreshToken}");

        var response = await SendToVaultAsync(message, cancellationToken);
        if (response is null)
        {
            return StatusCode(502, new Vault42ErrorResponse { Error = "Authentication service unavailable" });
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            // Vault refused (rotated, revoked, or family replay); force fresh login.
            await _sessions.RevokeAsync(sessionId, cancellationToken);
            ClearSessionCookie();
            return StatusCode((int)response.StatusCode,
                new Vault42ErrorResponse { Error = "Token refresh failed" });
        }

        // Rotate both server-side row and client cookie so Remember-Me
        // actually survives past its original window.
        if (TryExtractVaultRefreshToken(response, out var newToken, out var newMaxAge))
        {
            var lifetime = newMaxAge > TimeSpan.Zero
                ? newMaxAge
                : (session.RememberMe ? RememberMeLifetime : SessionOnlyLifetime);
            await _sessions.RotateVaultTokenAsync(
                sessionId,
                newToken,
                DateTimeOffset.UtcNow.Add(lifetime),
                cancellationToken);
            SetSessionCookie(sessionId, session.RememberMe, lifetime);
        }

        return Content(body, "application/json");
    }

    /// <summary>
    /// GET-friendly logout: clears the session/token cookies, best-effort
    /// revokes against Vault42, and 302-redirects the browser to
    /// <c>/login</c>. Designed for the sidebar Logout link to work as a
    /// plain anchor tag — the browser carries the HttpOnly cookies on the
    /// GET, the response's <c>Set-Cookie</c> with <c>Max-Age=0</c> reaches
    /// the browser before the redirect lands.
    /// </summary>
    /// <param name="cancellationToken">Token to abort the call.</param>
    /// <returns>302 to <c>/login</c>.</returns>
    [HttpGet("logout-redirect")]
    [AllowAnonymous]
    public async Task<ActionResult> LogoutRedirect(CancellationToken cancellationToken = default)
    {
        await Logout(cancellationToken);
        return Redirect("/login");
    }

    /// <summary>Revokes the current session both client and server-side (best-effort Vault logout).</summary>
    /// <param name="cancellationToken">Token to abort the call.</param>
    /// <returns>200 with a confirmation message.</returns>
    [HttpPost("logout")]
    [AllowAnonymous]
    public async Task<ActionResult> Logout(CancellationToken cancellationToken = default)
    {
        if (TryReadSessionCookie(out var sessionId))
        {
            var session = await _sessions.TryUseAsync(sessionId, cancellationToken);
            if (session is not null)
            {
                // Best-effort: Vault logout failure still clears our side.
                using var message = new HttpRequestMessage(HttpMethod.Post, "/auth/logout");
                message.Headers.Add("Cookie", $"{VaultRefreshCookiePrefix}{session.VaultRefreshToken}");
                var bearer = GetBearerToken();
                if (!string.IsNullOrEmpty(bearer))
                {
                    message.Headers.Add("Authorization", $"Bearer {bearer}");
                }
                _ = await SendToVaultAsync(message, cancellationToken);
            }
            await _sessions.RevokeAsync(sessionId, cancellationToken);
        }

        ClearSessionCookie();
        return Ok(new { message = "Logged out" });
    }

    /// <summary>Returns the current user's id, roles and scopes as derived from the access token.</summary>
    /// <returns>200 with the claim summary.</returns>
    [HttpGet("me")]
    [Authorize]
    public ActionResult GetCurrentUser() => Ok(new
    {
        id = User.GetUserId(),
        roles = User.GetRoles(),
        scopes = User.GetScopes()
    });

    // ── Session management internals ──────────────────────────────────

    private async Task IssueSessionAsync(
        HttpResponseMessage vaultResponse,
        Vault42LoginResponse loginResponse,
        bool rememberMe,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(loginResponse.AccessToken))
        {
            return;
        }

        var userId = ExtractSubFromJwt(loginResponse.AccessToken);
        if (userId is null)
        {
            _logger.LogWarning("Vault login returned a JWT with no sub claim; session not persisted");
            return;
        }

        if (!TryExtractVaultRefreshToken(vaultResponse, out var refreshToken, out var vaultMaxAge))
        {
            _logger.LogWarning("Vault login response had no refresh cookie; Remember-Me will not work");
            return;
        }

        var lifetime = rememberMe
            ? (vaultMaxAge > TimeSpan.Zero ? vaultMaxAge : RememberMeLifetime)
            : (vaultMaxAge > TimeSpan.Zero && vaultMaxAge < SessionOnlyLifetime
                ? vaultMaxAge
                : SessionOnlyLifetime);

        var sessionId = await _sessions.CreateAsync(userId, refreshToken, rememberMe, lifetime, cancellationToken);
        SetSessionCookie(sessionId, rememberMe, lifetime);
        // hermod_token is the JWT itself in an HttpOnly cookie; both the
        // Vault42AuthStateProvider (Blazor SSR) and the cookie→bearer
        // middleware in Program.cs read it. Lifetime tied to the access
        // token, not the session — it'll be re-set on /auth/refresh.
        SetTokenCookie(loginResponse.AccessToken, rememberMe, lifetime);
    }

    private void SetSessionCookie(Guid sessionId, bool rememberMe, TimeSpan lifetime)
    {
        var options = new CookieOptions
        {
            HttpOnly = true,
            Secure = Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            // MaxAge is only set for Remember-Me sessions. Session cookies
            // without MaxAge die with the browser window; that's the
            // expected behaviour when the box was unticked.
            MaxAge = rememberMe ? lifetime : null,
        };
        Response.Cookies.Append(HermodSessionCookie, sessionId.ToString("N"), options);
    }

    private void SetTokenCookie(string accessToken, bool rememberMe, TimeSpan lifetime)
    {
        var options = new CookieOptions
        {
            HttpOnly = true,
            Secure = Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            MaxAge = rememberMe ? lifetime : null,
        };
        Response.Cookies.Append(HermodTokenCookie, accessToken, options);
    }

    private void ClearSessionCookie()
    {
        var options = new CookieOptions
        {
            HttpOnly = true,
            Secure = Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            Path = "/",
        };
        Response.Cookies.Delete(HermodSessionCookie, options);
        Response.Cookies.Delete(HermodTokenCookie, options);
    }

    private bool TryReadSessionCookie(out Guid sessionId)
    {
        sessionId = default;
        var raw = Request.Cookies[HermodSessionCookie];
        if (string.IsNullOrEmpty(raw)) return false;
        return Guid.TryParseExact(raw, "N", out sessionId);
    }

    // ── Vault wire helpers ────────────────────────────────────────────

    private async Task<HttpResponseMessage?> ForwardToVaultAsync(
        HttpMethod method,
        string path,
        HttpContent? content,
        bool attachAuth,
        CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(method, path);
        if (content is not null) message.Content = content;
        if (attachAuth)
        {
            var bearer = GetBearerToken();
            if (!string.IsNullOrEmpty(bearer))
            {
                message.Headers.Add("Authorization", $"Bearer {bearer}");
            }
        }
        return await SendToVaultAsync(message, cancellationToken);
    }

    private async Task<HttpResponseMessage?> SendToVaultAsync(HttpRequestMessage message, CancellationToken cancellationToken)
    {
        AttachClientIpHeader(message);
        var client = _httpClientFactory.CreateClient(Vault42ClientName);
        try
        {
            return await client.SendAsync(message, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Failed to reach Vault at {Path}", message.RequestUri);
            return null;
        }
    }

    /// <summary>
    /// Forwards the original browser IP to vault42 via X-Forwarded-For so its
    /// per-IP rate limiter (login, refresh, password reset) can attribute
    /// requests to the actual attacker rather than the coordinator pod IP —
    /// which would otherwise let one bad actor exhaust the limit for everyone.
    /// Vault42 must be configured with VAULT_EMBEDDED_TRUSTED_UPSTREAM=true (or
    /// equivalent TRUSTED_PROXIES + REAL_IP_HEADER) for the header to be honoured.
    /// </summary>
    private void AttachClientIpHeader(HttpRequestMessage message)
    {
        var clientIp = HttpContext?.Connection?.RemoteIpAddress?.ToString();
        if (string.IsNullOrEmpty(clientIp))
        {
            return;
        }

        // Preserve any upstream chain: if a reverse proxy in front of the
        // coordinator already set XFF, append our observation rather than
        // overwriting (RFC 7239 semantics).
        var existing = Request.Headers["X-Forwarded-For"].ToString();
        var combined = string.IsNullOrEmpty(existing) ? clientIp : $"{existing}, {clientIp}";
        message.Headers.TryAddWithoutValidation("X-Forwarded-For", combined);
    }

    /// <summary>
    /// Parses the Vault refresh cookie out of a Set-Cookie header. Returns
    /// the raw token value plus the advertised Max-Age (0 if absent).
    /// Handles the <c>__Host-refresh_token</c> name that Vault uses on
    /// every deployment.
    /// </summary>
    private static bool TryExtractVaultRefreshToken(
        HttpResponseMessage response,
        out string token,
        out TimeSpan maxAge)
    {
        token = string.Empty;
        maxAge = TimeSpan.Zero;

        if (!response.Headers.TryGetValues("Set-Cookie", out var cookies)) return false;

        foreach (var cookie in cookies)
        {
            if (!cookie.StartsWith(VaultRefreshCookiePrefix, StringComparison.OrdinalIgnoreCase)) continue;

            var parts = cookie.Split(';', StringSplitOptions.TrimEntries);
            token = parts[0][VaultRefreshCookiePrefix.Length..];
            if (string.IsNullOrEmpty(token)) return false;

            foreach (var attr in parts.Skip(1))
            {
                if (attr.StartsWith("Max-Age=", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(attr["Max-Age=".Length..], out var seconds) &&
                    seconds > 0)
                {
                    maxAge = TimeSpan.FromSeconds(seconds);
                }
            }
            return true;
        }
        return false;
    }

    private string? GetBearerToken()
    {
        var header = Request.Headers.Authorization.FirstOrDefault();
        return header is not null && header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? header["Bearer ".Length..].Trim()
            : null;
    }

    private static StringContent JsonContent<T>(T value) =>
        new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    private static string? ExtractSubFromJwt(string jwt)
    {
        try
        {
            var parts = jwt.Split('.');
            if (parts.Length != 3) return null;

            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = (payload.Length % 4) switch
            {
                2 => payload + "==",
                3 => payload + "=",
                _ => payload
            };

            var bytes = Convert.FromBase64String(payload);
            using var doc = JsonDocument.Parse(bytes);
            return doc.RootElement.TryGetProperty("sub", out var sub) ? sub.GetString() : null;
        }
        catch (FormatException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static T? TryDeserialize<T>(string json) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
