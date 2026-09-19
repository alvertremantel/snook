using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Snook.Application;
using Snook.Contracts;
using Snook.Domain;
using Snook.Persistence.Sqlite;
using Xunit;

namespace Snook.Application.Tests;

public sealed class HabitTests
{
    private static readonly string[] InitialChangeKinds = ["created", "check-in"];
    [Fact]
    public async Task EmbeddedHabitContract()
    {
        var directory = Directory.CreateTempSubdirectory("snook-habits-");
        try
        {
            await using var backend = new SnookBackend(new SqliteStore(Path.Combine(directory.FullName, "workspace.db")));
            await backend.InitializeAsync();
            await AssertHabitContractAsync(backend);
        }
        finally { directory.Delete(true); }
    }

    internal static async Task AssertHabitContractAsync(IBackendClient backend)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var definition = new HabitDefinition("Read daily", "One chapter", today.AddDays(-10), "UTC");
        var create = Request();
        var habit = await backend.CreateHabitAsync(definition, create);
        Assert.Equal(1, habit.Revision);
        Assert.Equal(habit, await backend.CreateHabitAsync(definition, create));
        var cursor = (await backend.GetBootstrapAsync()).CommittedCursor;
        Assert.Equal(SnookErrorCode.ValidationFailed, (await Assert.ThrowsAsync<SnookException>(() =>
            backend.CreateHabitAsync(definition with { Name = "Different request" }, create))).Code);
        Assert.Equal(cursor, (await backend.GetBootstrapAsync()).CommittedCursor);
        var checkRequest = Request(habit.Revision);
        var checkedYesterday = await backend.SetHabitCompletionAsync(habit.Id, today.AddDays(-1), true, checkRequest);
        habit = checkedYesterday;
        var progress = (await backend.GetHabitsAsync(new HabitQuery())).Single(item => item.Habit.Id == habit.Id);
        Assert.Equal(1, progress.CurrentStreak); // Today still has time to be completed.
        Assert.Equal(11, progress.EligibleDays);
        Assert.Single(progress.CheckIns);
        Assert.Equal(TimeSpan.Zero, progress.CheckIns[0].CompletedAtUtc.Offset);

        habit = await backend.SetHabitCompletionAsync(habit.Id, today.AddDays(-2), true, Request(habit.Revision));
        habit = await backend.SetHabitCompletionAsync(habit.Id, today, true, Request(habit.Revision));
        progress = (await backend.GetHabitsAsync(new HabitQuery())).Single(item => item.Habit.Id == habit.Id);
        Assert.Equal(3, progress.CurrentStreak);
        Assert.Equal(3, progress.CompletedDays);
        var timestamp = progress.CheckIns.Single(item => item.Date == today).CompletedAtUtc;
        habit = await backend.SetHabitCompletionAsync(habit.Id, today, true, Request(habit.Revision));
        Assert.Equal(timestamp, (await backend.GetHabitsAsync(new HabitQuery())).Single(item => item.Habit.Id == habit.Id).CheckIns.Single(item => item.Date == today).CompletedAtUtc);

        cursor = (await backend.GetBootstrapAsync()).CommittedCursor;
        Assert.Equal(SnookErrorCode.RevisionConflict, (await Assert.ThrowsAsync<SnookException>(() =>
            backend.SetHabitCompletionAsync(habit.Id, today, false, Request(1)))).Code);
        Assert.Equal(checkedYesterday, await backend.SetHabitCompletionAsync(habit.Id, today.AddDays(-1), true, checkRequest));
        Assert.Equal(cursor, (await backend.GetBootstrapAsync()).CommittedCursor);
        Assert.Equal(SnookErrorCode.ValidationFailed, (await Assert.ThrowsAsync<SnookException>(() =>
            backend.SetHabitCompletionAsync(habit.Id, today.AddDays(-1), false, checkRequest))).Code);

