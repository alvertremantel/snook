using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Avalonia.Threading;
using Snook.Contracts;
using Snook.Domain;
using DomainCalendar = Snook.Domain.Calendar;

namespace Snook.UI;

public sealed partial class MainWindowViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private const string LocalDateTimeFormat = "yyyy-MM-dd HH:mm";
    private readonly IBackendClient _backend;
    private readonly Timer _displayTimer;
    private bool _initialized;
    private string _taskTitle = string.Empty;
    private string _searchText = string.Empty;
    private string _statusMessage = "Ready. Your workspace is stored locally.";
    private string _workspaceName = "Snook";
    private string _trackedToday = "0m";
    private string _currentSection = "Today";
    private string _sectionTitle = "Welcome back";
    private string _sectionSubtitle = "A little progress, thoughtfully recorded.";
    private bool _hasNoHistory = true;
    private bool _historyHasMore;
    private string? _historyContinuation;
    private bool _hasNoSummary = true;
    private bool _hasNoCalendarBlocks = true;
    private bool _showCompleted;
    private bool _showArchived;
    private bool _showDeleted;
    private bool _hasNoTasks = true;
    private Guid _selectedCalendarId;
    private string _calendarView = "Week";
    private string _taskViewMode = "List";
    private string _taskSortMode = "Priority";
    private string _summaryGrouping = "Project";
    private int _openTaskCount;
    private bool _hasNoActiveSessions = true;
    private bool _hasNoTodayRecentRows = true;
    private bool _hasNoTodayUpcomingRows = true;
    private bool _hasDeletedCalendarEvents;
    private Guid _selectedProjectId;
    private Guid _selectedBoardId;
    private Guid _taskBoardId;
    private Dictionary<Guid, Guid> _projectBoards = [];
    private string _newProjectName = string.Empty;
    private string _newBoardName = string.Empty;
    private string _newActivityName = string.Empty;
    private string _newActivityDescription = string.Empty;
    private SessionLane _newActivityLane = SessionLane.Foreground;
    private Guid _newActivityGroupId;
    private Guid _selectedTrackingActivityId;
    private string _newEventTitle = string.Empty;
    private string _newEventDescription = string.Empty;
    private string _newEventLocation = string.Empty;
    private string _newEventColor = "#246B63";
    private string _newEventRecurrence = string.Empty;
    private string _newEventRecurrenceEnd = string.Empty;
    private bool _newEventAllDay;
    private string _newEventStart = string.Empty;
    private string _newEventEnd = string.Empty;
    private string _newCalendarName = string.Empty;
    private string _newCalendarColor = "#246B63";
    private string _newActivityGroupName = string.Empty;
    private Guid? _manualTaskId;
    private Guid? _manualActivityId;
    private string _manualStartText = FormatLocalDateTime(DateTimeOffset.UtcNow.AddMinutes(-30));
    private string _manualEndText = FormatLocalDateTime(DateTimeOffset.UtcNow);
    private string _manualNotes = string.Empty;
    private bool _allowConcurrentForeground;
    private bool _timerPreferenceDirty;
    private long _settingsRevision = 1;
    private long _todayMilliseconds;
    private TodaySnapshot? _todayClockSnapshot;
    private DateTimeOffset _nextDayRefresh;
    private TaskDetailsPanelViewModel? _selectedTaskDetails;

    public MainWindowViewModel(IBackendClient backend)
    {
        _backend = backend;
        InitializeCalendarCommands();
        InitializeTaskWorkspaceCommands();
        InitializeWorkspaceEditors();
        _backend.Changed += OnBackendChanged;
        RefreshCommand = new AsyncCommand(_ => RefreshAsync());
        CreateTaskCommand = new AsyncCommand(_ => CreateTaskAsync(), _ => !string.IsNullOrWhiteSpace(TaskTitle) && SelectedProjectId != Guid.Empty);
        StartFocusCommand = new AsyncCommand(_ => StartFocusAsync());
        SelectSectionCommand = new AsyncCommand(section => SelectSectionAsync(section as string ?? "Today"));
        PlanNextTaskCommand = new AsyncCommand(_ => PlanNextTaskAsync());
        BackupCommand = new AsyncCommand(_ => CreateBackupAsync());
        ExportCommand = new AsyncCommand(_ => ExportAsync());
        ExportCsvCommand = new AsyncCommand(_ => ExportCsvAsync());
        RestoreCommand = new AsyncCommand(_ => RestoreLatestBackupAsync());
        LoadMoreHistoryCommand = new AsyncCommand(_ => LoadMoreHistoryAsync(), _ => HistoryHasMore);
        ShowTaskDetailsCommand = new AsyncCommand(row => row is TaskRowViewModel taskRow ? ShowTaskDetailsAsync(taskRow) : Task.CompletedTask);
        ManualTimeCommand = new AsyncCommand(_ => AddManualTimeAsync());
        QuickManualTimeCommand = new AsyncCommand(_ => AddQuickManualTimeAsync());
        SelectCalendarViewCommand = new AsyncCommand(view => SelectCalendarViewAsync(view as string ?? "Week"));
        SelectTaskViewCommand = new AsyncCommand(view => SelectTaskViewAsync(view as string ?? "List"));
        SelectSummaryGroupingCommand = new AsyncCommand(grouping => SelectSummaryGroupingAsync(grouping as string ?? "Tag"));
        CreateProjectCommand = new AsyncCommand(_ => CreateProjectAsync(), _ => !string.IsNullOrWhiteSpace(NewProjectName) && SelectedBoardId != Guid.Empty);
        CreateBoardCommand = new AsyncCommand(_ => CreateBoardAsync(), _ => !string.IsNullOrWhiteSpace(NewBoardName));
        CreateActivityCommand = new AsyncCommand(_ => CreateActivityAsync(), _ => !string.IsNullOrWhiteSpace(NewActivityName));
        StartSelectedActivityCommand = new AsyncCommand(_ => StartSelectedActivityAsync(), _ => SelectedTrackingActivityId != Guid.Empty);
        CreateActivityGroupCommand = new AsyncCommand(_ => CreateActivityGroupAsync(), _ => !string.IsNullOrWhiteSpace(NewActivityGroupName));
        CreateCalendarEventCommand = new AsyncCommand(_ => CreateCalendarEventAsync(), _ => !string.IsNullOrWhiteSpace(NewEventTitle) && SelectedCalendarId != Guid.Empty);
        CreateCalendarCommand = new AsyncCommand(_ => CreateCalendarAsync(), _ => !string.IsNullOrWhiteSpace(NewCalendarName));
        SaveSettingsCommand = new AsyncCommand(_ => SaveSettingsAsync());
        ResetTimerPreferenceCommand = new AsyncCommand(async _ =>
        {
            _timerPreferenceDirty = false;
            await RefreshAsync();
        });
        _displayTimer = new Timer(_ => Dispatcher.UIThread.Post(UpdateDisplayTimes), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<TaskRowViewModel> Tasks { get; } = [];
    public ObservableCollection<ProjectTaskGroupViewModel> TaskGroups { get; } = [];
    public ObservableCollection<ActiveSessionRowViewModel> ActiveSessions { get; } = [];
    public ObservableCollection<ProjectOption> Projects { get; } = [];
    public ObservableCollection<TaskPickerEntry> CaptureProjectOptions { get; } = [];
    public ObservableCollection<BoardOption> Boards { get; } = [];
    public ObservableCollection<BoardOption> TaskBoardOptions { get; } = [new(Guid.Empty, "All boards")];
    public Guid TaskBoardId
    {
        get => _taskBoardId;
        set
        {
            if (!SetField(ref _taskBoardId, value)) return;
            if (value != Guid.Empty)
            {
                SelectedBoardId = value;
                SelectedProjectId = Projects.FirstOrDefault(project => IsTaskBoardProject(project.Id))?.Id ?? Guid.Empty;
            }
            RebuildTaskWorkspace();
            NotifyBoardNavigation();
        }
    }
    public IEnumerable<TaskRowViewModel> WorkspaceTasks => Tasks.Where(row => IsTaskBoardProject(row.Task.ProjectId)
        && (!ShowStarredTasks || row.Task.Starred || IsStarredProject(row.Task.ProjectId)));
    public bool HasEmptyTaskBoard => TaskBoardId != Guid.Empty && !Projects.Any(project => IsTaskBoardProject(project.Id));
    public bool HasNoWorkspaceTasks => !HasEmptyTaskBoard && !WorkspaceTasks.Any();
    private bool IsTaskBoardProject(Guid projectId) => TaskBoardId == Guid.Empty || _projectBoards.GetValueOrDefault(projectId) == TaskBoardId;
    public ObservableCollection<ActivityOption> Activities { get; } = [];
    public ObservableCollection<TaskOption> ManualTaskOptions { get; } = [];
    public ObservableCollection<ActivityGroupOption> ActivityGroups { get; } = [];
    public ObservableCollection<ProjectAdminRowViewModel> ProjectAdminRows { get; } = [];
    public ObservableCollection<BoardAdminRowViewModel> BoardAdminRows { get; } = [];
    public ObservableCollection<ActivityAdminRowViewModel> ActivityAdminRows { get; } = [];
    public ObservableCollection<ActivityGroupAdminRowViewModel> ActivityGroupAdminRows { get; } = [];
    public ObservableCollection<HistoryRowViewModel> HistoryItems { get; } = [];
    public ObservableCollection<SummaryRowViewModel> SummaryItems { get; } = [];
    public ObservableCollection<CalendarBlockRowViewModel> CalendarBlocks { get; } = [];
    public ObservableCollection<CalendarEventRowViewModel> CalendarEvents { get; } = [];
    public ObservableCollection<CalendarDayColumnViewModel> CalendarDayColumns { get; } = [];
    public ObservableCollection<DeletedCalendarEventRowViewModel> DeletedCalendarEvents { get; } = [];
    public ObservableCollection<CalendarOption> CalendarOptions { get; } = [];
    public ObservableCollection<CalendarAdminRowViewModel> CalendarAdminRows { get; } = [];
    public ObservableCollection<RecoverySessionRowViewModel> RecoverySessions { get; } = [];
    public ObservableCollection<ReviewRowViewModel> TodayRecentRows { get; } = [];
    public ObservableCollection<ReviewRowViewModel> TodayUpcomingRows { get; } = [];

    public ICommand RefreshCommand { get; }
    public ICommand CreateTaskCommand { get; }
    public ICommand StartFocusCommand { get; }
    public ICommand SelectSectionCommand { get; }
    public ICommand PlanNextTaskCommand { get; }
    public ICommand BackupCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand ExportCsvCommand { get; }
    public ICommand RestoreCommand { get; }
    public ICommand LoadMoreHistoryCommand { get; }
    public ICommand ShowTaskDetailsCommand { get; }
    public ICommand ManualTimeCommand { get; }
    public ICommand QuickManualTimeCommand { get; }
    public ICommand SelectCalendarViewCommand { get; }
    public ICommand SelectTaskViewCommand { get; }
    public ICommand SelectSummaryGroupingCommand { get; }
    public ICommand CreateProjectCommand { get; }
    public ICommand CreateBoardCommand { get; }
    public ICommand CreateActivityCommand { get; }
    public ICommand StartSelectedActivityCommand { get; }
    public ICommand CreateActivityGroupCommand { get; }
    public ICommand CreateCalendarEventCommand { get; }
    public ICommand CreateCalendarCommand { get; }
    public ICommand SaveSettingsCommand { get; }
    public ICommand ResetTimerPreferenceCommand { get; }

    public string WorkspaceName { get => _workspaceName; private set => SetField(ref _workspaceName, value); }
    public string CurrentSection { get => _currentSection; private set => SetField(ref _currentSection, value); }
    public string SectionTitle { get => _sectionTitle; private set => SetField(ref _sectionTitle, value); }
    public string SectionSubtitle { get => _sectionSubtitle; private set => SetField(ref _sectionSubtitle, value); }
    public bool IsTodayVisible => CurrentSection == "Today";
    public bool IsTasksVisible => CurrentSection == "Tasks";
    public bool IsTimeTrackerVisible => CurrentSection == "Time Tracker";
    public bool IsTaskListVisible => IsTasksVisible && TaskViewMode == "List";
    public bool IsTaskBoardVisible => IsTasksVisible && TaskViewMode == "Board";
    public bool IsHistoryVisible => CurrentSection == "History";
    public bool IsSummaryVisible => CurrentSection == "Summary";
    public bool IsCalendarVisible => CurrentSection == "Calendar";
    public bool IsSettingsVisible => CurrentSection == "Settings";
    public string CalendarView
    {
        get => _calendarView;
        private set
        {
            if (!SetField(ref _calendarView, value)) return;
            RaisePropertyChanged(nameof(IsCalendarDay));
            RaisePropertyChanged(nameof(IsCalendarWeek));
            RaisePropertyChanged(nameof(IsCalendarMonth));
            RaisePropertyChanged(nameof(IsCalendarAgenda));
            RaisePropertyChanged(nameof(IsCalendarFlex));
            RaisePropertyChanged(nameof(IsCalendarGridVisible));
            RaisePropertyChanged(nameof(CalendarGridColumns));
            RaisePropertyChanged(nameof(IsCalendarTimelineVisible));
        }
    }
    public bool IsCalendarDay => CalendarView == "Day";
    public bool IsCalendarWeek => CalendarView == "Week";
    public bool IsCalendarMonth => CalendarView == "Month";
    public bool IsCalendarAgenda => CalendarView == "Agenda";
    public bool IsCalendarGridVisible => !IsCalendarAgenda;
    public int CalendarGridColumns => IsCalendarDay ? 1 : 7;
    public string SummaryGrouping
    {
        get => _summaryGrouping;
        private set
        {
            if (!SetField(ref _summaryGrouping, value)) return;
            foreach (var group in new[] { "Day", "Task", "Project", "Activity", "ActivityGroup", "Tag", "Lane" })
                RaisePropertyChanged("IsSummary" + group);
            RaisePropertyChanged(nameof(SummaryGroupingLabel));
        }
    }
    public bool IsSummaryDay => SummaryGrouping == "Day";
    public bool IsSummaryTask => SummaryGrouping == "Task";
    public bool IsSummaryProject => SummaryGrouping == "Project";
    public bool IsSummaryActivity => SummaryGrouping == "Activity";
    public bool IsSummaryActivityGroup => SummaryGrouping == "ActivityGroup";
    public bool IsSummaryTag => SummaryGrouping == "Tag";
    public bool IsSummaryLane => SummaryGrouping == "Lane";
    public string SummaryGroupingLabel => "By " + (SummaryGrouping == "ActivityGroup" ? "activity group" : SummaryGrouping.ToLowerInvariant());
    public string TaskFilterLabel
    {
        get
        {
            var count = (ShowCompleted ? 1 : 0) + (ShowArchived ? 1 : 0) + (ShowDeleted ? 1 : 0);
            return count > 0 ? $"Filters · {count}" : "Filters";
        }
    }
    public string TaskViewMode { get => _taskViewMode; private set { if (SetField(ref _taskViewMode, value)) { RaisePropertyChanged(nameof(IsTaskListVisible)); RaisePropertyChanged(nameof(IsTaskBoardVisible)); } } }
    public string TaskSortMode { get => _taskSortMode; set { if (SetField(ref _taskSortMode, value)) _ = LoadTasksAsync(); } }
    public IReadOnlyList<TaskSortOption> TaskSortOptions { get; } =
    [
        new("Priority", "Priority"),
        new("DueDate", "Deadline"),
        new("Tracked", "Tracked time"),
        new("Title", "Title")
    ];
    public bool HasNoHistory { get => _hasNoHistory; private set => SetField(ref _hasNoHistory, value); }
    public bool HistoryHasMore { get => _historyHasMore; private set { if (SetField(ref _historyHasMore, value)) ((AsyncCommand)LoadMoreHistoryCommand).RaiseCanExecuteChanged(); } }
    public TaskDetailsPanelViewModel? SelectedTaskDetails { get => _selectedTaskDetails; private set { if (SetField(ref _selectedTaskDetails, value)) RaisePropertyChanged(nameof(HasSelectedTaskDetails)); } }
    public bool HasSelectedTaskDetails => SelectedTaskDetails is not null;
    public bool HasNoSummary { get => _hasNoSummary; private set => SetField(ref _hasNoSummary, value); }
    public bool HasNoCalendarBlocks { get => _hasNoCalendarBlocks; private set => SetField(ref _hasNoCalendarBlocks, value); }
    public bool ShowCompleted { get => _showCompleted; set { if (SetField(ref _showCompleted, value)) { RaisePropertyChanged(nameof(TaskFilterLabel)); _ = LoadTasksAsync(); } } }
    public bool ShowArchived { get => _showArchived; set { if (SetField(ref _showArchived, value)) { RaisePropertyChanged(nameof(TaskFilterLabel)); _ = LoadTasksAsync(); } } }
    public bool ShowDeleted { get => _showDeleted; set { if (SetField(ref _showDeleted, value)) { RaisePropertyChanged(nameof(TaskFilterLabel)); _ = LoadTasksAsync(); } } }
    public bool HasNoTasks { get => _hasNoTasks; private set => SetField(ref _hasNoTasks, value); }
    public string TaskTitle { get => _taskTitle; set { if (SetField(ref _taskTitle, value)) ((AsyncCommand)CreateTaskCommand).RaiseCanExecuteChanged(); } }
    public string SearchText { get => _searchText; set { if (SetField(ref _searchText, value)) _ = RefreshAsync(); } }
    public string StatusMessage { get => _statusMessage; private set => SetField(ref _statusMessage, value); }
    public string TrackedToday { get => _trackedToday; private set => SetField(ref _trackedToday, value); }
    public int OpenTaskCount { get => _openTaskCount; private set => SetField(ref _openTaskCount, value); }
    public bool HasNoActiveSessions { get => _hasNoActiveSessions; private set => SetField(ref _hasNoActiveSessions, value); }
    private bool _showDeletedActivities;
    private bool _showDeletedBoards;
    public bool ShowDeletedActivities
    {
        get => _showDeletedActivities;
        set
        {
            if (!SetField(ref _showDeletedActivities, value)) return;
            RaisePropertyChanged(nameof(VisibleActivityAdminRows));
            RaisePropertyChanged(nameof(HasNoActivities));
        }
    }
    public bool ShowDeletedBoards
    {
        get => _showDeletedBoards;
        set
        {
            if (SetField(ref _showDeletedBoards, value)) RaisePropertyChanged(nameof(VisibleBoardAdminRows));
        }
    }
    public IEnumerable<ActivityAdminRowViewModel> VisibleActivityAdminRows => ActivityAdminRows.Where(row => ShowDeletedActivities || !row.IsDeleted);
    public IEnumerable<BoardAdminRowViewModel> VisibleBoardAdminRows => BoardAdminRows.Where(row => ShowDeletedBoards || !row.IsDeleted);
    public bool HasNoActivities => !VisibleActivityAdminRows.Any();
    public bool HasNoTodayRecentRows { get => _hasNoTodayRecentRows; private set => SetField(ref _hasNoTodayRecentRows, value); }
    public bool HasNoTodayUpcomingRows { get => _hasNoTodayUpcomingRows; private set => SetField(ref _hasNoTodayUpcomingRows, value); }
    public bool HasDeletedCalendarEvents { get => _hasDeletedCalendarEvents; private set => SetField(ref _hasDeletedCalendarEvents, value); }
    public bool HasRecoverySessions => RecoverySessions.Count > 0;
    public Guid SelectedProjectId { get => _selectedProjectId; set { if (SetField(ref _selectedProjectId, value)) ((AsyncCommand)CreateTaskCommand).RaiseCanExecuteChanged(); } }
    public Guid SelectedBoardId { get => _selectedBoardId; set { if (SetField(ref _selectedBoardId, value)) ((AsyncCommand)CreateProjectCommand).RaiseCanExecuteChanged(); } }
    public Guid SelectedCalendarId { get => _selectedCalendarId; set { if (SetField(ref _selectedCalendarId, value)) ((AsyncCommand)CreateCalendarEventCommand).RaiseCanExecuteChanged(); } }
    public string NewProjectName { get => _newProjectName; set { if (SetField(ref _newProjectName, value)) ((AsyncCommand)CreateProjectCommand).RaiseCanExecuteChanged(); } }
    public string NewBoardName { get => _newBoardName; set { if (SetField(ref _newBoardName, value)) ((AsyncCommand)CreateBoardCommand).RaiseCanExecuteChanged(); } }
    public string NewActivityName { get => _newActivityName; set { if (SetField(ref _newActivityName, value)) ((AsyncCommand)CreateActivityCommand).RaiseCanExecuteChanged(); } }
    public string NewActivityDescription { get => _newActivityDescription; set => SetField(ref _newActivityDescription, value); }
    public SessionLane NewActivityLane { get => _newActivityLane; set => SetField(ref _newActivityLane, value); }
    public Guid NewActivityGroupId { get => _newActivityGroupId; set => SetField(ref _newActivityGroupId, value); }
    public Guid SelectedTrackingActivityId { get => _selectedTrackingActivityId; set { if (SetField(ref _selectedTrackingActivityId, value)) ((AsyncCommand)StartSelectedActivityCommand).RaiseCanExecuteChanged(); } }
    public IReadOnlyList<SessionLaneOption> SessionLaneOptions { get; } =
    [
        new(SessionLane.Foreground, "Focus"),
        new(SessionLane.Background, "Background")
    ];
    public string NewActivityGroupName { get => _newActivityGroupName; set { if (SetField(ref _newActivityGroupName, value)) ((AsyncCommand)CreateActivityGroupCommand).RaiseCanExecuteChanged(); } }
    public string NewEventTitle { get => _newEventTitle; set { if (SetField(ref _newEventTitle, value)) ((AsyncCommand)CreateCalendarEventCommand).RaiseCanExecuteChanged(); } }
    public string NewEventDescription { get => _newEventDescription; set => SetField(ref _newEventDescription, value); }
    public string NewEventLocation { get => _newEventLocation; set => SetField(ref _newEventLocation, value); }
    public string NewEventColor { get => _newEventColor; set => SetField(ref _newEventColor, value); }
    public string NewEventRecurrence { get => _newEventRecurrence; set => SetField(ref _newEventRecurrence, value); }
    public string NewEventRecurrenceEnd { get => _newEventRecurrenceEnd; set => SetField(ref _newEventRecurrenceEnd, value); }
    public bool NewEventAllDay { get => _newEventAllDay; set => SetField(ref _newEventAllDay, value); }
    public string NewEventStart { get => _newEventStart; set => SetField(ref _newEventStart, value); }
    public string NewEventEnd { get => _newEventEnd; set => SetField(ref _newEventEnd, value); }
    public string NewCalendarName { get => _newCalendarName; set { if (SetField(ref _newCalendarName, value)) ((AsyncCommand)CreateCalendarCommand).RaiseCanExecuteChanged(); } }
    public string NewCalendarColor { get => _newCalendarColor; set => SetField(ref _newCalendarColor, value); }
    public bool AllowConcurrentForeground
    {
        get => _allowConcurrentForeground;
        set { if (SetField(ref _allowConcurrentForeground, value)) _timerPreferenceDirty = true; }
    }
    public Guid? ManualTaskId { get => _manualTaskId; set => SetField(ref _manualTaskId, value); }
    public Guid? ManualActivityId { get => _manualActivityId; set => SetField(ref _manualActivityId, value); }
    public string ManualStartText { get => _manualStartText; set => SetField(ref _manualStartText, value); }
    public string ManualEndText { get => _manualEndText; set => SetField(ref _manualEndText, value); }
    public string ManualNotes { get => _manualNotes; set => SetField(ref _manualNotes, value); }

    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        try
        {
            var bootstrap = await _backend.GetBootstrapAsync();
            WorkspaceName = bootstrap.Workspace.Name;
            if (bootstrap.Settings is not null && !_timerPreferenceDirty)
            {
                SetField(ref _allowConcurrentForeground, bootstrap.Settings.AllowConcurrentForeground, nameof(AllowConcurrentForeground));
                _settingsRevision = bootstrap.Settings.Revision;
            }
            _todayMilliseconds = bootstrap.Today.TrackedTodayMilliseconds;
            _todayClockSnapshot = bootstrap.Today;
            TrackedToday = FormatDuration(_todayMilliseconds);
            OpenTaskCount = bootstrap.Today.OpenTaskCount;

            var selectedBoardOption = Boards.FirstOrDefault(board => board.Id == SelectedBoardId);
            var selectedProjectOption = Projects.FirstOrDefault(project => project.Id == SelectedProjectId);
            ReconcileOptions(Boards, bootstrap.Boards.Where(item => item.ArchivedAtUtc is null && item.DeletedAtUtc is null)
                .Select(item => new BoardOption(item.Id, item.Name)), item => item.Id);
            _projectBoards = bootstrap.Projects.Concat(bootstrap.DeletedItems?.Projects ?? [])
                .DistinctBy(project => project.Id).ToDictionary(project => project.Id, project => project.BoardId);
            ReconcileOptions(TaskBoardOptions, new[] { new BoardOption(Guid.Empty, "All boards") }.Concat(Boards), item => item.Id);
            if (!TaskBoardOptions.Any(board => board.Id == TaskBoardId)) TaskBoardId = Guid.Empty;
            ReconcileOptions(Projects, bootstrap.Projects.Where(item => item.ArchivedAtUtc is null && item.DeletedAtUtc is null && Boards.Any(board => board.Id == item.BoardId))
                .OrderBy(item => Array.FindIndex(bootstrap.Boards.ToArray(), board => board.Id == item.BoardId))
                .ThenBy(item => item.SortKey)
                .Select(item => new ProjectOption(item.Id, item.Name, item.BoardId,
                    bootstrap.Boards.FirstOrDefault(board => board.Id == item.BoardId)?.Name ?? "Board")), item => item.Id);
            ReconcileOptions(CaptureProjectOptions, Projects.GroupBy(project => project.BoardId).SelectMany(group =>
                new[] { new TaskPickerEntry(group.Key, group.First().BoardName, "", true) }
                    .Concat(group.Select(project => new TaskPickerEntry(project.Id, project.Name, project.BoardName)))),
                item => item.Id);

            // Replacing a renamed option can clear a ComboBox's displayed selection
            // even when its selected ID has not changed. Reapply only replaced choices.
            if (selectedBoardOption is not null && Boards.FirstOrDefault(board => board.Id == selectedBoardOption.Id) is { } renamedBoard
                && !ReferenceEquals(selectedBoardOption, renamedBoard))
            {
                SelectedBoardId = Guid.Empty;
                SelectedBoardId = renamedBoard.Id;
            }
            if (selectedProjectOption is not null && Projects.FirstOrDefault(project => project.Id == selectedProjectOption.Id) is { } renamedProject
                && !ReferenceEquals(selectedProjectOption, renamedProject))
            {
                SelectedProjectId = Guid.Empty;
                SelectedProjectId = renamedProject.Id;
            }

            if (!Boards.Any(board => board.Id == SelectedBoardId))
            {
                SelectedBoardId = Boards.Count == 0 ? Guid.Empty : Boards[0].Id;
            }

            ReconcileOptions(Activities, bootstrap.Activities.Where(item => item.ArchivedAtUtc is null && item.DeletedAtUtc is null)
                .Select(item => new ActivityOption(item.Id, item.Name, item.DefaultLane)), item => item.Id);
            if (!Activities.Any(activity => activity.Id == SelectedTrackingActivityId))
            {
                SelectedTrackingActivityId = Activities.Count == 0 ? Guid.Empty : Activities[0].Id;
            }

            ReconcileOptions(ManualTaskOptions,
                (await _backend.SearchTasksAsync(includeCompleted: true, includeArchived: true))
                    .Select(task => new TaskOption(task.Task.Id, task.Task.Title)), task => task.Id);

            ReconcileOptions(ActivityGroups,
                new[] { new ActivityGroupOption(Guid.Empty, "No group") }.Concat(
                    (bootstrap.ActivityGroups ?? []).Where(item => item.DeletedAtUtc is null)
                        .Select(group => new ActivityGroupOption(group.Id, group.Name))), group => group.Id);

            ActivityGroupAdminRows.Clear();
            foreach (var group in (bootstrap.ActivityGroups ?? []).Concat(bootstrap.DeletedItems?.ActivityGroups ?? []))
            {
                ActivityGroupAdminRows.Add(new ActivityGroupAdminRowViewModel(group, SaveActivityGroupAsync, DeleteActivityGroupAsync, ReorderActivityGroupAsync));
            }

            CalendarAdminRows.Clear();
            ReconcileOptions(CalendarOptions, bootstrap.Calendars.Where(item => item.Visible && item.DeletedAtUtc is null)
                .Select(item => new CalendarOption(item.Id, item.Name)), item => item.Id);

            foreach (var calendar in bootstrap.Calendars.Concat(bootstrap.DeletedItems?.Calendars ?? []))
            {
                CalendarAdminRows.Add(new CalendarAdminRowViewModel(calendar, SaveCalendarAsync, DeleteCalendarAsync));
            }

            DeletedCalendarEvents.Clear();
            foreach (var item in bootstrap.DeletedItems?.CalendarEvents ?? [])
            {
                DeletedCalendarEvents.Add(new DeletedCalendarEventRowViewModel(item, RestoreDeletedCalendarEventAsync));
            }
            HasDeletedCalendarEvents = DeletedCalendarEvents.Count > 0;

            ProjectAdminRows.Clear();
            foreach (var project in bootstrap.Projects.Concat(bootstrap.DeletedItems?.Projects ?? []))
            {
                ProjectAdminRows.Add(new ProjectAdminRowViewModel(project, SaveProjectAsync, ToggleProjectArchiveAsync, DeleteProjectAsync, AddProjectTagAsync, ReorderProjectAsync));
            }

            BoardAdminRows.Clear();
            foreach (var board in bootstrap.Boards.Concat(bootstrap.DeletedItems?.Boards ?? []))
            {
                BoardAdminRows.Add(new BoardAdminRowViewModel(board, SaveBoardAsync, ToggleBoardArchiveAsync, DeleteBoardAsync, ReorderBoardAsync));
            }
            NotifyBoardNavigation();
            RaisePropertyChanged(nameof(VisibleBoardAdminRows));

            ActivityAdminRows.Clear();
            foreach (var activity in bootstrap.Activities.Concat(bootstrap.DeletedItems?.Activities ?? []))
            {
                ActivityAdminRows.Add(new ActivityAdminRowViewModel(activity, ActivityGroups, SaveActivityAsync, ToggleActivityArchiveAsync, DeleteActivityAsync, AddActivityTagAsync, StartActivityAsync));
            }
            RaisePropertyChanged(nameof(HasNoActivities));
            RaisePropertyChanged(nameof(VisibleActivityAdminRows));

            if (!Projects.Any(project => project.Id == SelectedProjectId))
            {
                SelectedProjectId = Projects.FirstOrDefault(project => !IsTasksVisible || IsTaskBoardProject(project.Id))?.Id ?? Guid.Empty;
            }
            if (!IsPlanEditor && !IsNewEventEditor && !CalendarOptions.Any(calendar => calendar.Id == SelectedCalendarId))
            {
                SelectedCalendarId = CalendarOptions.Count == 0 ? Guid.Empty : CalendarOptions[0].Id;
            }
            ((AsyncCommand)CreateCalendarEventCommand).RaiseCanExecuteChanged();

            ActiveSessions.Clear();
            foreach (var item in bootstrap.Today.ActiveSessions)
            {
                ActiveSessions.Add(new ActiveSessionRowViewModel(item, PauseResumeAsync, StopAsync));
            }
            HasNoActiveSessions = ActiveSessions.Count == 0;
            RefreshTracker(bootstrap);

            RecoverySessions.Clear();
            foreach (var session in bootstrap.Today.RecoverySessions ?? [])
            {
                RecoverySessions.Add(new RecoverySessionRowViewModel(session, ResolveRecoveryAsync));
            }
            RaisePropertyChanged(nameof(HasRecoverySessions));

            await LoadTasksAsync();
            if (IsTodayVisible)
            {
                await LoadTodayReviewAsync();
            }

            if (!IsTaskEditorOpen && !IsUtilityEditorOpen) StatusMessage = "All changes are saved locally.";
            if (IsHistoryVisible)
            {
                await LoadHistoryAsync();
            }
            else if (IsCalendarVisible)
            {
                await LoadCalendarAsync(bootstrap);
            }
            else if (IsSummaryVisible)
            {
                await LoadSummaryAsync();
            }
        }
        catch (Exception exception)
        {
            StatusMessage = exception is SnookException snook ? snook.Message : "Snook could not refresh the workspace.";
        }
    }

    private async Task CreateTaskAsync()
    {
        try
        {
            await _backend.CreateTaskAsync(SelectedProjectId, TaskTitle.Trim());
            TaskTitle = string.Empty;
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            StatusMessage = exception is SnookException snook ? snook.Message : "The task could not be created.";
        }
    }

    private async Task CreateProjectAsync()
    {
        try
        {
            var project = await _backend.CreateProjectAsync(SelectedBoardId, NewProjectName.Trim());
            NewProjectName = string.Empty;
            await RefreshAsync();
            SelectedProjectId = project.Id;
        }
        catch (Exception exception)
        {
            StatusMessage = exception is SnookException snook ? snook.Message : "The project could not be created.";
        }
    }

    private async Task CreateBoardAsync()
    {
        try
        {
            var board = await _backend.CreateBoardAsync(NewBoardName.Trim());
            NewBoardName = string.Empty;
            await RefreshAsync();
            SelectedBoardId = board.Id;
            if (IsTasksVisible)
            {
                TaskBoardId = board.Id;
                TaskViewMode = "Board";
            }
        }
        catch (Exception exception)
        {
            StatusMessage = exception is SnookException snook ? snook.Message : "The board could not be created.";
        }
    }

    private async Task CreateActivityAsync()
    {
        try
        {
            var activity = await _backend.CreateActivityAsync(
                NewActivityName.Trim(),
                NewActivityDescription.Trim(),
                NewActivityLane,
                NewActivityGroupId == Guid.Empty ? null : NewActivityGroupId);
            NewActivityName = string.Empty;
            NewActivityDescription = string.Empty;
            FinishUtilityEdit("activity");
            await RefreshAsync();
            SelectedTrackingActivityId = activity.Id;
        }
        catch (Exception exception)
        {
            StatusMessage = exception is SnookException snook ? snook.Message : "The activity could not be created.";
        }
    }

    private async Task CreateActivityGroupAsync()
    {
        try
        {
            await _backend.CreateActivityGroupAsync(NewActivityGroupName.Trim());
            NewActivityGroupName = string.Empty;
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            StatusMessage = exception is SnookException snook ? snook.Message : "The activity group could not be created.";
        }
    }

    private async Task SaveActivityGroupAsync(ActivityGroupAdminRowViewModel row)
    {
        try
        {
            await _backend.UpdateActivityGroupAsync(row.Group.Id, new ActivityGroupUpdate(row.DraftName), new OperationRequest(Guid.NewGuid(), Guid.NewGuid(), row.Group.Revision));
            FinishUtilityEdit(row);
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            StatusMessage = EditorFailure(exception, "The activity group could not be updated.");
        }
    }

    private async Task ReorderActivityGroupAsync(ActivityGroupAdminRowViewModel row, ReorderDirection direction)
    {
        try
        {
            await _backend.ReorderActivityGroupAsync(row.Group.Id, direction, new OperationRequest(Guid.NewGuid(), Guid.NewGuid(), row.Group.Revision));
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            StatusMessage = exception is SnookException snook ? snook.Message : "The activity group order could not be changed.";
        }
    }

    private async Task DeleteActivityGroupAsync(ActivityGroupAdminRowViewModel row)
    {
        try
        {
            var request = new OperationRequest(Guid.NewGuid(), Guid.NewGuid(), row.Group.Revision);
            if (row.Group.DeletedAtUtc is null)
            {
                await _backend.DeleteActivityGroupAsync(row.Group.Id, request);
                StatusMessage = "Activity group moved to deleted items.";
            }
            else
            {
                await _backend.RestoreDeletedActivityGroupAsync(row.Group.Id, request);
                StatusMessage = "Activity group restored.";
            }

            await RefreshAsync();
        }
        catch (Exception exception)
        {
            StatusMessage = exception is SnookException snook ? snook.Message : "The activity group deleted state could not be changed.";
        }
    }

    private async Task CreateCalendarAsync()
    {
        try
        {
            await _backend.CreateCalendarAsync(NewCalendarName.Trim(), NewCalendarColor.Trim());
            NewCalendarName = string.Empty;
            StatusMessage = "Calendar created.";
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            StatusMessage = exception is SnookException snook ? snook.Message : "The calendar could not be created.";
        }
    }

    private async Task SaveCalendarAsync(CalendarAdminRowViewModel row)
    {
        try
        {
            await _backend.UpdateCalendarAsync(
                row.Calendar.Id,
                new CalendarUpdate(row.DraftName, row.DraftColor, row.IsVisible),
                new OperationRequest(Guid.NewGuid(), Guid.NewGuid(), row.Calendar.Revision));
            StatusMessage = "Calendar updated.";
            FinishUtilityEdit(row);
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            StatusMessage = EditorFailure(exception, "The calendar could not be updated.");
        }
    }

    private async Task DeleteCalendarAsync(CalendarAdminRowViewModel row)
    {
        try
        {
            var request = new OperationRequest(Guid.NewGuid(), Guid.NewGuid(), row.Calendar.Revision);
            if (row.Calendar.DeletedAtUtc is null)
            {
                await _backend.DeleteCalendarAsync(row.Calendar.Id, request);
                StatusMessage = "Calendar moved to deleted items.";
            }
            else
            {
                await _backend.RestoreDeletedCalendarAsync(row.Calendar.Id, request);
                StatusMessage = "Calendar restored.";
            }

            await RefreshAsync();
        }
        catch (Exception exception)
        {
            StatusMessage = exception is SnookException snook ? snook.Message : "The calendar deleted state could not be changed.";
        }
    }

    private async Task RestoreDeletedCalendarEventAsync(DeletedCalendarEventRowViewModel row)
    {
        try
        {
            await _backend.RestoreDeletedCalendarEventAsync(row.Event.Id, new OperationRequest(Guid.NewGuid(), Guid.NewGuid(), row.Event.Revision));
            StatusMessage = "Calendar event restored.";
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            StatusMessage = exception is SnookException snook ? snook.Message : "The calendar event could not be restored.";
        }
    }

    private async Task SaveSettingsAsync()
    {
        try
        {
            var updated = await _backend.UpdateSettingsAsync(
                new WorkspaceSettings(AllowConcurrentForeground, _settingsRevision),
                new OperationRequest(Guid.NewGuid(), Guid.NewGuid(), _settingsRevision));
            _settingsRevision = updated.Revision;
            _timerPreferenceDirty = false;
            StatusMessage = "Workspace settings saved.";
        }
        catch (Exception exception)
        {
            StatusMessage = exception is SnookException { Code: SnookErrorCode.RevisionConflict }
                ? "The timer preference changed elsewhere. Use Reset to saved, then make your change again."
                : exception is SnookException snook ? snook.Message : "Workspace settings could not be saved.";
        }
    }

    private async Task SaveProjectAsync(ProjectAdminRowViewModel row)
    {
        try
        {
            await _backend.UpdateProjectAsync(row.Project.Id, new ProjectUpdate(row.DraftName, row.Project.Description, row.DraftStarred), new OperationRequest(Guid.NewGuid(), Guid.NewGuid(), row.Project.Revision));
            FinishUtilityEdit(row);
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            StatusMessage = EditorFailure(exception, "The project could not be updated.");
        }
    }

    private async Task ReorderProjectAsync(ProjectAdminRowViewModel row, ReorderDirection direction)
    {
        try
        {
            await _backend.ReorderProjectAsync(row.Project.Id, direction, new OperationRequest(Guid.NewGuid(), Guid.NewGuid(), row.Project.Revision));
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            StatusMessage = exception is SnookException snook ? snook.Message : "The project order could not be changed.";
        }
    }

    private async Task AddProjectTagAsync(ProjectAdminRowViewModel row)
    {
        if (string.IsNullOrWhiteSpace(row.TagText))
        {
            return;
        }

        try
        {
            await _backend.AddProjectTagAsync(row.Project.Id, row.TagText.Trim());
            row.TagText = string.Empty;
            StatusMessage = "Project tag added.";
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            StatusMessage = exception is SnookException snook ? snook.Message : "The project tag could not be added.";
        }
    }

    private async Task SaveBoardAsync(BoardAdminRowViewModel row)
    {
        try
        {
            await _backend.UpdateBoardAsync(row.Board.Id, new BoardUpdate(row.DraftName), new OperationRequest(Guid.NewGuid(), Guid.NewGuid(), row.Board.Revision));
            FinishUtilityEdit(row);
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            StatusMessage = EditorFailure(exception, "The board could not be updated.");
        }
    }

    private async Task ReorderBoardAsync(BoardAdminRowViewModel row, ReorderDirection direction)
    {
        try
        {
            await _backend.ReorderBoardAsync(row.Board.Id, direction, new OperationRequest(Guid.NewGuid(), Guid.NewGuid(), row.Board.Revision));
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            StatusMessage = exception is SnookException snook ? snook.Message : "The board order could not be changed.";
        }
    }

    private async Task ToggleBoardArchiveAsync(BoardAdminRowViewModel row)
    {
        try
        {
            var request = new OperationRequest(Guid.NewGuid(), Guid.NewGuid(), row.Board.Revision);
            if (row.Board.ArchivedAtUtc is null)
            {
                await _backend.ArchiveBoardAsync(row.Board.Id, request);
            }
            else
            {
                await _backend.RestoreBoardAsync(row.Board.Id, request);
            }

            await RefreshAsync();
        }
        catch (Exception exception)
        {
            StatusMessage = exception is SnookException snook ? snook.Message : "The board archive state could not be changed.";
        }
    }

    private async Task DeleteBoardAsync(BoardAdminRowViewModel row)
    {
        try
        {
            var request = new OperationRequest(Guid.NewGuid(), Guid.NewGuid(), row.Board.Revision);
            if (row.Board.DeletedAtUtc is null)
            {
                await _backend.DeleteBoardAsync(row.Board.Id, request);
                StatusMessage = "Board moved to deleted items.";
            }
            else
            {
                await _backend.RestoreDeletedBoardAsync(row.Board.Id, request);
                StatusMessage = "Board restored.";
            }

            await RefreshAsync();
        }
        catch (Exception exception)
        {
            StatusMessage = exception is SnookException snook ? snook.Message : "The board deleted state could not be changed.";
        }
    }

    private async Task ToggleProjectArchiveAsync(ProjectAdminRowViewModel row)
    {
        try
        {
            var request = new OperationRequest(Guid.NewGuid(), Guid.NewGuid(), row.Project.Revision);
            if (row.Project.ArchivedAtUtc is null)
            {
                await _backend.ArchiveProjectAsync(row.Project.Id, request);
            }
            else
            {
                await _backend.RestoreProjectAsync(row.Project.Id, request);
            }

            await RefreshAsync();
        }
        catch (Exception exception)
        {
            StatusMessage = exception is SnookException snook ? snook.Message : "The project archive state could not be changed.";
        }
    }

    private async Task DeleteProjectAsync(ProjectAdminRowViewModel row)
    {
        try
        {
            var request = new OperationRequest(Guid.NewGuid(), Guid.NewGuid(), row.Project.Revision);
            if (row.Project.DeletedAtUtc is null)
            {
                await _backend.DeleteProjectAsync(row.Project.Id, request);
                StatusMessage = "Project moved to deleted items.";
            }
            else
            {
                await _backend.RestoreDeletedProjectAsync(row.Project.Id, request);
                StatusMessage = "Project restored.";
            }

            await RefreshAsync();
        }
        catch (Exception exception)
        {
            StatusMessage = exception is SnookException snook ? snook.Message : "The project deleted state could not be changed.";
        }
    }

    private async Task SaveActivityAsync(ActivityAdminRowViewModel row)
    {
        try
        {
            await _backend.UpdateActivityAsync(row.Activity.Id, new ActivityUpdate(row.DraftName, row.DraftDescription, row.DraftLane, row.GroupId == Guid.Empty ? null : row.GroupId), new OperationRequest(Guid.NewGuid(), Guid.NewGuid(), row.Activity.Revision));
            FinishUtilityEdit(row);
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            StatusMessage = EditorFailure(exception, "The activity could not be updated.");
        }
    }

    private async Task AddActivityTagAsync(ActivityAdminRowViewModel row)
    {
        if (string.IsNullOrWhiteSpace(row.TagText))
        {
            return;
        }

        try
        {
            await _backend.AddActivityTagAsync(row.Activity.Id, row.TagText.Trim());
            row.TagText = string.Empty;
            StatusMessage = "Activity tag added.";
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            StatusMessage = exception is SnookException snook ? snook.Message : "The activity tag could not be added.";
        }
    }

    private async Task ToggleActivityArchiveAsync(ActivityAdminRowViewModel row)
    {
        try
        {
            var request = new OperationRequest(Guid.NewGuid(), Guid.NewGuid(), row.Activity.Revision);
            if (row.Activity.ArchivedAtUtc is null)
            {
                await _backend.ArchiveActivityAsync(row.Activity.Id, request);
            }
            else
            {
                await _backend.RestoreActivityAsync(row.Activity.Id, request);
            }

            await RefreshAsync();
        }
        catch (Exception exception)
        {
            StatusMessage = exception is SnookException snook ? snook.Message : "The activity archive state could not be changed.";
        }
    }

    private async Task DeleteActivityAsync(ActivityAdminRowViewModel row)
    {
        try
        {
            var request = new OperationRequest(Guid.NewGuid(), Guid.NewGuid(), row.Activity.Revision);
            if (row.Activity.DeletedAtUtc is null)
            {
                await _backend.DeleteActivityAsync(row.Activity.Id, request);
                StatusMessage = "Activity moved to deleted items.";
            }
            else
            {
                await _backend.RestoreDeletedActivityAsync(row.Activity.Id, request);
                StatusMessage = "Activity restored.";
            }

            await RefreshAsync();
        }
        catch (Exception exception)
        {
            StatusMessage = exception is SnookException snook ? snook.Message : "The activity deleted state could not be changed.";
        }
    }

    private async Task SelectSectionAsync(string section)
    {
        var normalized = section switch
        {
            "History" => "History",
            "Summary" => "Summary",
            "Calendar" => "Calendar",
            "Tasks" => "Tasks",
            "Time Tracker" => "Time Tracker",
            "Settings" => "Settings",
            _ => "Today"
        };
        CurrentSection = normalized;
        SectionTitle = normalized switch
        {
            "History" => "Time history",
            "Summary" => "Time summary",
            "Tasks" => "Your tasks",
            "Time Tracker" => "Time tracker",
            "Calendar" => "Make room for the work",
            "Settings" => "Workspace settings",
            _ => "Welcome back"
        };
        SectionSubtitle = normalized switch
        {
            "History" => "Revisit your sessions and keep your time accurate.",
            "Summary" => "A clearer picture of the last 30 days.",
            "Tasks" => "A clear place for everything you want to do.",
            "Time Tracker" => "Start tasks or standalone activities, then switch without losing your place.",
            "Calendar" => "Plan your work or see how your days were spent.",
            "Settings" => "Make space for the way you work.",
            _ => "A little progress, thoughtfully recorded."
        };
        RaisePropertyChanged(nameof(IsTodayVisible));
        RaisePropertyChanged(nameof(IsTasksVisible));
        RaisePropertyChanged(nameof(IsTimeTrackerVisible));
        RaisePropertyChanged(nameof(HasPageSearch));
        RaisePropertyChanged(nameof(SearchHint));
        RaisePropertyChanged(nameof(IsTaskListVisible));
        RaisePropertyChanged(nameof(IsTaskBoardVisible));
        RaisePropertyChanged(nameof(IsHistoryVisible));
        RaisePropertyChanged(nameof(IsSummaryVisible));
        RaisePropertyChanged(nameof(IsCalendarVisible));
        RaisePropertyChanged(nameof(IsSettingsVisible));
        if (normalized == "History")
        {
            await LoadHistoryAsync();
        }
        else if (normalized == "Tasks")
        {
            await LoadTasksAsync();
        }
        else if (normalized == "Calendar")
        {
            await LoadCalendarAsync(await _backend.GetBootstrapAsync());
        }
        else if (normalized == "Summary")
        {
            await LoadSummaryAsync();
        }
    }

    // Kept separate from the normal command path so the screenshot harness can
    // select a deterministic variant without reaching into private state.
    public async Task SelectSectionForScreenshotAsync(string section, string? variant)
    {
        CloseTaskEditorCommand.Execute(null);
        CloseUtilityEditor();
        await SelectSectionAsync(section);
        if (section == "Tasks")
        {
            ShowStarredTasks = variant == "starred";
            ClearTaskSelection();
            await SelectTaskViewAsync(variant == "board" ? "Board" : "List");
            if (variant?.StartsWith("details", StringComparison.Ordinal) == true && Tasks.Count > 0)
                await ShowTaskDetailsAsync(Tasks[0]);
            if (variant == "bulk")
            {
                foreach (var row in WorkspaceTasks.Take(2)) row.IsSelected = true;
                OpenBulkTaskEditorCommand.Execute(null);
            }
        }
        else if (section == "Calendar" && variant is "day" or "week" or "month" or "agenda")
        {
            await SelectCalendarModeAsync("Event");
            await SelectCalendarViewAsync(CultureInfo.InvariantCulture.TextInfo.ToTitleCase(variant));
        }
        else if (section == "Calendar" && variant is "history-week" or "history-flex")
        {
            await SelectCalendarModeAsync("History");
            await SelectCalendarViewAsync(variant == "history-week" ? "Week" : "Flex");
        }
        else if (section == "Calendar" && variant is "details" or "plan")
            OpenUtilityEditor(variant == "plan" ? "plan" : "event");
        else if (section == "Time Tracker" && variant is "details-bottom" or "new-activity" or "manual")
            OpenUtilityEditor(variant == "manual" ? "manual" : "activity");
        else if (section == "History" && variant == "details" && HistoryItems.Count > 0)
            OpenUtilityEditor(HistoryItems.FirstOrDefault(row => row.Item.Session.State == SessionState.Stopped) ?? HistoryItems[0]);
        else if (section == "Settings")
        {
            SettingsPage = variant switch
            {
                "organization" or "details" or "details-bottom" or "board-editor" => "Organization",
                "activities" or "activities-bottom" or "activity-editor" => "Activities",
                "calendars" => "Calendars",
                _ => "General"
            };
            if (variant == "activity-editor" && ActivityAdminRows.Count > 0) OpenUtilityEditor(ActivityAdminRows[0]);
            if (variant == "board-editor" && BoardAdminRows.Count > 0) OpenUtilityEditor(BoardAdminRows[0]);
        }
    }

    private Task LoadMoreHistoryAsync() => LoadHistoryAsync(true);

    private async Task LoadTodayReviewAsync()
    {
        var localNow = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.Local);
        var localStart = LocalDateToUtc(localNow.Date);
        var localEnd = LocalDateToUtc(localNow.Date.AddDays(7));
        var upcoming = new List<(DateTimeOffset At, ReviewRowViewModel Row)>();
        TodayRecentRows.Clear();
        TodayUpcomingRows.Clear();

        foreach (var task in Tasks.Where(item => item.Task.DueDate is not null).OrderBy(item => item.Task.DueDate).Take(5))
        {
            upcoming.Add((LocalDateToUtc(task.Task.DueDate!.Value.ToDateTime(TimeOnly.MaxValue)), new ReviewRowViewModel(task.Title, $"Deadline · {task.DueLabel}")));
        }

        var taskItems = await _backend.SearchTasksAsync(includeCompleted: true);
        var taskNames = taskItems.ToDictionary(item => item.Task.Id, item => item.Task.Title);
        var activityNames = Activities.ToDictionary(item => item.Id, item => item.Name);
        foreach (var block in await _backend.GetCalendarRangeAsync(new CalendarRangeQuery(localStart, localEnd)))
        {
            if (block.EndAtUtc <= DateTimeOffset.UtcNow) continue;
            var label = block.TaskId is { } taskId && taskNames.TryGetValue(taskId, out var taskName)
                ? taskName
                : block.ActivityId is { } activityId && activityNames.TryGetValue(activityId, out var activityName)
                    ? activityName
                    : block.TitleOverride ?? "Planned work";
            upcoming.Add((block.StartAtUtc, new ReviewRowViewModel(label, $"Scheduled · {block.StartAtUtc.ToLocalTime():ddd, MMM d h:mm tt}")));
        }

        foreach (var item in await _backend.GetCalendarEventsRangeAsync(new CalendarRangeQuery(localStart, localEnd)))
        {
            if (item.EndAtUtc <= DateTimeOffset.UtcNow) continue;
            upcoming.Add((item.StartAtUtc, new ReviewRowViewModel(item.Title, item.AllDay
                ? $"Event · {item.StartAtUtc.ToLocalTime():ddd, MMM d} all day"
                : $"Event · {item.StartAtUtc.ToLocalTime():ddd, MMM d h:mm tt}")));
        }
        foreach (var item in upcoming.OrderBy(item => item.At)) TodayUpcomingRows.Add(item.Row);

        var history = await _backend.GetHistoryAsync(new HistoryQuery(localStart.AddDays(-7), DateTimeOffset.UtcNow, PageSize: 5));
        foreach (var item in history.Items)
        {
            var label = item.TaskTitle ?? item.ActivityName ?? "Tracked work";
            TodayRecentRows.Add(new ReviewRowViewModel(label, $"{item.Session.StartedAtUtc.ToLocalTime():ddd, MMM d h:mm tt} · {FormatForRow(item.AttributedMilliseconds)}"));
        }

        HasNoTodayRecentRows = TodayRecentRows.Count == 0;
        HasNoTodayUpcomingRows = TodayUpcomingRows.Count == 0;
        RaisePropertyChanged(nameof(TodayAgendaPreview));
    }

    private async Task LoadHistoryAsync(bool append = false)
    {
        var now = DateTimeOffset.UtcNow;
        if (!append)
        {
            _historyContinuation = null;
            HistoryItems.Clear();
        }

        var items = await _backend.GetHistoryAsync(new HistoryQuery(now.AddDays(-30), now.AddDays(1), SearchText, 50, _historyContinuation));
        var taskItems = await _backend.SearchTasksAsync(includeCompleted: true, includeArchived: true);
        var taskOptions = taskItems.Select(item => new TaskOption(item.Task.Id, item.Task.Title)).ToArray();
        foreach (var item in items.Items)
        {
            HistoryItems.Add(new HistoryRowViewModel(item, taskOptions, Activities, CorrectHistoryAsync));
        }
        HasNoHistory = HistoryItems.Count == 0;
        _historyContinuation = items.ContinuationToken;
        HistoryHasMore = items.HasMore;
    }

    private async Task LoadSummaryAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var grouping = Enum.TryParse<Snook.Domain.SummaryGrouping>(SummaryGrouping, true, out var parsed) ? parsed : Snook.Domain.SummaryGrouping.Tag;
        var buckets = await _backend.GetSummaryAsync(now.AddDays(-30), now.AddDays(1), grouping, TimeZoneInfo.Local.Id);
        SummaryItems.Clear();
        var largest = buckets.Select(bucket => bucket.AttributedMilliseconds).DefaultIfEmpty(0).Max();
        foreach (var bucket in buckets.OrderByDescending(bucket => bucket.AttributedMilliseconds))
        {
            SummaryItems.Add(new SummaryRowViewModel(bucket, largest));
        }

        HasNoSummary = SummaryItems.Count == 0;
    }

    private int _tasksLoadVersion;
    private async Task LoadTasksAsync()
    {
        var version = ++_tasksLoadVersion;
        var tasks = await _backend.SearchTasksAsync(SearchText, ShowCompleted, ShowArchived, ShowDeleted);
        if (version != _tasksLoadVersion) return;
        tasks = TaskSortMode switch
        {
            "DueDate" => tasks.OrderBy(item => item.Task.DueDate is null).ThenBy(item => item.Task.DueDate).ThenBy(item => item.Task.Title, StringComparer.OrdinalIgnoreCase).ToArray(),
            "Tracked" => tasks.OrderByDescending(item => item.TrackedMilliseconds).ThenBy(item => item.Task.Title, StringComparer.OrdinalIgnoreCase).ToArray(),
            "Title" => tasks.OrderBy(item => item.Task.Title, StringComparer.OrdinalIgnoreCase).ToArray(),
            _ => tasks.OrderByDescending(item => item.Task.Priority).ThenBy(item => item.Task.DueDate is null).ThenBy(item => item.Task.DueDate).ThenBy(item => item.Task.Title, StringComparer.OrdinalIgnoreCase).ToArray()
        };
        var prerequisiteOptions = tasks.Select(item => new TaskOption(item.Task.Id, item.Task.Title)).ToArray();
        Tasks.Clear();
        foreach (var task in tasks)
        {
            var row = new TaskRowViewModel(task, Projects, Activities, prerequisiteOptions, StartTaskAsync, CompleteTaskAsync, SaveTaskAsync, MoveTaskAsync, ArchiveTaskAsync, DeleteTaskAsync, AddTaskTagAsync, AddTaskDependencyAsync, AddTaskLinkAsync, ShowTaskDetailsAsync);
            row.IsSelected = _selectedTaskIds.Contains(task.Task.Id);
            row.PropertyChanged += OnTaskSelectionChanged;
            Tasks.Add(row);
        }

        RebuildTaskWorkspace();
        HasNoTasks = Tasks.Count == 0;
        RaisePropertyChanged(nameof(TodayNextTasks));
        RaisePropertyChanged(nameof(HasNoTodayNextTasks));
    }

    private void RebuildTaskWorkspace()
    {
        TaskGroups.Clear();
        var rows = WorkspaceTasks.ToArray();
        foreach (var project in Projects.Where(project => IsTaskBoardProject(project.Id)
            && (!ShowStarredTasks || IsStarredProject(project.Id) || rows.Any(row => row.Task.ProjectId == project.Id))))
        {
            TaskGroups.Add(new ProjectTaskGroupViewModel(project.Id, project.Name, rows.Where(row => row.Task.ProjectId == project.Id).ToArray(),
                ProjectAdminRows.FirstOrDefault(row => row.Project.Id == project.Id), project.BoardName));
        }
        // Search can explicitly include tasks whose project has been archived or deleted.
        foreach (var group in rows.Where(row => !Projects.Any(project => project.Id == row.Task.ProjectId)).GroupBy(row => row.Task.ProjectId))
            TaskGroups.Add(new ProjectTaskGroupViewModel(group.Key, group.First().ProjectName, group.ToArray(), boardName: group.First().Item.BoardName));
        RaisePropertyChanged(nameof(WorkspaceTasks));
        RaisePropertyChanged(nameof(HasEmptyTaskBoard));
        RaisePropertyChanged(nameof(HasNoWorkspaceTasks));
        RaisePropertyChanged(nameof(StarredProjects));
        ReconcileTaskSelection();
    }

    private async Task LoadCalendarAsync(BootstrapSnapshot bootstrap)
    {
        var version = ++_calendarLoadVersion;
        var localNow = _calendarAnchor;
        var localStart = CalendarView switch
        {
            "Day" => localNow.Date,
            "Month" => new DateTime(localNow.Year, localNow.Month, 1),
            "Agenda" => localNow.Date,
            "Flex" => localNow.Date.AddDays(-((_calendarFlexDays - 1) / 2)),
            _ => localNow.Date.AddDays(-(int)localNow.DayOfWeek + (localNow.DayOfWeek == DayOfWeek.Sunday ? -6 : 1))
        };
        var localEnd = CalendarView switch
        {
            "Day" => localStart.AddDays(1),
            "Month" => localStart.AddMonths(1),
            "Agenda" => localStart.AddDays(14),
            "Flex" => localStart.AddDays(_calendarFlexDays),
            _ => localStart.AddDays(7)
        };
        var gridStart = localStart;
        var rangeLabel = IsCalendarMonth ? localStart.ToString("MMMM yyyy", CultureInfo.CurrentCulture)
            : IsCalendarDay ? localStart.ToString("dddd, MMMM d, yyyy", CultureInfo.CurrentCulture)
            : $"{localStart:MMM d} – {localEnd.AddDays(-1):MMM d, yyyy}";
        var gridEnd = localEnd;
        if (IsCalendarMonth)
        {
            while (gridStart.DayOfWeek != DayOfWeek.Monday)
            {
                gridStart = gridStart.AddDays(-1);
            }

            while (gridEnd.DayOfWeek != DayOfWeek.Monday)
            {
                gridEnd = gridEnd.AddDays(1);
            }
        }

        var rangeStart = LocalDateToUtc(gridStart);
        var rangeEnd = LocalDateToUtc(gridEnd);
        var range = new CalendarRangeQuery(rangeStart, rangeEnd);
        var blocks = await _backend.GetCalendarRangeAsync(range);
        var events = IsCalendarHistoryMode ? [] : await _backend.GetCalendarEventsRangeAsync(range);
        var taskItems = await _backend.SearchTasksAsync(includeCompleted: true);
        var history = new List<HistoryItem>();
        var historyTruncated = false;
        if (IsCalendarHistoryMode && rangeStart < DateTimeOffset.UtcNow)
        {
            string? continuation = null;
            // Bound work without silently presenting a partial day as complete.
            for (var pageIndex = 0; pageIndex < 50; pageIndex++)
            {
                var page = await _backend.GetHistoryAsync(new HistoryQuery(rangeStart, rangeEnd, PageSize: 200, ContinuationToken: continuation));
                if (version != _calendarLoadVersion) return;
                history.AddRange(page.Items);
                historyTruncated = page.HasMore;
                if (!page.HasMore) break;
                continuation = page.ContinuationToken;
            }
        }
        if (version != _calendarLoadVersion) return;
        CalendarRangeLabel = rangeLabel;
        CalendarHistoryNotice = historyTruncated ? "This range exceeds 10,000 sessions. Some history is omitted; use a narrower Flex range." : string.Empty;
        ReconcileOptions(CalendarPlanTasks, taskItems.Where(item => item.Task.Status != TaskState.Completed && item.Task.ArchivedAtUtc is null && item.Task.DeletedAtUtc is null)
            .Select(item => new TaskOption(item.Task.Id, item.Task.Title)), item => item.Id);
        CalendarBlocks.Clear();
        CalendarEvents.Clear();
        foreach (var block in blocks)
        {
            var task = taskItems.FirstOrDefault(item => item.Task.Id == block.TaskId)?.Task.Title;
            var activity = bootstrap.Activities.FirstOrDefault(item => item.Id == block.ActivityId)?.Name;
            CalendarBlocks.Add(new CalendarBlockRowViewModel(block, task ?? activity ?? block.TitleOverride ?? "Planned work", UpdateCalendarBlockAsync, StartCalendarBlockAsync));
        }
        foreach (var item in events)
        {
            CalendarEvents.Add(new CalendarEventRowViewModel(item, UpdateCalendarEventAsync, DeleteCalendarEventAsync));
        }
        if (IsCalendarHistoryMode)
        {
            _calendarHistory = history;
            _historyGridStart = gridStart;
            _historyGridEnd = gridEnd;
            _nextCalendarTick = DateTimeOffset.MinValue;
            RefreshCalendarHistory(DateTimeOffset.UtcNow);
        }
        else BuildCalendarGrid(gridStart, gridEnd, localStart, CalendarBlocks, CalendarEvents, taskItems);
        HasNoCalendarBlocks = CalendarBlocks.Count == 0 && CalendarEvents.Count == 0;
    }

    private void BuildCalendarGrid(
        DateTime gridStart,
        DateTime gridEnd,
        DateTime displayedMonth,
        IEnumerable<CalendarBlockRowViewModel> blocks,
        IEnumerable<CalendarEventRowViewModel> events,
        IReadOnlyList<TaskListItem> taskItems)
    {
        CalendarDayColumns.Clear();
        if (!IsCalendarGridVisible)
        {
            return;
        }

        var blockRows = blocks.ToArray();
        var eventRows = events.ToArray();
        for (var date = gridStart; date < gridEnd; date = date.AddDays(1))
        {
            var dayStart = LocalDateToUtc(date);
            var dayEnd = LocalDateToUtc(date.AddDays(1));
            var items = new List<CalendarGridItemViewModel>();

            foreach (var block in blockRows.Where(item => item.Block.StartAtUtc < dayEnd && item.Block.EndAtUtc > dayStart))
            {
                items.Add(CalendarGridItemViewModel.ForBlock(block, InspectCalendarItem));
            }

            foreach (var item in eventRows.Where(item => item.Event.StartAtUtc < dayEnd && item.Event.EndAtUtc > dayStart))
            {
                items.Add(CalendarGridItemViewModel.ForEvent(item, InspectCalendarItem));
            }

            CalendarDayColumns.Add(new CalendarDayColumnViewModel(
                date,
                IsCalendarMonth && date.Month != displayedMonth.Month,
                items.OrderBy(item => item.StartAtUtc).ThenBy(item => item.Label, StringComparer.OrdinalIgnoreCase).ToArray(),
                dueTasks: taskItems.Where(item => item.Task.DueDate == DateOnly.FromDateTime(date)
                    && item.Task.Status == TaskState.Open && item.Task.ArchivedAtUtc is null && item.Task.DeletedAtUtc is null)
                    .OrderByDescending(item => item.Task.Priority).ThenBy(item => item.Task.Title, StringComparer.OrdinalIgnoreCase)
                    .Select(item => new DueTaskViewModel(item.Task.Title, $"{item.BoardName} / {item.ProjectName}",
                        new AsyncCommand(_ => ShowDueTaskAsync(item)))).ToArray()));
        }
    }

    private static DateTimeOffset LocalDateToUtc(DateTime date)
        => new DateTimeOffset(date, TimeZoneInfo.Local.GetUtcOffset(date)).ToUniversalTime();

    private Task ShowDueTaskAsync(TaskListItem item) => ShowTaskDetailsAsync(new TaskRowViewModel(item, Projects, Activities, [],
        StartTaskAsync, CompleteTaskAsync, SaveTaskAsync, MoveTaskAsync, ArchiveTaskAsync, DeleteTaskAsync,
        AddTaskTagAsync, AddTaskDependencyAsync, AddTaskLinkAsync, ShowTaskDetailsAsync));

    internal static string FormatLocalDateTime(DateTimeOffset instant)
        => TimeZoneInfo.ConvertTime(instant, TimeZoneInfo.Local).ToString(LocalDateTimeFormat, CultureInfo.InvariantCulture);

    internal static bool TryParseLocalDateTime(string value, out DateTimeOffset instant)
    {
        if (DateTime.TryParseExact(value.Trim(), LocalDateTimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
        {
            local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
            if (TimeZoneInfo.Local.IsInvalidTime(local))
            {
                instant = default;
                return false;
            }

            instant = new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
            return true;
        }

        // Retain support for pasted ISO-8601 instants, including an explicit offset.
        return DateTimeOffset.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal, out instant);
    }

    private async Task PlanNextTaskAsync()
    {
        if (SelectedCalendarId == Guid.Empty)
        {
            StatusMessage = "Create a calendar before planning time.";
            return;
        }

        var tasks = await _backend.SearchTasksAsync(includeCompleted: false);
        var task = tasks.FirstOrDefault(item => item.Task.Id == CalendarPlanTaskId);
        if (task is null)
        {
            StatusMessage = "Choose an open task to plan.";
            return;
        }
        if (!TryParseLocalDateTime(CalendarPlanStartText, out var start) || !TryParseLocalDateTime(CalendarPlanEndText, out var end) || end <= start)
        {
            StatusMessage = "Enter a valid local start and end, with the end after the start.";
            return;
        }

        try
        {
            await _backend.CreateScheduleBlockAsync(SelectedCalendarId, task.Task.Id, task.Task.DefaultActivityId, null, start, end, TimeZoneInfo.Local.Id);
            _calendarAnchor = start.ToLocalTime().Date;
            RaisePropertyChanged(nameof(CalendarAnchor));
            StatusMessage = $"Planned {task.Task.Title} for {start.ToLocalTime():MMM d, h:mm tt}.";
            FinishUtilityEdit("plan");
            await LoadCalendarAsync(await _backend.GetBootstrapAsync());
        }
        catch (Exception exception)
        {
            StatusMessage = exception is SnookException snook ? snook.Message : "The schedule block could not be created.";
        }
    }

    private async Task SelectCalendarViewAsync(string view)
    {
        SelectedCalendarItem = null;
        CalendarView = IsCalendarHistoryMode ? (view == "Flex" ? "Flex" : "Week")
            : view is "Day" or "Week" or "Month" or "Agenda" ? view : "Week";
        if (IsCalendarHistoryMode) _historyCalendarView = CalendarView;
        else _eventCalendarView = CalendarView;
        if (IsCalendarVisible)
        {
            await ReloadCalendarAsync();
        }
    }

    private Task SelectTaskViewAsync(string view)
    {
        TaskViewMode = view is "Board" ? "Board" : "List";
        return Task.CompletedTask;
    }

    private async Task SelectSummaryGroupingAsync(string grouping)
    {
        SummaryGrouping = grouping is "Day" or "Task" or "Project" or "Activity" or "ActivityGroup" or "Tag" or "Lane" ? grouping : "Tag";
        if (IsSummaryVisible)
        {
            await LoadSummaryAsync();
        }
    }

    private async Task CreateCalendarEventAsync()
    {
        if (SelectedCalendarId == Guid.Empty)
        {
            StatusMessage = "Create a calendar before adding an event.";
            return;
        }

        if (!TryParseLocalDateTime(NewEventStart, out var start)
            || !TryParseLocalDateTime(NewEventEnd, out var end)
            || end <= start)
        {
            StatusMessage = "Use valid event start and end instants.";
            return;
        }

        try
        {
            DateTimeOffset? recurrenceEnd = null;
            if (!string.IsNullOrWhiteSpace(NewEventRecurrenceEnd))
            {
                if (!TryParseLocalDateTime(NewEventRecurrenceEnd, out var parsedRecurrenceEnd))
                {
                    StatusMessage = "Use a valid recurrence end instant, or leave it empty.";
                    return;
                }

                recurrenceEnd = parsedRecurrenceEnd.ToUniversalTime();
            }

            await _backend.CreateCalendarEventAsync(
                SelectedCalendarId,
                NewEventTitle.Trim(),
                start.ToUniversalTime(),
                end.ToUniversalTime(),
                NewEventDescription.Trim(),
                string.IsNullOrWhiteSpace(NewEventLocation) ? null : NewEventLocation.Trim(),
                string.IsNullOrWhiteSpace(NewEventColor) ? "#246B63" : NewEventColor.Trim(),
                NewEventAllDay,
                TimeZoneInfo.Local.Id,
                string.IsNullOrWhiteSpace(NewEventRecurrence) ? null : NewEventRecurrence.Trim(),
                recurrenceEnd);
            NewEventTitle = string.Empty;
            NewEventDescription = string.Empty;
            NewEventLocation = string.Empty;
            NewEventColor = "#246B63";
            NewEventRecurrence = string.Empty;
            NewEventRecurrenceEnd = string.Empty;
            NewEventAllDay = false;
            NewEventStart = string.Empty;
            NewEventEnd = string.Empty;
            StatusMessage = "Calendar event created.";
            FinishUtilityEdit("event");
            await LoadCalendarAsync(await _backend.GetBootstrapAsync());
        }
        catch (Exception exception)
        {
            StatusMessage = exception is SnookException snook ? snook.Message : "The calendar event could not be created.";
        }
    }

    private Task StartCalendarBlockAsync(CalendarBlockRowViewModel row)
        => StartTimerAsync(row.Block.TaskId, row.Block.ActivityId, SessionLane.Foreground);

    private async Task DeleteCalendarEventAsync(CalendarEventRowViewModel row)
    {
        try
        {
            await _backend.DeleteCalendarEventAsync(row.Event.Id, new OperationRequest(Guid.NewGuid(), Guid.NewGuid(), row.Event.Revision));
            StatusMessage = "Calendar event deleted.";
            await LoadCalendarAsync(await _backend.GetBootstrapAsync());
        }
        catch (Exception exception)
        {
            StatusMessage = exception is SnookException snook ? snook.Message : "The calendar event could not be deleted.";
        }
    }

    private async Task UpdateCalendarEventAsync(CalendarEventRowViewModel row)
    {
        if (!TryParseLocalDateTime(row.StartText, out var start)
            || !TryParseLocalDateTime(row.EndText, out var end)
            || end <= start)
        {
            StatusMessage = "Use valid event start and end instants.";
            return;
        }

        DateTimeOffset? recurrenceEnd = null;
        if (!string.IsNullOrWhiteSpace(row.RecurrenceEndText))
        {
            if (!TryParseLocalDateTime(row.RecurrenceEndText, out var parsedRecurrenceEnd))
            {
                StatusMessage = "Use a valid recurrence end instant, or leave it empty.";
                return;
            }

            recurrenceEnd = parsedRecurrenceEnd.ToUniversalTime();
        }

        try
        {
            await _backend.UpdateCalendarEventAsync(
                row.Event.Id,
                new CalendarEventUpdate(
                    row.TitleEditor.Trim(),
                    row.DescriptionEditor.Trim(),
                    string.IsNullOrWhiteSpace(row.LocationEditor) ? null : row.LocationEditor.Trim(),
                    string.IsNullOrWhiteSpace(row.ColorEditor) ? "#246B63" : row.ColorEditor.Trim(),
                    start.ToUniversalTime(),
                    end.ToUniversalTime(),
                    row.AllDayEditor,
                    row.Event.TimeZone,
                    string.IsNullOrWhiteSpace(row.RecurrenceEditor) ? null : row.RecurrenceEditor.Trim(),
                    recurrenceEnd),
                new OperationRequest(Guid.NewGuid(), Guid.NewGuid(), row.Event.Revision));
            StatusMessage = "Calendar event updated.";
            FinishUtilityEdit(row);
            await LoadCalendarAsync(await _backend.GetBootstrapAsync());
        }
        catch (Exception exception)
        {
            StatusMessage = EditorFailure(exception, "The calendar event could not be updated.");
        }
    }

    private async Task CorrectHistoryAsync(HistoryRowViewModel row)
    {
        if (!TryParseLocalDateTime(row.StartText, out var start))
        {
            StatusMessage = "Enter the start in local time, using YYYY-MM-DD HH:MM.";
            return;
        }

        DateTimeOffset? end = null;
        if (!string.IsNullOrWhiteSpace(row.EndText))
        {
            if (!TryParseLocalDateTime(row.EndText, out var parsedEnd))
            {
                StatusMessage = "Use a valid end instant, or leave it empty for an open interval.";
                return;
            }

            end = parsedEnd;
        }

        if (string.IsNullOrWhiteSpace(row.ReasonText))
        {
            StatusMessage = "Add a reason for this correction.";
            return;
        }

        var intervals = row.Item.Session.Intervals.ToArray();
        if (intervals.Length == 0)
        {
            StatusMessage = "This session has no intervals to correct.";
            return;
        }
        // The form displays minutes. A notes-only correction must keep the stored
        // seconds, especially for a short session that starts and ends in one minute.
        if (row.StartText == FormatLocalDateTime(row.Item.Session.StartedAtUtc))
            start = row.Item.Session.StartedAtUtc;
        var originalEnd = row.Item.Session.StoppedAtUtc ?? intervals[^1].EndedAtUtc;
        if (originalEnd is { } original && row.EndText == FormatLocalDateTime(original))
            end = original;
        intervals[0] = intervals[0] with { StartedAtUtc = start };
        if (end is not null && intervals[^1].EndedAtUtc is not null)
        {
            intervals[^1] = intervals[^1] with { EndedAtUtc = end };
        }

        try
        {
            var stoppedAt = row.Item.Session.State == SessionState.Stopped ? end : null;
            await _backend.CorrectSessionAsync(
                row.Item.Session.Id,
                new SessionCorrection(row.SelectedTaskId, row.SelectedActivityId, start, stoppedAt, row.NotesEditor, intervals, row.ReasonText),
                new OperationRequest(Guid.NewGuid(), Guid.NewGuid(), row.Item.Session.Revision));
            StatusMessage = "Correction saved with before/after provenance.";
            FinishUtilityEdit(row);
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            StatusMessage = EditorFailure(exception, "The correction could not be saved.");
        }
    }

    private async Task UpdateCalendarBlockAsync(CalendarBlockRowViewModel row)
    {
        if (!TryParseLocalDateTime(row.StartText, out var start)
            || !TryParseLocalDateTime(row.EndText, out var end))
        {
            StatusMessage = "Use valid start and end instants for the schedule block.";
            return;
        }

        try
        {
            await _backend.UpdateScheduleBlockAsync(
                row.Block.Id,
                new ScheduleBlockUpdate(start, end, row.Block.TimeZone, row.TitleEditor, row.Block.RecurrenceRule, row.Block.RecurrenceEndUtc),
                new OperationRequest(Guid.NewGuid(), Guid.NewGuid(), row.Block.Revision));
            StatusMessage = "Schedule block updated.";
            FinishUtilityEdit(row);
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            StatusMessage = EditorFailure(exception, "The schedule block could not be updated.");
        }
    }

    private async Task CreateBackupAsync()
    {
        try
        {
            var root = Environment.GetEnvironmentVariable("SNOOK_DATA_DIR")
                ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var path = Path.Combine(root, "Snook", "Backups", $"workspace-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.db");
            var result = await _backend.CreateBackupAsync(path);
            StatusMessage = $"Backup verified ({result.Bytes:N0} bytes).";
        }
        catch (Exception exception)
        {
            StatusMessage = exception is SnookException snook ? snook.Message : "The backup could not be created.";
        }
    }

    private async Task ExportAsync()
    {
        try
        {
            var root = Environment.GetEnvironmentVariable("SNOOK_DATA_DIR")
                ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var path = Path.Combine(root, "Snook", "Exports", $"workspace-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.json");
            var result = await _backend.ExportJsonAsync(path);
            StatusMessage = $"Exported {result.Bytes:N0} bytes of workspace data.";
        }
        catch (Exception exception)
        {
            StatusMessage = exception is SnookException snook ? snook.Message : "The export could not be created.";
        }
    }

    private async Task ExportCsvAsync()
    {
        try
        {
            var root = Environment.GetEnvironmentVariable("SNOOK_DATA_DIR")
                ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var path = Path.Combine(root, "Snook", "Exports", $"worklog-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.csv");
            var now = DateTimeOffset.UtcNow;
            var result = await _backend.ExportCsvAsync(path, now.AddDays(-30), now.AddDays(1));
            StatusMessage = $"Exported {result.Bytes:N0} bytes of CSV time history.";
        }
        catch (Exception exception)
        {
            StatusMessage = exception is SnookException snook ? snook.Message : "The CSV report could not be created.";
        }
    }

    private async Task RestoreLatestBackupAsync()
    {
        try
        {
            var root = Environment.GetEnvironmentVariable("SNOOK_DATA_DIR")
                ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var backupDirectory = Path.Combine(root, "Snook", "Backups");
            var latest = Directory.Exists(backupDirectory)
                ? Directory.GetFiles(backupDirectory, "*.db").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
                : null;
            if (latest is null)
            {
                StatusMessage = "There is no backup to restore yet.";
                return;
            }

            var result = await _backend.RestoreBackupAsync(latest);
            StatusMessage = $"Restored and verified {Path.GetFileName(result.SourcePath)}.";
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            StatusMessage = exception is SnookException snook ? snook.Message : "The backup could not be restored.";
        }
    }

    private async Task AddManualTimeAsync()
    {
        if (ManualTaskId is null && ManualActivityId is null)
        {
            StatusMessage = "Choose a task or activity before adding manual time.";
            return;
        }

        if (!TryParseLocalDateTime(ManualStartText, out var start)
            || !TryParseLocalDateTime(ManualEndText, out var end)
            || end <= start)
        {
            StatusMessage = "Use valid manual start and end instants.";
            return;
        }

        try
        {
            await _backend.CreateManualSessionAsync(
                ManualTaskId,
                ManualActivityId,
                start.ToUniversalTime(),
                end.ToUniversalTime(),
                string.IsNullOrWhiteSpace(ManualNotes) ? null : ManualNotes.Trim());
            StatusMessage = "Manual time added.";
            ManualStartText = FormatLocalDateTime(DateTimeOffset.UtcNow.AddMinutes(-30));
            ManualEndText = FormatLocalDateTime(DateTimeOffset.UtcNow);
            ManualNotes = string.Empty;
            FinishUtilityEdit("manual");
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            StatusMessage = exception is SnookException snook ? snook.Message : "Manual time could not be added.";
        }
    }

    private async Task AddQuickManualTimeAsync()
    {
        var activity = Activities.FirstOrDefault(item => item.DefaultLane == SessionLane.Foreground)
            ?? Activities.FirstOrDefault(item => item.Id != Guid.Empty);
        if (activity is null)
        {
            StatusMessage = "Create an activity before adding quick manual time.";
            return;
        }

        try
        {
            var end = DateTimeOffset.UtcNow;
            await _backend.CreateManualSessionAsync(null, activity.Id, end.AddMinutes(-30), end, "Manual entry");
            StatusMessage = "Added 30 minutes of manual time.";
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            StatusMessage = exception is SnookException snook ? snook.Message : "Manual time could not be added.";
        }
    }

    private async Task StartFocusAsync()
    {
        var activity = (await _backend.GetBootstrapAsync()).Activities.FirstOrDefault(item => item.DefaultLane == SessionLane.Foreground);
        if (activity is null)
        {
            StatusMessage = "Create an activity before starting a general timer.";
            return;
        }

        await StartTimerAsync(null, activity.Id, SessionLane.Foreground);
    }

    private Task StartTaskAsync(TaskRowViewModel task) => StartTimerAsync(task.Task.Id, task.Task.DefaultActivityId, SessionLane.Foreground);

    private async Task StartSelectedActivityAsync()
    {
        var activity = Activities.FirstOrDefault(item => item.Id == SelectedTrackingActivityId);
        if (activity is null)
        {
            StatusMessage = "Choose an activity before starting the timer.";
            return;
        }

        await StartOrSwitchActivityAsync(activity.Id, activity.DefaultLane);
    }

    private Task StartActivityAsync(ActivityAdminRowViewModel row)
        => StartOrSwitchActivityAsync(row.Activity.Id, row.Activity.DefaultLane);

    private async Task StartOrSwitchActivityAsync(Guid activityId, SessionLane lane)
    {
        var existing = ActiveSessions.FirstOrDefault(item => item.Session.TaskId is null && item.Session.ActivityId == activityId);
        if (existing?.Session.State == SessionState.Running)
        {
            StatusMessage = $"{existing.Label} is already being tracked.";
            return;
        }

        if (existing is not null)
        {
            await PauseResumeAsync(existing);
            return;
        }

        await StartTimerAsync(null, activityId, lane);
    }

    private async Task StartTimerAsync(Guid? taskId, Guid? activityId, SessionLane lane)
    {
        try
        {
            await _backend.StartSessionAsync(taskId, activityId, lane, new OperationRequest(Guid.NewGuid(), Guid.NewGuid()));
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            StatusMessage = exception is SnookException snook ? snook.Message : "The timer could not be started.";
        }
    }

    private async Task CompleteTaskAsync(TaskRowViewModel row)
    {
        try
        {
            var request = new OperationRequest(Guid.NewGuid(), Guid.NewGuid(), row.Task.Revision);
            if (row.Task.Status == TaskState.Completed)
            {
                await _backend.ReopenTaskAsync(row.Task.Id, request);
            }
            else
            {
                await _backend.CompleteTaskAsync(row.Task.Id, request);
            }
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            StatusMessage = exception is SnookException snook ? snook.Message : "The task could not be completed.";
        }
    }

    private async Task SaveTaskAsync(TaskRowViewModel row)
    {
        DateOnly? dueDate = null;
        if (!string.IsNullOrWhiteSpace(row.DueDateText))
        {
            if (!DateOnly.TryParseExact(row.DueDateText.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDueDate)
                || parsedDueDate < new DateOnly(1900, 1, 1))
            {
                StatusMessage = "Use a deadline in yyyy-MM-dd format, or leave it blank.";
                return;
            }

            dueDate = parsedDueDate;
        }

        try
        {
            var updated = await _backend.UpdateTaskAsync(row.Task.Id, new TaskUpdate(row.DraftTitle, row.DraftDescription, row.DraftPriority, dueDate, row.DraftActivityId, row.DraftStarred), new OperationRequest(Guid.NewGuid(), Guid.NewGuid(), row.Task.Revision));
            row.AcceptTask(updated);
            await RefreshAsync();
            if (ReferenceEquals(TaskEditor, row)) CloseTaskEditorCommand.Execute(null);
            StatusMessage = "Task saved.";
        }
        catch (Exception exception)
        {
            StatusMessage = EditorFailure(exception, "The task could not be updated.");
        }
    }

    private async Task MoveTaskAsync(TaskRowViewModel row)
    {
        if (row.TargetProjectId == Guid.Empty || row.TargetProjectId == row.Task.ProjectId)
        {
            StatusMessage = "Choose a different project before moving the task.";
            return;
        }

        try
        {
            var updated = await _backend.MoveTaskAsync(row.Task.Id, row.TargetProjectId, new OperationRequest(Guid.NewGuid(), Guid.NewGuid(), row.Task.Revision));
            row.AcceptTask(updated);
            await RefreshAsync();
            StatusMessage = $"Moved to {row.CurrentProjectPath}.";
        }
        catch (Exception exception)
        {
            StatusMessage = exception is SnookException snook ? snook.Message : "The task could not be moved.";
        }
    }

    private async Task AddTaskTagAsync(TaskRowViewModel row)
    {
        if (string.IsNullOrWhiteSpace(row.TagText))
        {
            return;
        }

        try
        {
            await _backend.AddTaskTagAsync(row.Task.Id, row.TagText.Trim());
            row.TagText = string.Empty;
            await RefreshAsync();
            await RefreshTaskEditorDetailsAsync(row);
        }
        catch (Exception exception)
        {
            StatusMessage = exception is SnookException snook ? snook.Message : "The task tag could not be added.";
        }
    }

    private async Task AddTaskDependencyAsync(TaskRowViewModel row)
    {
        if (row.PrerequisiteTaskId is null || row.PrerequisiteTaskId == row.Task.Id)
        {
            StatusMessage = "Choose a different prerequisite task.";
            return;
        }

        try
        {
            await _backend.AddTaskDependencyAsync(row.Task.Id, row.PrerequisiteTaskId.Value);
            row.PrerequisiteTaskId = null;
            StatusMessage = "Task dependency added.";
            await RefreshAsync();
            await RefreshTaskEditorDetailsAsync(row);
        }
        catch (Exception exception)
        {
            StatusMessage = exception is SnookException snook ? snook.Message : "The task dependency could not be added.";
        }
    }

    private async Task AddTaskLinkAsync(TaskRowViewModel row)
    {
        if (string.IsNullOrWhiteSpace(row.LinkUriText))
        {
            StatusMessage = "Enter an absolute HTTP, HTTPS, or file URI.";
            return;
        }

        try
        {
            await _backend.AddTaskLinkAsync(row.Task.Id, string.IsNullOrWhiteSpace(row.LinkLabelText) ? null : row.LinkLabelText.Trim(), row.LinkUriText.Trim());
            row.LinkLabelText = string.Empty;
            row.LinkUriText = string.Empty;
            StatusMessage = "Task reference added.";
            await RefreshAsync();
            await RefreshTaskEditorDetailsAsync(row);
        }
        catch (Exception exception)
        {
            StatusMessage = exception is SnookException snook ? snook.Message : "The task reference could not be added.";
        }
    }

    private async Task ShowTaskDetailsAsync(TaskRowViewModel row)
    {
        try
        {
            var details = await _backend.GetTaskDetailsAsync(row.Task.Id);
            SelectedTaskDetails = new TaskDetailsPanelViewModel(details);
            TaskEditor = new TaskRowViewModel(row.Item with { Task = details.Task }, Projects, Activities,
                (await _backend.SearchTasksAsync()).Select(TaskPickerOption),
                StartTaskAsync, CompleteTaskAsync, SaveTaskAsync, MoveTaskAsync, ArchiveTaskAsync,
                DeleteTaskAsync, AddTaskTagAsync, AddTaskDependencyAsync, AddTaskLinkAsync, ShowTaskDetailsAsync);
            StatusMessage = "Editing task details.";
        }
        catch (Exception exception)
        {
            StatusMessage = exception is SnookException snook ? snook.Message : "Task details could not be loaded.";
        }
    }

    private async Task ArchiveTaskAsync(TaskRowViewModel row)
    {
        try
        {
            var request = new OperationRequest(Guid.NewGuid(), Guid.NewGuid(), row.Task.Revision);
            if (row.Task.ArchivedAtUtc is null)
            {
                await _backend.ArchiveTaskAsync(row.Task.Id, request);
            }
            else
            {
                await _backend.RestoreTaskAsync(row.Task.Id, request);
            }

            await RefreshAsync();
            if (ReferenceEquals(TaskEditor, row)) CloseTaskEditorCommand.Execute(null);
        }
        catch (Exception exception)
        {
            StatusMessage = exception is SnookException snook ? snook.Message : "The task archive state could not be changed.";
        }
    }

    private async Task DeleteTaskAsync(TaskRowViewModel row)
    {
        try
        {
            var request = new OperationRequest(Guid.NewGuid(), Guid.NewGuid(), row.Task.Revision);
            if (row.Task.DeletedAtUtc is null)
            {
                await _backend.DeleteTaskAsync(row.Task.Id, request);
                StatusMessage = "Task moved to deleted items.";
            }
            else
            {
                await _backend.RestoreDeletedTaskAsync(row.Task.Id, request);
                StatusMessage = "Task restored.";
            }

            await RefreshAsync();
            if (ReferenceEquals(TaskEditor, row)) CloseTaskEditorCommand.Execute(null);
        }
        catch (Exception exception)
        {
            StatusMessage = exception is SnookException snook ? snook.Message : "The task deleted state could not be changed.";
        }
    }

    private async Task PauseResumeAsync(ActiveSessionRowViewModel row)
    {
        try
        {
            var request = new OperationRequest(Guid.NewGuid(), Guid.NewGuid(), row.Session.Revision);
            if (row.Session.State == SessionState.Running)
            {
                await _backend.PauseSessionAsync(row.Session.Id, request);
            }
            else
            {
                await _backend.ResumeSessionAsync(row.Session.Id, request);
            }

            await RefreshAsync();
        }
        catch (Exception exception)
        {
            StatusMessage = exception is SnookException snook ? snook.Message : "The timer could not be updated.";
        }
    }

    private async Task StopAsync(ActiveSessionRowViewModel row)
    {
        try
        {
            await _backend.StopSessionAsync(row.Session.Id, null, new OperationRequest(Guid.NewGuid(), Guid.NewGuid(), row.Session.Revision));
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            StatusMessage = exception is SnookException snook ? snook.Message : "The timer could not be stopped.";
        }
    }

    private async Task ResolveRecoveryAsync(RecoverySessionRowViewModel row, RecoveryDecision decision)
    {
        try
        {
            await _backend.ResolveRecoveryAsync(row.Session.Id, decision, new OperationRequest(Guid.NewGuid(), Guid.NewGuid(), row.Session.Revision));
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            StatusMessage = exception is SnookException snook ? snook.Message : "The recovery decision could not be saved.";
        }
    }

    private void OnBackendChanged(object? sender, ChangeNotification notification)
    {
        _ = Dispatcher.UIThread.InvokeAsync(RefreshAsync);
    }

    private void UpdateDisplayTimes()
    {
        RefreshCalendarHistory(DateTimeOffset.UtcNow);
        foreach (var session in ActiveSessions)
        {
            session.UpdateElapsed();
        }
        if (_todayClockSnapshot is not { } snapshot) return;
        var now = DateTimeOffset.UtcNow;
        var displayed = FormatDuration(LiveTodayClock.Milliseconds(snapshot, now, TimeZoneInfo.Local));
        if (TrackedToday != displayed)
        {
            TrackedToday = displayed;
            RaisePropertyChanged(nameof(TodayOverview));
        }
        if (snapshot.CapturedAtUtc.ToLocalTime().Date != now.ToLocalTime().Date && now >= _nextDayRefresh)
        {
            _nextDayRefresh = now.AddMinutes(1);
            _ = RefreshAsync();
        }
    }

    private static string FormatDuration(long milliseconds)
    {
        var minutes = Math.Max(0, milliseconds) / 60_000;
        return minutes < 60 ? $"{minutes}m" : $"{minutes / 60}h {minutes % 60:00}m";
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    private void RaisePropertyChanged(string propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public async ValueTask DisposeAsync()
    {
        _displayTimer.Dispose();
        _backend.Changed -= OnBackendChanged;
        await _backend.DisposeAsync();
    }
}

public sealed record ProjectOption(Guid Id, string Name, Guid BoardId = default, string BoardName = "");
public sealed record BoardOption(Guid Id, string Name);
public sealed record ActivityOption(Guid Id, string Name, SessionLane DefaultLane = SessionLane.Foreground);
public sealed record ActivityGroupOption(Guid Id, string Name);
public sealed record CalendarOption(Guid Id, string Name);
public sealed record TaskOption(Guid Id, string Name, string Context = "");
public sealed record PriorityOption(Priority Value, string Name);
public sealed record TaskSortOption(string Value, string Name);
public sealed record SessionLaneOption(SessionLane Value, string Name);

public sealed class TaskDetailsPanelViewModel
{
    public TaskDetailsPanelViewModel(TaskDetails details)
    {
        Details = details;
    }

    public TaskDetails Details { get; }
    public TaskItem Task => Details.Task;
    public IReadOnlyList<Tag> Tags => Details.Tags;
    public IReadOnlyList<TaskLink> Links => Details.Links;
    public IReadOnlyList<Guid> PrerequisiteTaskIds => Details.PrerequisiteTaskIds;
    public string TrackedLabel => $"{MainWindowViewModel.FormatForRow(Details.TrackedMilliseconds)} tracked";
    public string ActiveLabel => $"{MainWindowViewModel.FormatForRow(Details.ActiveMilliseconds)} active";
    public string PrerequisiteLabel => $"{Details.PrerequisiteTaskIds.Count} prerequisite{(Details.PrerequisiteTaskIds.Count == 1 ? "" : "s")}";
}

public sealed class ReviewRowViewModel
{
    public ReviewRowViewModel(string label, string detail)
    {
        Label = label;
        Detail = detail;
    }

    public string Label { get; }
    public string Detail { get; }
}

public sealed class ActivityGroupAdminRowViewModel
{
    private readonly Func<ActivityGroupAdminRowViewModel, Task> _save;
    private readonly Func<ActivityGroupAdminRowViewModel, Task> _delete;
    private readonly Func<ActivityGroupAdminRowViewModel, ReorderDirection, Task> _reorder;

    public ActivityGroupAdminRowViewModel(ActivityGroup group, Func<ActivityGroupAdminRowViewModel, Task> save, Func<ActivityGroupAdminRowViewModel, Task> delete, Func<ActivityGroupAdminRowViewModel, ReorderDirection, Task> reorder)
    {
        Group = group;
        DraftName = group.Name;
        _save = save;
        _delete = delete;
        _reorder = reorder;
        SaveCommand = new AsyncCommand(_ => _save(this));
        DeleteCommand = new AsyncCommand(_ => _delete(this));
        MoveEarlierCommand = new AsyncCommand(_ => _reorder(this, ReorderDirection.Earlier));
        MoveLaterCommand = new AsyncCommand(_ => _reorder(this, ReorderDirection.Later));
    }

    public ActivityGroup Group { get; }
    public string LifecycleLabel => Group.DeletedAtUtc is not null ? "Deleted" : "Activity group";
    public string DraftName { get; set; }
    public string DeleteLabel => Group.DeletedAtUtc is null ? "Delete" : "Restore deleted";
    public ICommand SaveCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand MoveEarlierCommand { get; }
    public ICommand MoveLaterCommand { get; }
}

public sealed class BoardAdminRowViewModel
{
    private readonly Func<BoardAdminRowViewModel, Task> _save;
    private readonly Func<BoardAdminRowViewModel, Task> _toggleArchive;
    private readonly Func<BoardAdminRowViewModel, Task> _delete;
    private readonly Func<BoardAdminRowViewModel, ReorderDirection, Task> _reorder;

    public BoardAdminRowViewModel(Board board, Func<BoardAdminRowViewModel, Task> save, Func<BoardAdminRowViewModel, Task> toggleArchive, Func<BoardAdminRowViewModel, Task> delete, Func<BoardAdminRowViewModel, ReorderDirection, Task> reorder)
    {
        Board = board;
        DraftName = board.Name;
        _save = save;
        _toggleArchive = toggleArchive;
        _delete = delete;
        _reorder = reorder;
        SaveCommand = new AsyncCommand(_ => _save(this));
        ToggleArchiveCommand = new AsyncCommand(_ => _toggleArchive(this));
        DeleteCommand = new AsyncCommand(_ => _delete(this));
        MoveEarlierCommand = new AsyncCommand(_ => _reorder(this, ReorderDirection.Earlier));
        MoveLaterCommand = new AsyncCommand(_ => _reorder(this, ReorderDirection.Later));
    }

    public Board Board { get; }
    public bool IsDeleted => Board.DeletedAtUtc is not null;
    public string LifecycleLabel => Board.DeletedAtUtc is not null ? "Deleted" : Board.ArchivedAtUtc is not null ? "Archived" : "Active board";
    public string DraftName { get; set; }
    public string ArchiveLabel => Board.ArchivedAtUtc is null ? "Archive" : "Restore";
    public string DeleteLabel => Board.DeletedAtUtc is null ? "Delete" : "Restore deleted";
    public ICommand SaveCommand { get; }
    public ICommand ToggleArchiveCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand MoveEarlierCommand { get; }
    public ICommand MoveLaterCommand { get; }
}

public sealed class ProjectAdminRowViewModel : INotifyPropertyChanged
{
    private string _tagText = string.Empty;
    public event PropertyChangedEventHandler? PropertyChanged;
    private readonly Func<ProjectAdminRowViewModel, Task> _save;
    private readonly Func<ProjectAdminRowViewModel, Task> _toggleArchive;
    private readonly Func<ProjectAdminRowViewModel, Task> _delete;
    private readonly Func<ProjectAdminRowViewModel, Task> _addTag;
    private readonly Func<ProjectAdminRowViewModel, ReorderDirection, Task> _reorder;

    public ProjectAdminRowViewModel(Project project, Func<ProjectAdminRowViewModel, Task> save, Func<ProjectAdminRowViewModel, Task> toggleArchive, Func<ProjectAdminRowViewModel, Task> delete, Func<ProjectAdminRowViewModel, Task> addTag, Func<ProjectAdminRowViewModel, ReorderDirection, Task> reorder)
    {
        Project = project;
        DraftName = project.Name;
        DraftStarred = project.Starred;
        _save = save;
        _toggleArchive = toggleArchive;
        _delete = delete;
        _addTag = addTag;
        _reorder = reorder;
        SaveCommand = new AsyncCommand(_ => _save(this));
        ToggleArchiveCommand = new AsyncCommand(_ => _toggleArchive(this));
        DeleteCommand = new AsyncCommand(_ => _delete(this));
        AddTagCommand = new AsyncCommand(_ => _addTag(this));
        MoveEarlierCommand = new AsyncCommand(_ => _reorder(this, ReorderDirection.Earlier));
        MoveLaterCommand = new AsyncCommand(_ => _reorder(this, ReorderDirection.Later));
    }

    public Project Project { get; }
    public bool DraftStarred { get; set; }
    public string StarGlyph => Project.Starred ? "★" : "☆";
    public string StarLabel => Project.Starred ? $"Unstar project {Project.Name}" : $"Star project {Project.Name}";
    public string LifecycleLabel => Project.DeletedAtUtc is not null ? "Deleted" : Project.ArchivedAtUtc is not null ? "Archived" : "Active project";
    public string DraftName { get; set; }
    public string TagText
    {
        get => _tagText;
        set { if (_tagText != value) { _tagText = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TagText))); } }
    }
    public string ArchiveLabel => Project.ArchivedAtUtc is null ? "Archive" : "Restore";
    public string DeleteLabel => Project.DeletedAtUtc is null ? "Delete" : "Restore deleted";
    public ICommand SaveCommand { get; }
    public ICommand ToggleArchiveCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand AddTagCommand { get; }
    public ICommand MoveEarlierCommand { get; }
    public ICommand MoveLaterCommand { get; }
}

