using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Snook.Contracts;
using Snook.Domain;
using Snook.Persistence.Sqlite;
using Xunit;

namespace Snook.Application.Tests;

public sealed class CommittedNotificationTests
{
    [Fact]
    public async Task EmbeddedMutationsPublishPersistedMetadataAndNoReplayEvents()
    {
        var directory = Directory.CreateTempSubdirectory("snook-committed-");
        try
        {
            var path = Path.Combine(directory.FullName, "workspace.db");
            await using var backend = new SnookBackend(new SqliteStore(path), new AdvancingClock());
            await backend.InitializeAsync();
            await AssertContractAsync(backend, path);
        }
        finally { directory.Delete(true); }
    }

    internal static async Task AssertContractAsync(IBackendClient backend, string path)
    {
        var initial = await backend.GetBootstrapAsync();
        var notifications = new ConcurrentQueue<ChangeNotification>();
        EventHandler<ChangeNotification> handler = (_, change) =>
        {
            if (change.OperationId != Guid.Empty) notifications.Enqueue(change);
        };
        backend.Changed += handler;
        try
        {
            var board = await backend.CreateBoardAsync("Commit contract");
            var rename = Request(board.Revision);
            var renamed = await backend.UpdateBoardAsync(board.Id, new BoardUpdate("Renamed commit board"), rename);
            await backend.UpdateBoardAsync(board.Id, new BoardUpdate("Renamed commit board"), rename);
            var project = await backend.CreateProjectAsync(board.Id, "Project");
            var activity = await backend.CreateActivityAsync("Activity");
            var task = await backend.CreateTaskAsync(project.Id, "Task");
            await backend.AddTaskTagAsync(task.Id, "task-tag");
            await backend.AddProjectTagAsync(project.Id, "project-tag");
            await backend.AddActivityTagAsync(activity.Id, "activity-tag");
            var start = Request();
            var session = await backend.StartSessionAsync(task.Id, activity.Id, SessionLane.Foreground, start);
            await backend.StartSessionAsync(task.Id, activity.Id, SessionLane.Foreground, start);
            var pause = Request(session.Revision);
            session = await backend.PauseSessionAsync(session.Id, pause);
            await backend.PauseSessionAsync(session.Id, pause);
            session = await backend.ResumeSessionAsync(session.Id, Request(session.Revision));
            var stop = Request(session.Revision);
            await backend.StopSessionAsync(session.Id, "Finished", stop);
            await backend.StopSessionAsync(session.Id, "Finished", stop);
            var complete = Request(task.Revision);
            await backend.CompleteTaskAsync(task.Id, complete);
            await backend.CompleteTaskAsync(task.Id, complete);
            var settings = await backend.GetSettingsAsync();
            var settingRequest = Request(settings.Revision);
            await backend.UpdateSettingsAsync(settings with { AllowConcurrentForeground = true }, settingRequest);
            await backend.UpdateSettingsAsync(settings with { AllowConcurrentForeground = true }, settingRequest);
            var calendar = await backend.CreateCalendarAsync("Commit calendar");
            var when = new DateTimeOffset(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);
            var item = await backend.CreateCalendarEventAsync(calendar.Id, "Event", when, when.AddHours(1));
            var delete = Request(item.Revision);
            await backend.DeleteCalendarEventAsync(item.Id, delete);
            await backend.DeleteCalendarEventAsync(item.Id, delete);
            var batchRequest = Request();
            TaskRevision[] targets = [new(task.Id, (await backend.GetTaskDetailsAsync(task.Id)).Task.Revision)];
            var batch = new BulkTaskUpdate(Starred: true);
            await backend.BulkUpdateTasksAsync(targets, batch, batchRequest);
            await backend.BulkUpdateTasksAsync(targets, batch, batchRequest);
            await Assert.ThrowsAsync<SnookException>(() => backend.UpdateBoardAsync(renamed.Id,
                new BoardUpdate("stale"), Request(board.Revision)));

            // Concurrent embedded callers must retain commit order as well as the daemon's serialized RPC callers.
            await Task.WhenAll(Enumerable.Range(0, 16).Select(index => backend.CreateBoardAsync($"Parallel {index}")));
            var final = await backend.GetBootstrapAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!notifications.Any(change => change.Cursor == final.CommittedCursor))
                await Task.Delay(20, timeout.Token);

            var saved = await ReadChangesAsync(path, initial.CommittedCursor);
            Assert.Equal(saved, notifications.ToArray());
            Assert.All(saved, change => Assert.True(change.Cursor > initial.CommittedCursor));
            Assert.Single(saved, change => change.OperationId == rename.OperationId);
            Assert.Single(saved, change => change.OperationId == start.OperationId);
            Assert.Single(saved, change => change.OperationId == stop.OperationId);
            Assert.Single(saved, change => change.OperationId == batchRequest.OperationId);
        }
        finally { backend.Changed -= handler; }
    }

    [Fact]
    public async Task ReplayAfterRestartAndTransactionRollbackDoNotPublish()
    {
        var directory = Directory.CreateTempSubdirectory("snook-replay-events-");
        try
        {
            var path = Path.Combine(directory.FullName, "workspace.db");
            Board board;
            OperationRequest update;
            await using (var backend = new SnookBackend(new SqliteStore(path)))
            {
                await backend.InitializeAsync();
                board = await backend.CreateBoardAsync("Before restart");
                update = Request(board.Revision);
                await backend.UpdateBoardAsync(board.Id, new BoardUpdate("Updated"), update);
            }
            await using (var backend = new SnookBackend(new SqliteStore(path)))
            {
                await backend.InitializeAsync();
                var before = await backend.GetBootstrapAsync();
                var changes = new List<ChangeNotification>();
                backend.Changed += (_, change) => changes.Add(change);
                await backend.UpdateBoardAsync(board.Id, new BoardUpdate("Updated"), update);
                Assert.Empty(changes);
                await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "CREATE TRIGGER reject_receipt BEFORE INSERT ON operation_receipts BEGIN SELECT RAISE(ABORT, 'test receipt failure'); END;";
                await command.ExecuteNonQueryAsync();
                await Assert.ThrowsAsync<SqliteException>(() => backend.CreateBoardAsync("Must roll back"));
                var after = await backend.GetBootstrapAsync();
                Assert.Equal(before.CommittedCursor, after.CommittedCursor);
                Assert.DoesNotContain(after.Boards, item => item.Name == "Must roll back");
                Assert.Empty(changes);
                command.CommandText = "DROP TRIGGER reject_receipt;";
                await command.ExecuteNonQueryAsync();
                var committed = await backend.CreateBoardAsync("After rollback");
                Assert.Equal(committed.Id, Assert.Single(changes).AggregateId);
            }
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public async Task ThrowingAndReentrantObserversCannotFailCommittedWritesOrReorderEvents()
    {
        var directory = Directory.CreateTempSubdirectory("snook-observer-");
        try
        {
            var path = Path.Combine(directory.FullName, "workspace.db");
            await using var backend = new SnookBackend(new SqliteStore(path));
            await backend.InitializeAsync();
            var changes = new List<ChangeNotification>();
            var nested = false;
            backend.Changed += (_, _) => throw new InvalidOperationException("Observer failed");
            backend.Changed += (_, change) =>
            {
                changes.Add(change);
                if (nested) return;
                nested = true;
                // The row is already visible to another connection at notification time.
                Assert.Single(ReadChangesAsync(path, change.Cursor - 1).GetAwaiter().GetResult());
                backend.CreateBoardAsync("Nested").GetAwaiter().GetResult();
            };
            var outer = await backend.CreateBoardAsync("Outer").WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(2, changes.Count);
            Assert.Equal(outer.Id, changes[0].AggregateId);
            Assert.True(changes[1].Cursor > changes[0].Cursor);
            Assert.Equal(await ReadChangesAsync(path, 0), changes);
        }
        finally { directory.Delete(true); }
    }

    private static async Task<IReadOnlyList<ChangeNotification>> ReadChangesAsync(string path, long after)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT sequence,operation_id,aggregate_type,aggregate_id,kind,new_revision,occurred_at_utc_ms FROM mutation_log WHERE sequence > $after ORDER BY sequence;";
        command.Parameters.AddWithValue("$after", after);
        var result = new List<ChangeNotification>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            result.Add(new ChangeNotification(reader.GetInt64(0), Guid.Parse(reader.GetString(1)), reader.GetString(2),
                Guid.Parse(reader.GetString(3)), reader.GetString(4), reader.GetInt64(5), DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(6))));
        return result;
    }

    private static OperationRequest Request(long? revision = null) => new(Guid.NewGuid(), Guid.NewGuid(), revision);

    private sealed class AdvancingClock : TimeProvider
    {
        private long _ticks;
        public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 9, 18, 10, 0, 0, TimeSpan.Zero)
            .AddTicks(Interlocked.Add(ref _ticks, TimeSpan.TicksPerSecond + 1234));
    }
}
