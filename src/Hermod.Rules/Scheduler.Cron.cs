namespace Hermod.Rules;

public sealed partial class Scheduler
{
    /// <summary>
    /// Computes the next occurrence of a cron expression strictly after now.
    /// Supports the standard 5-field POSIX form (minute hour day month weekday)
    /// with literals, <c>*</c>, <c>*/n</c>, comma lists, and ranges. POSIX
    /// day-of-month and day-of-week semantics: when both are wildcards, AND;
    /// when both are restricted, OR; when one is a wildcard, use the other
    /// alone. Sunday accepts both <c>0</c> and <c>7</c>.
    /// </summary>
    private DateTime? GetNextCronOccurrence(string cronExpression)
    {
        var parts = cronExpression.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 5) return null;

        parts[4] = NormalizeDowField(parts[4]);

        // Reject patterns that cannot match anything before the walk starts.
        if (!IsFieldReachable(parts[0], 0, 59) ||
            !IsFieldReachable(parts[1], 0, 23) ||
            !IsFieldReachable(parts[2], 1, 31) ||
            !IsFieldReachable(parts[3], 1, 12) ||
            !IsFieldReachable(parts[4], 0, 6))
        {
            return null;
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var candidate = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, DateTimeKind.Utc)
            .AddMinutes(1);

        // Skip-aware walk: when a coarser field rejects the candidate, jump
        // by the coarsest unit that can plausibly satisfy it. Sparse
        // patterns (e.g. "0 0 29 2 *") then converge in O(months) instead of
        // a full minute-by-minute year scan.
        for (var i = 0; i < 525600; i++)
        {
            if (!MatchesCronField(parts[3], candidate.Month, 1, 12))
            {
                candidate = new DateTime(candidate.Year, candidate.Month, 1, 0, 0, 0, DateTimeKind.Utc)
                    .AddMonths(1);
                continue;
            }

            var dom = parts[2];
            var dow = parts[4];
            var domHit = MatchesCronField(dom, candidate.Day, 1, 31);
            var dowHit = MatchesCronField(dow, NormalizeDayOfWeek((int)candidate.DayOfWeek), 0, 6);
            var dayHit = (dom != "*" && dow != "*") ? (domHit || dowHit) : (domHit && dowHit);
            if (!dayHit)
            {
                candidate = candidate.Date.AddDays(1);
                continue;
            }

            if (!MatchesCronField(parts[1], candidate.Hour, 0, 23))
            {
                candidate = new DateTime(
                        candidate.Year, candidate.Month, candidate.Day,
                        candidate.Hour, 0, 0, DateTimeKind.Utc)
                    .AddHours(1);
                continue;
            }

            if (!MatchesCronField(parts[0], candidate.Minute, 0, 59))
            {
                candidate = candidate.AddMinutes(1);
                continue;
            }

            return candidate;
        }

        return null;
    }

    private static int NormalizeDayOfWeek(int value) => value % 7;

    private static string NormalizeDowField(string field)
    {
        if (field == "*" || field.StartsWith("*/", StringComparison.Ordinal)) return field;

        var parts = field.Split(',');
        for (var i = 0; i < parts.Length; i++)
        {
            var p = parts[i].Trim();
            if (p == "7") { parts[i] = "0"; continue; }
            if (p.Contains('-', StringComparison.Ordinal))
            {
                var range = p.Split('-');
                if (range.Length == 2)
                {
                    if (range[0] == "7") range[0] = "0";
                    if (range[1] == "7") range[1] = "0";
                    parts[i] = string.Join('-', range);
                }
            }
        }
        return string.Join(',', parts);
    }

    private static bool IsFieldReachable(string field, int min, int max)
    {
        if (field == "*" || field.StartsWith("*/", StringComparison.Ordinal)) return true;

        foreach (var piece in field.Split(','))
        {
            var p = piece.Trim();
            if (p.Contains('-', StringComparison.Ordinal))
            {
                var range = p.Split('-');
                if (range.Length == 2 &&
                    int.TryParse(range[0], out var start) &&
                    int.TryParse(range[1], out var end) &&
                    start >= min && end <= max && start <= end)
                {
                    return true;
                }
                continue;
            }

            if (int.TryParse(p, out var single) && single >= min && single <= max)
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchesCronField(string field, int value, int min, int max)
    {
        if (field == "*") return true;

        if (field.StartsWith("*/", StringComparison.Ordinal))
        {
            return int.TryParse(field[2..], out var step) && step > 0 && value % step == 0;
        }

        if (field.Contains(',', StringComparison.Ordinal))
        {
            foreach (var piece in field.Split(','))
            {
                if (MatchesCronField(piece.Trim(), value, min, max)) return true;
            }
            return false;
        }

        if (field.Contains('-', StringComparison.Ordinal))
        {
            var rangeParts = field.Split('-');
            if (rangeParts.Length == 2 &&
                int.TryParse(rangeParts[0], out var start) &&
                int.TryParse(rangeParts[1], out var end))
            {
                return value >= start && value <= end;
            }
        }

        return int.TryParse(field, out var single) && value == single;
    }
}