public sealed class ActivityAdminRowViewModel : INotifyPropertyChanged
{
    private string _tagText = string.Empty;
    public event PropertyChangedEventHandler? PropertyChanged;
    private readonly Func<ActivityAdminRowViewModel, Task> _save;
    private readonly Func<ActivityAdminRowViewModel, Task> _toggleArchive;
    private readonly Func<ActivityAdminRowViewModel, Task> _delete;
    private readonly Func<ActivityAdminRowViewModel, Task> _addTag;
    private readonly Func<ActivityAdminRowViewModel, Task> _start;

    public ActivityAdminRowViewModel(Activity activity, IReadOnlyList<ActivityGroupOption> groups, Func<ActivityAdminRowViewModel, Task> save, Func<ActivityAdminRowViewModel, Task> toggleArchive, Func<ActivityAdminRowViewModel, Task> delete, Func<ActivityAdminRowViewModel, Task> addTag, Func<ActivityAdminRowViewModel, Task> start)
    {
        Activity = activity;
        GroupOptions = groups;
        GroupId = activity.GroupId ?? Guid.Empty;
        DraftName = activity.Name;
        DraftDescription = activity.Description;
        DraftLane = activity.DefaultLane;
        _save = save;
        _toggleArchive = toggleArchive;
        _delete = delete;
        _addTag = addTag;
        _start = start;
        SaveCommand = new AsyncCommand(_ => _save(this));
        ToggleArchiveCommand = new AsyncCommand(_ => _toggleArchive(this));
        DeleteCommand = new AsyncCommand(_ => _delete(this));
        AddTagCommand = new AsyncCommand(_ => _addTag(this));
        StartCommand = new AsyncCommand(_ => _start(this), _ => Activity.ArchivedAtUtc is null && Activity.DeletedAtUtc is null);
    }

