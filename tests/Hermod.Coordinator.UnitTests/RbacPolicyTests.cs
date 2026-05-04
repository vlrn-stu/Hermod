using System.Security.Claims;
using Hermod.Coordinator.Authorization;
using Hermod.Core.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Hermod.Coordinator.UnitTests;

/// <summary>
/// Pins the contract for the three RBAC policies registered by
/// <see cref="Policies.Register"/>: hierarchy (admin ⊃ operator ⊃ viewer),
/// super-admin always passes Admin, and the role strings the IsInRole
/// checks consume come from <see cref="AuthSettings"/> so deployments can
/// rebind without a recompile.
/// </summary>
public class RbacPolicyTests
{
    private static IAuthorizationService BuildAuthService(AuthSettings? auth = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization(options => Policies.Register(options, auth));
        return services.BuildServiceProvider().GetRequiredService<IAuthorizationService>();
    }

    private static ClaimsPrincipal UserWithRoles(params string[] roles)
    {
        var claims = roles.Select(r => new Claim(ClaimTypes.Role, r));
        var identity = new ClaimsIdentity(claims, authenticationType: "Test");
        return new ClaimsPrincipal(identity);
    }

    [Theory]
    [InlineData("viewer",   Policies.Viewer,   true)]
    [InlineData("viewer",   Policies.Operator, false)]
    [InlineData("viewer",   Policies.Admin,    false)]
    [InlineData("operator", Policies.Viewer,   true)]
    [InlineData("operator", Policies.Operator, true)]
    [InlineData("operator", Policies.Admin,    false)]
    [InlineData("admin",    Policies.Viewer,   true)]
    [InlineData("admin",    Policies.Operator, true)]
    [InlineData("admin",    Policies.Admin,    true)]
    public async Task Default_Hierarchy_AllowsLowerOrEqualTier(string role, string policy, bool expected)
    {
        var authz = BuildAuthService();
        var user = UserWithRoles(role);

        var result = await authz.AuthorizeAsync(user, resource: null, policy);

        Assert.Equal(expected, result.Succeeded);
    }

    // super_admin always passes Admin (Vault42 emits it standalone, no admin
    // role accompanying it).
    [Fact]
    public async Task SuperAdmin_PassesAdminPolicy_ByDefault()
    {
        var authz = BuildAuthService();
        var user = UserWithRoles("super_admin");

        var result = await authz.AuthorizeAsync(user, resource: null, Policies.Admin);

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData(Policies.Viewer)]
    [InlineData(Policies.Operator)]
    [InlineData(Policies.Admin)]
    public async Task UserWithNoRoles_FailsEveryPolicy(string policy)
    {
        var authz = BuildAuthService();
        var user = UserWithRoles();

        var result = await authz.AuthorizeAsync(user, resource: null, policy);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task ConfiguredAdminRole_RebindsAdminPolicy()
    {
        var authz = BuildAuthService(new AuthSettings { AdminRole = "elevated_admin" });

        var elevated = await authz.AuthorizeAsync(UserWithRoles("elevated_admin"), resource: null, Policies.Admin);
        Assert.True(elevated.Succeeded);

        var literalAdmin = await authz.AuthorizeAsync(UserWithRoles("admin"), resource: null, Policies.Admin);
        Assert.False(literalAdmin.Succeeded);
    }

    [Fact]
    public async Task ConfiguredSuperAdminRole_RebindsAdminPolicy()
    {
        var authz = BuildAuthService(new AuthSettings { SuperAdminRole = "root" });

        var rebound = await authz.AuthorizeAsync(UserWithRoles("root"), resource: null, Policies.Admin);
        Assert.True(rebound.Succeeded);

        var literalSuper = await authz.AuthorizeAsync(UserWithRoles("super_admin"), resource: null, Policies.Admin);
        Assert.False(literalSuper.Succeeded);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public async Task BlankAdminRoleSetting_FallsBackToDefault(string? blank)
    {
        var authz = BuildAuthService(new AuthSettings { AdminRole = blank! });

        var result = await authz.AuthorizeAsync(UserWithRoles("admin"), resource: null, Policies.Admin);

        Assert.True(result.Succeeded);
    }
}
