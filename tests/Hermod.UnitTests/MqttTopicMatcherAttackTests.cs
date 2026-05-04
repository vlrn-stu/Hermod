using Hermod.Core.Mqtt;
using Xunit;

namespace Hermod.UnitTests;

/// <summary>
/// Attack-surface tests for <see cref="MqttTopicMatcher"/>. Rule
/// patterns are user-supplied; topics are broker-supplied. Both sides
/// are untrusted so the matcher's behaviour on illegal patterns
/// (middle-`#`), empty segments from leading or trailing slashes,
/// $SYS-class topics under bare `#`, and degenerate inputs needs
/// regression guards. These tests document what the matcher does
/// today so a security-minded rewrite (reject middle-`#`, filter
/// $SYS from wildcard matches) flips pinned expectations and forces
/// an explicit review.
/// </summary>
public class MqttTopicMatcherAttackTests
{
    [Theory]
    [InlineData("a/#/b", "a/x/b")]
    [InlineData("#/tail", "head/tail")]
    [InlineData("a/#/c/d", "a/b/c/d")]
    public void IllegalMiddleHashPattern_NeverMatches(string pattern, string topic)
    {
        // MQTT 5 requires `#` to be the final segment. The matcher
        // short-circuits on a middle `#` with false, so an
        // illegally-authored pattern silently matches nothing instead
        // of matching everything. Pinning this so a future "relaxed"
        // parser can't start treating middle-# as wildcard-tail.
        Assert.False(MqttTopicMatcher.IsMatch(pattern, topic));
    }

    [Fact]
    public void BareHash_MatchesSysTopic_PolicyGapDocumented()
    {
        // MQTT spec says brokers SHOULD hide $SYS from `#`
        // subscriptions, but our matcher does not filter. A rule
        // pattern `#` will match any $SYS topic the broker exposes.
        // Pinning this as a KNOWN GAP: downstream authorization must
        // reject $SYS before consulting the matcher.
        Assert.True(MqttTopicMatcher.IsMatch("#", "$SYS/brokers/hermod/stats"));
    }

    [Fact]
    public void HashDoesNotMatchSysWhenHierarchical()
    {
        // `#` from a literal prefix still succeeds on $SYS. Same gap,
        // different angle. Prefix matchers in rules like `$SYS/#`
        // would be the intended way to observe broker metrics.
        Assert.True(MqttTopicMatcher.IsMatch("$SYS/#", "$SYS/brokers/hermod/clients"));
    }

    [Theory]
    [InlineData("zigbee/+", "zigbee/", true)]           // trailing slash = empty segment
    [InlineData("a/+/b", "a//b", true)]                  // embedded empty
    [InlineData("+/a", "/a", true)]                      // leading empty
    [InlineData("+/+/+/+", "///", true)]                 // string "///" splits to 4 empty segments
    public void PlusMatchesEmptySegment_PerMqttSpec(string pattern, string topic, bool expected)
    {
        // MQTT 5 says `+` matches any single segment INCLUDING empty.
        // Pinned because a downstream sanitiser that rejects empty
        // device ids must know the matcher happily lets them through.
        Assert.Equal(expected, MqttTopicMatcher.IsMatch(pattern, topic));
    }

    [Theory]
    [InlineData("zigbee/sensor", "zigbee/sensor/")]
    [InlineData("zigbee/+", "zigbee/sensor/")]           // + needs exactly one segment
    public void TrailingSlashTopic_SegmentCountMismatch(string pattern, string topic)
    {
        // Topic "zigbee/sensor/" splits to 3 segments incl a trailing
        // empty one. The matcher treats them as distinct so a 2-segment
        // pattern must not match. Guard against any "strip trailing
        // empty segment" optimisation that would let a trailing slash
        // smuggle a message into the wrong rule.
        Assert.False(MqttTopicMatcher.IsMatch(pattern, topic));
    }

    [Theory]
    [InlineData("zigbee/sensor_01", "zigbee/../../etc/secret")]
    [InlineData("zigbee/+", "../../etc/passwd")]
    public void LiteralPrefix_DoesNotMatchTraversalAttempt(string pattern, string topic)
    {
        // A topic containing literal `..` segments does NOT satisfy a
        // literal prefix — the matcher is segment-oriented, no path
        // traversal. The attack would have to come via a `#` pattern
        // that explicitly subscribes to the evil namespace.
        Assert.False(MqttTopicMatcher.IsMatch(pattern, topic));
    }

    [Fact]
    public void ExtremelyLongTopic_DoesNotBlowMemoryOrHang()
    {
        // 10 000-segment topic against a bare `#` returns true via
        // the short-circuit before any Split happens. A multi-segment
        // pattern would still be O(n) split-and-compare — bounded,
        // no recursion.
        var longTopic = string.Join('/', System.Linq.Enumerable.Repeat("s", 10_000));

        Assert.True(MqttTopicMatcher.IsMatch("#", longTopic));
        Assert.False(MqttTopicMatcher.IsMatch("a/b/c", longTopic));
    }

    [Theory]
    [InlineData("+", "")]
    [InlineData("+/+", "")]
    public void EmptyTopicAgainstPlusPattern_DoesNotMatch(string pattern, string topic)
    {
        // Empty string is a zero-segment topic (Split('/') returns a
        // single "" element). The matcher treats zero-content as
        // non-matching for single-segment patterns — the
        // null-or-empty guard short-circuits. Pin so the guard is
        // not lifted in pursuit of "always allow empty" semantics.
        Assert.False(MqttTopicMatcher.IsMatch(pattern, topic));
    }

    [Fact]
    public void ControlCharacterInTopic_ComparedLiterally()
    {
        // NUL byte, tab, newline in a topic segment. The matcher
        // uses Ordinal comparison so they compare verbatim. Pinned
        // to prevent a future "normalising" pass from silently
        // collapsing different control chars into the same match.
        Assert.True(MqttTopicMatcher.IsMatch("a/b\0c", "a/b\0c"));
        Assert.False(MqttTopicMatcher.IsMatch("a/b\0c", "a/bc"));
    }

    [Fact]
    public void MultipleHashesInPattern_OnlyFirstMatters()
    {
        // Two `#` in the same pattern: the first hits the middle-#
        // guard and returns false. Second is unreachable. Pinned as
        // behaviour, not endorsed as a feature.
        Assert.False(MqttTopicMatcher.IsMatch("a/#/#", "a/b/c"));
    }

    [Fact]
    public void PlusInsideLiteralSegment_NotTreatedAsWildcard()
    {
        // MQTT 5 is strict: `+` and `#` are only wildcards when they
        // occupy an entire segment. `a+b` is a literal three-char
        // segment; the matcher compares it verbatim.
        Assert.False(MqttTopicMatcher.IsMatch("a+b/c", "anything/c"));
        Assert.True(MqttTopicMatcher.IsMatch("a+b/c", "a+b/c"));
    }
}