    public Activity Activity { get; }
    public bool IsDeleted => Activity.DeletedAtUtc is not null;
    public string CatalogLabel => Activity.DeletedAtUtc is not null ? "Deleted" : Activity.ArchivedAtUtc is not null ? "Archived" : $"{LaneLabel} · {GroupOptions.FirstOrDefault(group => group.Id == GroupId)?.Name ?? "No group"}";
    public IReadOnlyList<ActivityGroupOption> GroupOptions { get; }
    public Guid GroupId { get; set; }
    public string DraftName { get; set; }
    public string DraftDescription { get; set; }
    public SessionLane DraftLane { get; set; }
    public IReadOnlyList<SessionLaneOption> LaneOptions { get; } =
    [
        new(SessionLane.Foreground, "Focus"),
        new(SessionLane.Background, "Background")
    ];
    public string TagText
    {
        get => _tagText;
        set { if (_tagText != value) { _tagText = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TagText))); } }
    }
    public string LaneLabel => DraftLane == SessionLane.Background ? "Background" : "Focus";
    public string ActivityContextLabel => string.IsNullOrWhiteSpace(DraftDescription) ? LaneLabel : $"{LaneLabel} · {DraftDescription}";
    public string ArchiveLabel => Activity.ArchivedAtUtc is null ? "Archive" : "Restore";
    public string DeleteLabel => Activity.DeletedAtUtc is null ? "Delete" : "Restore deleted";
    public ICommand SaveCommand { get; }
    public ICommand ToggleArchiveCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand AddTagCommand { get; }
    public ICommand StartCommand { get; }
}

