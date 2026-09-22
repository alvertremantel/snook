using Snook.Contracts;
using Snook.Domain;

namespace Snook.Application;

// Keep every IBackendClient call behind the session's admission/drain guard.
// The interface implementation makes contract additions compile-time obligations.
internal sealed partial class WorkspaceBackendClient
{
    public Task<IReadOnlyList<Journal>> GetJournalsAsync(bool includeDeleted = false, CancellationToken cancellationToken = default)
        => RunAsync(() => _backend.GetJournalsAsync(includeDeleted, cancellationToken), cancellationToken);

    public Task<Journal> CreateJournalAsync(JournalDefinition definition, OperationRequest request, CancellationToken cancellationToken = default)
        => RunAsync(() => _backend.CreateJournalAsync(definition, request, cancellationToken), cancellationToken);

    public Task<Journal> UpdateJournalAsync(Guid journalId, JournalDefinition definition, OperationRequest request, CancellationToken cancellationToken = default)
        => RunAsync(() => _backend.UpdateJournalAsync(journalId, definition, request, cancellationToken), cancellationToken);

    public Task<Journal> SetJournalDeletedAsync(Guid journalId, bool deleted, OperationRequest request, CancellationToken cancellationToken = default)
        => RunAsync(() => _backend.SetJournalDeletedAsync(journalId, deleted, request, cancellationToken), cancellationToken);

    public Task<JournalEntryPage> GetJournalEntriesAsync(JournalEntryQuery query, CancellationToken cancellationToken = default)
        => RunAsync(() => _backend.GetJournalEntriesAsync(query, cancellationToken), cancellationToken);

    public Task<JournalEntry> GetJournalEntryAsync(Guid entryId, CancellationToken cancellationToken = default)
        => RunAsync(() => _backend.GetJournalEntryAsync(entryId, cancellationToken), cancellationToken);

    public Task<IReadOnlyList<string>> GetJournalTagsAsync(Guid? journalId = null, CancellationToken cancellationToken = default)
        => RunAsync(() => _backend.GetJournalTagsAsync(journalId, cancellationToken), cancellationToken);

    public Task<JournalEntry> CreateJournalEntryAsync(JournalEntryDefinition definition, OperationRequest request, CancellationToken cancellationToken = default)
        => RunAsync(() => _backend.CreateJournalEntryAsync(definition, request, cancellationToken), cancellationToken);

    public Task<JournalEntry> UpdateJournalEntryAsync(Guid entryId, JournalEntryDefinition definition, OperationRequest request, CancellationToken cancellationToken = default)
        => RunAsync(() => _backend.UpdateJournalEntryAsync(entryId, definition, request, cancellationToken), cancellationToken);

    public Task<JournalEntry> SetJournalEntryDeletedAsync(Guid entryId, bool deleted, OperationRequest request, CancellationToken cancellationToken = default)
        => RunAsync(() => _backend.SetJournalEntryDeletedAsync(entryId, deleted, request, cancellationToken), cancellationToken);

    public Task<IReadOnlyList<HabitProgress>> GetHabitsAsync(HabitQuery query, CancellationToken cancellationToken = default)
        => RunAsync(() => _backend.GetHabitsAsync(query, cancellationToken), cancellationToken);

    public Task<Habit> CreateHabitAsync(HabitDefinition definition, OperationRequest request, CancellationToken cancellationToken = default)
        => RunAsync(() => _backend.CreateHabitAsync(definition, request, cancellationToken), cancellationToken);

    public Task<Habit> UpdateHabitAsync(Guid habitId, HabitUpdate update, OperationRequest request, CancellationToken cancellationToken = default)
        => RunAsync(() => _backend.UpdateHabitAsync(habitId, update, request, cancellationToken), cancellationToken);

    public Task<Habit> SetHabitCompletionAsync(Guid habitId, DateOnly day, bool completed, OperationRequest request, CancellationToken cancellationToken = default)
        => RunAsync(() => _backend.SetHabitCompletionAsync(habitId, day, completed, request, cancellationToken), cancellationToken);

    public Task<Habit> SetHabitArchivedAsync(Guid habitId, bool archived, OperationRequest request, CancellationToken cancellationToken = default)
        => RunAsync(() => _backend.SetHabitArchivedAsync(habitId, archived, request, cancellationToken), cancellationToken);

