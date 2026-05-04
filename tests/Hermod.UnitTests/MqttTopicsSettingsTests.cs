using Hermod.Core.Configuration;
using Xunit;

namespace Hermod.UnitTests;

/// <summary>
/// Tests for <see cref="MqttTopicsSettings.IsSystemTopic"/>. Covers the
/// refactor that moved the hardcoded prefix list out of
/// <c>MessageProcessor</c> into the config-driven
/// <see cref="MqttTopicsSettings.SystemTopicPrefixes"/>, plus the
/// helper extraction that followed.
/// </summary>
public class MqttTopicsSettingsTests
{
    [Fact]
    public void IsSystemTopic_DefaultPrefixes_MatchesAlertsAndHermod()
    {
        var sut = new MqttTopicsSettings();

        Assert.True(sut.IsSystemTopic("alerts/temperature/high"));
        Assert.True(sut.IsSystemTopic("hermod/debug/sensor_1"));
    }

    [Fact]
    public void IsSystemTopic_DefaultPrefixes_DoesNotMatchDeviceTopics()
    {
        var sut = new MqttTopicsSettings();

        Assert.False(sut.IsSystemTopic("zigbee2mqtt/front_door"));
        Assert.False(sut.IsSystemTopic("lora/weather_01"));
        Assert.False(sut.IsSystemTopic("bluetooth/govee_therm_fridge"));
        Assert.False(sut.IsSystemTopic("wifi/shelly_plug_dryer"));
    }

    [Fact]
    public void IsSystemTopic_DefaultPrefixes_DoesNotMatchDeadNamespaces()
    {
        // notifications/ and system/ were dropped from the default
        // prefix list because nothing publishes to them. Regression guard.
        var sut = new MqttTopicsSettings();

        Assert.False(sut.IsSystemTopic("notifications/anything"));
        Assert.False(sut.IsSystemTopic("system/anything"));
    }

    [Fact]
    public void IsSystemTopic_IsCaseSensitivePerMqttSpec()
    {
        // MQTT topics are case-sensitive per spec. A message published on
        // "HERMOD/*" must not be treated as a system topic; an earlier
        // ordinal-ignore-case check let a publisher bypass device
        // discovery with capitalised prefixes.
        var sut = new MqttTopicsSettings();

        Assert.False(sut.IsSystemTopic("HERMOD/debug/sensor_1"));
        Assert.False(sut.IsSystemTopic("Alerts/temperature/high"));
    }

    [Fact]
    public void IsSystemTopic_EmptyOrNullTopic_ReturnsFalse()
    {
        var sut = new MqttTopicsSettings();

        Assert.False(sut.IsSystemTopic(null));
        Assert.False(sut.IsSystemTopic(string.Empty));
    }

    [Fact]
    public void IsSystemTopic_NullPrefixArray_ReturnsFalse()
    {
        var sut = new MqttTopicsSettings
        {
            SystemTopicPrefixes = null!
        };

        Assert.False(sut.IsSystemTopic("alerts/anything"));
        Assert.False(sut.IsSystemTopic("hermod/anything"));
    }

    [Fact]
    public void IsSystemTopic_EmptyPrefixArray_ReturnsFalse()
    {
        var sut = new MqttTopicsSettings
        {
            SystemTopicPrefixes = System.Array.Empty<string>()
        };

        Assert.False(sut.IsSystemTopic("alerts/anything"));
    }

    [Fact]
    public void IsSystemTopic_CustomPrefixes_HonoursOperatorConfig()
    {
        // Validates that the prefix list is a real config knob, not just
        // a rename of the hardcoded literals. If an operator adds a new
        // prefix in appsettings.json, it should take effect.
        var sut = new MqttTopicsSettings
        {
            SystemTopicPrefixes = new[] { "audit/", "siem/" }
        };

        Assert.True(sut.IsSystemTopic("audit/login"));
        Assert.True(sut.IsSystemTopic("siem/event_1"));
        Assert.False(sut.IsSystemTopic("alerts/temperature/high"));
        Assert.False(sut.IsSystemTopic("hermod/debug/sensor_1"));
    }

    [Fact]
    public void IsSystemTopic_PrefixArrayContainingNullOrEmpty_SkipsThemSafely()
    {
        // Guard against a malformed config where an entry in the array is
        // the empty string. Without a guard, "".StartsWith(x) always returns
        // true for any topic, which would turn every topic into a system
        // topic and silently break device discovery.
        var sut = new MqttTopicsSettings
        {
            SystemTopicPrefixes = new[] { "", "hermod/" }
        };

        Assert.True(sut.IsSystemTopic("hermod/debug/sensor_1"));
        Assert.False(sut.IsSystemTopic("zigbee2mqtt/front_door"));
    }
}
