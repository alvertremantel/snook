namespace Snook.Domain;

public enum SessionLane
{
    Foreground,
    Background
}

public enum ReorderDirection
{
    Earlier,
    Later
}

public enum SessionState
{
    Running,
    Paused,
    Stopped,
    RecoveryRequired
}

public enum RecoveryDecision
{
    StopAtLastKnown,
    StopNow,
    Continue
}

public enum TaskState
{
    Open,
    Completed
}

public enum Priority
{
    None = 0,
    Low = 1,
    Medium = 2,
    High = 3,
    Urgent = 4
}

public sealed record Workspace(
    Guid Id,
    string Name,
    DateTimeOffset CreatedAtUtc,
    long Revision);

public sealed record WorkspaceSettings(
    bool AllowConcurrentForeground,
    long Revision);

public sealed record ActivityGroup(
    Guid Id,
    Guid WorkspaceId,
    string Name,
    decimal SortKey,
    DateTimeOffset? DeletedAtUtc,
    long Revision);

public sealed record Board(
    Guid Id,
    Guid WorkspaceId,
    string Name,
    decimal SortKey,
    DateTimeOffset? ArchivedAtUtc,
    DateTimeOffset? DeletedAtUtc,
    long Revision);

public sealed record Project(
    Guid Id,
    Guid BoardId,
    string Name,
    string Description,
    bool Starred,
    decimal SortKey,
    DateTimeOffset? ArchivedAtUtc,
    DateTimeOffset? DeletedAtUtc,
    long Revision);

public sealed record TaskItem(
    Guid Id,
    Guid ProjectId,
    string Title,
    string Description,
    Priority Priority,
    TaskState Status,
    DateOnly? DueDate,
    DateTimeOffset? DueAtUtc,
    string? DueTimeZone,
    Guid? DefaultActivityId,
    bool Starred,
    DateTimeOffset? CompletedAtUtc,
    DateTimeOffset? ArchivedAtUtc,
    DateTimeOffset? DeletedAtUtc,
    long Revision);

public sealed record Activity(
    Guid Id,
    Guid WorkspaceId,
    string Name,
    string Description,
    SessionLane DefaultLane,
    DateTimeOffset? ArchivedAtUtc,
    DateTimeOffset? DeletedAtUtc,
    long Revision,
    Guid? GroupId = null);

public sealed record TimeInterval(
    Guid Id,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? EndedAtUtc,
    string Source);

public sealed record TrackingSession(
    Guid Id,
    Guid WorkspaceId,
    Guid? TaskId,
    Guid? ActivityId,
    SessionLane Lane,
    SessionState State,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? StoppedAtUtc,
    string Notes,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    long Revision,
    IReadOnlyList<TimeInterval> Intervals,
    string? RecoveryStatus = null,
    string? RecoveryReason = null);

public sealed record SessionCorrection(
    Guid? TaskId,
    Guid? ActivityId,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? StoppedAtUtc,
    string Notes,
    IReadOnlyList<TimeInterval> Intervals,
    string Reason);

public sealed record TrackingCorrection(
    Guid Id,
    Guid SessionId,
    Guid OperationId,
    string Reason,
    TrackingSession Before,
    TrackingSession After,
    DateTimeOffset AppliedAtUtc);

public sealed record TaskLink(
    Guid Id,
    Guid TaskId,
    string? Label,
    string Uri,
    string Kind);

public sealed record Tag(
    Guid Id,
    Guid WorkspaceId,
    string NormalizedName,
    string DisplayName,
    string? Color,
    long Revision);

public sealed record TaskDetails(
    TaskItem Task,
    IReadOnlyList<Tag> Tags,
    IReadOnlyList<TaskLink> Links,
    IReadOnlyList<Guid> PrerequisiteTaskIds,
    long TrackedMilliseconds = 0,
    long ActiveMilliseconds = 0);

public sealed record ProjectDetails(
    Project Project,
    IReadOnlyList<TaskItem> Tasks,
    long TrackedMilliseconds,
    long ActiveMilliseconds);

public sealed record BoardUpdate(string Name);
public sealed record ProjectUpdate(string Name, string Description, bool Starred);
public sealed record ActivityUpdate(string Name, string Description, SessionLane DefaultLane, Guid? GroupId = null);
public sealed record ActivityGroupUpdate(string Name);

public sealed record TaskUpdate(
    string Title,
    string Description,
    Priority Priority,
    DateOnly? DueDate,
    Guid? DefaultActivityId,
    bool Starred);

public sealed record TaskRevision(Guid TaskId, long ExpectedRevision);

/// <summary>Only supplied fields change. Explicit flags distinguish clearing nullable fields from keeping them.</summary>
public sealed record BulkTaskUpdate(
    string? Title = null,
    string? Description = null,
    Priority? Priority = null,
    TaskState? Status = null,
    bool ChangeDueDate = false,
    DateOnly? DueDate = null,
    bool ChangeActivity = false,
    Guid? DefaultActivityId = null,
    bool? Starred = null,
    Guid? ProjectId = null,
    bool? Archived = null,
    IReadOnlyList<string>? TagsToAdd = null,
    IReadOnlyList<string>? TagsToRemove = null);

public sealed record Calendar(
    Guid Id,
    Guid WorkspaceId,
    string Name,
    string Color,
    bool Visible,
    long Revision,
    DateTimeOffset? DeletedAtUtc = null);

