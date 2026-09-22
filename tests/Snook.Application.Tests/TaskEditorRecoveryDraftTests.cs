using Snook.Application;
using Snook.Contracts;
using Snook.Domain;
using Snook.Persistence.Sqlite;
using Snook.UI;
using Xunit;

namespace Snook.Application.Tests;

public sealed class TaskEditorRecoveryDraftTests
{
    [Fact]
    public void CodecBoundsExpiryAndWorkspaceIdentity()
    {
        var now = DateTimeOffset.UtcNow;
        var workspace = Guid.NewGuid();
        var draft = new TaskEditorRecoveryDraft(2, workspace, now, Guid.NewGuid(), 3,
            "Unsaved title", "Unsaved description", Priority.High, "2026-11-01",
            Guid.NewGuid(), true, Guid.NewGuid(), "tag", Guid.NewGuid(), "Reference",
            "https://example.test/item", "08:30", "America/Chicago", AmbiguousTimeChoice.Later);
        var encoded = draft.Encode();
        Assert.Equal(draft, TaskEditorRecoveryDraft.Decode(encoded, workspace, now));
        Assert.Null(TaskEditorRecoveryDraft.Decode(encoded, Guid.NewGuid(), now));
        Assert.Null(TaskEditorRecoveryDraft.Decode(encoded, workspace, now.AddDays(7)));
        Assert.Null(TaskEditorRecoveryDraft.Decode(encoded, workspace, now.AddSeconds(-1)));
        Assert.Null(TaskEditorRecoveryDraft.Decode(new byte[TaskEditorRecoveryDraft.MaximumEncodedBytes + 1], workspace, now));
        Assert.Null(TaskEditorRecoveryDraft.Decode("invalid"u8, workspace, now));
        var longestValid = (draft with { DraftDescription = new string('x', 20_000) }).Encode();
        Assert.InRange(longestValid.Length, 1, TaskEditorRecoveryDraft.MaximumEncodedBytes);
        Assert.Throws<InvalidDataException>(() => (draft with { DraftDescription = new string('x', 20_001) }).Encode());
        Assert.Throws<InvalidDataException>(() => (draft with { TaskId = Guid.Empty }).Encode());
    }

    [Fact]
    public async Task RestoresEveryTaskFieldAndPreservesConflictRevision()
    {
        var directory = Directory.CreateTempSubdirectory("snook-task-editor-recovery-");
        try
        {
            await using var backend = new SnookBackend(new SqliteStore(Path.Combine(directory.FullName, "workspace.db")));
            await backend.InitializeAsync();
            var bootstrap = await backend.GetBootstrapAsync();
            var workspace = bootstrap.Workspace.Id;
            var project = bootstrap.Projects[0];
            var otherProject = await backend.CreateProjectAsync(bootstrap.Boards[0].Id, "Recovery target");
            var activity = bootstrap.Activities[0];
            var task = await backend.CreateTaskAsync(project.Id, "Persisted title");
            var prerequisite = await backend.CreateTaskAsync(otherProject.Id, "Recovery prerequisite");
            TaskEditorRecoveryDraft recovery;
            await using (var first = new MainWindowViewModel(backend, ownsBackend: false))
            {
                await first.InitializeAsync();
                var source = first.Tasks.Single(row => row.Task.Id == task.Id);
                await ((AsyncCommand)source.ShowDetailsCommand).ExecuteAsync(null);
                var editor = first.TaskEditor!;
                editor.DraftTitle = "Recovered title";
                editor.DraftDescription = "Recovered description";
                editor.DraftPriority = Priority.Urgent;
                editor.DueDateText = "2026-11-01";
                editor.ReminderTimeText = "01:30";
                editor.ReminderTimeZone = "America/Chicago";
                editor.ReminderFoldChoice = AmbiguousTimeChoice.Later;
                editor.DraftActivityId = activity.Id;
                editor.DraftStarred = true;
                editor.TargetProjectId = otherProject.Id;
                editor.TagText = "recovery tag";
                editor.PrerequisiteTaskId = prerequisite.Id;
                editor.LinkLabelText = "Recovery reference";
                editor.LinkUriText = "https://example.test/recovery";
                recovery = first.CaptureTaskEditorDraft(workspace)!;
            }

            var changed = await backend.UpdateTaskAsync(task.Id,
                new TaskUpdate("Changed elsewhere", "Server version", Priority.Low, null, null, false),
                new OperationRequest(Guid.NewGuid(), Guid.NewGuid(), task.Revision));
            await using var second = new MainWindowViewModel(backend, ownsBackend: false);
            await second.InitializeAsync();
            Assert.True(await second.RestoreTaskEditorDraftAsync(recovery, workspace));
            Assert.False(await second.RestoreTaskEditorDraftAsync(recovery, workspace));
            var restored = second.TaskEditor!;
            Assert.Equal(recovery.ExpectedRevision, restored.Task.Revision);
            Assert.Equal("Recovered title", restored.DraftTitle);
            Assert.Equal("Recovered description", restored.DraftDescription);
            Assert.Equal(Priority.Urgent, restored.DraftPriority);
            Assert.Equal("2026-11-01", restored.DueDateText);
            Assert.Equal("01:30", restored.ReminderTimeText);
            Assert.Equal("America/Chicago", restored.ReminderTimeZone);
            Assert.Equal(AmbiguousTimeChoice.Later, restored.ReminderFoldChoice);
            Assert.Equal(activity.Id, restored.DraftActivityId);
            Assert.True(restored.DraftStarred);
            Assert.Equal(otherProject.Id, restored.TargetProjectId);
            Assert.Equal("recovery tag", restored.TagText);
            Assert.Equal(prerequisite.Id, restored.PrerequisiteTaskId);
            Assert.Equal("Recovery reference", restored.LinkLabelText);
            Assert.Equal("https://example.test/recovery", restored.LinkUriText);
            await ((AsyncCommand)restored.SaveCommand).ExecuteAsync(null);
            Assert.Same(restored, second.TaskEditor);
            Assert.Contains("draft", second.StatusMessage, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(changed, (await backend.GetTaskDetailsAsync(task.Id)).Task);
        }
        finally { directory.Delete(true); }
    }
}
