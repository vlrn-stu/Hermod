using Hermod.Core.Interfaces;
using Hermod.Rules;
using Xunit;

namespace Hermod.UnitTests;

public class ExpressionEvaluatorTests
{
    private readonly ExpressionEvaluator _sut = new();

    private static ExpressionContext Ctx(
        Dictionary<string, object>? source = null,
        Dictionary<string, object>? state = null,
        Dictionary<string, object>? global = null)
    {
        return new ExpressionContext
        {
            Source = source ?? new Dictionary<string, object>(),
            State = state ?? new Dictionary<string, object>(),
            Global = global ?? new Dictionary<string, object>(),
            Now = new DateTime(2026, 1, 15, 10, 30, 0, DateTimeKind.Utc)
        };
    }

    [Fact]
    public void Evaluate_LiteralString_ReturnsUnchanged()
    {
        Assert.Equal("hello", _sut.Evaluate("hello", Ctx()));
    }

    [Fact]
    public void Evaluate_EmptyTemplate_ReturnsEmpty()
    {
        Assert.Equal("", _sut.Evaluate("", Ctx()));
    }

    [Fact]
    public void Evaluate_SingleSourceProperty_ReturnsValue()
    {
        var ctx = Ctx(source: new Dictionary<string, object> { ["temperature"] = 22.5 });
        Assert.Equal(22.5, _sut.Evaluate("{{source.temperature}}", ctx));
    }

    [Fact]
    public void Evaluate_InlineTemplate_SubstitutesValue()
    {
        var ctx = Ctx(source: new Dictionary<string, object> { ["name"] = "kitchen" });
        Assert.Equal("room=kitchen", _sut.Evaluate("room={{source.name}}", ctx));
    }

    [Fact]
    public void Evaluate_BareKey_FallsBackToSource()
    {
        var ctx = Ctx(source: new Dictionary<string, object> { ["humidity"] = 60 });
        Assert.Equal(60, _sut.Evaluate("{{humidity}}", ctx));
    }

    [Theory]
    [InlineData("{{source.x > 10}}", true)]
    [InlineData("{{source.x < 10}}", false)]
    [InlineData("{{source.x >= 25}}", true)]
    [InlineData("{{source.x == 25}}", true)]
    [InlineData("{{source.x != 25}}", false)]
    public void EvaluateCondition_NumericComparisons(string expr, bool expected)
    {
        var ctx = Ctx(source: new Dictionary<string, object> { ["x"] = 25 });
        Assert.Equal(expected, _sut.EvaluateCondition(expr, ctx));
    }

    [Fact]
    public void EvaluateCondition_LogicalAnd_ShortCircuits()
    {
        var ctx = Ctx(source: new Dictionary<string, object> { ["a"] = 1, ["b"] = 2 });
        Assert.True(_sut.EvaluateCondition("{{source.a == 1 && source.b == 2}}", ctx));
        Assert.False(_sut.EvaluateCondition("{{source.a == 0 && source.b == 2}}", ctx));
    }

    [Fact]
    public void EvaluateCondition_LogicalOr_ShortCircuits()
    {
        var ctx = Ctx(source: new Dictionary<string, object> { ["a"] = 1 });
        Assert.True(_sut.EvaluateCondition("{{source.a == 0 || source.a == 1}}", ctx));
        Assert.False(_sut.EvaluateCondition("{{source.a == 0 || source.a == 2}}", ctx));
    }

    [Fact]
    public void Evaluate_Arithmetic_RespectsPrecedence()
    {
        var ctx = Ctx(source: new Dictionary<string, object> { ["x"] = 2, ["y"] = 3, ["z"] = 4 });
        // 2 + 3 * 4 = 14, not 20
        Assert.Equal(14d, _sut.Evaluate("{{source.x + source.y * source.z}}", ctx));
    }

    [Fact]
    public void Evaluate_UnaryMinusLiteral_AfterMultiply_ParsesAsNegative()
    {
        // The classic failing case: `5 * -3`. A previous right-to-left
        // scan picked the `-` at position 4 as the split point,
        // producing left=`"5 *"` which fails to evaluate. The current
        // IsUnarySign check identifies the `-` as unary (preceded by
        // `*`) and the scanner finds `*` instead. Result: 5 * -3 = -15.
        var ctx = Ctx();
        Assert.Equal(-15d, _sut.Evaluate("{{5 * -3}}", ctx));
    }

