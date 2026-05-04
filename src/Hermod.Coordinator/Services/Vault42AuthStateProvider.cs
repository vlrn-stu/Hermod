using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.JSInterop;

namespace Hermod.Coordinator.Services;

/// <summary>
/// Blazor authentication state built from the Vault42 JWT. Reads the token
/// from the <c>hermod_token</c> cookie during SSR and from browser storage
/// once the circuit is interactive.
/// <para>
/// UI-only: parses the payload without signature verification. API
/// authorization runs through ASP.NET's <c>AddVault</c> JWKS-verified
/// middleware in <c>Program.cs</c>, which rejects forged tokens
/// regardless of what this provider returns.
/// </para>
/// </summary>
public sealed class Vault42AuthStateProvider : AuthenticationStateProvider
{
    /// <summary>
    /// Cookie name the Login page sets on the client and the provider reads
    /// server-side during SSR. Kept non-HttpOnly so client JS can also read
    /// it for Authorization headers.
    /// </summary>
    public const string CookieName = "hermod_token";

    private static readonly AuthenticationState Anonymous =
        new(new ClaimsPrincipal(new ClaimsIdentity()));

    private readonly IJSRuntime _jsRuntime;
    private readonly IHttpContextAccessor _httpContextAccessor;

    /// <summary>Creates the provider with JS interop and HTTP context access.</summary>
    /// <param name="jsRuntime">JS runtime used to read browser storage.</param>
    /// <param name="httpContextAccessor">Accessor for the current request's cookies during SSR.</param>
    public Vault42AuthStateProvider(IJSRuntime jsRuntime, IHttpContextAccessor httpContextAccessor)
    {
        ArgumentNullException.ThrowIfNull(jsRuntime);
        ArgumentNullException.ThrowIfNull(httpContextAccessor);
        _jsRuntime = jsRuntime;
        _httpContextAccessor = httpContextAccessor;
    }

    /// <summary>Builds the current <see cref="AuthenticationState"/> from the Vault42 JWT, falling back to anonymous.</summary>
    /// <returns>Authenticated state, or <see cref="Anonymous"/> if no token or the token is invalid / expired.</returns>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Auth state lookup must degrade to anonymous on any transient failure (JS interop during prerender, malformed token, etc.) without propagating.")]
    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        try
        {
            var token = await GetTokenAsync();
            if (string.IsNullOrEmpty(token)) return Anonymous;

            var claims = ParseJwtClaims(token);
            if (claims is null || claims.Count == 0) return Anonymous;

            if (IsExpired(claims)) return Anonymous;

            var identity = new ClaimsIdentity(claims, "Vault", "sub", ClaimTypes.Role);
            return new AuthenticationState(new ClaimsPrincipal(identity));
        }
        catch (Exception)
        {
            // JS interop unavailable during prerender or other transient error.
            return Anonymous;
        }
    }

    /// <summary>Notify subscribed components that the authentication state has changed.</summary>
    public void NotifyAuthenticationStateChanged() =>
        NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());

    private Task<string?> GetTokenAsync()
    {
        // HttpOnly cookie ONLY. The localStorage/sessionStorage fallback
        // we used to have caused an auth-bounce loop: stale browser
        // storage left the client thinking it was logged in (so /login
        // immediately redirected to /), while the server (cookie-only
        // path) still saw the request as anonymous and bounced it back.
        // Cookie-only keeps server + client on the exact same answer.
        var cookieToken = _httpContextAccessor.HttpContext?.Request.Cookies[CookieName];
        return Task.FromResult<string?>(string.IsNullOrEmpty(cookieToken) ? null : cookieToken);
    }

    private static bool IsExpired(IEnumerable<Claim> claims)
    {
        var exp = claims.FirstOrDefault(c => c.Type == "exp");
        // Missing or unparseable exp = expired. Forged tokens that
        // strip the claim don't bypass the UI check.
        if (exp is null || !long.TryParse(exp.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unixSeconds))
        {
            return true;
        }
        return DateTimeOffset.FromUnixTimeSeconds(unixSeconds) < DateTimeOffset.UtcNow;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Malformed tokens (base64 corruption, unexpected JSON) must silently degrade to anonymous auth, not crash the circuit.")]
    private static List<Claim>? ParseJwtClaims(string jwt)
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

            var json = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
            using var doc = JsonDocument.Parse(json);

            var claims = new List<Claim>();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                AddClaimsFromProperty(claims, prop);
            }
            return claims;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // JWT boolean claims are serialized as lowercase "true"/"false" strings.
    [SuppressMessage("Globalization", "CA1308:Normalize strings to uppercase", Justification = "JWT boolean claim values use lowercase by convention; uppercase would break downstream consumers.")]
    private static void AddClaimsFromProperty(List<Claim> claims, JsonProperty prop)
    {
        switch (prop.Value.ValueKind)
        {
            case JsonValueKind.Array:
                var claimType = prop.Name switch
                {
                    "roles" => ClaimTypes.Role,
                    "scopes" => "scope",
                    _ => prop.Name
                };
                foreach (var item in prop.Value.EnumerateArray())
                {
                    claims.Add(new Claim(claimType, item.GetString() ?? string.Empty));
                }
                break;
            case JsonValueKind.String:
                claims.Add(new Claim(prop.Name, prop.Value.GetString() ?? string.Empty));
                break;
            case JsonValueKind.Number:
                claims.Add(new Claim(prop.Name, prop.Value.GetRawText()));
                break;
            case JsonValueKind.True:
            case JsonValueKind.False:
                claims.Add(new Claim(prop.Name, prop.Value.GetBoolean().ToString().ToLowerInvariant()));
                break;
        }
    }
}