public sealed class ProjectTaskGroupViewModel
{
    public ProjectTaskGroupViewModel(Guid projectId, string projectName, IReadOnlyList<TaskRowViewModel> tasks, ProjectAdminRowViewModel? admin = null, string boardName = "")
    {
        ProjectId = projectId;
        ProjectName = projectName;
        Tasks = tasks;
        Admin = admin;
        BoardName = boardName;
    }

    public string ProjectName { get; }
    public string BoardName { get; }
    public ProjectAdminRowViewModel? Admin { get; }
    public bool CanManage => Admin is not null;
    public Guid ProjectId { get; }
    public string CountLabel => Tasks.Count.ToString(CultureInfo.InvariantCulture);
    public bool IsEmpty => Tasks.Count == 0;
    public IReadOnlyList<TaskRowViewModel> Tasks { get; }
}

public sealed class TaskRowViewModel : INotifyPropertyChanged
{
    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected == value) return; _isSelected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected))); }
    }
    public bool CanSelect => Task.DeletedAtUtc is null;
    public string SelectionLabel => $"Select task {Title}";
    public string StarGlyph => Task.Starred ? "★" : "☆";
    public string StarLabel => Task.Starred ? $"Unstar task {Title}" : $"Star task {Title}";
    private readonly Func<TaskRowViewModel, Task> _start;
    private readonly Func<TaskRowViewModel, Task> _complete;
    private readonly Func<TaskRowViewModel, Task> _save;
    private readonly Func<TaskRowViewModel, Task> _move;
    private readonly Func<TaskRowViewModel, Task> _archive;
    private readonly Func<TaskRowViewModel, Task> _delete;
    private readonly Func<TaskRowViewModel, Task> _addTag;
    private readonly Func<TaskRowViewModel, Task> _addDependency;
    private readonly Func<TaskRowViewModel, Task> _addLink;
    private readonly Func<TaskRowViewModel, Task> _showDetails;
    private string _draftTitle;
    private Guid _targetProjectId;

    public TaskRowViewModel(TaskListItem item, IEnumerable<ProjectOption> projects, IEnumerable<ActivityOption> activities, IEnumerable<TaskOption> prerequisiteOptions, Func<TaskRowViewModel, Task> start, Func<TaskRowViewModel, Task> complete, Func<TaskRowViewModel, Task> save, Func<TaskRowViewModel, Task> move, Func<TaskRowViewModel, Task> archive, Func<TaskRowViewModel, Task> delete, Func<TaskRowViewModel, Task> addTag, Func<TaskRowViewModel, Task> addDependency, Func<TaskRowViewModel, Task> addLink, Func<TaskRowViewModel, Task> showDetails)
    {
        Item = item;
        ProjectOptions = projects.ToArray();
        ActivityOptions = activities.ToArray();
        PrerequisiteOptions = prerequisiteOptions.Where(option => option.Id != item.Task.Id).ToArray();
        MoveOptions = ProjectOptions.GroupBy(project => project.BoardId).SelectMany(group =>
            new[] { new TaskPickerEntry(Guid.Empty, group.First().BoardName, "", true) }
                .Concat(group.Select(project => new TaskPickerEntry(project.Id, project.Name, project.BoardName)))).ToArray();
        DependencyOptions = PrerequisiteOptions.GroupBy(task => task.Context).SelectMany(group =>
            new[] { new TaskPickerEntry(Guid.Empty, group.Key, "", true) }
                .Concat(group.Select(task => new TaskPickerEntry(task.Id, task.Name, task.Context)))).ToArray();
        _start = start;
        _complete = complete;
        _save = save;
        _move = move;
        _archive = archive;
        _delete = delete;
        _addTag = addTag;
        _addDependency = addDependency;
        _addLink = addLink;
        _showDetails = showDetails;
        _draftTitle = item.Task.Title;
        _targetProjectId = item.Task.ProjectId;
        DueDateText = item.Task.DueDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty;
        DraftPriority = item.Task.Priority;
        DraftDescription = item.Task.Description;
        DraftActivityId = item.Task.DefaultActivityId;
        DraftStarred = item.Task.Starred;
        StartCommand = new AsyncCommand(_ => _start(this));
        CompleteCommand = new AsyncCommand(_ => _complete(this));
        SaveCommand = new AsyncCommand(_ => _save(this));
        MoveCommand = new AsyncCommand(_ => _move(this));
        ArchiveCommand = new AsyncCommand(_ => _archive(this));
        DeleteCommand = new AsyncCommand(_ => _delete(this));
        AddTagCommand = new AsyncCommand(_ => _addTag(this));
        AddDependencyCommand = new AsyncCommand(_ => _addDependency(this));
        AddLinkCommand = new AsyncCommand(_ => _addLink(this));
        ShowDetailsCommand = new AsyncCommand(_ => _showDetails(this));
    }

    public TaskListItem Item { get; private set; }
    public event PropertyChangedEventHandler? PropertyChanged;
    public void AcceptTask(TaskItem task)
    {
        Item = Item with { Task = task, ProjectName = ProjectOptions.FirstOrDefault(project => project.Id == task.ProjectId)?.Name ?? Item.ProjectName };
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentProjectPath)));
    }
    public string CurrentProjectPath
    {
        get
        {
            var project = ProjectOptions.FirstOrDefault(project => project.Id == Task.ProjectId);
            return project is null ? ProjectName : $"{project.BoardName} / {project.Name}";
        }
    }
    public IReadOnlyList<TaskPickerEntry> MoveOptions { get; }
    public IReadOnlyList<TaskPickerEntry> DependencyOptions { get; }
    public TaskItem Task => Item.Task;
    public string Title => Task.Title;
    public string DraftTitle { get => _draftTitle; set => _draftTitle = value; }
    public string DraftDescription { get; set; } = string.Empty;
    public IReadOnlyList<ProjectOption> ProjectOptions { get; }
    public IReadOnlyList<ActivityOption> ActivityOptions { get; }
    public IReadOnlyList<TaskOption> PrerequisiteOptions { get; }
    public IReadOnlyList<PriorityOption> PriorityOptions { get; } =
    [
        new(Priority.None, "No priority"),
        new(Priority.Low, "Low"),
        new(Priority.Medium, "Medium"),
        new(Priority.High, "High"),
        new(Priority.Urgent, "Urgent")
    ];
    public Guid TargetProjectId { get => _targetProjectId; set => _targetProjectId = value; }
    public string DueDateText { get; set; }
    public string TagText { get; set; } = string.Empty;
    public Priority DraftPriority { get; set; }
    public Guid? DraftActivityId { get; set; }
    public Guid? PrerequisiteTaskId { get; set; }
    public string LinkLabelText { get; set; } = string.Empty;
    public string LinkUriText { get; set; } = string.Empty;
    public bool DraftStarred { get; set; }
    public string ProjectName => Item.ProjectName;
    public string PriorityLabel => Task.Priority switch { Priority.Urgent => "Urgent", Priority.High => "High", Priority.Medium => "Medium", Priority.Low => "Low", _ => "No priority" };
    public string DueLabel => Task.DueDate is null ? "No deadline" : $"Due {Task.DueDate:MMM d}";
    public string TrackedLabel => Item.TrackedMilliseconds == 0 ? "No time yet" : MainWindowViewModel.FormatForRow(Item.TrackedMilliseconds);
    public string ActionLabel => Task.Status == TaskState.Completed ? "Reopen" : "Done";
    public string CompletionGlyph => Task.Status == TaskState.Completed ? "✓" : "○";
    public string ArchiveLabel => Task.ArchivedAtUtc is null ? "Archive" : "Restore";
    public string DeleteLabel => Task.DeletedAtUtc is null ? "Delete" : "Restore deleted";
    public ICommand StartCommand { get; }
    public ICommand CompleteCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand MoveCommand { get; }
    public ICommand ArchiveCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand AddTagCommand { get; }
    public ICommand AddDependencyCommand { get; }
    public ICommand AddLinkCommand { get; }
    public ICommand ShowDetailsCommand { get; }
}

