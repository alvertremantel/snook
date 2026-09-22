using Android.App;
using Android.Appwidget;
using Android.Content;
using Android.OS;
using Android.Views;
using Android.Widget;
using Avalonia.Threading;
using Snook.Application;
using Snook.Contracts;
using Snook.Persistence.Sqlite;

namespace Snook.Android;

internal static class TimerWidgetCoordinator
{
    internal static void Render(TimerNotificationSnapshot projection)
    {
        ArgumentNullException.ThrowIfNull(projection);
        var context = global::Android.App.Application.Context;
        using var manager = AppWidgetManager.GetInstance(context);
        using var providerClass = Java.Lang.Class.FromType(typeof(TimerWidgetProvider));
        using var component = new ComponentName(context, providerClass);
        var ids = manager?.GetAppWidgetIds(component) ?? [];
        foreach (var id in ids)
        {
            using var views = Views(context, projection);
            manager!.UpdateAppWidget(id, views);
        }
    }

    internal static void RenderUnavailable(string message)
    {
        var context = global::Android.App.Application.Context;
        using var manager = AppWidgetManager.GetInstance(context);
        using var providerClass = Java.Lang.Class.FromType(typeof(TimerWidgetProvider));
        using var component = new ComponentName(context, providerClass);
        var ids = manager?.GetAppWidgetIds(component) ?? [];
        foreach (var id in ids)
        {
            using var views = BaseViews(context);
            views.SetTextViewText(Resource.Id.widget_title, "Snook timers");
            views.SetTextViewText(Resource.Id.widget_state, message);
            views.SetViewVisibility(Resource.Id.widget_chronometer, ViewStates.Gone);
            views.SetTextViewText(Resource.Id.widget_count, string.Empty);
            manager!.UpdateAppWidget(id, views);
        }
        WorkspaceWidgetCoordinator.RenderUnavailable(message);
    }

    internal static async Task RefreshSelectedAsync(CancellationToken cancellationToken)
    {
        var context = global::Android.App.Application.Context;
        var root = context.NoBackupFilesDir?.AbsolutePath
            ?? throw new InvalidOperationException("Private storage is unavailable.");
        var host = new AndroidHostSelection().Load();
        if (host == AndroidHostMode.Unconfigured)
        {
            RenderUnavailable("Choose a workspace in Snook");
            return;
        }
        if (Avalonia.Application.Current is SpikeApp app)
        {
            await Dispatcher.UIThread.InvokeAsync(
                () => app.ReconcileTimerNotificationsAsync(cancellationToken));
            return;
        }

        IBackendClient? backend = null;
        AttachedProfileStore? profiles = null;
        try
        {
            if (host == AndroidHostMode.Independent)
            {
                var database = Path.Combine(root, "independent", "workspace.db");
                if (!File.Exists(database))
                {
                    RenderUnavailable("Device workspace unavailable");
                    return;
                }
                var embedded = new SnookBackend(new SqliteStore(database));
                await embedded.InitializeAsync(cancellationToken).ConfigureAwait(false);
                backend = embedded;
            }
            else
            {
                profiles = new AttachedProfileStore(root);
                var attached = await profiles.LoadAsync(cancellationToken).ConfigureAwait(false);
                if (attached is null || attached.Profile.InstallationId != AndroidInstallationIdentity.GetOrCreate())
                    throw new InvalidDataException("Paired profile is unavailable.");
                backend = new DaemonBackendClient(attached.Profile.ServeOrigin,
                    attached.Credential, DaemonEndpointKind.TailnetServe);
            }
            var bootstrap = await backend.GetBootstrapAsync(cancellationToken).ConfigureAwait(false);
            Render(TimerNotificationProjection.Build(bootstrap));
            await WorkspaceWidgetCoordinator.RenderAsync(backend, bootstrap,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (backend is not null) await backend.DisposeAsync().ConfigureAwait(false);
            profiles?.Dispose();
        }
    }

    private static RemoteViews Views(Context context, TimerNotificationSnapshot projection)
    {
        var views = BaseViews(context);
        var item = projection.Items.Count == 0 ? null : projection.Items[0];
        views.SetTextViewText(Resource.Id.widget_title, item?.Title ?? "Snook timers");
        views.SetTextViewText(Resource.Id.widget_state, item is null
            ? "No active timers"
            : item.State == Snook.Domain.SessionState.Running ? item.Context : $"{item.Context} · Paused");
        views.SetTextViewText(Resource.Id.widget_count, projection.TotalActiveCount > 1
            ? $"{projection.TotalActiveCount} active timers" : string.Empty);
        if (item is { State: Snook.Domain.SessionState.Running })
        {
            var @base = SystemClock.ElapsedRealtime() - item.DisplayedMilliseconds;
            views.SetLong(Resource.Id.widget_chronometer, "setBase", @base);
            views.SetBoolean(Resource.Id.widget_chronometer, "setStarted", true);
            views.SetViewVisibility(Resource.Id.widget_chronometer, ViewStates.Visible);
        }
        else if (item is not null)
        {
            views.SetBoolean(Resource.Id.widget_chronometer, "setStarted", false);
            views.SetChronometer(Resource.Id.widget_chronometer, SystemClock.ElapsedRealtime()
                - item.DisplayedMilliseconds, null, false);
            views.SetViewVisibility(Resource.Id.widget_chronometer, ViewStates.Visible);
        }
        else views.SetViewVisibility(Resource.Id.widget_chronometer, ViewStates.Gone);
        return views;
    }

    private static RemoteViews BaseViews(Context context)
    {
        var views = new RemoteViews(context.PackageName, Resource.Layout.snook_timer_widget);
        using var intent = new Intent(context, typeof(MainActivity));
        intent.SetAction(AndroidEntryRouter.TimeTrackerAction);
        intent.SetFlags(ActivityFlags.ClearTop | ActivityFlags.SingleTop);
        using var pending = PendingIntent.GetActivity(context, 0x534E57, intent,
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
        views.SetOnClickPendingIntent(Resource.Id.widget_root, pending);
        return views;
    }
}

[BroadcastReceiver(Name = "org.snook.spike.TimerWidgetProvider", Enabled = true,
    Exported = false)]
[IntentFilter([AppWidgetManager.ActionAppwidgetUpdate])]
[MetaData("android.appwidget.provider", Resource = "@xml/snook_timer_widget_info")]
public sealed class TimerWidgetProvider : AppWidgetProvider
{
    public override void OnUpdate(Context? context, AppWidgetManager? appWidgetManager,
        int[]? appWidgetIds)
    {
        base.OnUpdate(context, appWidgetManager, appWidgetIds);
        var pending = GoAsync();
        if (pending is null) return;
        _ = RefreshAsync(pending);
    }

    private static async Task RefreshAsync(PendingResult pending)
    {
        try
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(9));
            await TimerWidgetCoordinator.RefreshSelectedAsync(budget.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            global::Android.Util.Log.Warn("SnookWidget", $"Refresh: {exception.GetType().Name}");
            TimerWidgetCoordinator.RenderUnavailable("Workspace unavailable · tap to retry");
        }
        finally { pending.Finish(); }
    }
}
