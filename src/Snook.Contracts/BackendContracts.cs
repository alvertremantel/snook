using Snook.Domain;

namespace Snook.Contracts;

public static class ContractInfo
{
    public const int Major = 1;
    public const int Minor = 3;
}

public sealed record OperationRequest(
    Guid OperationId,
    Guid ClientDeviceId,
    long? ExpectedRevision = null);

public sealed record TaskListItem(
    TaskItem Task,
    string ProjectName,
    string BoardName,
    long TrackedMilliseconds,
    bool HasActiveSession);

public sealed record ActiveSessionItem(
    TrackingSession Session,
    string? TaskTitle,
    string? ActivityName,
    long DisplayedMilliseconds);

public sealed record TodaySnapshot(
    DateTimeOffset CapturedAtUtc,
    IReadOnlyList<TaskListItem> PriorityTasks,
    IReadOnlyList<ActiveSessionItem> ActiveSessions,
    long TrackedTodayMilliseconds,
    int OpenTaskCount,
    IReadOnlyList<TrackingSession>? RecoverySessions = null);

public sealed record BootstrapSnapshot(
    Workspace Workspace,
    IReadOnlyList<Board> Boards,
    IReadOnlyList<Project> Projects,
    IReadOnlyList<Activity> Activities,
    IReadOnlyList<Calendar> Calendars,
    TodaySnapshot Today,
    long CommittedCursor,
    HostCapabilities Capabilities,
    WorkspaceSettings? Settings = null,
    IReadOnlyList<ActivityGroup>? ActivityGroups = null,
    DeletedItemsSnapshot? DeletedItems = null);

public sealed record DeletedItemsSnapshot(
    IReadOnlyList<Board> Boards,
    IReadOnlyList<Project> Projects,
    IReadOnlyList<Activity> Activities,
    IReadOnlyList<ActivityGroup> ActivityGroups,
    IReadOnlyList<TaskItem> Tasks,
    IReadOnlyList<Calendar> Calendars,
    IReadOnlyList<CalendarEvent> CalendarEvents);

public sealed record HostCapabilities(
    string HostMode,
    bool Embedded,
    bool DaemonClient,
    bool Backups,
    bool Export,
    bool Sync,
    bool EncryptionAtRest);

public sealed record ChangeNotification(
    long Cursor,
    Guid OperationId,
    string AggregateType,
    Guid AggregateId,
    string ChangeKind,
    long NewRevision,
    DateTimeOffset CommittedAtUtc);

public sealed record HistoryQuery(
    DateTimeOffset RangeStartUtc,
    DateTimeOffset RangeEndUtc,
    string? Search = null,
    int PageSize = 50,
    string? ContinuationToken = null);

public sealed record HistoryPage(
    IReadOnlyList<HistoryItem> Items,
    string? ContinuationToken,
    bool HasMore);

public sealed record CalendarRangeQuery(
    DateTimeOffset RangeStartUtc,
    DateTimeOffset RangeEndUtc,
    int MaximumOccurrences = 500,
    bool IncludeDeleted = false);

public interface IBackendClient : IAsyncDisposable
{
    event EventHandler<ChangeNotification>? Changed;

    // Atomic, revision-checked patch of 1–500 distinct active tasks. Replays return the committed result.
    Task<IReadOnlyList<TaskItem>> BulkUpdateTasksAsync(
        IReadOnlyList<TaskRevision> tasks,
        BulkTaskUpdate update,
        OperationRequest request,
        CancellationToken cancellationToken = default);

    Task<BootstrapSnapshot> GetBootstrapAsync(CancellationToken cancellationToken = default);

    Task<WorkspaceSettings> GetSettingsAsync(CancellationToken cancellationToken = default);

