using System.Collections.Generic;
using Hermod.Core.Interfaces;
using Hermod.Rules;
using Xunit;

namespace Hermod.UnitTests;

/// <summary>
/// Attack-surface tests for <see cref="ExpressionEvaluator"/>.
/// Rules are user-supplied templates evaluated against user-supplied
/// MQTT payloads; both sides are untrusted. These tests pin the
/// evaluator's refusal to re-evaluate template-shaped data, its
/// graceful handling of malformed paths, and its resilience against
/// degenerate inputs that past revisions might have let through.
/// Failures here are security regressions.
/// </summary>
public class ExpressionEvaluatorAttackTests
{
    private readonly ExpressionEvaluator _sut = new();

    private static ExpressionContext Ctx(Dictionary<string, object>? source = null)
    {
        return new ExpressionContext
        {
            Source = source ?? new Dictionary<string, object>(),
            Now = new System.DateTime(2026, 1, 15, 10, 30, 0, System.DateTimeKind.Utc),
        };
    }

    [Fact]
    public void Evaluate_TemplateInsideSourceValue_DoesNotRecurse()
    {
        // Payload poisoning: an attacker sets a source field to a
        // string that looks like another template. The evaluator must
        // return the raw string, not re-evaluate it against the
        // context. Otherwise a malicious payload could read global
        // state it should have no access to.
        var ctx = Ctx(source: new Dictionary<string, object>
        {
            ["name"] = "{{global.secret}}",
        });

        var result = _sut.Evaluate("{{source.name}}", ctx);

        Assert.Equal("{{global.secret}}", result);
    }

    [Fact]
    public void Evaluate_NestedTemplateInMixedString_OnlyOuterReplaced()
    {
        // One-pass guarantee in mixed templates too: the replaced
        // portion is NOT scanned for further expansions.
        var ctx = Ctx(source: new Dictionary<string, object>
        {
            ["inner"] = "{{source.other}}",
            ["other"] = "should-not-appear",
        });

        var result = _sut.Evaluate("prefix-{{source.inner}}-suffix", ctx);

        Assert.Equal("prefix-{{source.other}}-suffix", result);
    }

    [Fact]
    public void Evaluate_DeeplyNestedMissingPath_ReturnsNullNoThrow()
    {
        var ctx = Ctx(source: new Dictionary<string, object> { ["a"] = 42 });

        // 50 levels deep on a scalar should return null, not throw.
        var path = "{{source.a." + string.Join(".", new string[50]).Replace(" ", "x") + "}}";
        var result = _sut.Evaluate(path, ctx);

        Assert.Null(result);
    }

    [Fact]
    public void Evaluate_TraversalThroughNonDict_ReturnsNull()
    {
        // source.x is an int; source.x.y is not a valid path. The
        // evaluator must treat the traversal as a miss, not panic.
        var ctx = Ctx(source: new Dictionary<string, object> { ["x"] = 123 });

        Assert.Null(_sut.Evaluate("{{source.x.y}}", ctx));
    }

    [Fact]
    public void Evaluate_UnknownRoot_FallsBackToSourceOrReturnsNull()
    {
        // The evaluator's default root-dispatch falls back to source
        // lookup. A name that exists in source is returned; an unknown
        // name must resolve to null, never leak an internal field name.
        var ctx = Ctx(source: new Dictionary<string, object> { ["known"] = "ok" });

        Assert.Equal("ok", _sut.Evaluate("{{known}}", ctx));
        Assert.Null(_sut.Evaluate("{{unknown_key_name}}", ctx));
    }

    [Fact]
    public void Evaluate_SpecialCharactersInKey_DoNotBreakParse()
    {
        // Keys with brackets, dots, spaces flow through split-on-dot
        // path lookup. Bracket-key lookup resolves because the
        // segment matches the dict key verbatim; dot-in-key names
        // become unreachable because the parser splits on dot; space
        // segments resolve verbatim. None of these cases may throw;
        // that is the attack-surface invariant worth pinning.
        var ctx = Ctx(source: new Dictionary<string, object>
        {
            ["weird key"] = "v1",
            ["a.b"] = "v2",
            ["[attack]"] = "v3",
        });

        Assert.Equal("v3", _sut.Evaluate("{{source.[attack]}}", ctx));
        Assert.Equal("v1", _sut.Evaluate("{{source.weird key}}", ctx));
        // "a.b" is unreachable via dot-path: the parser splits on dot
        // and the lookup walks the nested dict. There is no nested
        // dict at "a" so this must be null, not the literal-key value.
        Assert.Null(_sut.Evaluate("{{source.a.b}}", ctx));
    }

