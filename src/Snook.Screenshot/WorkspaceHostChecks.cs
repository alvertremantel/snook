using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using Snook.UI;

namespace Snook.Screenshot;

internal static partial class InteractionChecks
{
    public static async Task CheckWorkspaceHostAsync(MainWindow window, string path)
    {
        if (Path.GetFileName(path) == "tasks-board.png")
        {
            var boardWorkspace = window.Workspace;
            var oldWidth = boardWorkspace.Width;
            var picker = boardWorkspace.FindControl<ComboBox>("BoardProjectJump")!;
            var scroll = boardWorkspace.FindControl<ScrollViewer>("BoardScroll")!;
            var previous = boardWorkspace.FindControl<Button>("BoardPreviousButton")!;
            var next = boardWorkspace.FindControl<Button>("BoardNextButton")!;
            var oldSelection = picker.SelectedIndex;
            var oldOffset = scroll.Offset;
            var page = boardWorkspace.FindControl<ScrollViewer>("PageScroll")!;
            var oldPageOffset = page.Offset;
            try
            {
                foreach (var width in new[] { 320, 480, 720 })
                {
                    boardWorkspace.Width = width;
                    window.UpdateLayout();
                    picker.BringIntoView();
                    window.UpdateLayout();
                    if (picker.Bounds.Height < 48 || previous.Bounds.Width < 48 || next.Bounds.Height < 48)
                        throw new InvalidOperationException("Board navigation is smaller than its touch targets.");
                    foreach (var lane in boardWorkspace.GetVisualDescendants().OfType<Border>().Where(border => border.Classes.Contains("project-lane")))
                    {
                        if (lane.Bounds.Width > scroll.Viewport.Width + 1)
                            throw new InvalidOperationException($"A compact board lane ({lane.Bounds.Width}) does not fit the pane ({scroll.Viewport.Width}) at {width}.");
                        var laneTitle = lane.GetVisualDescendants().OfType<StackPanel>().First(panel => panel.Classes.Contains("lane-title"));
                        if (Grid.GetColumnSpan(laneTitle) != 4)
                            throw new InvalidOperationException("Compact project names do not have a full row.");
                    }
                    if (picker.ItemCount > 0)
                    {
                        picker.SelectedIndex = 0;
                        window.UpdateLayout();
                        if (previous.IsEnabled || scroll.Offset.X > 1)
                            throw new InvalidOperationException("Board navigation did not reach its first lane.");
                        picker.SelectedIndex = picker.ItemCount - 1;
                        window.UpdateLayout();
                        if (next.IsEnabled || Math.Abs(scroll.Offset.X - Math.Max(0, scroll.Extent.Width - scroll.Viewport.Width)) > 1)
                            throw new InvalidOperationException("Board navigation did not reach its last lane.");
                        if (previous.IsEnabled)
                        {
                            var last = picker.SelectedIndex;
                            Click(window, previous);
                            if (picker.SelectedIndex != last - 1)
                                throw new InvalidOperationException("Previous lane action did not follow the selected project.");
                            picker.SelectedIndex = last;
                        }
                    }
                    Save(window, Path.Combine(Path.GetDirectoryName(path)!, $"tasks-board-width-{width}.png"));
                    scroll.BringIntoView();
                    window.UpdateLayout();
                    Save(window, Path.Combine(Path.GetDirectoryName(path)!, $"tasks-board-lane-width-{width}.png"));
                }
            }
            finally
            {
                boardWorkspace.Width = oldWidth;
                window.UpdateLayout();
                picker.SelectedIndex = oldSelection;
                scroll.Offset = oldOffset;
                page.Offset = oldPageOffset;
                window.UpdateLayout();
            }
            return;
        }
        if (Path.GetFileName(path) is "tracker.png" or "history.png" or "summary.png" or "journals.png" or "habits.png" or "calendar-week.png" or "calendar-month.png" or "calendar-agenda.png")
        {
            var workspace = window.Workspace;
            var originalWidth = workspace.Width;
            try
            {
                foreach (var width in new[] { 480, 720 })
                {
                    workspace.Width = width;
                    await Task.Delay(50);
                    window.UpdateLayout();
                    if (Path.GetFileName(path) is "calendar-week.png" or "calendar-month.png" or "calendar-agenda.png")
                    {
                        if (Grid.GetRow(workspace.FindControl<StackPanel>("CalendarRangeActions")!) != 1
                            || Grid.GetRow(workspace.FindControl<StackPanel>("CalendarInspectorActions")!) != 1)
                            throw new InvalidOperationException("Compact calendar actions did not move below their labels.");
                        var choices = workspace.FindControl<WrapPanel>("CalendarViewChoices")!;
                        if (choices.Children.OfType<Button>().Where(button => button.IsVisible)
                            .Any(button => button.Bounds.Height < 48 || button.Bounds.Right > choices.Bounds.Width + 0.1))
                            throw new InvalidOperationException("Calendar view choice is clipped or smaller than the touch target.");
                        if (Path.GetFileName(path) == "calendar-week.png")
                        {
                            var timeline = workspace.GetVisualDescendants().OfType<CalendarTimeline>().Single();
                            var picker = timeline.GetVisualDescendants().OfType<ComboBox>().Single();
                            var horizontal = timeline.GetVisualDescendants().OfType<ScrollViewer>()
                                .Single(scroll => scroll.HorizontalScrollBarVisibility == Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
                            var previous = timeline.GetVisualDescendants().OfType<Button>()
                                .Single(button => AutomationProperties.GetName(button) == "Earlier calendar day");
                            var next = timeline.GetVisualDescendants().OfType<Button>()
                                .Single(button => AutomationProperties.GetName(button) == "Later calendar day");
                            picker.SelectedIndex = 0;
                            window.UpdateLayout();
                            if (previous.IsEnabled || !next.IsEnabled || horizontal.Offset.X > 0.1)
                                throw new InvalidOperationException("Calendar day navigation did not start at the first day.");
                            picker.SelectedIndex = picker.ItemCount - 1;
                            window.UpdateLayout();
                            if (next.IsEnabled || !previous.IsEnabled || horizontal.Offset.X <= 0
                                || Math.Abs(horizontal.Offset.X - (horizontal.Extent.Width - horizontal.Viewport.Width)) > 1)
                                throw new InvalidOperationException("Last calendar day is not reachable through day navigation.");
                            Save(window, Path.Combine(Path.GetDirectoryName(path)!, $"calendar-last-day-width-{width}.png"));
                            timeline.BringIntoView();
                            window.UpdateLayout();
                            Save(window, Path.Combine(Path.GetDirectoryName(path)!, $"calendar-last-day-grid-width-{width}.png"));
                            picker.SelectedIndex = 0;
                            window.UpdateLayout();
                        }
                        if (Path.GetFileName(path) == "calendar-month.png")
                        {
                            var month = workspace.GetVisualDescendants().OfType<CalendarMonthGrid>().Single();
                            var dayCards = month.GetVisualDescendants().OfType<Border>()
                                .Where(border => AutomationProperties.GetName(border)?.StartsWith("Calendar day ", StringComparison.Ordinal) == true)
                                .ToArray();
                            if (month.Bounds.Width < 600)
                            {
                                var picker = month.GetVisualDescendants().OfType<ComboBox>()
                                    .Single(combo => AutomationProperties.GetName(combo) == "Jump to calendar week");
                                var previous = month.GetVisualDescendants().OfType<Button>()
                                    .Single(button => AutomationProperties.GetName(button) == "Earlier calendar week");
                                var next = month.GetVisualDescendants().OfType<Button>()
                                    .Single(button => AutomationProperties.GetName(button) == "Later calendar week");
                                var selectedWeek = picker.SelectedIndex;
                                if (dayCards.Length != 7 || dayCards.Any(card => card.Bounds.Width > month.Bounds.Width + 1 || card.Bounds.Width < 240))
                                    throw new InvalidOperationException("Compact month did not expose seven full-width day cards.");
                                picker.SelectedIndex = 0;
                                window.UpdateLayout();
                                if (previous.IsEnabled || !next.IsEnabled)
                                    throw new InvalidOperationException("Compact month navigation did not reach its first week.");
                                picker.SelectedIndex = picker.ItemCount - 1;
                                window.UpdateLayout();
                                if (next.IsEnabled || !previous.IsEnabled)
                                    throw new InvalidOperationException("Compact month navigation did not reach its last week.");
                                Save(window, Path.Combine(Path.GetDirectoryName(path)!, $"calendar-month-last-week-width-{width}.png"));
                                picker.SelectedIndex = selectedWeek;
                                if (width == 480)
                                {
                                    month.Width = 620;
                                    await Task.Delay(50);
                                    window.UpdateLayout();
                                    if (month.GetVisualDescendants().OfType<ComboBox>()
                                        .Any(combo => AutomationProperties.GetName(combo) == "Jump to calendar week"))
                                        throw new InvalidOperationException("Month did not restore its expanded overview across the width threshold.");
                                    month.Width = double.NaN;
                                    await Task.Delay(50);
                                    window.UpdateLayout();
                                    var restored = month.GetVisualDescendants().OfType<ComboBox>()
                                        .Single(combo => AutomationProperties.GetName(combo) == "Jump to calendar week");
                                    if (restored.SelectedIndex != selectedWeek)
                                        throw new InvalidOperationException("Compact month lost its selected week across the width threshold.");
                                }
                            }
                            else if (dayCards.Length != ((MainWindowViewModel)window.DataContext!).CalendarDayColumns.Count
                                || month.GetVisualDescendants().OfType<ComboBox>().Any(combo => AutomationProperties.GetName(combo) == "Jump to calendar week"))
                                throw new InvalidOperationException("Expanded month did not restore its complete seven-column overview.");
                            month.BringIntoView();
                            window.UpdateLayout();
                        }
                    }
                    if (Path.GetFileName(path) == "habits.png")
                    {
                        var habits = workspace.GetVisualDescendants().OfType<HabitsView>().Single();
                        var date = habits.FindControl<TextBox>("HabitThroughField")!;
                        var oldDate = date.Text;
                        date.Text = "2026-09-01";
                        workspace.Width = width + 1;
                        window.UpdateLayout();
                        workspace.Width = width;
                        window.UpdateLayout();
                        if (date.Text != "2026-09-01" || Grid.GetRow(habits.FindControl<Button>("HabitViewDateButton")!) != 1
                            || date.Bounds.Width > habits.Bounds.Width)
                            throw new InvalidOperationException("Narrow habit date controls lost draft text or exceeded the pane.");
                        if (habits.GetVisualDescendants().OfType<Button>().Where(button => button.Classes.Contains("habit-day") && button.Bounds.Height > 0)
                            .Any(button => button.Bounds.Width < 48 || button.Bounds.Height < 48))
                            throw new InvalidOperationException("Habit day controls are smaller than the touch target.");
                        date.Text = oldDate;
                    }
                    if (Path.GetFileName(path) == "journals.png")
                    {
                        var journals = workspace.GetVisualDescendants().OfType<JournalsView>().Single();
                        var search = journals.FindControl<TextBox>("JournalSearchField")!;
                        var tag = journals.FindControl<TextBox>("JournalTagField")!;
                        var oldSearch = search.Text;
                        search.Text = "Unapplied journal search survives resize";
                        workspace.Width = width + 1;
                        window.UpdateLayout();
                        workspace.Width = width;
                        window.UpdateLayout();
                        if (search.Text != "Unapplied journal search survives resize" || Grid.GetRow(tag) != 1
                            || Grid.GetColumnSpan(search) != 3 || journals.FindControl<ComboBox>("JournalPicker")!.Bounds.Width > journals.Bounds.Width)
                            throw new InvalidOperationException("Narrow journal controls lost draft text or exceeded the pane.");
                        search.Text = oldSearch;
                    }
                    foreach (var row in workspace.GetVisualDescendants().OfType<Grid>().Where(grid => Path.GetFileName(path) == "history.png" && grid.Classes.Contains("history-row")))
                    {
                        var edit = row.Children.OfType<Button>().Single();
                        if (Grid.GetRow(edit) != 1 || edit.Bounds.Height < 48 || row.ColumnDefinitions[3].Width.Value != 0)
                            throw new InvalidOperationException("Narrow history row did not expose its second-row touch action.");
                    }
                    foreach (var metric in workspace.GetVisualDescendants().OfType<StackPanel>().Where(panel => Path.GetFileName(path) == "summary.png" && (panel.Classes.Contains("summary-attributed") || panel.Classes.Contains("summary-coverage"))))
                    {
                        if (Grid.GetRow(metric) != 1 || !metric.Children.OfType<TextBlock>().First().IsVisible)
                            throw new InvalidOperationException("Narrow summary lost its labeled metrics.");
                    }
                    Save(window, Path.Combine(Path.GetDirectoryName(path)!, $"{Path.GetFileNameWithoutExtension(path)}-width-{width}.png"));
                    if (Path.GetFileName(path) == "journals.png")
                    {
                        var firstEntry = workspace.GetVisualDescendants().OfType<Border>().FirstOrDefault(border => border.Classes.Contains("journal-entry"));
                        if (firstEntry is not null)
                        {
                            firstEntry.BringIntoView();
                            window.UpdateLayout();
                            Save(window, Path.Combine(Path.GetDirectoryName(path)!, $"journals-entry-width-{width}.png"));
                        }
                    }
                    if (Path.GetFileName(path) == "habits.png")
                    {
                        var firstHabit = workspace.GetVisualDescendants().OfType<Border>().FirstOrDefault(border => border.Classes.Contains("habit-row"));
                        if (firstHabit is not null)
                        {
                            firstHabit.BringIntoView();
                            window.UpdateLayout();
                            Save(window, Path.Combine(Path.GetDirectoryName(path)!, $"habits-days-width-{width}.png"));
                        }
                    }
                }
            }
            finally { workspace.Width = originalWidth; window.UpdateLayout(); }
            if (Path.GetFileName(path) == "journals.png")
            {
                var journals = workspace.GetVisualDescendants().OfType<JournalsView>().Single();
                if (Grid.GetRow(journals.FindControl<TextBox>("JournalTagField")!) != 0)
                    throw new InvalidOperationException("Expanded journal filters did not return to one row.");
            }
            if (workspace.GetVisualDescendants().OfType<StackPanel>()
                .Where(panel => panel.Classes.Contains("summary-attributed") || panel.Classes.Contains("history-start"))
                .Any(panel => Grid.GetRow(panel) != 0))
                throw new InvalidOperationException("Expanded report columns did not return after resize.");
            return;
        }
        if (Path.GetFileName(path) != "tasks-list.png") return;
        var model = (MainWindowViewModel)window.DataContext!;
        var original = window.Content;
        await model.SelectSectionForScreenshotAsync("Tasks", "details");
        window.UpdateLayout();
        var draft = model.TaskEditor ?? throw new InvalidOperationException("Host fixture needs a task.");
        var title = window.Workspace.FindControl<TextBox>("TaskEditorTitle")!;
        const string unsaved = "Draft survives workspace view recreation";
        title.Text = unsaved;
        await UntilAsync(() => Task.FromResult(draft.DraftTitle == unsaved));
        try
        {
            // This is view/activity-style replacement, not a second session/backend.
            var replacement = new WorkspaceView(model);
            window.Content = replacement;
            window.UpdateLayout();
            var restoredTitle = replacement.FindControl<TextBox>("TaskEditorTitle")!;
            await UntilAsync(() => Task.FromResult(restoredTitle.Text == unsaved));
            if (!ReferenceEquals(draft, model.TaskEditor))
                throw new InvalidOperationException("View recreation replaced the owned editor draft.");
            await model.SetPresentationActiveAsync(false);
            await App.ConfiguredBackend!.CreateActivityAsync("Recreated view refresh fixture");
            if (model.Activities.Any(activity => activity.Name == "Recreated view refresh fixture"))
                throw new InvalidOperationException("Hidden projection processed a backend change.");
            await model.SetPresentationActiveAsync(true);
            await UntilAsync(() => Task.FromResult(model.Activities.Any(activity => activity.Name == "Recreated view refresh fixture")));
            if (!ReferenceEquals(draft, model.TaskEditor) || restoredTitle.Text != unsaved)
                throw new InvalidOperationException("Refresh after view recreation replaced the draft.");
            restoredTitle.Focus();
            Save(window, Path.Combine(Path.GetDirectoryName(path)!, "workspace-recreated.png"));
            replacement.Width = 480;
            window.UpdateLayout();
            if (!replacement.FindControl<Border>("CompactNavigation")!.IsVisible ||
                replacement.FindControl<Border>("WorkspaceSidebar")!.IsVisible ||
                replacement.FindControl<Border>("TaskDrawer")!.Bounds.Width > 480 ||
                !ReferenceEquals(draft, model.TaskEditor) || restoredTitle.Text != unsaved)
                throw new InvalidOperationException("Compact resize lost the draft or did not adapt the editor/navigation.");
            Save(window, Path.Combine(Path.GetDirectoryName(path)!, "workspace-compact-editor.png"));
            replacement.Width = double.NaN;
            window.UpdateLayout();
            if (replacement.FindControl<Border>("CompactNavigation")!.IsVisible || restoredTitle.Text != unsaved)
                throw new InvalidOperationException("Expanded resize did not restore navigation and preserve the draft.");
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            await UntilAsync(() => Task.FromResult(!model.IsTaskEditorOpen));
            var persisted = await App.ConfiguredBackend.GetTaskDetailsAsync(draft.Task.Id);
            if (persisted.Task.Title == unsaved)
                throw new InvalidOperationException("View recreation or Escape persisted an unsaved draft.");
            await model.SelectSectionForScreenshotAsync("Today", null);
            replacement.Width = 480;
            window.UpdateLayout();
            var panes = replacement.FindControl<Grid>("TodayPrimaryLayout")!;
            if (panes.ColumnDefinitions.Count != 1 || Grid.GetRow(panes.Children[1]) != 1)
                throw new InvalidOperationException("Compact Today retained side-by-side panes.");
            if (Grid.GetRow(replacement.FindControl<Border>("TodayNextCard")!) != 2 ||
                Grid.GetRow(replacement.FindControl<Border>("TodayAgendaCard")!) != 3)
                throw new InvalidOperationException("Compact Today put the agenda ahead of next actions.");
            Save(window, Path.Combine(Path.GetDirectoryName(path)!, "workspace-compact-today.png"));
            var nextCard = replacement.FindControl<Border>("TodayNextCard")!;
            var nextRows = nextCard.GetVisualDescendants().OfType<Grid>()
                .Where(grid => grid.Classes.Contains("today-task-row")).ToArray();
            if (nextRows.Length == 0)
                throw new InvalidOperationException("Compact next-task fixture has no rows.");
            if (nextCard.GetVisualDescendants().OfType<StackPanel>()
                .Where(panel => panel.Classes.Contains("today-task-actions")).Any(panel => Grid.GetRow(panel) != 1))
                throw new InvalidOperationException("Compact next-task actions did not move below the text.");
            nextCard.BringIntoView();
            window.UpdateLayout();
            Save(window, Path.Combine(Path.GetDirectoryName(path)!, "workspace-compact-next.png"));
            await model.SelectSectionForScreenshotAsync("Tasks", "list");
            window.UpdateLayout();
            var captureTitle = replacement.FindControl<TextBox>("TaskCaptureTitle")!;
            const string captureDraft = "Compact task capture survives resize";
            captureTitle.Text = captureDraft;
            if (Grid.GetRow(replacement.FindControl<ComboBox>("TaskCaptureProject")!) != 1 ||
                Grid.GetRow(replacement.FindControl<Button>("BoardCreationButton")!) != 1)
                throw new InvalidOperationException("Compact Tasks capture/toolbar did not stack.");
            var taskActions = replacement.GetVisualDescendants().OfType<StackPanel>()
                .Where(panel => panel.Classes.Contains("task-row-actions")).ToArray();
            if (taskActions.Length == 0 || taskActions.Any(panel => Grid.GetRow(panel) != 1))
                throw new InvalidOperationException("Compact task actions did not move below their text.");
            Save(window, Path.Combine(Path.GetDirectoryName(path)!, "workspace-compact-tasks.png"));
            taskActions[0].BringIntoView();
            window.UpdateLayout();
            Save(window, Path.Combine(Path.GetDirectoryName(path)!, "workspace-compact-task-actions.png"));
            replacement.Width = 720;
            window.UpdateLayout();
            var sidebar = replacement.FindControl<Border>("WorkspaceSidebar")!;
            if (!sidebar.IsVisible || Math.Abs(sidebar.Bounds.Width - 88) > 0.1 ||
                replacement.FindControl<Border>("CompactNavigation")!.IsVisible || captureTitle.Text != captureDraft)
                throw new InvalidOperationException("Medium navigation rail did not preserve the capture and reclaim sidebar width.");
            var railItems = sidebar.GetVisualDescendants().OfType<StackPanel>()
                .Where(panel => panel.Classes.Contains("rail-item")).ToArray();
            if (railItems.Length != 9 || railItems.Any(panel => panel.Orientation != Avalonia.Layout.Orientation.Vertical))
                throw new InvalidOperationException("Medium navigation did not expose nine labeled vertical rail items.");
            replacement.FindControl<ScrollViewer>("PageScroll")!.Offset = default;
            window.UpdateLayout();
            Save(window, Path.Combine(Path.GetDirectoryName(path)!, "workspace-medium-tasks.png"));
            var settingsButton = sidebar.GetVisualDescendants().OfType<Button>()
                .Single(button => button.CommandParameter is "Settings");
            settingsButton.BringIntoView();
            window.UpdateLayout();
            var railScroll = sidebar.GetVisualDescendants().OfType<ScrollViewer>().First();
            var settingsPosition = settingsButton.TranslatePoint(default, railScroll);
            if (settingsPosition is not { } point || point.Y < -0.1 ||
                point.Y + settingsButton.Bounds.Height > railScroll.Bounds.Height + 0.1)
                throw new InvalidOperationException("The last rail destination is not scroll-reachable.");
            Save(window, Path.Combine(Path.GetDirectoryName(path)!, "workspace-medium-rail-end.png"));
            foreach (var width in new[] { 599, 600, 839, 840 })
            {
                replacement.Width = width;
                window.UpdateLayout();
                var expectedSidebar = width < 600 ? 0 : width < 840 ? 88 : 196;
                if (Math.Abs(replacement.FindControl<Grid>("WorkspaceLayout")!.ColumnDefinitions[0].ActualWidth - expectedSidebar) > 0.1 ||
                    captureTitle.Text != captureDraft)
                    throw new InvalidOperationException("Navigation width-class boundary lost capture or chose the wrong layout.");
            }
            replacement.Width = double.NaN;
            window.UpdateLayout();
            if (captureTitle.Text != captureDraft || model.TaskTitle != captureDraft ||
                Grid.GetRow(replacement.FindControl<ComboBox>("TaskCaptureProject")!) != 0 ||
                taskActions.Any(panel => Grid.GetRow(panel) != 0))
                throw new InvalidOperationException("Expanded Tasks did not restore layout and retain capture.");
            model.TaskTitle = string.Empty;
            await model.SelectSectionForScreenshotAsync("Time Tracker", null);
            replacement.Width = 480;
            window.UpdateLayout();
            var trackerActivities = replacement.FindControl<ScrollViewer>("TrackerActivitiesScroll")!;
            var trackerSessions = replacement.FindControl<ScrollViewer>("TrackerSessionsScroll")!;
            if (Grid.GetRow(trackerActivities) != 1 || Grid.GetColumn(trackerSessions) != 0
                || !double.IsPositiveInfinity(trackerActivities.MaxHeight)
                || Grid.GetRow(replacement.FindControl<Button>("TrackerManualButton")!) != 1)
                throw new InvalidOperationException("Compact Tracker did not stack sessions before activities and wrap its toolbar.");
            Save(window, Path.Combine(Path.GetDirectoryName(path)!, "workspace-compact-tracker.png"));
            trackerActivities.BringIntoView();
            window.UpdateLayout();
            Save(window, Path.Combine(Path.GetDirectoryName(path)!, "workspace-compact-tracker-activities.png"));
            await ((AsyncCommand)model.OpenUtilityEditorCommand).ExecuteAsync("manual");
            model.ManualNotes = "Manual timer draft survives narrow resize";
            replacement.Width = 720;
            window.UpdateLayout();
            if (model.ManualNotes != "Manual timer draft survives narrow resize")
                throw new InvalidOperationException("Tracker resize changed its manual-time draft.");
            Save(window, Path.Combine(Path.GetDirectoryName(path)!, "workspace-medium-tracker-editor.png"));
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            replacement.Width = double.NaN;
            window.UpdateLayout();
            if (Grid.GetRow(trackerActivities) != 0 || Grid.GetColumn(trackerSessions) != 1
                || double.IsPositiveInfinity(trackerActivities.MaxHeight))
                throw new InvalidOperationException("Expanded Tracker did not restore independent columns.");
            Console.WriteLine("PASS: workspace view recreation retains the session and draft through refresh; Escape discards without saving.");
        }
        finally
        {
            window.Content = original;
            await model.SelectSectionForScreenshotAsync("Tasks", "list");
            window.UpdateLayout();
        }
    }
}
