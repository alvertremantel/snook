using System.Text.Json;
using Snook.Application;
using Snook.Contracts;
using Snook.Domain;
using Snook.Persistence.Sqlite;

namespace Snook.Cli;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
        {
            PrintUsage();
            return 0;
        }

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
                    throw new InvalidOperationException("Daemon mode is enabled, but no daemon token was found.");
                }

                token = File.ReadAllText(tokenPath).Trim('\uFEFF');
            }

            backend = new DaemonBackendClient(new Uri(endpoint), token);
        }
        else
        {
            var databasePath = Path.Combine(dataRoot, "Snook", "workspace.db");
            var store = new SqliteStore(databasePath);
            var embeddedBackend = new SnookBackend(store);
            await embeddedBackend.InitializeAsync();
            backend = embeddedBackend;
        }
        await using var backendLifetime = backend;

        try
        {
            var result = args[0].ToLowerInvariant() switch
            {
                "bootstrap" => await RunBootstrapAsync(backend),
                "tasks" => await RunTasksAsync(backend, args[1..]),
                "summary" => await RunSummaryAsync(backend, args[1..]),
                _ => throw new CliUsageException($"Unknown command '{args[0]}'.")
            };
            WriteJson(result);
            return 0;
        }
        catch (CliUsageException exception)
        {
            Console.Error.WriteLine(exception.Message);
            PrintUsage();
            return 64;
        }
        catch (SnookException exception)
        {
            WriteJson(new { error = new { code = exception.Code.ToString(), message = exception.Message } }, Console.Error);
            return 2;
        }
        catch (Exception exception)
        {
            WriteJson(new { error = new { code = SnookErrorCode.InternalError.ToString(), message = exception.Message } }, Console.Error);
            return 1;
        }
    }

    private static async Task<object> RunBootstrapAsync(IBackendClient backend) => await backend.GetBootstrapAsync();

    private static async Task<object> RunTasksAsync(IBackendClient backend, string[] args)
    {
        if (args.Length > 1)
        {
            throw new CliUsageException("tasks accepts at most one search term.");
        }

        return await backend.SearchTasksAsync(args.FirstOrDefault(), includeCompleted: true);
    }

    private static async Task<object> RunSummaryAsync(IBackendClient backend, string[] args)
    {
        if (args.Length is < 1 or > 2 || !Enum.TryParse<SummaryGrouping>(args[0], true, out var grouping))
        {
            throw new CliUsageException("summary requires a grouping (day, task, project, activity, activitygroup, tag, or lane) and optionally a number of days.");
        }

        var days = args.Length == 2 && int.TryParse(args[1], out var parsedDays) ? parsedDays : 30;
        if (days is < 1 or > 366)
        {
            throw new CliUsageException("summary days must be between 1 and 366.");
        }

        var now = DateTimeOffset.UtcNow;
        return await backend.GetSummaryAsync(now.AddDays(-days), now, grouping, TimeZoneInfo.Local.Id);
    }

    private static void WriteJson(object value, TextWriter? writer = null)
    {
        (writer ?? Console.Out).WriteLine(JsonSerializer.Serialize(value, JsonOptions));
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine("snook bootstrap");
        Console.Error.WriteLine("snook tasks [search]");
        Console.Error.WriteLine("snook summary <day|task|project|activity|activitygroup|tag|lane> [days]");
    }

    private static int ReadDaemonPort()
    {
        var value = Environment.GetEnvironmentVariable("SNOOK_DAEMON_PORT");
        return int.TryParse(value, out var port) && port is >= 1024 and <= 65535 ? port : 43871;
    }

    private sealed class CliUsageException(string message) : Exception(message);
}
