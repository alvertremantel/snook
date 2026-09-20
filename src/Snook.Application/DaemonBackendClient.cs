using System.Net.Http.Headers;
using System.Net;
using System.Text;
using System.Text.Json;
using Snook.Contracts;
using Snook.Domain;

namespace Snook.Application;

public enum DaemonEndpointKind
{
    Loopback,
    TailnetServe
}

public sealed partial class DaemonBackendClient : IBackendClient, IBackendConnectionState, IBackendDocumentTransfer
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient;
    private readonly Uri _endpoint;
    private readonly string _token;
    private readonly string _credentialHeader;
    private readonly CancellationTokenSource _changesCancellation = new();
    private readonly Task _changesTask;
    private readonly SemaphoreSlim _negotiation = new(1, 1);
    private bool _negotiated;
    private bool _disposed;
    private readonly object _disposeSync = new();
    private Task? _disposeTask;

    public DaemonBackendClient(Uri endpoint, string token, HttpMessageHandler? handler = null)
        : this(endpoint, token, DaemonEndpointKind.Loopback, handler)
    {
    }

    public DaemonBackendClient(Uri endpoint, string token, DaemonEndpointKind endpointKind,
        HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ValidateEndpoint(endpoint.OriginalString, endpointKind);
        _endpoint = endpoint;
        EndpointKind = endpointKind;
        _token = Guard.Required(token, "token", 512);
        _credentialHeader = endpointKind == DaemonEndpointKind.TailnetServe
            ? "X-Snook-Device-Token" : "X-Snook-Token";
        _httpClient = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false });
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
        // The transport lifetime must not depend on an Avalonia synchronization
        // context remaining alive when a client is created by the connection UI.
        _changesTask = Task.Run(() => ListenForChangesAsync(_changesCancellation.Token));
    }

    public Uri Endpoint => _endpoint;
    public bool CanDownloadDocuments => true;
    public bool CanRestoreDocuments => EndpointKind == DaemonEndpointKind.TailnetServe;
    public DaemonEndpointKind EndpointKind { get; }
    public string ConnectionLabel => EndpointKind == DaemonEndpointKind.TailnetServe
        ? $"Paired daemon · {_endpoint.Host}" : $"Daemon · {_endpoint}";
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

    public static Uri ValidateEndpoint(string value, DaemonEndpointKind endpointKind = DaemonEndpointKind.Loopback)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var endpoint))
            throw InvalidEndpoint(endpointKind);
        var commonShape = endpoint.IsAbsoluteUri && endpoint.UserInfo.Length == 0
            && endpoint.AbsolutePath == "/" && endpoint.Query.Length == 0
            && endpoint.Fragment.Length == 0;
        if (endpointKind == DaemonEndpointKind.Loopback)
        {
            if (!commonShape || endpoint.Scheme != Uri.UriSchemeHttp || !endpoint.IsLoopback)
                throw InvalidEndpoint(endpointKind);
        }
        else if (endpointKind == DaemonEndpointKind.TailnetServe)
        {
            var host = endpoint.IdnHost;
            if (!commonShape || endpoint.Scheme != Uri.UriSchemeHttps || endpoint.Port != 443
                || endpoint.HostNameType != UriHostNameType.Dns || host.Length <= ".ts.net".Length
                || !host.EndsWith(".ts.net", StringComparison.OrdinalIgnoreCase))
                throw InvalidEndpoint(endpointKind);
        }
        else throw new ArgumentOutOfRangeException(nameof(endpointKind));
        return endpoint;
    }

    private static SnookException InvalidEndpoint(DaemonEndpointKind endpointKind) => new(
        SnookErrorCode.ValidationFailed,
        endpointKind == DaemonEndpointKind.TailnetServe
            ? "Android daemon endpoint must be an HTTPS *.ts.net origin on port 443 with no credentials, path, query or fragment."
            : "Daemon endpoint must be an HTTP loopback URL with no credentials, path, query or fragment.");

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

    public Task<BackupInspection> InspectBackupAsync(string sourcePath, CancellationToken cancellationToken = default)
        => CallAsync<BackupInspection>(nameof(InspectBackupAsync), cancellationToken, sourcePath);

    public Task<RestoreResult> RestoreBackupAsync(string sourcePath, CancellationToken cancellationToken = default)
        => CallAsync<RestoreResult>(nameof(RestoreBackupAsync), cancellationToken, sourcePath);

    public Task<DaemonPairingChallenge> CreatePairingChallengeAsync(Uri serveOrigin,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serveOrigin);
        _ = ValidateEndpoint(serveOrigin.OriginalString, DaemonEndpointKind.TailnetServe);
        return SendPairingAdminAsync<DaemonPairingChallenge>(HttpMethod.Post,
            "v1/pairing/challenges", new { serveOrigin = serveOrigin.AbsoluteUri },
            cancellationToken);
    }

    public async Task<IReadOnlyList<DaemonPairedDevice>> ListPairedDevicesAsync(
        CancellationToken cancellationToken = default)
    {
        var response = await SendPairingAdminAsync<PairingDevicesResponse>(HttpMethod.Get,
            "v1/pairing/devices", null, cancellationToken).ConfigureAwait(false);
        return response.Devices;
    }

    public Task<DaemonPairingRevocation> RevokePairedDeviceAsync(Guid credentialId,
        CancellationToken cancellationToken = default)
    {
        if (credentialId == Guid.Empty)
            throw new SnookException(SnookErrorCode.ValidationFailed,
                "Credential identity is required.");
        return SendPairingAdminAsync<DaemonPairingRevocation>(HttpMethod.Post,
            "v1/pairing/revoke", new { credentialId }, cancellationToken);
    }

    public Task<DaemonPairedDevice> RenamePairedDeviceAsync(Guid credentialId,
        string deviceName, CancellationToken cancellationToken = default)
    {
        if (credentialId == Guid.Empty)
            throw new SnookException(SnookErrorCode.ValidationFailed,
                "Credential identity is required.");
        if (string.IsNullOrWhiteSpace(deviceName) || deviceName.Length > 80
            || deviceName.Any(char.IsControl))
            throw new SnookException(SnookErrorCode.ValidationFailed,
                "Device name is required, limited to 80 characters, and cannot contain controls.");
        return SendPairingAdminAsync<DaemonPairedDevice>(HttpMethod.Post,
            "v1/pairing/rename", new { credentialId, deviceName = deviceName.Trim() },
            cancellationToken);
    }

    public Task<DaemonPairedDevice> RenameCurrentPairedDeviceAsync(string deviceName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(deviceName) || deviceName.Length > 80
            || deviceName.Any(char.IsControl))
            throw new SnookException(SnookErrorCode.ValidationFailed,
                "Device name is required, limited to 80 characters, and cannot contain controls.");
        return SendDevicePairingAsync<DaemonPairedDevice>("v1/pairing/self/rename",
            new { deviceName = deviceName.Trim() }, cancellationToken);
    }

    public Task<DaemonPairingRevocation> RevokeCurrentPairedDeviceAsync(
        CancellationToken cancellationToken = default)
        => SendDevicePairingAsync<DaemonPairingRevocation>("v1/pairing/self/revoke",
            new { }, cancellationToken);

    public Task<long> DownloadBackupAsync(Stream destination,
        CancellationToken cancellationToken = default)
        => DownloadDocumentAsync("v1/transfers/backup", null, destination,
            WorkspaceBackupBundle.MaximumBundleBytes, cancellationToken);

    public Task<long> DownloadJsonAsync(Stream destination,
        CancellationToken cancellationToken = default)
        => DownloadDocumentAsync("v1/transfers/json", null, destination,
            WorkspaceBackupBundle.MaximumDatabaseBytes, cancellationToken);

    public Task<long> DownloadCsvAsync(Stream destination, DateTimeOffset rangeStartUtc,
        DateTimeOffset rangeEndUtc, CancellationToken cancellationToken = default)
        => DownloadDocumentAsync("v1/transfers/csv", new { rangeStartUtc, rangeEndUtc },
            destination, WorkspaceBackupBundle.MaximumDatabaseBytes, cancellationToken);

    public async Task<RemoteBackupStage> StageRestoreBackupAsync(Stream source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead || !source.CanSeek || source.Position != 0
            || source.Length is < 1 || source.Length > WorkspaceBackupBundle.MaximumBundleBytes)
            throw new ArgumentException("A bounded seekable backup stream positioned at its start is required.",
                nameof(source));
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!CanRestoreDocuments)
            throw new SnookException(SnookErrorCode.ValidationFailed,
                "Remote restore upload requires a paired Tailnet Serve connection.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            await EnsureCompatibleAsync(deadline.Token).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Post,
                new Uri(_endpoint, "v1/transfers/restore/stage"));
            request.Headers.Add(_credentialHeader, _token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Content = new StreamContent(new LeaveOpenStream(source), 65_536);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
            request.Content.Headers.ContentLength = source.Length;
            return await ReadTransferResponseAsync<RemoteBackupStage>(request,
                uncertainWrite: false, deadline.Token).ConfigureAwait(false);
        }
        catch (SnookException) { throw; }
        catch (Exception exception) when (exception is HttpRequestException or IOException
            or JsonException || exception is OperationCanceledException
                && !cancellationToken.IsCancellationRequested)
        {
            throw new SnookException(SnookErrorCode.StoreUnavailable,
                TailnetFailureMessage(exception, uncertainWrite: false), exception);
        }
    }

    public async Task<RemoteRestoreResult> RestoreStagedBackupAsync(RemoteBackupStage stage,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stage);
        if (stage.UploadId == Guid.Empty || stage.CurrentWorkspaceId == Guid.Empty
            || stage.CurrentCursor < 0 || stage.Incoming is null)
            throw new SnookException(SnookErrorCode.ValidationFailed,
                "A valid reviewed remote backup stage is required.");
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!CanRestoreDocuments)
            throw new SnookException(SnookErrorCode.ValidationFailed,
                "Remote restore requires a paired Tailnet Serve connection.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            await EnsureCompatibleAsync(deadline.Token).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Post,
                new Uri(_endpoint, "v1/transfers/restore/commit"));
            request.Headers.Add(_credentialHeader, _token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Content = new StringContent(JsonSerializer.Serialize(stage, JsonOptions),
                Encoding.UTF8, "application/json");
            return await ReadTransferResponseAsync<RemoteRestoreResult>(request,
                uncertainWrite: true, deadline.Token).ConfigureAwait(false);
        }
        catch (SnookException) { throw; }
        catch (Exception exception) when (exception is HttpRequestException or IOException
            or JsonException || exception is OperationCanceledException
                && !cancellationToken.IsCancellationRequested)
        {
            throw new SnookException(SnookErrorCode.StoreUnavailable,
                TailnetFailureMessage(exception, uncertainWrite: true), exception);
        }
    }

    public async Task DiscardStagedBackupAsync(Guid uploadId,
        CancellationToken cancellationToken = default)
    {
        if (uploadId == Guid.Empty)
            throw new SnookException(SnookErrorCode.ValidationFailed,
                "Restore upload identity is required.");
        if (!CanRestoreDocuments) return;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post,
                new Uri(_endpoint, "v1/transfers/restore/discard"));
            request.Headers.Add(_credentialHeader, _token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Content = new StringContent(JsonSerializer.Serialize(new { uploadId }, JsonOptions),
                Encoding.UTF8, "application/json");
            _ = await ReadTransferResponseAsync<RestoreDiscardResponse>(request,
                uncertainWrite: false, deadline.Token).ConfigureAwait(false);
        }
        catch (SnookException) { throw; }
        catch (Exception exception) when (exception is HttpRequestException or IOException
            or JsonException || exception is OperationCanceledException
                && !cancellationToken.IsCancellationRequested)
        {
            throw new SnookException(SnookErrorCode.StoreUnavailable,
                TailnetFailureMessage(exception, uncertainWrite: false), exception);
        }
    }

    private async Task<T> ReadTransferResponseAsync<T>(HttpRequestMessage request,
        bool uncertainWrite, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.SendAsync(request,
            HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        await response.Content.LoadIntoBufferAsync(65_536, cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var message = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                ? "The daemon rejected remote restore authentication or tailnet policy."
                : "The daemon rejected the remote restore document or stale confirmation.";
            try
            {
                using var error = JsonDocument.Parse(text);
                if (error.RootElement.TryGetProperty("error", out var value)
                    && value.TryGetProperty("message", out var detail))
                    message = detail.GetString() ?? message;
            }
            catch (JsonException) { }
            if (uncertainWrite)
                message += " The restore may have committed; reconnect and inspect the workspace before retrying.";
            throw new SnookException(SnookErrorCode.StoreUnavailable, message);
        }
        return JsonSerializer.Deserialize<T>(text, JsonOptions)
            ?? throw new JsonException("Remote restore returned no result.");
    }

    private async Task<long> DownloadDocumentAsync(string route, object? body,
        Stream destination, long maximumBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
            throw new ArgumentException("A writable destination stream is required.", nameof(destination));
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            await EnsureCompatibleAsync(deadline.Token).ConfigureAwait(false);
            using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post,
                new Uri(_endpoint, route));
            request.Headers.Add(_credentialHeader, _token);
            if (body is not null)
                request.Content = new StringContent(JsonSerializer.Serialize(body, JsonOptions),
                    Encoding.UTF8, "application/json");
            using var response = await _httpClient.SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new SnookException(SnookErrorCode.StoreUnavailable,
                    "The daemon rejected document-download authentication. Reconnect or re-pair before retrying.");
            if (!response.IsSuccessStatusCode)
                throw new SnookException(SnookErrorCode.StoreUnavailable,
                    "The daemon could not create the requested document. No destination is complete.");
            if (response.Content.Headers.ContentLength is < 1
                || response.Content.Headers.ContentLength > maximumBytes)
                throw new InvalidDataException("The daemon document size is invalid.");
            await using var source = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            return await BoundedStreamTransfer.CopyAsync(source, destination,
                maximumBytes, deadline.Token).ConfigureAwait(false);
        }
        catch (SnookException) { throw; }
        catch (Exception exception) when (exception is HttpRequestException or IOException
            or InvalidDataException || exception is OperationCanceledException
                && !cancellationToken.IsCancellationRequested)
        {
            throw new SnookException(SnookErrorCode.StoreUnavailable,
                (EndpointKind == DaemonEndpointKind.TailnetServe
                    ? TailnetFailureMessage(exception, uncertainWrite: false)
                    : "The daemon document download was interrupted or invalid.")
                + " Remove the incomplete destination before retrying.", exception);
        }
    }

    private async Task<T> SendPairingAdminAsync<T>(HttpMethod method, string route,
        object? body, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (EndpointKind != DaemonEndpointKind.Loopback)
            throw new SnookException(SnookErrorCode.ValidationFailed,
                "Pairing administration requires the authenticated loopback daemon connection.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await EnsureCompatibleAsync(deadline.Token).ConfigureAwait(false);
            using var request = new HttpRequestMessage(method, new Uri(_endpoint, route));
            request.Headers.Add(_credentialHeader, _token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (body is not null)
                request.Content = new StringContent(JsonSerializer.Serialize(body, JsonOptions),
                    Encoding.UTF8, "application/json");
            using var response = await _httpClient.SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            await response.Content.LoadIntoBufferAsync(65_536, deadline.Token).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var message = "Pairing administration was rejected or unavailable.";
                try
                {
                    using var error = JsonDocument.Parse(text);
                    if (error.RootElement.TryGetProperty("error", out var value)
                        && value.TryGetProperty("message", out var detail))
                        message = detail.GetString() ?? message;
                }
                catch (JsonException) { }
                throw new SnookException(SnookErrorCode.StoreUnavailable, message);
            }
            return JsonSerializer.Deserialize<T>(text, JsonOptions)
                ?? throw new JsonException("Pairing administration returned no result.");
        }
        catch (SnookException) { throw; }
        catch (Exception exception) when (exception is HttpRequestException or IOException
            or JsonException || exception is OperationCanceledException
                && !cancellationToken.IsCancellationRequested)
        {
            throw new SnookException(SnookErrorCode.StoreUnavailable,
                "Pairing administration could not reach a valid authenticated loopback daemon response.", exception);
        }
    }

    private async Task<T> SendDevicePairingAsync<T>(string route, object body,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (EndpointKind != DaemonEndpointKind.TailnetServe)
            throw new SnookException(SnookErrorCode.ValidationFailed,
                "Paired-device management requires its Tailnet Serve profile.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await EnsureCompatibleAsync(deadline.Token).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_endpoint, route));
            request.Headers.Add(_credentialHeader, _token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Content = new StringContent(JsonSerializer.Serialize(body, JsonOptions),
                Encoding.UTF8, "application/json");
            using var response = await _httpClient.SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            await response.Content.LoadIntoBufferAsync(65_536, deadline.Token).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new SnookException(SnookErrorCode.StoreUnavailable,
                    response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                        ? "The daemon rejected this paired-device operation. Re-pair or ask its administrator to review the device record."
                        : "The daemon could not complete this paired-device operation.");
            return JsonSerializer.Deserialize<T>(text, JsonOptions)
                ?? throw new JsonException("Paired-device management returned no result.");
        }
        catch (SnookException) { throw; }
        catch (Exception exception) when (exception is HttpRequestException or IOException
            or JsonException || exception is OperationCanceledException
                && !cancellationToken.IsCancellationRequested)
        {
            throw new SnookException(SnookErrorCode.StoreUnavailable,
                TailnetFailureMessage(exception, uncertainWrite: true), exception);
        }
    }

    private sealed record PairingDevicesResponse(DaemonPairedDevice[] Devices);
    private sealed record RestoreDiscardResponse(Guid UploadId, bool Discarded);

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
            request.Headers.Add(_credentialHeader, _token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Content = new StringContent(JsonSerializer.Serialize(new { method, args }, JsonOptions), Encoding.UTF8, "application/json");
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new SnookException(SnookErrorCode.StoreUnavailable,
                    EndpointKind == DaemonEndpointKind.TailnetServe
                        ? response.StatusCode == HttpStatusCode.Forbidden
                            ? "The tailnet grant denied this Serve route. Restore the intended Tailscale policy; no alternate route was attempted."
                            : "The paired device credential or observed tailnet identity was rejected. Ask the daemon administrator to review or revoke the device record, then re-pair."
                        : "Daemon authentication failed. Check the selected profile and token file, then reconnect.");
            await response.Content.LoadIntoBufferAsync(64 * 1024 * 1024, deadline.Token);
            var body = await response.Content.ReadAsStringAsync(deadline.Token);
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new JsonException("Invalid daemon response envelope.");
            var hasError = document.RootElement.TryGetProperty("error", out var error);
            if (!response.IsSuccessStatusCode || hasError)
            {
                var code = SnookErrorCode.InternalError;
                var message = $"Daemon call {method} failed.";
                if (error.ValueKind == JsonValueKind.Object)
                {
                    if (error.TryGetProperty("code", out var codeElement))
                    {
                        if (Enum.TryParse<SnookErrorCode>(codeElement.GetString(), ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)) code = parsed;
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
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException
            || exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new SnookException(SnookErrorCode.StoreUnavailable,
                EndpointKind == DaemonEndpointKind.TailnetServe
                    ? TailnetFailureMessage(exception, uncertainWrite: true)
                    : "Daemon unavailable or returned an invalid response. Check that snookd is running for this profile and retry. A write may have committed; reuse its operation ID when retrying.", exception);
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
            if (_negotiated) return;
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_endpoint, "v1/health"));
            request.Headers.Add(_credentialHeader, _token);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new SnookException(SnookErrorCode.StoreUnavailable,
                    EndpointKind == DaemonEndpointKind.TailnetServe
                        ? response.StatusCode == HttpStatusCode.Forbidden
                            ? "The tailnet grant denied the paired Serve endpoint. Restore the intended policy and retry."
                            : "The paired daemon rejected health authentication. Restore the intended tailnet identity or re-pair this device."
                        : "Daemon health/authentication failed. Check the daemon endpoint and token file.");
            await response.Content.LoadIntoBufferAsync(8192, cancellationToken);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("ready", out var ready) || ready.ValueKind != JsonValueKind.True
                || !document.RootElement.TryGetProperty("contract", out var contract) || contract.ValueKind != JsonValueKind.String
                || !Version.TryParse(contract.GetString(), out var version)
                || version.Major != ContractInfo.Major || version.Minor < ContractInfo.Minor)
                throw new SnookException(SnookErrorCode.StoreUnavailable, "Daemon contract is incompatible. Upgrade the daemon and clients together.");
            _negotiated = true;
        }
        finally { _negotiation.Release(); }
    }

    private static string TailnetFailureMessage(Exception exception, bool uncertainWrite)
    {
        var detail = exception switch
        {
            HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError }
                => "MagicDNS could not resolve the paired Serve hostname. Check that Tailscale is connected to the intended tailnet.",
            HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError }
                => "HTTPS trust for the paired Serve hostname failed. Snook did not bypass certificate validation.",
            HttpRequestException { HttpRequestError: HttpRequestError.ConnectionError }
                => "The paired Serve route or daemon is unreachable. Check Tailscale, grants, Serve, and snookd; no LAN or public fallback was attempted.",
            OperationCanceledException
                => "The paired Serve request timed out. Restore tailnet or daemon reachability and retry.",
            JsonException
                => "The paired daemon returned an invalid protocol response. Upgrade the daemon and client together.",
            _ => "The paired daemon connection was interrupted. Check Tailscale, Serve, and snookd; no alternate route was attempted."
        };
        return uncertainWrite
            ? detail + " A write may have committed; retry only with the same operation receipt."
            : detail;
    }

    private async Task ListenForChangesAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await EnsureCompatibleAsync(cancellationToken);
                using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_endpoint, "v1/changes"));
                request.Headers.Add(_credentialHeader, _token);
                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();
                var snapshot = await GetBootstrapAsync(cancellationToken);
                SetConnected(true);
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
            _negotiated = false;

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

    private sealed class LeaveOpenStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count)
            => inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => inner.Read(buffer);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer,
            CancellationToken cancellationToken = default)
            => inner.ReadAsync(buffer, cancellationToken);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count,
            CancellationToken cancellationToken)
            => inner.ReadAsync(buffer, offset, count, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();
        protected override void Dispose(bool disposing) => base.Dispose(disposing);
        public override ValueTask DisposeAsync() => base.DisposeAsync();
    }
}
