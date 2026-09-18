using System.Globalization;
using Microsoft.Data.Sqlite;
using Snook.Application;
using Snook.Contracts;
using Snook.Domain;
using Snook.Persistence.Sqlite;
using Xunit;

namespace Snook.Application.Tests;

public sealed class TaskBatchTests
{
    [Fact]
    public async Task EmbeddedBatchContract()
    {
        var directory = Directory.CreateTempSubdirectory("snook-batch-");
        try
        {
            await using var backend = new SnookBackend(new SqliteStore(Path.Combine(directory.FullName, "workspace.db")));
            await backend.InitializeAsync();
            await AssertBatchContractAsync(backend);
        }
        finally { directory.Delete(true); }
    }

    internal static async Task AssertBatchContractAsync(IBackendClient backend)
    {
        var initial = await backend.GetBootstrapAsync();
        var project = initial.Projects[0];
        var destination = await backend.CreateProjectAsync(project.BoardId, "Batch destination");
        var first = await backend.CreateTaskAsync(project.Id, "First batch task", Priority.Low);
        var second = await backend.CreateTaskAsync(project.Id, "Second batch task", Priority.Urgent);
        await backend.AddTaskTagAsync(first.Id, "remove me");
        await backend.AddTaskTagAsync(first.Id, "keep me");
        var revisions = new[] { new TaskRevision(first.Id, first.Revision), new TaskRevision(second.Id, second.Revision) };
        var due = new DateOnly(2026, 11, 1); // A DST transition remains a civil date.
        var request = Request();
        var patch = new BulkTaskUpdate(Description: "Shared description", Priority: Priority.High,
            ChangeDueDate: true, DueDate: due, ChangeActivity: true, DefaultActivityId: initial.Activities[0].Id,
            Starred: true, ProjectId: destination.Id, TagsToAdd: ["release"], TagsToRemove: ["remove me"]);
        var result = await backend.BulkUpdateTasksAsync(revisions, patch, request);
        Assert.Equal(2, result.Count);
        Assert.Equal(first.Title, result[0].Title);
        Assert.Equal(second.Title, result[1].Title);
        Assert.All(result, task =>
        {
            Assert.Equal(2, task.Revision);
            Assert.Equal(due, task.DueDate);
            Assert.Equal(Priority.High, task.Priority);
            Assert.Equal(destination.Id, task.ProjectId);
            Assert.Equal("Shared description", task.Description);
            Assert.Equal(initial.Activities[0].Id, task.DefaultActivityId);
            Assert.True(task.Starred);
            Assert.Null(task.DueAtUtc);
        });
        var details = await backend.GetTaskDetailsAsync(first.Id);
        Assert.Contains(details.Tags, tag => tag.DisplayName == "keep me");
        Assert.Contains(details.Tags, tag => tag.DisplayName == "release");
        Assert.DoesNotContain(details.Tags, tag => tag.DisplayName == "remove me");
        var range = new CalendarRangeQuery(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero));
        Assert.DoesNotContain(await backend.GetCalendarRangeAsync(range), block => block.TaskId == first.Id || block.TaskId == second.Id);
        var planned = await backend.CreateScheduleBlockAsync(initial.Calendars[0].Id, first.Id, null, null,
            range.RangeStartUtc, range.RangeStartUtc.AddHours(1), "UTC");

        // A stale second task rolls back the first task, tags, receipt, and change log.
        var cursor = (await backend.GetBootstrapAsync()).CommittedCursor;
        var conflict = await Assert.ThrowsAsync<SnookException>(() => backend.BulkUpdateTasksAsync(
            [new(first.Id, result[0].Revision), new(second.Id, 1)],
            new BulkTaskUpdate(Title: "Must roll back", TagsToAdd: ["rolled back"]), Request()));
        Assert.Equal(SnookErrorCode.RevisionConflict, conflict.Code);
        Assert.Equal(cursor, (await backend.GetBootstrapAsync()).CommittedCursor);
        Assert.Equal(first.Title, (await backend.GetTaskDetailsAsync(first.Id)).Task.Title);
        Assert.DoesNotContain((await backend.GetTaskDetailsAsync(first.Id)).Tags, tag => tag.DisplayName == "rolled back");

