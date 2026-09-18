using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Windows.Input;
using Snook.Contracts;
using Snook.Domain;

namespace Snook.UI;

public sealed partial class MainWindowViewModel
{
    private readonly HashSet<Guid> _selectedTaskIds = [];
    private bool _showStarredTasks;
    public bool ShowStarredTasks
    {
        get => _showStarredTasks;
        set
        {
            if (!SetField(ref _showStarredTasks, value)) return;
            RaisePropertyChanged(nameof(ShowAllTasks));
            RebuildTaskWorkspace();
        }
    }
    public bool ShowAllTasks => !ShowStarredTasks;
    public bool HasSelectedTasks => _selectedTaskIds.Count > 0;
    public string TaskSelectionLabel => $"{_selectedTaskIds.Count} selected";
    public IEnumerable<ProjectAdminRowViewModel> StarredProjects => ProjectAdminRows.Where(row => row.Project.Starred
        && row.Project.ArchivedAtUtc is null && row.Project.DeletedAtUtc is null
        && Projects.Any(project => project.Id == row.Project.Id) && IsTaskBoardProject(row.Project.Id));
    private bool IsStarredProject(Guid id) => ProjectAdminRows.Any(row => row.Project.Id == id && row.Project.Starred);
    public ICommand SelectAllTasksCommand { get; private set; } = null!;
    public ICommand ClearTaskSelectionCommand { get; private set; } = null!;
    public ICommand OpenBulkTaskEditorCommand { get; private set; } = null!;
    public ICommand ToggleTaskStarCommand { get; private set; } = null!;
    public ICommand ToggleProjectStarCommand { get; private set; } = null!;
    public ICommand SelectTaskScopeCommand { get; private set; } = null!;

    private void InitializeBulkTaskCommands()
    {
        SelectTaskScopeCommand = new AsyncCommand(value => { ShowStarredTasks = value as string == "Starred"; return Task.CompletedTask; });
        SelectAllTasksCommand = new AsyncCommand(_ =>
        {
            var rows = WorkspaceTasks.Where(row => row.CanSelect).ToArray();
            if (rows.Length > 500) { StatusMessage = "Narrow the search or board to select at most 500 tasks."; return Task.CompletedTask; }
            foreach (var row in rows) row.IsSelected = true;
            return Task.CompletedTask;
        });
        ClearTaskSelectionCommand = new AsyncCommand(_ => { ClearTaskSelection(); return Task.CompletedTask; });
        OpenBulkTaskEditorCommand = new AsyncCommand(_ =>
        {
            var targets = WorkspaceTasks.Where(row => row.IsSelected && row.CanSelect).Select(row => row.Task).ToArray();
            if (targets.Length == 0) return Task.CompletedTask;
            if (targets.Length > 500) { StatusMessage = "Select at most 500 tasks."; return Task.CompletedTask; }
            var draft = new BulkTaskEditorViewModel(targets, Projects.ToArray(), Activities.ToArray(), SaveTaskBatchAsync, ReviewTaskBatchAsync);
            UtilityDraft = draft;
            UtilityTitle = $"Edit {targets.Length} tasks";
            UtilitySaveCommand = draft.SaveCommand;
            _utilityKind = "bulk";
            StatusMessage = "Check the fields to change. Unchecked fields keep each task’s current value.";
            NotifyUtilityEditor();
            return Task.CompletedTask;
        });
        ToggleTaskStarCommand = new AsyncCommand(async value =>
        {
            if (value is not TaskRowViewModel row) return;
            try
            {
                await _backend.BulkUpdateTasksAsync([new(row.Task.Id, row.Task.Revision)], new BulkTaskUpdate(Starred: !row.Task.Starred),
                    new OperationRequest(Guid.NewGuid(), Guid.NewGuid()));
                await RefreshAsync();
            }
            catch (Exception exception) { StatusMessage = EditorFailure(exception, "The task favorite could not be changed."); }
        });
        ToggleProjectStarCommand = new AsyncCommand(async value =>
        {
            if (value is not ProjectAdminRowViewModel row) return;
            try
            {
                await _backend.UpdateProjectAsync(row.Project.Id,
                    new ProjectUpdate(row.Project.Name, row.Project.Description, !row.Project.Starred),
                    new OperationRequest(Guid.NewGuid(), Guid.NewGuid(), row.Project.Revision));
                await RefreshAsync();
            }
            catch (Exception exception) { StatusMessage = EditorFailure(exception, "The project favorite could not be changed."); }
        });
    }

