using Snook.Persistence.Sqlite;
using Snook.UI;
using Xunit;

namespace Snook.Application.Tests;

public sealed class EditorDiscardTests
{
    [Fact]
    public async Task ConfirmationRetainsDraftUntilAcceptedAndCannotCloseReplacement()
    {
        var directory = Directory.CreateTempSubdirectory("snook-editor-discard-");
        try
        {
            await using var backend = new SnookBackend(new SqliteStore(Path.Combine(directory.FullName, "workspace.db")));
            await backend.InitializeAsync();
            await using var model = new MainWindowViewModel(backend, ownsBackend: false);
            await model.InitializeAsync();
            Assert.Null(model.CreateEditorDiscardRequest());
            var task = await backend.CreateTaskAsync(model.SelectedProjectId, "Persisted task");
            await ((AsyncCommand)model.RefreshCommand).ExecuteAsync(null);
            var row = model.Tasks.Single(item => item.Task.Id == task.Id);
            await ((AsyncCommand)row.ShowDetailsCommand).ExecuteAsync(null);
            model.TaskEditor!.DraftTitle = "Unsaved edit";
            var taskDiscard = Assert.IsType<Action>(model.CreateEditorDiscardRequest());
            Assert.Equal("Unsaved edit", model.TaskEditor.DraftTitle);
            await ((AsyncCommand)model.CloseTaskEditorCommand).ExecuteAsync(null);
            await ((AsyncCommand)row.ShowDetailsCommand).ExecuteAsync(null);
            var replacement = model.TaskEditor;
            taskDiscard();
            Assert.Same(replacement, model.TaskEditor);
            Assert.IsType<Action>(model.CreateEditorDiscardRequest())();
            Assert.Null(model.TaskEditor);
            Assert.Equal("Persisted task", (await backend.GetTaskDetailsAsync(task.Id)).Task.Title);
            await ((AsyncCommand)model.OpenUtilityEditorCommand).ExecuteAsync("activity");
            model.NewActivityName = "Keep until confirmed";
            var discard = Assert.IsType<Action>(model.CreateEditorDiscardRequest());
            Assert.True(model.IsUtilityEditorOpen);
            Assert.Equal("Keep until confirmed", model.NewActivityName);
            await ((AsyncCommand)model.CloseUtilityEditorCommand).ExecuteAsync(null);
            await ((AsyncCommand)model.OpenUtilityEditorCommand).ExecuteAsync("activity");
            model.NewActivityName = "Replacement draft";
            discard();
            Assert.True(model.IsUtilityEditorOpen);
            Assert.Equal("Replacement draft", model.NewActivityName);
            var current = Assert.IsType<Action>(model.CreateEditorDiscardRequest());
            current();
            current();
            Assert.False(model.IsUtilityEditorOpen);
            Assert.Empty(model.NewActivityName);
            Assert.Null(model.CreateEditorDiscardRequest());
        }
        finally { directory.Delete(true); }
    }
}
