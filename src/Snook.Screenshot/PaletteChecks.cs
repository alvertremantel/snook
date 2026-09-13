using Avalonia.Controls;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Snook.UI;

namespace Snook.Screenshot;

internal static partial class InteractionChecks
{
    public static async Task CheckPaletteAsync(MainWindow window, string path)
    {
        if (Path.GetFileName(path) != "tasks-details.png") return;
        if (window.ActualThemeVariant != ThemeVariant.Light)
            throw new InvalidOperationException("The workspace unexpectedly inherited a dark system theme.");
        var title = window.FindControl<TextBox>("TaskEditorTitle")!;
        title.Focus();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        if (title.Background is not ISolidColorBrush background || background.Color != Color.Parse("#FFFFFF"))
            throw new InvalidOperationException("Focused text entry does not use the light input surface.");
        Save(window, Path.Combine(Path.GetDirectoryName(path)!, "palette-focused-input.png"));
        var picker = Visible<ComboBox>(window).First(c => c.GetVisualAncestors().OfType<Border>().Any(b => b.Name == "TaskDrawer"));
        picker.Focus();
        picker.IsDropDownOpen = true;
        await Task.Delay(100);
        window.UpdateLayout();
        Save(window, Path.Combine(Path.GetDirectoryName(path)!, "palette-dropdown.png"));
        if (!picker.IsDropDownOpen)
            throw new InvalidOperationException("The palette dropdown capture did not open its popup.");
        picker.IsDropDownOpen = false;
        var save = Visible<Button>(window).Single(b => b.Classes.Contains("primary") && Equals(b.Content, "Save task"));
        var point = save.TranslatePoint(new Point(save.Bounds.Width / 2, save.Bounds.Height / 2), window)!.Value;
        window.MouseMove(point);
        Save(window, Path.Combine(Path.GetDirectoryName(path)!, "palette-button-hover.png"));
        window.MouseDown(point, MouseButton.Left);
        Save(window, Path.Combine(Path.GetDirectoryName(path)!, "palette-button-pressed.png"));
        // Release outside the button: inspect its pressed state without saving a task.
        window.MouseMove(new Point(1, 1));
        window.MouseUp(new Point(1, 1), MouseButton.Left);
        Console.WriteLine("PASS: explicit light theme, focused input surface, open dropdown, and button state captures.");
    }
}
