using System.ComponentModel;
using System.Runtime.CompilerServices;
using Snook.Application;
using Snook.Contracts;
using Snook.Domain;

namespace Snook.UI;

public sealed class ConnectionViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly ClientProfileStore _profiles;
    private readonly Func<ClientConnectionProfile, CancellationToken, Task<IBackendClient>> _open;
    private readonly CancellationTokenSource _lifetime = new();
    private string? _stamp;
    private string _dataDirectory;
    private string _endpoint;
    private string _tokenFile;
    private string _status;
    private bool _daemon;
    private bool _busy;
    private bool _closed;
    private bool _saveForNextLaunch;
    private Task? _attempt;
    private Task? _disposeTask;

    public ConnectionViewModel(ClientProfileStore profiles, ClientProfileSnapshot snapshot, ClientConnectionProfile initial,
        bool settingsOnly = false, string? error = null,
        Func<ClientConnectionProfile, CancellationToken, Task<IBackendClient>>? open = null)
    {
        _profiles = profiles;
        _stamp = snapshot.Stamp;
        _dataDirectory = initial.DataDirectory;
        _endpoint = initial.Endpoint ?? "";
        _tokenFile = initial.TokenFile ?? "";
        _daemon = initial.Host == "daemon";
        SettingsOnly = settingsOnly;
        _status = error ?? (settingsOnly ? "Changes apply on the next launch. Your current workspace and open drafts stay open." : "Choose how Snook opens this workspace.");
        _open = open ?? ((profile, token) => ClientProfileStore.OpenAsync(profile, cancellationToken: token));
        ConnectCommand = new AsyncCommand(_ => ConnectAsync(), _ => !_busy && !_closed);
        SaveCommand = new AsyncCommand(_ => SaveAsync(), _ => !_busy && !_closed);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? Connected;
    public event EventHandler? Saved;
    public IBackendClient? Backend { get; private set; }
    public ClientConnectionProfile? ConnectedProfile { get; private set; }
    public bool SettingsOnly { get; }
    public bool IsStartup => !SettingsOnly;
    public bool CanEdit => !_busy;
    public bool IsBusy => _busy;
    public string ProfilePath => _profiles.ProfilePath;
    public string DataDirectory { get => _dataDirectory; set => Set(ref _dataDirectory, value); }
    public string Endpoint { get => _endpoint; set => Set(ref _endpoint, value); }
    public string TokenFile { get => _tokenFile; set => Set(ref _tokenFile, value); }
    public bool UseDaemon { get => _daemon; set { Set(ref _daemon, value); Changed(nameof(UseEmbedded)); } }
    public bool UseEmbedded { get => !_daemon; set { if (value) UseDaemon = false; } }
    public bool SaveForNextLaunch { get => _saveForNextLaunch; set => Set(ref _saveForNextLaunch, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public AsyncCommand ConnectCommand { get; }
    public AsyncCommand SaveCommand { get; }

    public Task ConnectAsync() => _busy ? _attempt ?? Task.CompletedTask : _attempt = RunAsync(connect: true);
    public Task SaveAsync() => _busy ? _attempt ?? Task.CompletedTask : _attempt = RunAsync(connect: false);

    private async Task RunAsync(bool connect)
    {
        if (_busy || _closed) return;
        SetBusy(true);
        IBackendClient? candidate = null;
        try
        {
            var profile = ClientProfileStore.Validate(new(1, UseDaemon ? "daemon" : "embedded", DataDirectory, Endpoint, TokenFile));
            if (!connect || SaveForNextLaunch)
            {
                var saved = await _profiles.SaveAsync(profile, _stamp, _lifetime.Token);
                _stamp = saved.Stamp;
                Status = "Connection settings saved for the next launch.";
                if (!connect) { Saved?.Invoke(this, EventArgs.Empty); return; }
            }
            Status = UseDaemon ? "Connecting to the daemon… SQLite will not be opened." : "Opening the embedded workspace…";
            candidate = await _open(profile, _lifetime.Token);
            var snapshot = await candidate.GetBootstrapAsync(_lifetime.Token);
            if (_closed) return;
            Status = $"Connected to {snapshot.Workspace.Name}.";
            Backend = candidate;
            candidate = null;
            ConnectedProfile = profile;
            Connected?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception)
        {
            Status = exception is SnookException snook ? snook.Message
                : "Snook could not open or save this connection. Check the directory, daemon and token file, then retry.";
        }
        finally
        {
            if (candidate is not null) await candidate.DisposeAsync();
            SetBusy(false);
        }
    }

    private void SetBusy(bool value)
    {
        _busy = value;
        Changed(nameof(IsBusy));
        Changed(nameof(CanEdit));
        ConnectCommand.RaiseCanExecuteChanged();
        SaveCommand.RaiseCanExecuteChanged();
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        Changed(name);
    }
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public ValueTask DisposeAsync() => new(_disposeTask ??= DisposeCoreAsync());

    private async Task DisposeCoreAsync()
    {
        if (_closed) return;
        _closed = true;
        await _lifetime.CancelAsync();
        if (_attempt is not null) await _attempt;
        _lifetime.Dispose();
        // A successful backend is transferred to the desktop lifetime owner.
    }
}