    [Fact]
    public void Evaluate_UnaryMinusLiteral_AtStartOfExpression_ParsesAsNegative()
    {
        // `-3 + 2` where `-3` is at position 0. Scanner finds the
        // binary `+` at position 3 and splits: left=`"-3"`, right=`"2"`.
        // Left must evaluate to -3 (bare numeric literal).
        var ctx = Ctx();
        Assert.Equal(-1d, _sut.Evaluate("{{-3 + 2}}", ctx));
    }

    [Fact]
    public void Evaluate_UnaryMinusLiteral_AfterDivide_ParsesAsNegative()
    {
        var ctx = Ctx();
        // 10 / -2 = -5
        Assert.Equal(-5d, _sut.Evaluate("{{10 / -2}}", ctx));
    }

    [Fact]
    public void Evaluate_UnaryPlusLiteral_AfterMultiply_NoOp()
    {
        // Unary `+` is rarer but the same rule applies. `5 * +3 = 15`.
        var ctx = Ctx();
        Assert.Equal(15d, _sut.Evaluate("{{5 * +3}}", ctx));
    }

    [Fact]
    public void Evaluate_NegativeFraction_InExpression_ParsesCorrectly()
    {
        // Decimal edge case: `10 * -2.5 = -25`.
        var ctx = Ctx();
        Assert.Equal(-25d, _sut.Evaluate("{{10 * -2.5}}", ctx));
    }

    [Fact]
    public void Evaluate_SubtractBetweenTwoIdentifiers_StillWorks()
    {
        // Regression guard: the unary-sign fix must NOT misidentify
        // a genuine binary minus as unary. `source.x - source.y`
        // still splits at the `-` because it is preceded by a digit
        // or identifier character (via the whitespace-after-`x`
        // case: prev non-whitespace is `x`, not in the unary set).
        var ctx = Ctx(source: new Dictionary<string, object> { ["x"] = 10, ["y"] = 3 });
        Assert.Equal(7d, _sut.Evaluate("{{source.x - source.y}}", ctx));
    }

    [Fact]
    public void Evaluate_AddWithMissingLeftOperand_ReturnsNull()
    {
        // A previous implementation fell back to string concatenation
        // when numeric coercion failed. `source.temp + source.unit`
        // with `source.temp` missing would return "C" (the unit
        // string). That masked bad rule inputs. The fix returns null
        // so rule authors see the failure instead of getting a silent
        // garbage value.
        var ctx = Ctx(source: new Dictionary<string, object> { ["unit"] = "C" });
        Assert.Null(_sut.Evaluate("{{source.temp + source.unit}}", ctx));
    }

    [Fact]
    public void Evaluate_AddTwoStringsLookingLikeValues_ReturnsNull()
    {
        // Two non-numeric string operands must NOT concatenate.
        var ctx = Ctx(source: new Dictionary<string, object>
        {
            ["a"] = "hello",
            ["b"] = "world"
        });
        Assert.Null(_sut.Evaluate("{{source.a + source.b}}", ctx));
    }

    [Fact]
    public void Evaluate_AddNumericStringAndNumber_Works()
    {
        // A string that parses as a number should still participate
        // in arithmetic. `"23" + 7 = 30` because ToDouble can parse
        // the numeric-looking string. Pins that the removal of the
        // concat fallback did not also break legitimate numeric
        // string operands.
        var ctx = Ctx(source: new Dictionary<string, object>
        {
            ["s"] = "23",
            ["n"] = 7
        });
        Assert.Equal(30d, _sut.Evaluate("{{source.s + source.n}}", ctx));
    }

    [Fact]
    public void Evaluate_FunctionRound_NoDecimals()
    {
        var ctx = Ctx(source: new Dictionary<string, object> { ["v"] = 3.7 });
        Assert.Equal(4d, _sut.Evaluate("{{round(source.v)}}", ctx));
    }

    [Fact]
    public void Evaluate_FunctionRound_WithDecimals()
    {
        var ctx = Ctx(source: new Dictionary<string, object> { ["v"] = 3.14159 });
        Assert.Equal(3.14d, _sut.Evaluate("{{round(source.v, 2)}}", ctx));
    }

    [Fact]
    public void Evaluate_FunctionMin_ReturnsSmaller()
    {
        var ctx = Ctx();
        Assert.Equal(3d, _sut.Evaluate("{{min(5, 3)}}", ctx));
    }

