namespace Hermod.Core.Mqtt;

/// <summary>
/// Single-pattern MQTT topic matcher supporting <c>+</c> (single segment)
/// and <c>#</c> (tail multi-segment) wildcards. For dispatching one topic
/// against many patterns on the hot path, prefer the segment-trie index
/// in <c>Hermod.Rules.Indexing</c>.
/// </summary>
public static class MqttTopicMatcher
{
    /// <summary>True when <paramref name="topic"/> matches <paramref name="pattern"/>.</summary>
    /// <param name="pattern">MQTT pattern; null/empty matches any topic.</param>
    /// <param name="topic">Topic to test.</param>
    public static bool IsMatch(string? pattern, string? topic)
    {
        if (string.IsNullOrEmpty(pattern)) return true;
        if (pattern == "#") return true;
        if (string.IsNullOrEmpty(topic)) return false;

        var patternParts = pattern.Split('/');
        var topicParts = topic.Split('/');

        for (var i = 0; i < patternParts.Length; i++)
        {
            var part = patternParts[i];
            if (part == "#") return i == patternParts.Length - 1;
            if (i >= topicParts.Length) return false;
            if (part != "+" && !string.Equals(part, topicParts[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return patternParts.Length == topicParts.Length;
    }
}
