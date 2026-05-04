using Hermod.Core.Interfaces;
using Hermod.Core.Models.Rules;
using Hermod.Rules;
using Xunit;

namespace Hermod.UnitTests;

public class ConditionEvaluatorTests
{
    private readonly ConditionEvaluator _sut =
        new(new ExpressionEvaluator());

    private static ExpressionContext Ctx(
        Dictionary<string, object>? source = null,
        Dictionary<string, object>? previous = null)
    {
        return new ExpressionContext
        {
            Source = source ?? new Dictionary<string, object>(),
            Previous = previous
        };
    }

    [Fact]
    public void Evaluate_NullGroup_ReturnsTrue()
    {
        Assert.True(_sut.Evaluate(null, Ctx()));
    }

    [Fact]
    public void Evaluate_EmptyGroup_ReturnsTrue()
    {
        var group = new RuleConditionGroup { Logic = LogicOperator.All };
        Assert.True(_sut.Evaluate(group, Ctx()));
    }

    [Fact]
    public void EvaluateSingle_EqualsNumeric_HandlesIntToLong()
    {
        var ctx = Ctx(source: new Dictionary<string, object> { ["temp"] = 25 });
        var condition = new RuleCondition
        {
            Property = "temp",
            Operator = ComparisonOperator.Equals,
            Value = 25L
        };
        Assert.True(_sut.EvaluateSingle(condition, ctx));
    }

    [Fact]
    public void EvaluateSingle_GreaterThan_TruePath()
    {
        var ctx = Ctx(source: new Dictionary<string, object> { ["temp"] = 30 });
        var condition = new RuleCondition
        {
            Property = "temp",
            Operator = ComparisonOperator.GreaterThan,
            Value = 25
        };
        Assert.True(_sut.EvaluateSingle(condition, ctx));
    }

    [Fact]
    public void EvaluateSingle_Contains_StringSubstring()
    {
        var ctx = Ctx(source: new Dictionary<string, object> { ["msg"] = "Hello World" });
        var condition = new RuleCondition
        {
            Property = "msg",
            Operator = ComparisonOperator.Contains,
            Value = "World"
        };
        Assert.True(_sut.EvaluateSingle(condition, ctx));
    }

    [Fact]
    public void EvaluateSingle_In_MatchesAnyCandidate()
    {
        var ctx = Ctx(source: new Dictionary<string, object> { ["status"] = "active" });
        var condition = new RuleCondition
        {
            Property = "status",
            Operator = ComparisonOperator.In,
            Values = new List<object> { "pending", "active", "done" }
        };
        Assert.True(_sut.EvaluateSingle(condition, ctx));
    }

    [Fact]
    public void EvaluateSingle_NotIn_FailsWhenValueIsInCollection()
    {
        var ctx = Ctx(source: new Dictionary<string, object> { ["status"] = "active" });
        var condition = new RuleCondition
        {
            Property = "status",
            Operator = ComparisonOperator.NotIn,
            Values = new List<object> { "active" }
        };
        Assert.False(_sut.EvaluateSingle(condition, ctx));
    }

    [Fact]
    public void EvaluateSingle_Between_InclusiveBounds()
    {
        var ctx = Ctx(source: new Dictionary<string, object> { ["temp"] = 22 });
        var condition = new RuleCondition
        {
            Property = "temp",
            Operator = ComparisonOperator.Between,
            Values = new List<object> { 20, 25 }
        };
        Assert.True(_sut.EvaluateSingle(condition, ctx));
    }

    [Fact]
    public void EvaluateSingle_Exists_TrueWhenPresent()
    {
        var ctx = Ctx(source: new Dictionary<string, object> { ["battery"] = 80 });
        var condition = new RuleCondition
        {
            Property = "battery",
            Operator = ComparisonOperator.Exists
        };
        Assert.True(_sut.EvaluateSingle(condition, ctx));
    }

    [Fact]
    public void EvaluateSingle_NotExists_TrueWhenAbsent()
    {
        var ctx = Ctx(source: new Dictionary<string, object> { ["other"] = 1 });
        var condition = new RuleCondition
        {
            Property = "battery",
            Operator = ComparisonOperator.NotExists
        };
        Assert.True(_sut.EvaluateSingle(condition, ctx));
    }

