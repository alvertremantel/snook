using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Headless;
using Snook.UI;
using Snook.Contracts;
using Snook.Domain;

namespace Snook.Screenshot;

internal static partial class InteractionChecks
{
    public static async Task CheckCalendarHistoryAsync(MainWindow window, string path)
    {
        if (Path.GetFileName(path) != "calendar-history-flex.png") return;
        var vm = (MainWindowViewModel)window.DataContext!;
        Button Named(string name) => Visible<Button>(window).Single(button => AutomationProperties.GetName(button) == name);
        Click(window, Named("Calendar today"));
        await UntilAsync(() => Task.FromResult(vm.CalendarAnchor == DateTime.Today));
        await Task.Delay(250);
        window.UpdateLayout();
        var timeline = Visible<CalendarTimeline>(window).Single();
        var initialCount = vm.CalendarDayColumns.Count;
        if (!vm.IsCalendarHistoryMode || !vm.IsCalendarFlex || initialCount != CalendarHistoryProjection.FlexDayCount(timeline.Bounds.Width))
            throw new InvalidOperationException("History Flex did not adapt to its viewport.");
        if (Visible<Button>(window).Any(button => AutomationProperties.GetName(button) is "Show calendar day" or "Show calendar month" or "Show calendar agenda"))
            throw new InvalidOperationException("History exposed an unsupported arrangement.");
        if (!vm.CalendarDayColumns.SelectMany(day => day.Items).Any(item => item.IsActual))
            throw new InvalidOperationException("Persisted sessions were missing from calendar History.");
        var cutoff = DateTimeOffset.UtcNow.AddSeconds(-2);
        if (vm.CalendarDayColumns.SelectMany(day => day.Items).Any(item => item.IsEvent || item.IsFuturePlan && item.StartAtUtc < cutoff
            || item.IsActual && item.EndAtUtc > DateTimeOffset.UtcNow))
            throw new InvalidOperationException("History mixed past plans, future recordings, or events into actual time.");

        var actual = Visible<Button>(window).First(button => button.DataContext is CalendarGridItemViewModel { IsActual: true });
        actual.Focus();
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        await UntilAsync(() => Task.FromResult(vm.SelectedCalendarItem?.IsActual == true));
        if (vm.SelectedCalendarItem!.CanStart || Visible<Button>(window).Any(button => Equals(button.Content, "Edit in agenda")))
            throw new InvalidOperationException("Recorded time exposed a planning mutation.");
        Save(window, Path.Combine(Path.GetDirectoryName(path)!, "calendar-history-inspected.png"));
        Click(window, Visible<Button>(window).Single(button => Equals(button.Content, "Close")));

        var first = vm.CalendarDayColumns[0].Date;
        Click(window, Named("Next calendar range"));
        await UntilAsync(() => Task.FromResult(vm.CalendarDayColumns.Count == initialCount && vm.CalendarDayColumns[0].Date == first.AddDays(initialCount)));
        Click(window, Named("Calendar today"));
        await UntilAsync(() => Task.FromResult(vm.CalendarDayColumns[0].Date == first));
        var width = window.Width;
        window.Width = width >= 1200 ? 980 : 1280;
        await UntilAsync(() => Task.FromResult(vm.CalendarDayColumns.Count != initialCount));
        window.UpdateLayout();
        if (vm.CalendarDayColumns.Count != CalendarHistoryProjection.FlexDayCount(timeline.Bounds.Width))
            throw new InvalidOperationException("Flex count did not follow a window resize.");
        window.Width = width;
        await UntilAsync(() => Task.FromResult(vm.CalendarDayColumns.Count == initialCount));

        Click(window, Named("Show calendar week"));
        await UntilAsync(() => Task.FromResult(vm.CalendarDayColumns.Count == 7 && vm.IsCalendarWeek));
        Click(window, Named("Show calendar Event mode"));
        await UntilAsync(() => Task.FromResult(vm.IsCalendarEventMode));
        Click(window, Named("Show calendar month"));
        await UntilAsync(() => Task.FromResult(vm.IsCalendarMonth && vm.CalendarDayColumns.Count >= 28));
        Click(window, Named("Show calendar History mode"));
        await UntilAsync(() => Task.FromResult(vm.IsCalendarHistoryMode && vm.IsCalendarWeek && vm.CalendarDayColumns.Count == 7));
        Click(window, Named("Show calendar Event mode"));
        await UntilAsync(() => Task.FromResult(vm.IsCalendarEventMode && vm.IsCalendarMonth && vm.CalendarDayColumns.Count >= 28));
        Click(window, Named("Show calendar History mode"));
        await UntilAsync(() => Task.FromResult(vm.IsCalendarHistoryMode && vm.CalendarDayColumns.Count == 7));
        Click(window, Named("Show calendar flex"));
        await UntilAsync(() => Task.FromResult(vm.IsCalendarFlex && vm.CalendarDayColumns.Count == initialCount));
        var backend = App.ConfiguredBackend!;
        var running = (await backend.GetBootstrapAsync()).Today.ActiveSessions.First(session => session.Session.State == SessionState.Running).Session;
        var oldInterval = running.Intervals.Single(interval => interval.EndedAtUtc is null);
        var paused = await backend.PauseSessionAsync(running.Id, new OperationRequest(Guid.NewGuid(), Guid.NewGuid(), running.Revision));
        var pausedEnd = paused.Intervals.Single(interval => interval.Id == oldInterval.Id).EndedAtUtc;
        await UntilAsync(() => Task.FromResult(vm.CalendarDayColumns.SelectMany(day => day.Items)
            .Any(item => item.Key.StartsWith($"interval:{oldInterval.Id}:", StringComparison.Ordinal) && !item.IsRunning && item.EndAtUtc == pausedEnd)));
        var resumed = await backend.ResumeSessionAsync(paused.Id, new OperationRequest(Guid.NewGuid(), Guid.NewGuid(), paused.Revision));
        var newInterval = resumed.Intervals.Single(interval => interval.EndedAtUtc is null);
        await UntilAsync(() => Task.FromResult(vm.CalendarDayColumns.SelectMany(day => day.Items)
            .Any(item => item.Key.StartsWith($"interval:{newInterval.Id}:", StringComparison.Ordinal) && item.IsRunning)));
        var stopped = await backend.StopSessionAsync(resumed.Id, null, new OperationRequest(Guid.NewGuid(), Guid.NewGuid(), resumed.Revision));
        var stoppedEnd = stopped.Intervals.Single(interval => interval.Id == newInterval.Id).EndedAtUtc;
        await UntilAsync(() => Task.FromResult(vm.CalendarDayColumns.SelectMany(day => day.Items)
            .Any(item => item.Key.StartsWith($"interval:{newInterval.Id}:", StringComparison.Ordinal) && !item.IsRunning && item.EndAtUtc == stoppedEnd)));
        window.MouseMove(new Avalonia.Point(10, 10));
        await Task.Delay(100);
        Save(window, path);
        Console.WriteLine("PASS: calendar History persisted intervals, time boundary, keyboard inspection, adaptive Flex, navigation, independent arrangements, and pause/resume/stop refresh.");
    }
}
