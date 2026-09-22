using Snook.Application;
using Snook.Contracts;
using Snook.Domain;
using Snook.Persistence.Sqlite;
using Snook.UI;
using Xunit;

namespace Snook.Application.Tests;

public sealed class UtilityEditorRecoveryDraftTests
{
    [Fact]
    public void CodecIsBoundedExpiredAndWorkspaceScoped()
    {
        var now = DateTimeOffset.UtcNow;
        var workspace = Guid.NewGuid();
        var request = new OperationRequest(Guid.NewGuid(), Guid.NewGuid());
        var draft = new UtilityEditorRecoveryDraft(1, workspace, now, "activity", null, null,
            new Dictionary<string, string?>
            {
                ["name"] = "Recovered activity",
                ["description"] = "Private draft",
                ["lane"] = "0",
                ["group"] = null
            }, [], request);

        var encoded = draft.Encode();
        var decoded = Assert.IsType<UtilityEditorRecoveryDraft>(
            UtilityEditorRecoveryDraft.Decode(encoded, workspace, now));
        Assert.Equal(draft.Kind, decoded.Kind);
        Assert.Equal(draft.PendingRequest, decoded.PendingRequest);
        Assert.Equal(draft.Values.OrderBy(pair => pair.Key), decoded.Values.OrderBy(pair => pair.Key));
        Assert.Null(UtilityEditorRecoveryDraft.Decode(encoded, Guid.NewGuid(), now));
        Assert.Null(UtilityEditorRecoveryDraft.Decode(encoded, workspace, now.AddDays(7)));
        Assert.Null(UtilityEditorRecoveryDraft.Decode(encoded, workspace, now.AddSeconds(-1)));
        Assert.Null(UtilityEditorRecoveryDraft.Decode(
            new byte[UtilityEditorRecoveryDraft.MaximumEncodedBytes + 1], workspace, now));
        Assert.Null(UtilityEditorRecoveryDraft.Decode("invalid"u8, workspace, now));
        Assert.Throws<InvalidDataException>(() => (draft with
        {
            Values = new Dictionary<string, string?> { ["content"] = new string('x', 24_001) }
        }).Encode());
        Assert.Throws<InvalidDataException>(() => (draft with
        {
            EntityId = Guid.NewGuid(), ExpectedRevision = null
        }).Encode());
        Assert.Throws<InvalidDataException>(() => (draft with
        {
            PendingRequest = request with { ExpectedRevision = 2 }
        }).Encode());
    }

