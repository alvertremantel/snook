using Snook.Contracts;
using Snook.Domain;
using Snook.Persistence.Sqlite;
using System.Reflection;
using Xunit;

namespace Snook.Application.Tests;

public sealed class WorkspaceSessionTests
{
    [Fact]
    public async Task LockStateIsObservableWithoutOpeningASecondOwner()
    {
        var directory = Directory.CreateTempSubdirectory("snook-session-lock-");
        try
        {
            var opens = 0;
            var states = new List<WorkspaceSessionState>();
            await using var session = new WorkspaceSession(async token =>
            {
                opens++;
                return await OpenAsync(directory.FullName, token);
            });
            session.StateChanged += (_, state) => states.Add(state);
            await session.SetLockedAsync(true);
            Assert.Equal(WorkspaceSessionState.Locked, session.State);
            var backend = await session.OpenIndependentAsync();
            Assert.Equal(WorkspaceSessionState.Locked, session.State);
            Assert.Same(backend, await session.OpenIndependentAsync());
            Assert.Equal(1, opens);
            await session.SetLockedAsync(false);
            Assert.Equal(WorkspaceSessionState.ReadyIndependent, session.State);
            await session.SetLockedAsync(true);
            Assert.Equal(WorkspaceSessionState.Locked, session.State);
            await session.ResetAsync();
            Assert.Equal(WorkspaceSessionState.Locked, session.State);
            await session.SetLockedAsync(false);
            Assert.Equal(WorkspaceSessionState.Unconfigured, session.State);
            Assert.Contains(WorkspaceSessionState.Locked, states);
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public async Task ResetDrainsAndAllowsAnExplicitDifferentHostOpen()
    {
        var firstDirectory = Directory.CreateTempSubdirectory("snook-session-reset-first-");
        var secondDirectory = Directory.CreateTempSubdirectory("snook-session-reset-second-");
        try
        {
            var first = await OpenAsync(firstDirectory.FullName, CancellationToken.None);
            var secondBackend = await OpenAsync(secondDirectory.FullName, CancellationToken.None);
            var second = DispatchProxy.Create<IBackendClient, AttachedBackendProxy>();
            ((AttachedBackendProxy)second).Backend = secondBackend;
            var independentOpens = 0;
            await using var session = new WorkspaceSession(_ =>
            {
                independentOpens++;
                return Task.FromResult(first);
            });

            var old = await session.OpenIndependentAsync();
            await session.ResetAsync();

            Assert.Equal(WorkspaceSessionState.Unconfigured, session.State);
            await Assert.ThrowsAsync<SnookException>(() => old.GetBootstrapAsync());
            var attached = await session.OpenAttachedAsync(_ => Task.FromResult(second));
            Assert.Equal(WorkspaceSessionState.ReadyAttached, session.State);
            Assert.NotSame(old, attached);
            Assert.Equal(1, independentOpens);
        }
        finally
        {
            firstDirectory.Delete(true);
            secondDirectory.Delete(true);
        }
    }

    [Fact]
    public async Task BorrowedProjectionDisposalDoesNotCloseSessionBackend()
    {
        var directory = Directory.CreateTempSubdirectory("snook-session-projection-");
        try
        {
            await using var session = new WorkspaceSession(token => OpenAsync(directory.FullName, token));
            var backend = await session.OpenIndependentAsync();
            var projection = new UI.MainWindowViewModel(backend, ownsBackend: false);
            await projection.DisposeAsync();
            Assert.Same(backend, await session.OpenIndependentAsync());
            Assert.NotNull(await backend.GetBootstrapAsync());
            Assert.True(File.Exists(Path.Combine(directory.FullName, "workspace.db.owner")));
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public async Task ConcurrentActivityOpensShareOneOwnerAndObserversCannotBreakStartup()
    {
        var directory = Directory.CreateTempSubdirectory("snook-session-shared-");
        try
        {
            var count = 0;
            var states = new List<WorkspaceSessionState>();
            await using var session = new WorkspaceSession(async cancellation =>
            {
                Interlocked.Increment(ref count);
                return await OpenAsync(directory.FullName, cancellation);
            });
            session.StateChanged += (_, _) => throw new InvalidOperationException("broken observer");
            session.StateChanged += (_, state) => states.Add(state);
            Assert.Equal(WorkspaceSessionState.Unconfigured, session.State);
            var clients = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => session.OpenIndependentAsync()));
            Assert.Equal(1, count);
            Assert.All(clients, client => Assert.Same(clients[0], client));
            Assert.Equal([WorkspaceSessionState.Connecting, WorkspaceSessionState.ReadyIndependent], states);
            await session.DisposeAsync();
            Assert.Equal(WorkspaceSessionState.Closing, session.State);
            Assert.False(File.Exists(Path.Combine(directory.FullName, "workspace.db.owner")));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => session.OpenIndependentAsync());
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public async Task FailedStartupIsObservableAndExplicitRetryUsesSameFactory()
    {
        var directory = Directory.CreateTempSubdirectory("snook-session-retry-");
        try
        {
            var attempts = 0;
            await using var session = new WorkspaceSession(cancellation =>
                ++attempts == 1 ? throw new IOException("unavailable") : OpenAsync(directory.FullName, cancellation));
            await Assert.ThrowsAsync<IOException>(() => session.OpenIndependentAsync());
            Assert.Equal(WorkspaceSessionState.Fatal, session.State);
            var backend = await session.OpenIndependentAsync();
            Assert.NotNull(await backend.GetBootstrapAsync());
            Assert.Equal(WorkspaceSessionState.ReadyIndependent, session.State);
            Assert.Equal(2, attempts);
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public async Task CancelledWaiterDoesNotCancelTheOpeningOwnerAndCloseWaitsForStartup()
    {
        var directory = Directory.CreateTempSubdirectory("snook-session-close-");
        try
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var session = new WorkspaceSession(async cancellation =>
            {
                entered.SetResult();
                await release.Task.WaitAsync(cancellation);
                return await OpenAsync(directory.FullName, cancellation);
            });
            var opening = session.OpenIndependentAsync();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            using var cancellation = new CancellationTokenSource();
            var waiting = session.OpenIndependentAsync(cancellation.Token);
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
            Assert.Equal(WorkspaceSessionState.Connecting, session.State);
            var closing = session.DisposeAsync().AsTask();
            Assert.False(closing.IsCompleted);
            release.SetResult();
            await opening;
            await closing;
            Assert.Equal(WorkspaceSessionState.Closing, session.State);
            await using var replacement = await OpenAsync(directory.FullName, CancellationToken.None);
            Assert.NotNull(await replacement.GetBootstrapAsync());
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public async Task CancelledCandidateIsDisposedBeforeRetry()
    {
        var directory = Directory.CreateTempSubdirectory("snook-session-cancel-");
        try
        {
            using var cancellation = new CancellationTokenSource();
            var attempts = 0;
            await using var session = new WorkspaceSession(async token =>
            {
                var backend = await OpenAsync(directory.FullName, token);
                if (++attempts == 1) await cancellation.CancelAsync();
                return backend;
            });
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.OpenIndependentAsync(cancellation.Token));
            Assert.Equal(WorkspaceSessionState.Fatal, session.State);
            Assert.False(File.Exists(Path.Combine(directory.FullName, "workspace.db.owner")));
            Assert.NotNull(await session.OpenIndependentAsync());
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public async Task AttachedStartupIsCapabilityCheckedRetryableAndNeverUsesIndependentFactory()
    {
        var directory = Directory.CreateTempSubdirectory("snook-session-attached-");
        try
        {
            var independentCalls = 0;
            var attachedCalls = 0;
            await using var session = new WorkspaceSession(_ =>
            {
                independentCalls++;
                throw new InvalidOperationException("Independent fallback must not run.");
            });
            async Task<IBackendClient> OpenAttached(CancellationToken cancellation)
            {
                attachedCalls++;
                if (attachedCalls == 1) throw new IOException("tailnet unavailable");
                var backend = await OpenAsync(directory.FullName, cancellation);
                var client = DispatchProxy.Create<IBackendClient, AttachedBackendProxy>();
                ((AttachedBackendProxy)client).Backend = backend;
                return client;
            }

            await Assert.ThrowsAsync<IOException>(() => session.OpenAttachedAsync(OpenAttached));
            Assert.Equal(WorkspaceSessionState.StaleAttached, session.State);
            Assert.Equal(0, independentCalls);

            var attached = await session.OpenAttachedAsync(OpenAttached);
            Assert.Equal(WorkspaceSessionState.ReadyAttached, session.State);
            Assert.True((await attached.GetBootstrapAsync()).Capabilities.DaemonClient);
            Assert.Same(attached, await session.OpenAttachedAsync(OpenAttached));
            Assert.Equal(2, attachedCalls);
            Assert.Equal(0, independentCalls);
            var error = await Assert.ThrowsAsync<SnookException>(() => session.OpenIndependentAsync());
            Assert.Equal(SnookErrorCode.InvalidTransition, error.Code);
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public async Task AttachedStartupRejectsEmbeddedCapabilityWithoutPublishingBackend()
    {
        var directory = Directory.CreateTempSubdirectory("snook-session-attached-shape-");
        try
        {
            await using var session = new WorkspaceSession(_ => throw new InvalidOperationException());
            await Assert.ThrowsAsync<SnookException>(() => session.OpenAttachedAsync(
                token => OpenAsync(directory.FullName, token)));
            Assert.Equal(WorkspaceSessionState.StaleAttached, session.State);
            Assert.False(File.Exists(Path.Combine(directory.FullName, "workspace.db.owner")));
        }
        finally { directory.Delete(true); }
    }

    private static async Task<IBackendClient> OpenAsync(string directory, CancellationToken cancellation)
    {
        var backend = new SnookBackend(new SqliteStore(Path.Combine(directory, "workspace.db")));
        try
        {
            await backend.InitializeAsync(cancellation);
            return backend;
        }
        catch
        {
            await backend.DisposeAsync();
            throw;
        }
    }

    public class AttachedBackendProxy : DispatchProxy
    {
        public IBackendClient Backend { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            if (targetMethod.Name == nameof(IBackendClient.GetBootstrapAsync))
                return BootstrapAsync(args?.OfType<CancellationToken>().LastOrDefault() ?? default);
            return targetMethod.Invoke(Backend, args);
        }

        private async Task<BootstrapSnapshot> BootstrapAsync(CancellationToken cancellationToken)
        {
            var snapshot = await Backend.GetBootstrapAsync(cancellationToken);
            return snapshot with
            {
                Capabilities = snapshot.Capabilities with
                {
                    HostMode = "daemon-client",
                    Embedded = false,
                    DaemonClient = true
                }
            };
        }
    }
}