public sealed class ActiveSessionRowViewModel : INotifyPropertyChanged
{
    private readonly Func<ActiveSessionRowViewModel, Task> _toggle;
    private readonly Func<ActiveSessionRowViewModel, Task> _stop;
    private long _displayedMilliseconds;

    public ActiveSessionRowViewModel(ActiveSessionItem item, Func<ActiveSessionRowViewModel, Task> toggle, Func<ActiveSessionRowViewModel, Task> stop)
    {
        Session = item.Session;
        TaskTitle = item.TaskTitle;
        ActivityName = item.ActivityName;
        _displayedMilliseconds = item.DisplayedMilliseconds;
        _toggle = toggle;
        _stop = stop;
        ToggleCommand = new AsyncCommand(_ => _toggle(this));
        StopCommand = new AsyncCommand(_ => _stop(this));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public TrackingSession Session { get; }
    public string? TaskTitle { get; }
    public string? ActivityName { get; }
    public string Label => TaskTitle ?? ActivityName ?? "General focus";
    public string LaneLabel => Session.Lane == SessionLane.Background ? "Background" : "Focus";
    public string StateLabel => Session.State == SessionState.Running ? "Running" : "Paused";
    public string AccentColor => Session.State == SessionState.Paused ? "#91692E" : Session.Lane == SessionLane.Background ? "#566C87" : "#287667";
    public string SurfaceColor => Session.State == SessionState.Paused ? "#FBF5E9" : Session.Lane == SessionLane.Background ? "#EFF3F7" : "#EDF5F0";
    public string ElapsedLabel => MainWindowViewModel.FormatForRow(_displayedMilliseconds);
    public string ToggleLabel => Session.State == SessionState.Running ? "Pause" : "Resume";
    public ICommand ToggleCommand { get; }
    public ICommand StopCommand { get; }

    public void UpdateElapsed()
    {
        _displayedMilliseconds = TimeMath.DurationMilliseconds(Session.Intervals, DateTimeOffset.UtcNow);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ElapsedLabel)));
    }
}

