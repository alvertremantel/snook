using Android.Content;

namespace Snook.Android;

internal static class AndroidInstallationIdentity
{
    private const string PreferencesName = "snook-installation";
    private const string IdentityKey = "installation-id";

    internal static Guid GetOrCreate()
    {
        using var preferences = global::Android.App.Application.Context.GetSharedPreferences(
            PreferencesName, FileCreationMode.Private)
            ?? throw new IOException("Installation preferences are unavailable.");
        var value = preferences.GetString(IdentityKey, null);
        if (value is not null)
        {
            if (Guid.TryParseExact(value, "N", out var existing) && existing != Guid.Empty)
                return existing;
            throw new InvalidDataException("The installation identity is invalid. Clear app data before pairing.");
        }

        var created = Guid.NewGuid();
        using var editor = preferences.Edit()
            ?? throw new IOException("Installation preferences could not be edited.");
        editor.PutString(IdentityKey, created.ToString("N"));
        if (!editor.Commit()) throw new IOException("Installation identity could not be saved.");
        return created;
    }
}
