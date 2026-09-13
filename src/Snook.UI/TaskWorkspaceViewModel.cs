using System.Windows.Input;

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

    private void InitializeTaskWorkspaceCommands()
        => CloseTaskEditorCommand = new AsyncCommand(_ =>
        {
            if (TaskEditor is null) return Task.CompletedTask;
            TaskEditor = null;
            SelectedTaskDetails = null;
            StatusMessage = "Ready.";
            return Task.CompletedTask;
        });

    private async Task RefreshTaskEditorDetailsAsync(TaskRowViewModel row)
    {
        if (ReferenceEquals(TaskEditor, row))
            SelectedTaskDetails = new TaskDetailsPanelViewModel(await _backend.GetTaskDetailsAsync(row.Task.Id));
    }
}