    private void OnTaskSelectionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(TaskRowViewModel.IsSelected) || sender is not TaskRowViewModel row) return;
        if (row.IsSelected) _selectedTaskIds.Add(row.Task.Id);
        else _selectedTaskIds.Remove(row.Task.Id);
        NotifyTaskSelection();
    }

    private void ReconcileTaskSelection()
    {
        var visible = WorkspaceTasks.Where(row => row.CanSelect).Select(row => row.Task.Id).ToHashSet();
        _selectedTaskIds.IntersectWith(visible);
        foreach (var row in Tasks) row.IsSelected = _selectedTaskIds.Contains(row.Task.Id);
        NotifyTaskSelection();
    }

    private void ClearTaskSelection()
    {
        _selectedTaskIds.Clear();
        foreach (var row in Tasks) row.IsSelected = false;
        NotifyTaskSelection();
    }

    private void NotifyTaskSelection()
    {
        RaisePropertyChanged(nameof(HasSelectedTasks));
        RaisePropertyChanged(nameof(TaskSelectionLabel));
    }

    private async Task SaveTaskBatchAsync(BulkTaskEditorViewModel draft)
    {
        try
        {
            var update = draft.CreateUpdate();
            var targets = draft.Targets.Select(task => new TaskRevision(task.Id, task.Revision)).ToArray();
            var operation = draft.OperationFor(targets, update);
            await _backend.BulkUpdateTasksAsync(targets, update, operation);
            StatusMessage = $"Updated {targets.Length} tasks.";
            FinishUtilityEdit(draft);
            ClearTaskSelection();
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            StatusMessage = exception is SnookException or ArgumentException or FormatException
                ? exception.Message : "The batch could not be confirmed. Retry to safely recover its result.";
        }
    }

    private async Task ReviewTaskBatchAsync(BulkTaskEditorViewModel draft)
    {
        try
        {
            var current = await _backend.SearchTasksAsync(includeCompleted: true, includeArchived: true);
            var ids = draft.Targets.Select(task => task.Id).ToHashSet();
            var targets = current.Where(row => ids.Contains(row.Task.Id)).Select(row => row.Task).ToArray();
            if (targets.Length != ids.Count)
            {
                StatusMessage = "Some selected tasks were deleted or their project is unavailable. Cancel and select tasks again.";
                return;
            }
            draft.ReviewTargets(targets);
            StatusMessage = "Latest task revisions loaded. Review the selected tasks and your fields, then Apply. Your draft is unchanged.";
        }
        catch (Exception exception) { StatusMessage = EditorFailure(exception, "The selected tasks could not be refreshed."); }
    }
}