        habit = await backend.SetHabitCompletionAsync(habit.Id, today.AddDays(-1), false, Request(habit.Revision));
        Assert.Equal(1, (await backend.GetHabitsAsync(new HabitQuery())).Single(item => item.Habit.Id == habit.Id).CurrentStreak);
        habit = await backend.UpdateHabitAsync(habit.Id, new HabitUpdate("Evening reading", "A few pages"), Request(habit.Revision));
        var renamed = habit;
        habit = await backend.SetHabitArchivedAsync(habit.Id, true, Request(habit.Revision));
        Assert.DoesNotContain(await backend.GetHabitsAsync(new HabitQuery()), item => item.Habit.Id == habit.Id);
        await Assert.ThrowsAsync<SnookException>(() => backend.SetHabitCompletionAsync(habit.Id, today, false, Request(habit.Revision)));
        Assert.Equal(2, (await backend.GetHabitsAsync(new HabitQuery(IncludeArchived: true))).Single(item => item.Habit.Id == habit.Id).CompletedDays);
        habit = await backend.SetHabitDeletedAsync(habit.Id, true, Request(habit.Revision));
        Assert.DoesNotContain(await backend.GetHabitsAsync(new HabitQuery(IncludeArchived: true)), item => item.Habit.Id == habit.Id);
        Assert.Contains(await backend.GetHabitsAsync(new HabitQuery(IncludeDeleted: true)), item => item.Habit.Id == habit.Id);
        habit = await backend.SetHabitDeletedAsync(habit.Id, false, Request(habit.Revision));
        Assert.NotNull(habit.ArchivedAtUtc);
        habit = await backend.SetHabitArchivedAsync(habit.Id, false, Request(habit.Revision));
        Assert.Equal(renamed.Name, habit.Name);
        Assert.Equal(2, (await backend.GetHabitsAsync(new HabitQuery())).Single(item => item.Habit.Id == habit.Id).CompletedDays);
        var earlier = (await backend.GetHabitsAsync(new HabitQuery(2, today.AddDays(-2)))).Single(item => item.Habit.Id == habit.Id);
        Assert.Single(earlier.CheckIns);
        Assert.Equal(2, earlier.EligibleDays);
        Assert.Equal(1, earlier.CurrentStreak); // Always current, even while reviewing history.

