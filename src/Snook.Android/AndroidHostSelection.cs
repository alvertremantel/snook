using Android.Content;

namespace Snook.Android;

internal enum AndroidHostMode
{
    Unconfigured,
    Independent,
    Attached
}

/// <summary>
/// Device-local host selection. It contains no workspace data or credentials.
/// Existing development installs are adopted only when their private database is
/// already present; a clean install remains unconfigured until explicit consent.
/// </summary>
internal sealed class AndroidHostSelection
{
    private const string PreferencesName = "snook-host-selection";
    private const string ModeKey = "mode";
    private const string IndependentValue = "independent";
    private const string AttachedValue = "attached";
    private const string UnconfiguredValue = "unconfigured";
    private readonly string _databasePath;

    internal AndroidHostSelection()
    {
        var context = global::Android.App.Application.Context;
        var root = context.NoBackupFilesDir?.AbsolutePath
            ?? throw new InvalidOperationException("Private storage is unavailable.");
        _databasePath = Path.Combine(root, "independent", "workspace.db");
    }

    internal AndroidHostMode Load()
    {
        using var preferences = global::Android.App.Application.Context.GetSharedPreferences(
            PreferencesName, FileCreationMode.Private);
        var selected = preferences?.GetString(ModeKey, null);
        if (string.Equals(selected, UnconfiguredValue, StringComparison.Ordinal))
            return AndroidHostMode.Unconfigured;
        if (string.Equals(selected, AttachedValue, StringComparison.Ordinal))
            return AndroidHostMode.Attached;
        if (string.Equals(selected, IndependentValue, StringComparison.Ordinal) || File.Exists(_databasePath))
            return AndroidHostMode.Independent;
        return AndroidHostMode.Unconfigured;
    }

    internal static void SaveIndependent()
        => Save(IndependentValue);

    internal static void SaveAttached()
        => Save(AttachedValue);

    internal static void SaveUnconfigured()
        => Save(UnconfiguredValue);

    private static void Save(string value)
    {
        using var preferences = global::Android.App.Application.Context.GetSharedPreferences(
            PreferencesName, FileCreationMode.Private)
            ?? throw new IOException("Host preferences are unavailable.");
        using var editor = preferences.Edit()
            ?? throw new IOException("Host preferences could not be edited.");
        editor.PutString(ModeKey, value);
        if (!editor.Commit()) throw new IOException("Host selection could not be saved.");
    }
}