    public Task<Habit> SetHabitDeletedAsync(Guid habitId, bool deleted, OperationRequest request, CancellationToken cancellationToken = default)
        => RunAsync(() => _backend.SetHabitDeletedAsync(habitId, deleted, request, cancellationToken), cancellationToken);

    public Task<IReadOnlyList<TaskItem>> BulkUpdateTasksAsync(IReadOnlyList<TaskRevision> tasks, BulkTaskUpdate update, OperationRequest request, CancellationToken cancellationToken = default)
        => RunAsync(() => _backend.BulkUpdateTasksAsync(tasks, update, request, cancellationToken), cancellationToken);

    public Task<BootstrapSnapshot> GetBootstrapAsync(CancellationToken cancellationToken = default)
        => RunAsync(() => _backend.GetBootstrapAsync(cancellationToken), cancellationToken);

    public Task<WorkspaceSettings> GetSettingsAsync(CancellationToken cancellationToken = default)
        => RunAsync(() => _backend.GetSettingsAsync(cancellationToken), cancellationToken);

    public Task<WorkspaceSettings> UpdateSettingsAsync(WorkspaceSettings settings, OperationRequest request, CancellationToken cancellationToken = default)
        => RunAsync(() => _backend.UpdateSettingsAsync(settings, request, cancellationToken), cancellationToken);

    public Task<IReadOnlyList<TaskListItem>> SearchTasksAsync(string? search = null, bool includeCompleted = false, bool includeArchived = false, bool includeDeleted = false, CancellationToken cancellationToken = default)
        => RunAsync(() => _backend.SearchTasksAsync(search, includeCompleted, includeArchived, includeDeleted, cancellationToken), cancellationToken);

    public Task<Board> CreateBoardAsync(string name, OperationRequest? request = null, CancellationToken cancellationToken = default)
        => RunAsync(() => _backend.CreateBoardAsync(name, request, cancellationToken), cancellationToken);

    public Task<Board> UpdateBoardAsync(Guid boardId, BoardUpdate update, OperationRequest request, CancellationToken cancellationToken = default)
        => RunAsync(() => _backend.UpdateBoardAsync(boardId, update, request, cancellationToken), cancellationToken);

    public Task<Board> ReorderBoardAsync(Guid boardId, ReorderDirection direction, OperationRequest request, CancellationToken cancellationToken = default)
        => RunAsync(() => _backend.ReorderBoardAsync(boardId, direction, request, cancellationToken), cancellationToken);

    public Task<Board> ArchiveBoardAsync(Guid boardId, OperationRequest request, CancellationToken cancellationToken = default)
        => RunAsync(() => _backend.ArchiveBoardAsync(boardId, request, cancellationToken), cancellationToken);

    public Task<Board> RestoreBoardAsync(Guid boardId, OperationRequest request, CancellationToken cancellationToken = default)
        => RunAsync(() => _backend.RestoreBoardAsync(boardId, request, cancellationToken), cancellationToken);

    public Task<Board> DeleteBoardAsync(Guid boardId, OperationRequest request, CancellationToken cancellationToken = default)
        => RunAsync(() => _backend.DeleteBoardAsync(boardId, request, cancellationToken), cancellationToken);

    public Task<Board> RestoreDeletedBoardAsync(Guid boardId, OperationRequest request, CancellationToken cancellationToken = default)
        => RunAsync(() => _backend.RestoreDeletedBoardAsync(boardId, request, cancellationToken), cancellationToken);

    public Task<Project> CreateProjectAsync(Guid boardId, string name, string description = "", bool starred = false, OperationRequest? request = null, CancellationToken cancellationToken = default)
        => RunAsync(() => _backend.CreateProjectAsync(boardId, name, description, starred, request, cancellationToken), cancellationToken);

    public Task<Project> UpdateProjectAsync(Guid projectId, ProjectUpdate update, OperationRequest request, CancellationToken cancellationToken = default)
        => RunAsync(() => _backend.UpdateProjectAsync(projectId, update, request, cancellationToken), cancellationToken);

    public Task<Project> ReorderProjectAsync(Guid projectId, ReorderDirection direction, OperationRequest request, CancellationToken cancellationToken = default)
        => RunAsync(() => _backend.ReorderProjectAsync(projectId, direction, request, cancellationToken), cancellationToken);

    public Task<Project> ArchiveProjectAsync(Guid projectId, OperationRequest request, CancellationToken cancellationToken = default)
        => RunAsync(() => _backend.ArchiveProjectAsync(projectId, request, cancellationToken), cancellationToken);

