using Snook.Contracts;
using Snook.Domain;

namespace Snook.UI;

/// <summary>Projects the live display without requesting or mutating backend data each second.</summary>
public static class LiveTodayClock
{
    public static long Milliseconds(TodaySnapshot snapshot, DateTimeOffset now, TimeZoneInfo zone)
    {
        var date = TimeZoneInfo.ConvertTime(now, zone).Date;
        var (start, end) = TimeMath.LocalDayRangeUtc(now, zone);
        var intervals = snapshot.ActiveSessions.SelectMany(session => session.Session.Intervals).ToArray();
        if (TimeZoneInfo.ConvertTime(snapshot.CapturedAtUtc, zone).Date != date)
            return intervals.Sum(interval => TimeMath.OverlapMilliseconds(interval, start, end, now));

        var added = intervals.Where(interval => interval.EndedAtUtc is null).Sum(interval =>
            TimeMath.OverlapMilliseconds(interval, start, end, now)
            - TimeMath.OverlapMilliseconds(interval, start, end, snapshot.CapturedAtUtc));
        return Math.Max(0, snapshot.TrackedTodayMilliseconds + added);
    }
}
