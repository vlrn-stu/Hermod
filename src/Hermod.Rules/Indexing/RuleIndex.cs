using Hermod.Core.Models.Rules;
using Microsoft.Extensions.Logging;

namespace Hermod.Rules.Indexing;

/// <summary>
/// Two-trie index over enabled rules: one for live MQTT traffic, one for
/// availability transitions. Excludes OnStartup, OnSchedule, and OnChain
/// rules because those paths are dispatched directly rather than matched
/// against topics.
/// </summary>
public sealed class RuleIndex
{
    /// <summary>Shared empty index, used when no rules are loaded.</summary>
    public static readonly RuleIndex Empty = new(new TopicTrie<Rule>(), new TopicTrie<Rule>());

    /// <summary>Trie of message-trigger patterns (OnMessage, OnCommand, etc.).</summary>
    public TopicTrie<Rule> Messages { get; }

    /// <summary>Trie of availability-trigger patterns.</summary>
    public TopicTrie<Rule> Availability { get; }

    private RuleIndex(TopicTrie<Rule> messages, TopicTrie<Rule> availability)
    {
        Messages = messages;
        Availability = availability;
    }

    /// <summary>
    /// Builds a fresh <see cref="RuleIndex"/> over <paramref name="rules"/>.
    /// Patterns that fail to index are logged via <paramref name="logger"/>
    /// and skipped so a single malformed rule cannot block startup.
    /// </summary>
    public static RuleIndex Build(IEnumerable<Rule> rules, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(logger);

        var messages = new TopicTrie<Rule>();
        var availability = new TopicTrie<Rule>();

        foreach (var rule in rules)
        {
            if (!rule.Enabled) continue;

            var trie = rule.Trigger.Type switch
            {
                TriggerType.OnStartup or TriggerType.OnSchedule or TriggerType.OnChain => null,
                TriggerType.OnAvailability => availability,
                _ => messages,
            };

            if (trie is null) continue;

            try
            {
                trie.Insert(rule.Trigger.TopicPattern, rule);
            }
#pragma warning disable CA1031 // top-level logger-and-continue: one bad pattern must not abort startup indexing
            catch (Exception ex)
#pragma warning restore CA1031
            {
                logger.LogWarning(ex, "Failed to index topic pattern for rule {RuleId}: {Pattern}",
                    rule.Id, rule.Trigger.TopicPattern);
            }
        }

        return new RuleIndex(messages, availability);
    }
}
