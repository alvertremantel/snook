using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Snook.Contracts;
using Snook.Domain;
using Snook.Persistence.Sqlite;
using Xunit;

namespace Snook.Application.Tests;

public sealed class ExactReceiptTests
{
    [Fact]
    public async Task ExactResultsSurviveLaterWritesDeletionBackupRestoreAndRestart()
    {
        var directory = Directory.CreateTempSubdirectory("snook-exact-");
        try
        {
            var path = Path.Combine(directory.FullName, "workspace.db");
            ReceiptScenario scenario;
            await using (var backend = new SnookBackend(new SqliteStore(path)))
            {
                await backend.InitializeAsync();
                scenario = await ExerciseAsync(backend);
                var backup = await backend.CreateBackupAsync(Path.Combine(directory.FullName, "backup.db"));
                await backend.CreateBoardAsync("After backup");
                await backend.RestoreBackupAsync(backup.Path);
                await scenario.VerifyAsync(backend);
            }
            await using (var backend = new SnookBackend(new SqliteStore(path)))
            {
                await backend.InitializeAsync();
                await scenario.VerifyAsync(backend);
            }
        }
        finally { directory.Delete(true); }
    }

    internal static async Task<ReceiptScenario> ExerciseAsync(IBackendClient backend)
    {
        var scenario = new ReceiptScenario();
        var board = await backend.CreateBoardAsync("Receipt board");
        var boardRequest = Request(board.Revision);
        var boardUpdate = new BoardUpdate("First board result");
        await RejectAsync(() => backend.UpdateBoardAsync(board.Id, boardUpdate, boardRequest with { ClientDeviceId = Guid.Empty }));
        await RejectAsync(() => backend.UpdateBoardAsync(board.Id, boardUpdate, boardRequest with { OperationId = Guid.Empty }));
        board = await scenario.RememberAsync(client => client.UpdateBoardAsync(board.Id, boardUpdate, boardRequest), backend);
        var boardId = board.Id;
        await RejectAsync(() => backend.UpdateBoardAsync(boardId, new BoardUpdate("Changed payload"), boardRequest));
        await RejectAsync(() => backend.UpdateBoardAsync(boardId, boardUpdate, boardRequest with { ExpectedRevision = board.Revision }));

        var project = await backend.CreateProjectAsync(boardId, "Receipt project");
        var projectId = project.Id;
        var projectRequest = Request(project.Revision);
        project = await scenario.RememberAsync(client => client.UpdateProjectAsync(projectId,
            new ProjectUpdate("First project result", "Description", true), projectRequest), backend);
        var group = await backend.CreateActivityGroupAsync("Receipt group");
        var groupId = group.Id;
        var groupRequest = Request(group.Revision);
        group = await scenario.RememberAsync(client => client.UpdateActivityGroupAsync(groupId,
            new ActivityGroupUpdate("First group result"), groupRequest), backend);
        var activity = await backend.CreateActivityAsync("Receipt activity", groupId: groupId);
        var activityId = activity.Id;
        var activityRequest = Request(activity.Revision);
        activity = await scenario.RememberAsync(client => client.UpdateActivityAsync(activityId,
            new ActivityUpdate("First activity result", "Description", SessionLane.Foreground, groupId), activityRequest), backend);
        var otherActivity = await backend.CreateActivityAsync("Later default activity");
        var task = await backend.CreateTaskAsync(projectId, "Receipt task");
        var taskId = task.Id;
        var taskRequest = Request(task.Revision);
        var taskUpdate = new TaskUpdate("First task result", "Original", Priority.High, new DateOnly(2026, 9, 18), activityId, true);
        task = await scenario.RememberAsync(client => client.UpdateTaskAsync(taskId, taskUpdate, taskRequest), backend);

        var start = Request();
        await RejectAsync(() => backend.StartSessionAsync(taskId, null, SessionLane.Foreground, start with { ExpectedRevision = 1 }));
        await RejectAsync(() => backend.StartSessionAsync(taskId, null, (SessionLane)99, start));
        var started = await scenario.RememberAsync(client => client.StartSessionAsync(taskId, null, SessionLane.Foreground, start), backend);
        Assert.Equal(activityId, started.ActivityId);
        var pauseRequest = Request(started.Revision);
        var paused = await scenario.RememberAsync(client => client.PauseSessionAsync(started.Id, pauseRequest), backend);
        var resumeRequest = Request(paused.Revision);
        var resumed = await scenario.RememberAsync(client => client.ResumeSessionAsync(started.Id, resumeRequest), backend);
        var stopRequest = Request(resumed.Revision);
        await scenario.RememberAsync(client => client.StopSessionAsync(started.Id, "Original notes", stopRequest), backend);
        await RejectAsync(() => backend.StopSessionAsync(started.Id, "Different notes", stopRequest));
        // A retry must not resolve the default activity or concurrency policy again.
        task = await backend.UpdateTaskAsync(taskId, taskUpdate with { Title = "Later task", DefaultActivityId = otherActivity.Id }, Request(task.Revision));

        var when = new DateTimeOffset(2026, 9, 18, 12, 0, 0, TimeSpan.Zero).AddTicks(1234);
        var manualRequest = Request();
        var manual = await scenario.RememberAsync(client => client.CreateManualSessionAsync(taskId, activityId,
            when, when.AddMinutes(20), "Manual", manualRequest), backend);
        var correctionRequest = Request(manual.Revision);
        var correction = new SessionCorrection(taskId, activityId, when, when.AddMinutes(25), "Corrected",
            [new TimeInterval(manual.Intervals[0].Id, when, when.AddMinutes(25), "manual")], "Fix duration");
        await scenario.RememberAsync(client => client.CorrectSessionAsync(manual.Id,
            correction, correctionRequest), backend);

        var settings = await backend.GetSettingsAsync();
        var settingsRequest = Request(settings.Revision);
        var firstSettings = settings with { AllowConcurrentForeground = true };
        var savedSettings = await scenario.RememberAsync(client => client.UpdateSettingsAsync(firstSettings, settingsRequest), backend);
        await backend.UpdateSettingsAsync(savedSettings with { AllowConcurrentForeground = false }, Request(savedSettings.Revision));

        var calendar = await backend.CreateCalendarAsync("Receipt calendar");
        var calendarId = calendar.Id;
        var calendarRequest = Request(calendar.Revision);
        calendar = await scenario.RememberAsync(client => client.UpdateCalendarAsync(calendarId,
            new CalendarUpdate("First calendar result", "#123456", true), calendarRequest), backend);
        var calendarEvent = await backend.CreateCalendarEventAsync(calendarId, "Receipt event", when, when.AddHours(1), recurrenceRule: "FREQ=DAILY", recurrenceEndUtc: when.AddDays(3));
        var eventRequest = Request(calendarEvent.Revision);
        var eventId = calendarEvent.Id;
        calendarEvent = await scenario.RememberAsync(client => client.UpdateCalendarEventAsync(eventId,
            new CalendarEventUpdate("First event result", "Description", "Room", "#234567", when, when.AddHours(2), false, "UTC", "FREQ=DAILY", when.AddDays(3)), eventRequest), backend);
        var exceptionRequest = Request();
        var exception = await scenario.RememberAsync(client => client.UpsertCalendarEventExceptionAsync(eventId,
            when, when.AddMinutes(5), when.AddHours(2), "First override", false, exceptionRequest), backend);
        await backend.DeleteCalendarEventExceptionAsync(exception.Id, Request(exception.Revision));
        var block = await backend.CreateScheduleBlockAsync(calendarId, taskId, null, "Receipt plan", when, when.AddHours(1), "UTC");
        var blockId = block.Id;
        var blockRequest = Request(block.Revision);
        block = await scenario.RememberAsync(client => client.UpdateScheduleBlockAsync(blockId,
            new ScheduleBlockUpdate(when.AddHours(1), when.AddHours(2), "UTC", "First plan result", null, null), blockRequest), backend);
        await backend.UpdateScheduleBlockAsync(blockId,
            new ScheduleBlockUpdate(when.AddHours(2), when.AddHours(3), "UTC", "Later plan", null, null), Request(block.Revision));

        TaskRevision[] targets = [new(taskId, task.Revision)];
        var batchRequest = Request();
        var batchUpdate = new BulkTaskUpdate(Title: "First batch result", Starred: false);
        var batch = await scenario.RememberAsync(client => client.BulkUpdateTasksAsync(targets, batchUpdate, batchRequest), backend);
        await RejectAsync(() => backend.BulkUpdateTasksAsync(targets, batchUpdate with { Title = "Changed batch" }, batchRequest));
        task = Assert.Single(batch);
        await backend.DeleteTaskAsync(taskId, Request(task.Revision));
        await backend.DeleteProjectAsync(projectId, Request(project.Revision));
        await backend.DeleteActivityAsync(activityId, Request(activity.Revision));
        await backend.DeleteActivityGroupAsync(groupId, Request(group.Revision));
        await backend.DeleteCalendarEventAsync(eventId, Request(calendarEvent.Revision));
        await backend.DeleteCalendarAsync(calendarId, Request(calendar.Revision));
        await backend.DeleteBoardAsync(boardId, Request(board.Revision));
        await scenario.VerifyAsync(backend);
        return scenario;
    }

