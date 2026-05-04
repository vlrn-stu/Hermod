namespace Hermod.Core.Models.Rules;

/// <summary>
/// Boolean tree node used by <see cref="Rule"/>: combines leaf
/// <see cref="RuleCondition"/>s and nested <see cref="RuleConditionGroup"/>s
/// under a single <see cref="LogicOperator"/>.
/// </summary>
public class RuleConditionGroup
{
    /// <summary>How child conditions/groups combine.</summary>
    public LogicOperator Logic { get; set; } = LogicOperator.All;

    /// <summary>Leaf predicates at this level.</summary>
    public List<RuleCondition> Conditions { get; set; } = new();

    /// <summary>Nested groups, allowing arbitrary-depth boolean expressions.</summary>
    public List<RuleConditionGroup> Groups { get; set; } = new();
}

/// <summary>Combinator semantics for a <see cref="RuleConditionGroup"/>.</summary>
public enum LogicOperator
{
    /// <summary>All conditions must be true (AND).</summary>
    All,

    /// <summary>Any condition must be true (OR).</summary>
    Any,

    /// <summary>No conditions can be true (NOR).</summary>
    None
}

/// <summary>
/// Single predicate. Two authoring modes, mutually exclusive at evaluation
/// time: if <see cref="Expression"/> is set it's evaluated as a template and
/// <see cref="Property"/>/<see cref="Operator"/>/<see cref="Value"/> are
/// ignored; otherwise the structured fields drive the comparison.
/// </summary>
public class RuleCondition
{
    /// <summary>Template expression (e.g. <c>"{{temperature}} &gt; 25"</c>). Wins over the structured fields when set.</summary>
    public string? Expression { get; set; }

    /// <summary>Payload property to compare (dot-path supported).</summary>
    public string? Property { get; set; }

    /// <summary>Comparison operator applied between <see cref="Property"/> and <see cref="Value"/>.</summary>
    public ComparisonOperator Operator { get; set; } = ComparisonOperator.Equals;

    /// <summary>Value to compare against. May contain template substitutions (<c>{{state.threshold}}</c> etc.).</summary>
    public object? Value { get; set; }

    /// <summary>Value list for <see cref="ComparisonOperator.In"/>, <see cref="ComparisonOperator.NotIn"/>, and <see cref="ComparisonOperator.Between"/>.</summary>
    public List<object>? Values { get; set; }
}

/// <summary>Comparison semantics used by a <see cref="RuleCondition"/>.</summary>
public enum ComparisonOperator
{
    /// <summary>Equality.</summary>
    Equals,

    /// <summary>Inequality.</summary>
    NotEquals,

    /// <summary>Numeric greater-than.</summary>
    GreaterThan,

    /// <summary>Numeric less-than.</summary>
    LessThan,

    /// <summary>Numeric greater-than-or-equal.</summary>
    GreaterThanOrEquals,

    /// <summary>Numeric less-than-or-equal.</summary>
    LessThanOrEquals,

    /// <summary>Numeric inclusive-range check; reads from <see cref="RuleCondition.Values"/> (two entries).</summary>
    Between,

    /// <summary>Substring containment.</summary>
    Contains,

    /// <summary>Negation of <see cref="Contains"/>.</summary>
    NotContains,

    /// <summary>String starts-with.</summary>
    StartsWith,

    /// <summary>String ends-with.</summary>
    EndsWith,

    /// <summary>Regular-expression match.</summary>
    Matches,

    /// <summary>Property exists in the payload.</summary>
    Exists,

    /// <summary>Property does not exist in the payload.</summary>
    NotExists,

    /// <summary>Property exists and is null.</summary>
    IsNull,

    /// <summary>Property exists and is not null.</summary>
    IsNotNull,

    /// <summary>Property is boolean true.</summary>
    IsTrue,

    /// <summary>Property is boolean false.</summary>
    IsFalse,

    /// <summary>Property value differs from the previous device-state snapshot.</summary>
    Changed,

    /// <summary>Property value matches the previous device-state snapshot.</summary>
    Unchanged,

    /// <summary>Value is contained in <see cref="RuleCondition.Values"/>.</summary>
    In,

    /// <summary>Value is not contained in <see cref="RuleCondition.Values"/>.</summary>
    NotIn
}
