using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using Snook.Contracts;
using Snook.Domain;
using Snook.UI;

namespace Snook.Screenshot;

internal static partial class InteractionChecks
{
    public static async Task CheckTaskBatchAsync(MainWindow window, string path)
    {
        if (Path.GetFileName(path) != "tasks-list.png") return;
        var vm = (MainWindowViewModel)window.DataContext!;
        var backend = App.ConfiguredBackend!;
        var output = Path.GetDirectoryName(path)!;
        T Named<T>(string name) where T : Control
        {
            window.UpdateLayout();
            return Visible<T>(window).Single(control => AutomationProperties.GetName(control) == name);
        }
        async Task PressAsync(Control control)
        {
            var name = AutomationProperties.GetName(control);
            control.BringIntoView();
            await Task.Delay(50);
            if (control.TranslatePoint(default, window) is null && !string.IsNullOrWhiteSpace(name))
            {
                window.UpdateLayout();
                control = Visible<Control>(window).Single(candidate => AutomationProperties.GetName(candidate) == name);
                control.BringIntoView();
                window.UpdateLayout();
            }
            Click(window, control);
            await Task.Delay(50);
        }
        void Escape()
        {
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        }
        var initial = await backend.GetBootstrapAsync();
        var project = await backend.CreateProjectAsync(initial.Boards[0].Id, "Batch review project");
        var a = await backend.CreateTaskAsync(project.Id, "Batch UI first task", Priority.Low);
        var b = await backend.CreateTaskAsync(project.Id, "Batch UI second task", Priority.Urgent);
        var otherProject = await backend.CreateProjectAsync(initial.Boards[0].Id, "Other batch work");
        await backend.CreateTaskAsync(otherProject.Id, "Batch UI outside favorite project");
        vm.SearchText = "Batch UI";
        await UntilAsync(() => Task.FromResult(vm.WorkspaceTasks.Count() == 3 && vm.WorkspaceTasks.All(row => row.Title.StartsWith("Batch UI", StringComparison.Ordinal))
            && Visible<CheckBox>(window).Any(control => AutomationProperties.GetName(control) == $"Select task {a.Title}")));
        await Task.Delay(200);
        await PressAsync(Named<CheckBox>($"Select task {a.Title}"));
        await PressAsync(Named<CheckBox>($"Select task {b.Title}"));
        if (vm.TaskSelectionLabel != "2 selected") throw new InvalidOperationException("Checkboxes did not select exactly two tasks.");
        await backend.CreateActivityGroupAsync("Batch selection refresh");
        await UntilAsync(() => Task.FromResult(vm.ActivityGroups.Any(group => group.Name == "Batch selection refresh")));
        if (vm.WorkspaceTasks.Count(row => row.IsSelected) != 2) throw new InvalidOperationException("Refresh lost selection.");
        await PressAsync(Named<Button>("Edit selected tasks"));
        await UntilAsync(() => Task.FromResult(vm.UtilityDraft is BulkTaskEditorViewModel));
        var draft = (BulkTaskEditorViewModel)vm.UtilityDraft!;
        await PressAsync(Visible<CheckBox>(window).Single(control => Equals(control.Content, "Change priority")));
        Named<ComboBox>("Bulk task priority").SelectedValue = Priority.High;
        await PressAsync(Visible<CheckBox>(window).Single(control => Equals(control.Content, "Change due date")));
        Named<TextBox>("Bulk task due date").Text = DateTime.Today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        for (var tab = 0; tab < 24; tab++)
        {
            window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
            window.KeyRelease(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
            if (window.FocusManager?.GetFocusedElement() is not Control focused || !focused.GetVisualAncestors().Any(parent => parent is Border { Name: "UtilityDrawer" }))
                throw new InvalidOperationException("Focus escaped the bulk editor.");
        }
        await backend.UpdateTaskAsync(b.Id, new TaskUpdate(b.Title, "External description", b.Priority, null, null, false), new OperationRequest(Guid.NewGuid(), Guid.NewGuid(), b.Revision));
        await UntilAsync(() => Task.FromResult(vm.Tasks.Any(row => row.Task.Id == b.Id && row.Task.Revision > b.Revision)));
        if (!ReferenceEquals(draft, vm.UtilityDraft) || !draft.ChangeDueDate || draft.Priority != Priority.High)
            throw new InvalidOperationException("Refresh overwrote the bulk draft.");
        await PressAsync(Named<Button>("Save workspace editor"));
        await UntilAsync(() => Task.FromResult(vm.StatusMessage.Contains("No tasks were changed", StringComparison.Ordinal)));
        if ((await backend.GetTaskDetailsAsync(a.Id)).Task.Priority != Priority.Low || !vm.IsUtilityEditorOpen)
            throw new InvalidOperationException("A stale batch partially saved or discarded its draft.");
        window.FindControl<Border>("UtilityDrawer")!.GetVisualDescendants().OfType<ScrollViewer>().First().Offset = default;
        Save(window, Path.Combine(output, "tasks-bulk-conflict.png"));
        var review = Visible<Expander>(window).Single(expander => Equals(expander.Header, "Review selected tasks"));
        review.IsExpanded = true;
        window.UpdateLayout();
        await Task.Delay(100);
        await PressAsync(Named<Button>("Review latest selected tasks"));
        await UntilAsync(() => Task.FromResult(vm.StatusMessage.StartsWith("Latest task revisions loaded", StringComparison.Ordinal)));
        await PressAsync(Named<Button>("Save workspace editor"));
        await UntilAsync(() => Task.FromResult(!vm.IsUtilityEditorOpen));
        foreach (var id in new[] { a.Id, b.Id })
        {
            var saved = (await backend.GetTaskDetailsAsync(id)).Task;
            if (saved.Priority != Priority.High || saved.DueDate != DateOnly.FromDateTime(DateTime.Today))
                throw new InvalidOperationException("The bulk editor did not persist priority and due date.");
        }
        if ((await backend.GetTaskDetailsAsync(b.Id)).Task.Description != "External description") throw new InvalidOperationException("An unchecked field was overwritten.");
        await UntilAsync(() => Task.FromResult(!vm.HasSelectedTasks));
        await PressAsync(Named<Button>("Select all tasks in view"));
        if (vm.TaskSelectionLabel != "3 selected") throw new InvalidOperationException("Select all did not respect the current search.");
        await PressAsync(Named<Button>("Edit selected tasks"));
        await UntilAsync(() => Task.FromResult(vm.UtilityDraft is BulkTaskEditorViewModel));
        ((BulkTaskEditorViewModel)vm.UtilityDraft!).Title = "Abandoned draft";
        Escape();
        await UntilAsync(() => Task.FromResult(!vm.IsUtilityEditorOpen));
        await PressAsync(Named<Button>("Edit selected tasks"));
        if (((BulkTaskEditorViewModel)vm.UtilityDraft!).Title != "") throw new InvalidOperationException("Cancel retained the abandoned bulk draft.");
        Save(window, Path.Combine(output, "tasks-bulk-editor.png"));
        var bulkScroll = window.FindControl<Border>("UtilityDrawer")!.GetVisualDescendants().OfType<ScrollViewer>().First();
        bulkScroll.ScrollToEnd();
        await Task.Delay(50);
        Save(window, Path.Combine(output, "tasks-bulk-bottom.png"));
        Escape();
        vm.ClearTaskSelectionCommand.Execute(null);

        await PressAsync(Named<Button>($"Star task {a.Title}"));
        await UntilAsync(() => Task.FromResult(vm.Tasks.Any(row => row.Task.Id == a.Id && row.Task.Starred)));
        await vm.SelectSectionForScreenshotAsync("Tasks", "board");
        await Task.Delay(100);
        window.UpdateLayout();
        var lane = Visible<Border>(window).Single(border => border.Classes.Contains("project-lane") && border.DataContext is ProjectTaskGroupViewModel group && group.ProjectId == project.Id);
        lane.BringIntoView();
        await PressAsync(Named<Button>($"Star project {project.Name}"));
        await UntilAsync(() => Task.FromResult(vm.ProjectAdminRows.Any(row => row.Project.Id == project.Id && row.Project.Starred)));
        await vm.SelectSectionForScreenshotAsync("Tasks", "starred");
        await Task.Delay(100);
        window.UpdateLayout();
        if (vm.WorkspaceTasks.Count() != 2)
        {
            Save(window, Path.Combine(output, "tasks-starred-failure.png"));
            throw new InvalidOperationException($"Starred scope mismatch. Search={vm.SearchText}, starred={vm.ShowStarredTasks}, tasks={string.Join(',', vm.WorkspaceTasks.Select(row => row.Title))}");
        }
        Save(window, Path.Combine(output, "tasks-starred-verified.png"));
        await PressAsync(Named<Button>($"Unstar project {project.Name}"));
        await UntilAsync(() => Task.FromResult(vm.WorkspaceTasks.Count() == 1));
        if (vm.WorkspaceTasks.Single().Task.Id != a.Id) throw new InvalidOperationException("Individually starred task disappeared with its unstarred project.");
        vm.SearchText = "";

        await vm.SelectSectionForScreenshotAsync("Calendar", "day");
        await Task.Delay(100);
        window.UpdateLayout();
        await UntilAsync(() => Task.FromResult(vm.CalendarDayColumns.Count == 1 && vm.CalendarDayColumns[0].DueTasks.Any(task => task.Title == a.Title)));
        var day = vm.CalendarDayColumns.Single();
        if (day.Items.Any(item => item.Label == a.Title || item.Label == b.Title)) throw new InvalidOperationException("Unscheduled due tasks appeared as calendar blocks.");
        var dueButton = Named<Button>(day.DueAutomationLabel);
        await PressAsync(dueButton);
        var flyout = (Flyout)dueButton.Flyout!;
        await UntilAsync(() => Task.FromResult(flyout.IsOpen));
        Save(window, Path.Combine(output, "calendar-due-popup.png"));
        var dueEntry = ((Control)flyout.Content!).GetVisualDescendants().OfType<Button>().Single(button => AutomationProperties.GetName(button) == $"Open due task {a.Title}");
        await PressAsync(dueEntry);
        await UntilAsync(() => Task.FromResult(vm.TaskEditor?.Task.Id == a.Id));
        if (flyout.IsOpen) throw new InvalidOperationException("Due popup stayed open over the task drawer.");
        Escape();
        await vm.SelectSectionForScreenshotAsync("Calendar", "month");
        if (!vm.CalendarDayColumns.Any(column => column.DueTasks.Any(task => task.Title == a.Title))) throw new InvalidOperationException("Month lost due-date indicators.");
        window.UpdateLayout();
        await Task.Delay(100);
        var dueDay = Visible<CalendarDueIndicator>(window).First(indicator => indicator.DataContext is CalendarDayColumnViewModel column && column.Date == DateTime.Today);
        dueDay.BringIntoView();
        await Task.Delay(50);
        var cell = dueDay.GetVisualAncestors().OfType<Border>().First();
        var eventsScroll = cell.GetVisualDescendants().OfType<ScrollViewer>().First();
        if (eventsScroll.TranslatePoint(new Point(0, eventsScroll.Bounds.Height), cell)!.Value.Y > cell.Bounds.Height)
            throw new InvalidOperationException("Due-date indicators pushed calendar events outside their month cell.");
        Save(window, Path.Combine(output, "calendar-month-due.png"));
        Console.WriteLine("PASS: task selection, atomic bulk edits, stale draft recovery, cancel/focus, task/project favorites, starred scope, and due-date popups persisted correctly.");
    }
}
