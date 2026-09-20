using Android.App;
using Android.Content;
using Avalonia.Threading;
using Snook.Application;
using Snook.Persistence.Sqlite;

namespace Snook.Android;

/// <summary>
/// Rebuilds ordinary timer notifications from committed independent-workspace
/// state after system events that discard or invalidate their native presentation.
/// It never creates a workspace and never runs before credential-protected storage
/// is available.
/// </summary>
[BroadcastReceiver(Name = "org.snook.spike.TimerSystemEventReceiver", Enabled = true,
    Exported = false, DirectBootAware = false)]
[IntentFilter([Intent.ActionBootCompleted, Intent.ActionMyPackageReplaced,
    Intent.ActionTimeChanged, Intent.ActionTimezoneChanged])]
public sealed class TimerSystemEventReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is null || !IsSupported(intent?.Action)) return;
        var pending = GoAsync();
        if (pending is null) return;
        _ = CompleteAsync(pending);
    }

    private static bool IsSupported(string? action) => action is Intent.ActionBootCompleted
        or Intent.ActionMyPackageReplaced or Intent.ActionTimeChanged or Intent.ActionTimezoneChanged;

    private static async Task CompleteAsync(PendingResult pending)
    {
        try
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(9));
            if (Avalonia.Application.Current is SpikeApp app)
                await Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    await app.ReconcileTimerNotificationsAsync(budget.Token);
                    await app.ReconcileRemindersAsync(budget.Token);
                });
            else
            {
                await ReconcileSelectedHostAsync(budget.Token).ConfigureAwait(false);
                await ReminderBackground.ReconcileSelectedAsync(budget.Token).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            global::Android.Util.Log.Warn("SnookTimer", $"System event: {exception.GetType().Name}");
        }
        finally { pending.Finish(); }
    }

    private static async Task ReconcileSelectedHostAsync(CancellationToken cancellationToken)
    {
        var host = new AndroidHostSelection().Load();
        var root = global::Android.App.Application.Context.NoBackupFilesDir?.AbsolutePath
            ?? throw new InvalidOperationException("Private storage is unavailable.");
        if (host == AndroidHostMode.Attached)
        {
            using var profiles = new AttachedProfileStore(root);
            var attached = await profiles.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (attached is null) return;
            if (attached.Profile.InstallationId != AndroidInstallationIdentity.GetOrCreate())
                throw new InvalidDataException("The paired credential belongs to another installation.");
            await using var daemon = new DaemonBackendClient(attached.Profile.ServeOrigin,
                attached.Credential, DaemonEndpointKind.TailnetServe);
            var attachedSnapshot = await daemon.GetBootstrapAsync(cancellationToken).ConfigureAwait(false);
            if (attachedSnapshot.Workspace.Id != attached.Profile.WorkspaceId)
                throw new InvalidDataException("The paired daemon changed workspace identity.");
            await ReconcileAsync(root, daemon, attachedSnapshot.Workspace.Id, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (host != AndroidHostMode.Independent) return;
        var database = Path.Combine(root, "independent", "workspace.db");
        if (!File.Exists(database)) return;

        await using var backend = new SnookBackend(new SqliteStore(database));
        await backend.InitializeAsync(cancellationToken).ConfigureAwait(false);
        var snapshot = await backend.GetBootstrapAsync(cancellationToken).ConfigureAwait(false);
        await ReconcileAsync(root, backend, snapshot.Workspace.Id, cancellationToken).ConfigureAwait(false);
    }

    private static async Task ReconcileAsync(string root, Snook.Contracts.IBackendClient backend,
        Guid workspaceId, CancellationToken cancellationToken)
    {
        var coordinator = new TimerNotificationCoordinator(root);
        try { await coordinator.AttachAsync(backend, workspaceId, cancellationToken).ConfigureAwait(false); }
        finally { await coordinator.DetachAsync(clearNative: false, CancellationToken.None).ConfigureAwait(false); }
    }
}
