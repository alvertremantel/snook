using System.Text.Json;
using Microsoft.Data.Sqlite;
using Snook.Application;
using Snook.Contracts;
using Snook.Domain;
using Snook.Persistence.Sqlite;
using Xunit;

namespace Snook.Application.Tests;

public sealed class JournalTests
{
    [Fact]
    public async Task EmbeddedJournalContract()
    {
        var directory = Directory.CreateTempSubdirectory("snook-journal-contract-");
        try
        {
            await using var backend = new SnookBackend(new SqliteStore(Path.Combine(directory.FullName, "workspace.db")));
            await backend.InitializeAsync();
            await AssertJournalContractAsync(backend);
        }
        finally { directory.Delete(true); }
    }

    internal static async Task AssertJournalContractAsync(IBackendClient backend)
    {
        var create = Request();
        var definition = new JournalDefinition("Everyday", "Room for reflection");
        var journal = await backend.CreateJournalAsync(definition, create);
        Assert.Equal(journal, await backend.CreateJournalAsync(definition, create));
        Assert.Equal(1, journal.Revision);
        var other = await backend.CreateJournalAsync(new JournalDefinition("Work"), Request());
        journal = await backend.UpdateJournalAsync(journal.Id, definition with { Name = "Personal" }, Request(journal.Revision));
        Assert.Equal("Personal", (await backend.GetJournalsAsync()).Single(item => item.Id == journal.Id).Name);
        await AssertErrorAsync(SnookErrorCode.RevisionConflict, () => backend.UpdateJournalAsync(journal.Id, definition, Request(1)));
        await AssertErrorAsync(SnookErrorCode.ValidationFailed, () => backend.CreateJournalAsync(new JournalDefinition("different"), create));

        var when = new DateTimeOffset(2026, 9, 18, 9, 15, 0, TimeSpan.FromHours(-5));
        var entryDefinition = new JournalEntryDefinition(journal.Id, "A small win", "A literal 100% day.\nKept a little time for myself.", when, 7, ["Reflection", "reflection", "Outside"]);
        var entryRequest = Request();
        var committed = new TaskCompletionSource<ChangeNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<ChangeNotification> handler = (_, change) =>
        {
            if (change.OperationId == entryRequest.OperationId) committed.TrySetResult(change);
        };
        backend.Changed += handler;
        JournalEntry first;
        try
        {
            first = await backend.CreateJournalEntryAsync(entryDefinition, entryRequest);
            var change = await committed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("journal-entry", change.AggregateType);
            Assert.Equal(first.Id, change.AggregateId);
            Assert.Equal(first.Revision, change.NewRevision);
        }
        finally { backend.Changed -= handler; }
        Assert.Equal(TimeSpan.Zero, first.OccurredAtUtc.Offset);
        Assert.Equal(when.ToUniversalTime(), first.OccurredAtUtc);
        Assert.Equal(2, first.Tags.Count);
        AssertEntryEqual(first, await backend.GetJournalEntryAsync(first.Id));
        AssertEntryEqual(first, await backend.CreateJournalEntryAsync(entryDefinition, entryRequest));

        var second = await backend.CreateJournalEntryAsync(entryDefinition with { Title = "Second", Mood = 1, Tags = ["reflection"] }, Request());
        var third = await backend.CreateJournalEntryAsync(entryDefinition with { Title = "Third", Mood = null, Tags = [] }, Request());
        await backend.CreateJournalEntryAsync(entryDefinition with { JournalId = other.Id, Title = "Elsewhere", Tags = ["work-only"] }, Request());
        var page = await backend.GetJournalEntriesAsync(new JournalEntryQuery(journal.Id, PageSize: 2));
        Assert.Equal(2, page.Items.Count);
        Assert.True(page.HasMore);
        var lastPage = await backend.GetJournalEntriesAsync(new JournalEntryQuery(journal.Id, PageSize: 2, ContinuationToken: page.ContinuationToken));
        Assert.Single(lastPage.Items);
        Assert.False(lastPage.HasMore);
        Assert.Null(lastPage.ContinuationToken);
        Assert.Equal(3, page.Items.Concat(lastPage.Items).Select(entry => entry.Id).Distinct().Count());
        await AssertErrorAsync(SnookErrorCode.ValidationFailed, () => backend.GetJournalEntriesAsync(new JournalEntryQuery(other.Id, ContinuationToken: page.ContinuationToken)));
        Assert.Equal(2, (await backend.GetJournalEntriesAsync(new JournalEntryQuery(journal.Id, Tag: "REFLECTION"))).Items.Count);
        Assert.Equal(3, (await backend.GetJournalEntriesAsync(new JournalEntryQuery(journal.Id, Search: "100%"))).Items.Count);
        Assert.Empty((await backend.GetJournalEntriesAsync(new JournalEntryQuery(journal.Id, Search: "100_"))).Items);
        Assert.DoesNotContain("work-only", await backend.GetJournalTagsAsync(journal.Id));

        var task = await backend.CreateTaskAsync((await backend.GetBootstrapAsync()).Projects[0].Id, "Tag isolation");
        await backend.AddTaskTagAsync(task.Id, "task-only");
        await backend.AddTaskTagAsync(task.Id, "Reflection");
        Assert.DoesNotContain("task-only", await backend.GetJournalTagsAsync());
        Assert.DoesNotContain((await backend.GetTaskDetailsAsync(task.Id)).Tags, tag => tag.DisplayName == "Outside");

        var cursor = (await backend.GetBootstrapAsync()).CommittedCursor;
        foreach (var invalid in new[] { entryDefinition with { Mood = 0 }, entryDefinition with { Mood = 8 }, entryDefinition with { Content = " " },
                     entryDefinition with { Content = new string('x', 20001) }, entryDefinition with { Tags = [" "] }, entryDefinition with { JournalId = Guid.Empty },
                     entryDefinition with { Tags = Enumerable.Repeat("tag", 21).ToArray() } })
            await AssertErrorAsync(SnookErrorCode.ValidationFailed, () => backend.CreateJournalEntryAsync(invalid, Request()));
        await AssertErrorAsync(SnookErrorCode.NotFound, () => backend.CreateJournalEntryAsync(entryDefinition with { JournalId = Guid.NewGuid() }, Request()));
        await AssertErrorAsync(SnookErrorCode.ValidationFailed, () => backend.CreateJournalEntryAsync(entryDefinition, Request(1)));
        await AssertErrorAsync(SnookErrorCode.ValidationFailed, () => backend.UpdateJournalEntryAsync(first.Id, entryDefinition, Request()));
        await AssertErrorAsync(SnookErrorCode.ValidationFailed, () => backend.CreateJournalEntryAsync(entryDefinition, create));
        await AssertErrorAsync(SnookErrorCode.ValidationFailed, () => backend.CreateJournalAsync(definition, new OperationRequest(Guid.NewGuid(), Guid.Empty)));
        await AssertErrorAsync(SnookErrorCode.ValidationFailed, () => backend.GetJournalEntriesAsync(new JournalEntryQuery(PageSize: 101)));
        await AssertErrorAsync(SnookErrorCode.ValidationFailed, () => backend.GetJournalEntriesAsync(new JournalEntryQuery(PageSize: 0)));
        await AssertErrorAsync(SnookErrorCode.ValidationFailed, () => backend.GetJournalEntriesAsync(new JournalEntryQuery(ContinuationToken: "bad")));
        Assert.Equal(cursor, (await backend.GetBootstrapAsync()).CommittedCursor);

        var update = Request(first.Revision);
        var movedDefinition = entryDefinition with { JournalId = other.Id, Title = "Moved", Content = "Changed words", Mood = null, Tags = [] };
        var moved = await backend.UpdateJournalEntryAsync(first.Id, movedDefinition, update);
        Assert.Equal(first.CreatedAtUtc, moved.CreatedAtUtc);
        Assert.Equal(2, moved.Revision);
        Assert.Null(moved.Mood);
        Assert.Empty(moved.Tags);
        AssertEntryEqual(moved, await backend.UpdateJournalEntryAsync(first.Id, movedDefinition, update));
        cursor = (await backend.GetBootstrapAsync()).CommittedCursor;
        AssertEntryEqual(first, await backend.CreateJournalEntryAsync(entryDefinition, entryRequest));
        await AssertErrorAsync(SnookErrorCode.RevisionConflict, () => backend.UpdateJournalEntryAsync(first.Id, entryDefinition, Request(1)));
        await AssertErrorAsync(SnookErrorCode.ValidationFailed, () => backend.UpdateJournalEntryAsync(first.Id, entryDefinition, update));
        Assert.Equal(cursor, (await backend.GetBootstrapAsync()).CommittedCursor);
        Assert.Equal(other.Id, (await backend.GetJournalEntryAsync(first.Id)).JournalId);
        Assert.DoesNotContain("Outside", await backend.GetJournalTagsAsync());

        second = await backend.SetJournalEntryDeletedAsync(second.Id, true, Request(second.Revision));
        Assert.DoesNotContain((await backend.GetJournalEntriesAsync(new JournalEntryQuery(journal.Id))).Items, entry => entry.Id == second.Id);
        Assert.Contains((await backend.GetJournalEntriesAsync(new JournalEntryQuery(journal.Id, IncludeDeleted: true))).Items, entry => entry.Id == second.Id);
        await AssertErrorAsync(SnookErrorCode.InvalidTransition, () => backend.UpdateJournalEntryAsync(second.Id, entryDefinition, Request(second.Revision)));
        journal = await backend.SetJournalDeletedAsync(journal.Id, true, Request(journal.Revision));
        Assert.DoesNotContain(await backend.GetJournalsAsync(), item => item.Id == journal.Id);
        Assert.Contains(await backend.GetJournalsAsync(true), item => item.Id == journal.Id);
        Assert.Empty((await backend.GetJournalEntriesAsync(new JournalEntryQuery(journal.Id))).Items);
        Assert.Equal(2, (await backend.GetJournalEntriesAsync(new JournalEntryQuery(journal.Id, IncludeDeleted: true))).Items.Count);
        await AssertErrorAsync(SnookErrorCode.InvalidTransition, () => backend.SetJournalEntryDeletedAsync(second.Id, false, Request(second.Revision)));
        await AssertErrorAsync(SnookErrorCode.InvalidTransition, () => backend.CreateJournalEntryAsync(entryDefinition, Request()));
        await AssertErrorAsync(SnookErrorCode.InvalidTransition, () => backend.UpdateJournalAsync(journal.Id, definition, Request(journal.Revision)));
        await AssertErrorAsync(SnookErrorCode.InvalidTransition, () => backend.UpdateJournalEntryAsync(third.Id, movedDefinition, Request(third.Revision)));
        journal = await backend.SetJournalDeletedAsync(journal.Id, false, Request(journal.Revision));
        Assert.Single((await backend.GetJournalEntriesAsync(new JournalEntryQuery(journal.Id))).Items);
        second = await backend.SetJournalEntryDeletedAsync(second.Id, false, Request(second.Revision));
        Assert.Null(second.DeletedAtUtc);
        Assert.Equal(2, (await backend.GetJournalEntriesAsync(new JournalEntryQuery(journal.Id))).Items.Count);
        Assert.Single(await backend.GetJournalTagsAsync(journal.Id));
    }

