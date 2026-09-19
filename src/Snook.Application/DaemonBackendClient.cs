using System.Net.Http.Headers;
using System.Net;
using System.Text;
using System.Text.Json;
using Snook.Contracts;
using Snook.Domain;

namespace Snook.Application;

public sealed partial class DaemonBackendClient : IBackendClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient;
    private readonly Uri _endpoint;
    private readonly string _token;
    private readonly Guid? _expectedServerId;
    private readonly Guid? _expectedWorkspaceId;
    private readonly CancellationTokenSource _changesCancellation = new();
    private readonly Task _changesTask;
    private readonly SemaphoreSlim _negotiation = new(1, 1);
    private bool _negotiated;
    private bool _disposed;
    private readonly object _disposeSync = new();
    private Task? _disposeTask;

    public DaemonBackendClient(Uri endpoint, string token, HttpMessageHandler? handler = null,
        Guid? expectedServerId = null, Guid? expectedWorkspaceId = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var validated = DaemonEndpoint.Parse(endpoint.OriginalString);
        _endpoint = validated.Origin;
        EndpointKind = validated.Kind;
        _token = Guard.Required(token, "token", 512);
        _expectedServerId = expectedServerId;
        _expectedWorkspaceId = expectedWorkspaceId;
        _httpClient = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false });
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
        // The transport lifetime must not depend on an Avalonia synchronization
        // context remaining alive when a client is created by the connection UI.
        _changesTask = Task.Run(() => ListenForChangesAsync(_changesCancellation.Token));
    }

    public Uri Endpoint => _endpoint;
    public DaemonEndpointKind EndpointKind { get; }
    public Guid? NegotiatedServerId { get; private set; }
    public Guid? NegotiatedWorkspaceId { get; private set; }
    public Version? NegotiatedContract { get; private set; }
    public Guid? ExpectedServerId => _expectedServerId;
    public Guid? ExpectedWorkspaceId => _expectedWorkspaceId;
    public bool CanAdministerHost => EndpointKind == DaemonEndpointKind.LocalLoopback;

    public async Task<PairingInvitation> CreatePairingInvitationAsync(string deviceName, Uri serveOrigin,
        string? intendedTailscaleLogin = null, CancellationToken cancellationToken = default)
    {
        if (!CanAdministerHost) throw new SnookException(SnookErrorCode.ValidationFailed, "Pairing administration is available only through the host's local loopback connection.");
        using var administrator = new DaemonAdministrationClient(_endpoint, _token);
        return await administrator.CreatePairingAsync(deviceName, serveOrigin, intendedTailscaleLogin, cancellationToken);
    }

    public async Task<IReadOnlyList<PairedDeviceInfo>> ListPairedDevicesAsync(CancellationToken cancellationToken = default)
    {
        if (!CanAdministerHost) throw new SnookException(SnookErrorCode.ValidationFailed, "Device administration is available only through the host's local loopback connection.");
        using var administrator = new DaemonAdministrationClient(_endpoint, _token);
        return await administrator.ListDevicesAsync(cancellationToken);
    }

    public async Task CancelPairingInvitationAsync(string pairingCode, CancellationToken cancellationToken = default)
    {
        if (!CanAdministerHost) throw new SnookException(SnookErrorCode.ValidationFailed, "Pairing administration is available only through the host's local loopback connection.");
        using var administrator = new DaemonAdministrationClient(_endpoint, _token);
        await administrator.CancelPairingAsync(pairingCode, cancellationToken);
    }

    public async Task RevokePairedDeviceAsync(Guid credentialId, CancellationToken cancellationToken = default)
    {
        if (!CanAdministerHost) throw new SnookException(SnookErrorCode.ValidationFailed, "Device administration is available only through the host's local loopback connection.");
        using var administrator = new DaemonAdministrationClient(_endpoint, _token);
        await administrator.RevokeDeviceAsync(credentialId, cancellationToken);
    }
    private bool _connected;
    public bool IsConnected => Volatile.Read(ref _connected);
    public event EventHandler? ConnectionStateChanged;

    private void SetConnected(bool connected)
    {
        if (IsConnected == connected) return;
        Volatile.Write(ref _connected, connected);
        if (ConnectionStateChanged is not { } handlers) return;
        foreach (EventHandler handler in handlers.GetInvocationList())
            try { handler(this, EventArgs.Empty); } catch (Exception) { /* Local diagnostics cannot interrupt transport. */ }
    }

    public static Uri ValidateEndpoint(string value)
        => DaemonEndpoint.Parse(value).Origin;

    private event EventHandler<ChangeNotification>? ChangedHandlers;
    public event EventHandler<ChangeNotification>? Changed
    {
        add => ChangedHandlers += value;
        remove => ChangedHandlers -= value;
    }

    public async Task<BootstrapSnapshot> GetBootstrapAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await CallAsync<BootstrapSnapshot>(nameof(GetBootstrapAsync), cancellationToken);
        if (_expectedWorkspaceId is not null && snapshot.Workspace.Id != _expectedWorkspaceId)
        {
            Volatile.Write(ref _negotiated, false);
            throw new SnookException(SnookErrorCode.StoreUnavailable,
                "The bootstrap workspace identity does not match the saved profile. Refuse this connection and verify the Serve URL and restored workspace before pairing again.");
        }
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

    public async Task<IReadOnlyList<TaskListItem>> SearchTasksAsync(string? search = null, bool includeCompleted = false, bool includeArchived = false, bool includeDeleted = false, CancellationToken cancellationToken = default)
        => await CallAsync<TaskListItem[]>(nameof(SearchTasksAsync), cancellationToken, search, includeCompleted, includeArchived, includeDeleted);

    public Task<Board> CreateBoardAsync(string name, OperationRequest? request = null, CancellationToken cancellationToken = default)
        => CallAsync<Board>(nameof(CreateBoardAsync), cancellationToken, name, request);

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

    public Task<Project> CreateProjectAsync(Guid boardId, string name, string description = "", bool starred = false, OperationRequest? request = null, CancellationToken cancellationToken = default)
        => CallAsync<Project>(nameof(CreateProjectAsync), cancellationToken, boardId, name, description, starred, request);

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

    public Task<Activity> CreateActivityAsync(string name, string description = "", SessionLane defaultLane = SessionLane.Foreground, Guid? groupId = null, OperationRequest? request = null, CancellationToken cancellationToken = default)
        => CallAsync<Activity>(nameof(CreateActivityAsync), cancellationToken, name, description, defaultLane, groupId, request);

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

    public Task<ActivityGroup> CreateActivityGroupAsync(string name, OperationRequest? request = null, CancellationToken cancellationToken = default)
        => CallAsync<ActivityGroup>(nameof(CreateActivityGroupAsync), cancellationToken, name, request);

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

    public Task<Calendar> CreateCalendarAsync(string name, string color = "#6767F2", bool visible = true, OperationRequest? request = null, CancellationToken cancellationToken = default)
        => CallAsync<Calendar>(nameof(CreateCalendarAsync), cancellationToken, name, color, visible, request);

    public Task<Calendar> UpdateCalendarAsync(Guid calendarId, CalendarUpdate update, OperationRequest request, CancellationToken cancellationToken = default)
        => CallAsync<Calendar>(nameof(UpdateCalendarAsync), cancellationToken, calendarId, update, request);

    public Task<Calendar> DeleteCalendarAsync(Guid calendarId, OperationRequest request, CancellationToken cancellationToken = default)
        => CallAsync<Calendar>(nameof(DeleteCalendarAsync), cancellationToken, calendarId, request);

    public Task<Calendar> RestoreDeletedCalendarAsync(Guid calendarId, OperationRequest request, CancellationToken cancellationToken = default)
        => CallAsync<Calendar>(nameof(RestoreDeletedCalendarAsync), cancellationToken, calendarId, request);

    public async Task<IReadOnlyList<CalendarEvent>> GetCalendarEventsRangeAsync(CalendarRangeQuery query, CancellationToken cancellationToken = default)
        => await CallAsync<CalendarEvent[]>(nameof(GetCalendarEventsRangeAsync), cancellationToken, query);

    public Task<CalendarEvent> CreateCalendarEventAsync(Guid calendarId, string title, DateTimeOffset startAtUtc, DateTimeOffset endAtUtc, string description = "", string? location = null, string color = "#6767F2", bool allDay = false, string timeZone = "UTC", string? recurrenceRule = null, DateTimeOffset? recurrenceEndUtc = null, OperationRequest? request = null, CancellationToken cancellationToken = default)
        => CallAsync<CalendarEvent>(nameof(CreateCalendarEventAsync), cancellationToken, calendarId, title, startAtUtc, endAtUtc, description, location, color, allDay, timeZone, recurrenceRule, recurrenceEndUtc, request);

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

    public Task<ScheduleBlock> CreateScheduleBlockAsync(Guid calendarId, Guid? taskId, Guid? activityId, string? titleOverride, DateTimeOffset startAtUtc, DateTimeOffset endAtUtc, string timeZone, string? recurrenceRule = null, DateTimeOffset? recurrenceEndUtc = null, OperationRequest? request = null, CancellationToken cancellationToken = default)
        => CallAsync<ScheduleBlock>(nameof(CreateScheduleBlockAsync), cancellationToken, calendarId, taskId, activityId, titleOverride, startAtUtc, endAtUtc, timeZone, recurrenceRule, recurrenceEndUtc, request);

    public Task<ScheduleBlock> UpdateScheduleBlockAsync(Guid blockId, ScheduleBlockUpdate update, OperationRequest request, CancellationToken cancellationToken = default)
        => CallAsync<ScheduleBlock>(nameof(UpdateScheduleBlockAsync), cancellationToken, blockId, update, request);

    public Task<TaskItem> CreateTaskAsync(Guid projectId, string title, Priority priority = Priority.None, DateOnly? dueDate = null, OperationRequest? request = null, CancellationToken cancellationToken = default)
        => CallAsync<TaskItem>(nameof(CreateTaskAsync), cancellationToken, projectId, title, priority, dueDate, request);

    public Task<IReadOnlyList<TaskItem>> BulkUpdateTasksAsync(IReadOnlyList<TaskRevision> tasks, BulkTaskUpdate update, OperationRequest request, CancellationToken cancellationToken = default)
        => CallAsync<IReadOnlyList<TaskItem>>(nameof(BulkUpdateTasksAsync), cancellationToken, tasks, update, request);

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

    public Task<TaskLink> AddTaskLinkAsync(Guid taskId, string? label, string uri, string kind = "reference", OperationRequest? request = null, CancellationToken cancellationToken = default)
        => CallAsync<TaskLink>(nameof(AddTaskLinkAsync), cancellationToken, taskId, label, uri, kind, request);

    public Task<Tag> AddTaskTagAsync(Guid taskId, string displayName, string? color = null, OperationRequest? request = null, CancellationToken cancellationToken = default)
        => CallAsync<Tag>(nameof(AddTaskTagAsync), cancellationToken, taskId, displayName, color, request);

    public Task<Tag> AddProjectTagAsync(Guid projectId, string displayName, string? color = null, OperationRequest? request = null, CancellationToken cancellationToken = default)
        => CallAsync<Tag>(nameof(AddProjectTagAsync), cancellationToken, projectId, displayName, color, request);

    public Task<Tag> AddActivityTagAsync(Guid activityId, string displayName, string? color = null, OperationRequest? request = null, CancellationToken cancellationToken = default)
        => CallAsync<Tag>(nameof(AddActivityTagAsync), cancellationToken, activityId, displayName, color, request);

    public Task<TaskItem> CompleteTaskAsync(Guid taskId, OperationRequest request, CancellationToken cancellationToken = default)
        => CallAsync<TaskItem>(nameof(CompleteTaskAsync), cancellationToken, taskId, request);

    public Task<TaskItem> ReopenTaskAsync(Guid taskId, OperationRequest request, CancellationToken cancellationToken = default)
        => CallAsync<TaskItem>(nameof(ReopenTaskAsync), cancellationToken, taskId, request);

    public Task AddTaskDependencyAsync(Guid taskId, Guid prerequisiteTaskId, OperationRequest? request = null, CancellationToken cancellationToken = default)
        => CallVoidAsync(nameof(AddTaskDependencyAsync), cancellationToken, taskId, prerequisiteTaskId, request);

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

    public async Task<RestoreResult> RestoreBackupAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        var result = await CallAsync<RestoreResult>(nameof(RestoreBackupAsync), cancellationToken, sourcePath);
        Volatile.Write(ref _negotiated, false);
        return result;
    }

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
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await EnsureCompatibleAsync(deadline.Token);
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_endpoint, "v1/call"));
            request.Headers.Add("X-Snook-Token", _token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Content = new StringContent(JsonSerializer.Serialize(new { method, args }, JsonOptions), Encoding.UTF8, "application/json");
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new SnookException(SnookErrorCode.StoreUnavailable, "Daemon authentication failed. Check the selected profile and token file, then reconnect.");
            await response.Content.LoadIntoBufferAsync(64 * 1024 * 1024, deadline.Token);
            var body = await response.Content.ReadAsStringAsync(deadline.Token);
            using var document = JsonDocument.Parse(body,
                new JsonDocumentOptions { MaxDepth = 64, CommentHandling = JsonCommentHandling.Disallow });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new JsonException("Invalid daemon response envelope.");
            RejectDuplicateProperties(document.RootElement);
            var hasError = document.RootElement.TryGetProperty("error", out var error);
            if (!response.IsSuccessStatusCode || hasError)
            {
                var code = SnookErrorCode.InternalError;
                var message = $"Daemon call {method} failed.";
                if (error.ValueKind == JsonValueKind.Object)
                {
                    if (error.TryGetProperty("code", out var codeElement)
                        && codeElement.ValueKind == JsonValueKind.String)
                    {
                        if (Enum.TryParse<SnookErrorCode>(codeElement.GetString(), ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)) code = parsed;
                    }

                    if (error.TryGetProperty("message", out var messageElement)
                        && messageElement.ValueKind == JsonValueKind.String)
                    {
                        message = messageElement.GetString() ?? message;
                    }
                }

                throw new SnookException(code, message);
            }

            return document.RootElement.TryGetProperty("result", out var result) ? result.Clone() : null;
        }
        catch (HttpRequestException exception)
        {
            throw DaemonTransportFailure.From(exception, EndpointKind, uncertainWrite: true);
        }
        catch (Exception exception) when (exception is IOException or JsonException
            || exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new SnookException(SnookErrorCode.StoreUnavailable,
                "Daemon unavailable or returned an invalid response. Check that snookd is running for this profile and retry. A write may have committed; reuse its operation ID when retrying.", exception);
        }
    }

    private async Task EnsureCompatibleAsync(CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        cancellationToken = deadline.Token;
        await _negotiation.WaitAsync(cancellationToken);
        try
        {
            if (Volatile.Read(ref _negotiated)) return;
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_endpoint, "v1/health"));
            request.Headers.Add("X-Snook-Token", _token);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new SnookException(SnookErrorCode.StoreUnavailable,
                    response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                        ? "Daemon authentication was rejected. Check or re-pair the selected credential."
                        : "Daemon health protocol failed. Check that the endpoint runs a compatible Snook daemon.");
            await response.Content.LoadIntoBufferAsync(8192, cancellationToken);
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken),
                    new JsonDocumentOptions { MaxDepth = 8, CommentHandling = JsonCommentHandling.Disallow });
                RejectDuplicateProperties(document.RootElement);
            }
            catch (JsonException exception)
            {
                throw new SnookException(SnookErrorCode.StoreUnavailable,
                    "Daemon health protocol returned invalid JSON. Upgrade the daemon and client together.", exception);
            }
            using var ownedDocument = document;
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("ready", out var ready) || ready.ValueKind != JsonValueKind.True
                || !document.RootElement.TryGetProperty("contract", out var contract) || contract.ValueKind != JsonValueKind.String
                || !Version.TryParse(contract.GetString(), out var version)
                || version.Major != ContractInfo.Major || version.Minor < ContractInfo.Minor)
                throw new SnookException(SnookErrorCode.StoreUnavailable, "Daemon contract is incompatible. Upgrade the daemon and clients together.");
            var serverId = ReadOptionalGuid(document.RootElement, "serverId");
            var workspaceId = ReadOptionalGuid(document.RootElement, "workspaceId");
            if (_expectedServerId is not null && serverId != _expectedServerId
                || _expectedWorkspaceId is not null && workspaceId != _expectedWorkspaceId)
                throw new SnookException(SnookErrorCode.StoreUnavailable,
                    "The daemon identity does not match the saved profile. Refuse this connection and verify the Serve URL and workspace before pairing again.");
            NegotiatedServerId = serverId;
            NegotiatedWorkspaceId = workspaceId;
            NegotiatedContract = version;
            Volatile.Write(ref _negotiated, true);
        }
        finally { _negotiation.Release(); }
    }

    private static Guid? ReadOptionalGuid(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return null;
        return value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out var parsed) && parsed != Guid.Empty
            ? parsed
            : throw new SnookException(SnookErrorCode.StoreUnavailable, "Daemon health metadata is invalid.");
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new JsonException("Daemon response contains duplicate fields.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicateProperties(item);
    }

    private async Task ListenForChangesAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await EnsureCompatibleAsync(cancellationToken);
                using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_endpoint, "v1/changes"));
                request.Headers.Add("X-Snook-Token", _token);
                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();
                var snapshot = await GetBootstrapAsync(cancellationToken);
                SetConnected(true);
                ChangedHandlers?.Invoke(this, new ChangeNotification(snapshot.CommittedCursor, Guid.Empty, "workspace", snapshot.Workspace.Id, "reconnected", snapshot.Workspace.Revision, snapshot.Today.CapturedAtUtc));
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                while (!cancellationToken.IsCancellationRequested)
                {
                    var line = await ReadSseLineWithTimeoutAsync(reader, TimeSpan.FromSeconds(30),
                        cancellationToken);
                    if (line is null)
                    {
                        break;
                    }

                    if (!line.StartsWith("data:", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    using var notificationDocument = JsonDocument.Parse(line[5..].Trim(),
                        new JsonDocumentOptions { MaxDepth = 16, CommentHandling = JsonCommentHandling.Disallow });
                    RejectDuplicateProperties(notificationDocument.RootElement);
                    var notification = notificationDocument.RootElement.Deserialize<ChangeNotification>(JsonOptions);
                    if (notification is not null)
                    {
                        if (notification.ChangeKind == "restored") Volatile.Write(ref _negotiated, false);
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
            catch (IOException)
            {
                // A daemon shutdown or an interrupted SSE response is also a reconnectable transport event.
            }
            catch (OperationCanceledException)
            {
                // A connection deadline is reconnectable; application cancellation exits above.
            }
            catch (Exception exception) when (exception is SnookException or JsonException)
            {
                // Do not fault disposal after authentication or malformed peer responses.
            }

            SetConnected(false);
            Volatile.Write(ref _negotiated, false);

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

    internal static async Task<string?> ReadSseLineAsync(StreamReader reader,
        CancellationToken cancellationToken)
    {
        const int maximumCharacters = 64 * 1024;
        var line = new StringBuilder();
        var character = new char[1];
        while (true)
        {
            var read = await reader.ReadAsync(character.AsMemory(), cancellationToken);
            if (read == 0) return line.Length == 0 ? null : line.ToString().TrimEnd('\r');
            if (character[0] == '\n') return line.ToString().TrimEnd('\r');
            if (line.Length >= maximumCharacters)
                throw new SnookException(SnookErrorCode.StoreUnavailable,
                    "Daemon change stream returned an oversized frame and was disconnected.");
            line.Append(character[0]);
        }
    }

    internal static async Task<string?> ReadSseLineWithTimeoutAsync(StreamReader reader,
        TimeSpan idleTimeout, CancellationToken cancellationToken)
    {
        using var idleDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        idleDeadline.CancelAfter(idleTimeout);
        try { return await ReadSseLineAsync(reader, idleDeadline.Token); }
        catch (OperationCanceledException) when (idleDeadline.IsCancellationRequested
            && !cancellationToken.IsCancellationRequested)
        {
            throw new IOException("Daemon change stream heartbeat timed out.");
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeSync) return new(_disposeTask ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            _changesCancellation.Cancel();
            try
            {
                await _changesTask.ConfigureAwait(false);
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

    }
}