public sealed class BulkTaskEditorViewModel : INotifyPropertyChanged
{
    private bool _changeTitle, _changeDescription, _changePriority, _changeStatus, _changeDueDate, _changeActivity, _changeStarred, _changeProject, _changeArchive, _changeTags;
    private void SetFlag(ref bool field, bool value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (field == value) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
    private string? _operationSignature;
    private OperationRequest? _operation;
    public BulkTaskEditorViewModel(IReadOnlyList<TaskItem> tasks, IReadOnlyList<ProjectOption> projects, IReadOnlyList<ActivityOption> activities,
        Func<BulkTaskEditorViewModel, Task> save, Func<BulkTaskEditorViewModel, Task> review)
    {
        Targets = tasks;
        Projects = projects.Select(project => new TaskOption(project.Id, $"{project.BoardName} / {project.Name}")).ToArray();
        Activities = new[] { new TaskOption(Guid.Empty, "No activity") }.Concat(activities.Select(activity => new TaskOption(activity.Id, activity.Name))).ToArray();
        SaveCommand = new AsyncCommand(_ => save(this));
        ReviewCommand = new AsyncCommand(_ => review(this));
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    public IReadOnlyList<TaskItem> Targets { get; private set; }
    public string TargetSummary => string.Join("\n", Targets.Select(task => $"{task.Title} · {task.Priority} · {task.Status} · {(task.DueDate is { } due ? $"due {due:yyyy-MM-dd}" : "no due date")}{(task.Starred ? " · starred" : "")}{(task.ArchivedAtUtc is not null ? " · archived" : "")}"));
    public void ReviewTargets(IReadOnlyList<TaskItem> targets)
    {
        Targets = targets;
        _operationSignature = null;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TargetSummary)));
    }
    public IReadOnlyList<TaskOption> Projects { get; }
    public IReadOnlyList<TaskOption> Activities { get; }
    public IReadOnlyList<PriorityOption> Priorities { get; } = Enum.GetValues<Priority>().Select(priority => new PriorityOption(priority, priority.ToString())).ToArray();
    public bool ChangeTitle { get => _changeTitle; set => SetFlag(ref _changeTitle, value); }
    public string Title { get; set; } = "";
    public bool ChangeDescription { get => _changeDescription; set => SetFlag(ref _changeDescription, value); }
    public string Description { get; set; } = "";
    public bool ChangePriority { get => _changePriority; set => SetFlag(ref _changePriority, value); }
    public Priority Priority { get; set; }
    public bool ChangeStatus { get => _changeStatus; set => SetFlag(ref _changeStatus, value); }
    public bool Completed { get; set; }
    public bool ChangeDueDate { get => _changeDueDate; set => SetFlag(ref _changeDueDate, value); }
    public string DueDateText { get; set; } = "";
    public bool ChangeActivity { get => _changeActivity; set => SetFlag(ref _changeActivity, value); }
    public Guid ActivityId { get; set; }
    public bool ChangeStarred { get => _changeStarred; set => SetFlag(ref _changeStarred, value); }
    public bool Starred { get; set; } = true;
    public bool ChangeProject { get => _changeProject; set => SetFlag(ref _changeProject, value); }
    public Guid ProjectId { get; set; }
    public bool ChangeArchive { get => _changeArchive; set => SetFlag(ref _changeArchive, value); }
    public bool Archived { get; set; }
    public bool ChangeTags { get => _changeTags; set => SetFlag(ref _changeTags, value); }
    public string AddTagsText { get; set; } = "";
    public string RemoveTagsText { get; set; } = "";
    public ICommand SaveCommand { get; }
    public ICommand ReviewCommand { get; }
    public BulkTaskUpdate CreateUpdate()
    {
        DateOnly? due = null;
        if (ChangeDueDate && !string.IsNullOrWhiteSpace(DueDateText))
        {
            if (!DateOnly.TryParseExact(DueDateText.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) || parsed.Year < 1900)
                throw new FormatException("Enter a due date as yyyy-MM-dd (1900 or later), or leave it blank to clear dates.");
            due = parsed;
        }
        if (ChangeProject && !Projects.Any(project => project.Id == ProjectId)) throw new FormatException("Choose a destination project.");
        return new BulkTaskUpdate(ChangeTitle ? Title : null, ChangeDescription ? Description : null,
            ChangePriority ? Priority : null, ChangeStatus ? Completed ? TaskState.Completed : TaskState.Open : null,
            ChangeDueDate, due, ChangeActivity, ActivityId == Guid.Empty ? null : ActivityId,
            ChangeStarred ? Starred : null, ChangeProject ? ProjectId : null, ChangeArchive ? Archived : null,
            ChangeTags ? SplitTags(AddTagsText) : null, ChangeTags ? SplitTags(RemoveTagsText) : null);
    }
    private static string[] SplitTags(string value) => value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    public OperationRequest OperationFor(IReadOnlyList<TaskRevision> targets, BulkTaskUpdate update)
    {
        var signature = JsonSerializer.Serialize(new { targets, update });
        if (_operationSignature != signature)
        {
            _operationSignature = signature;
            _operation = new OperationRequest(Guid.NewGuid(), Guid.NewGuid());
        }
        return _operation!;
    }
}