        cursor = (await backend.GetBootstrapAsync()).CommittedCursor;
        foreach (var invalid in new[] { today.AddDays(1), today.AddDays(-11), today.AddDays(-366) })
            await Assert.ThrowsAsync<SnookException>(() => backend.SetHabitCompletionAsync(habit.Id, invalid, true, Request(habit.Revision)));
        await Assert.ThrowsAsync<SnookException>(() => backend.GetHabitsAsync(new HabitQuery(367)));
        await Assert.ThrowsAsync<SnookException>(() => backend.GetHabitsAsync(new HabitQuery(0)));
        await Assert.ThrowsAsync<SnookException>(() => backend.CreateHabitAsync(definition with { Name = " " }, Request()));
        await Assert.ThrowsAsync<SnookException>(() => backend.CreateHabitAsync(definition with { TimeZone = "invalid" }, Request()));
        await Assert.ThrowsAsync<SnookException>(() => backend.CreateHabitAsync(definition with { StartDate = today.AddDays(-366) }, Request()));
        await Assert.ThrowsAsync<SnookException>(() => backend.UpdateHabitAsync(habit.Id, new HabitUpdate("x", ""), Request()));
        await Assert.ThrowsAsync<SnookException>(() => backend.CreateHabitAsync(definition, new OperationRequest(Guid.Empty, Guid.NewGuid())));
        Assert.Equal(cursor, (await backend.GetBootstrapAsync()).CommittedCursor);
    }

    [Fact]
    public async Task MigrationRestartBackupRestoreAndExportPreserveHabitsAndReceipts()
    {
        var directory = Directory.CreateTempSubdirectory("snook-habit-upgrade-");
        var path = Path.Combine(directory.FullName, "workspace.db");
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var definition = new HabitDefinition("Walk", "Outside", today.AddDays(-3), "UTC");
        var create = Request();
        var check = Request(1);
        Habit initial;
        Habit completed;
        Guid taskId;
        try
        {
            await using (var backend = new SnookBackend(new SqliteStore(path)))
            {
                await backend.InitializeAsync();
                taskId = (await backend.CreateTaskAsync((await backend.GetBootstrapAsync()).Projects[0].Id, "Existing task")).Id;
            }
            // Model a real v7 database, including its unchanged historical checksum.
            await using (var connection = new SqliteConnection($"Data Source={path}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "DROP TABLE exact_operation_receipts; DROP TABLE journal_entry_tags; DROP TABLE journal_entries; DROP TABLE journals; DROP TABLE habit_check_ins; DROP TABLE habits; DELETE FROM schema_migrations WHERE sequence>=8;";
                await command.ExecuteNonQueryAsync();
            }
            await using (var backend = new SnookBackend(new SqliteStore(path)))
            {
                await backend.InitializeAsync();
                Assert.Equal("Existing task", (await backend.GetTaskDetailsAsync(taskId)).Task.Title);
                Assert.Empty(await backend.GetHabitsAsync(new HabitQuery()));
                var changes = new List<ChangeNotification>();
                backend.Changed += (_, change) => changes.Add(change);
                initial = await backend.CreateHabitAsync(definition, create);
                completed = await backend.SetHabitCompletionAsync(initial.Id, today, true, check);
                Assert.Equal(InitialChangeKinds, changes.Select(change => change.ChangeKind));
                Assert.All(changes, change => Assert.Equal("habit", change.AggregateType));
                Assert.Equal(completed, await backend.SetHabitCompletionAsync(initial.Id, today, true, check));
                Assert.Equal(2, changes.Count);
                var archived = await backend.SetHabitArchivedAsync(initial.Id, true, Request(completed.Revision));
                await backend.SetHabitDeletedAsync(initial.Id, true, Request(archived.Revision));
                var backup = await backend.CreateBackupAsync(Path.Combine(directory.FullName, "habits.db"));
                var export = await backend.ExportJsonAsync(Path.Combine(directory.FullName, "habits.json"));
                Assert.Equal(5, export.SchemaVersion);
                using var json = JsonDocument.Parse(await File.ReadAllTextAsync(export.Path));
                Assert.Single(json.RootElement.GetProperty("habitTracking").GetProperty("habits").EnumerateArray());
                Assert.Single(json.RootElement.GetProperty("habitTracking").GetProperty("checkIns").EnumerateArray());
                await backend.CreateHabitAsync(definition with { Name = "After backup" }, Request());
                await backend.RestoreBackupAsync(backup.Path);
            }
            await using (var backend = new SnookBackend(new SqliteStore(path)))
            {
                await backend.InitializeAsync();
                var progress = Assert.Single(await backend.GetHabitsAsync(new HabitQuery(IncludeDeleted: true)));
                Assert.NotNull(progress.Habit.DeletedAtUtc);
                Assert.Single(progress.CheckIns);
                Assert.Equal(initial, await backend.CreateHabitAsync(definition, create));
                Assert.Equal(completed, await backend.SetHabitCompletionAsync(initial.Id, today, true, check));
                Assert.Equal(progress.Habit.Revision, Assert.Single(await backend.GetHabitsAsync(new HabitQuery(IncludeDeleted: true))).Habit.Revision);
            }
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public async Task RestoringV7UpgradesStagingWithoutTouchingSourceAndRejectsNewerSchemas()
    {
        var directory = Directory.CreateTempSubdirectory("snook-habit-restore-");
        try
        {
            var path = Path.Combine(directory.FullName, "workspace.db");
            var backupPath = Path.Combine(directory.FullName, "v7.db");
            await using var backend = new SnookBackend(new SqliteStore(path));
            await backend.InitializeAsync();
            await backend.CreateBackupAsync(backupPath);
            await using (var connection = new SqliteConnection($"Data Source={backupPath};Pooling=False"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "DROP TABLE exact_operation_receipts; DROP TABLE journal_entry_tags; DROP TABLE journal_entries; DROP TABLE journals; DROP TABLE habit_check_ins; DROP TABLE habits; DELETE FROM schema_migrations WHERE sequence>=8;";
                await command.ExecuteNonQueryAsync();
            }
            var sourceBytes = await File.ReadAllBytesAsync(backupPath);
            await BackupFixture.WriteManifestAsync(backupPath);
            await backend.CreateHabitAsync(new HabitDefinition("After v7", "", DateOnly.FromDateTime(DateTime.UtcNow), "UTC"), Request());
            await backend.RestoreBackupAsync(backupPath);
            Assert.Empty(await backend.GetHabitsAsync(new HabitQuery()));
            Assert.Equal(sourceBytes, await File.ReadAllBytesAsync(backupPath));
            var habit = await backend.CreateHabitAsync(new HabitDefinition("After restore", "", DateOnly.FromDateTime(DateTime.UtcNow), "UTC"), Request());
            await using (var connection = new SqliteConnection($"Data Source={backupPath};Pooling=False"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "INSERT INTO schema_migrations VALUES(99,'future','unknown',0);";
                await command.ExecuteNonQueryAsync();
            }
            await BackupFixture.WriteManifestAsync(backupPath);
            Assert.Equal(SnookErrorCode.SchemaIncompatible, (await Assert.ThrowsAsync<SnookException>(() => backend.RestoreBackupAsync(backupPath))).Code);
            Assert.Equal(habit, Assert.Single(await backend.GetHabitsAsync(new HabitQuery())).Habit);
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public async Task StreakCrossesReadWindowAndMidnightUsesHabitZone()
    {
        var directory = Directory.CreateTempSubdirectory("snook-habit-clock-");
        var clock = new HabitClock(new DateTimeOffset(2026, 11, 1, 7, 30, 0, TimeSpan.Zero));
        try
        {
            await using var backend = new SnookBackend(new SqliteStore(Path.Combine(directory.FullName, "workspace.db")), clock);
            await backend.InitializeAsync();
            var today = new DateOnly(2026, 11, 1);
            var habit = await backend.CreateHabitAsync(new HabitDefinition("Practice", "", today.AddDays(-40), "America/Chicago"), Request());
            for (var offset = -40; offset <= 0; offset++)
                habit = await backend.SetHabitCompletionAsync(habit.Id, today.AddDays(offset), true, Request(habit.Revision));
            var progress = Assert.Single(await backend.GetHabitsAsync(new HabitQuery(7)));
            Assert.Equal(41, progress.CurrentStreak);
            Assert.Equal(7, progress.CompletedDays);
            clock.Now = new DateTimeOffset(2026, 11, 2, 5, 59, 0, TimeSpan.Zero);
            Assert.Equal(today, Assert.Single(await backend.GetHabitsAsync(new HabitQuery())).Today);
            clock.Now = clock.Now.AddMinutes(1);
            progress = Assert.Single(await backend.GetHabitsAsync(new HabitQuery()));
            Assert.Equal(today.AddDays(1), progress.Today);
            Assert.Equal(41, progress.CurrentStreak);
            clock.Now = clock.Now.AddDays(1);
            Assert.Equal(0, Assert.Single(await backend.GetHabitsAsync(new HabitQuery())).CurrentStreak);
        }
        finally { directory.Delete(true); }
    }

    private static OperationRequest Request(long? revision = null) => new(Guid.NewGuid(), Guid.NewGuid(), revision);
    private sealed class HabitClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