    [Fact]
    public void EvaluateSingle_Changed_TrueWhenPreviousDiffers()
    {
        var ctx = Ctx(
            source: new Dictionary<string, object> { ["state"] = "on" },
            previous: new Dictionary<string, object> { ["state"] = "off" });
        var condition = new RuleCondition
        {
            Property = "state",
            Operator = ComparisonOperator.Changed
        };
        Assert.True(_sut.EvaluateSingle(condition, ctx));
    }

    [Fact]
    public void Evaluate_AllGroup_RequiresEveryConditionTrue()
    {
        var ctx = Ctx(source: new Dictionary<string, object> { ["temp"] = 25, ["humidity"] = 60 });
        var group = new RuleConditionGroup
        {
            Logic = LogicOperator.All,
            Conditions = new List<RuleCondition>
            {
                new() { Property = "temp", Operator = ComparisonOperator.GreaterThan, Value = 20 },
                new() { Property = "humidity", Operator = ComparisonOperator.LessThan, Value = 70 }
            }
        };
        Assert.True(_sut.Evaluate(group, ctx));
    }

    [Fact]
    public void Evaluate_AllGroup_FailsWhenAnyConditionFalse()
    {
        var ctx = Ctx(source: new Dictionary<string, object> { ["temp"] = 25, ["humidity"] = 80 });
        var group = new RuleConditionGroup
        {
            Logic = LogicOperator.All,
            Conditions = new List<RuleCondition>
            {
                new() { Property = "temp", Operator = ComparisonOperator.GreaterThan, Value = 20 },
                new() { Property = "humidity", Operator = ComparisonOperator.LessThan, Value = 70 }
            }
        };
        Assert.False(_sut.Evaluate(group, ctx));
    }

    [Fact]
    public void Evaluate_AnyGroup_TrueWhenOneConditionHolds()
    {
        var ctx = Ctx(source: new Dictionary<string, object> { ["temp"] = 15 });
        var group = new RuleConditionGroup
        {
            Logic = LogicOperator.Any,
            Conditions = new List<RuleCondition>
            {
                new() { Property = "temp", Operator = ComparisonOperator.LessThan, Value = 20 },
                new() { Property = "temp", Operator = ComparisonOperator.GreaterThan, Value = 100 }
            }
        };
        Assert.True(_sut.Evaluate(group, ctx));
    }

    [Fact]
    public void Evaluate_NoneGroup_InvertsAll()
    {
        var ctx = Ctx(source: new Dictionary<string, object> { ["temp"] = 15 });
        var group = new RuleConditionGroup
        {
            Logic = LogicOperator.None,
            Conditions = new List<RuleCondition>
            {
                new() { Property = "temp", Operator = ComparisonOperator.GreaterThan, Value = 20 }
            }
        };
        Assert.True(_sut.Evaluate(group, ctx));
    }

    [Fact]
    public void EvaluateSingle_Changed_DottedPath_RoutesThroughGetPropertyValue()
    {
        // Regression guard for HasChanged: previously did a literal
        // dictionary lookup on the dotted key and always reported "not
        // changed" for nested properties.
        var ctx = new ExpressionContext
        {
            Source = new Dictionary<string, object>
            {
                ["battery"] = new Dictionary<string, object> { ["level"] = 80 }
            },
            Previous = new Dictionary<string, object>
            {
                ["battery"] = new Dictionary<string, object> { ["level"] = 50 }
            }
        };
        var condition = new RuleCondition
        {
            Property = "source.battery.level",
            Operator = ComparisonOperator.Changed
        };

        Assert.True(_sut.EvaluateSingle(condition, ctx));
    }

    [Fact]
    public void EvaluateSingle_Changed_DottedPath_Unchanged_ReportsFalse()
    {
        var ctx = new ExpressionContext
        {
            Source = new Dictionary<string, object>
            {
                ["battery"] = new Dictionary<string, object> { ["level"] = 50 }
            },
            Previous = new Dictionary<string, object>
            {
                ["battery"] = new Dictionary<string, object> { ["level"] = 50 }
            }
        };
        var condition = new RuleCondition
        {
            Property = "source.battery.level",
            Operator = ComparisonOperator.Changed
        };

        Assert.False(_sut.EvaluateSingle(condition, ctx));
    }

