using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Snook.UI;

public partial class MainWindow
{
    private Border? _dragCard;
    private Border? _dropLane;
    private Point _dragStart;
    private bool _dragging;
    private IPointer? _dragPointer;
    private Point _dragPosition;
    private DispatcherTimer? _boardDragTimer;

    private void OnBoardTabsPrevious(object? sender, RoutedEventArgs e) => ScrollBoardTabs(-280);
    private void OnBoardTabsNext(object? sender, RoutedEventArgs e) => ScrollBoardTabs(280);
    private void ScrollBoardTabs(double delta) => BoardTabsScroll.Offset = new Vector(
        Math.Clamp(BoardTabsScroll.Offset.X + delta, 0, Math.Max(0, BoardTabsScroll.Extent.Width - BoardTabsScroll.Viewport.Width)), 0);

    private void OnRenameWorkspaceItem(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        var row = button.DataContext;
        foreach (var trigger in this.GetVisualDescendants().OfType<Button>().Where(item => item.Flyout?.IsOpen == true))
            trigger.Flyout!.Hide();
        _viewModel.OpenUtilityEditorCommand.Execute(row);
    }

    private void OnWorkspaceActionClick(object? sender, RoutedEventArgs e)
    {
        var flyouts = this.GetVisualDescendants().OfType<Button>()
            .Where(item => item.Flyout?.IsOpen == true).Select(item => item.Flyout!).ToArray();
        // Let Button execute its command before detaching the popup's bindings.
        Dispatcher.UIThread.Post(() =>
        {
            foreach (var flyout in flyouts) flyout.Hide();
        });
    }

    private void OnBoardPrevious(object? sender, RoutedEventArgs e) => ScrollBoard(-306, 0);
    private void OnBoardNext(object? sender, RoutedEventArgs e) => ScrollBoard(306, 0);

    private void OnBoardProjectSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (BoardProjectJump.SelectedItem is not ProjectTaskGroupViewModel project) return;
        var lane = this.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("project-lane")
            && b.DataContext is ProjectTaskGroupViewModel p && p.ProjectId == project.ProjectId);
        lane?.BringIntoView();
    }

    private void ScrollBoard(double x, double y)
        => BoardScroll.Offset = new Vector(
            Math.Clamp(BoardScroll.Offset.X + x, 0, Math.Max(0, BoardScroll.Extent.Width - BoardScroll.Viewport.Width)),
            Math.Clamp(BoardScroll.Offset.Y + y, 0, Math.Max(0, BoardScroll.Extent.Height - BoardScroll.Viewport.Height)));

    private Rect VisibleBoardBounds()
    {
        var boardOrigin = BoardScroll.TranslatePoint(default, this) ?? default;
        var pageOrigin = PageScroll.TranslatePoint(default, this) ?? default;
        return new Rect(boardOrigin, BoardScroll.Viewport).Intersect(new Rect(pageOrigin, PageScroll.Bounds.Size));
    }

    private void AutoScrollBoard()
    {
        if (!_dragging) return;
        var visible = VisibleBoardBounds();
        if (!visible.Contains(_dragPosition)) return;
        var x = _dragPosition.X < visible.Left + 42 ? -18 : _dragPosition.X > visible.Right - 42 ? 18 : 0;
        var y = _dragPosition.Y < visible.Top + 32 ? -12 : _dragPosition.Y > visible.Bottom - 32 ? 12 : 0;
        if (x == 0 && y == 0) return;
        ScrollBoard(x, y);
        BoardScroll.UpdateLayout();
        UpdateDropTarget();
    }

    private void UpdateDropTarget()
    {
        _dropLane?.Classes.Remove("drop-target");
        _dropLane = VisibleBoardBounds().Contains(_dragPosition)
            ? this.GetVisualDescendants().OfType<Border>()
                .Where(b => b.Classes.Contains("project-lane") && b.IsEffectivelyVisible)
                .FirstOrDefault(b => this.TranslatePoint(_dragPosition, b) is { } local && new Rect(b.Bounds.Size).Contains(local))
            : null;
        _dropLane?.Classes.Add("drop-target");
    }

    private void OnCardPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border handle || !e.GetCurrentPoint(handle).Properties.IsLeftButtonPressed)
            return;
        _dragCard = handle.GetVisualAncestors().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("task-card"));
        _dragStart = e.GetPosition(this);
        _dragPointer = e.Pointer;
        e.Pointer.Capture(handle);
        e.Handled = true;
    }

    private void OnCardMoved(object? sender, PointerEventArgs e)
    {
        if (_dragCard is null) return;
        var position = e.GetPosition(this);
        _dragPosition = position;
        if (!_dragging && Math.Abs(position.X - _dragStart.X) + Math.Abs(position.Y - _dragStart.Y) < 6) return;
        _dragging = true;
        _boardDragTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(32), DispatcherPriority.Input, (_, _) => AutoScrollBoard());
        _boardDragTimer.Start();
        _dragCard.Classes.Add("dragging");
        BoardDragPreview.DataContext = _dragCard.DataContext;
        BoardDragOverlay.IsVisible = true;
        Canvas.SetLeft(BoardDragPreview, Math.Clamp(position.X + 14, 0, Math.Max(0, Bounds.Width - 274)));
        Canvas.SetTop(BoardDragPreview, Math.Clamp(position.Y + 14, 0, Math.Max(0, Bounds.Height - 140)));
        UpdateDropTarget();
    }

    private void OnCardReleased(object? sender, PointerReleasedEventArgs e)
    {
        var task = _dragCard?.DataContext as TaskRowViewModel;
        var project = _dropLane?.DataContext as ProjectTaskGroupViewModel;
        var shouldMove = _dragging && task is not null && project is not null && task.Task.ProjectId != project.ProjectId;
        ClearBoardDrag();
        e.Pointer.Capture(null);
        if (shouldMove)
        {
            task!.TargetProjectId = project!.ProjectId;
            task.MoveCommand.Execute(null);
        }
        e.Handled = true;
    }

    private void OnCardCaptureLost(object? sender, PointerCaptureLostEventArgs e) => ClearBoardDrag();

    private void OnTaskCaptureKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers != KeyModifiers.None) return;
        if (_viewModel.CreateTaskCommand.CanExecute(null))
            _viewModel.CreateTaskCommand.Execute(null);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _dragCard is not null)
        {
            ClearBoardDrag();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _viewModel.IsUtilityEditorOpen)
        {
            _viewModel.CloseUtilityEditorCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _viewModel.IsTaskEditorOpen)
        {
            _viewModel.CloseTaskEditorCommand.Execute(null);
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    private void ClearBoardDrag()
    {
        _dragCard?.Classes.Remove("dragging");
        _dropLane?.Classes.Remove("drop-target");
        _dragCard = null;
        _dropLane = null;
        _dragging = false;
        _boardDragTimer?.Stop();
        BoardDragOverlay.IsVisible = false;
        BoardDragPreview.DataContext = null;
        var pointer = _dragPointer;
        _dragPointer = null;
        pointer?.Capture(null);
    }
}
