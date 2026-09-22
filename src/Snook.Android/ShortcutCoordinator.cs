using Android.Content;
using Android.Content.PM;
using Android.Graphics.Drawables;
using Snook.Application;

namespace Snook.Android;

internal static class ShortcutCoordinator
{
    internal static void Ensure()
    {
        var context = global::Android.App.Application.Context;
        using var manager = context.GetSystemService(Context.ShortcutService) as ShortcutManager;
        if (manager is null) return;
        using var taskIcon = Icon.CreateWithResource(context, global::Android.Resource.Drawable.IcInputAdd);
        using var timerIcon = Icon.CreateWithResource(context, global::Android.Resource.Drawable.IcLockIdleAlarm);
        using var journalIcon = Icon.CreateWithResource(context, global::Android.Resource.Drawable.IcMenuEdit);
        using var todayIcon = Icon.CreateWithResource(context, global::Android.Resource.Drawable.IcMenuToday);
        using var captureIntent = new Intent(context, typeof(MainActivity));
        captureIntent.SetAction(AndroidEntryRouter.QuickCaptureAction);
        captureIntent.SetFlags(ActivityFlags.ClearTop | ActivityFlags.SingleTop);
        using var trackerIntent = new Intent(context, typeof(MainActivity));
        trackerIntent.SetAction(AndroidEntryRouter.TimeTrackerAction);
        trackerIntent.SetFlags(ActivityFlags.ClearTop | ActivityFlags.SingleTop);
        using var journalIntent = new Intent(context, typeof(MainActivity));
        journalIntent.SetAction(AndroidEntryRouter.JournalEntryAction);
        journalIntent.SetFlags(ActivityFlags.ClearTop | ActivityFlags.SingleTop);
        using var todayIntent = new Intent(context, typeof(MainActivity));
        todayIntent.SetAction(AndroidEntryRouter.TodayAction);
        todayIntent.SetFlags(ActivityFlags.ClearTop | ActivityFlags.SingleTop);
        using var capture = new ShortcutInfo.Builder(context, "quick-capture-v1")
            .SetShortLabel("Quick capture")
            .SetLongLabel("Capture a Snook task")
            .SetIcon(taskIcon)
            .SetIntent(captureIntent)
            .Build();
        using var tracker = new ShortcutInfo.Builder(context, "time-tracker-v1")
            .SetShortLabel("Start or resume")
            .SetLongLabel("Start or resume a Snook timer")
            .SetIcon(timerIcon)
            .SetIntent(trackerIntent)
            .Build();
        using var journal = new ShortcutInfo.Builder(context, "journal-entry-v1")
            .SetShortLabel("Journal entry")
            .SetLongLabel("Write a Snook journal entry")
            .SetIcon(journalIcon)
            .SetIntent(journalIntent)
            .Build();
        using var today = new ShortcutInfo.Builder(context, "today-v1")
            .SetShortLabel("Today")
            .SetLongLabel("Open Today in Snook")
            .SetIcon(todayIcon)
            .SetIntent(todayIntent)
            .Build();
        if (!manager.SetDynamicShortcuts([capture, tracker, journal, today]))
            global::Android.Util.Log.Warn("SnookShortcuts", "Android rejected dynamic shortcuts.");
    }
}
