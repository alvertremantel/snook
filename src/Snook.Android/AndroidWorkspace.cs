using Avalonia.Threading;
using Snook.Application;
using Snook.Contracts;
using Snook.Persistence.Sqlite;
using Snook.UI;
using System.ComponentModel;

namespace Snook.Android;

// Owned by the application, never by an Activity or a view. Android process death
// releases the kernel lease; no Activity destruction callback closes this owner.
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "Application-process lifetime owner. Android has no reliable process shutdown callback; activity destruction must not dispose the session.")]
internal sealed class AndroidWorkspace
{
    private readonly WorkspaceSession _session = new(OpenBackendAsync);
    private readonly AndroidHostSelection _hostSelection = new();
    private readonly AttachedProfileStore _attachedProfiles;
    private readonly TimerNotificationCoordinator _timerNotifications;
    private readonly ReminderCoordinator _reminders;
    private Task<MainWindowViewModel>? _opening;
    private MainWindowViewModel? _projection;
    private IBackendClient? _currentBackend;
    private bool _foreground;
    private bool _appLocked;
    private bool _workspaceSelected;
    private bool _configuringIndependent;
    private bool _configuringAttached;
    private DaemonPairingPreview? _pairingPreview;
    private EncryptedCaptureStore? _captureStore;
    private EncryptedTaskEditorStore? _taskEditorStore;
    private EncryptedUtilityEditorStore? _utilityEditorStore;
    private NavigationStateStore? _navigationStore;
    private Guid _workspaceId;
    private TaskCaptureDraft? _recovery;
    private TaskEditorRecoveryDraft? _taskEditorRecovery;
    private UtilityEditorRecoveryDraft? _utilityEditorRecovery;
    private WorkspaceNavigationState? _navigationState;
    private TailnetAttachedProfile? _activeAttachedProfile;
    private bool _recoveryBlocked;
    private bool _taskEditorRecoveryBlocked;
    private bool _utilityEditorRecoveryBlocked;
    private bool _restoringTaskEditorRecovery;
    private bool _restoringUtilityEditorRecovery;
    private bool _restoringNavigation;
    private TaskRowViewModel? _observedTaskEditor;
    internal bool IsRestoring { get; private set; }
    internal event EventHandler? ProjectionChanged;
    private readonly DispatcherTimer _captureTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly DispatcherTimer _taskEditorTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly DispatcherTimer _utilityEditorTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _navigationTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    internal string CaptureRecoveryMessage { get; private set; } = string.Empty;
    internal bool HasBlockedRecovery => _recovery is not null || _taskEditorRecovery is not null
        || _utilityEditorRecovery is not null || _recoveryBlocked || _taskEditorRecoveryBlocked
        || _utilityEditorRecoveryBlocked;
    internal AndroidHostMode HostMode { get; private set; }
    internal string HostConfigurationMessage { get; private set; } = string.Empty;
    internal DaemonPairingPreview? PairingPreview => _pairingPreview;
    internal bool HasSavedAttachedProfile => _attachedProfiles.HasProfile;
    internal IReadOnlyList<TailnetAttachedProfile> SavedAttachedProfiles
    {
        get
        {
            try { return _attachedProfiles.ListProfiles(); }
            catch (Exception exception)
            {
                global::Android.Util.Log.Warn("SnookPairing", $"Catalog: {exception.GetType().Name}");
                return [];
            }
        }
    }
    internal string ActiveHostLabel => _activeAttachedProfile is { } profile
        ? $"Snook · {profile.ServeOrigin.Host}" : "Snook · Paired daemon";
    internal static bool HasIndependentWorkspace => File.Exists(Path.Combine(
        global::Android.App.Application.Context.NoBackupFilesDir?.AbsolutePath
            ?? throw new InvalidOperationException("Private storage is unavailable."),
        "independent", "workspace.db"));

    internal AndroidWorkspace()
    {
        var root = global::Android.App.Application.Context.NoBackupFilesDir?.AbsolutePath
            ?? throw new InvalidOperationException("Private storage is unavailable.");
        _attachedProfiles = new AttachedProfileStore(root);
        _timerNotifications = new TimerNotificationCoordinator(root);
        _reminders = new ReminderCoordinator(root);
        HostMode = _hostSelection.Load();
        _workspaceSelected = HostMode is AndroidHostMode.Independent or AndroidHostMode.Attached;
        _captureTimer.Tick += async (_, _) => { _captureTimer.Stop(); await SaveCaptureAsync(); };
        _taskEditorTimer.Tick += async (_, _) => { _taskEditorTimer.Stop(); await SaveTaskEditorAsync(); };
        _utilityEditorTimer.Tick += async (_, _) =>
        {
            _utilityEditorTimer.Stop();
            await SaveUtilityEditorAsync();
            if (_projection?.IsUtilityEditorOpen == true && !_restoringUtilityEditorRecovery)
                _utilityEditorTimer.Start();
        };
        _navigationTimer.Tick += async (_, _) => { _navigationTimer.Stop(); await SaveNavigationAsync(); };
    }

    internal async Task ConfigureIndependentAsync()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (HostMode == AndroidHostMode.Independent)
        {
            IsWorkspaceSelected = true;
            return;
        }
        if (_configuringIndependent || MainActivity.Current is not { } activity) return;
        if (!await activity.ShowIndependentSetupAsync(HasIndependentWorkspace))
        {
            HostConfigurationMessage = "Independent workspace setup cancelled. No workspace was created.";
            ProjectionChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        _configuringIndependent = true;
        HostConfigurationMessage = "Creating a private workspace on this device…";
        ProjectionChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            // The backend factory is reached only after the foreground confirmation.
            // Persist the selection only after the database has opened and its first
            // committed snapshot is available.
            _workspaceSelected = true;
            await OpenAsync();
            AndroidHostSelection.SaveIndependent();
            HostMode = AndroidHostMode.Independent;
            HostConfigurationMessage = string.Empty;
        }
        catch
        {
            _workspaceSelected = false;
            HostConfigurationMessage = "The private workspace could not be opened. Nothing was switched to another host; retry when storage is available.";
        }
        finally
        {
            _configuringIndependent = false;
            ProjectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    internal async Task<DaemonPairingPreview> BeginAttachedPairingAsync(string origin,
        string code, string deviceName, CancellationToken cancellationToken = default)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (HostMode != AndroidHostMode.Unconfigured || _configuringAttached)
            throw new InvalidOperationException("A workspace host is already selected or pairing is in progress.");
        var endpoint = DaemonBackendClient.ValidateEndpoint(origin, DaemonEndpointKind.TailnetServe);
        var installationId = AndroidInstallationIdentity.GetOrCreate();
        await using var client = new AndroidTailnetPairingClient(endpoint);
        var preview = await client.BeginAsync(code, installationId, deviceName,
            cancellationToken).ConfigureAwait(true);
        _pairingPreview = preview;
        HostConfigurationMessage = "Review the daemon and tailnet identity before confirming. No credential has been issued yet.";
        return preview;
    }

    internal void CancelAttachedPairing()
    {
        Dispatcher.UIThread.VerifyAccess();
        _pairingPreview = null;
        HostConfigurationMessage = "Pairing confirmation cancelled. The consumed code cannot be reused; create a new code on the daemon.";
    }

