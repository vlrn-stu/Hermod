using Hermod.Core.Interfaces;
using Hermod.Rules.Coercion;

namespace Hermod.Rules;

public sealed partial class ExpressionEvaluator
{
    private static readonly string[] ComparisonOperators = [">=", "<=", "!=", "==", ">", "<"];
    private static readonly char[] ArithmeticOperators = ['+', '-', '*', '/'];
    private const double FloatEpsilon = 0.0001;

    private bool TryEvaluateLogical(string expression, ExpressionContext context, out object? result)
    {
        result = null;

        // `||` binds looser than `&&` so try `||` first: the outer split ends
        // up at the looser operator, putting `&&` subtrees on either side.
        var orIdx = FindOperatorAt(expression, "||");
        if (orIdx > 0)
        {
            var left = expression[..orIdx].Trim();
            var right = expression[(orIdx + 2)..].Trim();

            if (EvaluateSubExpressionAsBool(left, context))
            {
                result = true;
                return true;
            }
            result = EvaluateSubExpressionAsBool(right, context);
            return true;
        }

        var andIdx = FindOperatorAt(expression, "&&");
        if (andIdx > 0)
        {
            var left = expression[..andIdx].Trim();
            var right = expression[(andIdx + 2)..].Trim();

            if (!EvaluateSubExpressionAsBool(left, context))
            {
                result = false;
                return true;
            }
            result = EvaluateSubExpressionAsBool(right, context);
            return true;
        }

        // Unary `!`, guarding against swallowing the leading `!` of `!=`.
        if (expression.StartsWith('!') && (expression.Length < 2 || expression[1] != '='))
        {
            var inner = expression[1..].Trim();
            result = !EvaluateSubExpressionAsBool(inner, context);
            return true;
        }

        return false;
    }

    private bool EvaluateSubExpressionAsBool(string expression, ExpressionContext context) =>
        CoerceToBool(EvaluateExpression(expression, context));

    private bool TryEvaluateComparison(string expression, ExpressionContext context, out object? result)
    {
        result = null;

        var (idx, op) = FindTopLevelComparisonOperator(expression);
        if (idx < 0 || op is null) return false;

        var left = expression[..idx].Trim();
        var right = expression[(idx + op.Length)..].Trim();

        var leftVal = EvaluateExpression(left, context);
        var rightVal = EvaluateExpression(right, context);

        result = CompareValues(leftVal, rightVal, op);
        return true;
    }

    private bool TryEvaluateArithmetic(string expression, ExpressionContext context, out object? result)
    {
        result = null;

        var (idx, op) = FindTopLevelArithmeticOperator(expression);
        if (idx < 0 || op == '\0') return false;

        var left = expression[..idx].Trim();
        var right = expression[(idx + 1)..].Trim();

        var leftVal = EvaluateExpression(left, context);
        var rightVal = EvaluateExpression(right, context);

        if (NumericCoercion.TryToDouble(leftVal, out var leftNum) &&
            NumericCoercion.TryToDouble(rightVal, out var rightNum))
        {
            result = op switch
            {
                '+' => leftNum + rightNum,
                '-' => leftNum - rightNum,
                '*' => leftNum * rightNum,
                '/' when rightNum != 0 => leftNum / rightNum,
                _ => null,
            };
            return result is not null;
        }

        // Non-numeric operands do not silently fall back to string
        // concatenation on `+`; that would mask missing-property bugs.
        return false;
    }

    private static bool CompareValues(object? left, object? right, string op)
    {
        if (NumericCoercion.TryToDouble(left, out var leftNum) &&
            NumericCoercion.TryToDouble(right, out var rightNum))
        {
            return op switch
            {
                "==" => Math.Abs(leftNum - rightNum) < FloatEpsilon,
                "!=" => Math.Abs(leftNum - rightNum) >= FloatEpsilon,
                ">" => leftNum > rightNum,
                "<" => leftNum < rightNum,
                ">=" => leftNum >= rightNum,
                "<=" => leftNum <= rightNum,
                _ => false,
            };
        }

        var leftStr = left?.ToString() ?? "";
        var rightStr = right?.ToString() ?? "";

        return op switch
        {
            "==" => leftStr.Equals(rightStr, StringComparison.OrdinalIgnoreCase),
            "!=" => !leftStr.Equals(rightStr, StringComparison.OrdinalIgnoreCase),
            ">" => string.Compare(leftStr, rightStr, StringComparison.OrdinalIgnoreCase) > 0,
            "<" => string.Compare(leftStr, rightStr, StringComparison.OrdinalIgnoreCase) < 0,
            ">=" => string.Compare(leftStr, rightStr, StringComparison.OrdinalIgnoreCase) >= 0,
            "<=" => string.Compare(leftStr, rightStr, StringComparison.OrdinalIgnoreCase) <= 0,
            _ => false,
        };
    }
}
