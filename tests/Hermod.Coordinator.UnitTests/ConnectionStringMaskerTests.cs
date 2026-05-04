using Hermod.Coordinator.Services;
using Xunit;

namespace Hermod.Coordinator.UnitTests;

/// <summary>
/// Pins the dashboard password-mask. Any regression that re-exposes
/// the raw DB password on the /settings page should flip one of these facts.
/// </summary>
public class ConnectionStringMaskerTests
{
    [Fact]
    public void Mask_Null_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, ConnectionStringMasker.Mask(null));
    }

    [Fact]
    public void Mask_Empty_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, ConnectionStringMasker.Mask(string.Empty));
    }

    [Fact]
    public void Mask_StandardPassword_ReplacesWithAsterisks()
    {
        var masked = ConnectionStringMasker.Mask(
            "Host=db;Port=5432;Database=hermod;Username=hermod_app;Password=hunter2");
        // Canonical builder-produced order may differ; assert on content.
        Assert.Contains("Password=***", masked);
        Assert.DoesNotContain("hunter2", masked);
        Assert.Contains("Host=db", masked);
        Assert.Contains("Username=hermod_app", masked);
    }

    [Fact]
    public void Mask_NoPassword_LeavesConnectionStringIntact()
    {
        // When there is no Password field, the builder path runs and
        // leaves the string unchanged (well, re-serialized but with
        // the same fields).
        var masked = ConnectionStringMasker.Mask(
            "Host=db;Port=5432;Database=hermod;Username=hermod_app");
        Assert.DoesNotContain("***", masked);
        Assert.DoesNotContain("Password", masked);
        Assert.Contains("Host=db", masked);
        Assert.Contains("Username=hermod_app", masked);
    }

    [Fact]
    public void Mask_PasswordContainingSemicolon_StillMasked()
    {
        // Passwords with `;` must be quoted in the connection string.
        // The builder handles that correctly when parsing the input.
        var masked = ConnectionStringMasker.Mask(
            "Host=db;Port=5432;Database=hermod;Username=u;Password='pa;ss'");
        Assert.Contains("Password=***", masked);
        Assert.DoesNotContain("pa;ss", masked);
        Assert.DoesNotContain("pa", masked.Replace("Password=***", ""));
    }

    [Fact]
    public void Mask_PasswordContainingEquals_StillMasked()
    {
        var masked = ConnectionStringMasker.Mask(
            "Host=db;Port=5432;Database=hermod;Username=u;Password='x=y'");
        Assert.Contains("Password=***", masked);
        Assert.DoesNotContain("x=y", masked);
    }

    [Fact]
    public void Mask_MalformedInput_FallsBackToRegex()
    {
        // This input has an illegal fragment that NpgsqlConnectionStringBuilder
        // rejects. The regex fallback kicks in and still masks the Password.
        var malformed = "not a real connection string Password=topsecret garbage=== ;;";
        var masked = ConnectionStringMasker.Mask(malformed);
        // Regex may produce different shapes across the two paths, but
        // in every case "topsecret" must be gone and "Password=***"
        // must appear somewhere.
        Assert.DoesNotContain("topsecret", masked);
        Assert.Contains("Password=***", masked);
    }

    [Fact]
    public void Mask_CaseInsensitivePasswordKey_StillMasked()
    {
        // Npgsql builder accepts `PASSWORD=` and `password=` as the
        // same key. Both forms of input should come out masked.
        var uppered = ConnectionStringMasker.Mask(
            "Host=db;Port=5432;Database=hermod;Username=u;PASSWORD=leakme");
        Assert.Contains("Password=***", uppered);
        Assert.DoesNotContain("leakme", uppered);
    }

    [Fact]
    public void Mask_Whitespace_ReturnsMaskedOrEmpty()
    {
        // A whitespace-only string is technically non-empty. Builder
        // should accept it (returns empty builder) and produce an
        // empty-ish connection string with no Password field.
        var masked = ConnectionStringMasker.Mask("   ");
        Assert.DoesNotContain("***", masked);
        Assert.DoesNotContain("Password", masked);
    }
}
