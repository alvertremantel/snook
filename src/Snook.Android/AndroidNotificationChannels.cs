using Android.App;
using Android.Content;

namespace Snook.Android;

internal static class AndroidNotificationChannels
{
    internal static void Ensure()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(26)) return;
        var context = global::Android.App.Application.Context;
        using var manager = (NotificationManager)context.GetSystemService(Context.NotificationService)!;
        Create(manager, "active-timers-v1", "Active timers", NotificationImportance.Low,
            "Ongoing running and paused timers with validated actions");
        Create(manager, "task-reminders-v1", "Task reminders", NotificationImportance.Default,
            "Inexact reminders for explicitly timed task deadlines");
        Create(manager, "habit-updates-v1", "Habit updates", NotificationImportance.Default,
            "Optional ordinary habit notices; never promoted ongoing updates");
        Create(manager, "connection-status-v1", "Connection status", NotificationImportance.Low,
            "Optional daemon connection and recovery status");
    }

    private static void Create(NotificationManager manager, string id, string name,
        NotificationImportance importance, string description)
    {
        using var channel = new NotificationChannel(id, name, importance)
        { Description = description };
        manager.CreateNotificationChannel(channel);
    }
}