    [Fact]
    public void EvaluateSingle_Changed_NullPrevious_ReportsTrue()
    {
        // Contract: no previous state means every property is "changed".
        var ctx = new ExpressionContext
        {
            Source = new Dictionary<string, object> { ["v"] = 1 },
            Previous = null
        };
        var condition = new RuleCondition
        {
            Property = "v",
            Operator = ComparisonOperator.Changed
        };

        Assert.True(_sut.EvaluateSingle(condition, ctx));
    }

    [Fact]
    public void EvaluateSingle_In_NullCollection_ReturnsFalse()
    {
        var ctx = Ctx(source: new Dictionary<string, object> { ["status"] = "active" });
        var condition = new RuleCondition
        {
            Property = "status",
            Operator = ComparisonOperator.In,
            Values = null
        };
        Assert.False(_sut.EvaluateSingle(condition, ctx));
    }

    [Fact]
    public void EvaluateSingle_Between_WithOnlyOneBound_ReturnsFalse()
    {
        var ctx = Ctx(source: new Dictionary<string, object> { ["temp"] = 22 });
        var condition = new RuleCondition
        {
            Property = "temp",
            Operator = ComparisonOperator.Between,
            Values = new List<object> { 20 }
        };
        Assert.False(_sut.EvaluateSingle(condition, ctx));
    }

