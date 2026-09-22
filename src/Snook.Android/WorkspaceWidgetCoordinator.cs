using Android.App;
using Android.Appwidget;
using Android.Content;
using Android.Views;
using Android.Widget;
using Snook.Application;
using Snook.Contracts;
using Snook.Domain;

namespace Snook.Android;

internal static class WorkspaceWidgetCoordinator
{
    internal static async Task RenderAsync(IBackendClient backend, BootstrapSnapshot bootstrap,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(bootstrap);
        IReadOnlyList<HabitProgress> habits = [];
        var habitsAvailable = true;
        if (HasWidgets(typeof(HabitsWidgetProvider)))
        {
            try
            {
                habits = await backend.GetHabitsAsync(new HabitQuery(Days: 1),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                habitsAvailable = false;
                global::Android.Util.Log.Warn("SnookWidget", $"Habits: {exception.GetType().Name}");
            }
        }

        var projection = WorkspaceWidgetProjection.Build(bootstrap, habits);
        RenderToday(projection);
        if (habitsAvailable) RenderHabits(projection);
        else RenderHabitsUnavailable("Habits unavailable · tap to retry");
        RenderQuickCapture(configured: true);
    }

    internal static void RenderUnavailable(string message)
    {
        RenderTodayUnavailable(message);
        RenderHabitsUnavailable(message);
        RenderQuickCapture(configured: false);
    }

    internal static void RenderQuickCapture(bool configured)
    {
        Update(typeof(QuickCaptureWidgetProvider), Resource.Layout.snook_capture_widget,
            (context, views) =>
            {
                views.SetTextViewText(Resource.Id.capture_widget_label,
                    configured ? "＋  Quick capture" : "Open Snook to choose a workspace");
                SetOpenIntent(views, Resource.Id.capture_widget_root, context,
                    configured ? AndroidEntryRouter.QuickCaptureAction
                        : AndroidEntryRouter.MainAction, 0x534E43);
            });
    }

    private static void RenderToday(WorkspaceWidgetSnapshot projection)
    {
        Update(typeof(TodayWidgetProvider), Resource.Layout.snook_today_widget,
            (context, views) =>
            {
                SetRows(views,
                    [Resource.Id.today_widget_task_one, Resource.Id.today_widget_task_two,
                        Resource.Id.today_widget_task_three],
                    projection.NextTasks.Select(item => item.Title).ToArray(),
                    "No priority tasks");
                views.SetTextViewText(Resource.Id.today_widget_count,
                    projection.OpenTaskCount == 1 ? "1 open task"
                    : $"{projection.OpenTaskCount} open tasks");
                SetOpenIntent(views, Resource.Id.today_widget_root, context,
                    AndroidEntryRouter.TodayAction, 0x534E54);
            });
    }

    private static void RenderTodayUnavailable(string message)
    {
        Update(typeof(TodayWidgetProvider), Resource.Layout.snook_today_widget,
            (context, views) =>
            {
                SetRows(views,
                    [Resource.Id.today_widget_task_one, Resource.Id.today_widget_task_two,
                        Resource.Id.today_widget_task_three], [message], message);
                views.SetTextViewText(Resource.Id.today_widget_count, string.Empty);
                SetOpenIntent(views, Resource.Id.today_widget_root, context,
                    AndroidEntryRouter.MainAction, 0x534E54);
            });
    }

    private static void RenderHabits(WorkspaceWidgetSnapshot projection)
    {
        Update(typeof(HabitsWidgetProvider), Resource.Layout.snook_habits_widget,
            (context, views) =>
            {
                var rows = projection.Habits.Select(item =>
                    $"{(item.CompletedToday ? "✓" : "○")}  {item.Name}"
                    + (item.CurrentStreak > 0 ? $" · {item.CurrentStreak}d" : string.Empty)).ToArray();
                SetRows(views,
                    [Resource.Id.habits_widget_one, Resource.Id.habits_widget_two,
                        Resource.Id.habits_widget_three], rows, "No active habits");
                views.SetTextViewText(Resource.Id.habits_widget_state,
                    projection.Habits.Count == 0 ? "Tap to create one" : "Tap to review or check in");
                SetOpenIntent(views, Resource.Id.habits_widget_root, context,
                    AndroidEntryRouter.HabitsAction, 0x534E48);
            });
    }

    private static void RenderHabitsUnavailable(string message)
    {
        Update(typeof(HabitsWidgetProvider), Resource.Layout.snook_habits_widget,
            (context, views) =>
            {
                SetRows(views,
                    [Resource.Id.habits_widget_one, Resource.Id.habits_widget_two,
                        Resource.Id.habits_widget_three], [message], message);
                views.SetTextViewText(Resource.Id.habits_widget_state, string.Empty);
                SetOpenIntent(views, Resource.Id.habits_widget_root, context,
                    AndroidEntryRouter.HabitsAction, 0x534E48);
            });
    }

    private static void SetRows(RemoteViews views, int[] ids, string[] rows,
        string empty)
    {
        for (var index = 0; index < ids.Length; index++)
        {
            var text = index < rows.Length ? rows[index] : index == 0 && rows.Length == 0 ? empty : string.Empty;
            views.SetTextViewText(ids[index], text);
            views.SetViewVisibility(ids[index], text.Length == 0 ? ViewStates.Gone : ViewStates.Visible);
        }
    }

    private static bool HasWidgets(Type providerType)
    {
        var context = global::Android.App.Application.Context;
        using var manager = AppWidgetManager.GetInstance(context);
        using var providerClass = Java.Lang.Class.FromType(providerType);
        using var component = new ComponentName(context, providerClass);
        return manager?.GetAppWidgetIds(component)?.Length > 0;
    }

    private static void Update(Type providerType, int layout,
        Action<Context, RemoteViews> configure)
    {
        var context = global::Android.App.Application.Context;
        using var manager = AppWidgetManager.GetInstance(context);
        using var providerClass = Java.Lang.Class.FromType(providerType);
        using var component = new ComponentName(context, providerClass);
        foreach (var id in manager?.GetAppWidgetIds(component) ?? [])
        {
            using var views = new RemoteViews(context.PackageName, layout);
            configure(context, views);
            manager!.UpdateAppWidget(id, views);
        }
    }

    private static void SetOpenIntent(RemoteViews views, int viewId, Context context,
        string action, int requestCode)
    {
        using var intent = new Intent(context, typeof(MainActivity));
        intent.SetAction(action);
        intent.SetFlags(ActivityFlags.ClearTop | ActivityFlags.SingleTop);
        using var pending = PendingIntent.GetActivity(context, requestCode, intent,
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
        views.SetOnClickPendingIntent(viewId, pending);
    }

    internal static void QueueRefresh(AppWidgetProvider provider)
    {
        var pending = provider.GoAsync();
        if (pending is not null) _ = RefreshAsync(pending);
    }

    private static async Task RefreshAsync(BroadcastReceiver.PendingResult pending)
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

[BroadcastReceiver(Name = "org.snook.spike.TodayWidgetProvider", Enabled = true, Exported = false)]
[IntentFilter([AppWidgetManager.ActionAppwidgetUpdate])]
[MetaData("android.appwidget.provider", Resource = "@xml/snook_today_widget_info")]
public sealed class TodayWidgetProvider : AppWidgetProvider
{
    public override void OnUpdate(Context? context, AppWidgetManager? appWidgetManager,
        int[]? appWidgetIds)
    {
        base.OnUpdate(context, appWidgetManager, appWidgetIds);
        WorkspaceWidgetCoordinator.QueueRefresh(this);
    }
}

[BroadcastReceiver(Name = "org.snook.spike.HabitsWidgetProvider", Enabled = true, Exported = false)]
[IntentFilter([AppWidgetManager.ActionAppwidgetUpdate])]
[MetaData("android.appwidget.provider", Resource = "@xml/snook_habits_widget_info")]
public sealed class HabitsWidgetProvider : AppWidgetProvider
{
    public override void OnUpdate(Context? context, AppWidgetManager? appWidgetManager,
        int[]? appWidgetIds)
    {
        base.OnUpdate(context, appWidgetManager, appWidgetIds);
        WorkspaceWidgetCoordinator.QueueRefresh(this);
    }
}

[BroadcastReceiver(Name = "org.snook.spike.QuickCaptureWidgetProvider", Enabled = true, Exported = false)]
[IntentFilter([AppWidgetManager.ActionAppwidgetUpdate])]
[MetaData("android.appwidget.provider", Resource = "@xml/snook_capture_widget_info")]
public sealed class QuickCaptureWidgetProvider : AppWidgetProvider
{
    public override void OnUpdate(Context? context, AppWidgetManager? appWidgetManager,
        int[]? appWidgetIds)
    {
        base.OnUpdate(context, appWidgetManager, appWidgetIds);
        WorkspaceWidgetCoordinator.RenderQuickCapture(
            new AndroidHostSelection().Load() != AndroidHostMode.Unconfigured);
    }
}
