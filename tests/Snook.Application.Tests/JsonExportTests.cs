using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Snook.Contracts;
using Snook.Domain;
using Snook.Persistence.Sqlite;
using Xunit;

namespace Snook.Application.Tests;

public sealed class JsonExportTests
{
    private static readonly JsonSerializerOptions PersistedJsonOptions = new()
    {
        Converters = { new PersistedInstantConverter() }
    };

    [Fact]
    public async Task EmptyRelationshipSectionsAndPersistedSettingsAreExplicit()
    {
        var directory = Directory.CreateTempSubdirectory("snook-json-empty-");
        try
        {
            await using var backend = new SnookBackend(new SqliteStore(Path.Combine(directory.FullName, "workspace.db")), allowConcurrentForeground: true);
            await backend.InitializeAsync();
            var json = await ExportAsync(backend, Path.Combine(directory.FullName, "empty.json"));
            Assert.Empty(json["taskLinks"]!.AsArray());
            Assert.Empty(json["taskDependencies"]!.AsArray());
            Assert.False(json["settings"]!["AllowConcurrentForeground"]!.GetValue<bool>());
            AssertNode(await backend.GetSettingsAsync(), json["settings"]);
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public async Task StoredDeletedLinksTagsAndSessionsRetainTheirDataAndMetadata()
    {
        var directory = Directory.CreateTempSubdirectory("snook-json-deleted-link-");
        try
        {
            var database = Path.Combine(directory.FullName, "workspace.db");
            await using var backend = new SnookBackend(new SqliteStore(database));
            await backend.InitializeAsync();
            var project = Assert.Single((await backend.GetBootstrapAsync()).Projects);
            var task = await backend.CreateTaskAsync(project.Id, "Imported link tombstone");
            var link = await backend.AddTaskLinkAsync(task.Id, "Stored deleted link", "https://example.invalid/old");
            var tag = await backend.AddTaskTagAsync(task.Id, "Stored deleted tag");
            var when = new DateTimeOffset(2020, 9, 18, 12, 0, 0, TimeSpan.Zero);
            var session = await backend.CreateManualSessionAsync(task.Id, null, when, when.AddMinutes(5), "Deleted session notes");
            session = await backend.CorrectSessionAsync(session.Id, new SessionCorrection(task.Id, null, when, when.AddMinutes(6),
                session.Notes, [new TimeInterval(session.Intervals[0].Id, when, when.AddMinutes(6), "manual")], "Deleted session correction"), Request(session.Revision));
            // The schema can retain tombstones even though the current UI has no
            // delete-link command. Construct that stored state explicitly.
            await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString()))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "UPDATE task_links SET deleted_at_utc_ms=1600000000123,revision=2 WHERE id=$id; UPDATE tags SET deleted_at_utc_ms=1600000000123 WHERE id=$tag; UPDATE tracking_sessions SET deleted_at_utc_ms=1600000000123 WHERE id=$session;";
                command.Parameters.AddWithValue("$id", link.Id.ToString("D"));
                command.Parameters.AddWithValue("$tag", tag.Id.ToString("D"));
                command.Parameters.AddWithValue("$session", session.Id.ToString("D"));
                Assert.Equal(3, await command.ExecuteNonQueryAsync());
            }
            Assert.Empty((await backend.GetTaskDetailsAsync(task.Id)).Links);
            var json = await ExportAsync(backend, Path.Combine(directory.FullName, "deleted-link.json"));
            var row = Find(json["taskLinks"]!.AsArray(), link.Id);
            Assert.Equal(1600000000123, row["DeletedAtUtc"]!.GetValue<DateTimeOffset>().ToUnixTimeMilliseconds());
            Assert.Equal(2, row["Revision"]!.GetValue<long>());
            var exportedTag = Find(json["tags"]!.AsArray(), tag.Id);
            Assert.Equal("Stored deleted tag", exportedTag["DisplayName"]!.GetValue<string>());
            Assert.Equal(1600000000123, exportedTag["DeletedAtUtc"]!.GetValue<DateTimeOffset>().ToUnixTimeMilliseconds());
            var exportedSession = Find(json["sessions"]!.AsArray(), session.Id);
            Assert.Equal("Deleted session notes", exportedSession["Notes"]!.GetValue<string>());
            Assert.Equal(1600000000123, exportedSession["DeletedAtUtc"]!.GetValue<DateTimeOffset>().ToUnixTimeMilliseconds());
            Assert.Equal(when.AddMinutes(6), Assert.Single(exportedSession["Intervals"]!.AsArray())!["EndedAtUtc"]!.GetValue<DateTimeOffset>());
            Assert.Contains(json["corrections"]!.AsArray(), item => item!["SessionId"]!.GetValue<Guid>() == session.Id
                && item["Reason"]!.GetValue<string>() == "Deleted session correction");
            await AssertRelationshipRowsAsync(json, database);
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public async Task ConcurrentAtomicTaskAndTagChangesRemainOneExportSnapshot()
    {
        var directory = Directory.CreateTempSubdirectory("snook-json-concurrent-");
        try
        {
            await using var backend = new SnookBackend(new SqliteStore(Path.Combine(directory.FullName, "workspace.db")));
            await backend.InitializeAsync();
            var project = Assert.Single((await backend.GetBootstrapAsync()).Projects);
            var first = await backend.CreateTaskAsync(project.Id, "First");
            var second = await backend.CreateTaskAsync(project.Id, "Second");
            var current = await backend.BulkUpdateTasksAsync([new(first.Id, first.Revision), new(second.Id, second.Revision)],
                new BulkTaskUpdate(Title: "generation-0", TagsToAdd: ["generation-0"]), Request());
            var initialCursor = (await backend.GetBootstrapAsync()).CommittedCursor;
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var writer = Task.Run(async () =>
            {
                await start.Task;
                for (var generation = 1; generation <= 40; generation++)
                {
                    current = await backend.BulkUpdateTasksAsync(current.Select(task => new TaskRevision(task.Id, task.Revision)).ToArray(),
                        new BulkTaskUpdate(Title: $"generation-{generation}", TagsToAdd: [$"generation-{generation}"], TagsToRemove: [$"generation-{generation - 1}"]), Request());
                    await Task.Yield();
                }
            });
            var exporter = Task.Run(async () =>
            {
                await start.Task;
                for (var index = 0; index < 30; index++)
                {
                    var json = await ExportAsync(backend, Path.Combine(directory.FullName, $"snapshot-{index}.json"));
                    var exportedFirst = Find(json["tasks"]!.AsArray(), first.Id);
                    var exportedSecond = Find(json["tasks"]!.AsArray(), second.Id);
                    var title = exportedFirst["Title"]!.GetValue<string>();
                    Assert.Equal(title, exportedSecond["Title"]!.GetValue<string>());
                    var generation = int.Parse(title["generation-".Length..], System.Globalization.CultureInfo.InvariantCulture);
                    Assert.Equal(initialCursor + generation, json["committedCursor"]!.GetValue<long>());
                    foreach (var id in new[] { first.Id, second.Id })
                    {
                        var tagId = Assert.Single(json["taskTagIds"]![id.ToString()]!.Deserialize<Guid[]>()!);
                        Assert.Equal(title, Find(json["tags"]!.AsArray(), tagId)["DisplayName"]!.GetValue<string>());
                    }
                    await Task.Yield();
                }
            });
            start.SetResult();
            await Task.WhenAll(writer, exporter);
        }
        finally { directory.Delete(true); }
    }
    [Fact]
    public async Task ExportRetainsSupportedDataAfterBackupRestoreAndRestart()
    {
        var directory = Directory.CreateTempSubdirectory("snook-json-export-");
        try
        {
            var database = Path.Combine(directory.FullName, "workspace.db");
            JsonObject expected;
            await using (var backend = new SnookBackend(new SqliteStore(database)))
            {
                await backend.InitializeAsync();
                expected = await ExerciseAsync(backend, database, directory.FullName);
                var backup = await backend.CreateBackupAsync(Path.Combine(directory.FullName, "export-source.db"));
                await backend.CreateBoardAsync("Not in exported snapshot");
                await backend.RestoreBackupAsync(backup.Path);
                Assert.True(JsonNode.DeepEquals(expected, await ExportAsync(backend, Path.Combine(directory.FullName, "restored.json"))));
            }
            await using (var backend = new SnookBackend(new SqliteStore(database)))
            {
                await backend.InitializeAsync();
                Assert.True(JsonNode.DeepEquals(expected, await ExportAsync(backend, Path.Combine(directory.FullName, "restarted.json"))));
            }
        }
        finally { directory.Delete(true); }
    }

    internal static async Task<JsonObject> ExerciseAsync(IBackendClient backend, string database, string directory)
    {
        var board = await backend.CreateBoardAsync("Export board");
        var project = await backend.CreateProjectAsync(board.Id, "Export project", "Description", true);
        var group = await backend.CreateActivityGroupAsync("Export group");
        var activity = await backend.CreateActivityAsync("Export activity", "Background", SessionLane.Background, group.Id);
        var prerequisite = await backend.CreateTaskAsync(project.Id, "Export prerequisite");
        var task = await backend.CreateTaskAsync(project.Id, "Export task", Priority.High, new DateOnly(2026, 9, 20));
        task = await backend.UpdateTaskAsync(task.Id, new TaskUpdate(task.Title, "Task details", task.Priority,
            task.DueDate, activity.Id, true), Request(task.Revision));
        var removedTask = await backend.CreateTaskAsync(project.Id, "Deleted export task");
        var links = new[]
        {
            await backend.AddTaskLinkAsync(task.Id, "Unicode: café", "https://example.invalid/reference?q=one&two=2"),
            await backend.AddTaskLinkAsync(removedTask.Id, null, "file:///tmp/snook-export-reference.txt", "file")
        };
        await backend.AddTaskDependencyAsync(task.Id, prerequisite.Id);
        await backend.AddTaskDependencyAsync(removedTask.Id, prerequisite.Id);
        var taskTag = await backend.AddTaskTagAsync(removedTask.Id, "Export tag");
        var projectTag = await backend.AddProjectTagAsync(project.Id, "Export project tag");
        var activityTag = await backend.AddActivityTagAsync(activity.Id, "Export activity tag");
        var settings = await backend.GetSettingsAsync();
        settings = await backend.UpdateSettingsAsync(settings with { AllowConcurrentForeground = true }, Request(settings.Revision));

        var when = new DateTimeOffset(2020, 9, 18, 12, 0, 0, TimeSpan.Zero);
        var session = await backend.CreateManualSessionAsync(task.Id, activity.Id, when, when.AddMinutes(20), "Before correction");
        session = await backend.CorrectSessionAsync(session.Id, new SessionCorrection(task.Id, activity.Id, when, when.AddMinutes(25),
            "After correction", [new TimeInterval(session.Intervals[0].Id, when, when.AddMinutes(25), "manual")], "Duration correction"), Request(session.Revision));
        var calendar = await backend.CreateCalendarAsync("Export calendar", "#123456", false);
        var block = await backend.CreateScheduleBlockAsync(calendar.Id, task.Id, activity.Id, "Plan", when, when.AddHours(1), "UTC");
        var calendarEvent = await backend.CreateCalendarEventAsync(calendar.Id, "Export event", when, when.AddHours(1),
            "Event details", "Room", recurrenceRule: "FREQ=DAILY", recurrenceEndUtc: when.AddDays(2));
        var exception = await backend.UpsertCalendarEventExceptionAsync(calendarEvent.Id, when, null, null, null, true, Request());

        var day = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1);
        var habit = await backend.CreateHabitAsync(new HabitDefinition("Export habit", "Habit details", day, "UTC"), Request());
        habit = await backend.SetHabitCompletionAsync(habit.Id, day, true, Request(habit.Revision));
        habit = await backend.SetHabitDeletedAsync(habit.Id, true, Request(habit.Revision));
        var journal = await backend.CreateJournalAsync(new JournalDefinition("Export journal", "Journal details"), Request());
        var entry = await backend.CreateJournalEntryAsync(new JournalEntryDefinition(journal.Id, "Export entry", "Multiline\ncontent café",
            when, 6, ["journal-only", "Reflection"]), Request());
        entry = await backend.SetJournalEntryDeletedAsync(entry.Id, true, Request(entry.Revision));
        journal = await backend.SetJournalDeletedAsync(journal.Id, true, Request(journal.Revision));
        removedTask = await backend.DeleteTaskAsync(removedTask.Id, Request(removedTask.Revision));
        task = await backend.ArchiveTaskAsync(task.Id, Request(task.Revision));
        var cursor = (await backend.GetBootstrapAsync()).CommittedCursor;

        var json = await ExportAsync(backend, Path.Combine(directory, "complete-export.json"));
        Assert.Equal(cursor, json["committedCursor"]!.GetValue<long>());
        AssertNode(settings, json["settings"]);
        AssertRecord(board, json, "boards", board.Id);
        AssertRecord(project, json, "projects", project.Id);
        AssertRecord(group, json, "activityGroups", group.Id);
        AssertRecord(activity, json, "activities", activity.Id);
        AssertRecord(task, json, "tasks", task.Id);
        AssertRecord(removedTask, json, "tasks", removedTask.Id);
        AssertRecord(session, json, "sessions", session.Id);
        AssertRecord(calendar, json, "calendars", calendar.Id);
        AssertRecord(block, json, "scheduleBlocks", block.Id);
        AssertRecord(calendarEvent, json, "calendarEvents", calendarEvent.Id);
        AssertRecord(exception, json, "calendarEventExceptions", exception.Id);
        AssertRecord(habit, json["habitTracking"]!.AsObject(), "habits", habit.Id);
        AssertRecord(journal, json["journaling"]!.AsObject(), "journals", journal.Id);
        AssertRecord(entry, json["journaling"]!.AsObject(), "entries", entry.Id);
        Assert.Contains(json["habitTracking"]!["checkIns"]!.AsArray(), item => item!["HabitId"]!.GetValue<Guid>() == habit.Id
            && item["Date"]!.GetValue<string>() == day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        Assert.Contains(json["corrections"]!.AsArray(), item => item!["SessionId"]!.GetValue<Guid>() == session.Id
            && item["Reason"]!.GetValue<string>() == "Duration correction");
        foreach (var link in links)
        {
            var exported = Find(json["taskLinks"]!.AsArray(), link.Id);
            Assert.Equal(link, exported.Deserialize<TaskLink>());
            Assert.Equal(1, exported["Revision"]!.GetValue<long>());
            Assert.Null(exported["DeletedAtUtc"]);
            Assert.Equal(TimeSpan.Zero, exported["CreatedAtUtc"]!.GetValue<DateTimeOffset>().Offset);
        }
        Assert.Contains(json["taskDependencies"]!.AsArray(), item => item!["TaskId"]!.GetValue<Guid>() == removedTask.Id
            && item["PrerequisiteTaskId"]!.GetValue<Guid>() == prerequisite.Id);
        Assert.Contains(taskTag.Id, json["taskTagIds"]![removedTask.Id.ToString()]!.Deserialize<Guid[]>()!);
        Assert.Contains(projectTag.Id, json["projectTagIds"]![project.Id.ToString()]!.Deserialize<Guid[]>()!);
        Assert.Contains(activityTag.Id, json["activityTagIds"]![activity.Id.ToString()]!.Deserialize<Guid[]>()!);
        await AssertRelationshipRowsAsync(json, database);
        Assert.Equal(cursor, (await backend.GetBootstrapAsync()).CommittedCursor);
        foreach (var excluded in new[] { "operation_receipts", "exact_operation_receipts", "mutation_log", "token", "endpoint", "databasePath" })
            Assert.False(json.ContainsKey(excluded));
        return json;
    }

    private static async Task AssertRelationshipRowsAsync(JsonObject json, string database)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await connection.OpenAsync();
        foreach (var (table, section) in new[] { ("task_links", "taskLinks"), ("task_dependencies", "taskDependencies") })
        {
            await using var command = connection.CreateCommand();
            command.CommandText = table == "task_links"
                ? "SELECT id,task_id,created_at_utc_ms,revision,label,uri,kind,deleted_at_utc_ms FROM task_links;"
                : "SELECT id,task_id,created_at_utc_ms,revision,prerequisite_task_id FROM task_dependencies;";
            await using var reader = await command.ExecuteReaderAsync();
            var count = 0;
            while (await reader.ReadAsync())
            {
                count++;
                var row = Find(json[section]!.AsArray(), Guid.Parse(reader.GetString(0)));
                Assert.Equal(Guid.Parse(reader.GetString(1)), row["TaskId"]!.GetValue<Guid>());
                Assert.Equal(reader.GetInt64(2), row["CreatedAtUtc"]!.GetValue<DateTimeOffset>().ToUnixTimeMilliseconds());
                Assert.Equal(reader.GetInt64(3), row["Revision"]!.GetValue<long>());
                if (table == "task_dependencies") Assert.Equal(Guid.Parse(reader.GetString(4)), row["PrerequisiteTaskId"]!.GetValue<Guid>());
                else
                {
                    Assert.Equal(reader.IsDBNull(4) ? null : reader.GetString(4), row["Label"]?.GetValue<string>());
                    Assert.Equal(reader.GetString(5), row["Uri"]!.GetValue<string>());
                    Assert.Equal(reader.GetString(6), row["Kind"]!.GetValue<string>());
                    Assert.Equal(reader.IsDBNull(7) ? (long?)null : reader.GetInt64(7), row["DeletedAtUtc"]?.GetValue<DateTimeOffset>().ToUnixTimeMilliseconds());
                }
            }
            Assert.Equal(count, json[section]!.AsArray().Count);
        }
    }