    [Fact]
    public void EvaluateSingle_Matches_CatastrophicRegex_DoesNotHang()
    {
        // ReDoS payload. With the NonBacktracking engine and the 100 ms
        // match timeout both enforced, this must return quickly (and false)
        // rather than pinning a core.
        var ctx = Ctx(source: new Dictionary<string, object>
        {
            ["text"] = new string('a', 50) + "X"
        });
        var condition = new RuleCondition
        {
            Property = "text",
            Operator = ComparisonOperator.Matches,
            Value = "^(a+)+$"
        };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = _sut.EvaluateSingle(condition, ctx);
        sw.Stop();

        Assert.False(result);
        Assert.True(sw.ElapsedMilliseconds < 500,
            $"Regex should not take {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public void EvaluateSingle_NumericCompare_InvariantCulture_DecimalPoint()
    {
        // TryGetDouble must parse "22.5" with InvariantCulture even on
        // locales where `,` is the decimal separator.
        var originalCulture = System.Threading.Thread.CurrentThread.CurrentCulture;
        try
        {
            System.Threading.Thread.CurrentThread.CurrentCulture =
                new System.Globalization.CultureInfo("sk-SK");

            var ctx = Ctx(source: new Dictionary<string, object> { ["temp"] = "22.5" });
            var condition = new RuleCondition
            {
                Property = "temp",
                Operator = ComparisonOperator.GreaterThan,
                Value = 20
            };
            Assert.True(_sut.EvaluateSingle(condition, ctx));
        }
        finally
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public void Evaluate_NestedGroups_ComposeLogic()
    {
        var ctx = Ctx(source: new Dictionary<string, object>
        {
            ["temp"] = 25,
            ["humidity"] = 40,
            ["mode"] = "auto"
        });
        // (temp > 20 AND humidity < 50) OR mode == "manual"
        var group = new RuleConditionGroup
        {
            Logic = LogicOperator.Any,
            Conditions = new List<RuleCondition>
            {
                new() { Property = "mode", Operator = ComparisonOperator.Equals, Value = "manual" }
            },
            Groups = new List<RuleConditionGroup>
            {
                new()
                {
                    Logic = LogicOperator.All,
                    Conditions = new List<RuleCondition>
                    {
                        new() { Property = "temp", Operator = ComparisonOperator.GreaterThan, Value = 20 },
                        new() { Property = "humidity", Operator = ComparisonOperator.LessThan, Value = 50 }
                    }
                }
            }
        };
        Assert.True(_sut.Evaluate(group, ctx));
    }

    [Fact]
    public void Matches_ValidPattern_HitsCacheOnRepeatedCalls()
    {
        ConditionEvaluator.ResetPatternCacheForTests();
        Assert.Equal(0, ConditionEvaluator.PatternCacheCountForTests);

        var ctx = Ctx(source: new Dictionary<string, object> { ["name"] = "gateway-07" });
        var condition = new RuleCondition
        {
            Property = "name",
            Operator = ComparisonOperator.Matches,
            Value = @"^gateway-\d+$"
        };

        Assert.True(_sut.EvaluateSingle(condition, ctx));
        Assert.Equal(1, ConditionEvaluator.PatternCacheCountForTests);

        // Second call with the same pattern should not grow the cache.
        Assert.True(_sut.EvaluateSingle(condition, ctx));
        Assert.Equal(1, ConditionEvaluator.PatternCacheCountForTests);
    }

    [Fact]
    public void Matches_DistinctPatterns_EachAddsOneCacheEntry()
    {
        ConditionEvaluator.ResetPatternCacheForTests();

        var ctx = Ctx(source: new Dictionary<string, object> { ["id"] = "sensor-42" });

        var a = new RuleCondition
        {
            Property = "id",
            Operator = ComparisonOperator.Matches,
            Value = @"^sensor-\d+$"
        };
        var b = new RuleCondition
        {
            Property = "id",
            Operator = ComparisonOperator.Matches,
            Value = @"^\w+-42$"
        };

        Assert.True(_sut.EvaluateSingle(a, ctx));
        Assert.True(_sut.EvaluateSingle(b, ctx));

        Assert.Equal(2, ConditionEvaluator.PatternCacheCountForTests);
    }

    [Fact]
    public void Matches_InvalidPattern_ReturnsFalseAndIsCached()
    {
        ConditionEvaluator.ResetPatternCacheForTests();

        var ctx = Ctx(source: new Dictionary<string, object> { ["text"] = "anything" });
        var condition = new RuleCondition
        {
            Property = "text",
            Operator = ComparisonOperator.Matches,
            Value = "[unclosed"
        };

        // Fail-closed on invalid regex.
        Assert.False(_sut.EvaluateSingle(condition, ctx));

        // Invalid pattern is cached as a null-regex sentinel so a
        // pathological rule cannot drive repeated compile-throws in the
        // hot path. Cache count must still be exactly 1.
        Assert.Equal(1, ConditionEvaluator.PatternCacheCountForTests);

        // Second call must not grow the cache and must still return false.
        Assert.False(_sut.EvaluateSingle(condition, ctx));
        Assert.Equal(1, ConditionEvaluator.PatternCacheCountForTests);
    }

    [Fact]
    public void Matches_NoRegression_CaseInsensitive()
    {
        // The old implementation set RegexOptions.IgnoreCase. Ensure the
        // cached compile preserves that flag.
        ConditionEvaluator.ResetPatternCacheForTests();

        var ctx = Ctx(source: new Dictionary<string, object> { ["name"] = "GATEWAY-01" });
        var condition = new RuleCondition
        {
            Property = "name",
            Operator = ComparisonOperator.Matches,
            Value = @"^gateway-\d+$"
        };

        Assert.True(_sut.EvaluateSingle(condition, ctx));
    }

    /// <summary>
    /// Build a context whose <see cref="ExpressionContext.GetDeviceState"/>
    /// probe counts invocations into the supplied counter. Used by the
    /// short-circuit tests to prove that later conditions are NOT evaluated
    /// once the outcome of the group is decided.
    /// </summary>
    private static ExpressionContext ProbeCtx(Action increment)
    {
        return new ExpressionContext
        {
            Source = new Dictionary<string, object> { ["x"] = 1 },
            GetDeviceState = _ =>
            {
                increment();
                return new Dictionary<string, object> { ["y"] = 10 };
            }
        };
    }

    private static RuleCondition ProbeCondition() => new()
    {
        // Uses the `device(...)` helper so evaluating this condition fires
        // the GetDeviceState callback. The helper must be the entire
        // expression body (not `.y`-suffixed) because ExpressionEvaluator's
        // function dispatcher needs the whole expression to end in `)`.
        // Returns a non-null Dictionary<string, object> which is truthy
        // under IsTruthy default branch (`_ => true`).
        Expression = "{{device(\"probe\")}}",
        Operator = ComparisonOperator.IsTrue
    };

    [Fact]
    public void Evaluate_All_ShortCircuitsOnFirstFalse()
    {
        var probeCalls = 0;
        var ctx = ProbeCtx(() => probeCalls++);

        var group = new RuleConditionGroup
        {
            Logic = LogicOperator.All,
            Conditions = new List<RuleCondition>
            {
                // First condition is false, which must short-circuit All.
                new() { Property = "x", Operator = ComparisonOperator.Equals, Value = 999 },
                // Second condition would increment the probe if it ran.
                ProbeCondition()
            }
        };

        Assert.False(_sut.Evaluate(group, ctx));
        Assert.Equal(0, probeCalls);
    }

    [Fact]
    public void Evaluate_Any_ShortCircuitsOnFirstTrue()
    {
        var probeCalls = 0;
        var ctx = ProbeCtx(() => probeCalls++);

        var group = new RuleConditionGroup
        {
            Logic = LogicOperator.Any,
            Conditions = new List<RuleCondition>
            {
                // First condition is true, which must short-circuit Any.
                new() { Property = "x", Operator = ComparisonOperator.Equals, Value = 1 },
                ProbeCondition()
            }
        };

        Assert.True(_sut.Evaluate(group, ctx));
        Assert.Equal(0, probeCalls);
    }

    [Fact]
    public void Evaluate_None_ShortCircuitsOnFirstTrue()
    {
        var probeCalls = 0;
        var ctx = ProbeCtx(() => probeCalls++);

        var group = new RuleConditionGroup
        {
            Logic = LogicOperator.None,
            Conditions = new List<RuleCondition>
            {
                // First condition is true, so None must return false and
                // skip evaluating the probe.
                new() { Property = "x", Operator = ComparisonOperator.Equals, Value = 1 },
                ProbeCondition()
            }
        };

        Assert.False(_sut.Evaluate(group, ctx));
        Assert.Equal(0, probeCalls);
    }

    [Fact]
    public void Evaluate_All_WithNestedGroup_ShortCircuitsOnFalseNested()
    {
        var probeCalls = 0;
        var ctx = ProbeCtx(() => probeCalls++);

        var group = new RuleConditionGroup
        {
            Logic = LogicOperator.All,
            Groups = new List<RuleConditionGroup>
            {
                // Nested group resolves to false (inner condition is false).
                new()
                {
                    Logic = LogicOperator.All,
                    Conditions = new List<RuleCondition>
                    {
                        new() { Property = "x", Operator = ComparisonOperator.Equals, Value = 999 }
                    }
                },
                // If reached, this nested group would fire the probe.
                new()
                {
                    Logic = LogicOperator.All,
                    Conditions = new List<RuleCondition> { ProbeCondition() }
                }
            }
        };

        Assert.False(_sut.Evaluate(group, ctx));
        Assert.Equal(0, probeCalls);
    }

    [Fact]
    public void Evaluate_All_AllTrue_RunsEveryCondition()
    {
        // Regression guard: when no short-circuit is possible, all
        // conditions must be evaluated. Probe must fire exactly once.
        var probeCalls = 0;
        var ctx = ProbeCtx(() => probeCalls++);

        var group = new RuleConditionGroup
        {
            Logic = LogicOperator.All,
            Conditions = new List<RuleCondition>
            {
                new() { Property = "x", Operator = ComparisonOperator.Equals, Value = 1 },
                ProbeCondition()
            }
        };

        Assert.True(_sut.Evaluate(group, ctx));
        Assert.Equal(1, probeCalls);
    }

    [Fact]
    public void GetPropertyValue_SingleSegment_CacheHitsOnRepeatedCalls()
    {
        ConditionEvaluator.ResetPathCacheForTests();
        Assert.Equal(0, ConditionEvaluator.PathCacheCountForTests);

        var ctx = Ctx(source: new Dictionary<string, object> { ["temp"] = 23.5 });
        var condition = new RuleCondition
        {
            Property = "temp",
            Operator = ComparisonOperator.GreaterThan,
            Value = 20
        };

        Assert.True(_sut.EvaluateSingle(condition, ctx));
        Assert.Equal(1, ConditionEvaluator.PathCacheCountForTests);

        // Second call must hit the cache.
        Assert.True(_sut.EvaluateSingle(condition, ctx));
        Assert.Equal(1, ConditionEvaluator.PathCacheCountForTests);
    }

    [Fact]
    public void GetPropertyValue_KnownPrefix_CachesAndResolvesCorrectly()
    {
        ConditionEvaluator.ResetPathCacheForTests();

        var ctx = new ExpressionContext
        {
            Source = new Dictionary<string, object> { ["temp"] = 10 },
            State = new Dictionary<string, object>
            {
                ["counter"] = 42
            }
        };

        var condition = new RuleCondition
        {
            Property = "state.counter",
            Operator = ComparisonOperator.Equals,
            Value = 42
        };

        Assert.True(_sut.EvaluateSingle(condition, ctx));
        Assert.Equal(1, ConditionEvaluator.PathCacheCountForTests);

        // Repeated calls reuse cache.
        Assert.True(_sut.EvaluateSingle(condition, ctx));
        Assert.Equal(1, ConditionEvaluator.PathCacheCountForTests);
    }

    [Fact]
    public void GetPropertyValue_UnknownPrefix_FallsBackToSourceAndCaches()
    {
        ConditionEvaluator.ResetPathCacheForTests();

        // `position.x` has no known namespace prefix, so the parser must
        // treat the entire path as nested keys under Source.
        var ctx = Ctx(source: new Dictionary<string, object>
        {
            ["position"] = new Dictionary<string, object> { ["x"] = 12.5 }
        });
        var condition = new RuleCondition
        {
            Property = "position.x",
            Operator = ComparisonOperator.Equals,
            Value = 12.5
        };

        Assert.True(_sut.EvaluateSingle(condition, ctx));
        Assert.Equal(1, ConditionEvaluator.PathCacheCountForTests);
    }

    [Fact]
    public void GetPropertyValue_DistinctPaths_EachAddsCacheEntry()
    {
        ConditionEvaluator.ResetPathCacheForTests();

        var ctx = new ExpressionContext
        {
            Source = new Dictionary<string, object>
            {
                ["temp"] = 10,
                ["humidity"] = 50
            },
            State = new Dictionary<string, object>
            {
                ["counter"] = 1
            }
        };

        var c1 = new RuleCondition { Property = "temp", Operator = ComparisonOperator.GreaterThan, Value = 5 };
        var c2 = new RuleCondition { Property = "humidity", Operator = ComparisonOperator.GreaterThan, Value = 5 };
        var c3 = new RuleCondition { Property = "state.counter", Operator = ComparisonOperator.GreaterThan, Value = 0 };

        _sut.EvaluateSingle(c1, ctx);
        _sut.EvaluateSingle(c2, ctx);
        _sut.EvaluateSingle(c3, ctx);

        Assert.Equal(3, ConditionEvaluator.PathCacheCountForTests);
    }

    [Fact]
    public void GetPropertyValue_CaseInsensitivePrefix_IsPreservedThroughCache()
    {
        // The old implementation used `KnownPrefixes` with
        // StringComparer.OrdinalIgnoreCase, so `Source.temp` and `source.temp`
        // resolved identically. Regression guard for that behaviour.
        ConditionEvaluator.ResetPathCacheForTests();

        var ctx = Ctx(source: new Dictionary<string, object> { ["temp"] = 42 });

        var lowerCond = new RuleCondition
        {
            Property = "source.temp",
            Operator = ComparisonOperator.Equals,
            Value = 42
        };
        var upperCond = new RuleCondition
        {
            Property = "Source.temp",
            Operator = ComparisonOperator.Equals,
            Value = 42
        };

        Assert.True(_sut.EvaluateSingle(lowerCond, ctx));
        Assert.True(_sut.EvaluateSingle(upperCond, ctx));

        // Cache keys are case-sensitive so each string literally adds one
        // entry, but both resolve to the same PathNamespace.Source under
        // the hood. This is intentional: the key is the exact property
        // string, not its normalised form.
        Assert.Equal(2, ConditionEvaluator.PathCacheCountForTests);
    }
}