public sealed class RecoverySessionRowViewModel
{
    private readonly Func<RecoverySessionRowViewModel, RecoveryDecision, Task> _resolve;

    public RecoverySessionRowViewModel(TrackingSession session, Func<RecoverySessionRowViewModel, RecoveryDecision, Task> resolve)
    {
        Session = session;
        _resolve = resolve;
        StopAtLastKnownCommand = new AsyncCommand(_ => _resolve(this, RecoveryDecision.StopAtLastKnown));
        StopNowCommand = new AsyncCommand(_ => _resolve(this, RecoveryDecision.StopNow));
        ContinueCommand = new AsyncCommand(_ => _resolve(this, RecoveryDecision.Continue));
    }

    public TrackingSession Session { get; }
    public string ReasonLabel => Session.RecoveryReason ?? "The timer needs a recovery decision.";
    public ICommand StopAtLastKnownCommand { get; }
    public ICommand StopNowCommand { get; }
    public ICommand ContinueCommand { get; }
}

public sealed class HistoryRowViewModel
{
    private readonly Func<HistoryRowViewModel, Task> _correct;

    public HistoryRowViewModel(HistoryItem item, IReadOnlyList<TaskOption> taskOptions, IReadOnlyList<ActivityOption> activityOptions, Func<HistoryRowViewModel, Task> correct)
    {
        Item = item;
        TaskOptions = taskOptions;
        ActivityOptions = activityOptions;
        SelectedTaskId = item.Session.TaskId;
        SelectedActivityId = item.Session.ActivityId;
        NotesEditor = item.Session.Notes;
        StartText = MainWindowViewModel.FormatLocalDateTime(item.Session.StartedAtUtc);
        var lastIntervalEnd = item.Session.Intervals.Count == 0 ? null : item.Session.Intervals[^1].EndedAtUtc;
        EndText = item.Session.StoppedAtUtc is { } stoppedAt ? MainWindowViewModel.FormatLocalDateTime(stoppedAt)
            : lastIntervalEnd is { } intervalEnd ? MainWindowViewModel.FormatLocalDateTime(intervalEnd)
            : string.Empty;
        ReasonText = string.Empty;
        _correct = correct;
        CorrectCommand = new AsyncCommand(_ => _correct(this));
    }

