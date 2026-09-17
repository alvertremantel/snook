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
    private static async Task CheckWideBoardAsync(MainWindow window, string path)
    {
        var backend = App.ConfiguredBackend!;
        var vm = (MainWindowViewModel)window.DataContext!;
        var scroll = window.FindControl<ScrollViewer>("BoardScroll")!;
        scroll.Offset = default;
        window.UpdateLayout();
        var bounds = new Rect(scroll.TranslatePoint(default, window)!.Value, scroll.Viewport);
        var handle = Visible<Border>(window).First(b => b.Classes.Contains("drag-handle")
            && b.TranslatePoint(new Point(24, 24), window) is { } point && bounds.Contains(point));
        var task = (TaskRowViewModel)handle.DataContext!;
        var lastProject = vm.TaskGroups.Last().ProjectId;
        var origin = handle.TranslatePoint(new Point(24, 24), window)!.Value;
        var edge = new Point(bounds.Right - 12, bounds.Top + 75);
        window.MouseDown(origin, MouseButton.Left);
        window.MouseMove(edge, RawInputModifiers.LeftMouseButton);
        await UntilAsync(() => Task.FromResult(scroll.Offset.X >= scroll.Extent.Width - scroll.Viewport.Width - 60));
        var destinationLane = Visible<Border>(window).First(b => b.Classes.Contains("project-lane")
            && b.DataContext is ProjectTaskGroupViewModel p && p.ProjectId == lastProject);
        var destination = destinationLane.TranslatePoint(new Point(80, 24), window)!.Value;
        window.MouseMove(destination, RawInputModifiers.LeftMouseButton);
        Save(window, Path.Combine(Path.GetDirectoryName(path)!, "tasks-edge-drag.png"));
        window.MouseUp(destination, MouseButton.Left);
        await UntilAsync(async () => (await backend.GetTaskDetailsAsync(task.Task.Id)).Task.ProjectId == lastProject);
        var jump = window.FindControl<ComboBox>("BoardProjectJump")!;
        jump.SelectedItem = vm.TaskGroups[0];
        await UntilAsync(() => Task.FromResult(scroll.Offset.X < 10));
        Console.WriteLine("PASS: holding a dragged card at the edge reaches an offscreen project; the project picker returns to the first lane.");
    }

    private static async Task CheckTaskDrawerAsync(MainWindow window, string path)
    {
        var vm = (MainWindowViewModel)window.DataContext!;
        var backend = App.ConfiguredBackend!;
        var contextBoard = await backend.CreateBoardAsync("Personal context fixture");
        var contextProject = await backend.CreateProjectAsync(contextBoard.Id, "Studio refresh");
        await backend.CreateTaskAsync(contextProject.Id, "Review typography and color samples");
        await UntilAsync(() => Task.FromResult(vm.Projects.Any(project => project.Id == contextProject.Id)));
        await vm.SelectSectionForScreenshotAsync("Tasks", "details");
        window.UpdateLayout();
        var editor = vm.TaskEditor!;
        var title = window.FindControl<TextBox>("TaskEditorTitle")!;
        var saveButton = Visible<Button>(window).Single(button => AutomationProperties.GetName(button) == "Save task editor");
        var archive = Visible<Button>(window).Single(button => AutomationProperties.GetName(button) == "Archive or restore task");
        var tags = Visible<Expander>(window).Single(expander => Equals(expander.Header, "Tags, dependencies & references"));
        var moveSection = Visible<Expander>(window).Single(expander => Equals(expander.Header, "Move to another project"));
        if (archive.TranslatePoint(default, window)!.Value.Y >= saveButton.TranslatePoint(default, window)!.Value.Y
            || tags.TranslatePoint(default, window)!.Value.Y >= saveButton.TranslatePoint(default, window)!.Value.Y
            || saveButton.TranslatePoint(default, window)!.Value.Y >= moveSection.TranslatePoint(default, window)!.Value.Y)
            throw new InvalidOperationException("The task editor action order is incorrect.");
        title.Focus();
        for (var tab = 0; tab < 20; tab++)
        {
            window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
            window.KeyRelease(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
            if (window.FocusManager?.GetFocusedElement() is not Control focused
                || !focused.GetVisualAncestors().OfType<Border>().Any(b => b.Name == "TaskDrawer"))
                throw new InvalidOperationException("Keyboard focus escaped the task drawer.");
        }
        title.Text = editor.Title + " — ready to build";
        var draftTitle = title.Text;
        await backend.CreateActivityAsync("Drawer refresh check");
        await UntilAsync(() => Task.FromResult(vm.Activities.Any(a => a.Name == "Drawer refresh check")));
        if (!ReferenceEquals(editor, vm.TaskEditor) || title.Text != draftTitle)
            throw new InvalidOperationException("A backend notification replaced an unsaved task draft.");
        var moveTarget = vm.Projects.First(p => p.Id != editor.Task.ProjectId).Id;
        Visible<Expander>(window).Single(e => Equals(e.Header, "Move to another project")).IsExpanded = true;
        window.UpdateLayout();
        var movePicker = Visible<ComboBox>(window).Single(c => AutomationProperties.GetName(c) == "Move task to project");
        movePicker.BringIntoView();
        window.UpdateLayout();
        movePicker.IsDropDownOpen = true;
        await Task.Delay(100);
        if (editor.MoveOptions.Count(option => option.IsHeader && option.Name.Length > 0) < 2
            || movePicker.GetRealizedContainers().OfType<ComboBoxItem>().Any(item => item.DataContext is TaskPickerEntry { IsHeader: true } && item.IsEnabled))
            throw new InvalidOperationException("Project picker board headings are missing or selectable.");
        Save(window, Path.Combine(Path.GetDirectoryName(path)!, "task-project-picker.png"));
        movePicker.IsDropDownOpen = false;
        movePicker.SelectedValue = moveTarget;
        var move = Visible<Button>(window).Single(b => Equals(b.Content, "Move to project"));
        move.BringIntoView();
        window.UpdateLayout();
        Click(window, move);
        await UntilAsync(async () => (await backend.GetTaskDetailsAsync(editor.Task.Id)).Task.ProjectId == moveTarget);
        if (title.Text != draftTitle) throw new InvalidOperationException("Moving a task discarded its unsaved title.");
        await UntilAsync(() => Task.FromResult(Visible<TextBlock>(window).Any(text => AutomationProperties.GetName(text) == "Current task location"
            && text.Text == editor.CurrentProjectPath)));
        vm.CloseTaskEditorCommand.Execute(null);
        var afterCancel = (await backend.GetTaskDetailsAsync(editor.Task.Id)).Task;
        if (afterCancel.ProjectId != moveTarget || afterCancel.Title == draftTitle)
            throw new InvalidOperationException("Cancel reverted an immediate move or saved unsaved task fields.");
        vm.ShowTaskDetailsCommand.Execute(vm.Tasks.Single(task => task.Task.Id == editor.Task.Id));
        await UntilAsync(() => Task.FromResult(vm.IsTaskEditorOpen));
        editor = vm.TaskEditor!;
        title.Text = draftTitle;
        tags = Visible<Expander>(window).Single(expander => Equals(expander.Header, "Tags, dependencies & references"));
        tags.IsExpanded = true;
        window.UpdateLayout();
        var prerequisite = Visible<ComboBox>(window).Single(combo => AutomationProperties.GetName(combo) == "Prerequisite task");
        prerequisite.BringIntoView();
        window.UpdateLayout();
        prerequisite.IsDropDownOpen = true;
        await Task.Delay(100);
        if (editor.DependencyOptions.Count(option => option.IsHeader && option.Name.Contains(" / ", StringComparison.Ordinal)) < 2
            || prerequisite.GetRealizedContainers().OfType<ComboBoxItem>().Any(item => item.DataContext is TaskPickerEntry { IsHeader: true } && item.IsEnabled))
            throw new InvalidOperationException("Prerequisite picker is missing board/project headings or permits selecting them.");
        Save(window, Path.Combine(Path.GetDirectoryName(path)!, "task-prerequisite-picker.png"));
        prerequisite.IsDropDownOpen = false;
        tags.IsExpanded = false;
        var save = Visible<Button>(window).Single(b => AutomationProperties.GetName(b) == "Save task editor");
        Save(window, Path.Combine(Path.GetDirectoryName(path)!, "tasks-editor-draft.png"));
        save.BringIntoView();
        window.UpdateLayout();
        await Task.Delay(80);
        Save(window, Path.Combine(Path.GetDirectoryName(path)!, "tasks-editor-actions.png"));
        Click(window, save);
        await UntilAsync(async () => (await backend.GetTaskDetailsAsync(editor.Task.Id)).Task.Title == draftTitle && !vm.IsTaskEditorOpen);

        await vm.SelectSectionForScreenshotAsync("Tasks", "details");
        editor = vm.TaskEditor!;
        var original = editor.Task;
        title.Text = "A draft that must not overwrite another edit";
        await backend.UpdateTaskAsync(original.Id,
            new TaskUpdate("Updated in another view", original.Description, original.Priority, original.DueDate, original.DefaultActivityId, original.Starred),
            new OperationRequest(Guid.NewGuid(), Guid.NewGuid(), original.Revision));
        await UntilAsync(() => Task.FromResult(vm.Tasks.Any(t => t.Task.Id == original.Id && t.Title == "Updated in another view")));
        save.BringIntoView();
        window.UpdateLayout();
        Click(window, save);
        await UntilAsync(() => Task.FromResult(vm.StatusMessage.Contains("changed", StringComparison.OrdinalIgnoreCase)));
        if (!vm.IsTaskEditorOpen || title.Text != "A draft that must not overwrite another edit"
            || (await backend.GetTaskDetailsAsync(original.Id)).Task.Title != "Updated in another view")
            throw new InvalidOperationException("Stale task editing overwrote persisted work or discarded the local draft.");
        Save(window, Path.Combine(Path.GetDirectoryName(path)!, "tasks-editor-conflict.png"));
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        await UntilAsync(() => Task.FromResult(!vm.IsTaskEditorOpen));
        await vm.SelectSectionForScreenshotAsync("Tasks", "details");
        var persistedTitle = vm.TaskEditor!.Task.Title;
        title.Text = "Canceled draft must stay discarded";
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        await vm.SelectSectionForScreenshotAsync("Tasks", "details");
        if (title.Text != persistedTitle)
            throw new InvalidOperationException("Reopening a task resurrected a canceled draft.");
        vm.CloseTaskEditorCommand.Execute(null);
        Console.WriteLine("PASS: task action order, grouped pickers, immediate move retained after Cancel, draft survival, persisted save, stale revision rejection, and Escape cancellation.");
    }
}
