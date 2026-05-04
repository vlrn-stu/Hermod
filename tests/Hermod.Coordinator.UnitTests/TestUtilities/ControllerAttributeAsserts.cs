using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Hermod.Coordinator.UnitTests.TestUtilities;

/// <summary>
/// Shared reflection-based assertion helpers for controller authorization
/// metadata. Extracted after <c>BackupController</c> and
/// <c>ActionsController</c> grew nearly identical blocks of reflection
/// tests. Each helper flips a test if a future refactor silently removes
/// a hardening attribute.
/// </summary>
public static class ControllerAttributeAsserts
{
    /// <summary>
    /// Asserts that <typeparamref name="TController"/> carries a class-level
    /// <see cref="AuthorizeAttribute"/> with the expected role list.
    /// </summary>
    public static void AssertClassAuthorize<TController>(string expectedRoles)
    {
        var attr = typeof(TController)
            .GetCustomAttribute<AuthorizeAttribute>(inherit: false);
        Assert.NotNull(attr);
        Assert.Equal(expectedRoles, attr!.Roles);
    }

    /// <summary>
    /// Asserts that <typeparamref name="TController"/> has the class-level
    /// <see cref="AuthorizeAttribute"/> at all (role unspecified).
    /// </summary>
    public static void AssertHasClassAuthorize<TController>()
    {
        var attr = typeof(TController)
            .GetCustomAttribute<AuthorizeAttribute>(inherit: false);
        Assert.NotNull(attr);
    }

    /// <summary>
    /// Walks every public declared action method on
    /// <typeparamref name="TController"/> (methods that return
    /// <see cref="Task"/>, <c>Task&lt;...&gt;</c>, or
    /// <see cref="IActionResult"/>) and asserts that none carry
    /// <see cref="AllowAnonymousAttribute"/>. An AllowAnonymous on any
    /// endpoint would punch through the class-level authorization gate.
    /// </summary>
    public static void AssertNoAllowAnonymousOnEndpoints<TController>()
    {
        var actionMethods = typeof(TController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => typeof(Task).IsAssignableFrom(m.ReturnType)
                     || typeof(IActionResult).IsAssignableFrom(m.ReturnType))
            .ToList();

        Assert.NotEmpty(actionMethods);
        foreach (var m in actionMethods)
        {
            var allowAnonymous = m.GetCustomAttribute<AllowAnonymousAttribute>(inherit: false);
            Assert.True(
                allowAnonymous is null,
                $"{typeof(TController).Name}.{m.Name} has [AllowAnonymous] which bypasses the class-level authorization gate");
        }
    }

    /// <summary>
    /// Asserts that <typeparamref name="TController"/> declares each of
    /// the expected action method names. Used as a defence against
    /// silent renames that would otherwise leave
    /// <see cref="AssertNoAllowAnonymousOnEndpoints{TController}"/>
    /// covering zero methods.
    /// </summary>
    public static void AssertEndpointMethodsPresent<TController>(params string[] expectedNames)
    {
        var names = typeof(TController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(m => m.Name)
            .ToHashSet();

        foreach (var expected in expectedNames)
        {
            Assert.Contains(expected, names);
        }
    }
}