    [Fact]
    public void Evaluate_FunctionMax_ReturnsLarger()
    {
        var ctx = Ctx();
        Assert.Equal(5d, _sut.Evaluate("{{max(5, 3)}}", ctx));
    }

    [Fact]
    public void Evaluate_FunctionNow_ReturnsContextTime()
    {
        var ctx = Ctx();
        var result = _sut.Evaluate("{{now()}}", ctx);
        Assert.Equal(new DateTime(2026, 1, 15, 10, 30, 0, DateTimeKind.Utc), result);
    }

    [Fact]
    public void Evaluate_FunctionHour_ReturnsContextHour()
    {
        var ctx = Ctx();
        Assert.Equal(10, _sut.Evaluate("{{hour()}}", ctx));
    }

    [Fact]
    public void EvaluateCondition_NotOperator_Negates()
    {
        var ctx = Ctx(source: new Dictionary<string, object> { ["x"] = 1 });
        Assert.True(_sut.EvaluateCondition("{{!(source.x == 2)}}", ctx));
        Assert.False(_sut.EvaluateCondition("{{!(source.x == 1)}}", ctx));
    }

    [Fact]
    public void EvaluateGeneric_ConvertsToInt()
    {
        var ctx = Ctx(source: new Dictionary<string, object> { ["v"] = "42" });
        Assert.Equal(42, _sut.Evaluate<int>("{{source.v}}", ctx));
    }

    [Fact]
    public void EvaluateGeneric_ConvertsToString()
    {
        var ctx = Ctx(source: new Dictionary<string, object> { ["v"] = 42 });
        Assert.Equal("42", _sut.Evaluate<string>("{{source.v}}", ctx));
    }

    [Fact]
    public void Evaluate_StateProperty_ResolvesFromState()
    {
        var ctx = Ctx(state: new Dictionary<string, object> { ["count"] = 7 });
        Assert.Equal(7, _sut.Evaluate("{{state.count}}", ctx));
    }

    [Fact]
    public void Evaluate_GlobalProperty_ResolvesFromGlobal()
    {
        var ctx = Ctx(global: new Dictionary<string, object> { ["mode"] = "auto" });
        Assert.Equal("auto", _sut.Evaluate("{{global.mode}}", ctx));
    }

    [Fact]
    public void Evaluate_MissingProperty_ReturnsNull()
    {
        var ctx = Ctx();
        Assert.Null(_sut.Evaluate("{{source.missing}}", ctx));
    }

    [Fact]
    public void Evaluate_CoalesceFunction_ReturnsFirstNonNull()
    {
        var ctx = Ctx(source: new Dictionary<string, object> { ["b"] = "fallback" });
        Assert.Equal("fallback", _sut.Evaluate("{{coalesce(source.missing, source.b)}}", ctx));
    }

    [Fact]
    public void Evaluate_CoalesceAllNull_ReturnsNullInsteadOfRouteToResolveValue()
    {
        // Regression guard for TryEvaluateFunction: previously, a function
        // that legitimately returned null caused EvaluateExpression to fall
        // through to ResolveValue and parse the whole call as a dotted
        // identifier, silently breaking coalesce / default / device.
        var ctx = Ctx();
        Assert.Null(_sut.Evaluate("{{coalesce(source.a, source.b)}}", ctx));
    }

    [Fact]
    public void Evaluate_UpperOfNull_ReturnsNullInsteadOfRouteToResolveValue()
    {
        // Same category as the coalesce regression above. `upper(null)`
        // returns null; the function matcher must not fall through to
        // treating "upper(source.missing)" as a property name.
        var ctx = Ctx();
        Assert.Null(_sut.Evaluate("{{upper(source.missing)}}", ctx));
    }

    [Fact]
    public void EvaluateCondition_MixedAndOrPrecedence_OrBindsLooser()
    {
        // `&&` binds tighter than `||`, so `a == 1 && b == 0 || c == 1`
        // parses as `(a == 1 && b == 0) || c == 1`. With c=1 and a=1,b=0
        // the left side is true so the whole expression is true; with
        // a=0,b=0,c=1 the right side alone still makes it true.
        var ctxAllThree = Ctx(source: new Dictionary<string, object>
        {
            ["a"] = 1,
            ["b"] = 0,
            ["c"] = 1
        });
        Assert.True(_sut.EvaluateCondition(
            "{{source.a == 1 && source.b == 0 || source.c == 1}}", ctxAllThree));

        var ctxOnlyC = Ctx(source: new Dictionary<string, object>
        {
            ["a"] = 9,
            ["b"] = 9,
            ["c"] = 1
        });
        Assert.True(_sut.EvaluateCondition(
            "{{source.a == 1 && source.b == 0 || source.c == 1}}", ctxOnlyC));

        var ctxNone = Ctx(source: new Dictionary<string, object>
        {
            ["a"] = 9,
            ["b"] = 9,
            ["c"] = 9
        });
        Assert.False(_sut.EvaluateCondition(
            "{{source.a == 1 && source.b == 0 || source.c == 1}}", ctxNone));
    }

