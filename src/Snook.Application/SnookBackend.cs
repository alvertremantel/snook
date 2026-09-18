using System.Globalization;
using Snook.Contracts;
using Snook.Domain;
using Snook.Persistence.Sqlite;
using DomainCalendar = Snook.Domain.Calendar;

namespace Snook.Application;

public sealed partial class SnookBackend : IBackendClient
{
    private readonly SqliteStore _store;
    private readonly TimeProvider _clock;
    private readonly Guid _deviceId;
    private readonly bool? _allowConcurrentForegroundOverride;
    private bool _disposed;

    public SnookBackend(
        SqliteStore store,
        TimeProvider? clock = null,
        Guid? deviceId = null,
        bool? allowConcurrentForeground = null)
    {
        _store = store;
        _clock = clock ?? TimeProvider.System;
        _deviceId = deviceId ?? Guid.NewGuid();
        _allowConcurrentForegroundOverride = allowConcurrentForeground;
    }

    public event EventHandler<ChangeNotification>? Changed;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _store.InitializeAsync(cancellationToken);
    }

    public Task<WorkspaceSettings> GetSettingsAsync(CancellationToken cancellationToken = default)
        => _store.GetSettingsAsync(cancellationToken);

    public async Task<WorkspaceSettings> UpdateSettingsAsync(WorkspaceSettings settings, OperationRequest request, CancellationToken cancellationToken = default)
    {
        var updated = await _store.UpdateSettingsAsync(settings, RequireRevision(request), request.OperationId, _clock.GetUtcNow(), cancellationToken);
        await PublishAsync(request.OperationId, "workspace-settings", Guid.Empty, "updated", updated.Revision, cancellationToken);
        return updated;
    }

    public async Task<BootstrapSnapshot> GetBootstrapAsync(CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow();
        var state = await _store.LoadStateAsync(now, cancellationToken);
        return BuildBootstrap(state, now);
    }

    public async Task<IReadOnlyList<TaskListItem>> SearchTasksAsync(string? search = null, bool includeCompleted = false, bool includeArchived = false, bool includeDeleted = false, CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow();
        var state = await _store.LoadStateAsync(now, cancellationToken);
        var projects = state.Projects.ToDictionary(project => project.Id);
        var boards = state.Boards.ToDictionary(board => board.Id);
        var sessionsByTask = state.Sessions
            .Where(session => session.TaskId is not null)
            .GroupBy(session => session.TaskId!.Value)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var normalized = search?.Trim();
        return state.Tasks
            .Where(task => (includeArchived || task.ArchivedAtUtc is null) && (includeDeleted || task.DeletedAtUtc is null))
            .Where(task => projects.GetValueOrDefault(task.ProjectId) is { } project && project.ArchivedAtUtc is null && (includeDeleted || project.DeletedAtUtc is null))
            .Where(task => projects.GetValueOrDefault(task.ProjectId) is { } project && boards.GetValueOrDefault(project.BoardId) is { } board && board.ArchivedAtUtc is null && (includeDeleted || board.DeletedAtUtc is null))
            .Where(task => includeCompleted || task.Status != TaskState.Completed)
            .Where(task => string.IsNullOrWhiteSpace(normalized) || task.Title.Contains(normalized, StringComparison.OrdinalIgnoreCase) || task.Description.Contains(normalized, StringComparison.OrdinalIgnoreCase))
            .Select(task =>
            {
                var project = projects[task.ProjectId];
                var board = boards[project.BoardId];
                var sessions = sessionsByTask.GetValueOrDefault(task.Id) ?? [];
                return new TaskListItem(task, project.Name, board.Name, TimeMath.DurationMilliseconds(sessions.SelectMany(session => session.Intervals), now), sessions.Any(session => session.State == SessionState.Running));
            })
            .ToArray();
    }

    public async Task<Board> CreateBoardAsync(string name, CancellationToken cancellationToken = default)
    {
        var operationId = Guid.NewGuid();
        var board = await _store.CreateBoardAsync(name, operationId, _clock.GetUtcNow(), cancellationToken);
        await PublishAsync(operationId, "board", board.Id, "created", board.Revision, cancellationToken);
        return board;
    }

    public async Task<Board> UpdateBoardAsync(Guid boardId, BoardUpdate update, OperationRequest request, CancellationToken cancellationToken = default)
    {
        var board = await _store.UpdateBoardAsync(boardId, update, RequireRevision(request), request.OperationId, _clock.GetUtcNow(), cancellationToken);
        await PublishAsync(request.OperationId, "board", board.Id, "updated", board.Revision, cancellationToken);
        return board;
    }

    public async Task<Board> ReorderBoardAsync(Guid boardId, ReorderDirection direction, OperationRequest request, CancellationToken cancellationToken = default)
    {
        var board = await _store.ReorderBoardAsync(boardId, direction, RequireRevision(request), request.OperationId, _clock.GetUtcNow(), cancellationToken);
        await PublishAsync(request.OperationId, "board", board.Id, "reordered", board.Revision, cancellationToken);
        return board;
    }

    public Task<Board> ArchiveBoardAsync(Guid boardId, OperationRequest request, CancellationToken cancellationToken = default)
        => SetBoardArchivedAsync(boardId, true, request, "archived", cancellationToken);

    public Task<Board> RestoreBoardAsync(Guid boardId, OperationRequest request, CancellationToken cancellationToken = default)
        => SetBoardArchivedAsync(boardId, false, request, "restored", cancellationToken);

    private async Task<Board> SetBoardArchivedAsync(Guid boardId, bool archived, OperationRequest request, string kind, CancellationToken cancellationToken)
    {
        var board = await _store.SetBoardArchivedAsync(boardId, archived, RequireRevision(request), request.OperationId, _clock.GetUtcNow(), cancellationToken);
        await PublishAsync(request.OperationId, "board", board.Id, kind, board.Revision, cancellationToken);
        return board;
    }

    public Task<Board> DeleteBoardAsync(Guid boardId, OperationRequest request, CancellationToken cancellationToken = default)
        => SetBoardDeletedAsync(boardId, true, request, "deleted", cancellationToken);

    public Task<Board> RestoreDeletedBoardAsync(Guid boardId, OperationRequest request, CancellationToken cancellationToken = default)
        => SetBoardDeletedAsync(boardId, false, request, "restored-deleted", cancellationToken);

    private async Task<Board> SetBoardDeletedAsync(Guid boardId, bool deleted, OperationRequest request, string kind, CancellationToken cancellationToken)
    {
        var board = await _store.SetBoardDeletedAsync(boardId, deleted, RequireRevision(request), request.OperationId, _clock.GetUtcNow(), cancellationToken);
        await PublishAsync(request.OperationId, "board", board.Id, kind, board.Revision, cancellationToken);
        return board;
    }

    public async Task<Project> CreateProjectAsync(Guid boardId, string name, string description = "", bool starred = false, CancellationToken cancellationToken = default)
    {
        var operationId = Guid.NewGuid();
        var project = await _store.CreateProjectAsync(boardId, name, description, starred, operationId, _clock.GetUtcNow(), cancellationToken);
        await PublishAsync(operationId, "project", project.Id, "created", project.Revision, cancellationToken);
        return project;
    }

    public async Task<Project> UpdateProjectAsync(Guid projectId, ProjectUpdate update, OperationRequest request, CancellationToken cancellationToken = default)
    {
        var project = await _store.UpdateProjectAsync(projectId, update, RequireRevision(request), request.OperationId, _clock.GetUtcNow(), cancellationToken);
        await PublishAsync(request.OperationId, "project", project.Id, "updated", project.Revision, cancellationToken);
        return project;
    }

    public async Task<Project> ReorderProjectAsync(Guid projectId, ReorderDirection direction, OperationRequest request, CancellationToken cancellationToken = default)
    {
        var project = await _store.ReorderProjectAsync(projectId, direction, RequireRevision(request), request.OperationId, _clock.GetUtcNow(), cancellationToken);
        await PublishAsync(request.OperationId, "project", project.Id, "reordered", project.Revision, cancellationToken);
        return project;
    }

    public Task<Project> ArchiveProjectAsync(Guid projectId, OperationRequest request, CancellationToken cancellationToken = default)
        => SetProjectArchivedAsync(projectId, true, request, "archived", cancellationToken);

    public Task<Project> RestoreProjectAsync(Guid projectId, OperationRequest request, CancellationToken cancellationToken = default)
        => SetProjectArchivedAsync(projectId, false, request, "restored", cancellationToken);

    private async Task<Project> SetProjectArchivedAsync(Guid projectId, bool archived, OperationRequest request, string kind, CancellationToken cancellationToken)
    {
        var project = await _store.SetProjectArchivedAsync(projectId, archived, RequireRevision(request), request.OperationId, _clock.GetUtcNow(), cancellationToken);
        await PublishAsync(request.OperationId, "project", project.Id, kind, project.Revision, cancellationToken);
        return project;
    }

    public Task<Project> DeleteProjectAsync(Guid projectId, OperationRequest request, CancellationToken cancellationToken = default)
        => SetProjectDeletedAsync(projectId, true, request, "deleted", cancellationToken);

    public Task<Project> RestoreDeletedProjectAsync(Guid projectId, OperationRequest request, CancellationToken cancellationToken = default)
        => SetProjectDeletedAsync(projectId, false, request, "restored-deleted", cancellationToken);

    private async Task<Project> SetProjectDeletedAsync(Guid projectId, bool deleted, OperationRequest request, string kind, CancellationToken cancellationToken)
    {
        var project = await _store.SetProjectDeletedAsync(projectId, deleted, RequireRevision(request), request.OperationId, _clock.GetUtcNow(), cancellationToken);
        await PublishAsync(request.OperationId, "project", project.Id, kind, project.Revision, cancellationToken);
        return project;
    }

    public async Task<ActivityGroup> CreateActivityGroupAsync(string name, CancellationToken cancellationToken = default)
    {
        var operationId = Guid.NewGuid();
        var group = await _store.CreateActivityGroupAsync(name, operationId, _clock.GetUtcNow(), cancellationToken);
        await PublishAsync(operationId, "activity-group", group.Id, "created", group.Revision, cancellationToken);
        return group;
    }

    public async Task<ActivityGroup> UpdateActivityGroupAsync(Guid groupId, ActivityGroupUpdate update, OperationRequest request, CancellationToken cancellationToken = default)
    {
        var group = await _store.UpdateActivityGroupAsync(groupId, update, RequireRevision(request), request.OperationId, _clock.GetUtcNow(), cancellationToken);
        await PublishAsync(request.OperationId, "activity-group", group.Id, "updated", group.Revision, cancellationToken);
        return group;
    }

    public async Task<ActivityGroup> ReorderActivityGroupAsync(Guid groupId, ReorderDirection direction, OperationRequest request, CancellationToken cancellationToken = default)
    {
        var group = await _store.ReorderActivityGroupAsync(groupId, direction, RequireRevision(request), request.OperationId, _clock.GetUtcNow(), cancellationToken);
        await PublishAsync(request.OperationId, "activity-group", group.Id, "reordered", group.Revision, cancellationToken);
        return group;
    }

    public Task<ActivityGroup> DeleteActivityGroupAsync(Guid groupId, OperationRequest request, CancellationToken cancellationToken = default)
        => SetActivityGroupDeletedAsync(groupId, true, request, "deleted", cancellationToken);

    public Task<ActivityGroup> RestoreDeletedActivityGroupAsync(Guid groupId, OperationRequest request, CancellationToken cancellationToken = default)
        => SetActivityGroupDeletedAsync(groupId, false, request, "restored-deleted", cancellationToken);

    private async Task<ActivityGroup> SetActivityGroupDeletedAsync(Guid groupId, bool deleted, OperationRequest request, string kind, CancellationToken cancellationToken)
    {
        var group = await _store.SetActivityGroupDeletedAsync(groupId, deleted, RequireRevision(request), request.OperationId, _clock.GetUtcNow(), cancellationToken);
        await PublishAsync(request.OperationId, "activity-group", group.Id, kind, group.Revision, cancellationToken);
        return group;
    }

    public async Task<Activity> CreateActivityAsync(string name, string description = "", SessionLane defaultLane = SessionLane.Foreground, Guid? groupId = null, CancellationToken cancellationToken = default)
    {
        var operationId = Guid.NewGuid();
        var activity = await _store.CreateActivityAsync(name, description, defaultLane, groupId, operationId, _clock.GetUtcNow(), cancellationToken);
        await PublishAsync(operationId, "activity", activity.Id, "created", activity.Revision, cancellationToken);
        return activity;
    }

    public async Task<Activity> UpdateActivityAsync(Guid activityId, ActivityUpdate update, OperationRequest request, CancellationToken cancellationToken = default)
    {
        var activity = await _store.UpdateActivityAsync(activityId, update, RequireRevision(request), request.OperationId, _clock.GetUtcNow(), cancellationToken);
        await PublishAsync(request.OperationId, "activity", activity.Id, "updated", activity.Revision, cancellationToken);
        return activity;
    }

    public Task<Activity> ArchiveActivityAsync(Guid activityId, OperationRequest request, CancellationToken cancellationToken = default)
        => SetActivityArchivedAsync(activityId, true, request, "archived", cancellationToken);

    public Task<Activity> RestoreActivityAsync(Guid activityId, OperationRequest request, CancellationToken cancellationToken = default)
        => SetActivityArchivedAsync(activityId, false, request, "restored", cancellationToken);

    private async Task<Activity> SetActivityArchivedAsync(Guid activityId, bool archived, OperationRequest request, string kind, CancellationToken cancellationToken)
    {
        var activity = await _store.SetActivityArchivedAsync(activityId, archived, RequireRevision(request), request.OperationId, _clock.GetUtcNow(), cancellationToken);
        await PublishAsync(request.OperationId, "activity", activity.Id, kind, activity.Revision, cancellationToken);
        return activity;
    }

    public Task<Activity> DeleteActivityAsync(Guid activityId, OperationRequest request, CancellationToken cancellationToken = default)
        => SetActivityDeletedAsync(activityId, true, request, "deleted", cancellationToken);

    public Task<Activity> RestoreDeletedActivityAsync(Guid activityId, OperationRequest request, CancellationToken cancellationToken = default)
        => SetActivityDeletedAsync(activityId, false, request, "restored-deleted", cancellationToken);

    private async Task<Activity> SetActivityDeletedAsync(Guid activityId, bool deleted, OperationRequest request, string kind, CancellationToken cancellationToken)
    {
        var activity = await _store.SetActivityDeletedAsync(activityId, deleted, RequireRevision(request), request.OperationId, _clock.GetUtcNow(), cancellationToken);
        await PublishAsync(request.OperationId, "activity", activity.Id, kind, activity.Revision, cancellationToken);
        return activity;
    }

    public async Task<HistoryPage> GetHistoryAsync(HistoryQuery query, CancellationToken cancellationToken = default)
    {
        if (query.RangeEndUtc <= query.RangeStartUtc)
        {
            throw new SnookException(SnookErrorCode.ValidationFailed, "History range must end after it starts.");
        }

        var pageSize = Math.Clamp(query.PageSize, 1, 200);
        var offset = int.TryParse(query.ContinuationToken, out var parsedOffset) ? Math.Max(0, parsedOffset) : 0;
        var now = _clock.GetUtcNow();
        var state = await _store.LoadStateAsync(now, cancellationToken);
        var tasks = state.Tasks.ToDictionary(task => task.Id);
        var projects = state.Projects.ToDictionary(project => project.Id);
        var activities = state.Activities.ToDictionary(activity => activity.Id);
        var result = state.Sessions
            .Where(session => session.Intervals.Any(interval => TimeMath.OverlapMilliseconds(interval, query.RangeStartUtc, query.RangeEndUtc, now) > 0))
            .Select(session =>
            {
                var task = session.TaskId is not null ? tasks.GetValueOrDefault(session.TaskId.Value) : null;
                var project = task is not null ? projects.GetValueOrDefault(task.ProjectId) : null;
                var activity = session.ActivityId is not null ? activities.GetValueOrDefault(session.ActivityId.Value) : null;
                return new HistoryItem(session, task?.Title, project?.Name, activity?.Name, session.Intervals.Sum(interval => TimeMath.OverlapMilliseconds(interval, query.RangeStartUtc, query.RangeEndUtc, now)));
            })
            .Where(item => string.IsNullOrWhiteSpace(query.Search)
                || item.TaskTitle?.Contains(query.Search, StringComparison.OrdinalIgnoreCase) == true
                || item.ProjectName?.Contains(query.Search, StringComparison.OrdinalIgnoreCase) == true
                || item.ActivityName?.Contains(query.Search, StringComparison.OrdinalIgnoreCase) == true
                || item.Session.Notes.Contains(query.Search, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.Session.StartedAtUtc)
            .Skip(offset)
            .Take(pageSize + 1)
            .ToArray();
        var hasMore = result.Length > pageSize;
        var items = hasMore ? result[..pageSize] : result;
        return new HistoryPage(items, hasMore ? (offset + pageSize).ToString(CultureInfo.InvariantCulture) : null, hasMore);
    }

    public async Task<IReadOnlyList<SummaryBucket>> GetSummaryAsync(DateTimeOffset rangeStartUtc, DateTimeOffset rangeEndUtc, SummaryGrouping grouping, string? timeZone = null, CancellationToken cancellationToken = default)
    {
        if (rangeEndUtc <= rangeStartUtc)
        {
            throw new SnookException(SnookErrorCode.ValidationFailed, "Summary range must end after it starts.");
        }

        var now = _clock.GetUtcNow();
        var state = await _store.LoadStateAsync(now, cancellationToken);
        var tasks = state.Tasks.ToDictionary(task => task.Id);
        var projects = state.Projects.ToDictionary(project => project.Id);
        var activities = state.Activities.ToDictionary(activity => activity.Id);
        var activityGroups = state.ActivityGroups.ToDictionary(group => group.Id);
        var tags = state.Tags.ToDictionary(tag => tag.Id);
        var buckets = new Dictionary<string, SummaryAccumulator>(StringComparer.Ordinal);
        foreach (var session in state.Sessions)
        {
            var task = session.TaskId is not null ? tasks.GetValueOrDefault(session.TaskId.Value) : null;
            var project = task is not null ? projects.GetValueOrDefault(task.ProjectId) : null;
            var activity = session.ActivityId is not null ? activities.GetValueOrDefault(session.ActivityId.Value) : null;
            foreach (var part in SummaryParts(session, rangeStartUtc, rangeEndUtc, now, ResolveTimeZone(timeZone ?? TimeZoneInfo.Utc.Id)))
            {
                IEnumerable<(string Key, string Label)> groups;
                if (grouping == SummaryGrouping.Tag)
                {
                    var taskTagIds = task is null ? [] : state.TaskTagIds.GetValueOrDefault(task.Id) ?? [];
                    var projectTagIds = project is null ? [] : state.ProjectTagIds.GetValueOrDefault(project.Id) ?? [];
                    var activityTagIds = activity is null ? [] : state.ActivityTagIds.GetValueOrDefault(activity.Id) ?? [];
                    var tagged = taskTagIds.Concat(projectTagIds).Concat(activityTagIds).Distinct()
                        .Select(tagId => tags.GetValueOrDefault(tagId)).Where(tag => tag is not null)
                        .Select(tag => (tag!.Id.ToString("D"), tag.DisplayName)).ToArray();
                    groups = tagged.Length == 0 ? [("untagged", "Untagged")] : tagged;
                }
                else
                {
                    groups = [grouping switch
                    {
                        SummaryGrouping.Task => (task?.Id.ToString("D") ?? "general", task?.Title ?? "General time"),
                        SummaryGrouping.Project => (project?.Id.ToString("D") ?? "general", project?.Name ?? "General time"),
                        SummaryGrouping.Activity => (activity?.Id.ToString("D") ?? "general", activity?.Name ?? "Unassigned activity"),
                        SummaryGrouping.ActivityGroup => (activity?.GroupId is { } groupId && activityGroups.TryGetValue(groupId, out var group) ? group.Id.ToString("D") : "ungrouped-activity", activity?.GroupId is { } activityGroupId && activityGroups.TryGetValue(activityGroupId, out var activityGroup) ? activityGroup.Name : "Ungrouped activity"),
                        SummaryGrouping.Lane => (session.Lane.ToString().ToLowerInvariant(), session.Lane == SessionLane.Background ? "Background" : "Foreground"),
                        _ => (part.DayKey, part.DayLabel)
                    }];
                }

                foreach (var group in groups)
                {
                    if (!buckets.TryGetValue(group.Key, out var accumulator))
                    {
                        accumulator = new SummaryAccumulator(group.Label);
                        buckets.Add(group.Key, accumulator);
                    }

                    accumulator.AttributedMilliseconds += part.DurationMilliseconds;
                    accumulator.Ranges.Add((part.Start, part.End));
                    accumulator.SessionCount++;
                }
            }
        }

        return buckets
            .Select(pair => new SummaryBucket(pair.Key, pair.Value.Label, pair.Value.AttributedMilliseconds, TimeMath.UnionMilliseconds(pair.Value.Ranges), pair.Value.SessionCount))
            .OrderByDescending(bucket => bucket.AttributedMilliseconds)
            .ToArray();
    }

    public async Task<IReadOnlyList<ScheduleBlock>> GetCalendarRangeAsync(CalendarRangeQuery query, CancellationToken cancellationToken = default)
    {
        if (query.RangeEndUtc <= query.RangeStartUtc)
        {
            throw new SnookException(SnookErrorCode.ValidationFailed, "Calendar range must end after it starts.");
        }

        var state = await _store.LoadStateAsync(_clock.GetUtcNow(), cancellationToken);
        var maximum = Math.Clamp(query.MaximumOccurrences, 1, 2_000);
        var visibleCalendarIds = state.Calendars
            .Where(calendar => calendar.Visible && (query.IncludeDeleted || calendar.DeletedAtUtc is null))
            .Select(calendar => calendar.Id)
            .ToHashSet();
        var result = new List<ScheduleBlock>(maximum);
        foreach (var block in state.ScheduleBlocks.Where(block => visibleCalendarIds.Contains(block.CalendarId) && (query.IncludeDeleted || block.DeletedAtUtc is null)))
        {
            result.AddRange(ExpandScheduleBlock(block, query).Take(maximum - result.Count));
            if (result.Count >= maximum)
            {
                break;
            }
        }

        return result.OrderBy(block => block.StartAtUtc).ToArray();
    }

    public async Task<DomainCalendar> CreateCalendarAsync(string name, string color = "#6767F2", bool visible = true, CancellationToken cancellationToken = default)
    {
        var operationId = Guid.NewGuid();
        var calendar = await _store.CreateCalendarAsync(name, color, visible, operationId, _clock.GetUtcNow(), cancellationToken);
        await PublishAsync(operationId, "calendar", calendar.Id, "created", calendar.Revision, cancellationToken);
        return calendar;
    }

    public async Task<DomainCalendar> UpdateCalendarAsync(Guid calendarId, CalendarUpdate update, OperationRequest request, CancellationToken cancellationToken = default)
    {
        var calendar = await _store.UpdateCalendarAsync(calendarId, update, RequireRevision(request), request.OperationId, _clock.GetUtcNow(), cancellationToken);
        await PublishAsync(request.OperationId, "calendar", calendar.Id, "updated", calendar.Revision, cancellationToken);
        return calendar;
    }

    public Task<DomainCalendar> DeleteCalendarAsync(Guid calendarId, OperationRequest request, CancellationToken cancellationToken = default)
        => SetCalendarDeletedAsync(calendarId, true, request, "deleted", cancellationToken);

    public Task<DomainCalendar> RestoreDeletedCalendarAsync(Guid calendarId, OperationRequest request, CancellationToken cancellationToken = default)
        => SetCalendarDeletedAsync(calendarId, false, request, "restored-deleted", cancellationToken);

    private async Task<DomainCalendar> SetCalendarDeletedAsync(Guid calendarId, bool deleted, OperationRequest request, string kind, CancellationToken cancellationToken)
    {
        var calendar = await _store.SetCalendarDeletedAsync(calendarId, deleted, RequireRevision(request), request.OperationId, _clock.GetUtcNow(), cancellationToken);
        await PublishAsync(request.OperationId, "calendar", calendar.Id, kind, calendar.Revision, cancellationToken);
        return calendar;
    }

    public async Task<IReadOnlyList<CalendarEvent>> GetCalendarEventsRangeAsync(CalendarRangeQuery query, CancellationToken cancellationToken = default)
    {
        if (query.RangeEndUtc <= query.RangeStartUtc)
        {
            throw new SnookException(SnookErrorCode.ValidationFailed, "Calendar range must end after it starts.");
        }

        var state = await _store.LoadStateAsync(_clock.GetUtcNow(), cancellationToken);
        var maximum = Math.Clamp(query.MaximumOccurrences, 1, 2_000);
        var visibleCalendarIds = state.Calendars
            .Where(calendar => calendar.Visible && (query.IncludeDeleted || calendar.DeletedAtUtc is null))
            .Select(calendar => calendar.Id)
            .ToHashSet();
        var exceptions = state.CalendarEventExceptions
            .GroupBy(item => item.EventId)
            .ToDictionary(group => group.Key, group => group.ToDictionary(item => item.OriginalStartAtUtc));
        var result = new List<CalendarEvent>(maximum);
        foreach (var item in state.CalendarEvents.Where(item => visibleCalendarIds.Contains(item.CalendarId) && (query.IncludeDeleted || item.DeletedAtUtc is null)))
        {
            result.AddRange(ExpandCalendarEvent(item, exceptions.GetValueOrDefault(item.Id), query).Take(maximum - result.Count));
            if (result.Count >= maximum)
            {
                break;
            }
        }

        return result.OrderBy(item => item.StartAtUtc).ToArray();
    }

    public async Task<CalendarEvent> CreateCalendarEventAsync(Guid calendarId, string title, DateTimeOffset startAtUtc, DateTimeOffset endAtUtc, string description = "", string? location = null, string color = "#6767F2", bool allDay = false, string timeZone = "UTC", string? recurrenceRule = null, DateTimeOffset? recurrenceEndUtc = null, CancellationToken cancellationToken = default)
    {
        var operationId = Guid.NewGuid();
        var item = await _store.CreateCalendarEventAsync(calendarId, title, startAtUtc, endAtUtc, description, location, color, allDay, timeZone, recurrenceRule, recurrenceEndUtc, operationId, _clock.GetUtcNow(), cancellationToken);
        await PublishAsync(operationId, "calendar-event", item.Id, "created", item.Revision, cancellationToken);
        return item;
    }

    public async Task<CalendarEvent> UpdateCalendarEventAsync(Guid eventId, CalendarEventUpdate update, OperationRequest request, CancellationToken cancellationToken = default)
    {
        var item = await _store.UpdateCalendarEventAsync(eventId, update, RequireRevision(request), request.OperationId, _clock.GetUtcNow(), cancellationToken);
        await PublishAsync(request.OperationId, "calendar-event", item.Id, "updated", item.Revision, cancellationToken);
        return item;
    }

    public Task<CalendarEvent> DeleteCalendarEventAsync(Guid eventId, OperationRequest request, CancellationToken cancellationToken = default)
        => SetCalendarEventDeletedAsync(eventId, true, request, "deleted", cancellationToken);

    public Task<CalendarEvent> RestoreDeletedCalendarEventAsync(Guid eventId, OperationRequest request, CancellationToken cancellationToken = default)
        => SetCalendarEventDeletedAsync(eventId, false, request, "restored-deleted", cancellationToken);

    private async Task<CalendarEvent> SetCalendarEventDeletedAsync(Guid eventId, bool deleted, OperationRequest request, string kind, CancellationToken cancellationToken)
    {
        var item = await _store.SetCalendarEventDeletedAsync(eventId, deleted, RequireRevision(request), request.OperationId, _clock.GetUtcNow(), cancellationToken);
        await PublishAsync(request.OperationId, "calendar-event", item.Id, kind, item.Revision, cancellationToken);
        return item;
    }

    public async Task<CalendarEventOccurrenceOverride> UpsertCalendarEventExceptionAsync(Guid eventId, DateTimeOffset originalStartAtUtc, DateTimeOffset? newStartAtUtc, DateTimeOffset? newEndAtUtc, string? titleOverride, bool cancelled, OperationRequest request, CancellationToken cancellationToken = default)
    {
        var item = await _store.UpsertCalendarEventExceptionAsync(eventId, originalStartAtUtc, newStartAtUtc, newEndAtUtc, titleOverride, cancelled, request.ExpectedRevision, request.OperationId, _clock.GetUtcNow(), cancellationToken);
        await PublishAsync(request.OperationId, "calendar-event-exception", item.Id, item.Revision == 1 ? "created" : "updated", item.Revision, cancellationToken);
        return item;
    }

    public async Task DeleteCalendarEventExceptionAsync(Guid exceptionId, OperationRequest request, CancellationToken cancellationToken = default)
    {
        await _store.DeleteCalendarEventExceptionAsync(exceptionId, RequireRevision(request), request.OperationId, _clock.GetUtcNow(), cancellationToken);
        await PublishAsync(request.OperationId, "calendar-event-exception", exceptionId, "deleted", RequireRevision(request) + 1, cancellationToken);
    }

    public async Task<ScheduleBlock> CreateScheduleBlockAsync(Guid calendarId, Guid? taskId, Guid? activityId, string? titleOverride, DateTimeOffset startAtUtc, DateTimeOffset endAtUtc, string timeZone, string? recurrenceRule = null, DateTimeOffset? recurrenceEndUtc = null, CancellationToken cancellationToken = default)
    {
        var operationId = Guid.NewGuid();
        var block = await _store.CreateScheduleBlockAsync(calendarId, taskId, activityId, titleOverride, startAtUtc, endAtUtc, timeZone, recurrenceRule, recurrenceEndUtc, operationId, _clock.GetUtcNow(), cancellationToken);
        await PublishAsync(operationId, "schedule-block", block.Id, "created", block.Revision, cancellationToken);
        return block;
    }

    public async Task<ScheduleBlock> UpdateScheduleBlockAsync(Guid blockId, ScheduleBlockUpdate update, OperationRequest request, CancellationToken cancellationToken = default)
    {
        var block = await _store.UpdateScheduleBlockAsync(blockId, update, RequireRevision(request), request.OperationId, _clock.GetUtcNow(), cancellationToken);
        await PublishAsync(request.OperationId, "schedule-block", block.Id, "updated", block.Revision, cancellationToken);
        return block;
    }

    public async Task<TaskItem> CreateTaskAsync(Guid projectId, string title, Priority priority = Priority.None, DateOnly? dueDate = null, CancellationToken cancellationToken = default)
    {
        var operationId = Guid.NewGuid();
        var task = await _store.CreateTaskAsync(projectId, title, priority, dueDate, operationId, _clock.GetUtcNow(), cancellationToken);
        await PublishAsync(operationId, "task", task.Id, "created", task.Revision, cancellationToken);
        return task;
    }

    public async Task<TaskItem> UpdateTaskAsync(Guid taskId, TaskUpdate update, OperationRequest request, CancellationToken cancellationToken = default)
    {
        var task = await _store.UpdateTaskAsync(taskId, update, RequireRevision(request), request.OperationId, _clock.GetUtcNow(), cancellationToken);
        await PublishAsync(request.OperationId, "task", task.Id, "updated", task.Revision, cancellationToken);
        return task;
    }

    public async Task<TaskItem> MoveTaskAsync(Guid taskId, Guid projectId, OperationRequest request, CancellationToken cancellationToken = default)
    {
        var task = await _store.MoveTaskAsync(taskId, projectId, RequireRevision(request), request.OperationId, _clock.GetUtcNow(), cancellationToken);
        await PublishAsync(request.OperationId, "task", task.Id, "moved", task.Revision, cancellationToken);
        return task;
    }

    public Task<TaskItem> ArchiveTaskAsync(Guid taskId, OperationRequest request, CancellationToken cancellationToken = default)
        => SetTaskArchivedAsync(taskId, true, request, "archived", cancellationToken);

    public Task<TaskItem> RestoreTaskAsync(Guid taskId, OperationRequest request, CancellationToken cancellationToken = default)
        => SetTaskArchivedAsync(taskId, false, request, "unarchived", cancellationToken);

    public Task<TaskItem> DeleteTaskAsync(Guid taskId, OperationRequest request, CancellationToken cancellationToken = default)
        => SetTaskDeletedAsync(taskId, true, request, "deleted", cancellationToken);

    public Task<TaskItem> RestoreDeletedTaskAsync(Guid taskId, OperationRequest request, CancellationToken cancellationToken = default)
        => SetTaskDeletedAsync(taskId, false, request, "restored", cancellationToken);

    private async Task<TaskItem> SetTaskArchivedAsync(Guid taskId, bool archived, OperationRequest request, string kind, CancellationToken cancellationToken)
    {
        var task = await _store.SetTaskArchivedAsync(taskId, archived, RequireRevision(request), request.OperationId, _clock.GetUtcNow(), cancellationToken);
        await PublishAsync(request.OperationId, "task", task.Id, kind, task.Revision, cancellationToken);
        return task;
    }

    private async Task<TaskItem> SetTaskDeletedAsync(Guid taskId, bool deleted, OperationRequest request, string kind, CancellationToken cancellationToken)
    {
        var task = await _store.SetTaskDeletedAsync(taskId, deleted, RequireRevision(request), request.OperationId, _clock.GetUtcNow(), cancellationToken);
        await PublishAsync(request.OperationId, "task", task.Id, kind, task.Revision, cancellationToken);
        return task;
    }

    public Task<TaskDetails> GetTaskDetailsAsync(Guid taskId, CancellationToken cancellationToken = default) => _store.GetTaskDetailsAsync(taskId, _clock.GetUtcNow(), cancellationToken);

    public async Task<ProjectDetails> GetProjectDetailsAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow();
        var state = await _store.LoadStateAsync(now, cancellationToken);
        var project = state.Projects.FirstOrDefault(item => item.Id == projectId)
            ?? throw new SnookException(SnookErrorCode.NotFound, "Project was not found.");
        var tasks = state.Tasks.Where(task => task.ProjectId == projectId).ToArray();
        var taskIds = tasks.Select(task => task.Id).ToHashSet();
        var sessions = state.Sessions.Where(session => session.TaskId is not null && taskIds.Contains(session.TaskId.Value)).ToArray();
        var tracked = sessions.SelectMany(session => session.Intervals).Sum(interval => TimeMath.DurationMilliseconds([interval], now));
        var active = sessions.Where(session => session.State == SessionState.Running).SelectMany(session => session.Intervals.Where(interval => interval.EndedAtUtc is null)).Sum(interval => TimeMath.DurationMilliseconds([interval], now));
        return new ProjectDetails(project, tasks, tracked, active);
    }

    public async Task<TaskLink> AddTaskLinkAsync(Guid taskId, string? label, string uri, string kind = "reference", CancellationToken cancellationToken = default)
    {
        var operationId = Guid.NewGuid();
        var link = await _store.AddTaskLinkAsync(taskId, label, uri, kind, operationId, _clock.GetUtcNow(), cancellationToken);
        await PublishAsync(operationId, "task-link", link.Id, "created", 1, cancellationToken);
        return link;
    }

    public async Task<Tag> AddTaskTagAsync(Guid taskId, string displayName, string? color = null, CancellationToken cancellationToken = default)
    {
        var operationId = Guid.NewGuid();
        var tag = await _store.AddTaskTagAsync(taskId, displayName, color, operationId, _clock.GetUtcNow(), cancellationToken);
        await PublishAsync(operationId, "task", taskId, "tag-added", 1, cancellationToken);
        return tag;
    }

    public async Task<Tag> AddProjectTagAsync(Guid projectId, string displayName, string? color = null, CancellationToken cancellationToken = default)
    {
        var operationId = Guid.NewGuid();
        var tag = await _store.AddProjectTagAsync(projectId, displayName, color, operationId, _clock.GetUtcNow(), cancellationToken);
        await PublishAsync(operationId, "project", projectId, "tag-added", 0, cancellationToken);
        return tag;
    }

    public async Task<Tag> AddActivityTagAsync(Guid activityId, string displayName, string? color = null, CancellationToken cancellationToken = default)
    {
        var operationId = Guid.NewGuid();
        var tag = await _store.AddActivityTagAsync(activityId, displayName, color, operationId, _clock.GetUtcNow(), cancellationToken);
        await PublishAsync(operationId, "activity", activityId, "tag-added", 0, cancellationToken);
        return tag;
    }

    public async Task<TaskItem> CompleteTaskAsync(Guid taskId, OperationRequest request, CancellationToken cancellationToken = default)
    {
        var task = await _store.SetTaskCompletionAsync(taskId, true, RequireRevision(request), request.OperationId, _clock.GetUtcNow(), cancellationToken);
        await PublishAsync(request.OperationId, "task", task.Id, "completed", task.Revision, cancellationToken);
        return task;
    }

    public async Task<TaskItem> ReopenTaskAsync(Guid taskId, OperationRequest request, CancellationToken cancellationToken = default)
    {
        var task = await _store.SetTaskCompletionAsync(taskId, false, RequireRevision(request), request.OperationId, _clock.GetUtcNow(), cancellationToken);
        await PublishAsync(request.OperationId, "task", task.Id, "reopened", task.Revision, cancellationToken);
        return task;
    }

    public async Task AddTaskDependencyAsync(Guid taskId, Guid prerequisiteTaskId, CancellationToken cancellationToken = default)
    {
        var operationId = Guid.NewGuid();
        await _store.AddTaskDependencyAsync(taskId, prerequisiteTaskId, operationId, _clock.GetUtcNow(), cancellationToken);
        await PublishAsync(operationId, "task", taskId, "dependency-added", 1, cancellationToken);
    }

    public async Task<TrackingSession> StartSessionAsync(Guid? taskId, Guid? activityId, SessionLane lane, OperationRequest request, CancellationToken cancellationToken = default)
    {
        if (taskId is null && activityId is null)
        {
            throw new SnookException(SnookErrorCode.ValidationFailed, "Choose a task or activity before starting a timer.");
        }

        if (taskId is not null && activityId is null)
        {
            var state = await _store.LoadStateAsync(_clock.GetUtcNow(), cancellationToken);
            activityId = state.Tasks.FirstOrDefault(task => task.Id == taskId)?.DefaultActivityId;
        }

        var allowConcurrentForeground = _allowConcurrentForegroundOverride ?? (await _store.GetSettingsAsync(cancellationToken)).AllowConcurrentForeground;
        var session = await _store.StartSessionAsync(taskId, activityId, lane, request.OperationId, _clock.GetUtcNow(), allowConcurrentForeground, cancellationToken);
        await PublishAsync(request.OperationId, "session", session.Id, "started", session.Revision, cancellationToken);
        return session;
    }

    public async Task<TrackingSession> PauseSessionAsync(Guid sessionId, OperationRequest request, CancellationToken cancellationToken = default)
    {
        return await TransitionAsync(sessionId, SessionState.Paused, request, cancellationToken);
    }

    public async Task<TrackingSession> ResumeSessionAsync(Guid sessionId, OperationRequest request, CancellationToken cancellationToken = default)
    {
        return await TransitionAsync(sessionId, SessionState.Running, request, cancellationToken);
    }

    public async Task<TrackingSession> StopSessionAsync(Guid sessionId, string? notes, OperationRequest request, CancellationToken cancellationToken = default)
    {
        var session = await _store.TransitionSessionAsync(sessionId, SessionState.Stopped, RequireRevision(request), request.OperationId, _clock.GetUtcNow(), notes, cancellationToken: cancellationToken);
        await PublishAsync(request.OperationId, "session", session.Id, "stopped", session.Revision, cancellationToken);
        return session;
    }

    public async Task<TrackingSession> CreateManualSessionAsync(Guid? taskId, Guid? activityId, DateTimeOffset startedAtUtc, DateTimeOffset endedAtUtc, string? notes, OperationRequest? request = null, CancellationToken cancellationToken = default)
    {
        var operationId = request?.OperationId ?? Guid.NewGuid();
        var session = await _store.CreateManualSessionAsync(taskId, activityId, startedAtUtc, endedAtUtc, Guard.Optional(notes, "notes"), operationId, _clock.GetUtcNow(), cancellationToken);
        await PublishAsync(operationId, "session", session.Id, "manual", session.Revision, cancellationToken);
        return session;
    }

    public async Task<TrackingSession> CorrectSessionAsync(Guid sessionId, SessionCorrection correction, OperationRequest request, CancellationToken cancellationToken = default)
    {
        var session = await _store.CorrectSessionAsync(sessionId, correction, RequireRevision(request), request.OperationId, _clock.GetUtcNow(), cancellationToken);
        await PublishAsync(request.OperationId, "session", session.Id, "corrected", session.Revision, cancellationToken);
        return session;
    }

    public Task<IReadOnlyList<TrackingCorrection>> GetSessionCorrectionsAsync(Guid sessionId, CancellationToken cancellationToken = default)
        => _store.GetSessionCorrectionsAsync(sessionId, cancellationToken);

    public async Task<TrackingSession> ResolveRecoveryAsync(Guid sessionId, RecoveryDecision decision, OperationRequest request, CancellationToken cancellationToken = default)
    {
        var session = await _store.ResolveRecoveryAsync(sessionId, decision, RequireRevision(request), request.OperationId, _clock.GetUtcNow(), cancellationToken);
        await PublishAsync(request.OperationId, "session", session.Id, $"recovery-{decision.ToString().ToLowerInvariant()}", session.Revision, cancellationToken);
        return session;
    }

    public async Task<BackupResult> CreateBackupAsync(string destinationPath, CancellationToken cancellationToken = default)
    {
        var result = await _store.CreateBackupAsync(destinationPath, cancellationToken);
        return new BackupResult(result.Path, result.Bytes, result.Sha256, result.ManifestPath);
    }

    public async Task<ExportResult> ExportJsonAsync(string destinationPath, CancellationToken cancellationToken = default)
    {
        var result = await _store.ExportJsonAsync(destinationPath, cancellationToken);
        return new ExportResult(result.Path, result.Bytes, result.Sha256, result.SchemaVersion);
    }

    public async Task<ExportResult> ExportCsvAsync(string destinationPath, DateTimeOffset rangeStartUtc, DateTimeOffset rangeEndUtc, CancellationToken cancellationToken = default)
    {
        var result = await _store.ExportCsvAsync(destinationPath, rangeStartUtc, rangeEndUtc, cancellationToken);
        return new ExportResult(result.Path, result.Bytes, result.Sha256, result.SchemaVersion);
    }

    public async Task<RestoreResult> RestoreBackupAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        var result = await _store.RestoreBackupAsync(sourcePath, cancellationToken);
        await PublishAsync(Guid.NewGuid(), "workspace", Guid.Empty, "restored", 0, cancellationToken);
        return new RestoreResult(result.Path, result.Bytes, result.Sha256);
    }

    private async Task<TrackingSession> TransitionAsync(Guid sessionId, SessionState target, OperationRequest request, CancellationToken cancellationToken)
    {
        var pauseOtherForeground = target == SessionState.Running
            && !(_allowConcurrentForegroundOverride ?? (await _store.GetSettingsAsync(cancellationToken)).AllowConcurrentForeground);
        var session = await _store.TransitionSessionAsync(
            sessionId,
            target,
            RequireRevision(request),
            request.OperationId,
            _clock.GetUtcNow(),
            pauseOtherForeground: pauseOtherForeground,
            cancellationToken: cancellationToken);
        await PublishAsync(request.OperationId, "session", session.Id, target.ToString().ToLowerInvariant(), session.Revision, cancellationToken);
        return session;
    }

    public async Task<IReadOnlyList<TaskItem>> BulkUpdateTasksAsync(IReadOnlyList<TaskRevision> tasks, BulkTaskUpdate update, OperationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = await _store.BulkUpdateTasksAsync(tasks, update, request.OperationId, _clock.GetUtcNow(), cancellationToken);
        await PublishAsync(request.OperationId, "task-batch", request.OperationId, "updated", 1, cancellationToken);
        return result;
    }

    private async Task PublishAsync(Guid operationId, string aggregateType, Guid aggregateId, string kind, long revision, CancellationToken cancellationToken)
    {
        var state = await _store.LoadStateAsync(_clock.GetUtcNow(), cancellationToken);
        Changed?.Invoke(this, new ChangeNotification(state.Cursor, operationId, aggregateType, aggregateId, kind, revision, _clock.GetUtcNow()));
    }

    private static BootstrapSnapshot BuildBootstrap(StoreState state, DateTimeOffset now)
    {
        var projects = state.Projects.ToDictionary(project => project.Id);
        var boards = state.Boards.ToDictionary(board => board.Id);
        var liveBoards = state.Boards.Where(board => board.DeletedAtUtc is null).ToArray();
        var liveProjects = state.Projects.Where(project => project.DeletedAtUtc is null).ToArray();
        var liveActivities = state.Activities.Where(activity => activity.DeletedAtUtc is null).ToArray();
        var liveActivityGroups = state.ActivityGroups.Where(group => group.DeletedAtUtc is null).ToArray();
        var liveCalendars = state.Calendars.Where(calendar => calendar.DeletedAtUtc is null).ToArray();
        var deletedItems = new DeletedItemsSnapshot(
            state.Boards.Where(board => board.DeletedAtUtc is not null).ToArray(),
            state.Projects.Where(project => project.DeletedAtUtc is not null).ToArray(),
            state.Activities.Where(activity => activity.DeletedAtUtc is not null).ToArray(),
            state.ActivityGroups.Where(group => group.DeletedAtUtc is not null).ToArray(),
            state.Tasks.Where(task => task.DeletedAtUtc is not null).ToArray(),
            state.Calendars.Where(calendar => calendar.DeletedAtUtc is not null).ToArray(),
            state.CalendarEvents.Where(item => item.DeletedAtUtc is not null).ToArray());
        var active = state.Sessions
            .Where(session => session.State is SessionState.Running or SessionState.Paused)
            .Select(session =>
            {
                var task = session.TaskId is null ? null : state.Tasks.FirstOrDefault(item => item.Id == session.TaskId);
                var activity = session.ActivityId is null ? null : state.Activities.FirstOrDefault(item => item.Id == session.ActivityId);
                return new ActiveSessionItem(session, task?.Title, activity?.Name, TimeMath.DurationMilliseconds(session.Intervals, now));
            })
            .ToArray();
        var taskItems = state.Tasks
            .Where(task => task.Status == TaskState.Open && task.ArchivedAtUtc is null && task.DeletedAtUtc is null)
            .Where(task => projects.GetValueOrDefault(task.ProjectId) is { } project && project.ArchivedAtUtc is null && project.DeletedAtUtc is null)
            .Where(task => projects.GetValueOrDefault(task.ProjectId) is { } project && boards.GetValueOrDefault(project.BoardId) is { } board && board.ArchivedAtUtc is null && board.DeletedAtUtc is null)
            .Select(task =>
            {
                var project = projects[task.ProjectId];
                var board = boards[project.BoardId];
                var sessions = state.Sessions.Where(session => session.TaskId == task.Id).ToArray();
                return new TaskListItem(task, project.Name, board.Name, TimeMath.DurationMilliseconds(sessions.SelectMany(session => session.Intervals), now), sessions.Any(session => session.State == SessionState.Running));
            })
            .ToArray();
        var recoverySessions = state.Sessions.Where(session => session.State == SessionState.RecoveryRequired).ToArray();
        var (dayStart, dayEnd) = TimeMath.LocalDayRangeUtc(now, TimeZoneInfo.Local);
        var today = taskItems;
        var trackedToday = state.Sessions.SelectMany(session => session.Intervals).Sum(interval => TimeMath.OverlapMilliseconds(interval, dayStart, dayEnd, now));
        return new BootstrapSnapshot(
            state.Workspace,
            liveBoards,
            liveProjects,
            liveActivities,
            liveCalendars,
            new TodaySnapshot(now, taskItems.Where(item => item.Task.Priority >= Priority.High).Take(5).ToArray(), active, trackedToday, today.Length, recoverySessions),
            state.Cursor,
            new HostCapabilities("embedded", true, false, true, true, false, false),
            state.Settings,
            liveActivityGroups,
            deletedItems);
    }

    private static IEnumerable<CalendarEvent> ExpandCalendarEvent(CalendarEvent item, IReadOnlyDictionary<DateTimeOffset, CalendarEventOccurrenceOverride>? exceptions, CalendarRangeQuery query)
    {
        if (string.IsNullOrWhiteSpace(item.RecurrenceRule))
        {
            if (item.StartAtUtc < query.RangeEndUtc && item.EndAtUtc > query.RangeStartUtc)
            {
                yield return item;
            }

            yield break;
        }

        var parts = item.RecurrenceRule.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => part.Split('=', 2, StringSplitOptions.TrimEntries))
            .Where(pair => pair.Length == 2)
            .ToDictionary(pair => pair[0].ToUpperInvariant(), pair => pair[1].ToUpperInvariant(), StringComparer.OrdinalIgnoreCase);
        if (!parts.TryGetValue("FREQ", out var frequency))
        {
            yield break;
        }

        var interval = parts.TryGetValue("INTERVAL", out var intervalText) && int.TryParse(intervalText, CultureInfo.InvariantCulture, out var parsedInterval) ? parsedInterval : 1;
        var zone = ResolveTimeZone(item.TimeZone);
        var localBase = TimeZoneInfo.ConvertTime(item.StartAtUtc, zone).DateTime;
        var duration = item.EndAtUtc - item.StartAtUtc;
        var localQueryStart = TimeZoneInfo.ConvertTime(query.RangeStartUtc, zone).DateTime.Date;
        var localQueryEnd = TimeZoneInfo.ConvertTime(query.RangeEndUtc, zone).DateTime.Date.AddDays(1);
        var byDays = parts.TryGetValue("BYDAY", out var dayText)
            ? dayText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(ParseDay).OrderBy(day => day).ToArray()
            : [DayIndex(localBase.DayOfWeek)];

        if (frequency == "DAILY")
        {
            var firstIndex = Math.Max(0, (int)Math.Floor((localQueryStart - localBase.Date).TotalDays / interval) - 1);
            for (var index = firstIndex; index < firstIndex + 20_000; index++)
            {
                var localStart = localBase.AddDays(index * interval);
                var occurrence = BuildCalendarEventOccurrence(item, localStart, duration, zone, exceptions);
                if (occurrence is null)
                {
                    if (localStart >= localQueryEnd)
                    {
                        yield break;
                    }

                    continue;
                }

                if (occurrence.StartAtUtc >= query.RangeEndUtc && localStart >= localQueryEnd)
                {
                    yield break;
                }

                if (occurrence.StartAtUtc < query.RangeEndUtc && occurrence.EndAtUtc > query.RangeStartUtc)
                {
                    yield return occurrence;
                }
            }

            yield break;
        }

        var monday = localBase.Date.AddDays(-DayIndex(localBase.DayOfWeek));
        var firstWeek = Math.Max(0, (int)Math.Floor((localQueryStart - monday).TotalDays / 7 / interval) - 1);
        for (var week = firstWeek; week < firstWeek + 10_000; week++)
        {
            var weekStart = monday.AddDays(week * 7L * interval);
            if (weekStart >= localQueryEnd)
            {
                yield break;
            }

            foreach (var day in byDays)
            {
                var localStart = weekStart.AddDays(day).Add(localBase.TimeOfDay);
                if (localStart < localBase)
                {
                    continue;
                }

                var occurrence = BuildCalendarEventOccurrence(item, localStart, duration, zone, exceptions);
                if (occurrence is not null && occurrence.StartAtUtc < query.RangeEndUtc && occurrence.EndAtUtc > query.RangeStartUtc)
                {
                    yield return occurrence;
                }
            }
        }
    }

    private static CalendarEvent? BuildCalendarEventOccurrence(CalendarEvent item, DateTime localStart, TimeSpan duration, TimeZoneInfo zone, IReadOnlyDictionary<DateTimeOffset, CalendarEventOccurrenceOverride>? exceptions)
    {
        localStart = DateTime.SpecifyKind(localStart, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(localStart))
        {
            return null;
        }

        var utcStart = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localStart, zone));
        if (item.RecurrenceEndUtc is not null && utcStart > item.RecurrenceEndUtc.Value)
        {
            return null;
        }

        if (exceptions is not null && exceptions.TryGetValue(utcStart, out var exception))
        {
            if (exception.Cancelled)
            {
                return null;
            }

            var overriddenStart = exception.NewStartAtUtc ?? utcStart;
            var overriddenEnd = exception.NewEndAtUtc ?? overriddenStart.Add(duration);
            return item with
            {
                StartAtUtc = overriddenStart,
                EndAtUtc = overriddenEnd,
                Title = exception.TitleOverride ?? item.Title
            };
        }

        return item with { StartAtUtc = utcStart, EndAtUtc = utcStart.Add(duration) };
    }

    private static IEnumerable<ScheduleBlock> ExpandScheduleBlock(ScheduleBlock block, CalendarRangeQuery query)
    {
        if (string.IsNullOrWhiteSpace(block.RecurrenceRule))
        {
            if (block.StartAtUtc < query.RangeEndUtc && block.EndAtUtc > query.RangeStartUtc)
            {
                yield return block;
            }

            yield break;
        }

        var parts = block.RecurrenceRule.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => part.Split('=', 2, StringSplitOptions.TrimEntries))
            .Where(pair => pair.Length == 2)
            .ToDictionary(pair => pair[0].ToUpperInvariant(), pair => pair[1].ToUpperInvariant(), StringComparer.OrdinalIgnoreCase);
        if (!parts.TryGetValue("FREQ", out var frequency))
        {
            yield break;
        }

        var interval = parts.TryGetValue("INTERVAL", out var intervalText) && int.TryParse(intervalText, CultureInfo.InvariantCulture, out var parsedInterval) ? parsedInterval : 1;
        var zone = ResolveTimeZone(block.TimeZone);
        var localBase = TimeZoneInfo.ConvertTime(block.StartAtUtc, zone).DateTime;
        var duration = block.EndAtUtc - block.StartAtUtc;
        var localQueryStart = TimeZoneInfo.ConvertTime(query.RangeStartUtc, zone).DateTime.Date;
        var localQueryEnd = TimeZoneInfo.ConvertTime(query.RangeEndUtc, zone).DateTime.Date.AddDays(1);
        var recurrenceEnd = block.RecurrenceEndUtc;
        var byDays = parts.TryGetValue("BYDAY", out var dayText)
            ? dayText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(ParseDay).OrderBy(day => day).ToArray()
            : [DayIndex(localBase.DayOfWeek)];

        if (frequency == "DAILY")
        {
            var firstIndex = Math.Max(0, (int)Math.Floor((localQueryStart - localBase.Date).TotalDays / interval) - 1);
            for (var index = firstIndex; index < firstIndex + 20_000; index++)
            {
                var localStart = localBase.AddDays(index * interval);
                if (localStart >= localQueryEnd && localStart.ToUniversalTime() >= query.RangeEndUtc.UtcDateTime)
                {
                    yield break;
                }

                var occurrence = BuildOccurrence(block, localStart, duration, zone, recurrenceEnd);
                if (occurrence is not null && occurrence.StartAtUtc < query.RangeEndUtc && occurrence.EndAtUtc > query.RangeStartUtc)
                {
                    yield return occurrence;
                }
            }

            yield break;
        }

        var monday = localBase.Date.AddDays(-DayIndex(localBase.DayOfWeek));
        var firstWeek = Math.Max(0, (int)Math.Floor((localQueryStart - monday).TotalDays / 7 / interval) - 1);
        for (var week = firstWeek; week < firstWeek + 10_000; week++)
        {
            var weekStart = monday.AddDays(week * 7L * interval);
            if (weekStart >= localQueryEnd && weekStart.ToUniversalTime() >= query.RangeEndUtc.UtcDateTime)
            {
                yield break;
            }

            foreach (var day in byDays)
            {
                var localStart = weekStart.AddDays(day).Add(localBase.TimeOfDay);
                if (localStart < localBase)
                {
                    continue;
                }

                var occurrence = BuildOccurrence(block, localStart, duration, zone, recurrenceEnd);
                if (occurrence is not null && occurrence.StartAtUtc < query.RangeEndUtc && occurrence.EndAtUtc > query.RangeStartUtc)
                {
                    yield return occurrence;
                }
            }
        }
    }

    private static ScheduleBlock? BuildOccurrence(ScheduleBlock block, DateTime localStart, TimeSpan duration, TimeZoneInfo zone, DateTimeOffset? recurrenceEnd)
    {
        localStart = DateTime.SpecifyKind(localStart, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(localStart))
        {
            return null;
        }

        var utcStart = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localStart, zone));
        if (recurrenceEnd is not null && utcStart > recurrenceEnd.Value)
        {
            return null;
        }

        return block with { StartAtUtc = utcStart, EndAtUtc = utcStart.Add(duration) };
    }

    private static TimeZoneInfo ResolveTimeZone(string timeZone)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZone);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Utc;
        }
        catch (InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }

    private static int ParseDay(string value) => value switch
    {
        "MO" => 0,
        "TU" => 1,
        "WE" => 2,
        "TH" => 3,
        "FR" => 4,
        "SA" => 5,
        "SU" => 6,
        _ => throw new SnookException(SnookErrorCode.ValidationFailed, "Recurrence contains an invalid weekday.")
    };

    private static int DayIndex(DayOfWeek day) => day == DayOfWeek.Sunday ? 6 : (int)day - 1;

    private static long RequireRevision(OperationRequest request) => request.ExpectedRevision ?? throw new SnookException(SnookErrorCode.ValidationFailed, "An expected revision is required for this mutation.");

    private static IEnumerable<SummaryPart> SummaryParts(TrackingSession session, DateTimeOffset rangeStartUtc, DateTimeOffset rangeEndUtc, DateTimeOffset asOfUtc, TimeZoneInfo timeZone)
    {
        foreach (var interval in session.Intervals)
        {
            var intervalEnd = interval.EndedAtUtc ?? (rangeEndUtc < asOfUtc ? rangeEndUtc : asOfUtc);
            var cursor = interval.StartedAtUtc > rangeStartUtc ? interval.StartedAtUtc : rangeStartUtc;
            var end = intervalEnd < rangeEndUtc ? intervalEnd : rangeEndUtc;
            while (cursor < end)
            {
                var localCursor = TimeZoneInfo.ConvertTime(cursor, timeZone);
                var nextLocalMidnight = DateTime.SpecifyKind(localCursor.Date.AddDays(1), DateTimeKind.Unspecified);
                var nextBoundary = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(nextLocalMidnight, timeZone));
                var partEnd = end < nextBoundary ? end : nextBoundary;
                if (partEnd > cursor)
                {
                    yield return new SummaryPart(localCursor.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), localCursor.ToString("ddd, MMM d", CultureInfo.InvariantCulture), cursor, partEnd, (partEnd - cursor).Ticks / TimeSpan.TicksPerMillisecond);
                }

                cursor = partEnd;
            }
        }
    }

    private sealed class SummaryAccumulator(string label)
    {
        public string Label { get; } = label;
        public long AttributedMilliseconds { get; set; }
        public int SessionCount { get; set; }
        public List<(DateTimeOffset Start, DateTimeOffset End)> Ranges { get; } = [];
    }

    private sealed record SummaryPart(string DayKey, string DayLabel, DateTimeOffset Start, DateTimeOffset End, long DurationMilliseconds);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _store.DisposeAsync();
    }
}
