namespace Hermod.Rules.Indexing;

/// <summary>
/// Segment-trie index for MQTT topic patterns with <c>+</c> (single-segment)
/// and <c>#</c> (multi-segment tail) wildcards. <see cref="Match"/> returns
/// every value whose pattern matches a given concrete topic in time
/// proportional to the topic's segment depth, independent of how many
/// patterns have been inserted.
/// </summary>
public sealed class TopicTrie<T>
{
    private readonly Node _root = new();
    private int _count;

    /// <summary>Total number of values indexed, counted per pattern/value pair.</summary>
    public int Count => _count;

    /// <summary>
    /// Inserts <paramref name="value"/> under the MQTT filter
    /// <paramref name="pattern"/>. Both literal segments and the <c>+</c>/<c>#</c>
    /// wildcards are accepted.
    /// </summary>
    public void Insert(string pattern, T value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var segments = SplitPattern(pattern);
        var node = _root;

        for (var i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];

            if (segment == "#")
            {
                node.HashValues ??= [];
                node.HashValues.Add(value);
                _count++;
                return;
            }

            if (segment == "+")
            {
                node.Plus ??= new Node();
                node = node.Plus;
                continue;
            }

            node.Children ??= new Dictionary<string, Node>(StringComparer.Ordinal);
            if (!node.Children.TryGetValue(segment, out var child))
            {
                child = new Node();
                node.Children[segment] = child;
            }
            node = child;
        }

        node.ExactValues ??= [];
        node.ExactValues.Add(value);
        _count++;
    }

    /// <summary>
    /// Returns every value whose pattern matches the concrete MQTT
    /// <paramref name="topic"/>. Order reflects trie traversal; duplicates are
    /// not deduplicated.
    /// </summary>
    public List<T> Match(string topic)
    {
        ArgumentNullException.ThrowIfNull(topic);

        List<T> result = [];
        var segments = topic.Split('/');
        MatchInternal(_root, segments, 0, result);
        return result;
    }

    /// <summary>Removes every pattern and value from the trie.</summary>
    public void Clear()
    {
        _root.ExactValues = null;
        _root.HashValues = null;
        _root.Children = null;
        _root.Plus = null;
        _count = 0;
    }

    private static void MatchInternal(Node node, string[] segments, int index, List<T> result)
    {
        if (node.HashValues is { } hash)
        {
            result.AddRange(hash);
        }

        if (index == segments.Length)
        {
            if (node.ExactValues is { } exact)
            {
                result.AddRange(exact);
            }
            return;
        }

        var segment = segments[index];

        if (node.Children is { } children && children.TryGetValue(segment, out var literalChild))
        {
            MatchInternal(literalChild, segments, index + 1, result);
        }

        if (node.Plus is { } plusChild)
        {
            MatchInternal(plusChild, segments, index + 1, result);
        }
    }

    private static string[] SplitPattern(string pattern) =>
        string.IsNullOrEmpty(pattern) ? ["#"] : pattern.Split('/');

    private sealed class Node
    {
        public Dictionary<string, Node>? Children;
        public Node? Plus;
        public List<T>? ExactValues;
        public List<T>? HashValues;
    }
}