    [Fact]
    public void Evaluate_LongPathDoesNotStackOverflow()
    {
        // ~1000 segments. GetNestedValue iterates, not recurses, so
        // this is O(path-length) not O(stack). Regression guard
        // against anyone "optimizing" it into recursion later.
        var ctx = Ctx(source: new Dictionary<string, object> { ["root"] = 1 });
        var path = "{{source.root" + string.Concat(System.Linq.Enumerable.Repeat(".x", 1_000)) + "}}";

        var result = _sut.Evaluate(path, ctx);

        Assert.Null(result);
    }

    [Fact]
    public void EvaluateCondition_EmptyExpression_IsTrue()
    {
        // Empty condition = "always" by convention; tests the documented
        // vacuous-match behaviour. An attacker who supplies an empty
        // condition expecting to disable a rule is confirming the rule
        // fires, not disabling it.
        Assert.True(_sut.EvaluateCondition("", Ctx()));
    }

    [Fact]
    public void EvaluateCondition_NonBooleanSource_CoercesViaTruthiness()
    {
        // Strings like "false", "0", "" coerce to false; anything
        // else coerces to true. An attacker supplying the literal
        // string "true" via source value must only enable the rule
        // if explicitly evaluated, never by accident via path-chaining.
        var falsy = Ctx(source: new Dictionary<string, object> { ["v"] = "false" });
        var truthy = Ctx(source: new Dictionary<string, object> { ["v"] = "yes" });

        Assert.False(_sut.EvaluateCondition("{{source.v}}", falsy));
        Assert.True(_sut.EvaluateCondition("{{source.v}}", truthy));
    }

    [Fact]
    public void Evaluate_DivisionByZero_DegradesWithoutThrowing()
    {
        // Attacker-controlled expression triggers division by zero.
        // The invariant is that evaluation returns gracefully (null,
        // zero, or infinity — implementation is free to pick) rather
        // than propagating an exception that would kill the calling
        // rule dispatch loop.
        var result = _sut.Evaluate("{{1 / 0}}", Ctx());

        // null, a double Infinity/NaN, or zero — any bounded outcome
        // is acceptable so long as the call returned at all.
        if (result is double d)
        {
            Assert.True(double.IsInfinity(d) || double.IsNaN(d) || d == 0.0);
        }
    }

    [Fact]
    public void Evaluate_ArithmeticOnStrings_ReturnsValueOrDefault()
    {
        // Source value is a literal string that looks like an
        // expression. Evaluator must NOT "eval" it; either coerce
        // numerically (if parseable) or return null / 0, never
        // something like "1 + DROP TABLE".
        var ctx = Ctx(source: new Dictionary<string, object>
        {
            ["raw"] = "1 + DROP TABLE x",
        });

        var result = _sut.Evaluate("{{source.raw}}", ctx);

        Assert.Equal("1 + DROP TABLE x", result);
    }

    [Fact]
    public void Evaluate_QuotedStringContainingTemplate_TemplateExpandsFirst()
    {
        // Documented behaviour: TemplatePattern.Replace runs when the
        // outer string is a mixed template, so an embedded `{{x}}`
        // gets expanded even inside what looks like a quoted literal.
        // The outer double-quote characters are returned verbatim
        // because the Replace branch does not also run ResolveValue's
        // quote-stripping. With no matching source value, the
        // substitution is the empty string. Pinning this so a future
        // refactor that "sandboxes" quoted literals does not silently
        // change rule-author semantics.
        var result = _sut.Evaluate("\"literal {{x}} token\"", Ctx());

        Assert.Equal("\"literal  token\"", result);
    }

    [Fact]
    public void Evaluate_GenericT_CoercionFailure_ReturnsDefault()
    {
        // Misuse: template resolves to a non-numeric string; caller
        // asks for int. The evaluator returns default(int) = 0, not
        // throws, so one bad rule doesn't poison a batch of actions.
        var ctx = Ctx(source: new Dictionary<string, object> { ["v"] = "not-a-number" });

        var result = _sut.Evaluate<int>("{{source.v}}", ctx);

        Assert.Equal(0, result);
    }

