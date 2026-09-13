using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Snook.Contracts;
using Snook.Domain;
using Snook.UI;

namespace Snook.Screenshot;

internal static partial class InteractionChecks
{
    public static async Task RunAsync(MainWindow window, string path)
    {
        var backend = App.ConfiguredBackend!;
        if (Path.GetFileName(path) == "calendar-week.png")
        {
            var vm = (MainWindowViewModel)window.DataContext!;
            var originalRange = vm.CalendarRangeLabel;
            var originalDay = vm.CalendarDayColumns[0].Date;
            Button Named(string name) => Visible<Button>(window).First(b => AutomationProperties.GetName(b) == name);
            Click(window, Named("Next calendar range"));
            await UntilAsync(() => Task.FromResult(vm.CalendarDayColumns.Count == 7 && vm.CalendarDayColumns[0].Date == originalDay.AddDays(7)));
            Click(window, Named("Calendar today"));
            await UntilAsync(() => Task.FromResult(vm.CalendarRangeLabel == originalRange && vm.CalendarDayColumns.Count == 7 && vm.CalendarDayColumns[0].Date == originalDay));
            foreach (var mode in new[] { "day", "month" })
            {
                Click(window, Named($"Show calendar {mode}"));
                await UntilAsync(() => Task.FromResult(mode == "day" ? vm.CalendarDayColumns.Count == 1 : vm.CalendarDayColumns.Count >= 28));
                var before = vm.CalendarAnchor;
                Click(window, Named("Next calendar range"));
                var expected = mode == "day" ? before.AddDays(1) : before.AddMonths(1);
                await UntilAsync(() => Task.FromResult(vm.CalendarAnchor == expected));
                Click(window, Named("Calendar today"));
                await UntilAsync(() => Task.FromResult(vm.CalendarAnchor == DateTime.Today));
            }
            Click(window, Named("Show calendar week"));
            await UntilAsync(() => Task.FromResult(vm.CalendarDayColumns.Count == 7 && vm.CalendarDayColumns[0].Date == originalDay));
            await Task.Delay(60);
            window.UpdateLayout();
            var layout = CalendarScheduleLayout.Arrange(vm.CalendarDayColumns);
            if (layout.Any(p => p.Item.AllDay || p.StartMinute < 0 || p.EndMinute > 1440 || p.EndMinute <= p.StartMinute))
                throw new InvalidOperationException("Calendar placement escaped its day or included an all-day item.");
            foreach (var item in layout)
                if (layout.Any(other => !ReferenceEquals(item, other) && other.DayIndex == item.DayIndex && other.Column == item.Column
                    && other.StartMinute < item.EndMinute && item.StartMinute < other.EndMinute))
                    throw new InvalidOperationException("Overlapping calendar items share a visual column.");
            if (Environment.GetEnvironmentVariable("SNOOK_SCREENSHOT_CALENDAR_STRESS") == "1"
                && (!layout.Any(p => p.ColumnCount > 1) || !vm.CalendarDayColumns.Any(d => d.Items.Any(i => i.AllDay))))
                throw new InvalidOperationException("Calendar stress fixture did not exercise overlaps and all-day items.");
            var planned = Visible<Button>(window).First(b => b.Classes.Contains("calendar-entry") && b.DataContext is CalendarGridItemViewModel i && i.CanStart);
            planned.Focus();
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            await UntilAsync(() => Task.FromResult(vm.HasSelectedCalendarItem));
            Save(window, Path.Combine(Path.GetDirectoryName(path)!, "calendar-inspected.png"));
            var activeBefore = (await backend.GetBootstrapAsync()).Today.ActiveSessions.Select(s => s.Session.Id).ToHashSet();
            Click(window, Visible<Button>(window).Single(b => Equals(b.Content, "Start planned work")));
            await UntilAsync(async () => (await backend.GetBootstrapAsync()).Today.ActiveSessions.Any(s => !activeBefore.Contains(s.Session.Id) && s.Session.TaskId is not null));
            await UntilAsync(() => Task.FromResult(vm.CalendarPlanTasks.Count > 0));
            var taskId = vm.CalendarPlanTasks[0].Id;
            var planStart = new DateTimeOffset(DateTime.Today.AddDays(3).AddHours(9)).ToUniversalTime();
            vm.CalendarPlanTaskId = taskId;
            vm.CalendarPlanStartText = planStart.ToLocalTime().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
            vm.CalendarPlanEndText = vm.CalendarPlanStartText;
            Visible<Expander>(window).Single(e => Equals(e.Header, "Add an event or plan a task")).IsExpanded = true;
            window.UpdateLayout();
            var planButton = Named("Plan selected task");
            planButton.BringIntoView();
            window.UpdateLayout();
            Click(window, planButton);
            await UntilAsync(() => Task.FromResult(vm.StatusMessage.StartsWith("Enter a valid local start", StringComparison.Ordinal)));
            vm.CalendarPlanEndText = planStart.AddHours(1).ToLocalTime().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
            Click(window, planButton);
            await UntilAsync(async () => (await backend.GetCalendarRangeAsync(new CalendarRangeQuery(planStart, planStart.AddHours(1))))
                .Any(b => b.TaskId == taskId && b.StartAtUtc == planStart && b.EndAtUtc == planStart.AddHours(1)));
            await UntilAsync(() => Task.FromResult(vm.CalendarAnchor == planStart.ToLocalTime().Date));
            Console.WriteLine("PASS: calendar navigation, bounded overlap layout, inspection, starting work, invalid-plan validation, and explicit task planning.");
        }
        if (Path.GetFileName(path) == "tasks-board.png")
        {
            var lanes = Visible<Border>(window).Where(b => b.Classes.Contains("project-lane")).ToArray();
            var handle = Visible<Border>(window).First(b => b.Classes.Contains("drag-handle"));
            var task = (TaskRowViewModel)handle.DataContext!;
            var target = lanes.First(b => ((ProjectTaskGroupViewModel)b.DataContext!).ProjectId != task.Task.ProjectId);
            var projectId = ((ProjectTaskGroupViewModel)target.DataContext!).ProjectId;
            var origin = handle.TranslatePoint(new Point(24, 24), window)!.Value;
            var destination = target.TranslatePoint(new Point(80, 24), window)!.Value;
            window.MouseDown(origin, MouseButton.Left);
            window.MouseMove(destination, RawInputModifiers.LeftMouseButton);
            if (!target.Classes.Contains("drop-target")) throw new InvalidOperationException("Board drag did not highlight its target.");
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            window.MouseUp(destination, MouseButton.Left);
            if (target.Classes.Contains("drop-target") || (await backend.SearchTasksAsync()).Single(t => t.Task.Id == task.Task.Id).Task.ProjectId != task.Task.ProjectId)
                throw new InvalidOperationException("Escape did not cancel the drag without changing the task.");
            window.MouseDown(origin, MouseButton.Left);
            window.MouseMove(destination, RawInputModifiers.LeftMouseButton);
            Save(window, Path.Combine(Path.GetDirectoryName(path)!, "tasks-drag.png"));
            window.MouseUp(destination, MouseButton.Left);
            await UntilAsync(async () => (await backend.SearchTasksAsync()).Any(t => t.Task.Id == task.Task.Id && t.Task.ProjectId == projectId));
            await UntilAsync(() => Task.FromResult(Visible<Border>(window).Any(b => b.Classes.Contains("drag-handle") && b.DataContext is TaskRowViewModel t && t.Task.Id == task.Task.Id && t.Task.ProjectId == projectId)));
            if (Visible<Border>(window).Any(b => b.Classes.Contains("drop-target") || b.Classes.Contains("dragging")))
                throw new InvalidOperationException("Drag feedback was not cleared after drop.");
            Save(window, Path.Combine(Path.GetDirectoryName(path)!, "tasks-after-drop.png"));
            Console.WriteLine("PASS: Escape cancels a drag; pointer drop moves the task in SQLite and refreshes its project lane.");
            if (Environment.GetEnvironmentVariable("SNOOK_SCREENSHOT_BOARD_STRESS") == "1")
                await CheckWideBoardAsync(window, path);
            await CheckTaskDrawerAsync(window, path);
        }
        if (Path.GetFileName(path) == "tracker.png")
        {
            var captured = (await backend.GetBootstrapAsync()).Today;
            var sample = captured.ActiveSessions.First(s => s.Session.State == SessionState.Running);
            var midnight = new DateTimeOffset(2026, 9, 13, 0, 0, 0, TimeSpan.Zero);
            var interval = new TimeInterval(Guid.NewGuid(), midnight.AddMinutes(-10), null, "timer");
            var running = sample with { Session = sample.Session with { Intervals = [interval] } };
            var clockSnapshot = captured with { CapturedAtUtc = midnight.AddMinutes(-5), TrackedTodayMilliseconds = 300_000, ActiveSessions = [running] };
            if (LiveTodayClock.Milliseconds(clockSnapshot, midnight.AddMinutes(-1), TimeZoneInfo.Utc) != 540_000
                || LiveTodayClock.Milliseconds(clockSnapshot, midnight.AddMinutes(2), TimeZoneInfo.Utc) != 120_000)
                throw new InvalidOperationException("Live today projection failed to advance or split at midnight.");
            var stoppedInterval = interval with { EndedAtUtc = midnight.AddMinutes(-5) };
            clockSnapshot = clockSnapshot with { ActiveSessions = [running with { Session = running.Session with { Intervals = [stoppedInterval], State = SessionState.Paused } }] };
            if (LiveTodayClock.Milliseconds(clockSnapshot, midnight.AddMinutes(-1), TimeZoneInfo.Utc) != 300_000)
                throw new InvalidOperationException("Paused time advanced the daily total.");
            Console.WriteLine("PASS: live today advances running time, excludes paused time, and splits at midnight.");
            var vm = (MainWindowViewModel)window.DataContext!;
            var projectChoice = vm.Projects.Last().Id;
            var activityChoice = vm.Activities.Last().Id;
            vm.SelectedProjectId = projectChoice;
            vm.SelectedTrackingActivityId = activityChoice;
            var session = ((MainWindowViewModel)window.DataContext!).FocusSessions.First(s => s.Session.State == SessionState.Running);
            Button SessionButton(string label) => Visible<Button>(window).First(b => b.DataContext is ActiveSessionRowViewModel s && s.Session.Id == session.Session.Id && Equals(b.Content, label));
            Click(window, SessionButton("Pause"));
            await UntilAsync(async () => (await backend.GetBootstrapAsync()).Today.ActiveSessions.Any(s => s.Session.Id == session.Session.Id && s.Session.State == SessionState.Paused));
            await UntilAsync(() => Task.FromResult(Visible<Button>(window).Any(b => b.DataContext is ActiveSessionRowViewModel s && s.Session.Id == session.Session.Id && Equals(b.Content, "Resume"))));
            Save(window, Path.Combine(Path.GetDirectoryName(path)!, "tracker-paused.png"));
            Click(window, SessionButton("Resume"));
            await UntilAsync(async () => (await backend.GetBootstrapAsync()).Today.ActiveSessions.Any(s => s.Session.Id == session.Session.Id && s.Session.State == SessionState.Running));
            await UntilAsync(() => Task.FromResult(Visible<Button>(window).Any(b => b.DataContext is ActiveSessionRowViewModel s && s.Session.Id == session.Session.Id && Equals(b.Content, "Pause"))));
            Click(window, SessionButton("Stop"));
            await UntilAsync(async () => (await backend.GetBootstrapAsync()).Today.ActiveSessions.All(s => s.Session.Id != session.Session.Id));
            var activity = Visible<Button>(window).First(b => b.Classes.Contains("activity") && b.DataContext is TrackerActivity a && a.ActionLabel == "Start");
            var name = ((TrackerActivity)activity.DataContext!).Name;
            Click(window, activity);
            await UntilAsync(async () => (await backend.GetBootstrapAsync()).Today.ActiveSessions.Any(s => s.ActivityName == name && s.Session.State == SessionState.Running));
            await Task.Delay(60);
            if (vm.SelectedProjectId != projectChoice || vm.SelectedTrackingActivityId != activityChoice)
                throw new InvalidOperationException("Timer refresh discarded the user's project or activity selection.");
            Console.WriteLine("PASS: pointer controls pause, resume, stop, and start an activity against SQLite.");
        }
    }

    private static IEnumerable<T> Visible<T>(MainWindow window) where T : Control => window.GetVisualDescendants().OfType<T>().Where(c => c.IsEffectivelyVisible);

    private static void Click(MainWindow window, Control control)
    {
        window.UpdateLayout();
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
    }

    private static async Task UntilAsync(Func<Task<bool>> predicate)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (await predicate()) return;
            await Task.Delay(30);
        }
        throw new InvalidOperationException("UI interaction did not reach the expected persisted state within three seconds.");
    }

    private static void Save(MainWindow window, string path)
    {
        window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
        using var frame = window.CaptureRenderedFrame()!;
        frame.Save(path, PngBitmapEncoderOptions.Default);
    }
}
