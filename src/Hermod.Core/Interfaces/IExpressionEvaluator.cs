namespace Hermod.Core.Interfaces;

/// <summary>
/// Template expression evaluator used by the rules engine: substitutes
/// <c>{{...}}</c> placeholders from an <see cref="ExpressionContext"/> and
/// evaluates arithmetic/boolean expressions.
/// </summary>
public interface IExpressionEvaluator
{
    /// <summary>Evaluates a template and returns the raw value (string, number, bool, or object).</summary>
    object? Evaluate(string template, ExpressionContext context);

    /// <summary>
    /// Evaluates a boolean expression. Empty/null expressions return <c>true</c> (vacuous
    /// match); non-boolean results are coerced via truthiness rules.
    /// </summary>
    bool EvaluateCondition(string expression, ExpressionContext context);

    /// <summary>Evaluates a template and converts the result to <typeparamref name="T"/>; returns default on conversion failure.</summary>
    T? Evaluate<T>(string template, ExpressionContext context);
}

/// <summary>
/// Immutable input bundle for a single expression evaluation. Each property
/// lists the template prefix that exposes it (see XML on each field).
/// </summary>
public record ExpressionContext
{
    /// <summary>Source message payload. Access via <c>{{source.prop}}</c> or bare <c>{{prop}}</c>.</summary>
    public Dictionary<string, object> Source { get; init; } = new();

    /// <summary>Source MQTT topic. Access via <c>{{topic}}</c>.</summary>
    public string Topic { get; init; } = string.Empty;

    /// <summary>Device name extracted from the topic. Access via <c>{{deviceName}}</c>.</summary>
    public string? DeviceName { get; init; }

    /// <summary>Rule-specific state. Access via <c>{{state.key}}</c>.</summary>
    public Dictionary<string, object> State { get; init; } = new();

    /// <summary>Global state shared across rules. Access via <c>{{global.key}}</c>.</summary>
    public Dictionary<string, object> Global { get; init; } = new();

    /// <summary>Payload forwarded from an upstream chained rule. Access via <c>{{chain.key}}</c>.</summary>
    public Dictionary<string, object>? ChainData { get; init; }

    /// <summary>Previous device state, for edge/diff conditions. Access via <c>{{previous.key}}</c>.</summary>
    public Dictionary<string, object>? Previous { get; init; }

    /// <summary>Evaluation timestamp (UTC). Access via <c>{{now()}}</c>.</summary>
    public DateTime Now { get; init; } = DateTime.UtcNow;

    /// <summary>Lookup for another device's state. Access via <c>{{device("name").prop}}</c>.</summary>
    public Func<string, Dictionary<string, object>?>? GetDeviceState { get; init; }

    /// <summary>Caller-supplied extras merged into the scope.</summary>
    public Dictionary<string, object> Variables { get; init; } = new();

    /// <summary>
    /// Opaque trace id from the source <see cref="Models.MqttMessage.TraceUuid"/>,
    /// or null. Propagated into rule action execution so outbound
    /// <c>action_publish</c> timestamps correlate back to the originating
    /// <c>publish_tx</c>. Never exposed to rule expressions — internal
    /// observability plumbing only.
    /// </summary>
    public string? TraceUuid { get; init; }
}