    public HistoryItem Item { get; }
    public IReadOnlyList<TaskOption> TaskOptions { get; }
    public IReadOnlyList<ActivityOption> ActivityOptions { get; }
    public Guid? SelectedTaskId { get; set; }
    public Guid? SelectedActivityId { get; set; }
    public string NotesEditor { get; set; }
    public string StartText { get; set; }
    public string EndText { get; set; }
    public string ReasonText { get; set; }
    public ICommand CorrectCommand { get; }
    public string Title => Item.TaskTitle ?? Item.ActivityName ?? "General time";
    public string Context => Item.ProjectName is null ? Item.ActivityName ?? "Activity" : $"{Item.ProjectName}  ·  {Item.ActivityName ?? "Task work"}";
    public string DateLabel => Item.Session.StartedAtUtc.ToLocalTime().ToString("ddd, MMM d", CultureInfo.CurrentCulture);
    public string TimeLabel => $"{Item.Session.StartedAtUtc.ToLocalTime():h:mm tt}  ·  {MainWindowViewModel.FormatForRow(Item.AttributedMilliseconds)}";
    public string LaneLabel => Item.Session.Lane == SessionLane.Background ? "Background" : "Focus";
    public string ShortDateLabel => Item.Session.StartedAtUtc.ToLocalTime().ToString("ddd, MMM d", CultureInfo.CurrentCulture);
    public string StartLabel => Item.Session.StartedAtUtc.ToLocalTime().ToString("h:mm tt", CultureInfo.CurrentCulture);
    public string DurationLabel => MainWindowViewModel.FormatForRow(Item.AttributedMilliseconds);
    public string StateLabel => $"{LaneLabel} · {Item.Session.State}";
}

