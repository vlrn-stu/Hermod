using System.Collections.Generic;
using System.Text.Json;
using Hermod.Core.Interfaces;
using Hermod.Rules;
using Hermod.Rules.Payload;
using Xunit;

namespace Hermod.UnitTests;

/// <summary>
/// Attack-surface tests for <see cref="JsonPayloadConverter"/>.
/// Rule-action payloads are authored by users and evaluated against
/// broker-supplied payloads; every knob inside the converter is a
/// potential avenue for template abuse, recursion blow-up, or
/// detachment failure in passthrough publishes. These tests pin the
/// converter's behaviour on those edges.
/// </summary>
public class JsonPayloadConverterAttackTests
{
    private readonly JsonPayloadConverter _sut;

    public JsonPayloadConverterAttackTests()
    {
        IExpressionEvaluator evaluator = new ExpressionEvaluator();
        _sut = new JsonPayloadConverter(evaluator);
    }

    private static ExpressionContext Ctx(Dictionary<string, object>? source = null)
    {
        return new ExpressionContext
        {
            Source = source ?? new Dictionary<string, object>(),
        };
    }

    [Fact]
    public void EvaluatePayloadValue_TemplateString_Expanded()
    {
        var ctx = Ctx(new Dictionary<string, object> { ["temp"] = 42 });

        var result = _sut.EvaluatePayloadValue("{{source.temp}}", ctx);

        Assert.Equal(42L, System.Convert.ToInt64(result));
    }

    [Fact]
    public void EvaluatePayloadValue_PlainStringWithoutBraces_PassesThroughUnchanged()
    {
        // Fast path: no `{{` means no evaluator call. A plain string
        // reaches the MQTT publish verbatim including any characters
        // that would otherwise be special in the template syntax.
        var result = _sut.EvaluatePayloadValue("no mustache here", Ctx());

        Assert.Equal("no mustache here", result);
    }

    [Fact]
    public void EvaluatePayloadValue_TemplateInsideNestedArray_ExpandsEachElement()
    {
        // A JSON array whose elements contain templates: every string
        // element that carries `{{` gets routed through the evaluator.
        // Non-template strings pass through. Pinning recursion on arrays.
        var ctx = Ctx(new Dictionary<string, object> { ["v"] = "expanded" });
        var arr = JsonDocument.Parse("[\"{{source.v}}\", \"plain\", 42]").RootElement;

        var result = _sut.EvaluatePayloadValue(arr, ctx);

        var list = Assert.IsType<List<object?>>(result);
        Assert.Equal(3, list.Count);
        Assert.Equal("expanded", list[0]);
        Assert.Equal("plain", list[1]);
        Assert.Equal(42L, list[2]);
    }

    [Fact]
    public void EvaluatePayloadValue_DeepObject_RecursesWithoutStackOverflow()
    {
        // JsonDocument caps depth at 64 by default, so anything this
        // converter sees is already bounded. Regression guard against
        // a future relaxation of the parse depth that would let a
        // malicious payload reach the recursion limit.
        var json = "{\"a\":{\"b\":{\"c\":{\"d\":{\"e\":{\"f\":\"leaf\"}}}}}}";
        var el = JsonDocument.Parse(json).RootElement;

        var result = _sut.EvaluatePayloadValue(el, Ctx());

        var outer = Assert.IsType<Dictionary<string, object?>>(result);
        Assert.Contains("a", outer.Keys);
    }

    [Fact]
    public void EvaluatePayloadValue_NullJsonElement_MapsToEmptyString()
    {
        // Top-level JSON null becomes "" per the MQTT contract — a
        // null would otherwise crash callers that expect a non-null
        // payload body. Pinned so a future refactor that preserves
        // null through the pipeline doesn't silently break publishers.
        var el = JsonDocument.Parse("null").RootElement;

        Assert.Equal("", _sut.EvaluatePayloadValue(el, Ctx()));
    }

    [Fact]
    public void EvaluatePayloadValue_NumberFormatPreserved_IntVsDouble()
    {
        // Integer literal stays Int64; fractional literal becomes
        // double. Matters because downstream JSON serialisation of
        // the rule output carries the form through, so "42" stays
        // "42" and "42.0" stays "42.0".
        var obj = JsonDocument.Parse("{\"n\":42,\"f\":42.0,\"e\":1e9}").RootElement;

        var result = _sut.EvaluatePayloadValue(obj, Ctx()) as Dictionary<string, object?>;

        Assert.NotNull(result);
        Assert.IsType<long>(result["n"]);
        Assert.IsType<double>(result["f"]);
        Assert.IsType<double>(result["e"]);
    }

