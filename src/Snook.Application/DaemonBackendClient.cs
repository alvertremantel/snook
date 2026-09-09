using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Snook.Contracts;
using Snook.Domain;

namespace Snook.Application;

public sealed class DaemonBackendClient : IBackendClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient;
    private readonly Uri _endpoint;
    private readonly string _token;
    private readonly CancellationTokenSource _changesCancellation = new();
    private readonly Task _changesTask;
    private bool _disposed;

    public DaemonBackendClient(Uri endpoint, string token, HttpMessageHandler? handler = null)
    {
        _endpoint = endpoint.AbsoluteUri.EndsWith('/') ? endpoint : new Uri(endpoint.AbsoluteUri + "/");
        _token = Guard.Required(token, "token", 512);
        _httpClient = handler is null ? new HttpClient() : new HttpClient(handler);
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
        _changesTask = ListenForChangesAsync(_changesCancellation.Token);
    }

    private event EventHandler<ChangeNotification>? ChangedHandlers;
    public event EventHandler<ChangeNotification>? Changed
    {
        add => ChangedHandlers += value;
        remove => ChangedHandlers -= value;
    }

    public async Task<BootstrapSnapshot> GetBootstrapAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await CallAsync<BootstrapSnapshot>(nameof(GetBootstrapAsync), cancellationToken);
        return snapshot with
        {
            Capabilities = snapshot.Capabilities with
            {
                HostMode = "daemon-client",
                Embedded = false,
                DaemonClient = true
            }
        };
    }

    public Task<WorkspaceSettings> GetSettingsAsync(CancellationToken cancellationToken = default)
        => CallAsync<WorkspaceSettings>(nameof(GetSettingsAsync), cancellationToken);

    public Task<WorkspaceSettings> UpdateSettingsAsync(WorkspaceSettings settings, OperationRequest request, CancellationToken cancellationToken = default)
        => CallAsync<WorkspaceSettings>(nameof(UpdateSettingsAsync), cancellationToken, settings, request);

    public async Task<IReadOnlyList<TaskListItem>> SearchTasksAsync(string? search = null, bool includeCompleted = false, bool includeArchived = false, CancellationToken cancellationToken = default)
        => await CallAsync<TaskListItem[]>(nameof(SearchTasksAsync), cancellationToken, search, includeCompleted, includeArchived);

    public Task<Board> CreateBoardAsync(string name, CancellationToken cancellationToken = default)
        => CallAsync<Board>(nameof(CreateBoardAsync), cancellationToken, name);

    public Task<Board> UpdateBoardAsync(Guid boardId, BoardUpdate update, OperationRequest request, CancellationToken cancellationToken = default)
        => CallAsync<Board>(nameof(UpdateBoardAsync), cancellationToken, boardId, update, request);

    public Task<Board> ReorderBoardAsync(Guid boardId, ReorderDirection direction, OperationRequest request, CancellationToken cancellationToken = default)
        => CallAsync<Board>(nameof(ReorderBoardAsync), cancellationToken, boardId, direction, request);

    public Task<Board> ArchiveBoardAsync(Guid boardId, OperationRequest request, CancellationToken cancellationToken = default)
        => CallAsync<Board>(nameof(ArchiveBoardAsync), cancellationToken, boardId, request);

    public Task<Board> RestoreBoardAsync(Guid boardId, OperationRequest request, CancellationToken cancellationToken = default)
        => CallAsync<Board>(nameof(RestoreBoardAsync), cancellationToken, boardId, request);

    public Task<Board> DeleteBoardAsync(Guid boardId, OperationRequest request, CancellationToken cancellationToken = default)
        => CallAsync<Board>(nameof(DeleteBoardAsync), cancellationToken, boardId, request);

    public Task<Board> RestoreDeletedBoardAsync(Guid boardId, OperationRequest request, CancellationToken cancellationToken = default)
        => CallAsync<Board>(nameof(RestoreDeletedBoardAsync), cancellationToken, boardId, request);

    public Task<Project> CreateProjectAsync(Guid boardId, string name, string description = "", bool starred = false, CancellationToken cancellationToken = default)
        => CallAsync<Project>(nameof(CreateProjectAsync), cancellationToken, boardId, name, description, starred);

    public Task<Project> UpdateProjectAsync(Guid projectId, ProjectUpdate update, OperationRequest request, CancellationToken cancellationToken = default)
        => CallAsync<Project>(nameof(UpdateProjectAsync), cancellationToken, projectId, update, request);

    public Task<Project> ReorderProjectAsync(Guid projectId, ReorderDirection direction, OperationRequest request, CancellationToken cancellationToken = default)
        => CallAsync<Project>(nameof(ReorderProjectAsync), cancellationToken, projectId, direction, request);

    public Task<Project> ArchiveProjectAsync(Guid projectId, OperationRequest request, CancellationToken cancellationToken = default)
        => CallAsync<Project>(nameof(ArchiveProjectAsync), cancellationToken, projectId, request);

    public Task<Project> RestoreProjectAsync(Guid projectId, OperationRequest request, CancellationToken cancellationToken = default)
        => CallAsync<Project>(nameof(RestoreProjectAsync), cancellationToken, projectId, request);

    public Task<Project> DeleteProjectAsync(Guid projectId, OperationRequest request, CancellationToken cancellationToken = default)
        => CallAsync<Project>(nameof(DeleteProjectAsync), cancellationToken, projectId, request);

    public Task<Project> RestoreDeletedProjectAsync(Guid projectId, OperationRequest request, CancellationToken cancellationToken = default)
        => CallAsync<Project>(nameof(RestoreDeletedProjectAsync), cancellationToken, projectId, request);

    public Task<Activity> CreateActivityAsync(string name, string description = "", SessionLane defaultLane = SessionLane.Foreground, Guid? groupId = null, CancellationToken cancellationToken = default)
        => CallAsync<Activity>(nameof(CreateActivityAsync), cancellationToken, name, description, defaultLane, groupId);

    public Task<Activity> UpdateActivityAsync(Guid activityId, ActivityUpdate update, OperationRequest request, CancellationToken cancellationToken = default)
        => CallAsync<Activity>(nameof(UpdateActivityAsync), cancellationToken, activityId, update, request);

    public Task<Activity> ArchiveActivityAsync(Guid activityId, OperationRequest request, CancellationToken cancellationToken = default)
        => CallAsync<Activity>(nameof(ArchiveActivityAsync), cancellationToken, activityId, request);

    public Task<Activity> RestoreActivityAsync(Guid activityId, OperationRequest request, CancellationToken cancellationToken = default)
        => CallAsync<Activity>(nameof(RestoreActivityAsync), cancellationToken, activityId, request);

    public Task<Activity> DeleteActivityAsync(Guid activityId, OperationRequest request, CancellationToken cancellationToken = default)
        => CallAsync<Activity>(nameof(DeleteActivityAsync), cancellationToken, activityId, request);

    public Task<Activity> RestoreDeletedActivityAsync(Guid activityId, OperationRequest request, CancellationToken cancellationToken = default)
        => CallAsync<Activity>(nameof(RestoreDeletedActivityAsync), cancellationToken, activityId, request);

    public Task<ActivityGroup> CreateActivityGroupAsync(string name, CancellationToken cancellationToken = default)
        => CallAsync<ActivityGroup>(nameof(CreateActivityGroupAsync), cancellationToken, name);

    public Task<ActivityGroup> UpdateActivityGroupAsync(Guid groupId, ActivityGroupUpdate update, OperationRequest request, CancellationToken cancellationToken = default)
        => CallAsync<ActivityGroup>(nameof(UpdateActivityGroupAsync), cancellationToken, groupId, update, request);

    public Task<ActivityGroup> ReorderActivityGroupAsync(Guid groupId, ReorderDirection direction, OperationRequest request, CancellationToken cancellationToken = default)
        => CallAsync<ActivityGroup>(nameof(ReorderActivityGroupAsync), cancellationToken, groupId, direction, request);

    public Task<ActivityGroup> DeleteActivityGroupAsync(Guid groupId, OperationRequest request, CancellationToken cancellationToken = default)
        => CallAsync<ActivityGroup>(nameof(DeleteActivityGroupAsync), cancellationToken, groupId, request);

    public Task<ActivityGroup> RestoreDeletedActivityGroupAsync(Guid groupId, OperationRequest request, CancellationToken cancellationToken = default)
        => CallAsync<ActivityGroup>(nameof(RestoreDeletedActivityGroupAsync), cancellationToken, groupId, request);

    public Task<HistoryPage> GetHistoryAsync(HistoryQuery query, CancellationToken cancellationToken = default)
        => CallAsync<HistoryPage>(nameof(GetHistoryAsync), cancellationToken, query);

    public async Task<IReadOnlyList<SummaryBucket>> GetSummaryAsync(DateTimeOffset rangeStartUtc, DateTimeOffset rangeEndUtc, SummaryGrouping grouping, string? timeZone = null, CancellationToken cancellationToken = default)
        => await CallAsync<SummaryBucket[]>(nameof(GetSummaryAsync), cancellationToken, rangeStartUtc, rangeEndUtc, grouping, timeZone);

    public async Task<IReadOnlyList<ScheduleBlock>> GetCalendarRangeAsync(CalendarRangeQuery query, CancellationToken cancellationToken = default)
        => await CallAsync<ScheduleBlock[]>(nameof(GetCalendarRangeAsync), cancellationToken, query);

    public Task<Calendar> CreateCalendarAsync(string name, string color = "#6767F2", bool visible = true, CancellationToken cancellationToken = default)
        => CallAsync<Calendar>(nameof(CreateCalendarAsync), cancellationToken, name, color, visible);

    public Task<Calendar> UpdateCalendarAsync(Guid calendarId, CalendarUpdate update, OperationRequest request, CancellationToken cancellationToken = default)
        => CallAsync<Calendar>(nameof(UpdateCalendarAsync), cancellationToken, calendarId, update, request);

    public Task<Calendar> DeleteCalendarAsync(Guid calendarId, OperationRequest request, CancellationToken cancellationToken = default)
        => CallAsync<Calendar>(nameof(DeleteCalendarAsync), cancellationToken, calendarId, request);

    public Task<Calendar> RestoreDeletedCalendarAsync(Guid calendarId, OperationRequest request, CancellationToken cancellationToken = default)
        => CallAsync<Calendar>(nameof(RestoreDeletedCalendarAsync), cancellationToken, calendarId, request);

    public async Task<IReadOnlyList<CalendarEvent>> GetCalendarEventsRangeAsync(CalendarRangeQuery query, CancellationToken cancellationToken = default)
        => await CallAsync<CalendarEvent[]>(nameof(GetCalendarEventsRangeAsync), cancellationToken, query);

    public Task<CalendarEvent> CreateCalendarEventAsync(Guid calendarId, string title, DateTimeOffset startAtUtc, DateTimeOffset endAtUtc, string description = "", string? location = null, string color = "#6767F2", bool allDay = false, string timeZone = "UTC", string? recurrenceRule = null, DateTimeOffset? recurrenceEndUtc = null, CancellationToken cancellationToken = default)
        => CallAsync<CalendarEvent>(nameof(CreateCalendarEventAsync), cancellationToken, calendarId, title, startAtUtc, endAtUtc, description, location, color, allDay, timeZone, recurrenceRule, recurrenceEndUtc);

    public Task<CalendarEvent> UpdateCalendarEventAsync(Guid eventId, CalendarEventUpdate update, OperationRequest request, CancellationToken cancellationToken = default)
        => CallAsync<CalendarEvent>(nameof(UpdateCalendarEventAsync), cancellationToken, eventId, update, request);

    public Task<CalendarEvent> DeleteCalendarEventAsync(Guid eventId, OperationRequest request, CancellationToken cancellationToken = default)
        => CallAsync<CalendarEvent>(nameof(DeleteCalendarEventAsync), cancellationToken, eventId, request);

    public Task<CalendarEvent> RestoreDeletedCalendarEventAsync(Guid eventId, OperationRequest request, CancellationToken cancellationToken = default)
        => CallAsync<CalendarEvent>(nameof(RestoreDeletedCalendarEventAsync), cancellationToken, eventId, request);

    public Task<CalendarEventOccurrenceOverride> UpsertCalendarEventExceptionAsync(Guid eventId, DateTimeOffset originalStartAtUtc, DateTimeOffset? newStartAtUtc, DateTimeOffset? newEndAtUtc, string? titleOverride, bool cancelled, OperationRequest request, CancellationToken cancellationToken = default)
        => CallAsync<CalendarEventOccurrenceOverride>(nameof(UpsertCalendarEventExceptionAsync), cancellationToken, eventId, originalStartAtUtc, newStartAtUtc, newEndAtUtc, titleOverride, cancelled, request);

    public Task DeleteCalendarEventExceptionAsync(Guid exceptionId, OperationRequest request, CancellationToken cancellationToken = default)
        => CallVoidAsync(nameof(DeleteCalendarEventExceptionAsync), cancellationToken, exceptionId, request);

    public Task<ScheduleBlock> CreateScheduleBlockAsync(Guid calendarId, Guid? taskId, Guid? activityId, string? titleOverride, DateTimeOffset startAtUtc, DateTimeOffset endAtUtc, string timeZone, string? recurrenceRule = null, DateTimeOffset? recurrenceEndUtc = null, CancellationToken cancellationToken = default)
        => CallAsync<ScheduleBlock>(nameof(CreateScheduleBlockAsync), cancellationToken, calendarId, taskId, activityId, titleOverride, startAtUtc, endAtUtc, timeZone, recurrenceRule, recurrenceEndUtc);

    public Task<ScheduleBlock> UpdateScheduleBlockAsync(Guid blockId, ScheduleBlockUpdate update, OperationRequest request, CancellationToken cancellationToken = default)
        => CallAsync<ScheduleBlock>(nameof(UpdateScheduleBlockAsync), cancellationToken, blockId, update, request);

    public Task<TaskItem> CreateTaskAsync(Guid projectId, string title, Priority priority = Priority.None, DateOnly? dueDate = null, CancellationToken cancellationToken = default)
        => CallAsync<TaskItem>(nameof(CreateTaskAsync), cancellationToken, projectId, title, priority, dueDate);

    public Task<TaskItem> UpdateTaskAsync(Guid taskId, TaskUpdate update, OperationRequest request, CancellationToken cancellationToken = default)
        => CallAsync<TaskItem>(nameof(UpdateTaskAsync), cancellationToken, taskId, update, request);

    public Task<TaskItem> MoveTaskAsync(Guid taskId, Guid projectId, OperationRequest request, CancellationToken cancellationToken = default)
        => CallAsync<TaskItem>(nameof(MoveTaskAsync), cancellationToken, taskId, projectId, request);

    public Task<TaskItem> ArchiveTaskAsync(Guid taskId, OperationRequest request, CancellationToken cancellationToken = default)
        => CallAsync<TaskItem>(nameof(ArchiveTaskAsync), cancellationToken, taskId, request);

    public Task<TaskItem> RestoreTaskAsync(Guid taskId, OperationRequest request, CancellationToken cancellationToken = default)
        => CallAsync<TaskItem>(nameof(RestoreTaskAsync), cancellationToken, taskId, request);

    public Task<TaskItem> DeleteTaskAsync(Guid taskId, OperationRequest request, CancellationToken cancellationToken = default)
        => CallAsync<TaskItem>(nameof(DeleteTaskAsync), cancellationToken, taskId, request);

    public Task<TaskItem> RestoreDeletedTaskAsync(Guid taskId, OperationRequest request, CancellationToken cancellationToken = default)
        => CallAsync<TaskItem>(nameof(RestoreDeletedTaskAsync), cancellationToken, taskId, request);

    public Task<TaskDetails> GetTaskDetailsAsync(Guid taskId, CancellationToken cancellationToken = default)
        => CallAsync<TaskDetails>(nameof(GetTaskDetailsAsync), cancellationToken, taskId);

    public Task<ProjectDetails> GetProjectDetailsAsync(Guid projectId, CancellationToken cancellationToken = default)
        => CallAsync<ProjectDetails>(nameof(GetProjectDetailsAsync), cancellationToken, projectId);

    public Task<TaskLink> AddTaskLinkAsync(Guid taskId, string? label, string uri, string kind = "reference", CancellationToken cancellationToken = default)
        => CallAsync<TaskLink>(nameof(AddTaskLinkAsync), cancellationToken, taskId, label, uri, kind);

    public Task<Tag> AddTaskTagAsync(Guid taskId, string displayName, string? color = null, CancellationToken cancellationToken = default)
        => CallAsync<Tag>(nameof(AddTaskTagAsync), cancellationToken, taskId, displayName, color);

    public Task<Tag> AddProjectTagAsync(Guid projectId, string displayName, string? color = null, CancellationToken cancellationToken = default)
        => CallAsync<Tag>(nameof(AddProjectTagAsync), cancellationToken, projectId, displayName, color);

    public Task<Tag> AddActivityTagAsync(Guid activityId, string displayName, string? color = null, CancellationToken cancellationToken = default)
        => CallAsync<Tag>(nameof(AddActivityTagAsync), cancellationToken, activityId, displayName, color);

    public Task<TaskItem> CompleteTaskAsync(Guid taskId, OperationRequest request, CancellationToken cancellationToken = default)
        => CallAsync<TaskItem>(nameof(CompleteTaskAsync), cancellationToken, taskId, request);

    public Task<TaskItem> ReopenTaskAsync(Guid taskId, OperationRequest request, CancellationToken cancellationToken = default)
        => CallAsync<TaskItem>(nameof(ReopenTaskAsync), cancellationToken, taskId, request);

    public Task AddTaskDependencyAsync(Guid taskId, Guid prerequisiteTaskId, CancellationToken cancellationToken = default)
        => CallVoidAsync(nameof(AddTaskDependencyAsync), cancellationToken, taskId, prerequisiteTaskId);

    public Task<TrackingSession> StartSessionAsync(Guid? taskId, Guid? activityId, SessionLane lane, OperationRequest request, CancellationToken cancellationToken = default)
        => CallAsync<TrackingSession>(nameof(StartSessionAsync), cancellationToken, taskId, activityId, lane, request);

    public Task<TrackingSession> PauseSessionAsync(Guid sessionId, OperationRequest request, CancellationToken cancellationToken = default)
        => CallAsync<TrackingSession>(nameof(PauseSessionAsync), cancellationToken, sessionId, request);

    public Task<TrackingSession> ResumeSessionAsync(Guid sessionId, OperationRequest request, CancellationToken cancellationToken = default)
        => CallAsync<TrackingSession>(nameof(ResumeSessionAsync), cancellationToken, sessionId, request);

    public Task<TrackingSession> StopSessionAsync(Guid sessionId, string? notes, OperationRequest request, CancellationToken cancellationToken = default)
        => CallAsync<TrackingSession>(nameof(StopSessionAsync), cancellationToken, sessionId, notes, request);

    public Task<TrackingSession> CreateManualSessionAsync(Guid? taskId, Guid? activityId, DateTimeOffset startedAtUtc, DateTimeOffset endedAtUtc, string? notes, OperationRequest? request = null, CancellationToken cancellationToken = default)
        => CallAsync<TrackingSession>(nameof(CreateManualSessionAsync), cancellationToken, taskId, activityId, startedAtUtc, endedAtUtc, notes, request);

    public Task<TrackingSession> CorrectSessionAsync(Guid sessionId, SessionCorrection correction, OperationRequest request, CancellationToken cancellationToken = default)
        => CallAsync<TrackingSession>(nameof(CorrectSessionAsync), cancellationToken, sessionId, correction, request);

    public async Task<IReadOnlyList<TrackingCorrection>> GetSessionCorrectionsAsync(Guid sessionId, CancellationToken cancellationToken = default)
        => await CallAsync<TrackingCorrection[]>(nameof(GetSessionCorrectionsAsync), cancellationToken, sessionId);

    public Task<TrackingSession> ResolveRecoveryAsync(Guid sessionId, RecoveryDecision decision, OperationRequest request, CancellationToken cancellationToken = default)
        => CallAsync<TrackingSession>(nameof(ResolveRecoveryAsync), cancellationToken, sessionId, decision, request);

    public Task<BackupResult> CreateBackupAsync(string destinationPath, CancellationToken cancellationToken = default)
        => CallAsync<BackupResult>(nameof(CreateBackupAsync), cancellationToken, destinationPath);

    public Task<ExportResult> ExportJsonAsync(string destinationPath, CancellationToken cancellationToken = default)
        => CallAsync<ExportResult>(nameof(ExportJsonAsync), cancellationToken, destinationPath);

    public Task<ExportResult> ExportCsvAsync(string destinationPath, DateTimeOffset rangeStartUtc, DateTimeOffset rangeEndUtc, CancellationToken cancellationToken = default)
        => CallAsync<ExportResult>(nameof(ExportCsvAsync), cancellationToken, destinationPath, rangeStartUtc, rangeEndUtc);

    public Task<RestoreResult> RestoreBackupAsync(string sourcePath, CancellationToken cancellationToken = default)
        => CallAsync<RestoreResult>(nameof(RestoreBackupAsync), cancellationToken, sourcePath);

    private async Task<T> CallAsync<T>(string method, CancellationToken cancellationToken, params object?[] args)
    {
        var result = await SendAsync(method, args, cancellationToken);
        if (result is null)
        {
            throw new SnookException(SnookErrorCode.InternalError, $"Daemon returned no result for {method}.");
        }

        return result.Value.Deserialize<T>(JsonOptions) ?? throw new SnookException(SnookErrorCode.InternalError, $"Daemon returned an invalid result for {method}.");
    }

    private async Task CallVoidAsync(string method, CancellationToken cancellationToken, params object?[] args)
    {
        _ = await SendAsync(method, args, cancellationToken);
    }

    private async Task<JsonElement?> SendAsync(string method, object?[] args, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_endpoint, "v1/call"));
        request.Headers.Add("X-Snook-Token", _token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = new StringContent(JsonSerializer.Serialize(new { method, args }, JsonOptions), Encoding.UTF8, "application/json");
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(body);
        var hasError = document.RootElement.TryGetProperty("error", out var error);
        if (!response.IsSuccessStatusCode || hasError)
        {
            var code = SnookErrorCode.InternalError;
            var message = $"Daemon call {method} failed.";
            if (error.ValueKind == JsonValueKind.Object)
            {
                if (error.TryGetProperty("code", out var codeElement))
                {
                    _ = Enum.TryParse(codeElement.GetString(), ignoreCase: true, out code);
                }

                if (error.TryGetProperty("message", out var messageElement))
                {
                    message = messageElement.GetString() ?? message;
                }
            }

            throw new SnookException(code, message);
        }

        return document.RootElement.TryGetProperty("result", out var result) ? result.Clone() : null;
    }

    private async Task ListenForChangesAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_endpoint, "v1/changes"));
                request.Headers.Add("X-Snook-Token", _token);
                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();
                var snapshot = await GetBootstrapAsync(cancellationToken);
                ChangedHandlers?.Invoke(this, new ChangeNotification(snapshot.CommittedCursor, Guid.Empty, "workspace", snapshot.Workspace.Id, "reconnected", snapshot.Workspace.Revision, snapshot.Today.CapturedAtUtc));
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                while (!cancellationToken.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(cancellationToken);
                    if (line is null)
                    {
                        break;
                    }

                    if (!line.StartsWith("data:", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var notification = JsonSerializer.Deserialize<ChangeNotification>(line[5..].Trim(), JsonOptions);
                    if (notification is not null)
                    {
                        ChangedHandlers?.Invoke(this, notification);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (HttpRequestException)
            {
                // The daemon may be starting or restarting; reconnect without opening SQLite locally.
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            _changesCancellation.Cancel();
            try
            {
                _changesTask.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
            }

            if (ChangedHandlers is not null)
            {
                ChangedHandlers = null;
            }
            _httpClient.Dispose();
            _changesCancellation.Dispose();
        }

        return ValueTask.CompletedTask;
    }
}
