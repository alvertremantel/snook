using System.Reflection;
using Snook.Contracts;
using Snook.Domain;
using Snook.Persistence.Sqlite;
using Xunit;

namespace Snook.Application.Tests;

public sealed class WorkspaceRestoreSessionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuccessfulRestoreReplacesBorrowerAndNeverRevivesOldProjection(bool cancelAfterCommit)
    {
        var directory = Directory.CreateTempSubdirectory("snook-session-restore-");
        try
        {
            var raw = await OpenAsync(Path.Combine(directory.FullName, "live.db"));
            await using var session = new WorkspaceSession(_ => Task.FromResult<IBackendClient>(raw));
            var old = await session.OpenIndependentAsync();
            await using var oldProjection = new UI.MainWindowViewModel(old, ownsBackend: false);
            await oldProjection.InitializeAsync();
            oldProjection.TaskTitle = "Old projection draft";
            var incoming = await OpenAsync(Path.Combine(directory.FullName, "incoming.db"));
            await using (incoming)
            {
                await incoming.CreateBoardAsync("Incoming identity marker");
                await incoming.CreateBackupAsync(Path.Combine(directory.FullName, "backup.db"));
            }
            var path = Path.Combine(directory.FullName, "backup.db");
            var request = await RequestAsync(old, path);
            Assert.NotEqual(request.CurrentWorkspaceId, request.Incoming.Workspace.Id);
            var changes = new List<ChangeNotification>();
            old.Changed += (_, change) => changes.Add(change);
            using var cancellation = new CancellationTokenSource();
            raw.Changed += (_, change) => { if (cancelAfterCommit && change.ChangeKind == "restored") cancellation.Cancel(); };
            var outcome = await session.RestoreIndependentAsync(path, request, cancellation.Token);
            Assert.Equal(cancelAfterCommit, cancellation.IsCancellationRequested);
            Assert.Equal(request.Incoming.Workspace, outcome.Snapshot.Workspace);
            Assert.Equal(WorkspaceSessionState.ReadyIndependent, session.State);
            Assert.NotSame(old, outcome.Backend);
            Assert.Same(outcome.Backend, await session.OpenIndependentAsync());
            Assert.Contains(outcome.Snapshot.Boards, board => board.Name == "Incoming identity marker");
            Assert.Empty(changes);
            await Assert.ThrowsAsync<SnookException>(() => old.CreateBoardAsync("Old projection must not write"));
            await ((UI.AsyncCommand)oldProjection.CreateTaskCommand).ExecuteAsync(null);
            Assert.Equal("Old projection draft", oldProjection.TaskTitle);
            Assert.Empty(await outcome.Backend.SearchTasksAsync("Old projection draft"));
            await using var newProjection = new UI.MainWindowViewModel(outcome.Backend, ownsBackend: false);
            await newProjection.InitializeAsync();
            Assert.Equal(outcome.Snapshot.Workspace.Name, newProjection.WorkspaceName);
            newProjection.TaskTitle = "New projection task";
            await ((UI.AsyncCommand)newProjection.CreateTaskCommand).ExecuteAsync(null);
            Assert.Single(await outcome.Backend.SearchTasksAsync("New projection task"));
            await outcome.Backend.CreateBoardAsync("New projection can write");
            Assert.Empty(changes);
            Assert.Single(Directory.GetFiles(directory.FullName, "live.db.before-restore-*"));
            Assert.True(File.Exists(Path.Combine(directory.FullName, "live.db.owner")));
            await Assert.ThrowsAsync<SnookException>(() => outcome.Backend.RestoreBackupAsync(path));
        }
        finally { directory.Delete(true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StaleConfirmationPreservesCurrentWorkspaceAndBorrower(bool changedBackup)
    {
        var directory = Directory.CreateTempSubdirectory("snook-session-stale-");
        try
        {
            await using var session = new WorkspaceSession(async _ => await OpenAsync(Path.Combine(directory.FullName, "live.db")));
            var old = await session.OpenIndependentAsync();
            var path = Path.Combine(directory.FullName, "backup.db");
            await old.CreateBackupAsync(path);
            var request = await RequestAsync(old, path);
            if (changedBackup) request = request with { Incoming = request.Incoming with { Sha256 = new string('0', 64) } };
            else await old.CreateBoardAsync("Changed after confirmation");
            var before = await old.GetBootstrapAsync();
            var error = await Assert.ThrowsAsync<SnookException>(() => session.RestoreIndependentAsync(path, request));
            Assert.Equal(SnookErrorCode.RevisionConflict, error.Code);
            Assert.Same(old, await session.OpenIndependentAsync());
            Assert.Equal(before.CommittedCursor, (await old.GetBootstrapAsync()).CommittedCursor);
            Assert.Empty(Directory.GetFiles(directory.FullName, "live.db.before-restore-*"));
        }
        finally { directory.Delete(true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdmissionStopsAndDrainsExistingCallsBeforeRestore(bool cancelDrain)
    {
        var directory = Directory.CreateTempSubdirectory("snook-session-drain-");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var raw = await OpenAsync(Path.Combine(directory.FullName, "live.db"));
            var client = DispatchProxy.Create<IBackendClient, PresentationBackendProxy>();
            var proxy = (PresentationBackendProxy)(object)client;
            proxy.Backend = raw;
            await using var session = new WorkspaceSession(_ => Task.FromResult(client));
            var old = await session.OpenIndependentAsync();
            var path = Path.Combine(directory.FullName, "backup.db");
            await old.CreateBackupAsync(path);
            var request = await RequestAsync(old, path);
            proxy.BlockNextMethod = nameof(IBackendClient.CreateBoardAsync);
            proxy.BlockNextResponse = release.Task;
            var writing = old.CreateBoardAsync("Committed before drain");
            await proxy.Captured.Task.WaitAsync(TimeSpan.FromSeconds(10));
            using var cancellation = new CancellationTokenSource();
            var restoring = session.RestoreIndependentAsync(path, request, cancellation.Token);
            Assert.Equal(WorkspaceSessionState.Restoring, session.State);
            Assert.False(restoring.IsCompleted);
            await Assert.ThrowsAsync<SnookException>(() => old.CreateBoardAsync("Must not enter while draining"));
            if (cancelDrain)
            {
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => restoring);
            }
            release.TrySetResult();
            await writing;
            if (!cancelDrain)
                Assert.Equal(SnookErrorCode.RevisionConflict, (await Assert.ThrowsAsync<SnookException>(() => restoring)).Code);
            Assert.Equal(WorkspaceSessionState.ReadyIndependent, session.State);
            Assert.Same(old, await session.OpenIndependentAsync());
            Assert.Contains((await old.GetBootstrapAsync()).Boards, board => board.Name == "Committed before drain");
            Assert.Empty(Directory.GetFiles(directory.FullName, "live.db.before-restore-*"));
        }
        finally { release.TrySetResult(); directory.Delete(true); }
    }

    [Fact]
    public async Task LostRestoreResponseFailsClosedAndRetainsOwnerUntilExplicitDisposal()
    {
        var directory = Directory.CreateTempSubdirectory("snook-session-uncertain-");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var database = Path.Combine(directory.FullName, "live.db");
            var raw = await OpenAsync(database);
            var client = DispatchProxy.Create<IBackendClient, PresentationBackendProxy>();
            var proxy = (PresentationBackendProxy)(object)client;
            proxy.Backend = raw;
            await using var session = new WorkspaceSession(_ => Task.FromResult(client));
            var old = await session.OpenIndependentAsync();
            var path = Path.Combine(directory.FullName, "backup.db");
            await old.CreateBackupAsync(path);
            var request = await RequestAsync(old, path);
            proxy.BlockNextMethod = nameof(IBackendClient.RestoreBackupAsync);
            proxy.BlockNextResponse = release.Task;
            var restoring = session.RestoreIndependentAsync(path, request);
            await proxy.Captured.Task.WaitAsync(TimeSpan.FromSeconds(10));
            release.SetException(new IOException("Lost committed response"));
            await Assert.ThrowsAsync<IOException>(() => restoring);
            Assert.Equal(WorkspaceSessionState.Fatal, session.State);
            await Assert.ThrowsAsync<SnookException>(() => old.GetBootstrapAsync());
            await Assert.ThrowsAsync<SnookException>(() => session.OpenIndependentAsync());
            Assert.True(File.Exists(database + ".owner"));
            await session.DisposeAsync();
            await using var recovered = await OpenAsync(database);
            Assert.Equal(request.Incoming.Workspace, (await recovered.GetBootstrapAsync()).Workspace);
        }
        finally { release.TrySetResult(); directory.Delete(true); }
    }

    private static async Task<WorkspaceRestoreRequest> RequestAsync(IBackendClient backend, string path)
    {
        var snapshot = await backend.GetBootstrapAsync();
        return new WorkspaceRestoreRequest(snapshot.Workspace.Id, snapshot.CommittedCursor, await backend.InspectBackupAsync(path));
    }

    private static async Task<SnookBackend> OpenAsync(string path)
    {
        var backend = new SnookBackend(new SqliteStore(path));
        await backend.InitializeAsync();
        return backend;
    }
}
