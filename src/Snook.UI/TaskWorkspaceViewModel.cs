using System.Windows.Input;
using System.Collections.ObjectModel;
using System.ComponentModel;
using Snook.Application;
using Snook.Contracts;
using Snook.Domain;

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
        try
        {
            if (!ReferenceEquals(TaskEditor, row)) return;
            var details = await _backend.GetTaskDetailsAsync(row.Task.Id);
            if (ReferenceEquals(TaskEditor, row)) SelectedTaskDetails = new TaskDetailsPanelViewModel(details);
        }
        catch (Exception)
        {
            StatusMessage = "The change was saved, but task details could not refresh. Refresh to load the saved details; do not repeat the saved action.";
        }
    }

    public TaskEditorRecoveryDraft? CaptureTaskEditorDraft(Guid workspaceId)
    {
        if (TaskEditor is not { } row) return null;
        return new TaskEditorRecoveryDraft(2, workspaceId, _clock.GetUtcNow(), row.Task.Id,
            row.Task.Revision, row.DraftTitle, row.DraftDescription, row.DraftPriority,
            row.DueDateText, row.DraftActivityId, row.DraftStarred, row.TargetProjectId,
            row.TagText, row.PrerequisiteTaskId, row.LinkLabelText, row.LinkUriText,
            row.ReminderTimeText, row.ReminderTimeZone, row.ReminderFoldChoice);
    }

    public async Task<bool> RestoreTaskEditorDraftAsync(TaskEditorRecoveryDraft draft,
        Guid workspaceId)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (_disposed || !draft.IsValid(workspaceId, _clock.GetUtcNow())
            || IsTaskEditorOpen || IsUtilityEditorOpen) return false;
        try
        {
            var item = (await _backend.SearchTasksAsync(includeCompleted: true, includeArchived: true))
                .FirstOrDefault(candidate => candidate.Task.Id == draft.TaskId);
            if (item is null) return false;
            var details = await _backend.GetTaskDetailsAsync(draft.TaskId);
            await SelectSectionAsync("Tasks");
            var expectedTask = details.Task with { Revision = draft.ExpectedRevision };
            SelectedTaskDetails = new TaskDetailsPanelViewModel(details);
            var row = new TaskRowViewModel(item with { Task = expectedTask }, Projects, Activities,
                (await _backend.SearchTasksAsync()).Select(TaskPickerOption),
                StartTaskAsync, CompleteTaskAsync, SaveTaskAsync, MoveTaskAsync, ArchiveTaskAsync,
                DeleteTaskAsync, AddTaskTagAsync, AddTaskDependencyAsync, AddTaskLinkAsync,
                ShowTaskDetailsAsync)
            {
                DraftTitle = draft.DraftTitle,
                DraftDescription = draft.DraftDescription,
                DraftPriority = draft.DraftPriority,
                DueDateText = draft.DueDateText,
                DraftActivityId = draft.DraftActivityId,
                DraftStarred = draft.DraftStarred,
                TargetProjectId = draft.TargetProjectId,
                TagText = draft.TagText,
                PrerequisiteTaskId = draft.PrerequisiteTaskId,
                LinkLabelText = draft.LinkLabelText,
                LinkUriText = draft.LinkUriText,
                ReminderTimeText = draft.ReminderTimeText,
                ReminderTimeZone = draft.Version == 1 || string.IsNullOrWhiteSpace(draft.ReminderTimeZone)
                    ? TimeZoneInfo.Local.Id : draft.ReminderTimeZone,
                ReminderFoldChoice = draft.ReminderFoldChoice
            };
            TaskEditor = row;
            StatusMessage = details.Task.Revision == draft.ExpectedRevision
                ? "Unsaved task edit recovered. Review before saving."
                : "Unsaved task edit recovered, but the saved task changed. Your draft is preserved; saving will require conflict recovery.";
            return true;
        }
        catch (SnookException) { return false; }
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
