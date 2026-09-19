using Avalonia;
using Snook.Application;
using Snook.UI;

namespace Snook.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Any(argument => argument is "--help" or "-h"))
        {
            Console.WriteLine("Snook desktop: --host embedded|daemon --data-dir PATH --endpoint URL --token-file PATH --configure --no-profile");
            Console.WriteLine("--configure opens connection settings without opening a workspace. --no-profile explicitly ignores saved settings.");
            Console.WriteLine("Clients read <launch-data-dir>/Snook/client-profile.json. Flags override SNOOK_* environment settings, then saved settings.");
            Console.WriteLine("Daemon mode connects to snookd and never opens SQLite. Environment: SNOOK_HOST_MODE, SNOOK_DATA_DIR, SNOOK_DAEMON_ENDPOINT, SNOOK_DAEMON_TOKEN_FILE.");
            return;
        }
        string? dataRoot = null, host = null, endpoint = null, tokenFile = null, error = null;
        var configure = false;
        var ignoreProfile = false;
        var avaloniaArguments = new List<string>();
        try
        {
            for (var index = 0; index < args.Length; index++)
            {
                if (args[index] == "--configure") { configure = true; continue; }
                if (args[index] == "--no-profile") { ignoreProfile = true; continue; }
                if (args[index] is not ("--host" or "--data-dir" or "--endpoint" or "--token-file"))
                {
                    avaloniaArguments.Add(args[index]);
                    continue;
                }
                var option = args[index];
                if (++index >= args.Length) throw new ArgumentException($"{option} requires a value.");
                switch (option)
                {
                    case "--host": host = args[index]; break;
                    case "--data-dir": dataRoot = args[index]; break;
                    case "--endpoint": endpoint = args[index]; break;
                    case "--token-file": tokenFile = args[index]; break;
                }
            }
        }
        catch (ArgumentException exception) { error = exception.Message; }

        // Invalid configuration gets a visible daemon-only draft, never an
        // implicit embedded fallback or an exception before Avalonia can start.
        var launchRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        ClientProfileSnapshot saved = new(null, null);
        ClientConnectionProfile initial = new(1, "daemon", launchRoot);
        try
        {
            launchRoot = ClientProfileStore.LaunchDataDirectory(dataRoot);
            App.ClientProfiles = new ClientProfileStore(launchRoot);
            if (!ignoreProfile) saved = App.ClientProfiles.ReadAsync().GetAwaiter().GetResult();
            initial = ClientProfileStore.Resolve(saved.Profile, launchRoot, host, dataRoot, endpoint, tokenFile);
            if (ignoreProfile && host is null && Environment.GetEnvironmentVariable("SNOOK_HOST_MODE") is null)
            {
                initial = initial with { Host = "daemon" };
                configure = true;
            }
        }
        catch (Exception exception)
        {
            error = exception is Snook.Domain.SnookException snook ? snook.Message : "Connection settings could not be loaded. Check the selected data directory and saved profile.";
            initial = new(1, "daemon", launchRoot);
        }
        App.ClientProfiles ??= new ClientProfileStore(launchRoot);
        var profiles = App.ClientProfiles;
        var legacyToken = initial.TokenFile is null ? Environment.GetEnvironmentVariable("SNOOK_DAEMON_TOKEN") : null;
        App.ConnectionStartup = () => new ConnectionWindow(new ConnectionViewModel(profiles, saved, initial, error: error,
            open: (profile, cancellationToken) => ClientProfileStore.OpenAsync(profile,
                profile.TokenFile is null ? legacyToken : null, ReadAllowConcurrentForeground(), cancellationToken)),
            autoConnect: !configure && error is null);
        try { BuildAvaloniaApp().StartWithClassicDesktopLifetime(avaloniaArguments.ToArray()); }
        finally { App.ConfiguredBackend?.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }

    private static bool? ReadAllowConcurrentForeground()
    {
        var value = Environment.GetEnvironmentVariable("SNOOK_ALLOW_CONCURRENT_FOREGROUND");
        return bool.TryParse(value, out var parsed) ? parsed : null;
    }

    private static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
