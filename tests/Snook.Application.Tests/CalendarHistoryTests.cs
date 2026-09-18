using Snook.Domain;
using Snook.UI;
using Snook.Application;
using Snook.Persistence.Sqlite;
using Xunit;

namespace Snook.Application.Tests;

public sealed class CalendarHistoryTests
{
    private static readonly DateTime Day = new(2026, 9, 17);
    private static DateTimeOffset At(double hours) => new DateTimeOffset(Day, TimeSpan.Zero).AddHours(hours);

    [Fact]
    public void IntervalsLeavePausesEmptyAndClampOpenAndFutureTimeAtNow()
    {
        var history = Item([Interval(9, 9.5), Interval(10, null), Interval(14, 15)], SessionState.Running);
        var columns = Build([history], At(10.25));
        var items = Assert.Single(columns).Items;
        Assert.Equal(2, items.Count);
        Assert.Equal(At(9.5), items[0].EndAtUtc);
        Assert.Equal(At(10), items[1].StartAtUtc);
        Assert.Equal(At(10.25), items[1].EndAtUtc);
        Assert.True(items[1].IsRunning);
        Assert.All(items, item => Assert.False(item.CanStart));
        Assert.Equal("45m tracked", columns[0].TrackedLabel);
        var later = Build([history], At(10.5));
        Assert.Equal(At(10.5), later[0].Items[1].EndAtUtc);
    }

    [Fact]
    public void PastPlansAreAbsentAndStraddlingPlansShowOnlyTheirFuturePortion()
    {
        var plans = new[] { Plan(8, 9), Plan(9, 11), Plan(12, 13) };
        var column = Assert.Single(Build([], At(10), plans));
        Assert.Equal(2, column.Items.Count);
        Assert.Equal(At(10), column.Items[0].StartAtUtc);
        Assert.Equal(At(11), column.Items[0].EndAtUtc);
        Assert.All(column.Items, item => { Assert.True(item.IsFuturePlan); Assert.False(item.IsActual); Assert.True(item.CanStart); });
        Assert.Equal("0m tracked", column.TrackedLabel);
        Assert.Empty(Assert.Single(Build([], At(14), plans)).Items);
    }

    [Fact]
    public void OvernightSessionsSplitAtMidnightWithoutLosingTime()
    {
        var columns = CalendarHistoryProjection.Build(Day, Day.AddDays(2), [Item([Interval(23.5, 25)])], [], At(48), TimeZoneInfo.Utc, _ => { });
        Assert.Equal(30, Assert.Single(columns[0].Items).EndAtUtc.Subtract(columns[0].Items[0].StartAtUtc).TotalMinutes);
        Assert.Equal(60, Assert.Single(columns[1].Items).EndAtUtc.Subtract(columns[1].Items[0].StartAtUtc).TotalMinutes);
        Assert.Equal(columns[0].Items[0].EndAtUtc, columns[1].Items[0].StartAtUtc);
    }

    [Fact]
    public void OpenOvernightSessionMarksOnlyThePresentSliceAsRunning()
    {
        var columns = CalendarHistoryProjection.Build(Day, Day.AddDays(2), [Item([Interval(23.5, null)], SessionState.Running)], [], At(25), TimeZoneInfo.Utc, _ => { });
        Assert.False(Assert.Single(columns[0].Items).IsRunning);
        Assert.True(Assert.Single(columns[1].Items).IsRunning);
    }

    [Fact]
    public void ShortSessionsKeepTheirExactLengthAndOverlapsGetSeparateColumns()
    {
        var columns = Build([Item([Interval(9, 9 + 5.0 / 60)]), Item([Interval(9 + 2.0 / 60, 9 + 3.0 / 60)])], At(12));
        var positions = CalendarScheduleLayout.Arrange(columns);
        Assert.Equal(5, positions[0].EndMinute - positions[0].StartMinute, 6);
        Assert.Equal(1, positions[1].EndMinute - positions[1].StartMinute, 6);
        Assert.All(positions, position => Assert.Equal(2, position.ColumnCount));
        Assert.NotEqual(positions[0].Column, positions[1].Column);
        var tiny = CalendarScheduleLayout.Arrange(Build([Item([Interval(9, 9 + 1.0 / 3600)])], At(12)));
        Assert.Equal(1.0 / 60, Assert.Single(tiny).EndMinute - tiny[0].StartMinute, 6);
    }

