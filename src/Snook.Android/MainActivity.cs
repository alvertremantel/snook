using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Content;
using Android.Views;
using Avalonia.Android;
using Snook.Application;

namespace Snook.Android;

[Activity(Label = "Snook platform spike", Theme = "@style/SnookSpikeTheme", MainLauncher = true,
    Exported = true, LaunchMode = LaunchMode.SingleTop, WindowSoftInputMode = SoftInput.AdjustResize)]
[IntentFilter([Intent.ActionSend], Categories = [Intent.CategoryDefault], DataMimeType = "text/plain")]
public sealed class MainActivity : AvaloniaMainActivity
{
    private const int NotificationRequest = 7902;
    private const int AppLockRequest = 7903;
    private static WeakReference<MainActivity>? _current;
    private TaskCompletionSource<bool>? _permissionResult;
    private EditorBackCallback? _editorBack;
    private global::Android.App.AlertDialog? _backupReview;
    private global::Android.App.AlertDialog? _independentSetup;
    private global::Android.App.AlertDialog? _forgetPairing;
    private global::Android.App.AlertDialog? _revokePairing;
    private global::Android.App.AlertDialog? _repairPairing;
    private global::Android.App.AlertDialog? _switchWorkspace;
    private TaskCompletionSource<bool>? _appLockResult;
    private bool _appLockPromptActive;
    private bool _configuringAppLock;
    private bool _requestedAppLockEnabled;
    private bool _unlockSucceeded;
    private bool _sensitiveScreen;
    internal bool IsResumed { get; private set; }

