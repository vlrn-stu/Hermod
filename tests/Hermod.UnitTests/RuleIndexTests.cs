using Hermod.Core.Models.Rules;
using Hermod.Rules.Indexing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Hermod.UnitTests;

/// <summary>
/// Pins <see cref="RuleIndex"/>. Rebuilt on every rule-cache
/// invalidation; a regression in the trigger-type router would silently
/// send OnAvailability rules through the message index (or vice versa)
/// so they never fire in the right dispatch path.
/// </summary>
public class RuleIndexTests
{
    private static Rule MakeRule(string id, TriggerType type, string topic, bool enabled = true) => new()
    {
        Id = id,
        Name = id,
        Enabled = enabled,
        Trigger = new RuleTrigger { Type = type, TopicPattern = topic },
    };

    [Fact]
    public void Empty_HasBothTriesEmpty()
    {
        Assert.Empty(RuleIndex.Empty.Messages.Match("zigbee/lamp/state"));
        Assert.Empty(RuleIndex.Empty.Availability.Match("zigbee/lamp/availability"));
    }

    [Fact]
    public void Build_OnMessageRule_LandsInMessagesTrie()
    {
        var r = MakeRule("r1", TriggerType.OnMessage, "zigbee/+/state");

        var index = RuleIndex.Build(new[] { r }, NullLogger.Instance);

        Assert.Contains(r, index.Messages.Match("zigbee/lamp/state"));
        Assert.Empty(index.Availability.Match("zigbee/lamp/state"));
    }

    [Fact]
    public void Build_OnAvailabilityRule_LandsInAvailabilityTrie()
    {
        var r = MakeRule("r1", TriggerType.OnAvailability, "availability/#");

        var index = RuleIndex.Build(new[] { r }, NullLogger.Instance);

        Assert.Contains(r, index.Availability.Match("availability/lamp"));
        Assert.Empty(index.Messages.Match("availability/lamp"));
    }

    [Theory]
    [InlineData(TriggerType.OnStartup)]
    [InlineData(TriggerType.OnSchedule)]
    [InlineData(TriggerType.OnChain)]
    public void Build_DispatchedTriggerTypes_NotIndexed(TriggerType type)
    {
        // These three are dispatched directly by the engine (startup
        // sweep, scheduler tick, chain action) rather than topic-matched.
        // Indexing them would produce phantom matches for any inbound
        // message that happens to share the pattern.
        var r = MakeRule("r1", type, "any/topic");

        var index = RuleIndex.Build(new[] { r }, NullLogger.Instance);

        Assert.Empty(index.Messages.Match("any/topic"));
        Assert.Empty(index.Availability.Match("any/topic"));
    }

    [Fact]
    public void Build_DisabledRules_Skipped()
    {
        var r = MakeRule("r1", TriggerType.OnMessage, "zigbee/+/state", enabled: false);

        var index = RuleIndex.Build(new[] { r }, NullLogger.Instance);

        Assert.Empty(index.Messages.Match("zigbee/lamp/state"));
    }

    [Fact]
    public void Build_MalformedPattern_SkippedWithoutAborting()
    {
        // A single rule's trie insert can throw (e.g. empty pattern).
        // The whole build must continue so one bad rule doesn't block
        // startup — the rest of the index stays valid.
        var good = MakeRule("good", TriggerType.OnMessage, "zigbee/+/state");
        var bad = MakeRule("bad", TriggerType.OnMessage, "");

        var index = RuleIndex.Build(new[] { bad, good }, NullLogger.Instance);

        Assert.Contains(good, index.Messages.Match("zigbee/lamp/state"));
    }

    [Fact]
    public void Build_MultipleRulesSamePattern_AllReturned()
    {
        var r1 = MakeRule("r1", TriggerType.OnMessage, "zigbee/+/state");
        var r2 = MakeRule("r2", TriggerType.OnMessage, "zigbee/+/state");

        var index = RuleIndex.Build(new[] { r1, r2 }, NullLogger.Instance);

        var matches = index.Messages.Match("zigbee/lamp/state").ToList();
        Assert.Equal(2, matches.Count);
        Assert.Contains(r1, matches);
        Assert.Contains(r2, matches);
    }

    [Fact]
    public void Build_MixedRules_RoutedByTriggerType()
    {
        var msg = MakeRule("msg", TriggerType.OnMessage, "zigbee/+/state");
        var avail = MakeRule("avail", TriggerType.OnAvailability, "availability/#");
        var startup = MakeRule("startup", TriggerType.OnStartup, "never/matched");
        var disabled = MakeRule("disabled", TriggerType.OnMessage, "zigbee/+/state", enabled: false);

        var index = RuleIndex.Build(new[] { msg, avail, startup, disabled }, NullLogger.Instance);

        Assert.Contains(msg, index.Messages.Match("zigbee/lamp/state"));
        Assert.Contains(avail, index.Availability.Match("availability/lamp"));
        // startup + disabled both absent.
        Assert.DoesNotContain(startup, index.Messages.Match("never/matched"));
        Assert.DoesNotContain(disabled, index.Messages.Match("zigbee/lamp/state"));
    }

    [Fact]
    public void Build_NullRules_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => RuleIndex.Build(null!, NullLogger.Instance));
    }

    [Fact]
    public void Build_NullLogger_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => RuleIndex.Build(Array.Empty<Rule>(), null!));
    }
}
