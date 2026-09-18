using Snook.Domain;

namespace Snook.UI;

/// <summary>Projects real intervals, leaving pauses empty and reserving plans for the future.</summary>
public static class CalendarHistoryProjection
{
    public const double DayWidth = 156;
    public static int FlexDayCount(double viewportWidth) => double.IsFinite(viewportWidth)
        ? (int)Math.Clamp(Math.Floor((viewportWidth - 56) / DayWidth), 2, 21) : 5;

    public static IReadOnlyList<CalendarDayColumnViewModel> Build(DateTime start, DateTime end,
        IEnumerable<HistoryItem> history, IEnumerable<CalendarGridItemViewModel> plans, DateTimeOffset now,
        TimeZoneInfo zone, Action<CalendarGridItemViewModel> inspect)
    {
        if (end <= start || (end - start).TotalDays > 21)
            throw new ArgumentOutOfRangeException(nameof(end), "History displays between one and 21 days.");
        var intervals = history.SelectMany(item => item.Session.Intervals.Select(interval => (Item: item, Interval: interval))).ToArray();
        var future = plans.Where(plan => plan.EndAtUtc > now).ToArray();
        var columns = new List<CalendarDayColumnViewModel>();
        for (var date = start.Date; date < end; date = date.AddDays(1))
        {
            var dayStart = MidnightUtc(date, zone);
            var dayEnd = MidnightUtc(date.AddDays(1), zone);
            var items = new List<CalendarGridItemViewModel>();
            foreach (var (item, interval) in intervals)
            {
                var from = Max(interval.StartedAtUtc, dayStart);
                var to = Min(Min(interval.EndedAtUtc ?? now, now), dayEnd);
                if (to <= from) continue;
                var running = interval.EndedAtUtc is null && item.Session.State == SessionState.Running && to == now;
                items.Add(new CalendarGridItemViewModel(item.TaskTitle ?? item.ActivityName ?? "Tracked time", from, to, false, false,
                    string.Join(" · ", new[] { running ? "Running" : "Recorded time", item.Session.Lane == SessionLane.Background ? "Background" : "Foreground",
                        item.ProjectName, item.ActivityName, item.Session.Notes }.Where(value => !string.IsNullOrWhiteSpace(value))),
                    null, inspect, $"interval:{interval.Id}:{date:yyyy-MM-dd}", isActual: true, isRunning: running,
                    isBackground: item.Session.Lane == SessionLane.Background));
            }
            foreach (var plan in future)
            {
                var from = Max(Max(plan.StartAtUtc, now), dayStart);
                var to = Min(plan.EndAtUtc, dayEnd);
                if (to <= from) continue;
                items.Add(new CalendarGridItemViewModel(plan.Label, from, to, false, false,
                    $"Planned, not yet recorded · Original plan: {plan.IntervalLabel}", plan.StartCommand, inspect,
                    $"{plan.Key}:{date:yyyy-MM-dd}", isFuturePlan: true));
            }
            columns.Add(new CalendarDayColumnViewModel(date, false,
                items.OrderBy(item => item.StartAtUtc).ThenBy(item => item.Key, StringComparer.Ordinal).ToArray(), true, zone));
        }
        return columns;
    }

    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right) => left < right ? left : right;
    private static DateTimeOffset Max(DateTimeOffset left, DateTimeOffset right) => left > right ? left : right;
    internal static DateTimeOffset MidnightUtc(DateTime date, TimeZoneInfo zone)
    {
        var local = DateTime.SpecifyKind(date.Date, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
    }
}