    [Fact]
    public async Task CreationCheckpointSurvivesProcessDeathAndReusesExactRequest()
    {
        var directory = Directory.CreateTempSubdirectory("snook-utility-create-recovery-");
        try
        {
            await using var backend = new SnookBackend(new SqliteStore(
                Path.Combine(directory.FullName, "workspace.db")));
            await backend.InitializeAsync();
            var workspace = (await backend.GetBootstrapAsync()).Workspace.Id;
            UtilityEditorRecoveryDraft recovery;

            await using (var first = new MainWindowViewModel(backend, ownsBackend: false))
            {
                await first.InitializeAsync();
                await ((AsyncCommand)first.OpenUtilityEditorCommand).ExecuteAsync("activity");
                first.NewActivityName = "Recovered exact activity";
                first.NewActivityDescription = "Survives an uncertain dispatch";
                first.NewActivityLane = SessionLane.Background;
                first.UtilityEditorCheckpointAsync = () => throw new IOException("Synthetic storage outage");
                await ((AsyncCommand)first.CreateActivityCommand).ExecuteAsync(null);
                recovery = Assert.IsType<UtilityEditorRecoveryDraft>(
                    first.CaptureUtilityEditorDraft(workspace));
                Assert.NotNull(recovery.PendingRequest);
                Assert.DoesNotContain((await backend.GetBootstrapAsync()).Activities,
                    activity => activity.Name == "Recovered exact activity");
            }

            await using var second = new MainWindowViewModel(backend, ownsBackend: false);
            await second.InitializeAsync();
            Assert.True(await second.RestoreUtilityEditorDraftAsync(recovery, workspace));
            Assert.Equal("Recovered exact activity", second.NewActivityName);
            Assert.Equal(SessionLane.Background, second.NewActivityLane);
            OperationRequest? checkpoint = null;
            second.UtilityEditorCheckpointAsync = () =>
            {
                checkpoint = second.CaptureUtilityEditorDraft(workspace)?.PendingRequest;
                return Task.CompletedTask;
            };
            await ((AsyncCommand)second.CreateActivityCommand).ExecuteAsync(null);
            Assert.Equal(recovery.PendingRequest, checkpoint);
            Assert.Single((await backend.GetBootstrapAsync()).Activities,
                activity => activity.Name == "Recovered exact activity");
            Assert.False(second.IsUtilityEditorOpen);
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public async Task TimedCreationRecoveryDoesNotReinterpretItsCapturedTimeZone()
    {
        var directory = Directory.CreateTempSubdirectory("snook-utility-time-recovery-");
        try
        {
            var clock = new MutableCreateClock(new DateTimeOffset(2026, 9, 20, 12, 0, 0,
                TimeSpan.Zero)) { Zone = TimeZoneInfo.Utc };
            await using var backend = new SnookBackend(new SqliteStore(
                Path.Combine(directory.FullName, "workspace.db")), clock);
            await backend.InitializeAsync();
            var activity = await backend.CreateActivityAsync("Recovered timed activity");
            var workspace = (await backend.GetBootstrapAsync()).Workspace.Id;
            UtilityEditorRecoveryDraft recovery;

            await using (var first = new MainWindowViewModel(backend, clock,
                             ownsBackend: false))
            {
                await first.InitializeAsync();
                await ((AsyncCommand)first.OpenUtilityEditorCommand).ExecuteAsync("manual");
                first.ManualActivityId = activity.Id;
                first.ManualStartText = "2026-09-20 10:00";
                first.ManualEndText = "2026-09-20 11:00";
                first.UtilityEditorCheckpointAsync = () => throw new IOException("Synthetic storage outage");
                await ((AsyncCommand)first.ManualTimeCommand).ExecuteAsync(null);
                recovery = Assert.IsType<UtilityEditorRecoveryDraft>(
                    first.CaptureUtilityEditorDraft(workspace));
                Assert.Equal("2026-09-20T10:00:00.0000000+00:00",
                    recovery.Values["capturedStart"]);
            }

            clock.Zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
            await using var second = new MainWindowViewModel(backend, clock,
                ownsBackend: false);
            await second.InitializeAsync();
            Assert.True(await second.RestoreUtilityEditorDraftAsync(recovery, workspace));
            second.UtilityEditorCheckpointAsync = () => Task.CompletedTask;
            await ((AsyncCommand)second.ManualTimeCommand).ExecuteAsync(null);
            var history = await backend.GetHistoryAsync(new HistoryQuery(
                new DateTimeOffset(2026, 9, 19, 0, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 9, 22, 0, 0, 0, TimeSpan.Zero)));
            var saved = Assert.Single(history.Items).Session;
            Assert.Equal(new DateTimeOffset(2026, 9, 20, 10, 0, 0, TimeSpan.Zero),
                saved.StartedAtUtc);
            Assert.Equal(new DateTimeOffset(2026, 9, 20, 11, 0, 0, TimeSpan.Zero),
                saved.StoppedAtUtc);
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public async Task JournalEntryRecoveryKeepsSensitiveContentAndConflictRevision()
    {
        var directory = Directory.CreateTempSubdirectory("snook-utility-journal-recovery-");
        try
        {
            await using var backend = new SnookBackend(new SqliteStore(
                Path.Combine(directory.FullName, "workspace.db")));
            await backend.InitializeAsync();
            var journal = await backend.CreateJournalAsync(new JournalDefinition("Recovery journal"),
                Request());
            var entry = await backend.CreateJournalEntryAsync(new JournalEntryDefinition(journal.Id,
                "Saved title", "Saved content", DateTimeOffset.UtcNow, null, []), Request());
            var workspace = (await backend.GetBootstrapAsync()).Workspace.Id;
            UtilityEditorRecoveryDraft recovery;

            await using (var first = new MainWindowViewModel(backend, ownsBackend: false))
            {
                await first.InitializeAsync();
                await first.SelectSectionForScreenshotAsync("Journals", null);
                await ((AsyncCommand)first.JournalEntries.Single(row => row.Entry.Id == entry.Id)
                    .OpenCommand).ExecuteAsync(null);
                var editor = Assert.IsType<JournalEntryEditorViewModel>(first.UtilityDraft);
                editor.Title = "Recovered private title";
                editor.Content = "Sensitive text that must survive process death.";
                editor.TagsText = "private, recovery";
                recovery = Assert.IsType<UtilityEditorRecoveryDraft>(
                    first.CaptureUtilityEditorDraft(workspace));
            }

            var changed = await backend.UpdateJournalEntryAsync(entry.Id,
                new JournalEntryDefinition(journal.Id, "Changed elsewhere", "Server content",
                    entry.OccurredAtUtc, null, []), Request(entry.Revision));

            await using var second = new MainWindowViewModel(backend, ownsBackend: false);
            await second.InitializeAsync();
            await second.SelectSectionForScreenshotAsync("Journals", null);
            Assert.True(await second.RestoreUtilityEditorDraftAsync(recovery, workspace));
            var restored = Assert.IsType<JournalEntryEditorViewModel>(second.UtilityDraft);
            Assert.Equal(entry.Revision, restored.Entry!.Revision);
            Assert.Equal("Recovered private title", restored.Title);
            Assert.Equal("Sensitive text that must survive process death.", restored.Content);
            Assert.Equal("private, recovery", restored.TagsText);
            await ((AsyncCommand)restored.SaveCommand).ExecuteAsync(null);
            Assert.Same(restored, second.UtilityDraft);
            var persisted = await backend.GetJournalEntryAsync(entry.Id);
            Assert.Equal(changed.Id, persisted.Id);
            Assert.Equal(changed.Title, persisted.Title);
            Assert.Equal(changed.Content, persisted.Content);
            Assert.Equal(changed.Revision, persisted.Revision);
            Assert.Equal(changed.Tags, persisted.Tags);
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public async Task BatchRecoveryRetainsEveryExpectedRevision()
    {
        var directory = Directory.CreateTempSubdirectory("snook-utility-batch-recovery-");
        try
        {
            await using var backend = new SnookBackend(new SqliteStore(
                Path.Combine(directory.FullName, "workspace.db")));
            await backend.InitializeAsync();
            var bootstrap = await backend.GetBootstrapAsync();
            var firstTask = await backend.CreateTaskAsync(bootstrap.Projects[0].Id, "First recovered batch task");
            var secondTask = await backend.CreateTaskAsync(bootstrap.Projects[0].Id, "Second recovered batch task");
            UtilityEditorRecoveryDraft recovery;

            await using (var first = new MainWindowViewModel(backend, ownsBackend: false))
            {
                await first.InitializeAsync();
                await first.SelectSectionForScreenshotAsync("Tasks", null);
                first.Tasks.Single(row => row.Task.Id == firstTask.Id).IsSelected = true;
                first.Tasks.Single(row => row.Task.Id == secondTask.Id).IsSelected = true;
                await ((AsyncCommand)first.OpenBulkTaskEditorCommand).ExecuteAsync(null);
                var editor = Assert.IsType<BulkTaskEditorViewModel>(first.UtilityDraft);
                editor.ChangeDescription = true;
                editor.Description = "Recovered batch description";
                recovery = Assert.IsType<UtilityEditorRecoveryDraft>(
                    first.CaptureUtilityEditorDraft(bootstrap.Workspace.Id));
                Assert.Equal(2, recovery.TargetRevisions.Count);
            }

            var changed = await backend.UpdateTaskAsync(firstTask.Id,
                new TaskUpdate(firstTask.Title, "Changed elsewhere", firstTask.Priority,
                    firstTask.DueDate, firstTask.DefaultActivityId, firstTask.Starred),
                Request(firstTask.Revision));

            await using var second = new MainWindowViewModel(backend, ownsBackend: false);
            await second.InitializeAsync();
            await second.SelectSectionForScreenshotAsync("Tasks", null);
            Assert.True(await second.RestoreUtilityEditorDraftAsync(recovery,
                bootstrap.Workspace.Id));
            var restored = Assert.IsType<BulkTaskEditorViewModel>(second.UtilityDraft);
            Assert.Equal(recovery.TargetRevisions,
                restored.Targets.Select(task => new TaskRevision(task.Id, task.Revision)));
            await ((AsyncCommand)restored.SaveCommand).ExecuteAsync(null);
            Assert.Same(restored, second.UtilityDraft);
            Assert.Equal("Changed elsewhere",
                (await backend.GetTaskDetailsAsync(changed.Id)).Task.Description);
            Assert.Equal(string.Empty,
                (await backend.GetTaskDetailsAsync(secondTask.Id)).Task.Description);
        }
        finally { directory.Delete(true); }
    }

    private static OperationRequest Request(long? revision = null)
        => new(Guid.NewGuid(), Guid.NewGuid(), revision);
}