        var changed = await backend.BulkUpdateTasksAsync(result.Select(task => new TaskRevision(task.Id, task.Revision)).ToArray(),
            new BulkTaskUpdate(Status: TaskState.Completed, ChangeDueDate: true, ChangeActivity: true, Starred: false), Request());
        Assert.All(changed, task =>
        {
            Assert.Null(task.DueDate);
            Assert.Null(task.DefaultActivityId);
            Assert.False(task.Starred);
            Assert.Equal(TaskState.Completed, task.Status);
            Assert.NotNull(task.CompletedAtUtc);
            Assert.Equal("Shared description", task.Description);
        });
        Assert.Contains(await backend.GetCalendarRangeAsync(range), block => block.Id == planned.Id);
        cursor = (await backend.GetBootstrapAsync()).CommittedCursor;
        Assert.Equal(result, await backend.BulkUpdateTasksAsync(revisions, patch, request));
        Assert.Equal(cursor, (await backend.GetBootstrapAsync()).CommittedCursor);
        Assert.Equal(changed[0], (await backend.GetTaskDetailsAsync(first.Id)).Task);

        var archived = await backend.BulkUpdateTasksAsync(changed.Select(task => new TaskRevision(task.Id, task.Revision)).ToArray(),
            new BulkTaskUpdate(Archived: true), Request());
        Assert.All(archived, task => Assert.NotNull(task.ArchivedAtUtc));
        var restored = await backend.BulkUpdateTasksAsync(archived.Select(task => new TaskRevision(task.Id, task.Revision)).ToArray(),
            new BulkTaskUpdate(Archived: false, Status: TaskState.Open), Request());
        Assert.All(restored, task => { Assert.Null(task.ArchivedAtUtc); Assert.Null(task.CompletedAtUtc); });

        var invalid = await Assert.ThrowsAsync<SnookException>(() => backend.BulkUpdateTasksAsync([], patch, Request()));
        Assert.Equal(SnookErrorCode.ValidationFailed, invalid.Code);
        await Assert.ThrowsAsync<SnookException>(() => backend.BulkUpdateTasksAsync([new(first.Id, restored[0].Revision), new(first.Id, restored[0].Revision)], patch, Request()));
        await Assert.ThrowsAsync<SnookException>(() => backend.BulkUpdateTasksAsync([new(first.Id, restored[0].Revision)], new BulkTaskUpdate(), Request()));
        await Assert.ThrowsAsync<SnookException>(() => backend.BulkUpdateTasksAsync(
            Enumerable.Range(0, 501).Select(_ => new TaskRevision(Guid.NewGuid(), 1)).ToArray(), patch, Request()));
        await Assert.ThrowsAsync<SnookException>(() => backend.BulkUpdateTasksAsync([new(first.Id, restored[0].Revision)], new BulkTaskUpdate(Priority: (Priority)99), Request()));

        var starredProject = await backend.UpdateProjectAsync(destination.Id, new ProjectUpdate(destination.Name, "Favorite project", true), Request(destination.Revision));
        Assert.True((await backend.GetProjectDetailsAsync(destination.Id)).Project.Starred);
        await backend.UpdateProjectAsync(destination.Id, new ProjectUpdate(destination.Name, "Favorite project", false), Request(starredProject.Revision));
        Assert.False((await backend.GetProjectDetailsAsync(destination.Id)).Project.Starred);

