using Android.Content;

namespace Snook.Android;

internal static class NotificationPrivacy
{
    private const string Preferences = "snook-notification-privacy";
    private const string DetailKey = "show-private-details";

    internal static bool ShowDetails => global::Android.App.Application.Context
        .GetSharedPreferences(Preferences, FileCreationMode.Private)!
        .GetBoolean(DetailKey, false);

    internal static bool Toggle()
    {
        var next = !ShowDetails;
        if (!global::Android.App.Application.Context.GetSharedPreferences(Preferences,
                FileCreationMode.Private)!.Edit()!.PutBoolean(DetailKey, next)!.Commit())
            throw new IOException("Notification privacy preference could not be saved.");
        return next;
    }
}
