using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hermod.Coordinator.Authorization;

/// <summary>
/// Always-succeeds auth scheme used by test/dev profiles when
/// <c>Hermod:Security:AuthBypass=true</c>. Returns admin+operator+viewer
/// roles so every <c>[Authorize(Policy=…)]</c> gate passes without
/// minting JWTs or running a vault stub. The startup guard in
/// <c>Program.cs</c> hard-fails if this is registered while
/// <c>ASPNETCORE_ENVIRONMENT=Production</c>.
/// </summary>
public sealed class AuthBypassHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    /// <summary>The ASP.NET Core authentication scheme name registered for this handler.</summary>
    public const string SchemeName = "AuthBypass";

    /// <summary>Standard ASP.NET Core authentication-handler constructor.</summary>
    public AuthBypassHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder) { }

    /// <inheritdoc />
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var claims = new[]
        {
            new Claim("sub", "00000000-0000-0000-0000-000000000000"),
            new Claim(ClaimTypes.NameIdentifier, "00000000-0000-0000-0000-000000000000"),
            new Claim(ClaimTypes.Name, "auth-bypass"),
            new Claim(ClaimTypes.Email, "bypass@hermod.local"),
            new Claim(ClaimTypes.Role, "admin"),
            new Claim(ClaimTypes.Role, "operator"),
            new Claim(ClaimTypes.Role, "viewer"),
            new Claim("fingerprint", new string('0', 64)),
        };
        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
