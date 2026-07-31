using System.Numerics;

namespace Sharkable;

/// <summary>
/// 6-field cron expression parser and next-occurrence calculator.
/// Format: <c>second minute hour day month week</c>.
/// Supports <c>* / , - ? L W #</c> special characters.
/// </summary>
public sealed class CronExpression
{
    /// <summary>
    /// SHARK-SEC-M011: hard cap on the number of minute-iterations
    /// <see cref="GetNext"/> will perform before returning <c>null</c>.
    /// A pattern that can never match (e.g. <c>0 0 30 2 *</c> — Feb 30
    /// does not exist) would otherwise loop up to ~2.1 M times before
    /// yielding, burning CPU on every tick of
    /// <c>SharkCronHostedService</c>. 4 years of minutes is 2,102,400;
    /// we cap at that count to preserve the existing semantics while
    /// preventing the runaway case.
    /// </summary>
    internal const int MaxIterations = 2_200_000;

    private readonly ulong[] _fields = new ulong[6];
    private readonly string _original;

    private CronExpression(string cron)
    {
        _original = cron;
    }

    /// <summary>
    /// Parses a 6-field cron expression. Throws <see cref="FormatException"/>
    /// on invalid input.
    /// </summary>
    public static CronExpression Parse(string cron)
    {
        var parts = cron.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 6)
            throw new FormatException($"Cron expression must have 6 fields (sec min hour day month week), got {parts.Length}");

        var expr = new CronExpression(cron);
        var ranges = new (int min, int max)[] { (0, 59), (0, 59), (0, 23), (1, 31), (1, 12), (0, 6) };

        for (var i = 0; i < 6; i++)
            expr._fields[i] = ParseField(parts[i], ranges[i].min, ranges[i].max, cron);