    public Task<Project> RestoreProjectAsync(Guid projectId, OperationRequest request, CancellationToken cancellationToken = default)
        => RunAsync(() => _backend.RestoreProjectAsync(projectId, request, cancellationToken), cancellationToken);

    public Task<Project> DeleteProjectAsync(Guid projectId, OperationRequest request, CancellationToken cancellationToken = default)
        => RunAsync(() => _backend.DeleteProjectAsync(projectId, request, cancellationToken), cancellationToken);

    public Task<Project> RestoreDeletedProjectAsync(Guid projectId, OperationRequest request, CancellationToken cancellationToken = default)
        => RunAsync(() => _backend.RestoreDeletedProjectAsync(projectId, request, cancellationToken), cancellationToken);

    public Task<Activity> CreateActivityAsync(string name, string description = "", SessionLane defaultLane = SessionLane.Foreground, Guid? groupId = null, OperationRequest? request = null, CancellationToken cancellationToken = default)
        => RunAsync(() => _backend.CreateActivityAsync(name, description, defaultLane, groupId, request, cancellationToken), cancellationToken);

    public Task<Activity> UpdateActivityAsync(Guid activityId, ActivityUpdate update, OperationRequest request, CancellationToken cancellationToken = default)
        => RunAsync(() => _backend.UpdateActivityAsync(activityId, update, request, cancellationToken), cancellationToken);

    public Task<Activity> ArchiveActivityAsync(Guid activityId, OperationRequest request, CancellationToken cancellationToken = default)
        => RunAsync(() => _backend.ArchiveActivityAsync(activityId, request, cancellationToken), cancellationToken);

    public Task<Activity> RestoreActivityAsync(Guid activityId, OperationRequest request, CancellationToken cancellationToken = default)
        => RunAsync(() => _backend.RestoreActivityAsync(activityId, request, cancellationToken), cancellationToken);

    public Task<Activity> DeleteActivityAsync(Guid activityId, OperationRequest request, CancellationToken cancellationToken = default)
        => RunAsync(() => _backend.DeleteActivityAsync(activityId, request, cancellationToken), cancellationToken);

    public Task<Activity> RestoreDeletedActivityAsync(Guid activityId, OperationRequest request, CancellationToken cancellationToken = default)
        => RunAsync(() => _backend.RestoreDeletedActivityAsync(activityId, request, cancellationToken), cancellationToken);

    public Task<ActivityGroup> CreateActivityGroupAsync(string name, OperationRequest? request = null, CancellationToken cancellationToken = default)
        => RunAsync(() => _backend.CreateActivityGroupAsync(name, request, cancellationToken), cancellationToken);

    public Task<ActivityGroup> UpdateActivityGroupAsync(Guid groupId, ActivityGroupUpdate update, OperationRequest request, CancellationToken cancellationToken = default)
        => RunAsync(() => _backend.UpdateActivityGroupAsync(groupId, update, request, cancellationToken), cancellationToken);

    public Task<ActivityGroup> ReorderActivityGroupAsync(Guid groupId, ReorderDirection direction, OperationRequest request, CancellationToken cancellationToken = default)
        => RunAsync(() => _backend.ReorderActivityGroupAsync(groupId, direction, request, cancellationToken), cancellationToken);

    public Task<ActivityGroup> DeleteActivityGroupAsync(Guid groupId, OperationRequest request, CancellationToken cancellationToken = default)
        => RunAsync(() => _backend.DeleteActivityGroupAsync(groupId, request, cancellationToken), cancellationToken);

    public Task<ActivityGroup> RestoreDeletedActivityGroupAsync(Guid groupId, OperationRequest request, CancellationToken cancellationToken = default)
        => RunAsync(() => _backend.RestoreDeletedActivityGroupAsync(groupId, request, cancellationToken), cancellationToken);

    public Task<HistoryPage> GetHistoryAsync(HistoryQuery query, CancellationToken cancellationToken = default)
        => RunAsync(() => _backend.GetHistoryAsync(query, cancellationToken), cancellationToken);

    public Task<IReadOnlyList<SummaryBucket>> GetSummaryAsync(DateTimeOffset rangeStartUtc, DateTimeOffset rangeEndUtc, SummaryGrouping grouping, string? timeZone = null, CancellationToken cancellationToken = default)
        => RunAsync(() => _backend.GetSummaryAsync(rangeStartUtc, rangeEndUtc, grouping, timeZone, cancellationToken), cancellationToken);

