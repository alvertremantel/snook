using Snook.Application;
using Snook.Contracts;
using Snook.Domain;
using Xunit;

namespace Snook.Application.Tests;

public sealed class WorkspaceWidgetProjectionTests
{
    [Fact]
    public void ProjectionIsBoundedDeterministicAndExcludesInactiveItems()
    {
        var now = new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
        var workspace = Guid.NewGuid();
        var active = Task("  Make progress  ", TaskState.Open);
        var completed = Task("Already done", TaskState.Completed);
        var deleted = Task("Deleted", TaskState.Open) with { DeletedAtUtc = now };
        var today = new TodaySnapshot(now,
            [List(active), List(completed), List(deleted), List(Task(new string('x', 100), TaskState.Open))],
            [], 0, 9);
        var firstHabit = Habit("zeta", now, completed: false, streak: -2);
        var secondHabit = Habit("Alpha", now, completed: true, streak: 4);
        var archivedHabit = Habit("Archived", now, completed: true, streak: 8) with
        {
            Habit = Habit("Archived", now, completed: true, streak: 8).Habit with { ArchivedAtUtc = now }
        };
        var snapshot = new BootstrapSnapshot(new Workspace(workspace, "Workspace", now, 1),
            [], [], [], [], today, 1,
            new HostCapabilities("Embedded", true, false, true, true, false, false));

        var result = WorkspaceWidgetProjection.Build(snapshot,
            [firstHabit, archivedHabit, secondHabit], maximumItems: 2);

        Assert.Equal(workspace, result.WorkspaceId);
        Assert.Equal(now, result.CapturedAtUtc);
        Assert.Equal(9, result.OpenTaskCount);
        Assert.Collection(result.NextTasks,
            item => Assert.Equal("Make progress", item.Title),
            item =>
            {
                Assert.EndsWith("…", item.Title, StringComparison.Ordinal);
                Assert.Equal(81, new System.Globalization.StringInfo(item.Title).LengthInTextElements);
            });
        Assert.Collection(result.Habits,
            item =>
            {
                Assert.Equal("Alpha", item.Name);
                Assert.True(item.CompletedToday);
                Assert.Equal(4, item.CurrentStreak);
            },
            item =>
            {
                Assert.Equal("zeta", item.Name);
                Assert.False(item.CompletedToday);
                Assert.Equal(0, item.CurrentStreak);
            });
    }

    [Fact]
    public void ProjectionValidatesBoundsAndUsesPrivacySafeFallbacks()
    {
        var now = DateTimeOffset.UnixEpoch;
        var workspace = Guid.NewGuid();
        var today = new TodaySnapshot(now, [new TaskListItem(Task(" ", TaskState.Open), " ", " ", 0, false)], [], 0, -1);
        var snapshot = new BootstrapSnapshot(new Workspace(workspace, "Workspace", now, 1),
            [], [], [], [], today, 1,
            new HostCapabilities("Embedded", true, false, true, true, false, false));

        var result = WorkspaceWidgetProjection.Build(snapshot);

        Assert.Equal("Untitled", Assert.Single(result.NextTasks).Title);
        Assert.Equal("Untitled", Assert.Single(result.NextTasks).Context);
        Assert.Equal(0, result.OpenTaskCount);
        Assert.Throws<ArgumentOutOfRangeException>(() => WorkspaceWidgetProjection.Build(snapshot, maximumItems: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => WorkspaceWidgetProjection.Build(snapshot, maximumItems: 7));
    }

    private static TaskItem Task(string title, TaskState state) => new(Guid.NewGuid(), Guid.NewGuid(),
        title, "private description", Priority.High, state, null, null, null, null, false,
        state == TaskState.Completed ? DateTimeOffset.UnixEpoch : null, null, null, 1);

    private static TaskListItem List(TaskItem task) => new(task, "Project", "Board", 0, false);

    private static HabitProgress Habit(string name, DateTimeOffset now, bool completed, int streak)
    {
        var habit = new Habit(Guid.NewGuid(), name, "private description", DateOnly.FromDateTime(now.Date),
            "UTC", now, null, null, 1);
        var today = DateOnly.FromDateTime(now.Date);
        return new HabitProgress(habit, today, today, today,
            completed ? [new HabitCheckIn(habit.Id, today, now)] : [], streak, 1);
    }
}
