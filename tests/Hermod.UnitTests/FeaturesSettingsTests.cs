using System;
using System.Collections.Generic;
using Hermod.Core.Configuration;
using Xunit;

namespace Hermod.UnitTests;

/// <summary>
/// Tests for <see cref="FeaturesSettings.UuidTrace"/> and its
/// single-underscore environment-variable overlay. Matrix profiles
/// toggle tracing via <c>HERMOD_UUID_TRACE_ENABLED</c>; the overlay
/// must win over whatever the <c>Hermod:Features:UuidTrace</c>
/// config binding produced so trace-baseline runs need only set the
/// env var on the coordinator pod.
/// </summary>
public class FeaturesSettingsTests
{
    [Fact]
    public void UuidTrace_Default_IsOff()
    {
        var sut = new FeaturesSettings();

        Assert.False(sut.UuidTrace);
    }

    [Fact]
    public void ApplyEnvironmentOverrides_EnvVarUnset_LeavesFlagUnchanged()
    {
        var sut = new FeaturesSettings { UuidTrace = false };

        sut.ApplyEnvironmentOverrides(_ => null);

        Assert.False(sut.UuidTrace);
    }

    [Fact]
    public void ApplyEnvironmentOverrides_EnvVarTrue_FlipsFlagOn()
    {
        var sut = new FeaturesSettings { UuidTrace = false };
        var env = new Dictionary<string, string?>
        {
            [FeaturesSettings.UuidTraceEnvVar] = "true",
        };

        sut.ApplyEnvironmentOverrides(k => env.TryGetValue(k, out var v) ? v : null);

        Assert.True(sut.UuidTrace);
    }

    [Fact]
    public void ApplyEnvironmentOverrides_EnvVarFalse_FlipsFlagOff()
    {
        var sut = new FeaturesSettings { UuidTrace = true };
        var env = new Dictionary<string, string?>
        {
            [FeaturesSettings.UuidTraceEnvVar] = "false",
        };

        sut.ApplyEnvironmentOverrides(k => env.TryGetValue(k, out var v) ? v : null);

        Assert.False(sut.UuidTrace);
    }

    [Theory]
    [InlineData("True")]
    [InlineData("TRUE")]
    [InlineData("  true  ")]
    public void ApplyEnvironmentOverrides_CaseAndWhitespaceInsensitive(string raw)
    {
        var sut = new FeaturesSettings { UuidTrace = false };

        sut.ApplyEnvironmentOverrides(_ => raw);

        Assert.True(sut.UuidTrace);
    }

    [Fact]
    public void ApplyEnvironmentOverrides_NonBooleanString_LeavesFlagUnchanged()
    {
        var sut = new FeaturesSettings { UuidTrace = false };

        sut.ApplyEnvironmentOverrides(_ => "yes");

        Assert.False(sut.UuidTrace);
    }

    [Fact]
    public void ApplyEnvironmentOverrides_EmptyString_LeavesFlagUnchanged()
    {
        var sut = new FeaturesSettings { UuidTrace = true };

        sut.ApplyEnvironmentOverrides(_ => string.Empty);

        Assert.True(sut.UuidTrace);
    }

    [Fact]
    public void ApplyEnvironmentOverrides_NullReader_Throws()
    {
        var sut = new FeaturesSettings();

        Assert.Throws<ArgumentNullException>(() => sut.ApplyEnvironmentOverrides(null!));
    }

    [Fact]
    public void EnvVarName_MatchesCleanupSpec()
    {
        Assert.Equal("HERMOD_UUID_TRACE_ENABLED", FeaturesSettings.UuidTraceEnvVar);
    }
}
