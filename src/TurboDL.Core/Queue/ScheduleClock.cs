namespace TurboDL.Core.Queue;

/// <summary>Decides when a scheduled time has been reached, from one periodic check to the next.</summary>
public static class ScheduleClock
{
    /// <summary>
    /// True if <paramref name="at"/> fell in (<paramref name="last"/>, <paramref name="now"/>] on an allowed day.
    /// Comparing against the previous check means a time is never missed (sleep, a slow tick) or fired twice.
    /// Gaps over a day only fire once, for the most recent occurrence.
    /// </summary>
    public static bool Crossed(DateTime last, DateTime now, TimeOnly at, IReadOnlyCollection<DayOfWeek>? days)
    {
        if (now <= last) return false;
        for (var day = now.Date; day >= last.Date; day = day.AddDays(-1))
        {
            var moment = day + at.ToTimeSpan();
            if (moment <= last) return false;
            if (moment <= now && (days is not { Count: > 0 } || days.Contains(day.DayOfWeek))) return true;
        }
        return false;
    }
}
