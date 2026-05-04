using Hermod.Core.Mqtt;
using Xunit;

namespace Hermod.UnitTests;

/// <summary>
/// Pins the single-pattern MQTT topic matcher used by
/// <see cref="Hermod.Infrastructure.Database.PostgresRulesService.GetMatchingRulesAsync"/>
/// and by the topology page's device-to-rule edge resolver. Wildcard
/// semantics must match the MQTT 5.0 spec for <c>+</c> (one segment) and
/// <c>#</c> (tail multi-segment).
/// </summary>
public class MqttTopicMatcherTests
{
    [Theory]
    [InlineData("zigbee2mqtt/living_room", "zigbee2mqtt/living_room", true)]
    [InlineData("zigbee2mqtt/living_room", "zigbee2mqtt/kitchen", false)]
    [InlineData("zigbee2mqtt/living_room", "zigbee2mqtt/living_room/set", false)]
    public void LiteralPatterns_MatchExactly(string pattern, string topic, bool expected)
    {
        Assert.Equal(expected, MqttTopicMatcher.IsMatch(pattern, topic));
    }

    [Theory]
    [InlineData("zigbee2mqtt/+", "zigbee2mqtt/sensor_01", true)]
    [InlineData("zigbee2mqtt/+", "zigbee2mqtt/sensor_01/set", false)] // + is single segment only
    [InlineData("zigbee2mqtt/+/set", "zigbee2mqtt/sensor_01/set", true)]
    [InlineData("zigbee2mqtt/+/set", "zigbee2mqtt/sensor_01/get", false)]
    [InlineData("+/+/set", "zigbee2mqtt/sensor_01/set", true)]
    public void SingleSegmentWildcard_MatchesExactlyOneSegment(string pattern, string topic, bool expected)
    {
        Assert.Equal(expected, MqttTopicMatcher.IsMatch(pattern, topic));
    }

    [Theory]
    [InlineData("zigbee2mqtt/#", "zigbee2mqtt/sensor_01", true)]
    [InlineData("zigbee2mqtt/#", "zigbee2mqtt/sensor_01/set", true)]
    [InlineData("zigbee2mqtt/#", "zigbee2mqtt/bridge/event", true)]
    [InlineData("zigbee2mqtt/#", "lora/sensor_01", false)]
    public void MultiSegmentWildcard_MatchesAnyTail(string pattern, string topic, bool expected)
    {
        Assert.Equal(expected, MqttTopicMatcher.IsMatch(pattern, topic));
    }

    [Theory]
    [InlineData("#", "anything", true)]
    [InlineData("#", "multi/level/topic", true)]
    [InlineData("#", "", true)]
    public void BareHashPattern_MatchesEverything(string pattern, string topic, bool expected)
    {
        Assert.Equal(expected, MqttTopicMatcher.IsMatch(pattern, topic));
    }

    [Theory]
    [InlineData("", "any/topic", true)]
    [InlineData(null, "any/topic", true)]
    public void EmptyOrNullPattern_MatchesEverything(string? pattern, string topic, bool expected)
    {
        Assert.Equal(expected, MqttTopicMatcher.IsMatch(pattern, topic));
    }

    [Theory]
    [InlineData("zigbee2mqtt/+", "", false)]
    [InlineData("zigbee2mqtt/+", null, false)]
    public void EmptyOrNullTopic_DoesNotMatchNonBareHashPattern(string pattern, string? topic, bool expected)
    {
        Assert.Equal(expected, MqttTopicMatcher.IsMatch(pattern, topic));
    }

    [Fact]
    public void CaseSensitive_AsPerMqttSpec()
    {
        // MQTT topic segments are case-sensitive per the spec.
        Assert.False(MqttTopicMatcher.IsMatch("zigbee2mqtt/Sensor", "zigbee2mqtt/sensor"));
        Assert.False(MqttTopicMatcher.IsMatch("zigbee2mqtt/sensor", "Zigbee2MQTT/sensor"));
    }

    [Theory]
    [InlineData("a/+/c/#", "a/b/c/d/e", true)]
    [InlineData("a/+/c/#", "a/b/c", true)]        // # matches zero-or-more tail
    [InlineData("a/+/c/#", "a/b/d/e", false)]     // literal c mismatch
    public void ComplexCombinedWildcards_Resolve(string pattern, string topic, bool expected)
    {
        Assert.Equal(expected, MqttTopicMatcher.IsMatch(pattern, topic));
    }
}