    [Fact]
    public void Evaluate_ArithmeticMinusDoesNotFallBackToStringConcat()
    {
        // A prior regression fell back to `$"{leftVal}{rightVal}"` on any
        // arithmetic with a non-numeric operand. That masked bad inputs.
        var ctx = Ctx(source: new Dictionary<string, object> { ["unit"] = "C" });
        // source.missing is null, source.unit is "C"; before the fix, this
        // silently returned "C" instead of rejecting the operation.
        var result = _sut.Evaluate("{{source.missing + source.unit}}", ctx);
        Assert.NotEqual("C", result);
    }

    [Fact]
    public void Evaluate_FunctionUpper_TransformsString()
    {
        var ctx = Ctx(source: new Dictionary<string, object> { ["name"] = "kitchen" });
        Assert.Equal("KITCHEN", _sut.Evaluate("{{upper(source.name)}}", ctx));
    }

    [Fact]
    public void Evaluate_FunctionLower_TransformsString()
    {
        var ctx = Ctx(source: new Dictionary<string, object> { ["name"] = "KITCHEN" });
        Assert.Equal("kitchen", _sut.Evaluate("{{lower(source.name)}}", ctx));
    }

    [Fact]
    public void Evaluate_FunctionLength_CountsChars()
    {
        var ctx = Ctx(source: new Dictionary<string, object> { ["name"] = "kitchen" });
        Assert.Equal(7, _sut.Evaluate("{{length(source.name)}}", ctx));
    }

    [Fact]
    public void Evaluate_FunctionSubstring_RespectsBounds()
    {
        var ctx = Ctx(source: new Dictionary<string, object> { ["v"] = "hermod" });
        Assert.Equal("her", _sut.Evaluate("{{substring(source.v, 0, 3)}}", ctx));
        Assert.Equal("mod", _sut.Evaluate("{{substring(source.v, 3)}}", ctx));
    }

    [Fact]
    public void Evaluate_FunctionContains_CaseInsensitive()
    {
        var ctx = Ctx();
        Assert.Equal(true, _sut.Evaluate("{{contains(\"Hello World\", world)}}", ctx));
    }

    [Fact]
    public void Evaluate_FunctionFloorCeilAbs()
    {
        var ctx = Ctx(source: new Dictionary<string, object> { ["v"] = 3.7, ["n"] = -5.2 });
        Assert.Equal(3d, _sut.Evaluate("{{floor(source.v)}}", ctx));
        Assert.Equal(4d, _sut.Evaluate("{{ceil(source.v)}}", ctx));
        Assert.Equal(5.2, _sut.Evaluate("{{abs(source.n)}}", ctx));
    }

    [Fact]
    public void Evaluate_FunctionIf_ReturnsMatchingBranch()
    {
        var ctx = Ctx(source: new Dictionary<string, object> { ["x"] = 10 });
        Assert.Equal("big", _sut.Evaluate("{{if(source.x > 5, \"big\", \"small\")}}", ctx));
    }

    [Fact]
    public void Evaluate_FunctionYearMonthDay_FromContextNow()
    {
        var ctx = Ctx();
        Assert.Equal(2026, _sut.Evaluate("{{year()}}", ctx));
        Assert.Equal(1, _sut.Evaluate("{{month()}}", ctx));
        Assert.Equal(15, _sut.Evaluate("{{day()}}", ctx));
    }

    [Fact]
    public void Evaluate_PreviousNamespace_ResolvesFromContext()
    {
        var ctx = Ctx() with
        {
            Previous = new Dictionary<string, object> { ["temp"] = 20.5 }
        };
        Assert.Equal(20.5, _sut.Evaluate("{{previous.temp}}", ctx));
    }

