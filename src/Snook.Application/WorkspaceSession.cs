using Snook.Contracts;
using Snook.Domain;

namespace Snook.Application;

public enum WorkspaceSessionState
{
    Unconfigured,
    Connecting,
    ReadyIndependent,
    ReadyAttached,
    StaleAttached,
    Locked,
    Closing,
    Fatal,
    Restoring
}

public sealed record WorkspaceRestoreRequest(Guid CurrentWorkspaceId, long CurrentCursor, BackupInspection Incoming);
public sealed record WorkspaceRestoreOutcome(IBackendClient Backend, BootstrapSnapshot Snapshot, RestoreResult Result);

/// <summary>
/// Process-scoped owner of exactly one independent or attached workspace connection.
/// Views borrow the connection; they must not dispose it when their activity is
/// recreated. Host switching requires closing this session first.
/// </summary>
public sealed class WorkspaceSession : IAsyncDisposable
{
    private readonly SemaphoreSlim _transitions = new(1, 1);
    private readonly Func<CancellationToken, Task<IBackendClient>> _openIndependent;
    private IBackendClient? _backend;
    private WorkspaceBackendClient? _borrowed;
    private volatile WorkspaceSessionState _state;
    private bool _closed;
    private bool _handoffFailed;
    private bool _locked;
    private WorkspaceSessionState _readyState;

    /// <param name="openIndependent">
    /// Opens and initializes a backend. Ownership transfers on successful return;
    /// the factory must dispose partially created resources if it throws.
    /// </param>
    public WorkspaceSession(Func<CancellationToken, Task<IBackendClient>> openIndependent)
    {
        ArgumentNullException.ThrowIfNull(openIndependent);
        _openIndependent = openIndependent;
    }

    public WorkspaceSessionState State => _state;

    /// <summary>
    /// Notifications run synchronously on the transition thread. UI subscribers
    /// must dispatch to their UI thread and must not synchronously wait on a transition.
    /// </summary>
    public event EventHandler<WorkspaceSessionState>? StateChanged;

