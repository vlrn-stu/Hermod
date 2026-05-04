using Hermod.Rules.Indexing;
using Xunit;

namespace Hermod.UnitTests;

/// <summary>
/// Pins the hot-path rule-indexing trie used by
/// <c>EnhancedRulesEngine</c>. Match must be O(segments) in the topic
/// depth regardless of how many patterns were inserted, and wildcard
/// semantics have to agree with the single-pattern
/// <see cref="Hermod.Core.Mqtt.MqttTopicMatcher"/>.
/// </summary>
public class TopicTrieTests
{
    [Fact]
    public void Insert_ExactPattern_MatchedByExactTopic()
    {
        var trie = new TopicTrie<string>();
        trie.Insert("zigbee2mqtt/sensor_01", "rule-a");

        var hits = trie.Match("zigbee2mqtt/sensor_01");

        Assert.Single(hits);
        Assert.Contains("rule-a", hits);
    }

    [Fact]
    public void Insert_ExactPattern_NotMatchedByDifferentTopic()
    {
        var trie = new TopicTrie<string>();
        trie.Insert("zigbee2mqtt/sensor_01", "rule-a");

        Assert.Empty(trie.Match("zigbee2mqtt/sensor_02"));
        Assert.Empty(trie.Match("zigbee2mqtt/sensor_01/set"));
    }

    [Fact]
    public void PlusWildcard_MatchesAnySingleSegment()
    {
        var trie = new TopicTrie<string>();
        trie.Insert("zigbee2mqtt/+", "rule-a");

        Assert.Contains("rule-a", trie.Match("zigbee2mqtt/sensor_01"));
        Assert.Contains("rule-a", trie.Match("zigbee2mqtt/bridge"));
        // + does not match two segments
        Assert.Empty(trie.Match("zigbee2mqtt/sensor_01/set"));
    }

    [Fact]
    public void HashWildcard_MatchesTail()
    {
        var trie = new TopicTrie<string>();
        trie.Insert("zigbee2mqtt/#", "rule-tail");

        Assert.Contains("rule-tail", trie.Match("zigbee2mqtt/sensor_01"));
        Assert.Contains("rule-tail", trie.Match("zigbee2mqtt/sensor_01/set"));
        Assert.Contains("rule-tail", trie.Match("zigbee2mqtt/bridge/event"));
        Assert.Empty(trie.Match("lora/sensor_01"));
    }

    [Fact]
    public void BareHashPattern_MatchesAll()
    {
        var trie = new TopicTrie<string>();
        trie.Insert("#", "catch-all");

        Assert.Contains("catch-all", trie.Match("anything"));
        Assert.Contains("catch-all", trie.Match("a/b/c"));
    }

    [Fact]
    public void MultiplePatterns_OverlappingMatchesReturnAll()
    {
        var trie = new TopicTrie<string>();
        trie.Insert("zigbee2mqtt/#", "tail");
        trie.Insert("zigbee2mqtt/+", "single");
        trie.Insert("zigbee2mqtt/sensor_01", "exact");
        trie.Insert("#", "catchAll");

        var hits = trie.Match("zigbee2mqtt/sensor_01");

        Assert.Contains("exact", hits);
        Assert.Contains("single", hits);
        Assert.Contains("tail", hits);
        Assert.Contains("catchAll", hits);
        Assert.Equal(4, hits.Count);
    }

    [Fact]
    public void Match_CaseSensitive()
    {
        var trie = new TopicTrie<string>();
        trie.Insert("zigbee2mqtt/Sensor", "rule-a");

        Assert.Empty(trie.Match("zigbee2mqtt/sensor"));
        Assert.Contains("rule-a", trie.Match("zigbee2mqtt/Sensor"));
    }

    [Fact]
    public void Insert_NullValue_Throws()
    {
        var trie = new TopicTrie<string>();
        Assert.Throws<ArgumentNullException>(() => trie.Insert("topic", null!));
    }

    [Fact]
    public void Count_ReflectsInsertedPatterns()
    {
        var trie = new TopicTrie<string>();
        Assert.Equal(0, trie.Count);

        trie.Insert("a/b", "1");
        trie.Insert("a/#", "2");
        trie.Insert("+/b", "3");

        Assert.Equal(3, trie.Count);
    }

    [Fact]
    public void Clear_RemovesEverything()
    {
        var trie = new TopicTrie<string>();
        trie.Insert("a/b", "1");
        trie.Insert("a/#", "2");

        trie.Clear();

        Assert.Equal(0, trie.Count);
        Assert.Empty(trie.Match("a/b"));
    }

    [Fact]
    public void MultiplePatternsAtSameNode_AllReturned()
    {
        var trie = new TopicTrie<string>();
        trie.Insert("zigbee2mqtt/sensor_01", "r1");
        trie.Insert("zigbee2mqtt/sensor_01", "r2");
        trie.Insert("zigbee2mqtt/sensor_01", "r3");

        var hits = trie.Match("zigbee2mqtt/sensor_01");
        Assert.Equal(3, hits.Count);
    }

    [Fact]
    public void LargeInsertCount_MatchStillBounded()
    {
        // Scale smoke: 10k exact patterns + one hash wildcard should return
        // only the matching exact + the hash in O(depth).
        var trie = new TopicTrie<int>();
        for (var i = 0; i < 10_000; i++)
        {
            trie.Insert($"devices/bucket/{i}", i);
        }
        trie.Insert("#", -1);

        var hits = trie.Match("devices/bucket/4242");
        Assert.Contains(4242, hits);
        Assert.Contains(-1, hits);
        Assert.Equal(2, hits.Count);
    }
}