    internal static MainActivity? Current => _current?.TryGetTarget(out var activity) == true ? activity : null;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        try { ShortcutCoordinator.Ensure(); }
        catch (Exception exception)
        {
            global::Android.Util.Log.Warn("SnookShortcuts", $"Setup: {exception.GetType().Name}");
        }
        if (savedInstanceState is null) ReceiveEntry(Intent);
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        ReceiveEntry(intent);
    }

    private static void ReceiveEntry(Intent? intent)
    {
        if (intent is null) return;
        var app = Avalonia.Application.Current as SpikeApp;
        try
        {
            var hasText = intent.HasExtra(Intent.ExtraText);
            var hasToken = intent.HasExtra(TimerNotificationCoordinator.TokenExtra);
            // Extract primitive metadata only. Never follow a URI, accept a stream,
            // traverse ClipData, or deserialize any caller-owned parcelable object.
            var data = intent.Data;
            var route = AndroidEntryRouter.Route(new(
                intent.Action,
                intent.Type,
                data?.Scheme,
                data?.Host,
                data?.Path,
                data?.Query,
                data?.Fragment,
                data?.Port ?? -1,
                hasToken
                    ? intent.GetStringExtra(TimerNotificationCoordinator.TokenExtra)
                    : hasText ? intent.GetStringExtra(Intent.ExtraText) : null,
                intent.Extras?.Size() ?? 0,
                hasText,
                hasToken,
                intent.HasExtra(Intent.ExtraStream),
                intent.ClipData is not null));
            app?.ReceiveEntry(route);
        }
        catch (Exception)
        {
            app?.ReceiveEntry(new(AndroidEntryKind.Rejected));
        }
    }

    protected override void OnStart()
    {
        base.OnStart();
        _editorBack = new EditorBackCallback(this);
        OnBackPressedDispatcher.AddCallback(this, _editorBack);
        ApplyLockVisibility();
    }

    protected override void OnStop()
    {
        _editorBack?.Remove();
        _editorBack?.Dispose();
        _editorBack = null;
        (Avalonia.Application.Current as SpikeApp)?.SetActivityVisible(this, false);
        if (!_appLockPromptActive) _unlockSucceeded = false;
        base.OnStop();
    }

    private sealed class EditorBackCallback(MainActivity activity) : AndroidX.Activity.OnBackPressedCallback(true)
    {
        public override void HandleOnBackPressed()
        {
            if (activity.Content is AndroidHostView host && host.HandleBack()) return;
            Enabled = false;
            try { activity.OnBackPressedDispatcher.OnBackPressed(); }
            finally { if (ReferenceEquals(activity._editorBack, this)) Enabled = true; }
        }
    }

    public override bool DispatchKeyEvent(global::Android.Views.KeyEvent? e)
    {
        // Avalonia maps the Android Back key into its keyboard path before the
        // activity callback. Do not let that path discard an editor first.
        // IME-consumed Back never reaches this post-IME dispatch stage.
        if (e?.KeyCode == global::Android.Views.Keycode.Back && Content is AndroidHostView { HasOpenEditor: true } host)
        {
            if (e.Action == global::Android.Views.KeyEventActions.Up && !e.IsCanceled) host.HandleBack();
            return true;
        }
        return base.DispatchKeyEvent(e);
    }

    protected override void OnResume()
    {
        base.OnResume();
        IsResumed = true;
        _current = new(this);
        if (IsAppLockEnabled && !_unlockSucceeded && !_appLockPromptActive)
            BeginDeviceCredentialPrompt(enableAfterSuccess: false);
        else ApplyLockVisibility();
    }

    protected override void OnPause()
    {
        IsResumed = false;
        _backupReview?.Dismiss();
        _independentSetup?.Dismiss();
        _forgetPairing?.Dismiss();
        _revokePairing?.Dismiss();
        _repairPairing?.Dismiss();
        _switchWorkspace?.Dismiss();
        base.OnPause();
    }

    internal bool CanReviewBackup()
    {
        using var keyguard = GetSystemService(KeyguardService) as KeyguardManager;
        return IsResumed && !IsFinishing && !IsDestroyed && keyguard is { IsDeviceLocked: false };
    }

    internal Task<bool> ShowBackupReviewAsync(string message)
    {
        if (!CanReviewBackup() || _backupReview is not null) return Task.FromResult(false);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var builder = new AlertDialog.Builder(this);
        builder.SetTitle("Verified backup review");
        builder.SetMessage(message);
        builder.SetNegativeButton("Keep current workspace", (_, _) => { });
        builder.SetPositiveButton("Replace workspace", (_, _) => completion.TrySetResult(CanReviewBackup()));
        var dialog = builder.Create()!;
        _backupReview = dialog;
        dialog.DismissEvent += (_, _) =>
        {
            if (ReferenceEquals(_backupReview, dialog)) _backupReview = null;
            completion.TrySetResult(false);
            dialog.Dispose();
        };
        dialog.Show();
        return completion.Task;
    }

    internal Task<bool> ShowIndependentSetupAsync(bool existing)
    {
        if (!CanReviewBackup() || _independentSetup is not null) return Task.FromResult(false);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var builder = new AlertDialog.Builder(this);
        builder.SetTitle(existing ? "Open the device workspace?" : "Create an independent workspace?");
        builder.SetMessage(existing
            ? "Snook will reopen the existing private workspace on this device. It is separate from every paired daemon and is not synchronized or merged."
            : "Snook will create one private workspace on this device. It works offline and is not synchronized with another Snook workspace. You are responsible for creating verified backups.");
        builder.SetNegativeButton("Not now", (_, _) => { });
        builder.SetPositiveButton(existing ? "Open device workspace" : "Create private workspace",
            (_, _) => completion.TrySetResult(CanReviewBackup()));
        var dialog = builder.Create()!;
        _independentSetup = dialog;
        dialog.DismissEvent += (_, _) =>
        {
            if (ReferenceEquals(_independentSetup, dialog)) _independentSetup = null;
            completion.TrySetResult(false);
            dialog.Dispose();
        };
        dialog.Show();
        return completion.Task;
    }

    internal Task<bool> ShowWorkspaceSwitchAsync()
    {
        if (!CanReviewBackup() || _switchWorkspace is not null) return Task.FromResult(false);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var builder = new AlertDialog.Builder(this);
        builder.SetTitle("Choose another workspace?");
        builder.SetMessage("Snook will checkpoint recoverable drafts, close this workspace connection, and clear its timer notifications before showing the host chooser. It will not copy, merge, delete, or silently open another workspace.");
        builder.SetNegativeButton("Stay here", (_, _) => { });
        builder.SetPositiveButton("Choose workspace", (_, _) => completion.TrySetResult(CanReviewBackup()));
        var dialog = builder.Create()!;
        _switchWorkspace = dialog;
        dialog.DismissEvent += (_, _) =>
        {
            if (ReferenceEquals(_switchWorkspace, dialog)) _switchWorkspace = null;
            completion.TrySetResult(false);
            dialog.Dispose();
        };
        dialog.Show();
        return completion.Task;
    }

    internal Task<bool> ShowForgetPairingAsync()
    {
        if (!CanReviewBackup() || _forgetPairing is not null) return Task.FromResult(false);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var builder = new AlertDialog.Builder(this);
        builder.SetTitle("Forget this paired daemon?");
        builder.SetMessage("Snook will remove this device's encrypted credential and connection profile. It will not delete or copy workspace data, open a local database, or revoke the daemon-side device record. Ask the daemon administrator to revoke that record before creating a new pairing code. Recoverable drafts remain bound to the same workspace identity.");
        builder.SetNegativeButton("Keep pairing", (_, _) => { });
        builder.SetPositiveButton("Forget pairing", (_, _) => completion.TrySetResult(CanReviewBackup()));
        var dialog = builder.Create()!;
        _forgetPairing = dialog;
        dialog.DismissEvent += (_, _) =>
        {
            if (ReferenceEquals(_forgetPairing, dialog)) _forgetPairing = null;
            completion.TrySetResult(false);
            dialog.Dispose();
        };
        dialog.Show();
        return completion.Task;
    }

    internal Task<bool> ShowRevokePairingAsync()
    {
        if (!CanReviewBackup() || _revokePairing is not null) return Task.FromResult(false);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var builder = new AlertDialog.Builder(this);
        builder.SetTitle("Revoke this paired device?");
        builder.SetMessage("The daemon will permanently revoke this app installation's credential and close its live change stream. Workspace data is not deleted, copied, or synchronized. A fresh pairing code will be required to reconnect.");
        builder.SetNegativeButton("Keep pairing", (_, _) => { });
        builder.SetPositiveButton("Revoke device", (_, _) => completion.TrySetResult(CanReviewBackup()));
        var dialog = builder.Create()!;
        _revokePairing = dialog;
        dialog.DismissEvent += (_, _) =>
        {
            if (ReferenceEquals(_revokePairing, dialog)) _revokePairing = null;
            completion.TrySetResult(false);
            dialog.Dispose();
        };
        dialog.Show();
        return completion.Task;
    }

    internal Task<bool> ShowRepairPairingAsync(Guid savedWorkspaceId,
        Guid daemonWorkspaceId, string daemonWorkspaceName)
    {
        if (!CanReviewBackup() || _repairPairing is not null) return Task.FromResult(false);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var builder = new AlertDialog.Builder(this);
        builder.SetTitle("Repair restored workspace identity?");
        builder.SetMessage($"The authenticated daemon now reports {daemonWorkspaceName} ({daemonWorkspaceId}), while this device saved {savedWorkspaceId}. Update only this non-secret local profile after an uncertain server restore? No workspace data will be copied, restored, or merged.");
        builder.SetNegativeButton("Cancel", (_, _) => { });
        builder.SetPositiveButton("Repair profile", (_, _) => completion.TrySetResult(CanReviewBackup()));
        var dialog = builder.Create()!;
        _repairPairing = dialog;
        dialog.DismissEvent += (_, _) =>
        {
            if (ReferenceEquals(_repairPairing, dialog)) _repairPairing = null;
            completion.TrySetResult(false);
            dialog.Dispose();
        };
        dialog.Show();
        return completion.Task;
    }

    protected override void OnDestroy()
    {
        (Avalonia.Application.Current as SpikeApp)?.SetActivityVisible(this, false);
        _permissionResult?.TrySetResult(false);
        _appLockResult?.TrySetResult(false);
        if (ReferenceEquals(Current, this)) _current = null;
        base.OnDestroy();
    }

    internal Task<bool> RequestNotificationPermissionAsync()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(33) ||
            CheckSelfPermission(global::Android.Manifest.Permission.PostNotifications) == Permission.Granted)
            return Task.FromResult(true);

        if (_permissionResult is not null) return _permissionResult.Task;
        using var preferences = GetSharedPreferences("native-spike", global::Android.Content.FileCreationMode.Private)!;
        if (preferences.GetBoolean("notification-permission-requested", false)) return Task.FromResult(false);
        using var editor = preferences.Edit()!;
        editor.PutBoolean("notification-permission-requested", true);
        if (!editor.Commit()) return Task.FromResult(false);
        _permissionResult = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RequestPermissions([global::Android.Manifest.Permission.PostNotifications], NotificationRequest);
        return _permissionResult.Task;
    }

    internal void ShowTransientMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message) || message.Length > 240) return;
        RunOnUiThread(() => global::Android.Widget.Toast.MakeText(this, message,
            global::Android.Widget.ToastLength.Short)?.Show());
    }

    internal void PerformSnookHaptic(Snook.UI.UiHaptic kind)
    {
        var feedback = kind == Snook.UI.UiHaptic.Invalid
            ? FeedbackConstants.LongPress : FeedbackConstants.ContextClick;
        Window?.DecorView?.PerformHapticFeedback(feedback);
    }

    internal Task<string> ConfigureAppLockAsync()
    {
        if (!IsResumed || IsFinishing || IsDestroyed)
            return Task.FromResult("Return to Snook before changing app lock.");
        using var keyguard = GetSystemService(KeyguardService) as KeyguardManager;
        if (keyguard?.IsDeviceSecure != true)
            return Task.FromResult("Set a device PIN, pattern, password, or biometric in Android settings before enabling app lock.");
        if (_appLockResult is not null)
            return CompleteAppLockConfigurationAsync(_appLockResult.Task, IsAppLockEnabled);
        var disabling = IsAppLockEnabled;
        _appLockResult = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _configuringAppLock = true;
        BeginDeviceCredentialPrompt(enableAfterSuccess: !disabling);
        return CompleteAppLockConfigurationAsync(_appLockResult.Task, disabling);
    }

    internal Task<string> ConfigureScreenPrivacyAsync()
    {
        var preferences = GetSharedPreferences("snook-screen-privacy", FileCreationMode.Private)!;
        var next = !preferences.GetBoolean("block-capture", false);
        if (!preferences.Edit()!.PutBoolean("block-capture", next)!.Commit())
            return Task.FromResult("Screen privacy could not be changed.");
        ApplyLockVisibility();
        return Task.FromResult(next
            ? "Screenshots and Android recent-task previews are blocked for the workspace."
            : "Ordinary workspace screenshots are allowed again. Locked screens remain protected.");
    }

    internal void SetSensitiveScreen(bool sensitive)
    {
        if (_sensitiveScreen == sensitive) return;
        _sensitiveScreen = sensitive;
        ApplyLockVisibility();
    }

    private static async Task<string> CompleteAppLockConfigurationAsync(Task<bool> result, bool disabling)
    {
        var accepted = await result.ConfigureAwait(true);
        return accepted
            ? disabling ? "App lock disabled after device authentication."
                : "App lock enabled. Snook will hide workspace UI whenever the activity leaves the foreground."
            : "App lock was not changed. Core workspace features remain available.";
    }

    private bool IsAppLockEnabled => GetSharedPreferences("snook-app-lock",
        FileCreationMode.Private)!.GetBoolean("enabled", false);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Interoperability", "CA1422:Validate platform compatibility",
        Justification = "The system device-credential activity remains the API 26 fallback and is intentionally used through API 36; AndroidX biometric integration is not required for this optional gate.")]
    private void BeginDeviceCredentialPrompt(bool enableAfterSuccess)
    {
        using var keyguard = GetSystemService(KeyguardService) as KeyguardManager;
        using var intent = keyguard?.CreateConfirmDeviceCredentialIntent(
            enableAfterSuccess ? "Enable Snook app lock" : "Unlock Snook",
            "Authenticate with this device's secure screen lock.");
        if (intent is null)
        {
            _appLockResult?.TrySetResult(false);
            _appLockResult = null;
            return;
        }
        _appLockPromptActive = true;
        _requestedAppLockEnabled = enableAfterSuccess;
        ApplyLockVisibility();
        StartActivityForResult(intent, AppLockRequest);
    }

    protected override void OnActivityResult(int requestCode,
        [global::Android.Runtime.GeneratedEnum] Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode != AppLockRequest) return;
        var accepted = resultCode == Result.Ok;
        if (accepted)
        {
            if (_configuringAppLock)
                GetSharedPreferences("snook-app-lock", FileCreationMode.Private)!
                    .Edit()!.PutBoolean("enabled", _requestedAppLockEnabled)!.Commit();
            _unlockSucceeded = true;
            Window?.DecorView?.PerformHapticFeedback(FeedbackConstants.ContextClick);
        }
        _appLockPromptActive = false;
        _configuringAppLock = false;
        _appLockResult?.TrySetResult(accepted);
        _appLockResult = null;
        ApplyLockVisibility();
    }

    private void ApplyLockVisibility()
    {
        var unlocked = !IsAppLockEnabled || _unlockSucceeded;
        var blockCapture = !unlocked || _sensitiveScreen || GetSharedPreferences("snook-screen-privacy",
            FileCreationMode.Private)!.GetBoolean("block-capture", false);
        if (blockCapture) Window?.AddFlags(WindowManagerFlags.Secure);
        else Window?.ClearFlags(WindowManagerFlags.Secure);
        if (Window?.DecorView is { } decor)
            decor.Visibility = unlocked ? ViewStates.Visible : ViewStates.Invisible;
        if (Avalonia.Application.Current is { } application && application is SpikeApp app)
        {
            app.SetActivityVisible(this, IsResumed && unlocked);
            app.SetAppLocked(!unlocked);
        }
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions,
        [global::Android.Runtime.GeneratedEnum] Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        if (requestCode != NotificationRequest) return;
        _permissionResult?.TrySetResult(grantResults.Length == 1 && grantResults[0] == Permission.Granted);
        _permissionResult = null;
    }
}
