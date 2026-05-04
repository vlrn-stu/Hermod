using Hermod.Core.Configuration;
using Microsoft.AspNetCore.Authorization;

namespace Hermod.Coordinator.Authorization;

/// <summary>
/// Three-role RBAC for Hermod. Roles arrive on the Vault42 JWT as a
/// "roles": [...] claim and the Vault42AuthStateProvider folds each
/// entry into <see cref="System.Security.Claims.ClaimTypes.Role"/>, so
/// these policies just compose role checks via IsInRole.
/// <para>
/// Hierarchy: admin ⊃ operator ⊃ viewer. Every higher role inherits
/// every lower role's permissions, which keeps endpoint attribution
/// minimal — a viewer-only endpoint stays usable by operators and
/// admins without explicit OR clauses on each method.
/// </para>
/// <para>
/// Policy names (<see cref="Viewer"/>, <see cref="Operator"/>,
/// <see cref="Admin"/>) are stable string keys used by
/// <c>[Authorize(Policy = ...)]</c>. The role strings IsInRole checks
/// for admin / super_admin come from <see cref="AuthSettings"/> so a
/// deployment can rename either tier without recompiling.
/// </para>
/// </summary>
public static class Policies
{
    /// <summary>Read-only access to /api/* GET endpoints + the dashboard.</summary>
    public const string Viewer = "viewer";

    /// <summary>Mutating access to device + rule write endpoints.</summary>
    public const string Operator = "operator";

    /// <summary>Cluster-admin access to /admin/* gateway + user management.</summary>
    public const string Admin = "admin";

    /// <summary>
    /// Wires the three policies into the global authorization options.
    /// Call from Program.cs inside <c>AddAuthorization(o => ...)</c>.
    /// </summary>
    /// <param name="options">The authorization options to add policies to.</param>
    /// <param name="auth">
    /// Auth settings supplying the configurable role strings. Null falls
    /// back to defaults (<c>"admin"</c> / <c>"super_admin"</c>) — same
    /// behaviour as before this knob existed.
    /// </param>
    public static void Register(AuthorizationOptions options, AuthSettings? auth = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        auth ??= new AuthSettings();
        var adminRole = string.IsNullOrWhiteSpace(auth.AdminRole) ? "admin" : auth.AdminRole;
        var superAdminRole = string.IsNullOrWhiteSpace(auth.SuperAdminRole) ? "super_admin" : auth.SuperAdminRole;

        options.AddPolicy(Viewer, p => p.RequireAssertion(c =>
            c.User.IsInRole(Viewer) ||
            c.User.IsInRole(Operator) ||
            c.User.IsInRole(adminRole)));

        options.AddPolicy(Operator, p => p.RequireAssertion(c =>
            c.User.IsInRole(Operator) ||
            c.User.IsInRole(adminRole)));

        // vault42 emits super_admin for AdminUser-tier accounts and
        // admin for the gateway's mid-tier admin role; both grant the
        // /admin/* gateway in Hermod's RBAC. RequireRole(adminRole) alone
        // would reject super_admin even though it's strictly higher.
        options.AddPolicy(Admin, p => p.RequireAssertion(c =>
            c.User.IsInRole(adminRole) ||
            c.User.IsInRole(superAdminRole)));
    }
}
