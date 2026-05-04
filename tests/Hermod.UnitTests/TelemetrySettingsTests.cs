using System.Collections.Generic;
using Hermod.Core.Configuration;
using Xunit;

namespace Hermod.UnitTests;

/// <summary>
/// Tests for the <c>HERMOD_TIMESTAMPS_CSV</c> env-var overlay on
/// <see cref="TelemetrySettings"/>. Matrix runs need the overlay to win
/// over whatever the <c>Hermod:Telemetry:TimestampsCsvPath</c> config
/// section bound so the run script can inject the per-run path without
/// re-rolling the coordinator image.
/// </summary>
public class TelemetrySettingsTests
{
    [Fact]
    public void TimestampsCsvPath_Default_IsNull()
    {
        var sut = new TelemetrySettings();

        Assert.Null(sut.TimestampsCsvPath);
    }

    [Fact]
    public void ApplyEnvironmentOverrides_PathSet_OverwritesExisting()
    {
        var sut = new TelemetrySettings { TimestampsCsvPath = "/from-config.csv" };
        var env = new Dictionary<string, string?>
        {
            [TelemetrySettings.TimestampsCsvPathEnvVar] = "/from-env.csv",
        };

        sut.ApplyEnvironmentOverrides(k => env.TryGetValue(k, out var v) ? v : null);

        Assert.Equal("/from-env.csv", sut.TimestampsCsvPath);
    }

    [Fact]
    public void ApplyEnvironmentOverrides_PathUnset_LeavesConfigValue()
    {
        var sut = new TelemetrySettings { TimestampsCsvPath = "/from-config.csv" };

        sut.ApplyEnvironmentOverrides(_ => null);

        Assert.Equal("/from-config.csv", sut.TimestampsCsvPath);
    }

    [Fact]
    public void ApplyEnvironmentOverrides_PathWhitespace_LeavesConfigValue()
    {
        var sut = new TelemetrySettings { TimestampsCsvPath = "/from-config.csv" };

        sut.ApplyEnvironmentOverrides(_ => "   ");

        Assert.Equal("/from-config.csv", sut.TimestampsCsvPath);
    }

    [Fact]
    public void ApplyEnvironmentOverrides_PathTrimmedBeforeStore()
    {
        var sut = new TelemetrySettings();

        sut.ApplyEnvironmentOverrides(_ => "  /run/timestamps.csv  ");

        Assert.Equal("/run/timestamps.csv", sut.TimestampsCsvPath);
    }

    [Fact]
    public void EnvVarName_MatchesCleanupSpec()
    {
        Assert.Equal("HERMOD_TIMESTAMPS_CSV", TelemetrySettings.TimestampsCsvPathEnvVar);
    }
}
