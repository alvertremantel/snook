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
        if (Path.GetFileName(path) == "today.png")
        {
            var capture = Visible<TextBox>(window).Single(c => AutomationProperties.GetName(c) == "New task title");
            var tracker = Visible<ComboBox>(window).Single(c => AutomationProperties.GetName(c) == "Dashboard activity");
            if (capture.TranslatePoint(default, window)!.Value.X >= tracker.TranslatePoint(default, window)!.Value.X)
                throw new InvalidOperationException("Quick capture is not left of the dashboard tracker.");
            return;
        }
        if (Path.GetFileName(path) != "tasks-board.png") return;
        var backend = App.ConfiguredBackend!;
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
        Named<ComboBox>("Tasks screen project board").SelectedValue = created.Id;
        Named<TextBox>("Tasks screen new project name").Text = "Popup project";
        Click(window, Named<Button>("Create project from Tasks"));
        await UntilAsync(async () => (await backend.GetBootstrapAsync()).Projects.Any(p => p.Name == "Popup project" && p.BoardId == created.Id));
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        await UntilAsync(() => Task.FromResult(!flyout.IsOpen));
        Click(window, trigger);
        await UntilAsync(() => Task.FromResult(flyout.IsOpen));
        window.MouseDown(new Point(210, 100), MouseButton.Left);
        window.MouseUp(new Point(210, 100), MouseButton.Left);
        await UntilAsync(() => Task.FromResult(!flyout.IsOpen));
        Console.WriteLine("PASS: dashboard column order; board creation overlays without reflow, persists board/project, and dismisses with Escape/outside click.");
    }
}
