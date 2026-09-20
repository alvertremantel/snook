using Android.App;
using Android.Content;

namespace Snook.Android;

internal static class NotificationSpike
{
    private const string ChannelId = "platform-spike-timer";
    private const int NotificationId = 7902;

    internal static async Task<string> ShowAsync()
    {
        var activity = MainActivity.Current;
        if (activity is null) return "No active Android activity. Reopen the probe and try again.";
        if (!await activity.RequestNotificationPermissionAsync())
            return "Notifications unavailable. Local checks still work. To opt in after denial or dismissal, enable notifications in Android app settings.";

        var context = global::Android.App.Application.Context;
        using var manager = (NotificationManager)context.GetSystemService(Context.NotificationService)!;
        using var channel = new NotificationChannel(ChannelId, "Synthetic timer probe", NotificationImportance.Low);
        manager.CreateNotificationChannel(channel);
        using var actualChannel = manager.GetNotificationChannel(ChannelId);
        if (!manager.AreNotificationsEnabled() || actualChannel?.Importance == NotificationImportance.None)
            return "Notifications or the probe channel are disabled in Android settings. Local checks still work.";

        using var intent = new Intent(context, typeof(MainActivity));
        intent.SetFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop);
        using var contentIntent = PendingIntent.GetActivity(context, NotificationId, intent,
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
        using var builder = new Notification.Builder(context, ChannelId);
        builder.SetSmallIcon(global::Android.Resource.Drawable.IcLockIdleAlarm)
            .SetContentTitle("Snook synthetic timer")
            .SetContentText("Platform probe only — remove from the probe screen")
            .SetWhen(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            .SetUsesChronometer(true)
            .SetOngoing(true)
            .SetOnlyAlertOnce(true)
            .SetVisibility(NotificationVisibility.Private)
            .SetContentIntent(contentIntent);
        using var notification = builder.Build();
        manager.Notify(NotificationId, notification);
        return "Posted ordinary ongoing notification with a system chronometer. Inspect the notification shade. No foreground service or app tick loop. Live Update promotion is not requested or verified.";
    }

    internal static void Remove()
    {
        using var manager = (NotificationManager)global::Android.App.Application.Context.GetSystemService(Context.NotificationService)!;
        manager.Cancel(NotificationId);
    }
}