    /// <summary>
    /// Reuses the live backend after activity recreation. Cancellation cancels this
    /// attempt, never another caller's pending attempt. A failed open may be retried
    /// explicitly. No alternate host or transport is selected on failure.
    /// </summary>
    public async Task<IBackendClient> OpenIndependentAsync(CancellationToken cancellationToken = default)
        => await OpenAsync(_openIndependent, WorkspaceSessionState.ReadyIndependent,
            WorkspaceSessionState.Fatal, daemonRequired: false, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Opens one explicitly configured remote backend without invoking the independent
    /// factory. Initial reachability/authentication failures remain retryable in
    /// <see cref="WorkspaceSessionState.StaleAttached"/> and never fall back to SQLite.
    /// </summary>
    public async Task<IBackendClient> OpenAttachedAsync(
        Func<CancellationToken, Task<IBackendClient>> openAttached,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(openAttached);
        return await OpenAsync(openAttached, WorkspaceSessionState.ReadyAttached,
            WorkspaceSessionState.StaleAttached, daemonRequired: true, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IBackendClient> OpenAsync(Func<CancellationToken, Task<IBackendClient>> factory,
        WorkspaceSessionState readyState, WorkspaceSessionState failureState, bool daemonRequired,
        CancellationToken cancellationToken)
    {
        await _transitions.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (_handoffFailed) throw new SnookException(SnookErrorCode.StoreUnavailable,
                "Workspace handoff needs recovery. Close this session and inspect the workspace before reopening.");
            if (_borrowed is not null)
            {
                if (_state == readyState || _state == WorkspaceSessionState.Locked
                    && _readyState == readyState) return _borrowed;
                throw new SnookException(SnookErrorCode.InvalidTransition,
                    "Close the current workspace session before selecting another host.");
            }
            SetState(WorkspaceSessionState.Connecting);
            IBackendClient? candidate = null;
            try
            {
                candidate = await factory(cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The workspace factory returned no backend.");
                // Read committed state before publishing a connection as ready.
                var snapshot = await candidate.GetBootstrapAsync(cancellationToken).ConfigureAwait(false);
                if (snapshot.Capabilities.DaemonClient != daemonRequired)
                    throw new SnookException(SnookErrorCode.ValidationFailed, daemonRequired
                        ? "Attached workspace setup returned a non-daemon backend. No local fallback was accepted."
                        : "Independent workspace setup returned a daemon backend.");
                cancellationToken.ThrowIfCancellationRequested();
                _backend = candidate;
                _borrowed = new WorkspaceBackendClient(candidate);
                _readyState = readyState;
                SetState(_locked ? WorkspaceSessionState.Locked : readyState);
                return _borrowed;
            }
            catch
            {
                // Cleanup failure is terminal: opening another owner would be unsafe.
                try
                {
                    if (candidate is not null) await candidate.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                    _closed = true;
                    SetState(WorkspaceSessionState.Fatal);
                    throw;
                }
                SetState(failureState);
                throw;
            }
        }
        finally { _transitions.Release(); }
    }

    /// <summary>
    /// Changes only presentation/security admission state. The process-owned backend
    /// remains open so activity recreation cannot create a competing store owner.
    /// Native surfaces and UI visibility are the platform adapter's responsibility.
    /// </summary>
    public async Task SetLockedAsync(bool locked, CancellationToken cancellationToken = default)
    {
        await _transitions.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (_locked == locked) return;
            _locked = locked;
            if (_state is WorkspaceSessionState.Closing or WorkspaceSessionState.Fatal
                or WorkspaceSessionState.Restoring) return;
            if (locked)
            {
                SetState(WorkspaceSessionState.Locked);
                return;
            }
            SetState(_borrowed is null ? WorkspaceSessionState.Unconfigured : _readyState);
        }
        finally { _transitions.Release(); }
    }

    /// <summary>
    /// Caller must resolve drafts, confirm both identities, authorize the operation and
    /// check storage before calling. Admission closes before in-flight calls drain.
    /// A changed snapshot or backup rejects the stale confirmation without replacement.
    /// After dispatch, any uncertain failure retires the old view and fails closed.
    /// Success returns a fresh borrower for a new projection, never reviving the old one.
    /// </summary>
    public async Task<WorkspaceRestoreOutcome> RestoreIndependentAsync(string sourcePath, WorkspaceRestoreRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Incoming);
        await _transitions.WaitAsync(cancellationToken).ConfigureAwait(false);
        WorkspaceBackendClient? borrowed = null;
        var dispatched = false;
        try
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (_state != WorkspaceSessionState.ReadyIndependent || _backend is not { } backend || _borrowed is null)
                throw new SnookException(SnookErrorCode.InvalidTransition, "An active independent workspace is required for restore.");
            borrowed = _borrowed;
            SetState(WorkspaceSessionState.Restoring);
            await borrowed.PauseAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            var current = await backend.GetBootstrapAsync(cancellationToken).ConfigureAwait(false);
            if (current.Workspace.Id != request.CurrentWorkspaceId || current.CommittedCursor != request.CurrentCursor)
                throw new SnookException(SnookErrorCode.RevisionConflict, "The current workspace changed. Review the backup and confirm again.");
            var incoming = await backend.InspectBackupAsync(sourcePath, cancellationToken).ConfigureAwait(false);
            if (incoming != request.Incoming)
                throw new SnookException(SnookErrorCode.RevisionConflict, "The selected backup changed. Review it and confirm again.");
            cancellationToken.ThrowIfCancellationRequested();
            dispatched = true;
            var result = await backend.RestoreBackupAsync(sourcePath, cancellationToken).ConfigureAwait(false);
            // Restore may commit just before cancellation. Never report cancellation or
            // revive the old borrower merely because the caller left after that commit.
            await borrowed.DisposeAsync().ConfigureAwait(false);
            using var reopenBudget = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var snapshot = await backend.GetBootstrapAsync(reopenBudget.Token).ConfigureAwait(false);
            if (snapshot.Workspace.Id != incoming.Workspace.Id)
                throw new SnookException(SnookErrorCode.StoreUnavailable, "The restored workspace identity could not be confirmed.");
            _borrowed = new WorkspaceBackendClient(backend);
            SetState(WorkspaceSessionState.ReadyIndependent);
            return new WorkspaceRestoreOutcome(_borrowed, snapshot, result);
        }
        catch
        {
            if (borrowed is not null)
            {
                if (dispatched)
                {
                    await borrowed.DisposeAsync().ConfigureAwait(false);
                    _handoffFailed = true;
                    SetState(WorkspaceSessionState.Fatal);
                }
                else
                {
                    borrowed.Resume();
                    SetState(WorkspaceSessionState.ReadyIndependent);
                }
            }
            throw;
        }
        finally { _transitions.Release(); }
    }

    /// <summary>
    /// Retires the current borrower, drains its calls and closes the selected host
    /// without disposing the process-scoped session. The caller owns draft review
    /// and native-surface cleanup before requesting this transition.
    /// </summary>
    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        await _transitions.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            SetState(WorkspaceSessionState.Closing);
            var borrowed = _borrowed;
            var backend = _backend;
            _borrowed = null;
            _backend = null;
            try
            {
                if (borrowed is not null) await borrowed.DisposeAsync().ConfigureAwait(false);
                if (backend is not null) await backend.DisposeAsync().ConfigureAwait(false);
                _handoffFailed = false;
                _readyState = default;
                SetState(_locked ? WorkspaceSessionState.Locked : WorkspaceSessionState.Unconfigured);
            }
            catch
            {
                _closed = true;
                SetState(WorkspaceSessionState.Fatal);
                throw;
            }
        }
        finally { _transitions.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _transitions.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_closed) return;
            _closed = true;
            SetState(WorkspaceSessionState.Closing);
            var backend = _backend;
            _backend = null;
            try
            {
                if (_borrowed is not null) await _borrowed.DisposeAsync().ConfigureAwait(false);
                _borrowed = null;
                if (backend is not null) await backend.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                SetState(WorkspaceSessionState.Fatal);
                throw;
            }
        }
        // Keep the semaphore usable for queued callers, which must observe closed.
        finally { _transitions.Release(); }
    }

    private void SetState(WorkspaceSessionState state)
    {
        _state = state;
        if (StateChanged is not { } handlers) return;
        foreach (EventHandler<WorkspaceSessionState> handler in handlers.GetInvocationList())
        {
            try { handler(this, state); }
            catch (Exception) { /* An observer must not break ownership or starve other observers. */ }
        }
    }
}
