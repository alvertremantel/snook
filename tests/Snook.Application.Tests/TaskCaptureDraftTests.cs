using Snook.Contracts;
using Snook.Persistence.Sqlite;
using Snook.UI;
using System.Reflection;
using Xunit;

namespace Snook.Application.Tests;

public sealed class TaskCaptureDraftTests
{
    [Fact]
    public async Task FailedRecoveryCheckpointPreventsDispatchAndKeepsRetryIdentity()
    {
        var directory = Directory.CreateTempSubdirectory("snook-capture-checkpoint-");
        try
        {
            await using var backend = new SnookBackend(new SqliteStore(Path.Combine(directory.FullName, "workspace.db")));
            await backend.InitializeAsync();
            await using var model = new MainWindowViewModel(backend, ownsBackend: false);
            await model.InitializeAsync();
            model.TaskTitle = "Checkpoint before dispatch";
            OperationRequest? first = null;
            model.TaskCreationCheckpointAsync = (_, request) =>
            {
                first = request;
                throw new IOException("Synthetic unavailable storage");
            };
            await ((AsyncCommand)model.CreateTaskCommand).ExecuteAsync(null);
            Assert.Empty(await backend.SearchTasksAsync("Checkpoint before dispatch"));
            Assert.NotNull(first);
            model.TaskCreationCheckpointAsync = (_, request) =>
            {
                Assert.Equal(first, request);
                return Task.CompletedTask;
            };
            await ((AsyncCommand)model.CreateTaskCommand).ExecuteAsync(null);
            Assert.Single(await backend.SearchTasksAsync("Checkpoint before dispatch"));
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public void CodecBoundsAndScopesRecovery()
    {
        var now = DateTimeOffset.UtcNow;
        var workspace = Guid.NewGuid();
        var draft = new TaskCaptureDraft(1, workspace, now, new(Guid.NewGuid(), "Keep this draft"),
            new(Guid.NewGuid(), Guid.NewGuid()));
        var encoded = draft.Encode();
        Assert.Equal(draft, TaskCaptureDraft.Decode(encoded, workspace, now));
        Assert.Null(TaskCaptureDraft.Decode(encoded, Guid.NewGuid(), now));
        Assert.Null(TaskCaptureDraft.Decode(encoded, workspace, now.AddDays(7)));
        Assert.Null(TaskCaptureDraft.Decode(encoded, workspace, now.AddSeconds(-1)));
        Assert.Null(TaskCaptureDraft.Decode(new byte[4097], workspace, now));
        Assert.Null(TaskCaptureDraft.Decode("invalid"u8, workspace, now));
        Assert.Throws<InvalidDataException>(() => (draft with { Intent = draft.Intent with { Title = new string('x', 301) } }).Encode());
        Assert.Throws<InvalidDataException>(() => (draft with { PendingRequest = new(Guid.Empty, Guid.NewGuid()) }).Encode());
    }

    [Fact]
    public async Task RestoredUncertainCaptureReplaysTheCommittedCreation()
    {
        var directory = Directory.CreateTempSubdirectory("snook-capture-recovery-");
        try
        {
            await using var backend = new SnookBackend(new SqliteStore(Path.Combine(directory.FullName, "workspace.db")));
            await backend.InitializeAsync();
            var workspace = (await backend.GetBootstrapAsync()).Workspace.Id;
            var client = DispatchProxy.Create<IBackendClient, PresentationBackendProxy>();
            var proxy = (PresentationBackendProxy)client;
            proxy.Backend = backend;
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCaptureDraft recovered;
            await using (var first = new MainWindowViewModel(client, ownsBackend: false))
            {
                await first.InitializeAsync();
                first.TaskTitle = "Recover exactly once";
                proxy.BlockNextMethod = nameof(IBackendClient.CreateTaskAsync);
                proxy.BlockNextResponse = release.Task;
                var save = ((AsyncCommand)first.CreateTaskCommand).ExecuteAsync(null);
                await proxy.Captured.Task.WaitAsync(TimeSpan.FromSeconds(10));
                var draft = first.CaptureTaskDraft(workspace)!;
                Assert.NotNull(draft.PendingRequest);
                recovered = TaskCaptureDraft.Decode(draft.Encode(), workspace, DateTimeOffset.UtcNow)!;
                await first.DisposeAsync();
                release.SetResult();
                await save;
            }
            await using var second = new MainWindowViewModel(backend, ownsBackend: false);
            await second.InitializeAsync();
            Assert.True(second.RestoreTaskDraft(recovered, workspace));
            Assert.False(second.RestoreTaskDraft(recovered, workspace));
            await ((AsyncCommand)second.CreateTaskCommand).ExecuteAsync(null);
            Assert.Single(await backend.SearchTasksAsync("Recover exactly once"));
            Assert.Null(second.CaptureTaskDraft(workspace));
        }
        finally { directory.Delete(true); }
    }
}
