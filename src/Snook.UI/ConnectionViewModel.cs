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
    private string _pairingCode = "";
    private string _deviceName = Environment.MachineName;
    private string _status;
    private int _mode;
    private string? _credentialReference;
    private Guid? _expectedServerId;
    private Guid? _expectedWorkspaceId;
    private string? _friendlyName;
    private int? _contractMajor;
    private int? _contractMinor;
    private PendingDaemonPairing? _pendingPairing;
    private bool _useOwnerOnlyCredentialFile;
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
        _mode = initial.Host == "embedded" ? 0 : initial.TransportKind == "tailscale-serve" ? 2 : 1;
        _credentialReference = initial.CredentialReference;
        _expectedServerId = initial.ExpectedServerId;
        _expectedWorkspaceId = initial.ExpectedWorkspaceId;
        _friendlyName = initial.FriendlyName;
        _contractMajor = initial.ContractMajor;
        _contractMinor = initial.ContractMinor;
        SettingsOnly = settingsOnly;
        _status = error ?? (settingsOnly ? "Changes apply on the next launch. Your current workspace and open drafts stay open." : "Choose how Snook opens this workspace.");
        _open = open ?? ((profile, token) => ClientProfileStore.OpenAsync(profile, cancellationToken: token));
        ConnectCommand = new AsyncCommand(_ => ConnectAsync(), _ => !_busy && !_closed);
        SaveCommand = new AsyncCommand(_ => SaveAsync(), _ => !_busy && !_closed);
        PairCommand = new AsyncCommand(_ => PairAsync(), _ => !_busy && !_closed && UseTailnet);
        ConfirmPairingCommand = new AsyncCommand(_ => ConfirmPairingAsync(), _ => !_busy && !_closed && _pendingPairing is not null);
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
    public string Endpoint
    {
        get => _endpoint;
        set
        {
            if (_endpoint == value) return;
            _endpoint = value;
            Changed();
            Changed(nameof(TailnetServerSummary));
        }
    }
    public string TokenFile
    {
        get => _tokenFile;
        set
        {
            if (_tokenFile == value) return;
            _tokenFile = value;
            Changed();
            Changed(nameof(TailnetCredentialSummary));
        }
    }
    public string PairingCode { get => _pairingCode; set => Set(ref _pairingCode, value); }
    public string DeviceName { get => _deviceName; set => Set(ref _deviceName, value); }
    public bool UseDaemon { get => _mode == 1; set { if (value) SetMode(1); } }
    public bool UseEmbedded { get => _mode == 0; set { if (value) SetMode(0); } }
    public bool UseTailnet { get => _mode == 2; set { if (value) SetMode(2); } }
    public bool HasPendingPairing => _pendingPairing is not null;
    public bool HasSavedTailnetIdentity => _expectedServerId is not null && _expectedWorkspaceId is not null;
    public string TailnetIdentitySummary => HasSavedTailnetIdentity
        ? $"Pinned server {_expectedServerId} · workspace {_expectedWorkspaceId}"
        : "No server identity is saved yet. Pair and review identity before connecting.";
    public string TailnetServerSummary => Uri.TryCreate(Endpoint, UriKind.Absolute, out var endpoint)
        ? $"Server: {endpoint.Host}"
        : "Server: no canonical Serve URL saved";
    public string TailnetNegotiationSummary => _contractMajor is not null && _contractMinor is not null
        ? $"Last saved negotiation: contract {_contractMajor}.{_contractMinor}; identity is reverified on every connection."
        : "No successful contract negotiation is saved; identity will be verified before RPC.";
    public string TailnetCredentialSummary => _credentialReference is null
        ? string.IsNullOrWhiteSpace(TokenFile) ? "No protected credential is selected." : "Credential: explicit owner-only token file"
        : $"Credential: {_friendlyName ?? "paired device"} · {CredentialStoreLabel(_credentialReference)}";
    public bool UseOwnerOnlyCredentialFile { get => _useOwnerOnlyCredentialFile; set => Set(ref _useOwnerOnlyCredentialFile, value); }
    public static string NativeCredentialStoreName => ClientCredentialStores.NativeStoreName;
    public bool SaveForNextLaunch { get => _saveForNextLaunch; set => Set(ref _saveForNextLaunch, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public AsyncCommand ConnectCommand { get; }
    public AsyncCommand SaveCommand { get; }
    public AsyncCommand PairCommand { get; }
    public AsyncCommand ConfirmPairingCommand { get; }

    public Task ConnectAsync() => _busy ? _attempt ?? Task.CompletedTask : _attempt = RunAsync(connect: true);
    public Task SaveAsync() => _busy ? _attempt ?? Task.CompletedTask : _attempt = RunAsync(connect: false);

    private async Task RunAsync(bool connect)
    {
        if (_busy || _closed) return;
        SetBusy(true);
        IBackendClient? candidate = null;
        try
        {
            var profile = BuildProfile();
            if (!connect || SaveForNextLaunch)
            {
                var saved = await _profiles.SaveAsync(profile, _stamp, _lifetime.Token);
                _stamp = saved.Stamp;
                Status = "Connection settings saved for the next launch.";
                if (!connect) { Saved?.Invoke(this, EventArgs.Empty); return; }
            }
            Status = UseEmbedded ? "Opening the embedded workspace…"
                : UseTailnet ? "Connecting through Tailscale Serve… SQLite will not be opened on this device."
                : "Connecting to the local daemon… SQLite will not be opened.";
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

    private ClientConnectionProfile BuildProfile()
    {
        if (!UseTailnet)
            return ClientProfileStore.Validate(new(1, UseDaemon ? "daemon" : "embedded", DataDirectory, Endpoint, TokenFile));
        return ClientProfileStore.Validate(new ClientConnectionProfile(2, "daemon", DataDirectory, Endpoint,
            string.IsNullOrWhiteSpace(TokenFile) ? null : TokenFile, "tailscale-serve", _credentialReference,
            _expectedServerId, _expectedWorkspaceId, _friendlyName, _contractMajor, _contractMinor));
    }

    private async Task PairAsync()
    {
        if (_busy || _closed) return;
        SetBusy(true);
        try
        {
            if (_pendingPairing is not null) await _pendingPairing.CancelAsync(_lifetime.Token);
            if (!Uri.TryCreate(Endpoint.Trim(), UriKind.Absolute, out var origin))
                throw new SnookException(SnookErrorCode.ValidationFailed, "Enter the canonical https://<machine>.<tailnet>.ts.net/ Serve URL.");
            Status = "Redeeming the one-time invitation…";
            _pendingPairing = await TailnetPairingClient.RedeemAsync(origin, PairingCode, DeviceName,
                cancellationToken: _lifetime.Token);
            var identity = _pendingPairing.Identity;
            Status = $"Review before confirming: {identity.WorkspaceName} · workspace {identity.WorkspaceId} · server {identity.ServerId} · contract {identity.Contract}.";
            Changed(nameof(HasPendingPairing));
            ConfirmPairingCommand.RaiseCanExecuteChanged();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception)
        {
            Status = exception is SnookException snook ? snook.Message
                : "Pairing failed. Check Tailscale connectivity, the Serve URL, and the short-lived code, then retry.";
        }
        finally { SetBusy(false); }
    }

    private async Task ConfirmPairingAsync()
    {
        if (_busy || _closed || _pendingPairing is null) return;
        SetBusy(true);
        try
        {
            var pending = _pendingPairing;
            IClientCredentialStore credentialStore = UseOwnerOnlyCredentialFile
                ? new OwnerOnlyFileCredentialStore(DataDirectory)
                : ClientCredentialStores.CreateNative();
            var profile = await pending.ConfirmAsync(DataDirectory, credentialStore, DeviceName, _lifetime.Token);
            ClientProfileSnapshot saved;
            try { saved = await _profiles.SaveAsync(profile, _stamp, _lifetime.Token); }
            catch { await pending.CancelAsync(_lifetime.Token); _pendingPairing = null; throw; }
            _stamp = saved.Stamp;
            pending.Complete();
            _pendingPairing = null;
            _credentialReference = profile.CredentialReference;
            _expectedServerId = profile.ExpectedServerId;
            _expectedWorkspaceId = profile.ExpectedWorkspaceId;
            _friendlyName = profile.FriendlyName;
            _contractMajor = profile.ContractMajor;
            _contractMinor = profile.ContractMinor;
            PairingCode = "";
            Changed(nameof(HasSavedTailnetIdentity));
            Changed(nameof(TailnetIdentitySummary));
            Changed(nameof(TailnetServerSummary));
            Changed(nameof(TailnetNegotiationSummary));
            Changed(nameof(TailnetCredentialSummary));
            Changed(nameof(HasPendingPairing));
            try
            {
                await using var verified = await _open(profile, _lifetime.Token);
                _ = await verified.GetBootstrapAsync(_lifetime.Token);
                Status = $"Pairing confirmed and the credential was saved in {(UseOwnerOnlyCredentialFile ? "the explicitly selected owner-only file" : NativeCredentialStoreName)}. Connect when ready.";
            }
            catch (Exception exception)
            {
                Status = "The reviewed profile was saved, but its provisional credential could not be activated. Retry Connect promptly; if the five-minute window expires, pair again. "
                    + (exception is SnookException snook ? snook.Message : "The daemon could not be reached.");
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception)
        {
            Status = exception is SnookException snook ? snook.Message : "Pairing could not be confirmed or saved.";
        }
        finally { SetBusy(false); ConfirmPairingCommand.RaiseCanExecuteChanged(); }
    }

    private void SetMode(int mode)
    {
        if (_mode == mode) return;
        _mode = mode;
        Changed(nameof(UseEmbedded));
        Changed(nameof(UseDaemon));
        Changed(nameof(UseTailnet));
        PairCommand.RaiseCanExecuteChanged();
    }

    private static string CredentialStoreLabel(string reference) => reference.Split(':', 2)[0] switch
    {
        "secret-service" => "Linux Secret Service",
        "windows-credential" => "Windows Credential Manager",
        "macos-keychain" => "macOS Keychain",
        "owner-file" => "owner-only file",
        _ => "unknown protected store"
    };

    private void SetBusy(bool value)
    {
        _busy = value;
        Changed(nameof(IsBusy));
        Changed(nameof(CanEdit));
        ConnectCommand.RaiseCanExecuteChanged();
        SaveCommand.RaiseCanExecuteChanged();
        PairCommand.RaiseCanExecuteChanged();
        ConfirmPairingCommand.RaiseCanExecuteChanged();
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
        if (_pendingPairing is not null) await _pendingPairing.DisposeAsync();
        _lifetime.Dispose();
        // A successful backend is transferred to the desktop lifetime owner.
    }
}