    [Fact]
    public async Task StoreCreatesAndNoOpsHaveExactBoundReceipts()
    {
        var directory = Directory.CreateTempSubdirectory("snook-create-receipts-");
        try
        {
            var path = Path.Combine(directory.FullName, "workspace.db");
            var createId = Guid.NewGuid();
            var noOpId = Guid.NewGuid();
            Board created;
            Board noOp;
            await using (var store = new SqliteStore(path))
            {
                await store.InitializeAsync();
                created = await store.CreateBoardAsync("Original create", createId, DateTimeOffset.UtcNow);
                noOp = await store.ReorderBoardAsync(created.Id, ReorderDirection.Later, created.Revision, noOpId, DateTimeOffset.UtcNow);
                await store.UpdateBoardAsync(created.Id, new BoardUpdate("Later"), created.Revision, Guid.NewGuid(), DateTimeOffset.UtcNow);
                Assert.Equal(created, await store.CreateBoardAsync("Original create", createId, DateTimeOffset.UtcNow));
                await RejectAsync(() => store.CreateBoardAsync("Different create", createId, DateTimeOffset.UtcNow));
                await RejectAsync(() => store.CreateActivityGroupAsync("Wrong command", createId, DateTimeOffset.UtcNow));
            }
            await using (var store = new SqliteStore(path))
            {
                await store.InitializeAsync();
                Assert.Equal(created, await store.CreateBoardAsync("Original create", createId, DateTimeOffset.UtcNow));
                Assert.Equal(noOp, await store.ReorderBoardAsync(created.Id, ReorderDirection.Later, created.Revision, noOpId, DateTimeOffset.UtcNow));
            }
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public async Task ReceiptWriteFailureRollsBackMutationAndLeavesOperationRetryable()
    {
        var directory = Directory.CreateTempSubdirectory("snook-receipt-rollback-");
        try
        {
            var path = Path.Combine(directory.FullName, "workspace.db");
            await using var store = new SqliteStore(path);
            await using var backend = new SnookBackend(store);
            await backend.InitializeAsync();
            var before = await backend.GetBootstrapAsync();
            var changes = new List<ChangeNotification>();
            backend.Changed += (_, change) => changes.Add(change);
            var operation = Guid.NewGuid();
            await SqlAsync(path, "CREATE TRIGGER reject_exact BEFORE INSERT ON exact_operation_receipts BEGIN SELECT RAISE(ABORT, 'test receipt failure'); END;");
            await Assert.ThrowsAsync<SqliteException>(() => store.CreateBoardAsync("Atomic receipt", operation, DateTimeOffset.UtcNow));
            var after = await backend.GetBootstrapAsync();
            Assert.Equal(before.CommittedCursor, after.CommittedCursor);
            Assert.DoesNotContain(after.Boards, item => item.Name == "Atomic receipt");
            Assert.Empty(changes);
            await SqlAsync(path, "DROP TRIGGER reject_exact;");
            var created = await store.CreateBoardAsync("Atomic receipt", operation, DateTimeOffset.UtcNow);
            Assert.Equal(created.Id, Assert.Single(changes).AggregateId);
            Assert.Equal(created, await store.CreateBoardAsync("Atomic receipt", operation, DateTimeOffset.UtcNow));
            Assert.Single(changes);
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public async Task MigrationAndStagedRestorePreserveLegacyReceiptsWithoutInventingResults()
    {
        var directory = Directory.CreateTempSubdirectory("snook-v9-receipts-");
        try
        {
            var source = Path.Combine(directory.FullName, "v9.db");
            Board board;
            var request = Request(1);
            await using (var backend = new SnookBackend(new SqliteStore(source)))
            {
                await backend.InitializeAsync();
                board = await backend.CreateBoardAsync("Legacy board");
                await backend.UpdateBoardAsync(board.Id, new BoardUpdate("Legacy result"), request);
            }
            await SqlAsync(source, "DROP TABLE exact_operation_receipts; DELETE FROM schema_migrations WHERE sequence=10;");
            await BackupFixture.WriteManifestAsync(source);
            var sourceBytes = await File.ReadAllBytesAsync(source);
            var target = Path.Combine(directory.FullName, "target.db");
            await using (var backend = new SnookBackend(new SqliteStore(target)))
            {
                await backend.InitializeAsync();
                await backend.RestoreBackupAsync(source);
                await RejectAsync(() => backend.UpdateBoardAsync(board.Id, new BoardUpdate("Legacy result"), request));
                Assert.Equal(sourceBytes, await File.ReadAllBytesAsync(source));
                var saved = await backend.UpdateBoardAsync(board.Id, new BoardUpdate("New result"), Request(2));
                Assert.Equal(3, saved.Revision);
            }
            await using var verify = new SqliteConnection($"Data Source={target};Pooling=False");
            await verify.OpenAsync();
            await using var command = verify.CreateCommand();
            command.CommandText = "SELECT MAX(sequence) FROM schema_migrations;";
            Assert.Equal(10L, Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture));
            await SqlAsync(target, "UPDATE schema_migrations SET checksum='bad' WHERE sequence=9;");
            await using var invalid = new SnookBackend(new SqliteStore(target));
            Assert.Equal(SnookErrorCode.SchemaIncompatible, (await Assert.ThrowsAsync<SnookException>(() => invalid.InitializeAsync())).Code);
        }
        finally { directory.Delete(true); }
    }

    private static async Task SqlAsync(string path, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static OperationRequest Request(long? revision = null) => new(Guid.NewGuid(), Guid.NewGuid(), revision);
    private static async Task RejectAsync(Func<Task> action)
        => Assert.Equal(SnookErrorCode.ValidationFailed, (await Assert.ThrowsAsync<SnookException>(action)).Code);

    internal sealed class ReceiptScenario
    {
        private readonly List<(string Expected, Func<IBackendClient, Task<string>> Replay)> _receipts = [];
        public async Task<T> RememberAsync<T>(Func<IBackendClient, Task<T>> action, IBackendClient backend)
        {
            var value = await action(backend);
            _receipts.Add((JsonSerializer.Serialize(value), async client => JsonSerializer.Serialize(await action(client))));
            return value;
        }
        public async Task VerifyAsync(IBackendClient backend)
        {
            var before = (await backend.GetBootstrapAsync()).CommittedCursor;
            foreach (var receipt in _receipts) Assert.Equal(receipt.Expected, await receipt.Replay(backend));
            Assert.Equal(before, (await backend.GetBootstrapAsync()).CommittedCursor);
        }
    }
}