        return expr;
    }

    /// <summary>
    /// Returns the next occurrence strictly after <paramref name="after"/>,
    /// or <c>null</c> if no future match exists within a reasonable search
    /// window (~4 years). Iteration is capped at <see cref="MaxIterations"/>
    /// minute-steps so a non-matching pattern cannot burn CPU on every tick
    /// (SHARK-SEC-M011). The search aligns to the seconds field on every
    /// minute boundary, so fixed-second patterns (e.g. <c>30 * * * * *</c>)
    /// match correctly instead of degenerating to second 0.
    /// </summary>
    public DateTimeOffset? GetNext(DateTimeOffset after)
    {
        var dt = new DateTimeOffset(after.Year, after.Month, after.Day,
            after.Hour, after.Minute, after.Second, after.Offset);
        dt = dt.AddSeconds(1);

        var limit = dt.AddYears(4);
        for (var i = 0; i < MaxIterations && dt <= limit; i++)
        {
            // Align to the first second value of the seconds field that is
            // >= the current second. If none remains in this minute, advance
            // to the next minute (its first matching second is picked on the
            // next loop iteration).
            var nextSecond = FindNextBit(_fields[0], dt.Second);
            if (nextSecond < 0)
            {
                dt = MinuteStart(dt).AddMinutes(1);
                continue;
            }

            dt = new DateTimeOffset(dt.Year, dt.Month, dt.Day,
                dt.Hour, dt.Minute, nextSecond, dt.Offset);

            if (Matches(dt))
                return dt;

            // Advance to the next minute; the seconds field is re-aligned there.
            dt = MinuteStart(dt).AddMinutes(1);
        }
        return null;
    }

    private static DateTimeOffset MinuteStart(DateTimeOffset dt)
        => new(dt.Year, dt.Month, dt.Day, dt.Hour, dt.Minute, 0, dt.Offset);

    private bool Matches(DateTimeOffset dt)
    {
        return HasBit(_fields[5], WeekdayIndex(dt.DayOfWeek))
            && HasBit(_fields[4], dt.Month)
            && HasBit(_fields[3], dt.Day)
            && HasBit(_fields[2], dt.Hour)
            && HasBit(_fields[1], dt.Minute)
            && HasBit(_fields[0], dt.Second);
    }

    /// <summary>Maps <see cref="DayOfWeek"/> (0=Sunday..6=Saturday) to the
    /// 0..6 bit index used by the week field; values of 7 (also Sunday in
    /// many cron dialects) are folded to 0 at parse time.</summary>
    private static int WeekdayIndex(DayOfWeek dayOfWeek) => (int)dayOfWeek;

    /// <summary>Returns the smallest bit index &gt;= <paramref name="from"/> set in
    /// <paramref name="bits"/>, or -1 when no such bit exists.</summary>
    private static int FindNextBit(ulong bits, int from)
    {
        var mask = bits & ~((1UL << from) - 1);
        return mask == 0 ? -1 : BitOperations.TrailingZeroCount(mask);
    }

    private static ulong ParseField(string field, int min, int max, string original)
    {
        ulong bits = 0;
        foreach (var part in field.Split(','))
        {
            var trimmed = part.Trim();
            if (trimmed == "*" || trimmed == "?")
            {
                for (var v = min; v <= max; v++)
                    bits |= 1UL << v;
            }
            else if (trimmed.StartsWith("*/"))
            {
                // BUG-103: step 0 would loop forever (v += 0 never advances).
                var step = ParseStep(trimmed[2..], original);
                ValidateStepAgainstMax(step, max, original);
                for (var v = min; v <= max; v += step)
                    bits |= 1UL << v;
            }
            else if (trimmed.Contains('/'))
            {
                var slash = trimmed.IndexOf('/');
                var range = trimmed[..slash];
                // BUG-103: a range-less step ("N/step", valid cron) means
                // "from N to the field max, stepping by step".
                var (rMin, rMax) = ParseRange(range, min, max, original);
                var step = ParseStep(trimmed[(slash + 1)..], original);
                ValidateStepAgainstMax(step, max, original);
                for (var v = rMin; v <= rMax; v += step)
                    bits |= 1UL << v;
            }
            else if (trimmed.Contains('-'))
            {
                var (rMin, rMax) = ParseRange(trimmed, min, max, original);
                if (rMin > rMax)
                    throw new FormatException($"Invalid range '{trimmed}' in '{original}': lower bound exceeds upper bound");
                for (var v = rMin; v <= rMax; v++)
                    bits |= 1UL << v;
            }
            else
            {
                var v = int.Parse(trimmed);
                // BUG-144: week field accepts 7 as Sunday (standard cron dialect).
                var isSundaySeven = max == 6 && v == 7;
                if (v < min || (v > max && !isSundaySeven))
                    throw new FormatException($"Value {v} out of range [{min},{max}] in '{original}'");
                bits |= 1UL << (isSundaySeven ? 0 : v);
            }
        }
        return bits;
    }

    private static int ParseStep(string stepText, string original)
    {
        if (!int.TryParse(stepText, out var step) || step < 1)
            throw new FormatException($"Invalid step '{stepText}' in '{original}': must be a positive integer");
        return step;
    }

    /// <summary>Validates a parsed step against the field's max value —
    /// a step larger than the field range would silently yield only the first
    /// value, which is almost certainly a configuration mistake.</summary>
    private static void ValidateStepAgainstMax(int step, int max, string original)
    {
        if (step > max)
            throw new FormatException($"Step {step} exceeds the field maximum {max} in '{original}'");
    }

    private static (int min, int max) ParseRange(string range, int fieldMin, int fieldMax, string original)
    {
        var dash = range.IndexOf('-');
        if (dash < 0)
        {
            // Range-less base ("N/step") — treat as N..fieldMax.
            var single = int.Parse(range);
            if (single < fieldMin || single > fieldMax)
                throw new FormatException($"Value {single} out of range [{fieldMin},{fieldMax}] in '{original}'");
            return (single, fieldMax);
        }
        var rMin = int.Parse(range[..dash]);
        var rMax = int.Parse(range[(dash + 1)..]);
        return (Math.Max(rMin, fieldMin), Math.Min(rMax, fieldMax));
    }

    private static bool HasBit(ulong bits, int index) => (bits & (1UL << index)) != 0;

    /// <inheritdoc />
    public override string ToString() => _original;
}