    public Task<IReadOnlyList<ScheduleBlock>> GetCalendarRangeAsync(CalendarRangeQuery query, CancellationToken cancellationToken = default)
        => RunAsync(() => _backend.GetCalendarRangeAsync(query, cancellationToken), cancellationToken);

    public Task<Calendar> CreateCalendarAsync(string name, string color = "#6767F2", bool visible = true, OperationRequest? request = null, CancellationToken cancellationToken = default)
        => RunAsync(() => _backend.CreateCalendarAsync(name, color, visible, request, cancellationToken), cancellationToken);

    public Task<Calendar> UpdateCalendarAsync(Guid calendarId, CalendarUpdate update, OperationRequest request, CancellationToken cancellationToken = default)
        => RunAsync(() => _backend.UpdateCalendarAsync(calendarId, update, request, cancellationToken), cancellationToken);

    public Task<Calendar> DeleteCalendarAsync(Guid calendarId, OperationRequest request, CancellationToken cancellationToken = default)
        => RunAsync(() => _backend.DeleteCalendarAsync(calendarId, request, cancellationToken), cancellationToken);

    public Task<Calendar> RestoreDeletedCalendarAsync(Guid calendarId, OperationRequest request, CancellationToken cancellationToken = default)
        => RunAsync(() => _backend.RestoreDeletedCalendarAsync(calendarId, request, cancellationToken), cancellationToken);

    public Task<IReadOnlyList<CalendarEvent>> GetCalendarEventsRangeAsync(CalendarRangeQuery query, CancellationToken cancellationToken = default)
        => RunAsync(() => _backend.GetCalendarEventsRangeAsync(query, cancellationToken), cancellationToken);

    public Task<CalendarEvent> CreateCalendarEventAsync(Guid calendarId, string title, DateTimeOffset startAtUtc, DateTimeOffset endAtUtc, string description = "", string? location = null, string color = "#6767F2", bool allDay = false, string timeZone = "UTC", string? recurrenceRule = null, DateTimeOffset? recurrenceEndUtc = null, OperationRequest? request = null, CancellationToken cancellationToken = default)
        => RunAsync(() => _backend.CreateCalendarEventAsync(calendarId, title, startAtUtc, endAtUtc, description, location, color, allDay, timeZone, recurrenceRule, recurrenceEndUtc, request, cancellationToken), cancellationToken);

    public Task<CalendarEvent> UpdateCalendarEventAsync(Guid eventId, CalendarEventUpdate update, OperationRequest request, CancellationToken cancellationToken = default)
        => RunAsync(() => _backend.UpdateCalendarEventAsync(eventId, update, request, cancellationToken), cancellationToken);

    public Task<CalendarEvent> DeleteCalendarEventAsync(Guid eventId, OperationRequest request, CancellationToken cancellationToken = default)
        => RunAsync(() => _backend.DeleteCalendarEventAsync(eventId, request, cancellationToken), cancellationToken);

    public Task<CalendarEvent> RestoreDeletedCalendarEventAsync(Guid eventId, OperationRequest request, CancellationToken cancellationToken = default)
        => RunAsync(() => _backend.RestoreDeletedCalendarEventAsync(eventId, request, cancellationToken), cancellationToken);

    public Task<CalendarEventOccurrenceOverride> UpsertCalendarEventExceptionAsync(Guid eventId, DateTimeOffset originalStartAtUtc, DateTimeOffset? newStartAtUtc, DateTimeOffset? newEndAtUtc, string? titleOverride, bool cancelled, OperationRequest request, CancellationToken cancellationToken = default)
        => RunAsync(() => _backend.UpsertCalendarEventExceptionAsync(eventId, originalStartAtUtc, newStartAtUtc, newEndAtUtc, titleOverride, cancelled, request, cancellationToken), cancellationToken);

    public Task DeleteCalendarEventExceptionAsync(Guid exceptionId, OperationRequest request, CancellationToken cancellationToken = default)
        => RunAsync(() => _backend.DeleteCalendarEventExceptionAsync(exceptionId, request, cancellationToken), cancellationToken);

