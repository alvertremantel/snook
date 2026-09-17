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
    public static async Task CheckWorkspaceAsync(MainWindow window, string path)
    {
        var backend = App.ConfiguredBackend!;
        var viewModel = (MainWindowViewModel)window.DataContext!;
        if (Path.GetFileName(path) == "today.png")
        {
            var capture = Visible<TextBox>(window).Single(c => AutomationProperties.GetName(c) == "New task title");
            var tracker = Visible<ComboBox>(window).Single(c => AutomationProperties.GetName(c) == "Dashboard activity");
            if (capture.TranslatePoint(default, window)!.Value.X >= tracker.TranslatePoint(default, window)!.Value.X)
                throw new InvalidOperationException("Quick capture is not left of the dashboard tracker.");
            var before = (await backend.SearchTasksAsync()).Count;
            capture.Focus();
            capture.Text = "   ";
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            await Task.Delay(150);
            if ((await backend.SearchTasksAsync()).Count != before)
                throw new InvalidOperationException("Enter created a blank task.");
            capture.Text = "Keyboard capture fixture";
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            await UntilAsync(() => Task.FromResult(capture.Text == "" && viewModel.Tasks.Any(task => task.Title == "Keyboard capture fixture")));
            if ((await backend.SearchTasksAsync()).Count(task => task.Task.Title == "Keyboard capture fixture") != 1)
                throw new InvalidOperationException("Enter did not persist exactly one task.");
            if (window.FocusManager?.GetFocusedElement() != capture)
                throw new InvalidOperationException("Quick capture lost focus after Enter.");
            Console.WriteLine("PASS: Enter captures exactly one persisted task, clears the field, retains focus, and rejects whitespace.");
            return;
        }
        if (Path.GetFileName(path) != "tasks-board.png") return;
        var trigger = window.FindControl<Button>("BoardCreationButton")!;
        var board = window.FindControl<ScrollViewer>("BoardScroll")!;
        var boardTop = board.TranslatePoint(default, window)!.Value.Y;
        if (trigger.TranslatePoint(default, window)!.Value.Y >= boardTop)
            throw new InvalidOperationException("Board creation is not above the lanes.");
        Click(window, trigger);
        var flyout = (Flyout)trigger.Flyout!;
        await UntilAsync(() => Task.FromResult(flyout.IsOpen));
        await Task.Delay(100);
        var content = (Control)flyout.Content!;
        T Named<T>(string name) where T : Control => content.GetVisualDescendants().OfType<T>().Single(c => AutomationProperties.GetName(c) == name);
        if (board.TranslatePoint(default, window)!.Value.Y != boardTop)
            throw new InvalidOperationException("Opening creation displaced the board.");
        Save(window, Path.Combine(Path.GetDirectoryName(path)!, "board-creation-popup.png"));
        Named<TextBox>("Tasks screen new board name").Text = "Popup board";
        Click(window, Named<Button>("Create board from Tasks"));
        await UntilAsync(async () => (await backend.GetBootstrapAsync()).Boards.Any(b => b.Name == "Popup board"));
        await UntilAsync(() => Task.FromResult(((MainWindowViewModel)window.DataContext!).NewBoardName == ""));
        var created = (await backend.GetBootstrapAsync()).Boards.Single(b => b.Name == "Popup board");
        await UntilAsync(() => Task.FromResult(viewModel.TaskBoardId == created.Id && viewModel.HasEmptyTaskBoard));
        if (viewModel.SelectedProjectId != Guid.Empty)
            throw new InvalidOperationException("An empty board selected another board's project for capture.");
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        await UntilAsync(() => Task.FromResult(!flyout.IsOpen));
        await Task.Delay(100);
        var selector = Visible<ComboBox>(window).Single(c => AutomationProperties.GetName(c) == "Task board");
        if (selector.SelectedItem is not BoardOption { Name: "Popup board" } ||
            !Visible<TextBlock>(window).Any(text => text.Text?.StartsWith("This board has no projects yet", StringComparison.Ordinal) == true))
            throw new InvalidOperationException("The newly created empty board is not rendered.");
        Save(window, Path.Combine(Path.GetDirectoryName(path)!, "created-empty-board.png"));
        Click(window, trigger);
        await UntilAsync(() => Task.FromResult(flyout.IsOpen));
        Named<ComboBox>("Tasks screen project board").SelectedValue = created.Id;
        Named<TextBox>("Tasks screen new project name").Text = "Popup project";
        Click(window, Named<Button>("Create project from Tasks"));
        await UntilAsync(async () => (await backend.GetBootstrapAsync()).Projects.Any(p => p.Name == "Popup project" && p.BoardId == created.Id));
        await UntilAsync(() => Task.FromResult(viewModel.TaskGroups.Count == 1 && viewModel.TaskGroups[0].ProjectName == "Popup project"));
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        await UntilAsync(() => Task.FromResult(!flyout.IsOpen));
        await Task.Delay(100);
        if (!Visible<TextBlock>(window).Any(text => text.Text == "Popup project"))
            throw new InvalidOperationException("The new board's project lane is not rendered.");
        Save(window, Path.Combine(Path.GetDirectoryName(path)!, "created-board-project.png"));
        selector.SelectedValue = Guid.Empty;
        await UntilAsync(() => Task.FromResult(viewModel.TaskGroups.Count > 1));
        Click(window, trigger);
        await UntilAsync(() => Task.FromResult(flyout.IsOpen));
        window.MouseDown(new Point(210, 100), MouseButton.Left);
        window.MouseUp(new Point(210, 100), MouseButton.Left);
        await UntilAsync(() => Task.FromResult(!flyout.IsOpen));
        Console.WriteLine("PASS: board creation persists and renders the selected empty board, renders its first project, supports All boards, and dismisses with Escape/outside click.");
    }
}