        await backend.AddTaskDependencyAsync(first.Id, second.Id);
        var blocked = await Assert.ThrowsAsync<SnookException>(() => backend.BulkUpdateTasksAsync([new(first.Id, restored[0].Revision)],
            new BulkTaskUpdate(Status: TaskState.Completed), Request()));
        Assert.Equal(SnookErrorCode.DependencyBlocked, blocked.Code);
        Assert.Equal(TaskState.Open, (await backend.GetTaskDetailsAsync(first.Id)).Task.Status);
        var completedTogether = await backend.BulkUpdateTasksAsync([new(first.Id, restored[0].Revision), new(second.Id, restored[1].Revision)],
            new BulkTaskUpdate(Status: TaskState.Completed), Request());
        Assert.All(completedTogether, task => Assert.Equal(TaskState.Completed, task.Status));
    }

    [Fact]
    public async Task DueMigrationPreservesDatesSchedulesAndDurableBatchReplay()
    {
        var directory = Directory.CreateTempSubdirectory("snook-due-migration-");
        var path = Path.Combine(directory.FullName, "workspace.db");
        TaskItem original;
        TaskItem timed;
        ScheduleBlock schedule;
        try
        {
            await using (var backend = new SnookBackend(new SqliteStore(path)))
            {
                await backend.InitializeAsync();
                var initial = await backend.GetBootstrapAsync();
                original = await backend.CreateTaskAsync(initial.Projects[0].Id, "Preserve due date", dueDate: new DateOnly(2026, 11, 1));
                timed = await backend.CreateTaskAsync(initial.Projects[0].Id, "Legacy timed deadline");
                schedule = await backend.CreateScheduleBlockAsync(initial.Calendars[0].Id, original.Id, null, null,
                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1), "UTC");
            }
            await using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "DROP TABLE journal_entry_tags; DROP TABLE journal_entries; DROP TABLE journals; DROP TABLE habit_check_ins; DROP TABLE habits; DELETE FROM schema_migrations WHERE sequence>=7; DROP INDEX tasks_open_due_date; DROP INDEX tasks_starred; DROP INDEX projects_starred;";
                await command.ExecuteNonQueryAsync();
                command.CommandText = "UPDATE tasks SET due_at_utc_ms=1793538000000,due_time_zone='America/Chicago' WHERE id=$id;";
                command.Parameters.AddWithValue("$id", timed.Id.ToString("D"));
                await command.ExecuteNonQueryAsync();
            }
            var request = Request();
            TaskItem updated;
            await using (var backend = new SnookBackend(new SqliteStore(path)))
            {
                await backend.InitializeAsync();
                Assert.Equal(original, (await backend.GetTaskDetailsAsync(original.Id)).Task);
                var unchanged = await backend.UpdateTaskAsync(timed.Id, new TaskUpdate("Renamed legacy task", "", Priority.None, null, null, false), Request(timed.Revision));
                Assert.NotNull(unchanged.DueAtUtc);
                Assert.Equal("America/Chicago", unchanged.DueTimeZone);
                var dateOnly = await backend.UpdateTaskAsync(timed.Id, new TaskUpdate(unchanged.Title, "", Priority.None, original.DueDate, null, false), Request(unchanged.Revision));
                Assert.Equal(original.DueDate, dateOnly.DueDate);
                Assert.Null(dateOnly.DueAtUtc);
                Assert.Null(dateOnly.DueTimeZone);
                Assert.Contains(await backend.GetCalendarRangeAsync(new CalendarRangeQuery(schedule.StartAtUtc, schedule.EndAtUtc)), block => block.Id == schedule.Id);
                updated = Assert.Single(await backend.BulkUpdateTasksAsync([new(original.Id, original.Revision)], new BulkTaskUpdate(Starred: true), request));
            }
            await using (var backend = new SnookBackend(new SqliteStore(path)))
            {
                await backend.InitializeAsync();
                Assert.Equal(updated, Assert.Single(await backend.BulkUpdateTasksAsync([new(original.Id, original.Revision)], new BulkTaskUpdate(Starred: true), request)));
            }
            await using var verification = new SqliteConnection($"Data Source={path};Pooling=False");
            await verification.OpenAsync();
            await using var query = verification.CreateCommand();
            query.CommandText = "SELECT MAX(sequence) FROM schema_migrations;";
            Assert.Equal(9L, Convert.ToInt64(await query.ExecuteScalarAsync(), CultureInfo.InvariantCulture));
            query.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='tasks_open_due_date';";
            Assert.Equal(1L, Convert.ToInt64(await query.ExecuteScalarAsync(), CultureInfo.InvariantCulture));
        }
        finally { directory.Delete(true); }
    }

    private static OperationRequest Request(long? revision = null) => new(Guid.NewGuid(), Guid.NewGuid(), revision);
}