    internal static async Task<JsonObject> ExportAsync(IBackendClient backend, string path)
    {
        var result = await backend.ExportJsonAsync(path);
        Assert.Equal(5, result.SchemaVersion);
        var json = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        Assert.Equal(5, json["schemaVersion"]!.GetValue<int>());
        Assert.Equal(TimeSpan.Zero, json["exportedAtUtc"]!.GetValue<DateTimeOffset>().Offset);
        json.Remove("exportedAtUtc");
        return json;
    }

    private static JsonNode Find(JsonArray rows, Guid id) => Assert.Single(rows, row => row!["Id"]!.GetValue<Guid>() == id)!;
    private static void AssertNode<T>(T expected, JsonNode? actual)
    {
        // Older mutation results can retain sub-millisecond clock ticks. Exports
        // must match SQLite's explicitly millisecond-precision persisted values.
        var persisted = JsonSerializer.SerializeToNode(expected, PersistedJsonOptions);
        if (expected is TrackingSession) persisted!["DeletedAtUtc"] = null;
        Assert.True(JsonNode.DeepEquals(persisted, actual), $"Expected persisted data: {persisted}\nActual export: {actual}");
    }
    private static void AssertRecord<T>(T expected, JsonObject json, string section, Guid id) => AssertNode(expected, Find(json[section]!.AsArray(), id));
    private static OperationRequest Request(long? revision = null) => new(Guid.NewGuid(), Guid.NewGuid(), revision);

    private sealed class PersistedInstantConverter : System.Text.Json.Serialization.JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.GetDateTimeOffset();
        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
            => writer.WriteStringValue(DateTimeOffset.FromUnixTimeMilliseconds(value.ToUnixTimeMilliseconds()));
    }
}
