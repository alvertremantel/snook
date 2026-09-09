using Avalonia;
using Snook.Application;
using Snook.Contracts;
using Snook.Persistence.Sqlite;
using Snook.UI;

namespace Snook.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var dataRoot = Environment.GetEnvironmentVariable("SNOOK_DATA_DIR")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var daemonMode = string.Equals(Environment.GetEnvironmentVariable("SNOOK_HOST_MODE"), "daemon", StringComparison.OrdinalIgnoreCase);
        IBackendClient backend;
        if (daemonMode)
        {
            var endpoint = Environment.GetEnvironmentVariable("SNOOK_DAEMON_ENDPOINT")
                ?? $"http://127.0.0.1:{ReadDaemonPort()}/";
            var token = Environment.GetEnvironmentVariable("SNOOK_DAEMON_TOKEN");
            if (string.IsNullOrWhiteSpace(token))
            {
                var tokenPath = Path.Combine(dataRoot, "Snook", "daemon.token");
                if (!File.Exists(tokenPath))
                {
                    throw new InvalidOperationException("Daemon mode is enabled, but no daemon token was found. Start snookd or set SNOOK_DAEMON_TOKEN; the desktop client will not open SQLite directly in daemon mode.");
                }

                token = File.ReadAllText(tokenPath).Trim('\uFEFF');
            }

            backend = new DaemonBackendClient(new Uri(endpoint), token);
        }
        else
        {
            var databasePath = Path.Combine(dataRoot, "Snook", "workspace.db");
            var store = new SqliteStore(databasePath);
            var embeddedBackend = new SnookBackend(store, allowConcurrentForeground: ReadAllowConcurrentForeground());
            embeddedBackend.InitializeAsync().GetAwaiter().GetResult();
            backend = embeddedBackend;
        }

        App.ConfiguredBackend = backend;

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            backend.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static int ReadDaemonPort()
    {
        var value = Environment.GetEnvironmentVariable("SNOOK_DAEMON_PORT");
        return int.TryParse(value, out var port) && port is >= 1024 and <= 65535 ? port : 43871;
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