    public Task<ScheduleBlock> CreateScheduleBlockAsync(Guid calendarId, Guid? taskId, Guid? activityId, string? titleOverride, DateTimeOffset startAtUtc, DateTimeOffset endAtUtc, string timeZone, string? recurrenceRule = null, DateTimeOffset? recurrenceEndUtc = null, OperationRequest? request = null, CancellationToken cancellationToken = default)
        => RunAsync(() => _backend.CreateScheduleBlockAsync(calendarId, taskId, activityId, titleOverride, startAtUtc, endAtUtc, timeZone, recurrenceRule, recurrenceEndUtc, request, cancellationToken), cancellationToken);

    public Task<ScheduleBlock> UpdateScheduleBlockAsync(Guid blockId, ScheduleBlockUpdate update, OperationRequest request, CancellationToken cancellationToken = default)
        => RunAsync(() => _backend.UpdateScheduleBlockAsync(blockId, update, request, cancellationToken), cancellationToken);

    public Task<TaskItem> CreateTaskAsync(Guid projectId, string title, Priority priority = Priority.None, DateOnly? dueDate = null, OperationRequest? request = null, CancellationToken cancellationToken = default)
        => RunAsync(() => _backend.CreateTaskAsync(projectId, title, priority, dueDate, request, cancellationToken), cancellationToken);

    public Task<TaskItem> UpdateTaskAsync(Guid taskId, TaskUpdate update, OperationRequest request, CancellationToken cancellationToken = default)
        => RunAsync(() => _backend.UpdateTaskAsync(taskId, update, request, cancellationToken), cancellationToken);

    public Task<TaskItem> MoveTaskAsync(Guid taskId, Guid projectId, OperationRequest request, CancellationToken cancellationToken = default)
        => RunAsync(() => _backend.MoveTaskAsync(taskId, projectId, request, cancellationToken), cancellationToken);

    public Task<TaskItem> ArchiveTaskAsync(Guid taskId, OperationRequest request, CancellationToken cancellationToken = default)
        => RunAsync(() => _backend.ArchiveTaskAsync(taskId, request, cancellationToken), cancellationToken);

    public Task<TaskItem> RestoreTaskAsync(Guid taskId, OperationRequest request, CancellationToken cancellationToken = default)
        => RunAsync(() => _backend.RestoreTaskAsync(taskId, request, cancellationToken), cancellationToken);

    public Task<TaskItem> DeleteTaskAsync(Guid taskId, OperationRequest request, CancellationToken cancellationToken = default)
        => RunAsync(() => _backend.DeleteTaskAsync(taskId, request, cancellationToken), cancellationToken);

    public Task<TaskItem> RestoreDeletedTaskAsync(Guid taskId, OperationRequest request, CancellationToken cancellationToken = default)
        => RunAsync(() => _backend.RestoreDeletedTaskAsync(taskId, request, cancellationToken), cancellationToken);

    public Task<TaskDetails> GetTaskDetailsAsync(Guid taskId, CancellationToken cancellationToken = default)
        => RunAsync(() => _backend.GetTaskDetailsAsync(taskId, cancellationToken), cancellationToken);

    public Task<ProjectDetails> GetProjectDetailsAsync(Guid projectId, CancellationToken cancellationToken = default)
        => RunAsync(() => _backend.GetProjectDetailsAsync(projectId, cancellationToken), cancellationToken);

    public Task<TaskLink> AddTaskLinkAsync(Guid taskId, string? label, string uri, string kind = "reference", OperationRequest? request = null, CancellationToken cancellationToken = default)
        => RunAsync(() => _backend.AddTaskLinkAsync(taskId, label, uri, kind, request, cancellationToken), cancellationToken);

    public Task<Tag> AddTaskTagAsync(Guid taskId, string displayName, string? color = null, OperationRequest? request = null, CancellationToken cancellationToken = default)
        => RunAsync(() => _backend.AddTaskTagAsync(taskId, displayName, color, request, cancellationToken), cancellationToken);

    public Task<Tag> AddProjectTagAsync(Guid projectId, string displayName, string? color = null, OperationRequest? request = null, CancellationToken cancellationToken = default)
        => RunAsync(() => _backend.AddProjectTagAsync(projectId, displayName, color, request, cancellationToken), cancellationToken);

    public Task<Tag> AddActivityTagAsync(Guid activityId, string displayName, string? color = null, OperationRequest? request = null, CancellationToken cancellationToken = default)
        => RunAsync(() => _backend.AddActivityTagAsync(activityId, displayName, color, request, cancellationToken), cancellationToken);