    [Fact]
    public void Evaluate_MissingProperty_ReturnsNullNotTemplateString()
    {
        // Lock the null path so any regression that returns the raw
        // "{{source.missing}}" template string would fail here.
        var ctx = Ctx();
        Assert.Null(_sut.Evaluate("{{source.missing}}", ctx));
        Assert.False(_sut.EvaluateCondition("{{source.missing}}", ctx));
    }

    // The READ path (TryGetDouble, TryParse) was already InvariantCulture.
    // The hidden failure mode was the WRITE path: template substitution
    // at line 37 used `value?.ToString()` which for a double 23.5 under
    // a German or Russian locale produces "23,5", and any downstream
    // parser using InvariantCulture then rejects it. FormatValueInvariant
    // guarantees `.` decimals on the output side too.

    // Each test temporarily swaps CurrentCulture to one with a `,`
    // decimal separator, runs the substitution, and asserts the output
    // contains a `.` decimal (or the correct thousand-free integer
    // form). Tests use `"val={{...}}"` templates (not bare `"{{...}}"`)
    // because the single-expression fast path in Evaluate short-circuits
    // and returns the raw `object?`, bypassing FormatValueInvariant.
    // Embedding the expression inside literal text forces the
    // TemplatePattern.Replace substitution branch which is what matters
    // for payload rendering.

    [Fact]
    public void Evaluate_DoubleSubstitution_UsesInvariantDotDecimal()
    {
        var previous = System.Threading.Thread.CurrentThread.CurrentCulture;
        try
        {
            System.Threading.Thread.CurrentThread.CurrentCulture =
                System.Globalization.CultureInfo.GetCultureInfo("de-DE");

            var ctx = Ctx(source: new() { ["temperature"] = 23.5 });
            var result = _sut.Evaluate("val={{source.temperature}}", ctx);
            Assert.Equal("val=23.5", result);
        }
        finally
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Evaluate_FloatSubstitution_UsesInvariantDotDecimal()
    {
        var previous = System.Threading.Thread.CurrentThread.CurrentCulture;
        try
        {
            System.Threading.Thread.CurrentThread.CurrentCulture =
                System.Globalization.CultureInfo.GetCultureInfo("fr-FR");
            var ctx = Ctx(source: new() { ["ratio"] = 0.75f });
            var result = _sut.Evaluate("r={{source.ratio}}", ctx);
            Assert.Equal("r=0.75", result);
        }
        finally
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Evaluate_DecimalSubstitution_UsesInvariantDotDecimal()
    {
        var previous = System.Threading.Thread.CurrentThread.CurrentCulture;
        try
        {
            System.Threading.Thread.CurrentThread.CurrentCulture =
                System.Globalization.CultureInfo.GetCultureInfo("ru-RU");
            var ctx = Ctx(source: new() { ["price"] = 99.95m });
            var result = _sut.Evaluate("p={{source.price}}", ctx);
            Assert.Equal("p=99.95", result);
        }
        finally
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Evaluate_IntegerSubstitution_UsesInvariantFormatting()
    {
        // Integers are IFormattable so the fallback path hits them too.
        // Large integers in `de-DE` format as "1.000.000" (thousand
        // separator is `.`). Must come out as "1000000" without
        // separators under invariant formatting.
        var previous = System.Threading.Thread.CurrentThread.CurrentCulture;
        try
        {
            System.Threading.Thread.CurrentThread.CurrentCulture =
                System.Globalization.CultureInfo.GetCultureInfo("de-DE");
            var ctx = Ctx(source: new() { ["count"] = 1_000_000 });
            var result = _sut.Evaluate("c={{source.count}}", ctx);
            Assert.Equal("c=1000000", result);
        }
        finally
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    [Fact]
    public void EvaluateGeneric_StringWithDouble_UsesInvariantDotDecimal()
    {
        // The Evaluate<string>(single-expression-template) path also
        // goes through FormatValueInvariant. Pin it explicitly so a
        // future refactor that removes the typed path's invariant call
        // flips this test.
        var previous = System.Threading.Thread.CurrentThread.CurrentCulture;
        try
        {
            System.Threading.Thread.CurrentThread.CurrentCulture =
                System.Globalization.CultureInfo.GetCultureInfo("de-DE");
            var ctx = Ctx(source: new() { ["temperature"] = 23.5 });
            var result = _sut.Evaluate<string>("{{source.temperature}}", ctx);
            Assert.Equal("23.5", result);
        }
        finally
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = previous;
        }
    }
}