public sealed record CalendarUpdate(
    string Name,
    string Color,
    bool Visible);

public sealed record CalendarEvent(
    Guid Id,
    Guid CalendarId,
    string Title,
    string Description,
    string? Location,
    string Color,
    DateTimeOffset StartAtUtc,
    DateTimeOffset EndAtUtc,
    bool AllDay,
    string TimeZone,
    string? RecurrenceRule,
    DateTimeOffset? RecurrenceEndUtc,
    DateTimeOffset? DeletedAtUtc,
    long Revision);

public sealed record CalendarEventUpdate(
    string Title,
    string Description,
    string? Location,
    string Color,
    DateTimeOffset StartAtUtc,
    DateTimeOffset EndAtUtc,
    bool AllDay,
    string TimeZone,
    string? RecurrenceRule,
    DateTimeOffset? RecurrenceEndUtc);

public sealed record CalendarEventOccurrenceOverride(
    Guid Id,
    Guid EventId,
    DateTimeOffset OriginalStartAtUtc,
    DateTimeOffset? NewStartAtUtc,
    DateTimeOffset? NewEndAtUtc,
    string? TitleOverride,
    bool Cancelled,
    long Revision);

public sealed record ScheduleBlock(
    Guid Id,
    Guid CalendarId,
    Guid? TaskId,
    Guid? ActivityId,
    string? TitleOverride,
    DateTimeOffset StartAtUtc,
    DateTimeOffset EndAtUtc,
    string TimeZone,
    string? RecurrenceRule,
    DateTimeOffset? RecurrenceEndUtc,
    DateTimeOffset? DeletedAtUtc,
    long Revision);

public sealed record ScheduleBlockUpdate(
    DateTimeOffset StartAtUtc,
    DateTimeOffset EndAtUtc,
    string TimeZone,
    string? TitleOverride,
    string? RecurrenceRule,
    DateTimeOffset? RecurrenceEndUtc);

public sealed record HistoryItem(
    TrackingSession Session,
    string? TaskTitle,
    string? ProjectName,
    string? ActivityName,
    long AttributedMilliseconds);

public sealed record SummaryBucket(
    string Key,
    string Label,
    long AttributedMilliseconds,
    long CoverageMilliseconds,
    int SessionCount);

public enum SummaryGrouping
{
    Day,
    Task,
    Project,
    Activity,
    ActivityGroup,
    Tag,
    Lane
}

public static class TimeMath
{
    public static (DateTimeOffset Start, DateTimeOffset End) LocalDayRangeUtc(DateTimeOffset instant, TimeZoneInfo zone)
    {
        var date = TimeZoneInfo.ConvertTime(instant, zone).Date;
        var next = date.AddDays(1);
        return (new DateTimeOffset(date, zone.GetUtcOffset(date)).ToUniversalTime(),
            new DateTimeOffset(next, zone.GetUtcOffset(next)).ToUniversalTime());
    }

    public static long DurationMilliseconds(
        IEnumerable<TimeInterval> intervals,
        DateTimeOffset asOfUtc)
    {
        return intervals.Sum(interval =>
        {
            var end = interval.EndedAtUtc ?? asOfUtc;
            var duration = end - interval.StartedAtUtc;
            return Math.Max(0, duration.Ticks / TimeSpan.TicksPerMillisecond);
        });
    }

    public static long OverlapMilliseconds(
        TimeInterval interval,
        DateTimeOffset rangeStartUtc,
        DateTimeOffset rangeEndUtc,
        DateTimeOffset asOfUtc)
    {
        var end = interval.EndedAtUtc ?? asOfUtc;
        var start = interval.StartedAtUtc > rangeStartUtc ? interval.StartedAtUtc : rangeStartUtc;
        var finish = end < rangeEndUtc ? end : rangeEndUtc;
        return finish <= start ? 0 : (finish - start).Ticks / TimeSpan.TicksPerMillisecond;
    }

    public static long UnionMilliseconds(
        IEnumerable<(DateTimeOffset Start, DateTimeOffset End)> ranges)
    {
        var ordered = ranges
            .Where(range => range.End > range.Start)
            .OrderBy(range => range.Start)
            .ToArray();

        if (ordered.Length == 0)
        {
            return 0;
        }

        var total = TimeSpan.Zero;
        var currentStart = ordered[0].Start;
        var currentEnd = ordered[0].End;
        foreach (var range in ordered.Skip(1))
        {
            if (range.Start <= currentEnd)
            {
                if (range.End > currentEnd)
                {
                    currentEnd = range.End;
                }

                continue;
            }

            total += currentEnd - currentStart;
            currentStart = range.Start;
            currentEnd = range.End;
        }

        total += currentEnd - currentStart;
        return total.Ticks / TimeSpan.TicksPerMillisecond;
    }
}

public static class Guard
{
    public static string Required(string value, string fieldName, int maxLength = 500)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, fieldName);
        if (value.Length > maxLength)
        {
            throw new ArgumentOutOfRangeException(fieldName, $"{fieldName} cannot exceed {maxLength} characters.");
        }

        return value.Trim();
    }

    public static string Optional(string? value, string fieldName, int maxLength = 10_000)
    {
        var result = value?.Trim() ?? string.Empty;
        if (result.Length > maxLength)
        {
            throw new ArgumentOutOfRangeException(fieldName, $"{fieldName} cannot exceed {maxLength} characters.");
        }

        return result;
    }
}
