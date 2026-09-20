using Snook.Application;
using Snook.Contracts;
using Snook.Domain;
using Xunit;

namespace Snook.Application.Tests;

public sealed class ReminderNotificationProjectionTests
{
    [Fact]
    public void ProjectionIsBoundedOrderedAndOmitsPrivateDescription()
    {
        var now = new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
        var workspace = Guid.NewGuid();
        var tasks = Enumerable.Range(0, 70).Select(index => Item(index, now.AddMinutes(70 - index))).ToArray();
        var snapshot = ReminderNotificationSnapshot.Build(workspace, tasks, now);
        Assert.Equal(ReminderNotificationSnapshot.MaximumItems, snapshot.Items.Count);
        Assert.Equal(tasks[^1].Task.Id, snapshot.Items[0].TaskId);
        Assert.Equal(now.AddHours(24), snapshot.FreshUntilUtc);
        Assert.DoesNotContain("private", System.Text.Json.JsonSerializer.Serialize(snapshot), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProjectionExcludesCompletedArchivedDeletedAndDateOnlyTasks()
    {
        var now = DateTimeOffset.UtcNow;
        var included = Item(1, now.AddHours(1));
        var candidates = new[]
        {
            included,
            Item(2, now.AddHours(2)) with { Task = Item(2, now.AddHours(2)).Task with { Status = TaskState.Completed, CompletedAtUtc = now } },
            Item(3, now.AddHours(3)) with { Task = Item(3, now.AddHours(3)).Task with { ArchivedAtUtc = now } },
            Item(4, now.AddHours(4)) with { Task = Item(4, now.AddHours(4)).Task with { DeletedAtUtc = now } },
            Item(5, now.AddHours(5)) with { Task = Item(5, now.AddHours(5)).Task with { DueAtUtc = null, DueTimeZone = null, DueDate = DateOnly.FromDateTime(now.Date) } }
        };
        Assert.Equal(included.Task.Id, Assert.Single(ReminderNotificationSnapshot.Build(Guid.NewGuid(), candidates, now).Items).TaskId);
    }

    private static TaskListItem Item(int index, DateTimeOffset due) => new(
        new TaskItem(Guid.NewGuid(), Guid.NewGuid(), $"Task {index}", "private description",
            Priority.None, TaskState.Open, null, due, "America/Chicago", null, false,
            null, null, null, index + 1, Guid.NewGuid(), DateOnly.FromDateTime(due.Date),
            TimeOnly.FromDateTime(due.DateTime), AmbiguousTimeChoice.Earlier,
            ReminderDeliveryPolicy.Inexact), "Project", "Board", 0, false);
}