    Task<WorkspaceSettings> UpdateSettingsAsync(
        WorkspaceSettings settings,
        OperationRequest request,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TaskListItem>> SearchTasksAsync(
        string? search = null,
        bool includeCompleted = false,
        bool includeArchived = false,
        bool includeDeleted = false,
        CancellationToken cancellationToken = default);

    Task<Board> CreateBoardAsync(string name, CancellationToken cancellationToken = default);
    Task<Board> UpdateBoardAsync(Guid boardId, BoardUpdate update, OperationRequest request, CancellationToken cancellationToken = default);
    Task<Board> ReorderBoardAsync(Guid boardId, ReorderDirection direction, OperationRequest request, CancellationToken cancellationToken = default);
    Task<Board> ArchiveBoardAsync(Guid boardId, OperationRequest request, CancellationToken cancellationToken = default);
    Task<Board> RestoreBoardAsync(Guid boardId, OperationRequest request, CancellationToken cancellationToken = default);
    Task<Board> DeleteBoardAsync(Guid boardId, OperationRequest request, CancellationToken cancellationToken = default);
    Task<Board> RestoreDeletedBoardAsync(Guid boardId, OperationRequest request, CancellationToken cancellationToken = default);

    Task<Project> CreateProjectAsync(Guid boardId, string name, string description = "", bool starred = false, CancellationToken cancellationToken = default);
    Task<Project> UpdateProjectAsync(Guid projectId, ProjectUpdate update, OperationRequest request, CancellationToken cancellationToken = default);
    Task<Project> ReorderProjectAsync(Guid projectId, ReorderDirection direction, OperationRequest request, CancellationToken cancellationToken = default);
    Task<Project> ArchiveProjectAsync(Guid projectId, OperationRequest request, CancellationToken cancellationToken = default);
    Task<Project> RestoreProjectAsync(Guid projectId, OperationRequest request, CancellationToken cancellationToken = default);
    Task<Project> DeleteProjectAsync(Guid projectId, OperationRequest request, CancellationToken cancellationToken = default);
    Task<Project> RestoreDeletedProjectAsync(Guid projectId, OperationRequest request, CancellationToken cancellationToken = default);

    Task<Activity> CreateActivityAsync(string name, string description = "", SessionLane defaultLane = SessionLane.Foreground, Guid? groupId = null, CancellationToken cancellationToken = default);
    Task<Activity> UpdateActivityAsync(Guid activityId, ActivityUpdate update, OperationRequest request, CancellationToken cancellationToken = default);
    Task<Activity> ArchiveActivityAsync(Guid activityId, OperationRequest request, CancellationToken cancellationToken = default);
    Task<Activity> RestoreActivityAsync(Guid activityId, OperationRequest request, CancellationToken cancellationToken = default);
    Task<Activity> DeleteActivityAsync(Guid activityId, OperationRequest request, CancellationToken cancellationToken = default);
    Task<Activity> RestoreDeletedActivityAsync(Guid activityId, OperationRequest request, CancellationToken cancellationToken = default);

    Task<ActivityGroup> CreateActivityGroupAsync(string name, CancellationToken cancellationToken = default);
    Task<ActivityGroup> UpdateActivityGroupAsync(Guid groupId, ActivityGroupUpdate update, OperationRequest request, CancellationToken cancellationToken = default);
    Task<ActivityGroup> ReorderActivityGroupAsync(Guid groupId, ReorderDirection direction, OperationRequest request, CancellationToken cancellationToken = default);
    Task<ActivityGroup> DeleteActivityGroupAsync(Guid groupId, OperationRequest request, CancellationToken cancellationToken = default);
    Task<ActivityGroup> RestoreDeletedActivityGroupAsync(Guid groupId, OperationRequest request, CancellationToken cancellationToken = default);

    Task<HistoryPage> GetHistoryAsync(
        HistoryQuery query,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SummaryBucket>> GetSummaryAsync(
        DateTimeOffset rangeStartUtc,
        DateTimeOffset rangeEndUtc,
        SummaryGrouping grouping,
        string? timeZone = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ScheduleBlock>> GetCalendarRangeAsync(
        CalendarRangeQuery query,
        CancellationToken cancellationToken = default);

    Task<Calendar> CreateCalendarAsync(
        string name,
        string color = "#6767F2",
        bool visible = true,
        CancellationToken cancellationToken = default);

    Task<Calendar> UpdateCalendarAsync(
        Guid calendarId,
        CalendarUpdate update,
        OperationRequest request,
        CancellationToken cancellationToken = default);

    Task<Calendar> DeleteCalendarAsync(
        Guid calendarId,
        OperationRequest request,
        CancellationToken cancellationToken = default);

    Task<Calendar> RestoreDeletedCalendarAsync(
        Guid calendarId,
        OperationRequest request,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CalendarEvent>> GetCalendarEventsRangeAsync(
        CalendarRangeQuery query,
        CancellationToken cancellationToken = default);

    Task<CalendarEvent> CreateCalendarEventAsync(
        Guid calendarId,
        string title,
        DateTimeOffset startAtUtc,
        DateTimeOffset endAtUtc,
        string description = "",
        string? location = null,
        string color = "#6767F2",
        bool allDay = false,
        string timeZone = "UTC",
        string? recurrenceRule = null,
        DateTimeOffset? recurrenceEndUtc = null,
        CancellationToken cancellationToken = default);

    Task<CalendarEvent> UpdateCalendarEventAsync(
        Guid eventId,
        CalendarEventUpdate update,
        OperationRequest request,
        CancellationToken cancellationToken = default);

    Task<CalendarEvent> DeleteCalendarEventAsync(
        Guid eventId,
        OperationRequest request,
        CancellationToken cancellationToken = default);

    Task<CalendarEvent> RestoreDeletedCalendarEventAsync(
        Guid eventId,
        OperationRequest request,
        CancellationToken cancellationToken = default);

    Task<CalendarEventOccurrenceOverride> UpsertCalendarEventExceptionAsync(
        Guid eventId,
        DateTimeOffset originalStartAtUtc,
        DateTimeOffset? newStartAtUtc,
        DateTimeOffset? newEndAtUtc,
        string? titleOverride,
        bool cancelled,
        OperationRequest request,
        CancellationToken cancellationToken = default);

    Task DeleteCalendarEventExceptionAsync(
        Guid exceptionId,
        OperationRequest request,
        CancellationToken cancellationToken = default);

    Task<ScheduleBlock> CreateScheduleBlockAsync(
        Guid calendarId,
        Guid? taskId,
        Guid? activityId,
        string? titleOverride,
        DateTimeOffset startAtUtc,
        DateTimeOffset endAtUtc,
        string timeZone,
        string? recurrenceRule = null,
        DateTimeOffset? recurrenceEndUtc = null,
        CancellationToken cancellationToken = default);

    Task<ScheduleBlock> UpdateScheduleBlockAsync(
        Guid blockId,
        ScheduleBlockUpdate update,
        OperationRequest request,
        CancellationToken cancellationToken = default);

    Task<TaskItem> CreateTaskAsync(
        Guid projectId,
        string title,
        Priority priority = Priority.None,
        DateOnly? dueDate = null,
        CancellationToken cancellationToken = default);

    Task<TaskItem> UpdateTaskAsync(
        Guid taskId,
        TaskUpdate update,
        OperationRequest request,
        CancellationToken cancellationToken = default);

    Task<TaskItem> MoveTaskAsync(
        Guid taskId,
        Guid projectId,
        OperationRequest request,
        CancellationToken cancellationToken = default);

    Task<TaskItem> ArchiveTaskAsync(
        Guid taskId,
        OperationRequest request,
        CancellationToken cancellationToken = default);

    Task<TaskItem> RestoreTaskAsync(
        Guid taskId,
        OperationRequest request,
        CancellationToken cancellationToken = default);

    Task<TaskItem> DeleteTaskAsync(
        Guid taskId,
        OperationRequest request,
        CancellationToken cancellationToken = default);

    Task<TaskItem> RestoreDeletedTaskAsync(
        Guid taskId,
        OperationRequest request,
        CancellationToken cancellationToken = default);

    Task<TaskDetails> GetTaskDetailsAsync(
        Guid taskId,
        CancellationToken cancellationToken = default);

    Task<ProjectDetails> GetProjectDetailsAsync(
        Guid projectId,
        CancellationToken cancellationToken = default);

    Task<TaskLink> AddTaskLinkAsync(
        Guid taskId,
        string? label,
        string uri,
        string kind = "reference",
        CancellationToken cancellationToken = default);

    Task<Tag> AddTaskTagAsync(
        Guid taskId,
        string displayName,
        string? color = null,
        CancellationToken cancellationToken = default);

    Task<Tag> AddProjectTagAsync(
        Guid projectId,
        string displayName,
        string? color = null,
        CancellationToken cancellationToken = default);

    Task<Tag> AddActivityTagAsync(
        Guid activityId,
        string displayName,
        string? color = null,
        CancellationToken cancellationToken = default);

    Task<TaskItem> CompleteTaskAsync(
        Guid taskId,
        OperationRequest request,
        CancellationToken cancellationToken = default);

    Task<TaskItem> ReopenTaskAsync(
        Guid taskId,
        OperationRequest request,
        CancellationToken cancellationToken = default);

    Task AddTaskDependencyAsync(
        Guid taskId,
        Guid prerequisiteTaskId,
        CancellationToken cancellationToken = default);

    Task<TrackingSession> StartSessionAsync(
        Guid? taskId,
        Guid? activityId,
        SessionLane lane,
        OperationRequest request,
        CancellationToken cancellationToken = default);

    Task<TrackingSession> PauseSessionAsync(
        Guid sessionId,
        OperationRequest request,
        CancellationToken cancellationToken = default);

    Task<TrackingSession> ResumeSessionAsync(
        Guid sessionId,
        OperationRequest request,
        CancellationToken cancellationToken = default);

    Task<TrackingSession> StopSessionAsync(
        Guid sessionId,
        string? notes,
        OperationRequest request,
        CancellationToken cancellationToken = default);

    Task<TrackingSession> CreateManualSessionAsync(
        Guid? taskId,
        Guid? activityId,
        DateTimeOffset startedAtUtc,
        DateTimeOffset endedAtUtc,
        string? notes,
        OperationRequest? request = null,
        CancellationToken cancellationToken = default);

    Task<TrackingSession> CorrectSessionAsync(
        Guid sessionId,
        SessionCorrection correction,
        OperationRequest request,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TrackingCorrection>> GetSessionCorrectionsAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default);

    Task<TrackingSession> ResolveRecoveryAsync(
        Guid sessionId,
        RecoveryDecision decision,
        OperationRequest request,
        CancellationToken cancellationToken = default);

    Task<BackupResult> CreateBackupAsync(
        string destinationPath,
        CancellationToken cancellationToken = default);

    Task<ExportResult> ExportJsonAsync(
        string destinationPath,
        CancellationToken cancellationToken = default);

    Task<ExportResult> ExportCsvAsync(
        string destinationPath,
        DateTimeOffset rangeStartUtc,
        DateTimeOffset rangeEndUtc,
        CancellationToken cancellationToken = default);

    Task<RestoreResult> RestoreBackupAsync(
        string sourcePath,
        CancellationToken cancellationToken = default);
}

public sealed record BackupResult(string Path, long Bytes, string Sha256, string? ManifestPath = null);

public sealed record ExportResult(string Path, long Bytes, string Sha256, int SchemaVersion);

public sealed record RestoreResult(string SourcePath, long Bytes, string Sha256);
