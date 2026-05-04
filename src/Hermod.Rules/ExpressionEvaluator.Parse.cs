using System.Text;

namespace Hermod.Rules;

public sealed partial class ExpressionEvaluator
{
    /// <summary>
    /// Strip a single layer of balanced parentheses wrapping the whole
    /// expression. <c>(a + b)</c> becomes <c>a + b</c>, but <c>(a) + (b)</c>
    /// is returned unchanged.
    /// </summary>
    private static string StripOuterParens(string expression)
    {
        expression = expression.Trim();
        while (expression.Length >= 2 && expression[0] == '(' && expression[^1] == ')')
        {
            var depth = 0;
            var wrapsWhole = true;
            for (var i = 0; i < expression.Length; i++)
            {
                if (expression[i] == '(') depth++;
                else if (expression[i] == ')') depth--;
                if (depth == 0 && i < expression.Length - 1)
                {
                    wrapsWhole = false;
                    break;
                }
            }
            if (!wrapsWhole) break;
            expression = expression[1..^1].Trim();
        }
        return expression;
    }

    /// <summary>
    /// Right-to-left scan for a multi-char operator at paren depth 0 and
    /// outside string literals. Returns the starting index, or -1.
    /// Scan starts at the last character so every quote gets visited —
    /// starting at <c>length - op.Length</c> skipped the final op.Length-1
    /// characters, which could contain the closing quote of a string
    /// literal whose body included the operator. The op-match is bounds-
    /// guarded so we still only return positions where the full operator
    /// fits.
    /// </summary>
    private static int FindOperatorAt(string expression, string op)
    {
        var depth = 0;
        var inString = false;

        for (var i = expression.Length - 1; i >= 0; i--)
        {
            var c = expression[i];
            if (c == '"' || c == '\'')
            {
                inString = !inString;
                continue;
            }
            if (inString) continue;

            if (c == ')') depth++;
            else if (c == '(') depth--;
            else if (depth == 0 &&
                     i + op.Length <= expression.Length &&
                     expression.AsSpan(i, op.Length).SequenceEqual(op))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Single-pass right-to-left scan for the rightmost occurrence of every
    /// comparison operator. The first-priority operator (<c>&gt;=</c>) wins
    /// and early-exits the scan. Skips parenthesised regions and string
    /// literals. Semantically identical to six separate operator scans.
    /// </summary>
    private static (int Index, string? Op) FindTopLevelComparisonOperator(string expression)
    {
        var ops = ComparisonOperators;
        Span<int> rightmost = stackalloc int[ops.Length];
        for (var k = 0; k < ops.Length; k++) rightmost[k] = -1;
        var found = 0;

        var depth = 0;
        var inString = false;

        for (var i = expression.Length - 1; i >= 0; i--)
        {
            var c = expression[i];
            if (c == '"' || c == '\'')
            {
                inString = !inString;
                continue;
            }
            if (inString) continue;

            if (c == ')') { depth++; continue; }
            if (c == '(') { depth--; continue; }
            if (depth != 0) continue;

            for (var oi = 0; oi < ops.Length; oi++)
            {
                if (rightmost[oi] >= 0) continue;
                var op = ops[oi];
                if (i + op.Length > expression.Length) continue;
                if (!expression.AsSpan(i, op.Length).SequenceEqual(op)) continue;
                rightmost[oi] = i;
                found++;

                if (oi == 0) return (i, ops[0]);
            }

            if (found == ops.Length) break;
        }

        // >= 0: index 0 is a valid match position (matches the early
        // return above which uses `i` directly).
        for (var oi = 0; oi < ops.Length; oi++)
        {
            if (rightmost[oi] >= 0) return (rightmost[oi], ops[oi]);
        }
        return (-1, null);
    }

    /// <summary>
    /// Single-pass right-to-left scan for the rightmost occurrence of every
    /// arithmetic operator. Precedence handling: <c>+</c> and <c>-</c> split
    /// at the outer level before <c>*</c> and <c>/</c>. Unary sign usage
    /// (<c>5 * -3</c>) is detected so signs inside numeric literals are not
    /// mistaken for binary operators.
    /// </summary>
    private static (int Index, char Op) FindTopLevelArithmeticOperator(string expression)
    {
        var ops = ArithmeticOperators;
        Span<int> rightmost = stackalloc int[ops.Length];
        for (var k = 0; k < ops.Length; k++) rightmost[k] = -1;

        var depth = 0;
        var inString = false;

        for (var i = expression.Length - 1; i >= 0; i--)
        {
            var c = expression[i];
            if (c == '"' || c == '\'')
            {
                inString = !inString;
                continue;
            }
            if (inString) continue;

            if (c == ')') { depth++; continue; }
            if (c == '(') { depth--; continue; }
            if (depth != 0) continue;

            for (var oi = 0; oi < ops.Length; oi++)
            {
                if (rightmost[oi] >= 0) continue;
                if (c != ops[oi]) continue;

                if ((c == '-' || c == '+') && IsUnarySign(expression, i)) continue;

                rightmost[oi] = i;
                if (oi == 0) return (i, ops[0]);
                break;
            }
        }

        // >= 0: matches the early-return's `i`-based acceptance of index 0.
        for (var oi = 0; oi < ops.Length; oi++)
        {
            if (rightmost[oi] >= 0) return (rightmost[oi], ops[oi]);
        }
        return (-1, '\0');
    }

    private static bool IsUnarySign(string expression, int index)
    {
        var j = index - 1;
        while (j >= 0 && char.IsWhiteSpace(expression[j])) j--;
        if (j < 0) return true;

        var prev = expression[j];
        return prev is '+' or '-' or '*' or '/' or '(';
    }

    /// <summary>
    /// Split a function-argument list at top-level commas, respecting nested
    /// parens and string literals. Uses <see cref="StringBuilder"/> so an
    /// n-character argument allocates O(n) once rather than O(n^2) via
    /// repeated string concatenation.
    /// </summary>
    private static List<string> ParseArguments(string argsStr)
    {
        var args = new List<string>();
        var current = new StringBuilder(64);
        var depth = 0;
        var inString = false;

        foreach (var c in argsStr)
        {
            if (c == '"' || c == '\'') inString = !inString;

            if (!inString)
            {
                if (c == '(') depth++;
                else if (c == ')') depth--;
                else if (c == ',' && depth == 0)
                {
                    args.Add(current.ToString().Trim());
                    current.Clear();
                    continue;
                }
            }

            current.Append(c);
        }

        if (current.Length > 0)
        {
            var trimmed = current.ToString().Trim();
            if (trimmed.Length > 0) args.Add(trimmed);
        }

        return args;
    }
}
