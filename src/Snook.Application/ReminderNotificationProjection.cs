using Snook.Contracts;
using Snook.Domain;

namespace Snook.Application;

public sealed record ReminderDescriptor(
    Guid ReminderId,
    Guid TaskId,
    long Revision,
    string Title,
    DateTimeOffset DueAtUtc,
    string TimeZone);

public sealed record ReminderNotificationSnapshot(
    int Version,
    Guid WorkspaceId,
    DateTimeOffset CapturedAtUtc,
    DateTimeOffset FreshUntilUtc,
    IReadOnlyList<ReminderDescriptor> Items)
{
    public const int CurrentVersion = 1;
    public const int MaximumItems = 64;

    public static ReminderNotificationSnapshot Build(Guid workspaceId,
        IEnumerable<TaskListItem> tasks, DateTimeOffset capturedAtUtc,
        TimeSpan? freshness = null)
    {
        if (workspaceId == Guid.Empty) throw new ArgumentException("A workspace is required.", nameof(workspaceId));
        if (capturedAtUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("Capture time must be UTC.", nameof(capturedAtUtc));
        ArgumentNullException.ThrowIfNull(tasks);
        var lifetime = freshness ?? TimeSpan.FromHours(24);
        if (lifetime <= TimeSpan.Zero || lifetime > TimeSpan.FromDays(7))
            throw new ArgumentOutOfRangeException(nameof(freshness));
        var items = tasks
            .Where(item => item.Task.Status == TaskState.Open
                && item.Task.ArchivedAtUtc is null && item.Task.DeletedAtUtc is null
                && item.Task.DueAtUtc is not null && !string.IsNullOrWhiteSpace(item.Task.DueTimeZone)
                && item.Task.ReminderId is not null
                && item.Task.ReminderLocalDate is not null && item.Task.ReminderLocalTime is not null
                && item.Task.ReminderFoldChoice is not null
                && item.Task.ReminderDeliveryPolicy == ReminderDeliveryPolicy.Inexact)
            .OrderBy(item => item.Task.DueAtUtc)
            .ThenBy(item => item.Task.Id)
            .Take(MaximumItems)
            .Select(item => new ReminderDescriptor(item.Task.ReminderId!.Value, item.Task.Id, item.Task.Revision,
                item.Task.Title, item.Task.DueAtUtc!.Value, item.Task.DueTimeZone!))
            .ToArray();
        return new(CurrentVersion, workspaceId, capturedAtUtc, capturedAtUtc + lifetime, items);
    }
}