    [Fact]
    public async Task MigrationRestartBackupRestoreExportAndReceiptsPreserveJournals()
    {
        var directory = Directory.CreateTempSubdirectory("snook-journal-migration-");
        var path = Path.Combine(directory.FullName, "workspace.db");
        var create = Request();
        var definition = new JournalDefinition("Preserved", "Across restarts");
        var entryRequest = Request();
        Journal journal;
        JournalEntry entry;
        JournalEntryDefinition entryDefinition;
        try
        {
            await using (var backend = new SnookBackend(new SqliteStore(path)))
            {
                await backend.InitializeAsync();
                await backend.CreateHabitAsync(new HabitDefinition("Existing habit", "", DateOnly.FromDateTime(DateTime.UtcNow), "UTC"), Request());
            }
            await SqlAsync(path, "DROP TABLE journal_entry_tags; DROP TABLE journal_entries; DROP TABLE journals; DELETE FROM schema_migrations WHERE sequence=9;");
            var oldBackup = Path.Combine(directory.FullName, "v8.db");
            File.Copy(path, oldBackup);
            var oldBytes = await File.ReadAllBytesAsync(oldBackup);
            await using (var backend = new SnookBackend(new SqliteStore(path)))
            {
                await backend.InitializeAsync();
                Assert.Single(await backend.GetHabitsAsync(new HabitQuery()));
                Assert.Empty(await backend.GetJournalsAsync());
                journal = await backend.CreateJournalAsync(definition, create);
                entryDefinition = new JournalEntryDefinition(journal.Id, "Keepsake", "My words", DateTimeOffset.UtcNow, 4, ["private"]);
                entry = await backend.CreateJournalEntryAsync(entryDefinition, entryRequest);
                var changes = new List<ChangeNotification>();
                backend.Changed += (_, change) => changes.Add(change);
                var deleted = await backend.SetJournalEntryDeletedAsync(entry.Id, true, Request(entry.Revision));
                journal = await backend.SetJournalDeletedAsync(journal.Id, true, Request(journal.Revision));
                AssertEntryEqual(entry, await backend.CreateJournalEntryAsync(entryDefinition, entryRequest));
                Assert.Equal(2, changes.Count);
                var backup = await backend.CreateBackupAsync(Path.Combine(directory.FullName, "journals.db"));
                var export = await backend.ExportJsonAsync(Path.Combine(directory.FullName, "journals.json"));
                Assert.Equal(4, export.SchemaVersion);
                using var json = JsonDocument.Parse(await File.ReadAllTextAsync(export.Path));
                var exported = json.RootElement.GetProperty("journaling");
                Assert.Single(exported.GetProperty("journals").EnumerateArray());
                var exportedEntry = Assert.Single(exported.GetProperty("entries").EnumerateArray());
                Assert.Equal("private", exportedEntry.GetProperty("Tags")[0].GetString());
                Assert.Equal(deleted.Revision, exportedEntry.GetProperty("Revision").GetInt64());
                await backend.CreateJournalAsync(new JournalDefinition("After backup"), Request());
                await backend.RestoreBackupAsync(backup.Path);
            }
            await using (var backend = new SnookBackend(new SqliteStore(path)))
            {
                await backend.InitializeAsync();
                Assert.Equal(journal, Assert.Single(await backend.GetJournalsAsync(true)));
                Assert.NotNull((await backend.GetJournalEntryAsync(entry.Id)).DeletedAtUtc);
                AssertEntryEqual(entry, await backend.CreateJournalEntryAsync(entryDefinition, entryRequest));
                Assert.Equal(1, (await backend.CreateJournalAsync(definition, create)).Revision);
                await backend.RestoreBackupAsync(oldBackup);
                Assert.Empty(await backend.GetJournalsAsync(true));
                Assert.Empty((await backend.GetJournalEntriesAsync(new JournalEntryQuery())).Items);
                Assert.Single(await backend.GetHabitsAsync(new HabitQuery()));
                Assert.Equal(oldBytes, await File.ReadAllBytesAsync(oldBackup));
                await backend.CreateJournalAsync(definition, create);
                var corrupt = await backend.CreateBackupAsync(Path.Combine(directory.FullName, "bad.db"));
                await SqlAsync(corrupt.Path, "UPDATE schema_migrations SET checksum='bad' WHERE sequence=9;");
                await AssertErrorAsync(SnookErrorCode.SchemaIncompatible, () => backend.RestoreBackupAsync(corrupt.Path));
                Assert.Single(await backend.GetJournalsAsync());
            }
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
    private static void AssertEntryEqual(JournalEntry expected, JournalEntry actual) => Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(actual));
    private static async Task AssertErrorAsync(SnookErrorCode code, Func<Task> action) => Assert.Equal(code, (await Assert.ThrowsAsync<SnookException>(action)).Code);
}
