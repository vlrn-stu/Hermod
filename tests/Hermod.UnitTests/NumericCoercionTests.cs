using Hermod.Rules.Coercion;
using Xunit;

namespace Hermod.UnitTests;

/// <summary>
/// Pins <see cref="NumericCoercion"/>. The helper is load-bearing for
/// every rule condition that compares device state values — regressions
/// here silently break rule firing across the whole engine. Zero direct
/// tests existed before this fixture.
/// </summary>
public class NumericCoercionTests
{
    [Fact]
    public void TryToDouble_Null_ReturnsFalse()
    {
        Assert.False(NumericCoercion.TryToDouble(null, out var r));
        Assert.Equal(0, r);
    }

    [Theory]
    [InlineData((double)3.14, 3.14)]
    [InlineData((float)2.5f, 2.5)]
    [InlineData(42, 42.0)]
    [InlineData(100L, 100.0)]
    [InlineData((short)7, 7.0)]
    [InlineData((byte)5, 5.0)]
    public void TryToDouble_PrimitiveNumerics_Convert(object input, double expected)
    {
        Assert.True(NumericCoercion.TryToDouble(input, out var r));
        Assert.Equal(expected, r);
    }

    [Fact]
    public void TryToDouble_Decimal_Converts()
    {
        Assert.True(NumericCoercion.TryToDouble(12.5m, out var r));
        Assert.Equal(12.5, r);
    }

    [Theory]
    [InlineData(true, 1.0)]
    [InlineData(false, 0.0)]
    public void TryToDouble_Bool_MapsTo1Or0(bool input, double expected)
    {
        Assert.True(NumericCoercion.TryToDouble(input, out var r));
        Assert.Equal(expected, r);
    }

    [Theory]
    [InlineData("42", 42.0)]
    [InlineData("3.14", 3.14)]
    [InlineData("-5", -5.0)]
    [InlineData("1e3", 1000.0)]
    public void TryToDouble_ParseableString_Converts(string input, double expected)
    {
        Assert.True(NumericCoercion.TryToDouble(input, out var r));
        Assert.Equal(expected, r);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("12xyz")]
    public void TryToDouble_NonNumericString_ReturnsFalse(string input)
    {
        Assert.False(NumericCoercion.TryToDouble(input, out _));
    }

    [Fact]
    public void TryToDouble_CommaAsGroupSeparator_ReadsAsThousands()
    {
        // Invariant culture: ',' is the group (thousands) separator,
        // '.' is the decimal. "1,500" parses as 1500, "1.5" as 1.5.
        // Rule authors must NOT see host-locale-dependent behaviour
        // (German user writing "1,5" and getting 1.5 on their machine
        // but 15 / reject on the server). Pin both directions.
        Assert.True(NumericCoercion.TryToDouble("1,500", out var thousands));
        Assert.Equal(1500.0, thousands);

        Assert.True(NumericCoercion.TryToDouble("1.5", out var decimalPoint));
        Assert.Equal(1.5, decimalPoint);
    }

    [Fact]
    public void ToDoubleOrDefault_Parseable_ReturnsValue()
        => Assert.Equal(42.0, NumericCoercion.ToDoubleOrDefault("42"));

    [Fact]
    public void ToDoubleOrDefault_Unparseable_ReturnsDefault()
        => Assert.Equal(-1.0, NumericCoercion.ToDoubleOrDefault("nope", defaultValue: -1));

    [Fact]
    public void ToDoubleOrDefault_Null_ReturnsDefault()
        => Assert.Equal(99.0, NumericCoercion.ToDoubleOrDefault(null, defaultValue: 99));

    [Theory]
    [InlineData(42, 42)]
    [InlineData("100", 100)]
    [InlineData(3.7, 3)]           // truncates toward zero
    [InlineData(-2.9, -2)]         // truncates toward zero (not floor)
    public void ToInt_ValidInputs_Truncate(object input, int expected)
        => Assert.Equal(expected, NumericCoercion.ToInt(input));

    [Fact]
    public void ToInt_Null_ReturnsNull()
        => Assert.Null(NumericCoercion.ToInt(null));

    [Fact]
    public void ToInt_NonNumericString_ReturnsNull()
        => Assert.Null(NumericCoercion.ToInt("abc"));

    [Fact]
    public void LooseEquals_BothNull_True()
        => Assert.True(NumericCoercion.LooseEquals(null, null));

    [Theory]
    [InlineData(null, 1)]
    [InlineData(1, null)]
    public void LooseEquals_OneNull_False(object? a, object? b)
        => Assert.False(NumericCoercion.LooseEquals(a, b));

    [Fact]
    public void LooseEquals_CrossNumericType_True()
    {
        // Headline use case: device state may carry int 1, json
        // deserializer may produce long 1, rule author writes == 1.0.
        Assert.True(NumericCoercion.LooseEquals(1, 1.0));
        Assert.True(NumericCoercion.LooseEquals(1L, 1.0));
        Assert.True(NumericCoercion.LooseEquals(1, "1"));
    }

    [Fact]
    public void LooseEquals_WithinEpsilon_True()
    {
        // Default epsilon 0.0001, STRICTLY less-than.
        Assert.True(NumericCoercion.LooseEquals(1.00005, 1.0));
    }

    [Fact]
    public void LooseEquals_OutsideEpsilon_False()
    {
        Assert.False(NumericCoercion.LooseEquals(1.001, 1.0));
    }

    [Fact]
    public void LooseEquals_EqualStrings_True()
        => Assert.True(NumericCoercion.LooseEquals("foo", "foo"));

    [Fact]
    public void LooseEquals_CaseInsensitiveOption_Works()
    {
        Assert.False(NumericCoercion.LooseEquals("FOO", "foo"));
        Assert.True(NumericCoercion.LooseEquals("FOO", "foo", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Compare_BothNull_Zero()
        => Assert.Equal(0, NumericCoercion.Compare(null, null));

    [Fact]
    public void Compare_NullSortsBefore()
    {
        Assert.True(NumericCoercion.Compare(null, 1) < 0);
        Assert.True(NumericCoercion.Compare(1, null) > 0);
    }

    [Fact]
    public void Compare_Numeric_UsesNumericOrdering()
    {
        Assert.True(NumericCoercion.Compare(1, 2) < 0);
        Assert.True(NumericCoercion.Compare(10, "2") > 0);
        // Lexical string sort would say "10" < "2" — the numeric path
        // must take precedence when both sides parse as numbers.
    }

    [Fact]
    public void Compare_NonNumericStrings_LexicalOrder()
    {
        Assert.True(NumericCoercion.Compare("apple", "banana") < 0);
        Assert.Equal(0, NumericCoercion.Compare("x", "x"));
    }
}