    [Fact]
    public void Evaluate_PathWithEmptySegments_ReturnsNull()
    {
        // Malformed path "source..." attempts to traverse through
        // empty keys. Should return null, not explode.
        var ctx = Ctx(source: new Dictionary<string, object> { ["x"] = 1 });

        Assert.Null(_sut.Evaluate("{{source..x}}", ctx));
    }

    [Fact]
    public void Evaluate_SourceDictionaryWithNullValue_PropagatesAsNull()
    {
        // A payload can carry a JSON null; the evaluator must pass it
        // through as null so downstream consumers differentiate
        // "missing" from "present but null".
        var ctx = Ctx(source: new Dictionary<string, object>
        {
            ["explicit_null"] = null!,
        });

        var result = _sut.Evaluate("{{source.explicit_null}}", ctx);

        Assert.Null(result);
    }

    [Fact]
    public void Evaluate_CaseInsensitiveRoot_DoesNotLeakAcrossScopes()
    {
        // root dispatch uppercases before switching; tests that this
        // cannot be abused to access global via "SOURCE" etc.
        var ctx = new ExpressionContext
        {
            Source = new Dictionary<string, object> { ["marker"] = "source" },
            Global = new Dictionary<string, object> { ["marker"] = "global" },
        };

        Assert.Equal("source", _sut.Evaluate("{{source.marker}}", ctx));
        Assert.Equal("global", _sut.Evaluate("{{global.marker}}", ctx));
        Assert.Equal("source", _sut.Evaluate("{{Source.marker}}", ctx));
        Assert.Equal("global", _sut.Evaluate("{{GLOBAL.marker}}", ctx));
    }

    [Fact]
    public void Evaluate_ContainsWithQuotedPattern_Matches()
    {
        // Quoted arg must resolve to its literal string (via ResolveValue's
        // quote-stripping) and be passed to Contains. Before the fix, args[1]
        // was passed raw including the quotes, so `"world"` never matched.
        var ctx = Ctx(source: new Dictionary<string, object> { ["msg"] = "Hello World" });
        Assert.Equal(true, _sut.Evaluate("{{contains(source.msg, \"world\")}}", ctx));
    }

    [Fact]
    public void Evaluate_ContainsWithVariableArg_ResolvesVariable()
    {
        // `contains(x, source.needle)` must look up `source.needle` at match
        // time instead of searching for the literal string "source.needle".
        var ctx = Ctx(source: new Dictionary<string, object>
        {
            ["msg"] = "Hello World",
            ["needle"] = "World",
        });
        Assert.Equal(true, _sut.Evaluate("{{contains(source.msg, source.needle)}}", ctx));
    }

    [Fact]
    public void Evaluate_ReplaceWithQuotedArgs_Substitutes()
    {
        // Before the fix, REPLACE searched for literal `"foo"` (with quotes)
        // and would never match an unquoted source.
        Assert.Equal("bazbar", _sut.Evaluate("{{replace(\"foobar\", \"foo\", \"baz\")}}", Ctx()));
    }

    [Fact]
    public void Evaluate_SubstringNegativeStart_DegradesWithoutThrowing()
    {
        // Range with negative index would throw ArgumentOutOfRangeException.
        var ctx = Ctx(source: new Dictionary<string, object> { ["v"] = "abc" });
        var exception = Record.Exception(() => _sut.Evaluate("{{substring(source.v, -5)}}", ctx));
        Assert.Null(exception);
    }

    [Fact]
    public void Evaluate_RoundOutOfRangeDecimals_DegradesWithoutThrowing()
    {
        // Math.Round throws when decimals is outside 0..15; clamping lets the
        // user-authored expression degrade to sensible rounding instead.
        var ctx = Ctx(source: new Dictionary<string, object> { ["v"] = 3.14159 });
        var exception = Record.Exception(() => _sut.Evaluate("{{round(source.v, -1)}}", ctx));
        Assert.Null(exception);
    }

    [Theory]
    [InlineData("{{\"}}")]
    [InlineData("{{'}}")]
    public void Evaluate_SingleQuoteLiteral_DegradesWithoutThrowing(string template)
    {
        // Regression guard: a single `"` or `'` used to satisfy both the
        // StartsWith and EndsWith checks in ResolveValue, and then
        // path[1..^1] threw ArgumentOutOfRangeException (range [1..0] on a
        // length-1 string). Malformed user templates must degrade, not
        // crash the evaluator.
        var exception = Record.Exception(() => _sut.Evaluate(template, Ctx()));
        Assert.Null(exception);
    }
}