    public Task<TaskItem> CompleteTaskAsync(Guid taskId, OperationRequest request, CancellationToken cancellationToken = default)
        => RunAsync(() => _backend.CompleteTaskAsync(taskId, request, cancellationToken), cancellationToken);

    public Task<TaskItem> ReopenTaskAsync(Guid taskId, OperationRequest request, CancellationToken cancellationToken = default)
        => RunAsync(() => _backend.ReopenTaskAsync(taskId, request, cancellationToken), cancellationToken);

    public Task AddTaskDependencyAsync(Guid taskId, Guid prerequisiteTaskId, OperationRequest? request = null, CancellationToken cancellationToken = default)
        => RunAsync(() => _backend.AddTaskDependencyAsync(taskId, prerequisiteTaskId, request, cancellationToken), cancellationToken);

    public Task<TrackingSession> StartSessionAsync(Guid? taskId, Guid? activityId, SessionLane lane, OperationRequest request, CancellationToken cancellationToken = default)
        => RunAsync(() => _backend.StartSessionAsync(taskId, activityId, lane, request, cancellationToken), cancellationToken);

    public Task<TrackingSession> PauseSessionAsync(Guid sessionId, OperationRequest request, CancellationToken cancellationToken = default)
        => RunAsync(() => _backend.PauseSessionAsync(sessionId, request, cancellationToken), cancellationToken);

    public Task<TrackingSession> ResumeSessionAsync(Guid sessionId, OperationRequest request, CancellationToken cancellationToken = default)
        => RunAsync(() => _backend.ResumeSessionAsync(sessionId, request, cancellationToken), cancellationToken);

    public Task<TrackingSession> StopSessionAsync(Guid sessionId, string? notes, OperationRequest request, CancellationToken cancellationToken = default)
        => RunAsync(() => _backend.StopSessionAsync(sessionId, notes, request, cancellationToken), cancellationToken);

    public Task<TrackingSession> CreateManualSessionAsync(Guid? taskId, Guid? activityId, DateTimeOffset startedAtUtc, DateTimeOffset endedAtUtc, string? notes, OperationRequest? request = null, CancellationToken cancellationToken = default)
        => RunAsync(() => _backend.CreateManualSessionAsync(taskId, activityId, startedAtUtc, endedAtUtc, notes, request, cancellationToken), cancellationToken);

    public Task<TrackingSession> CorrectSessionAsync(Guid sessionId, SessionCorrection correction, OperationRequest request, CancellationToken cancellationToken = default)
        => RunAsync(() => _backend.CorrectSessionAsync(sessionId, correction, request, cancellationToken), cancellationToken);

    public Task<IReadOnlyList<TrackingCorrection>> GetSessionCorrectionsAsync(Guid sessionId, CancellationToken cancellationToken = default)
        => RunAsync(() => _backend.GetSessionCorrectionsAsync(sessionId, cancellationToken), cancellationToken);

    public Task<TrackingSession> ResolveRecoveryAsync(Guid sessionId, RecoveryDecision decision, OperationRequest request, CancellationToken cancellationToken = default)
        => RunAsync(() => _backend.ResolveRecoveryAsync(sessionId, decision, request, cancellationToken), cancellationToken);

    public Task<BackupResult> CreateBackupAsync(string destinationPath, CancellationToken cancellationToken = default)
        => RunAsync(() => _backend.CreateBackupAsync(destinationPath, cancellationToken), cancellationToken);

    public Task<ExportResult> ExportJsonAsync(string destinationPath, CancellationToken cancellationToken = default)
        => RunAsync(() => _backend.ExportJsonAsync(destinationPath, cancellationToken), cancellationToken);

    public Task<ExportResult> ExportCsvAsync(string destinationPath, DateTimeOffset rangeStartUtc, DateTimeOffset rangeEndUtc, CancellationToken cancellationToken = default)
        => RunAsync(() => _backend.ExportCsvAsync(destinationPath, rangeStartUtc, rangeEndUtc, cancellationToken), cancellationToken);

    public Task<BackupInspection> InspectBackupAsync(string sourcePath, CancellationToken cancellationToken = default)
        => RunAsync(() => _backend.InspectBackupAsync(sourcePath, cancellationToken), cancellationToken);

    public Task<RestoreResult> RestoreBackupAsync(string sourcePath, CancellationToken cancellationToken = default)
        => Task.FromException<RestoreResult>(new SnookException(SnookErrorCode.InvalidTransition, "Restore must use the workspace session handoff."));
}
