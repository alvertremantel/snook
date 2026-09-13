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
        await vm.SelectSectionForScreenshotAsync("Tasks", "details");
        window.UpdateLayout();
        var editor = vm.TaskEditor!;
        var title = window.FindControl<TextBox>("TaskEditorTitle")!;
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
        Visible<ComboBox>(window).Single(c => AutomationProperties.GetName(c) == "Move task to project").SelectedValue = moveTarget;
        var move = Visible<Button>(window).Single(b => Equals(b.Content, "Move to project"));
        move.BringIntoView();
        window.UpdateLayout();
        Click(window, move);
        await UntilAsync(async () => (await backend.GetTaskDetailsAsync(editor.Task.Id)).Task.ProjectId == moveTarget);
        if (title.Text != draftTitle) throw new InvalidOperationException("Moving a task discarded its unsaved title.");
        var save = Visible<Button>(window).Single(b => AutomationProperties.GetName(b) == "Save task editor");
        Save(window, Path.Combine(Path.GetDirectoryName(path)!, "tasks-editor-draft.png"));
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
        Console.WriteLine("PASS: task drafts survive refresh, save to SQLite, reject stale revisions without discarding drafts, and cancel with Escape.");
    }
}
