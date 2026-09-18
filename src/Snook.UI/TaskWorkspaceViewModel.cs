using System.Windows.Input;
using System.Collections.ObjectModel;
using System.ComponentModel;
using Snook.Contracts;

namespace Snook.UI;

public sealed partial class MainWindowViewModel
{
    private TaskRowViewModel? _taskEditor;
    public TaskRowViewModel? TaskEditor
    {
        get => _taskEditor;
        private set { if (SetField(ref _taskEditor, value)) RaisePropertyChanged(nameof(IsTaskEditorOpen)); }
    }
    public bool IsTaskEditorOpen => TaskEditor is not null;
    public bool HasPageSearch => IsTasksVisible || IsTodayVisible || IsHistoryVisible;
    public string SearchHint => IsHistoryVisible ? "Search sessions" : "Search tasks";
    public ICommand CloseTaskEditorCommand { get; private set; } = null!;
    public ICommand SelectTaskBoardCommand { get; private set; } = null!;
    public ObservableCollection<BoardTab> BoardTabs { get; } = [];
    public BoardAdminRowViewModel? SelectedTaskBoardAdmin => BoardAdminRows.FirstOrDefault(row => row.Board.Id == TaskBoardId);
    public bool HasSelectedTaskBoard => SelectedTaskBoardAdmin is not null;

    private void InitializeTaskWorkspaceCommands()
    {
        InitializeBulkTaskCommands();
        SelectTaskBoardCommand = new AsyncCommand(value =>
        {
            if (value is Guid id) TaskBoardId = id;
            return Task.CompletedTask;
        });
        CloseTaskEditorCommand = new AsyncCommand(_ =>
        {
            if (TaskEditor is null) return Task.CompletedTask;
            TaskEditor = null;
            SelectedTaskDetails = null;
            StatusMessage = "Ready.";
            return Task.CompletedTask;
        });
    }

    private void NotifyBoardNavigation()
    {
        ReconcileOptions(BoardTabs, TaskBoardOptions.Select(board => BoardTabs.FirstOrDefault(tab => tab.Id == board.Id && tab.Name == board.Name)
            ?? new BoardTab(board.Id, board.Name)), tab => tab.Id);
        foreach (var tab in BoardTabs) tab.IsSelected = tab.Id == TaskBoardId;
        RaisePropertyChanged(nameof(BoardTabs));
        RaisePropertyChanged(nameof(SelectedTaskBoardAdmin));
        RaisePropertyChanged(nameof(HasSelectedTaskBoard));
    }

    private TaskOption TaskPickerOption(TaskListItem item)
        => new(item.Task.Id, item.Task.Title, $"{item.BoardName} / {item.ProjectName}");

    private async Task RefreshTaskEditorDetailsAsync(TaskRowViewModel row)
    {
        if (ReferenceEquals(TaskEditor, row))
            SelectedTaskDetails = new TaskDetailsPanelViewModel(await _backend.GetTaskDetailsAsync(row.Task.Id));
    }
}

public sealed class BoardTab(Guid id, string name) : INotifyPropertyChanged
{
    private bool _isSelected;
    public Guid Id { get; } = id;
    public string Name { get; } = name;
    public event PropertyChangedEventHandler? PropertyChanged;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }
}
public sealed record TaskPickerEntry(Guid Id, string Name, string Context, bool IsHeader = false)
{
    public bool IsSelectable => !IsHeader;
    public bool HasContext => Context.Length > 0;
}
