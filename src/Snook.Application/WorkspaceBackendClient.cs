using Snook.Contracts;
using Snook.Domain;

namespace Snook.Application;

// A revocable borrower, not the owner of the underlying backend.
internal sealed partial class WorkspaceBackendClient : IBackendClient, IBackendConnectionState, IBackendDocumentTransfer
{
    private readonly IBackendClient _backend;
    private readonly object _gate = new();
    private int _active;
    private bool _paused;
    private bool _retired;
    private TaskCompletionSource? _drained;

    internal WorkspaceBackendClient(IBackendClient backend)
    {
        _backend = backend;
        _backend.Changed += BackendChanged;
    }

    public event EventHandler<ChangeNotification>? Changed;

    public bool IsConnected => _backend is not IBackendConnectionState connection
        || connection.IsConnected;

    public string ConnectionLabel => _backend is IBackendConnectionState connection
        ? connection.ConnectionLabel : "Embedded workspace";

    public event EventHandler? ConnectionStateChanged
    {
        add
        {
            if (_backend is IBackendConnectionState connection)
                connection.ConnectionStateChanged += value;
        }
        remove
        {
            if (_backend is IBackendConnectionState connection)
                connection.ConnectionStateChanged -= value;
        }
    }

    private void BackendChanged(object? sender, ChangeNotification change)
    {
        EventHandler<ChangeNotification>? handlers;
        lock (_gate) handlers = _paused || _retired ? null : Changed;
        if (handlers is null) return;
        foreach (EventHandler<ChangeNotification> handler in handlers.GetInvocationList())
        {
            try { handler(this, change); }
            catch (Exception) { /* Observers cannot fail committed backend operations. */ }
        }
    }

    private Task<bool> RunAsync(Func<Task> operation, CancellationToken cancellationToken)
        => RunAsync(async () => { await operation().ConfigureAwait(false); return true; }, cancellationToken);

    private async Task<T> RunAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_paused || _retired)
                throw new SnookException(SnookErrorCode.StoreUnavailable,
                    "This workspace view is unavailable during or after a workspace handoff. Reopen the current workspace.");
            _active++;
        }
        try { return await operation().ConfigureAwait(false); }
        finally
        {
            lock (_gate)
            {
                _active--;
                if (_active == 0) _drained?.TrySetResult();
            }
        }
    }

    internal Task PauseAsync()
    {
        lock (_gate)
        {
            _paused = true;
            if (_active == 0) return Task.CompletedTask;
            _drained ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
            return _drained.Task;
        }
    }

    internal void Resume()
    {
        lock (_gate)
        {
            if (_retired) throw new InvalidOperationException("A retired workspace borrower cannot resume.");
            _paused = false;
            _drained = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task drain;
        lock (_gate)
        {
            _retired = true;
            Changed = null;
            drain = PauseAsync();
        }
        _backend.Changed -= BackendChanged;
        await drain.ConfigureAwait(false);
    }
}