public sealed class SummaryRowViewModel
{
    public SummaryRowViewModel(SummaryBucket bucket, long largest)
    {
        Bucket = bucket;
        BarPercent = largest > 0 ? 100d * bucket.AttributedMilliseconds / largest : 0;
    }

    public SummaryBucket Bucket { get; }
    public double BarPercent { get; }
    public string Label => Bucket.Label;
    public string DurationLabel => MainWindowViewModel.FormatForRow(Bucket.AttributedMilliseconds);
    public string CoverageLabel => MainWindowViewModel.FormatForRow(Bucket.CoverageMilliseconds);
}

public sealed class CalendarBlockRowViewModel
{
    private readonly Func<CalendarBlockRowViewModel, Task> _update;
    private readonly Func<CalendarBlockRowViewModel, Task> _start;

    public CalendarBlockRowViewModel(ScheduleBlock block, string label, Func<CalendarBlockRowViewModel, Task> update, Func<CalendarBlockRowViewModel, Task> start)
    {
        Block = block;
        Label = label;
        TitleEditor = block.TitleOverride ?? label;
        StartText = MainWindowViewModel.FormatLocalDateTime(block.StartAtUtc);
        EndText = MainWindowViewModel.FormatLocalDateTime(block.EndAtUtc);
        _update = update;
        _start = start;
        UpdateCommand = new AsyncCommand(_ => _update(this));
        StartCommand = new AsyncCommand(_ => _start(this));
    }

    public ScheduleBlock Block { get; }
    public string Label { get; }
    public string TitleEditor { get; set; }
    public string StartText { get; set; }
    public string EndText { get; set; }
    public ICommand UpdateCommand { get; }
    public ICommand StartCommand { get; }
    public string DayLabel => Block.StartAtUtc.ToLocalTime().ToString("ddd, MMM d", CultureInfo.CurrentCulture);
    public string TimeLabel => $"{Block.StartAtUtc.ToLocalTime():h:mm tt} – {Block.EndAtUtc.ToLocalTime():h:mm tt}";
    public string DurationLabel => MainWindowViewModel.FormatForRow((Block.EndAtUtc - Block.StartAtUtc).Ticks / TimeSpan.TicksPerMillisecond);
}

public sealed class CalendarDayColumnViewModel
{
    public CalendarDayColumnViewModel(DateTime date, bool outsideDisplayedMonth, IReadOnlyList<CalendarGridItemViewModel> items,
        bool isHistory = false, TimeZoneInfo? timeZone = null, IReadOnlyList<DueTaskViewModel>? dueTasks = null)
    {
        Date = date;
        OutsideDisplayedMonth = outsideDisplayedMonth;
        Items = items;
        IsHistory = isHistory;
        TimeZone = timeZone ?? TimeZoneInfo.Local;
        DueTasks = dueTasks ?? [];
    }

    public DateTime Date { get; }
    public bool OutsideDisplayedMonth { get; }
    public double Opacity => OutsideDisplayedMonth ? 0.48 : 1;
    public string DayLabel => Date.ToString("ddd", CultureInfo.CurrentCulture);
    public string DateLabel => Date.ToString("MMM d", CultureInfo.CurrentCulture);
    public IReadOnlyList<CalendarGridItemViewModel> Items { get; }
    public IReadOnlyList<DueTaskViewModel> DueTasks { get; }
    public bool HasDueTasks => DueTasks.Count > 0;
    public string DueCountLabel => $"{DueTasks.Count} due";
    public string DueAutomationLabel => $"{DueTasks.Count} tasks due {Date:MMMM d, yyyy}";
    public bool HasItems => Items.Count > 0;
    public bool IsHistory { get; }
    public TimeZoneInfo TimeZone { get; }
    public DateTimeOffset StartAtUtc => CalendarHistoryProjection.MidnightUtc(Date, TimeZone);
    public DateTimeOffset EndAtUtc => CalendarHistoryProjection.MidnightUtc(Date.AddDays(1), TimeZone);
    public double MinuteCount => IsHistory ? (EndAtUtc - StartAtUtc).TotalMinutes : 1440;
    public string TrackedLabel
    {
        get
        {
            var duration = TimeSpan.FromTicks(Items.Where(item => item.IsActual).Sum(item => (item.EndAtUtc - item.StartAtUtc).Ticks));
            return duration.TotalMinutes < 1 && duration > TimeSpan.Zero ? "<1m tracked"
                : duration.TotalHours >= 1 ? $"{(int)duration.TotalHours}h {duration.Minutes:00}m tracked" : $"{(int)duration.TotalMinutes}m tracked";
        }
    }
}

public sealed record DueTaskViewModel(string Title, string ProjectPath, ICommand OpenCommand);

public sealed class CalendarGridItemViewModel
{
    internal CalendarGridItemViewModel(string label, DateTimeOffset startAtUtc, DateTimeOffset endAtUtc, bool isEvent, bool allDay,
        string details, ICommand? startCommand, Action<CalendarGridItemViewModel> inspect,
        string key = "", bool isActual = false, bool isFuturePlan = false, bool isRunning = false, bool isBackground = false)
    {
        Label = label;
        StartAtUtc = startAtUtc;
        EndAtUtc = endAtUtc;
        IsEvent = isEvent;
        AllDay = allDay;
        Details = details;
        StartCommand = startCommand;
        Key = key;
        IsActual = isActual;
        IsFuturePlan = isFuturePlan;
        IsRunning = isRunning;
        IsBackground = isBackground;
        InspectCommand = new AsyncCommand(_ => { inspect(this); return Task.CompletedTask; });
    }

    public string Label { get; }
    public string Key { get; }
    public bool IsActual { get; }
    public bool IsFuturePlan { get; }
    public bool IsRunning { get; }
    public bool IsBackground { get; }
    public DateTimeOffset StartAtUtc { get; }
    public DateTimeOffset EndAtUtc { get; }
    public bool IsEvent { get; }
    public bool AllDay { get; }
    public string Details { get; }
    public bool CanStart => StartCommand is not null;
    public ICommand? StartCommand { get; }
    public ICommand InspectCommand { get; }
    public string IntervalLabel => AllDay ? $"{StartAtUtc.ToLocalTime():MMM d} · All day"
        : IsActual || IsFuturePlan ? $"{StartAtUtc.ToLocalTime():ddd, MMM d HH:mm:ss zzz} – {EndAtUtc.ToLocalTime():MMM d HH:mm:ss zzz} · {DurationLabel}"
        : $"{StartAtUtc.ToLocalTime():ddd, MMM d h:mm tt} – {EndAtUtc.ToLocalTime():MMM d h:mm tt} · {(EndAtUtc - StartAtUtc).TotalMinutes:0} min";
    public string DurationLabel => EndAtUtc - StartAtUtc < TimeSpan.FromMinutes(1) ? $"{(EndAtUtc - StartAtUtc).TotalSeconds:0.#} sec"
        : $"{(EndAtUtc - StartAtUtc).TotalMinutes:0.#} min";
    public string KindLabel => IsActual ? (IsRunning ? "Running" : "Recorded") + (IsBackground ? " · Background" : string.Empty)
        : IsFuturePlan ? "Planned" : IsEvent ? "Event" : "Plan";
    public string TimeLabel => AllDay ? "All day" : StartAtUtc.ToLocalTime().ToString("h:mm tt", CultureInfo.CurrentCulture);
    public string Background => IsFuturePlan ? "#EFF1F1" : IsActual && IsBackground ? "#E7EEF5" : IsEvent ? "#FBF5E9" : "#E6F0EC";
    public string Foreground => IsFuturePlan ? "#606D72" : IsActual && IsBackground ? "#44647F" : IsEvent ? "#91692E" : "#246B63";

    public static CalendarGridItemViewModel ForBlock(CalendarBlockRowViewModel row, Action<CalendarGridItemViewModel> inspect)
        => new(row.Label, row.Block.StartAtUtc, row.Block.EndAtUtc, false, false, "Planned work · Start when you are ready.", row.StartCommand, inspect,
            $"plan:{row.Block.Id}:{row.Block.StartAtUtc:O}");

    public static CalendarGridItemViewModel ForEvent(CalendarEventRowViewModel row, Action<CalendarGridItemViewModel> inspect)
        => new(row.Label, row.Event.StartAtUtc, row.Event.EndAtUtc, true, row.Event.AllDay,
            string.Join(" · ", new[] { row.LocationLabel, row.DescriptionLabel }.Where(value => !string.IsNullOrWhiteSpace(value))), null, inspect);
}

public sealed class CalendarAdminRowViewModel
{
    private readonly Func<CalendarAdminRowViewModel, Task> _save;
    private readonly Func<CalendarAdminRowViewModel, Task> _delete;

    public CalendarAdminRowViewModel(DomainCalendar calendar, Func<CalendarAdminRowViewModel, Task> save, Func<CalendarAdminRowViewModel, Task> delete)
    {
        Calendar = calendar;
        DraftName = calendar.Name;
        DraftColor = calendar.Color;
        IsVisible = calendar.Visible;
        _save = save;
        _delete = delete;
        SaveCommand = new AsyncCommand(_ => _save(this));
        DeleteCommand = new AsyncCommand(_ => _delete(this));
    }

    public DomainCalendar Calendar { get; }
    public string CatalogLabel => Calendar.DeletedAtUtc is not null ? "Deleted" : Calendar.Visible ? "Visible in schedule" : "Hidden from schedule";
    public string DraftName { get; set; }
    public string DraftColor { get; set; }
    public bool IsVisible { get; set; }
    public string DeleteLabel => Calendar.DeletedAtUtc is null ? "Delete" : "Restore deleted";
    public ICommand SaveCommand { get; }
    public ICommand DeleteCommand { get; }
}

public sealed class CalendarEventRowViewModel
{
    private readonly Func<CalendarEventRowViewModel, Task> _update;
    private readonly Func<CalendarEventRowViewModel, Task> _delete;

    public CalendarEventRowViewModel(CalendarEvent item, Func<CalendarEventRowViewModel, Task> update, Func<CalendarEventRowViewModel, Task> delete)
    {
        Event = item;
        TitleEditor = item.Title;
        DescriptionEditor = item.Description;
        LocationEditor = item.Location ?? string.Empty;
        ColorEditor = item.Color;
        StartText = MainWindowViewModel.FormatLocalDateTime(item.StartAtUtc);
        EndText = MainWindowViewModel.FormatLocalDateTime(item.EndAtUtc);
        RecurrenceEditor = item.RecurrenceRule ?? string.Empty;
        RecurrenceEndText = item.RecurrenceEndUtc is { } recurrenceEnd
            ? MainWindowViewModel.FormatLocalDateTime(recurrenceEnd)
            : string.Empty;
        AllDayEditor = item.AllDay;
        _update = update;
        _delete = delete;
        UpdateCommand = new AsyncCommand(_ => _update(this));
        DeleteCommand = new AsyncCommand(_ => _delete(this));
    }

    public CalendarEvent Event { get; }
    public string TitleEditor { get; set; }
    public string DescriptionEditor { get; set; }
    public string LocationEditor { get; set; }
    public string ColorEditor { get; set; }
    public string StartText { get; set; }
    public string EndText { get; set; }
    public string RecurrenceEditor { get; set; }
    public string RecurrenceEndText { get; set; }
    public bool AllDayEditor { get; set; }
    public string Label => Event.Title;
    public string DetailLabel => Event.AllDay
        ? $"{Event.StartAtUtc.ToLocalTime():ddd, MMM d} · all day"
        : $"{Event.StartAtUtc.ToLocalTime():ddd, MMM d · h:mm tt} – {Event.EndAtUtc.ToLocalTime():h:mm tt}";
    public string LocationLabel => string.IsNullOrWhiteSpace(Event.Location) ? "" : Event.Location;
    public string DescriptionLabel => Event.Description;
    public ICommand UpdateCommand { get; }
    public ICommand DeleteCommand { get; }
}

public sealed class DeletedCalendarEventRowViewModel
{
    private readonly Func<DeletedCalendarEventRowViewModel, Task> _restore;

    public DeletedCalendarEventRowViewModel(CalendarEvent item, Func<DeletedCalendarEventRowViewModel, Task> restore)
    {
        Event = item;
        _restore = restore;
        RestoreCommand = new AsyncCommand(_ => _restore(this));
    }

    public CalendarEvent Event { get; }
    public string Title => Event.Title;
    public string DetailLabel => Event.AllDay
        ? $"{Event.StartAtUtc.ToLocalTime():ddd, MMM d} · all day"
        : $"{Event.StartAtUtc.ToLocalTime():ddd, MMM d · h:mm tt} – {Event.EndAtUtc.ToLocalTime():h:mm tt}";
    public ICommand RestoreCommand { get; }
}

public partial class MainWindowViewModel
{
    internal static string FormatForRow(long milliseconds)
    {
        var totalSeconds = Math.Max(0, milliseconds) / 1000;
        return $"{totalSeconds / 3600:00}:{totalSeconds / 60 % 60:00}:{totalSeconds % 60:00}";
    }
}
