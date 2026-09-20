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
                command.CommandText = "DROP TABLE exact_operation_receipts; DROP TABLE journal_entry_tags; DROP TABLE journal_entries; DROP TABLE journals; DROP TABLE habit_check_ins; DROP TABLE habits; DELETE FROM schema_migrations WHERE sequence>=7; DROP INDEX tasks_open_due_date; DROP INDEX tasks_starred; DROP INDEX projects_starred;";
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
                var reminderAt = LocalCivilTime.Resolve(new DateOnly(2026, 11, 1),
                    new TimeOnly(1, 30), "America/Chicago", AmbiguousTimeChoice.Later);
                var reminderRequest = Request(dateOnly.Revision);
                var reminderUpdate =
                    new TaskUpdate(dateOnly.Title, "", Priority.None, null, null, false,
                        reminderAt, "America/Chicago", ChangeTimedDue: true,
                        ReminderLocalDate: new DateOnly(2026, 11, 1),
                        ReminderLocalTime: new TimeOnly(1, 30),
                        ReminderFoldChoice: AmbiguousTimeChoice.Later,
                        ReminderDeliveryPolicy: ReminderDeliveryPolicy.Inexact);
                var reminded = await backend.UpdateTaskAsync(timed.Id, reminderUpdate,
                    reminderRequest);
                Assert.Equal(reminded, await backend.UpdateTaskAsync(timed.Id, reminderUpdate,
                    reminderRequest));
                Assert.Null(reminded.DueDate);
                Assert.Equal(reminderAt, reminded.DueAtUtc);
                Assert.Equal("America/Chicago", reminded.DueTimeZone);
                Assert.NotEqual(Guid.Empty, reminded.ReminderId);
                Assert.Equal(new DateOnly(2026, 11, 1), reminded.ReminderLocalDate);
                Assert.Equal(new TimeOnly(1, 30), reminded.ReminderLocalTime);
                Assert.Equal(AmbiguousTimeChoice.Later, reminded.ReminderFoldChoice);
                Assert.Equal(ReminderDeliveryPolicy.Inexact, reminded.ReminderDeliveryPolicy);
                var preserved = await backend.UpdateTaskAsync(timed.Id,
                    new TaskUpdate("Reminder retained", "", Priority.None, null, null, false),
                    Request(reminded.Revision));
                Assert.Equal(reminded.ReminderId, preserved.ReminderId);
                Assert.Equal(reminded.DueAtUtc, preserved.DueAtUtc);
                var cleared = await backend.UpdateTaskAsync(timed.Id,
                    new TaskUpdate(preserved.Title, "", Priority.None, null, null, false,
                        ChangeTimedDue: true), Request(preserved.Revision));
                Assert.Null(cleared.DueAtUtc);
                Assert.Null(cleared.DueTimeZone);
                await Assert.ThrowsAsync<SnookException>(() => backend.UpdateTaskAsync(timed.Id,
                    new TaskUpdate(cleared.Title, "", Priority.None, null, null, false,
                        reminderAt, null, ChangeTimedDue: true), Request(cleared.Revision)));
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
            Assert.Equal(11L, Convert.ToInt64(await query.ExecuteScalarAsync(), CultureInfo.InvariantCulture));
            query.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='tasks_open_due_date';";
            Assert.Equal(1L, Convert.ToInt64(await query.ExecuteScalarAsync(), CultureInfo.InvariantCulture));
            query.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='tasks_open_due_at';";
            Assert.Equal(1L, Convert.ToInt64(await query.ExecuteScalarAsync(), CultureInfo.InvariantCulture));
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public async Task SequenceTenWorkspaceWithoutTimedColumnsUpgradesThroughMigrationEleven()
    {
        var directory = Directory.CreateTempSubdirectory("snook-reminder-migration-");
        var path = Path.Combine(directory.FullName, "workspace.db");
        try
        {
            Guid taskId;
            await using (var backend = new SnookBackend(new SqliteStore(path)))
            {
                await backend.InitializeAsync();
                var initial = await backend.GetBootstrapAsync();
                taskId = (await backend.CreateTaskAsync(initial.Projects[0].Id, "Legacy task",
                    dueDate: new DateOnly(2027, 1, 2))).Id;
            }
            await using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    PRAGMA foreign_keys=OFF;
                    CREATE TABLE tasks_legacy(id TEXT PRIMARY KEY, project_id TEXT NOT NULL REFERENCES projects(id), title TEXT NOT NULL CHECK(length(title) BETWEEN 1 AND 300), description TEXT NOT NULL DEFAULT '', priority INTEGER NOT NULL CHECK(priority BETWEEN 0 AND 4), status TEXT NOT NULL CHECK(status IN ('open','completed')), due_date TEXT, default_activity_id TEXT REFERENCES activities(id), starred INTEGER NOT NULL DEFAULT 0 CHECK(starred IN (0,1)), completed_at_utc_ms INTEGER, archived_at_utc_ms INTEGER, deleted_at_utc_ms INTEGER, revision INTEGER NOT NULL CHECK(revision > 0), created_at_utc_ms INTEGER NOT NULL, updated_at_utc_ms INTEGER NOT NULL);
                    INSERT INTO tasks_legacy(id,project_id,title,description,priority,status,due_date,default_activity_id,starred,completed_at_utc_ms,archived_at_utc_ms,deleted_at_utc_ms,revision,created_at_utc_ms,updated_at_utc_ms)
                        SELECT id,project_id,title,description,priority,status,due_date,default_activity_id,starred,completed_at_utc_ms,archived_at_utc_ms,deleted_at_utc_ms,revision,created_at_utc_ms,updated_at_utc_ms FROM tasks;
                    DROP TABLE tasks;
                    ALTER TABLE tasks_legacy RENAME TO tasks;
                    CREATE INDEX ix_tasks_project_status ON tasks(project_id,status,updated_at_utc_ms);
                    CREATE INDEX tasks_open_due_date ON tasks(due_date,project_id) WHERE due_date IS NOT NULL AND status='open' AND archived_at_utc_ms IS NULL AND deleted_at_utc_ms IS NULL;
                    CREATE INDEX tasks_starred ON tasks(project_id) WHERE starred=1;
                    DELETE FROM schema_migrations WHERE sequence=11;
                    PRAGMA foreign_keys=ON;
                    """;
                await command.ExecuteNonQueryAsync();
            }
            await using (var backend = new SnookBackend(new SqliteStore(path)))
            {
                await backend.InitializeAsync();
                var task = (await backend.GetTaskDetailsAsync(taskId)).Task;
                Assert.Equal(new DateOnly(2027, 1, 2), task.DueDate);
                Assert.Null(task.DueAtUtc);
            }
            await using var verify = new SqliteConnection($"Data Source={path};Pooling=False");
            await verify.OpenAsync();
            await using var query = verify.CreateCommand();
            query.CommandText = "SELECT MAX(sequence) FROM schema_migrations;";
            Assert.Equal(11L, Convert.ToInt64(await query.ExecuteScalarAsync(), CultureInfo.InvariantCulture));
            query.CommandText = "SELECT COUNT(*) FROM pragma_table_info('tasks') WHERE name IN ('due_at_utc_ms','due_time_zone','reminder_id','reminder_local_date','reminder_local_time','reminder_fold','reminder_delivery');";
            Assert.Equal(7L, Convert.ToInt64(await query.ExecuteScalarAsync(), CultureInfo.InvariantCulture));
        }
        finally { directory.Delete(true); }
    }

    private static OperationRequest Request(long? revision = null) => new(Guid.NewGuid(), Guid.NewGuid(), revision);
}