    internal async Task ConfigureAttachedAsync(CancellationToken cancellationToken = default)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_pairingPreview is not { } preview || HostMode != AndroidHostMode.Unconfigured
            || _configuringAttached) return;
        _configuringAttached = true;
        HostConfigurationMessage = "Issuing and protecting this device credential…";
        try
        {
            var installationId = AndroidInstallationIdentity.GetOrCreate();
            await using var client = new AndroidTailnetPairingClient(preview.ServeOrigin);
            var paired = await client.CompleteAsync(preview, installationId,
                cancellationToken).ConfigureAwait(true);
            await _attachedProfiles.SaveAsync(paired, cancellationToken).ConfigureAwait(true);
            _pairingPreview = null;
            await OpenSavedAttachedAsync(cancellationToken).ConfigureAwait(true);
        }
        catch
        {
            HostConfigurationMessage = _attachedProfiles.HasProfile
                ? "The credential is protected, but the daemon snapshot could not be loaded. Retry the saved pairing; Snook will not open a local workspace."
                : "Pairing was not completed or protected. Create a new short-lived code and retry; no local workspace was opened.";
            throw;
        }
        finally
        {
            _configuringAttached = false;
            ProjectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    internal async Task RetrySavedAttachedAsync(Guid? credentialId = null,
        CancellationToken cancellationToken = default)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (HostMode != AndroidHostMode.Unconfigured || _configuringAttached) return;
        _configuringAttached = true;
        HostConfigurationMessage = "Authenticating the saved pairing and loading a fresh snapshot…";
        try
        {
            if (credentialId is not null)
                await _attachedProfiles.SelectAsync(credentialId.Value, cancellationToken).ConfigureAwait(true);
            await OpenSavedAttachedAsync(cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            _configuringAttached = false;
            ProjectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task OpenSavedAttachedAsync(CancellationToken cancellationToken)
    {
        _workspaceSelected = true;
        _opening = OpenCoreAsync();
        try
        {
            await _opening.WaitAsync(cancellationToken).ConfigureAwait(true);
            AndroidHostSelection.SaveAttached();
            HostMode = AndroidHostMode.Attached;
            HostConfigurationMessage = string.Empty;
        }
        catch
        {
            _workspaceSelected = false;
            throw;
        }
    }

    internal async Task ForgetAttachedAsync()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!_attachedProfiles.HasProfile || MainActivity.Current is not { } activity
            || !await activity.ShowForgetPairingAsync()) return;
        HostConfigurationMessage = "Closing the paired workspace…";
        ProjectionChanged?.Invoke(this, EventArgs.Empty);
        _captureTimer.Stop();
        _taskEditorTimer.Stop();
        _utilityEditorTimer.Stop();
        _navigationTimer.Stop();
        await SaveCaptureAsync();
        await SaveTaskEditorAsync();
        await SaveUtilityEditorAsync();
        await SaveNavigationAsync();
        await _timerNotifications.DetachAsync(clearNative: true);
        await _reminders.DetachAsync(clearNative: true);
        if (_projection is { } model)
        {
            model.PropertyChanged -= CaptureChanged;
            if (_observedTaskEditor is not null) _observedTaskEditor.PropertyChanged -= TaskEditorChanged;
            _observedTaskEditor = null;
            await model.SetPresentationActiveAsync(false);
            await model.DisposeAsync();
        }
        _projection = null;
        _currentBackend = null;
        _opening = null;
        await _session.ResetAsync();
        await _attachedProfiles.ClearSelectedAsync();
        AndroidHostSelection.SaveUnconfigured();
        HostMode = AndroidHostMode.Unconfigured;
        _workspaceSelected = false;
        _pairingPreview = null;
        ResetTransientWorkspaceState();
        HostConfigurationMessage = "Pairing removed from this device. Revoke its old device record on the daemon before pairing again.";
        ProjectionChanged?.Invoke(this, EventArgs.Empty);
    }

    internal async Task ForgetSavedAttachedAsync(Guid credentialId)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (HostMode != AndroidHostMode.Unconfigured || MainActivity.Current is not { } activity
            || !SavedAttachedProfiles.Any(profile => profile.CredentialId == credentialId)
            || !await activity.ShowForgetPairingAsync()) return;
        await _attachedProfiles.SelectAsync(credentialId);
        await _attachedProfiles.ClearSelectedAsync();
        HostConfigurationMessage = "Saved pairing removed from this device. Revoke its old device record on that daemon before pairing again.";
        ProjectionChanged?.Invoke(this, EventArgs.Empty);
    }

    internal async Task TestSavedAttachedAsync(Guid credentialId,
        CancellationToken cancellationToken = default)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (HostMode != AndroidHostMode.Unconfigured) return;
        HostConfigurationMessage = "Testing the selected Tailnet-only pairing…";
        ProjectionChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            var attached = await SelectAttachedCredentialAsync(credentialId,
                cancellationToken).ConfigureAwait(true);
            await using var client = new DaemonBackendClient(attached.Profile.ServeOrigin,
                attached.Credential, DaemonEndpointKind.TailnetServe);
            var snapshot = await client.GetBootstrapAsync(cancellationToken).ConfigureAwait(true);
            if (snapshot.Workspace.Id != attached.Profile.WorkspaceId)
                throw new InvalidDataException("The paired daemon changed workspace identity.");
            HostConfigurationMessage = $"Pairing verified: {attached.Profile.ServeOrigin.Host} · {snapshot.Workspace.Name}.";
        }
        catch
        {
            HostConfigurationMessage = "Pairing test failed closed. Check Tailscale, grants, Serve, the daemon, and device revocation; no alternate route was attempted.";
            throw;
        }
        finally { ProjectionChanged?.Invoke(this, EventArgs.Empty); }
    }

    internal async Task RenameSavedAttachedAsync(Guid credentialId, string deviceName,
        CancellationToken cancellationToken = default)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (HostMode != AndroidHostMode.Unconfigured) return;
        HostConfigurationMessage = "Renaming this paired-device record…";
        ProjectionChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            var attached = await SelectAttachedCredentialAsync(credentialId,
                cancellationToken).ConfigureAwait(true);
            await using var client = new DaemonBackendClient(attached.Profile.ServeOrigin,
                attached.Credential, DaemonEndpointKind.TailnetServe);
            var renamed = await client.RenameCurrentPairedDeviceAsync(deviceName,
                cancellationToken).ConfigureAwait(true);
            if (renamed.CredentialId != credentialId)
                throw new InvalidDataException("The daemon renamed a different paired device.");
            await _attachedProfiles.RenameSelectedAsync(renamed.DeviceName,
                cancellationToken).ConfigureAwait(true);
            HostConfigurationMessage = $"Paired device renamed to {renamed.DeviceName}.";
        }
        catch
        {
            HostConfigurationMessage = "Rename failed or its result is uncertain. Test the pairing before retrying; no workspace data was changed.";
            throw;
        }
        finally { ProjectionChanged?.Invoke(this, EventArgs.Empty); }
    }

    internal async Task RepairSavedAttachedAsync(Guid credentialId,
        CancellationToken cancellationToken = default)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (HostMode != AndroidHostMode.Unconfigured || MainActivity.Current is not { } activity) return;
        HostConfigurationMessage = "Checking the daemon's authoritative workspace identity…";
        ProjectionChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            var attached = await SelectAttachedCredentialAsync(credentialId,
                cancellationToken).ConfigureAwait(true);
            await using var client = new DaemonBackendClient(attached.Profile.ServeOrigin,
                attached.Credential, DaemonEndpointKind.TailnetServe);
            var snapshot = await client.GetBootstrapAsync(cancellationToken).ConfigureAwait(true);
            if (snapshot.Workspace.Id == attached.Profile.WorkspaceId)
            {
                HostConfigurationMessage = "The saved pairing already matches the daemon. No repair was needed.";
                return;
            }
            if (!await activity.ShowRepairPairingAsync(attached.Profile.WorkspaceId,
                snapshot.Workspace.Id, snapshot.Workspace.Name))
            {
                HostConfigurationMessage = "Profile repair cancelled. The saved identity was not changed.";
                return;
            }
            await _attachedProfiles.RebindSelectedWorkspaceAsync(attached.Profile.WorkspaceId,
                snapshot.Workspace.Id, cancellationToken).ConfigureAwait(true);
            HostConfigurationMessage = $"Saved pairing repaired for {snapshot.Workspace.Name}. Test or connect to load a fresh snapshot.";
        }
        catch
        {
            HostConfigurationMessage = "Profile repair failed closed. If the credential was revoked, ask the daemon administrator to verify its device list and create a fresh pairing code.";
            throw;
        }
        finally { ProjectionChanged?.Invoke(this, EventArgs.Empty); }
    }

    internal async Task RevokeSavedAttachedAsync(Guid credentialId,
        CancellationToken cancellationToken = default)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (HostMode != AndroidHostMode.Unconfigured || MainActivity.Current is not { } activity
            || !await activity.ShowRevokePairingAsync()) return;
        HostConfigurationMessage = "Revoking this paired-device credential…";
        ProjectionChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            var attached = await SelectAttachedCredentialAsync(credentialId,
                cancellationToken).ConfigureAwait(true);
            await using var client = new DaemonBackendClient(attached.Profile.ServeOrigin,
                attached.Credential, DaemonEndpointKind.TailnetServe);
            var revoked = await client.RevokeCurrentPairedDeviceAsync(cancellationToken)
                .ConfigureAwait(true);
            if (!revoked.Revoked || revoked.CredentialId != credentialId)
                throw new InvalidDataException("The daemon did not confirm this device revocation.");
            await _attachedProfiles.ClearSelectedAsync(cancellationToken).ConfigureAwait(true);
            HostConfigurationMessage = "Paired device revoked on the daemon and removed from this device. You may now create a fresh pairing code.";
        }
        catch
        {
            HostConfigurationMessage = "Revocation failed or its result is uncertain. Test this pairing; if authentication now fails, ask the daemon administrator to verify the device list before re-pairing.";
            throw;
        }
        finally { ProjectionChanged?.Invoke(this, EventArgs.Empty); }
    }

    private async Task<AttachedProfileCredential> SelectAttachedCredentialAsync(Guid credentialId,
        CancellationToken cancellationToken)
    {
        await _attachedProfiles.SelectAsync(credentialId, cancellationToken).ConfigureAwait(true);
        var attached = await _attachedProfiles.LoadAsync(cancellationToken).ConfigureAwait(true)
            ?? throw new InvalidDataException("The selected paired daemon profile is missing.");
        if (attached.Profile.CredentialId != credentialId
            || attached.Profile.InstallationId != AndroidInstallationIdentity.GetOrCreate())
            throw new InvalidDataException("The selected credential belongs to another profile or installation.");
        return attached;
    }

    internal async Task SelectAnotherHostAsync()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (HostMode == AndroidHostMode.Unconfigured || MainActivity.Current is not { } activity
            || !await activity.ShowWorkspaceSwitchAsync()) return;
        HostConfigurationMessage = "Closing the current workspace…";
        ProjectionChanged?.Invoke(this, EventArgs.Empty);
        _captureTimer.Stop();
        _taskEditorTimer.Stop();
        _utilityEditorTimer.Stop();
        _navigationTimer.Stop();
        await SaveCaptureAsync();
        await SaveTaskEditorAsync();
        await SaveUtilityEditorAsync();
        await SaveNavigationAsync();
        await _timerNotifications.DetachAsync(clearNative: true);
        await _reminders.DetachAsync(clearNative: true);
        if (_projection is { } model)
        {
            model.PropertyChanged -= CaptureChanged;
            if (_observedTaskEditor is not null) _observedTaskEditor.PropertyChanged -= TaskEditorChanged;
            _observedTaskEditor = null;
            await model.SetPresentationActiveAsync(false);
            await model.DisposeAsync();
        }
        _projection = null;
        _currentBackend = null;
        _opening = null;
        await _session.ResetAsync();
        AndroidHostSelection.SaveUnconfigured();
        HostMode = AndroidHostMode.Unconfigured;
        _workspaceSelected = false;
        ResetTransientWorkspaceState();
        HostConfigurationMessage = "Choose the device workspace or the saved paired daemon. They remain separate and are not synchronized.";
        ProjectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ResetTransientWorkspaceState()
    {
        _captureStore = null;
        _taskEditorStore = null;
        _utilityEditorStore = null;
        _navigationStore = null;
        _recovery = null;
        _taskEditorRecovery = null;
        _utilityEditorRecovery = null;
        _navigationState = null;
        _activeAttachedProfile = null;
        _recoveryBlocked = false;
        _taskEditorRecoveryBlocked = false;
        _utilityEditorRecoveryBlocked = false;
        CaptureRecoveryMessage = string.Empty;
    }

    private void CaptureChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.CurrentSection)
            or nameof(MainWindowViewModel.TaskViewMode)
            or nameof(MainWindowViewModel.TaskBoardId)
            or nameof(MainWindowViewModel.TaskSortMode)
            or nameof(MainWindowViewModel.CalendarMode)
            or nameof(MainWindowViewModel.CalendarView)
            or nameof(MainWindowViewModel.CalendarAnchor)
            or nameof(MainWindowViewModel.SummaryGrouping)
            or nameof(MainWindowViewModel.SettingsPage)
            or nameof(MainWindowViewModel.SelectedJournalId))
        {
            if (!_restoringNavigation)
            {
                _navigationTimer.Stop();
                _navigationTimer.Start();
            }
        }
        if (e.PropertyName is nameof(MainWindowViewModel.TaskTitle) or nameof(MainWindowViewModel.SelectedProjectId))
        {
            if (IsRestoring || _recovery is not null || _recoveryBlocked) return;
            _captureTimer.Stop();
            _captureTimer.Start();
        }
        else if (e.PropertyName == nameof(MainWindowViewModel.IsTaskEditorOpen))
        {
            ObserveTaskEditor();
            _taskEditorTimer.Stop();
            if (_projection?.TaskEditor is null)
            {
                _ = SaveTaskEditorAsync();
                _ = RestoreUtilityEditorAsync();
            }
            else _taskEditorTimer.Start();
        }
        else if (e.PropertyName == nameof(MainWindowViewModel.IsUtilityEditorOpen))
        {
            _utilityEditorTimer.Stop();
            if (_projection?.IsUtilityEditorOpen == true) _utilityEditorTimer.Start();
            else _ = SaveUtilityEditorAsync();
        }
    }

    private void ObserveTaskEditor()
    {
        if (_observedTaskEditor is not null) _observedTaskEditor.PropertyChanged -= TaskEditorChanged;
        _observedTaskEditor = _projection?.TaskEditor;
        if (_observedTaskEditor is not null) _observedTaskEditor.PropertyChanged += TaskEditorChanged;
    }

    private void TaskEditorChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (IsRestoring || _taskEditorRecovery is not null || _taskEditorRecoveryBlocked) return;
        _taskEditorTimer.Stop();
        _taskEditorTimer.Start();
    }

    private async Task SaveCaptureAsync()
    {
        if (IsRestoring || _captureStore is null || _projection is null || _recovery is not null || _recoveryBlocked) return;
        try { await _captureStore.SaveAsync(_projection.CaptureTaskDraft(_workspaceId)); }
        catch (Exception)
        {
            CaptureRecoveryMessage = "Capture recovery could not be saved. Keep the app open and retry.";
            SharedCaptureChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task SaveTaskEditorAsync()
    {
        if (IsRestoring || _restoringTaskEditorRecovery || _taskEditorStore is null || _projection is null
            || _taskEditorRecovery is not null || _taskEditorRecoveryBlocked) return;
        try { await _taskEditorStore.SaveAsync(_projection.CaptureTaskEditorDraft(_workspaceId)); }
        catch (Exception)
        {
            CaptureRecoveryMessage = "Task-edit recovery could not be saved. Keep the app open and retry.";
            SharedCaptureChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task SaveUtilityEditorAsync()
    {
        if (IsRestoring || _restoringUtilityEditorRecovery || _utilityEditorStore is null
            || _projection is null || _utilityEditorRecovery is not null
            || _utilityEditorRecoveryBlocked) return;
        try { await _utilityEditorStore.SaveAsync(_projection.CaptureUtilityEditorDraft(_workspaceId)); }
        catch (Exception)
        {
            CaptureRecoveryMessage = "Utility-editor recovery could not be saved. Keep the app open and retry.";
            SharedCaptureChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task SaveNavigationAsync()
    {
        if (_restoringNavigation || _navigationState is not null
            || _navigationStore is null || _projection is null) return;
        try { await _navigationStore.SaveAsync(_projection.CaptureNavigationState(_workspaceId)); }
        catch (Exception exception)
        {
            global::Android.Util.Log.Warn("SnookNavigation", $"Save: {exception.GetType().Name}");
        }
    }

    internal async Task DiscardRecoveredCaptureAsync()
    {
        if (!HasBlockedRecovery)
        {
            CaptureRecoveryMessage = string.Empty;
            SharedCaptureChanged?.Invoke(this, EventArgs.Empty);
            return;
        }
        if (_captureStore is null || _taskEditorStore is null || _utilityEditorStore is null) return;
        try
        {
            await _captureStore.SaveAsync(null);
            await _taskEditorStore.SaveAsync(null);
            await _utilityEditorStore.SaveAsync(null);
            _recovery = null;
            _taskEditorRecovery = null;
            _utilityEditorRecovery = null;
            _recoveryBlocked = false;
            _taskEditorRecoveryBlocked = false;
            _utilityEditorRecoveryBlocked = false;
            CaptureRecoveryMessage = string.Empty;
        }
        catch (Exception) { CaptureRecoveryMessage = "The recovery copy could not be discarded. Retry when storage is available."; }
        SharedCaptureChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RestoreCapture()
    {
        if (_recovery is not { } draft || _projection is not { IsPresentationActive: true } model) return;
        if (model.RestoreTaskDraft(draft, _workspaceId))
        {
            _recovery = null;
            CaptureRecoveryMessage = draft.PendingRequest is null
                ? "Unsaved capture recovered. Review before adding the task."
                : "Capture recovered with an unconfirmed save. Retry unchanged to confirm it without creating a duplicate.";
        }
        else CaptureRecoveryMessage = "A capture recovery copy could not be opened in the current project or draft. It has not been overwritten.";
        SharedCaptureChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task RestoreTaskEditorAsync()
    {
        if (_restoringTaskEditorRecovery || _taskEditorRecovery is not { } draft
            || _projection is not { IsPresentationActive: true } model) return;
        _restoringTaskEditorRecovery = true;
        try
        {
            if (await model.RestoreTaskEditorDraftAsync(draft, _workspaceId))
            {
                _taskEditorRecovery = null;
                CaptureRecoveryMessage = string.IsNullOrEmpty(CaptureRecoveryMessage)
                    ? "Unsaved task edit recovered. Review it before saving."
                    : CaptureRecoveryMessage + " An unsaved task edit was also recovered.";
            }
            else CaptureRecoveryMessage = "A task-edit recovery copy could not be opened against the current workspace. It has not been overwritten.";
            SharedCaptureChanged?.Invoke(this, EventArgs.Empty);
        }
        finally { _restoringTaskEditorRecovery = false; }
    }

    private async Task RestoreUtilityEditorAsync()
    {
        if (_restoringUtilityEditorRecovery || _utilityEditorRecovery is not { } draft
            || _projection is not { IsPresentationActive: true } model
            || model.IsTaskEditorOpen) return;
        _restoringUtilityEditorRecovery = true;
        try
        {
            if (await model.RestoreUtilityEditorDraftAsync(draft, _workspaceId))
            {
                _utilityEditorRecovery = null;
                CaptureRecoveryMessage = string.IsNullOrEmpty(CaptureRecoveryMessage)
                    ? "Unsaved utility editor recovered. Review it before saving."
                    : CaptureRecoveryMessage + " An unsaved utility editor was also recovered.";
                _utilityEditorTimer.Start();
            }
            else CaptureRecoveryMessage = "A utility-editor recovery copy could not be opened against the current workspace. It has not been overwritten.";
            SharedCaptureChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception)
        {
            _utilityEditorRecoveryBlocked = true;
            CaptureRecoveryMessage = $"The utility-editor recovery copy could not be opened ({exception.GetType().Name}). Discard it to continue without that draft.";
            SharedCaptureChanged?.Invoke(this, EventArgs.Empty);
        }
        finally { _restoringUtilityEditorRecovery = false; }
    }

    private async Task RestoreNavigationAsync()
    {
        if (_restoringNavigation || _navigationState is not { } state
            || _projection is not { } model) return;
        _restoringNavigation = true;
        try
        {
            await model.RestoreNavigationStateAsync(state, _workspaceId);
        }
        finally
        {
            _navigationState = null;
            _restoringNavigation = false;
        }
    }
    internal SharedCaptureInbox SharedCapture { get; } = new();
    internal event EventHandler? SharedCaptureChanged;
    internal string SharedCaptureMessage { get; private set; } = string.Empty;

    internal void ReceiveSharedText(string text)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (IsRestoring) return;
        // The platform router is the first gate; the inbox remains the owning
        // validation and pending-draft gate before exposing review UI.
        SharedCaptureMessage = SharedCapture.TryOffer(AndroidEntryRouter.SendAction,
            AndroidEntryRouter.PlainTextMime, dataUri: null, text)
            ? "Shared text is ready to review. Nothing has been saved."
            : "Share not accepted. Use plain text up to 300 characters and resolve any pending share first.";
        SharedCaptureChanged?.Invoke(this, EventArgs.Empty);
    }

    internal void RejectEntry(bool share)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (IsRestoring) return;
        SharedCaptureMessage = share
            ? "Share not accepted. Use plain text up to 300 characters and resolve any pending share first."
            : "Entry not accepted. Open Snook normally or share plain text up to 300 characters.";
        SharedCaptureChanged?.Invoke(this, EventArgs.Empty);
    }

    internal void DiscardShare()
    {
        SharedCapture.Discard();
        SharedCaptureMessage = string.Empty;
        SharedCaptureChanged?.Invoke(this, EventArgs.Empty);
    }

    internal async Task ReviewShareAsync()
    {
        var model = await OpenAsync();
        var text = SharedCapture.Text;
        if (text is null)
            SharedCaptureMessage = "Shared text expired or was discarded. Share it again to review.";
        else if (!string.IsNullOrEmpty(model.TaskTitle) || model.IsTaskEditorOpen || model.IsUtilityEditorOpen)
            SharedCaptureMessage = "Finish or discard the current draft before reviewing shared text.";
        else
        {
            model.TaskTitle = text;
            SharedCapture.Discard();
            SharedCaptureMessage = "Review the task title and project, then choose Add task to save.";
            await ((AsyncCommand)model.SelectSectionCommand).ExecuteAsync("Tasks");
        }
        SharedCaptureChanged?.Invoke(this, EventArgs.Empty);
    }

    internal async Task OpenSectionAsync(string section)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (HostMode == AndroidHostMode.Unconfigured)
        {
            HostConfigurationMessage = "Choose a workspace before opening this shortcut. No workspace was created automatically.";
            ProjectionChanged?.Invoke(this, EventArgs.Empty);
            return;
        }
        var model = await OpenAsync();
        IsWorkspaceSelected = true;
        await ((AsyncCommand)model.SelectSectionCommand).ExecuteAsync(section);
        ProjectionChanged?.Invoke(this, EventArgs.Empty);
    }

    internal async Task OpenJournalEntryAsync()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (HostMode == AndroidHostMode.Unconfigured)
        {
            HostConfigurationMessage = "Choose a workspace before opening this shortcut. No workspace was created automatically.";
            ProjectionChanged?.Invoke(this, EventArgs.Empty);
            return;
        }
        var model = await OpenAsync();
        IsWorkspaceSelected = true;
        await model.OpenJournalEntryComposerAsync();
        ProjectionChanged?.Invoke(this, EventArgs.Empty);
    }

    internal async Task<string> HandleTimerActionAsync(string token, CancellationToken cancellationToken)
    {
        Dispatcher.UIThread.VerifyAccess();
        await OpenAsync();
        return await _timerNotifications.ExecuteAsync(token, cancellationToken);
    }

    internal async Task<string> OpenTimerAsync(string token)
    {
        Dispatcher.UIThread.VerifyAccess();
        var model = await OpenAsync();
        if (!await _timerNotifications.ValidateOpenAsync(token))
            return "That timer notification is stale or belongs to another workspace.";
        await ((AsyncCommand)model.SelectSectionCommand).ExecuteAsync("Time Tracker");
        return "";
    }

    internal async Task ReconcileTimerNotificationsAsync(CancellationToken cancellationToken)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (HostMode is not (AndroidHostMode.Independent or AndroidHostMode.Attached)) return;
        await OpenAsync();
        await _timerNotifications.ReconcileNowAsync(cancellationToken);
    }

    internal async Task ReconcileRemindersAsync(CancellationToken cancellationToken)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (HostMode is not (AndroidHostMode.Independent or AndroidHostMode.Attached)) return;
        await OpenAsync();
        await _reminders.ReconcileNowAsync(cancellationToken);
    }

    internal async Task FireReminderAsync(Guid taskId, Guid workspaceId,
        CancellationToken cancellationToken)
    {
        Dispatcher.UIThread.VerifyAccess();
        await OpenAsync();
        if (_workspaceId != workspaceId || _currentBackend is not { } backend) return;
        var tasks = await backend.SearchTasksAsync(includeCompleted: true,
            includeArchived: true, cancellationToken: cancellationToken);
        var item = ReminderNotificationSnapshot.Build(workspaceId, tasks,
            DateTimeOffset.UtcNow).Items.FirstOrDefault(value => value.TaskId == taskId);
        if (item is not null && item.DueAtUtc <= DateTimeOffset.UtcNow.AddMinutes(5)
            && item.DueAtUtc >= DateTimeOffset.UtcNow.AddDays(-1))
            ReminderCoordinator.Post(item, staleAttached: false);
        await _reminders.ReconcileNowAsync(cancellationToken);
    }

    internal bool IsWorkspaceSelected
    {
        get => _workspaceSelected;
        set { _workspaceSelected = value; UpdatePresentation(); }
    }

    internal void SetForeground(bool foreground)
    {
        _foreground = foreground;
        UpdatePresentation();
        if (foreground) _ = _timerNotifications.ReconcileNowAsync();
        if (foreground) _ = _reminders.ReconcileNowAsync();
        if (!foreground)
        {
            _captureTimer.Stop();
            _taskEditorTimer.Stop();
            _utilityEditorTimer.Stop();
            _navigationTimer.Stop();
            _ = SaveCaptureAsync();
            _ = SaveTaskEditorAsync();
            _ = SaveUtilityEditorAsync();
            _ = SaveNavigationAsync();
        }
    }

    internal async Task SetAppLockedAsync(bool locked)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_appLocked == locked) return;
        _appLocked = locked;
        await _session.SetLockedAsync(locked);
        if (locked)
        {
            await _timerNotifications.DetachAsync(clearNative: true);
            await _reminders.DetachAsync(clearNative: true);
            TimerWidgetCoordinator.RenderUnavailable("Unlock Snook to show workspace details");
            WorkspaceWidgetCoordinator.RenderUnavailable("Unlock Snook to show workspace details");
            return;
        }
        if (_currentBackend is not { } backend || _workspaceId == Guid.Empty) return;
        try
        {
            await _timerNotifications.AttachAsync(backend, _workspaceId);
            await _reminders.AttachAsync(backend, _workspaceId);
        }
        catch (Exception exception)
        {
            global::Android.Util.Log.Warn("SnookLock", $"Unlock reconcile: {exception.GetType().Name}");
        }
    }

    private void UpdatePresentation()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_projection is { } model)
            _ = UpdatePresentationAsync(model);
    }

    private async Task UpdatePresentationAsync(MainWindowViewModel model)
    {
        await model.SetPresentationActiveAsync(!IsRestoring && _foreground && _workspaceSelected);
        RestoreCapture();
        await RestoreTaskEditorAsync();
        await RestoreUtilityEditorAsync();
    }

    internal Task<MainWindowViewModel> OpenAsync()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (HostMode is not (AndroidHostMode.Independent or AndroidHostMode.Attached)
            && !_configuringIndependent && !_configuringAttached)
            return Task.FromException<MainWindowViewModel>(new InvalidOperationException(
                "Choose and confirm a workspace host before opening Snook."));
        // Retain one projection (and its editors) across every activity factory call.
        // A failed attempt can be retried explicitly from the host.
        if (_opening is null || _opening.IsFaulted || _opening.IsCanceled)
            _opening = OpenCoreAsync();
        return _opening;
    }

    private async Task<MainWindowViewModel> OpenCoreAsync()
    {
        var attached = HostMode == AndroidHostMode.Attached || _configuringAttached;
        AttachedProfileCredential? attachedCredential = null;
        IBackendClient backend;
        if (attached)
        {
            var noBackupRoot = global::Android.App.Application.Context.NoBackupFilesDir?.AbsolutePath
                ?? throw new InvalidOperationException("Private storage is unavailable.");
            if (File.Exists(Path.Combine(noBackupRoot, "attached", "workspace.db")))
                throw new InvalidDataException("Attached mode found an unexpected client database. Inspect and remove app data before reconnecting.");
            attachedCredential = await _attachedProfiles.LoadAsync()
                ?? throw new InvalidOperationException("The paired daemon profile is missing. Re-pairing is required.");
            if (attachedCredential.Profile.InstallationId != AndroidInstallationIdentity.GetOrCreate())
                throw new InvalidDataException("The paired credential belongs to another installation. Re-pairing is required.");
            backend = await _session.OpenAttachedAsync(_ => Task.FromResult<IBackendClient>(
                new DaemonBackendClient(attachedCredential.Profile.ServeOrigin,
                    attachedCredential.Credential, DaemonEndpointKind.TailnetServe)));
        }
        else backend = await _session.OpenIndependentAsync();
        var bootstrap = await backend.GetBootstrapAsync();
        _workspaceId = bootstrap.Workspace.Id;
        if (attachedCredential is not null && _workspaceId != attachedCredential.Profile.WorkspaceId)
            throw new InvalidOperationException("The paired daemon returned a different workspace. Re-pairing is required.");
        var directory = global::Android.App.Application.Context.NoBackupFilesDir!.AbsolutePath;
        _activeAttachedProfile = attachedCredential?.Profile;
        var sessionDirectory = attachedCredential is null
            ? Path.Combine(directory, "independent")
            : AttachedStateDirectory(directory, _workspaceId);
        _captureStore = new EncryptedCaptureStore(sessionDirectory, _workspaceId);
        _taskEditorStore = new EncryptedTaskEditorStore(sessionDirectory, _workspaceId);
        _utilityEditorStore = new EncryptedUtilityEditorStore(sessionDirectory, _workspaceId);
        _navigationStore = new NavigationStateStore(sessionDirectory, _workspaceId);
        try { _recovery = await _captureStore.LoadAsync(); }
        catch (Exception)
        {
            _recoveryBlocked = true;
            CaptureRecoveryMessage = "The encrypted capture recovery copy is unavailable. Discard it to start a new recoverable capture.";
        }
        try { _taskEditorRecovery = await _taskEditorStore.LoadAsync(); }
        catch (Exception)
        {
            _taskEditorRecoveryBlocked = true;
            CaptureRecoveryMessage = "The encrypted task-edit recovery copy is unavailable. Discard it before opening a recoverable editor.";
        }
        try { _utilityEditorRecovery = await _utilityEditorStore.LoadAsync(); }
        catch (Exception)
        {
            _utilityEditorRecoveryBlocked = true;
            CaptureRecoveryMessage = "The encrypted utility-editor recovery copy is unavailable. Discard it before opening another utility editor.";
        }
        try { _navigationState = await _navigationStore.LoadAsync(); }
        catch (Exception exception)
        {
            global::Android.Util.Log.Warn("SnookNavigation", $"Load: {exception.GetType().Name}");
        }
        var viewModel = new MainWindowViewModel(backend, ownsBackend: false,
            presentationActive: _foreground && _workspaceSelected);
        viewModel.HapticFeedback = kind => MainActivity.Current?.PerformSnookHaptic(kind);
        if (!attached)
        {
            viewModel.BackupExportAsync = () => AndroidWorkspaceFiles.BackupAsync(backend);
            viewModel.BackupReviewAsync = () => AndroidWorkspaceFiles.ReviewBackupAsync(backend, RestoreAsync);
            viewModel.JsonDocumentExportAsync = () => AndroidWorkspaceFiles.JsonAsync(backend);
            viewModel.CsvDocumentExportAsync = (start, end) => AndroidWorkspaceFiles.CsvAsync(backend, start, end);
            viewModel.JsonDocumentShareAsync = () => AndroidWorkspaceFiles.ShareJsonAsync(backend);
            viewModel.CsvDocumentShareAsync = (start, end) => AndroidWorkspaceFiles.ShareCsvAsync(backend, start, end);
        }
        else
        {
            viewModel.BackupExportAsync = () => AndroidWorkspaceFiles.BackupAsync(backend);
            viewModel.BackupReviewAsync = () => AndroidWorkspaceFiles.ReviewBackupAsync(
                backend, RestoreAsync, RestoreAttachedAsync);
            viewModel.JsonDocumentExportAsync = () => AndroidWorkspaceFiles.JsonAsync(backend);
            viewModel.CsvDocumentExportAsync = (start, end) => AndroidWorkspaceFiles.CsvAsync(backend, start, end);
            viewModel.JsonDocumentShareAsync = () => AndroidWorkspaceFiles.ShareJsonAsync(backend);
            viewModel.CsvDocumentShareAsync = (start, end) => AndroidWorkspaceFiles.ShareCsvAsync(backend, start, end);
        }
        viewModel.TimerNotificationSetupAsync = () => _timerNotifications.EnableAsync();
        viewModel.ReminderNotificationSetupAsync = () => _reminders.EnableAndTestAsync();
        viewModel.AppLockSetupAsync = () => MainActivity.Current?.ConfigureAppLockAsync()
            ?? Task.FromResult("Return to Snook before changing app lock.");
        viewModel.NotificationPrivacySetupAsync = async () =>
        {
            var enabled = NotificationPrivacy.Toggle();
            await _timerNotifications.ReconcileNowAsync();
            await _reminders.ReconcileNowAsync();
            return enabled
                ? "Task and activity titles may now appear in private notifications. Lock-screen public versions remain redacted."
                : "Notification details are hidden. Timer and reminder surfaces now use generic text.";
        };
        viewModel.ScreenPrivacySetupAsync = () => MainActivity.Current?.ConfigureScreenPrivacyAsync()
            ?? Task.FromResult("Return to Snook before changing screen privacy.");
        if (OperatingSystem.IsAndroidVersionAtLeast(36))
            viewModel.TimerLiveUpdateSetupAsync = TimerNotificationCoordinator.ReviewPromotionAccessAsync;
        _projection = viewModel;
        viewModel.UtilityEditorCheckpointAsync = SaveUtilityEditorAsync;
        try
        {
            await viewModel.InitializeAsync();
            _currentBackend = backend;
            if (!_appLocked)
            {
                try { await _timerNotifications.AttachAsync(backend, _workspaceId); }
                catch (Exception exception)
                {
                    global::Android.Util.Log.Warn("SnookTimer", $"Attach: {exception.GetType().Name}");
                }
                try { await _reminders.AttachAsync(backend, _workspaceId); }
                catch (Exception exception)
                {
                    global::Android.Util.Log.Warn("SnookReminder", $"Attach: {exception.GetType().Name}");
                }
            }
            await RestoreNavigationAsync();
            RestoreCapture();
            await RestoreTaskEditorAsync();
            await RestoreUtilityEditorAsync();
            var captureStore = _captureStore;
            var workspaceId = _workspaceId;
            viewModel.TaskCreationCheckpointAsync = async (intent, request) =>
            {
                if (IsRestoring || !ReferenceEquals(_projection, viewModel) || _recovery is not null || _recoveryBlocked)
                    throw new InvalidOperationException("Resolve the recovery copy or workspace handoff before saving a task.");
                await captureStore.SaveAsync(new TaskCaptureDraft(1, workspaceId, DateTimeOffset.UtcNow, intent, request));
            };
            viewModel.PropertyChanged += CaptureChanged;
            ObserveTaskEditor();
            await SaveNavigationAsync();
            SharedCaptureChanged?.Invoke(this, EventArgs.Empty);
            return viewModel;
        }
        catch
        {
            _projection = null;
            _currentBackend = null;
            try { await _timerNotifications.DetachAsync(clearNative: true, CancellationToken.None); }
            catch (Exception exception) { global::Android.Util.Log.Warn("SnookTimer", $"Open cleanup: {exception.GetType().Name}"); }
            try { await _reminders.DetachAsync(clearNative: true, CancellationToken.None); }
            catch (Exception exception) { global::Android.Util.Log.Warn("SnookReminder", $"Open cleanup: {exception.GetType().Name}"); }
            await viewModel.DisposeAsync();
            throw;
        }
    }

    private async Task<string> RestoreAsync(string database, WorkspaceRestoreRequest request, CancellationToken cancellation)
    {
        var activity = MainActivity.Current;
        if (IsRestoring || _projection is not { } old || activity?.CanReviewBackup() != true)
            return "Restore cancelled. Return to the unlocked workspace and review again.";
        IsRestoring = true;
        _captureTimer.Stop();
        _taskEditorTimer.Stop();
        _utilityEditorTimer.Stop();
        _navigationTimer.Stop();
        SharedCaptureChanged?.Invoke(this, EventArgs.Empty);
        var replaced = false;
        var oldBackend = _currentBackend;
        try
        {
            await old.SetPresentationActiveAsync(false);
            // Consent explicitly discards durable capture. Drain its serialized writes
            // before replacement so process death cannot replay an old receipt/draft
            // against a rolled-back or different workspace.
            if (_captureStore is { } capture) await capture.SaveAsync(null);
            if (_taskEditorStore is { } taskEditor) await taskEditor.SaveAsync(null);
            if (_utilityEditorStore is { } utilityEditor) await utilityEditor.SaveAsync(null);
            if (_navigationStore is { } navigation) await navigation.SaveAsync(null);
            cancellation.ThrowIfCancellationRequested();
            if (!ReferenceEquals(MainActivity.Current, activity) || !activity.CanReviewBackup())
                throw new OperationCanceledException("Restore left the foreground before dispatch.");
            await _timerNotifications.DetachAsync(clearNative: true, cancellationToken: cancellation);
            await _reminders.DetachAsync(clearNative: true, cancellationToken: cancellation);
            await _session.RestoreIndependentAsync(database, request, cancellation);
            replaced = true;
            NotificationSpike.Remove();
            old.PropertyChanged -= CaptureChanged;
            if (_observedTaskEditor is not null) _observedTaskEditor.PropertyChanged -= TaskEditorChanged;
            _observedTaskEditor = null;
            await old.DisposeAsync();
            _projection = null;
            _currentBackend = null;
            _recovery = null;
            _taskEditorRecovery = null;
            _utilityEditorRecovery = null;
            _navigationState = null;
            _recoveryBlocked = false;
            _taskEditorRecoveryBlocked = false;
            _utilityEditorRecoveryBlocked = false;
            SharedCapture.Discard();
            SharedCaptureMessage = string.Empty;
            _opening = OpenCoreAsync();
            await _opening;
            CaptureRecoveryMessage = "Backup restored and reopened. Previous database retained privately for recovery.";
            return CaptureRecoveryMessage;
        }
        catch (Exception exception)
        {
            global::Android.Util.Log.Warn("SnookRestore", $"Session handoff: {exception.GetType().Name}");
            if (replaced || _session.State == WorkspaceSessionState.Fatal)
            {
                old.PropertyChanged -= CaptureChanged;
                if (_observedTaskEditor is not null) _observedTaskEditor.PropertyChanged -= TaskEditorChanged;
                _observedTaskEditor = null;
                await old.DisposeAsync();
                _projection = null;
                _currentBackend = null;
                _opening = Task.FromException<MainWindowViewModel>(new InvalidOperationException("Workspace handoff requires recovery."));
                CaptureRecoveryMessage = "Restore needs recovery. Do not retry blindly. Restart the app and inspect the workspace; the previous database is retained if replacement began.";
            }
            else
            {
                CaptureRecoveryMessage = "Restore was not applied. Your current workspace and open drafts remain; review the backup again.";
                if (oldBackend is not null)
                {
                    try { await _timerNotifications.AttachAsync(oldBackend, _workspaceId, CancellationToken.None); }
                    catch (Exception notificationException)
                    {
                        global::Android.Util.Log.Warn("SnookTimer", $"Reattach: {notificationException.GetType().Name}");
                        CaptureRecoveryMessage += " Timer notifications need to be reopened from Settings.";
                    }
                    try { await _reminders.AttachAsync(oldBackend, _workspaceId, CancellationToken.None); }
                    catch (Exception reminderException)
                    {
                        global::Android.Util.Log.Warn("SnookReminder", $"Restore rollback: {reminderException.GetType().Name}");
                    }
                }
                if (_captureStore is { } capture)
                {
                    try { await capture.SaveAsync(_recovery ?? old.CaptureTaskDraft(_workspaceId)); }
                    catch (Exception) { CaptureRecoveryMessage += " Capture recovery could not be resaved; keep the app open."; }
                }
                if (_taskEditorStore is { } taskEditor)
                {
                    try { await taskEditor.SaveAsync(_taskEditorRecovery ?? old.CaptureTaskEditorDraft(_workspaceId)); }
                    catch (Exception) { CaptureRecoveryMessage += " Task-edit recovery could not be resaved; keep the app open."; }
                }
                if (_utilityEditorStore is { } utilityEditor)
                {
                    try { await utilityEditor.SaveAsync(_utilityEditorRecovery ?? old.CaptureUtilityEditorDraft(_workspaceId)); }
                    catch (Exception) { CaptureRecoveryMessage += " Utility-editor recovery could not be resaved; keep the app open."; }
                }
                if (_navigationStore is { } navigation)
                {
                    try { await navigation.SaveAsync(old.CaptureNavigationState(_workspaceId)); }
                    catch (Exception) { global::Android.Util.Log.Warn("SnookNavigation", "Restore rollback navigation save failed."); }
                }
            }
            return CaptureRecoveryMessage;
        }
        finally
        {
            IsRestoring = false;
            UpdatePresentation();
            SharedCaptureChanged?.Invoke(this, EventArgs.Empty);
            ProjectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task<string> RestoreAttachedAsync(RemoteBackupStage stage,
        CancellationToken cancellationToken)
    {
        var activity = MainActivity.Current;
        if (IsRestoring || HostMode != AndroidHostMode.Attached
            || _projection is not { } old || _currentBackend is not IBackendDocumentTransfer
                { CanRestoreDocuments: true } transfer
            || activity?.CanReviewBackup() != true || stage.CurrentWorkspaceId != _workspaceId)
            return "Attached restore cancelled. Return to the unlocked paired workspace and review again.";
        IsRestoring = true;
        _captureTimer.Stop();
        _taskEditorTimer.Stop();
        _utilityEditorTimer.Stop();
        _navigationTimer.Stop();
        SharedCaptureChanged?.Invoke(this, EventArgs.Empty);
        var committed = false;
        try
        {
            await old.SetPresentationActiveAsync(false);
            await SaveCaptureAsync();
            await SaveTaskEditorAsync();
            await SaveUtilityEditorAsync();
            await SaveNavigationAsync();
            cancellationToken.ThrowIfCancellationRequested();
            if (!ReferenceEquals(MainActivity.Current, activity) || !activity.CanReviewBackup())
                throw new OperationCanceledException("Restore left the foreground before dispatch.");
            await _timerNotifications.DetachAsync(clearNative: true,
                cancellationToken: cancellationToken);
            await _reminders.DetachAsync(clearNative: true,
                cancellationToken: cancellationToken);
            var result = await transfer.RestoreStagedBackupAsync(stage, cancellationToken);
            committed = true;
            if (result.WorkspaceId != stage.Incoming.Workspace.Id)
                throw new InvalidDataException("The daemon confirmed a different restored workspace.");
            await _session.ResetAsync(CancellationToken.None);
            await _attachedProfiles.RebindSelectedWorkspaceAsync(stage.CurrentWorkspaceId,
                result.WorkspaceId, CancellationToken.None);
            if (_captureStore is { } capture) await capture.SaveAsync(null);
            if (_taskEditorStore is { } taskEditor) await taskEditor.SaveAsync(null);
            if (_utilityEditorStore is { } utilityEditor) await utilityEditor.SaveAsync(null);
            if (_navigationStore is { } navigation) await navigation.SaveAsync(null);
            NotificationSpike.Remove();
            old.PropertyChanged -= CaptureChanged;
            if (_observedTaskEditor is not null) _observedTaskEditor.PropertyChanged -= TaskEditorChanged;
            _observedTaskEditor = null;
            await old.DisposeAsync();
            _projection = null;
            _currentBackend = null;
            _opening = null;
            ResetTransientWorkspaceState();
            SharedCapture.Discard();
            SharedCaptureMessage = string.Empty;
            _workspaceSelected = true;
            _opening = OpenCoreAsync();
            await _opening;
            CaptureRecoveryMessage = result.RevokedOtherDeviceCount == 0
                ? "Daemon backup restored and reopened. The previous server database is retained for operator recovery."
                : $"Daemon backup restored and reopened. {result.RevokedOtherDeviceCount} other paired device(s) were revoked for the new workspace identity.";
            return CaptureRecoveryMessage;
        }
        catch (Exception exception)
        {
            global::Android.Util.Log.Warn("SnookRestore", $"Attached handoff: {exception.GetType().Name}");
            try { await _timerNotifications.DetachAsync(clearNative: true, CancellationToken.None); }
            catch (Exception notificationException)
            {
                global::Android.Util.Log.Warn("SnookTimer", $"Attached restore cleanup: {notificationException.GetType().Name}");
            }
            try { await _reminders.DetachAsync(clearNative: true, CancellationToken.None); }
            catch (Exception notificationException)
            {
                global::Android.Util.Log.Warn("SnookReminder", $"Attached restore cleanup: {notificationException.GetType().Name}");
            }
            old.PropertyChanged -= CaptureChanged;
            if (_observedTaskEditor is not null) _observedTaskEditor.PropertyChanged -= TaskEditorChanged;
            _observedTaskEditor = null;
            try { await old.DisposeAsync(); }
            catch (Exception disposeException)
            {
                global::Android.Util.Log.Warn("SnookRestore", $"Attached projection cleanup: {disposeException.GetType().Name}");
            }
            try { await _session.ResetAsync(CancellationToken.None); }
            catch (Exception resetException)
            {
                global::Android.Util.Log.Warn("SnookRestore", $"Attached session cleanup: {resetException.GetType().Name}");
            }
            _projection = null;
            _currentBackend = null;
            _opening = null;
            AndroidHostSelection.SaveUnconfigured();
            HostMode = AndroidHostMode.Unconfigured;
            _workspaceSelected = false;
            ResetTransientWorkspaceState();
            HostConfigurationMessage = committed
                ? "The daemon restored the backup, but local profile handoff failed. Use Repair on this saved pairing before reconnecting; do not restore again."
                : "Restore outcome is uncertain. Use Repair or Test on this saved pairing and inspect the daemon before retrying.";
            return HostConfigurationMessage;
        }
        finally
        {
            IsRestoring = false;
            UpdatePresentation();
            SharedCaptureChanged?.Invoke(this, EventArgs.Empty);
            ProjectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private static async Task<IBackendClient> OpenBackendAsync(CancellationToken cancellation)
    {
        var directory = global::Android.App.Application.Context.NoBackupFilesDir?.AbsolutePath
            ?? throw new InvalidOperationException("Private storage is unavailable.");
        // Separate from the synthetic probe database. No desktop profile, external
        // path, connection intent or credential can redirect this development host.
        var backend = new SnookBackend(new SqliteStore(Path.Combine(directory, "independent", "workspace.db")));
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

    private static string AttachedStateDirectory(string noBackupRoot, Guid workspaceId)
    {
        var legacy = Path.Combine(noBackupRoot, "attached", "state");
        var target = Path.Combine(legacy, workspaceId.ToString("N"));
        if (Directory.Exists(legacy)
            && (File.GetAttributes(legacy) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Attached recovery storage must not be a symbolic link.");
        Directory.CreateDirectory(target);
        if ((File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Attached workspace recovery storage must not be a symbolic link.");
        foreach (var name in new[] { "capture-draft.bin", "task-editor-draft.bin", "navigation-state.json" })
        {
            var source = Path.Combine(legacy, name);
            var destination = Path.Combine(target, name);
            if (File.Exists(source) && !File.Exists(destination))
                File.Move(source, destination);
        }
        return target;
    }
}