    [Fact]
    public void EvaluatePayloadValue_NumberLargerThanInt64_FallsBackToDouble()
    {
        // "99999999999999999999" exceeds Int64. ConvertNumber's
        // fractional path is not taken (no '.'/'e'), so the TryGetInt64
        // first branch is hit and fails; the double fallback catches
        // the overflow.
        var obj = JsonDocument.Parse("{\"big\":99999999999999999999}").RootElement;

        var result = _sut.EvaluatePayloadValue(obj, Ctx()) as Dictionary<string, object?>;

        Assert.NotNull(result);
        Assert.IsType<double>(result["big"]);
    }

    [Fact]
    public void DeepCloneDictionary_EmptySource_ReturnsEmpty()
    {
        var clone = JsonPayloadConverter.DeepCloneDictionary(new Dictionary<string, object>());

        Assert.Empty(clone);
    }

    [Fact]
    public void DeepCloneDictionary_NullSource_Throws()
    {
        Assert.Throws<System.ArgumentNullException>(() =>
            JsonPayloadConverter.DeepCloneDictionary(null!));
    }

    [Fact]
    public void DeepCloneDictionary_DetachesNestedDict()
    {
        // Passthrough publishes rely on this: mutating the original
        // source after the clone must not change the cloned payload.
        var inner = new Dictionary<string, object> { ["x"] = 1 };
        var source = new Dictionary<string, object> { ["nested"] = inner };

        var clone = JsonPayloadConverter.DeepCloneDictionary(source);

        inner["x"] = 999;  // mutate the original, clone must not see it
        var clonedInner = clone["nested"];
        // The clone came through a JSON round-trip so nested becomes
        // a JsonElement/JsonObject, not a Dictionary reference.
        // Either way, the value observed in the clone must reflect
        // the pre-mutation state.
        var serialised = JsonSerializer.Serialize(clonedInner);
        Assert.Contains("\"x\":1", serialised);
        Assert.DoesNotContain("999", serialised);
    }

    [Fact]
    public void DeepCloneDictionary_DetachesNestedList()
    {
        var innerList = new List<object> { 1, 2, 3 };
        var source = new Dictionary<string, object> { ["list"] = innerList };

        var clone = JsonPayloadConverter.DeepCloneDictionary(source);

        innerList.Add(999);
        var serialised = JsonSerializer.Serialize(clone["list"]);
        Assert.DoesNotContain("999", serialised);
    }

    [Fact]
    public void DeepCloneDictionary_UnserialisableValue_FallsBackToShallowCopy()
    {
        // A value that JSON round-trip fails on (e.g. a circular
        // reference via nested dicts). The fallback branch returns a
        // new dictionary with the same references — not a true deep
        // clone, but the test-caller can still see a distinct outer
        // container.
        var cyclic = new Dictionary<string, object>();
        cyclic["self"] = cyclic;

        var clone = JsonPayloadConverter.DeepCloneDictionary(cyclic);

        Assert.NotSame(cyclic, clone);
        // Shallow copy: the fallback does NOT break the cycle, it just
        // reuses the same references. Callers that need safety against
        // cycles must handle that themselves.
        Assert.Contains("self", clone);
    }

    [Fact]
    public void EvaluatePayloadValue_StringContainingOnlyBracePair_ExpandsToEmpty()
    {
        // `{{}}` is a valid-shaped template with an empty expression
        // inside. Evaluator resolves an empty expression; the result
        // is coerced by the converter. Pin so a future tightening
        // that rejects empty-body templates is a deliberate choice.
        var result = _sut.EvaluatePayloadValue("{{}}", Ctx());

        Assert.NotNull(result);
    }

    [Fact]
    public void EvaluatePayloadValue_TemplateReferencingMissingPath_ReturnsEmptyString()
    {
        // `{{source.nope}}` resolves to null. EvaluatePayloadValue
        // coalesces null to "" at the top level. Pinned because rule
        // authors rely on "missing field" silently publishing empty,
        // not crashing the action.
        var result = _sut.EvaluatePayloadValue("{{source.nope}}", Ctx());

        Assert.Equal("", result);
    }

    [Fact]
    public void EvaluatePayloadValue_NonStringNonJson_PassesThroughUnchanged()
    {
        // A CLR int in the payload dict: no template expansion, no
        // JsonElement unwrap, passthrough. Matters because some callers
        // pre-convert payload entries to CLR types before passing them
        // to EvaluatePayloadValue.
        Assert.Equal(42, _sut.EvaluatePayloadValue(42, Ctx()));
        Assert.Equal(true, _sut.EvaluatePayloadValue(true, Ctx()));
    }
}
