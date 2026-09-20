using Avalonia.Threading;
using Android.Content;
using Snook.Application;
using Snook.Contracts;
using Snook.Persistence.Sqlite;

namespace Snook.Android;

internal static class ReminderBackground
{
    internal static async Task FireAsync(BroadcastReceiver.PendingResult pending,
        Guid taskId, Guid workspaceId)
    {
        try
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            if (Avalonia.Application.Current is SpikeApp app)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                    app.FireReminderAsync(taskId, workspaceId, budget.Token));
                return;
            }
            await WithSelectedBackendAsync(async (backend, selectedWorkspace, root) =>
            {
                if (selectedWorkspace != workspaceId) return;
                var tasks = await backend.SearchTasksAsync(includeCompleted: true,
                    includeArchived: true, cancellationToken: budget.Token).ConfigureAwait(false);
                var item = ReminderNotificationSnapshot.Build(workspaceId, tasks,
                    DateTimeOffset.UtcNow).Items.FirstOrDefault(value => value.TaskId == taskId);
                if (item is not null && item.DueAtUtc <= DateTimeOffset.UtcNow.AddMinutes(5)
                    && item.DueAtUtc >= DateTimeOffset.UtcNow.AddDays(-1))
                    ReminderCoordinator.Post(item, staleAttached: false);
                await ReconcileBackendAsync(root, backend, workspaceId, budget.Token).ConfigureAwait(false);
            }, budget.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            global::Android.Util.Log.Warn("SnookReminder", $"Alarm: {exception.GetType().Name}");
            TryPostAttachedCache(taskId, workspaceId);
        }
        finally { pending.Finish(); }
    }

    internal static async Task ReconcileSelectedAsync(CancellationToken cancellationToken)
    {
        if (Avalonia.Application.Current is SpikeApp app)
        {
            await Dispatcher.UIThread.InvokeAsync(() => app.ReconcileRemindersAsync(cancellationToken));
            return;
        }
        await WithSelectedBackendAsync((backend, workspaceId, root) =>
            ReconcileBackendAsync(root, backend, workspaceId, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task ReconcileBackendAsync(string root, IBackendClient backend,
        Guid workspaceId, CancellationToken cancellationToken)
    {
        var coordinator = new ReminderCoordinator(root);
        try { await coordinator.AttachAsync(backend, workspaceId, cancellationToken).ConfigureAwait(false); }
        finally { await coordinator.DetachAsync(clearNative: false, CancellationToken.None).ConfigureAwait(false); }
    }

    private static async Task WithSelectedBackendAsync(
        Func<IBackendClient, Guid, string, Task> action, CancellationToken cancellationToken)
    {
        var root = global::Android.App.Application.Context.NoBackupFilesDir?.AbsolutePath
            ?? throw new InvalidOperationException("Private storage is unavailable.");
        var host = new AndroidHostSelection().Load();
        if (host == AndroidHostMode.Attached)
        {
            using var profiles = new AttachedProfileStore(root);
            var attached = await profiles.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (attached is null || attached.Profile.InstallationId != AndroidInstallationIdentity.GetOrCreate()) return;
            await using var daemon = new DaemonBackendClient(attached.Profile.ServeOrigin,
                attached.Credential, DaemonEndpointKind.TailnetServe);
            var bootstrap = await daemon.GetBootstrapAsync(cancellationToken).ConfigureAwait(false);
            if (bootstrap.Workspace.Id != attached.Profile.WorkspaceId)
                throw new InvalidDataException("The paired daemon changed workspace identity.");
            await action(daemon, bootstrap.Workspace.Id, root).ConfigureAwait(false);
            return;
        }
        if (host != AndroidHostMode.Independent) return;
        var database = Path.Combine(root, "independent", "workspace.db");
        if (!File.Exists(database)) return;
        await using var backend = new SnookBackend(new SqliteStore(database));
        await backend.InitializeAsync(cancellationToken).ConfigureAwait(false);
        var snapshot = await backend.GetBootstrapAsync(cancellationToken).ConfigureAwait(false);
        await action(backend, snapshot.Workspace.Id, root).ConfigureAwait(false);
    }

    private static void TryPostAttachedCache(Guid taskId, Guid workspaceId)
    {
        try
        {
            if (new AndroidHostSelection().Load() != AndroidHostMode.Attached) return;
            var root = global::Android.App.Application.Context.NoBackupFilesDir?.AbsolutePath;
            if (root is null) return;
            var snapshot = new ReminderIndexStore(root).Load(DateTimeOffset.UtcNow);
            var item = snapshot?.WorkspaceId == workspaceId
                ? snapshot.Items.FirstOrDefault(value => value.TaskId == taskId) : null;
            if (item is not null && item.DueAtUtc <= DateTimeOffset.UtcNow.AddMinutes(5)
                && item.DueAtUtc >= DateTimeOffset.UtcNow.AddDays(-1))
                ReminderCoordinator.Post(item, staleAttached: true);
        }
        catch (Exception exception)
        {
            global::Android.Util.Log.Warn("SnookReminder", $"Cached alarm: {exception.GetType().Name}");
        }
    }
}
