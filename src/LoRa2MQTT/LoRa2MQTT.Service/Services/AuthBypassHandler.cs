using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LoRa2MQTT.Service.Services;

/// <summary>
/// Always-succeeds auth scheme used by test/dev profiles when
/// Hermod:Security:AuthBypass=true. NEVER register this in prod.
/// </summary>
public sealed class AuthBypassHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    /// <summary>Scheme name registered with ASP.NET auth.</summary>
    public const string SchemeName = "AuthBypass";

    /// <summary>Creates the handler.</summary>
    public AuthBypassHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder) { }

    /// <inheritdoc/>
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var claims = new[]
        {
            new Claim("sub", "00000000-0000-0000-0000-000000000000"),
            new Claim(ClaimTypes.Name, "auth-bypass"),
            new Claim(ClaimTypes.Role, "admin"),
            new Claim(ClaimTypes.Role, "operator"),
            new Claim(ClaimTypes.Role, "viewer"),
        };
        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