    [Theory]
    [InlineData(2026, 3, 8, 23)]
    [InlineData(2026, 11, 1, 25)]
    public void ClockChangeDaysUseRealElapsedTime(int year, int month, int day, int hours)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");
        var date = new DateTime(year, month, day);
        var start = new DateTimeOffset(date, zone.GetUtcOffset(date));
        var end = new DateTimeOffset(date.AddDays(1), zone.GetUtcOffset(date.AddDays(1)));
        var history = Item([new TimeInterval(Guid.NewGuid(), start, end, "manual")]);
        var columns = CalendarHistoryProjection.Build(date, date.AddDays(1), [history], [], end, zone, _ => { });
        Assert.Equal(hours * 60, Assert.Single(columns).MinuteCount);
        var placement = Assert.Single(CalendarScheduleLayout.Arrange(columns));
        Assert.Equal(hours * 60, placement.EndMinute - placement.StartMinute);
        // A real hour spanning the gap/fold must always remain a real hour on the grid.
        var crossing = Item([new TimeInterval(Guid.NewGuid(), start.AddHours(1.5), start.AddHours(2.5), "manual")]);
        placement = Assert.Single(CalendarScheduleLayout.Arrange(CalendarHistoryProjection.Build(date, date.AddDays(1), [crossing], [], end, zone, _ => { })));
        Assert.Equal(60, placement.EndMinute - placement.StartMinute);
    }

    [Theory]
    [InlineData(680, 4)]
    [InlineData(992, 6)]
    [InlineData(1148, 7)]
    [InlineData(0, 2)]
    [InlineData(99999, 21)]
    public void FlexUsesBoundedStandardDayWidths(double width, int expected)
        => Assert.Equal(expected, CalendarHistoryProjection.FlexDayCount(width));

    [Fact]
    public void ActivityOnlyBackgroundSessionsRemainVisible()
    {
        var item = Item([Interval(9, 10)]) with { TaskTitle = null, ActivityName = "Listening" };
        item = item with { Session = item.Session with { TaskId = null, Lane = SessionLane.Background } };
        var actual = Assert.Single(Assert.Single(Build([item], At(12))).Items);
        Assert.Equal("Listening", actual.Label);
        Assert.True(actual.IsBackground);
        Assert.Contains("Background", actual.KindLabel);
    }

    [Fact]
    public async Task CalendarLoadsAllHistoryPagesFromTheEmbeddedBackend()
    {
        var directory = Directory.CreateTempSubdirectory("snook-calendar-pages-");
        try
        {
            var backend = new SnookBackend(new SqliteStore(Path.Combine(directory.FullName, "workspace.db")));
            await backend.InitializeAsync();
            var bootstrap = await backend.GetBootstrapAsync();
            var task = await backend.CreateTaskAsync(bootstrap.Projects[0].Id, "Pagination fixture");
            var start = DateTimeOffset.UtcNow.AddMinutes(-10);
            for (var index = 0; index < 205; index++)
                await backend.CreateManualSessionAsync(task.Id, bootstrap.Activities[0].Id, start.AddSeconds(index * 2), start.AddSeconds(index * 2 + 1), "");
            await using var vm = new MainWindowViewModel(backend);
            await vm.InitializeAsync();
            await vm.SelectSectionForScreenshotAsync("Calendar", "history-flex");
            Assert.Equal(205, vm.CalendarDayColumns.SelectMany(day => day.Items).Count(item => item.IsActual));
            Assert.False(vm.HasCalendarHistoryNotice);
        }
        finally { directory.Delete(true); }
    }

    private static IReadOnlyList<CalendarDayColumnViewModel> Build(IEnumerable<HistoryItem> history, DateTimeOffset now,
        IEnumerable<CalendarGridItemViewModel>? plans = null)
        => CalendarHistoryProjection.Build(Day, Day.AddDays(1), history, plans ?? [], now, TimeZoneInfo.Utc, _ => { });

    private static TimeInterval Interval(double start, double? end) => new(Guid.NewGuid(), At(start), end is { } value ? At(value) : null, "manual");
    private static HistoryItem Item(IReadOnlyList<TimeInterval> intervals, SessionState state = SessionState.Stopped)
        => new(new TrackingSession(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), SessionLane.Foreground,
            state, intervals[0].StartedAtUtc, state == SessionState.Stopped ? intervals[^1].EndedAtUtc : null, "", At(0), At(0), 1, intervals),
            "Design", "Studio", "Focus", 0);

    private static CalendarGridItemViewModel Plan(double start, double end)
    {
        var block = new ScheduleBlock(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null,
            At(start), At(end), "UTC", null, null, null, 1);
        return CalendarGridItemViewModel.ForBlock(new CalendarBlockRowViewModel(block, "Planned design", _ => Task.CompletedTask, _ => Task.CompletedTask), _ => { });
    }
}
