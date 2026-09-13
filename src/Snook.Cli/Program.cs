using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Snook.Application;
using Snook.Contracts;
using Snook.Domain;
using Snook.Persistence.Sqlite;

namespace Snook.Cli;

/// <summary>
/// Structured JSON command-line access to the public Snook backend contract.
/// It calls <see cref="IBackendClient"/>, so the same commands work with the
/// embedded store and the authenticated daemon.
/// </summary>
public static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static async Task<int> Main(string[] args)
    {
        using var cancellationSource = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellationSource.Cancel();
        };
        Console.CancelKeyPress += handler;
        try
        {
            return await RunAsync(args, Console.Out, Console.Error, cancellationSource.Token);
        }
        finally
        {
            Console.CancelKeyPress -= handler;
        }
    }

    /// <summary>Runs the CLI with injectable writers, primarily for integration tests.</summary>
    public static async Task<int> RunAsync(string[] args, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken = default)
    {
        try
        {
            var invocation = ParseInvocation(args);
            if (invocation.Command is "help" or "--help" or "-h")
            {
                PrintUsage(stderr);
                return 0;
            }

            if (invocation.Command is "api" or "schema")
            {
                WriteJson(GetApiSchema(), stdout);
                return 0;
            }

            await using var backend = await CreateBackendAsync(invocation.Options, cancellationToken);
            if (invocation.Command == "watch")
            {
                await RunWatchAsync(backend, invocation.Arguments, stdout, cancellationToken);
                return 0;
            }

            var result = invocation.Command switch
            {
                "bootstrap" => await backend.GetBootstrapAsync(cancellationToken),
                "tasks" => await RunTasksAsync(backend, invocation.Arguments, cancellationToken),
                "summary" => await RunSummaryAsync(backend, invocation.Arguments, cancellationToken),
                "history" => await RunHistoryAsync(backend, invocation.Arguments, cancellationToken),
                "calendar" => await RunCalendarAsync(backend, invocation.Arguments, cancellationToken),
                "doctor" => await RunDoctorAsync(backend, invocation.Options, cancellationToken),
                "call" => await RunCallAsync(backend, invocation.Arguments, cancellationToken),
                _ => throw new CliUsageException($"Unknown command '{invocation.Command}'.")
            };
            WriteJson(result, stdout);
            return 0;
        }
        catch (CliUsageException exception)
        {
            WriteJson(new { error = new { code = "Usage", message = exception.Message }, hint = "Run 'snook help' for command syntax or 'snook api' for backend methods." }, stderr);
            return 64;
        }
        catch (JsonException exception)
        {
            WriteJson(new { error = new { code = "InvalidJson", message = exception.Message }, hint = "Pass a JSON object with the named arguments shown by 'snook api'." }, stderr);
            return 64;
        }
        catch (SnookException exception)
        {
            WriteJson(new { error = new { code = exception.Code.ToString(), message = exception.Message } }, stderr);
            return 2;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            WriteJson(new { error = new { code = "Cancelled", message = "The command was cancelled." } }, stderr);
            return 130;
        }
        catch (Exception exception)
        {
            WriteJson(new { error = new { code = SnookErrorCode.InternalError.ToString(), message = exception.Message } }, stderr);
            return 1;
        }
    }

    private static async Task<IBackendClient> CreateBackendAsync(CliOptions options, CancellationToken cancellationToken)
    {
        var dataRoot = options.DataDirectory
            ?? Environment.GetEnvironmentVariable("SNOOK_DATA_DIR")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var host = options.Host ?? Environment.GetEnvironmentVariable("SNOOK_HOST_MODE") ?? "embedded";
        if (host.Equals("daemon", StringComparison.OrdinalIgnoreCase))
        {
            var endpoint = options.Endpoint
                ?? Environment.GetEnvironmentVariable("SNOOK_DAEMON_ENDPOINT")
                ?? $"http://127.0.0.1:{ReadDaemonPort()}/";
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri)
                || endpointUri.Scheme is not ("http" or "https"))
            {
                throw new CliUsageException("--endpoint must be an absolute HTTP(S) URL.");
            }

            var token = options.Token ?? Environment.GetEnvironmentVariable("SNOOK_DAEMON_TOKEN");
            if (string.IsNullOrWhiteSpace(token))
            {
                var tokenPath = options.TokenFile ?? Path.Combine(dataRoot, "Snook", "daemon.token");
                if (!File.Exists(tokenPath))
                {
                    throw new InvalidOperationException("Daemon mode is enabled, but no daemon token was found. Start snookd, use --token/--token-file, or set SNOOK_DAEMON_TOKEN. The CLI will not open SQLite in daemon mode.");
                }

                token = (await File.ReadAllTextAsync(tokenPath, cancellationToken)).Trim('\uFEFF', '\r', '\n', ' ');
            }

            return new DaemonBackendClient(endpointUri, Guard.Required(token, "daemon token", 512));
        }

        if (!host.Equals("embedded", StringComparison.OrdinalIgnoreCase))
        {
            throw new CliUsageException("--host must be 'embedded' or 'daemon'.");
        }

        var store = new SqliteStore(Path.Combine(dataRoot, "Snook", "workspace.db"));
        var backend = new SnookBackend(store);
        await backend.InitializeAsync(cancellationToken);
        return backend;
    }

    private static async Task<object> RunTasksAsync(IBackendClient backend, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        if (args.Count > 1)
        {
            throw new CliUsageException("tasks accepts at most one search term.");
        }

        return await backend.SearchTasksAsync(args.Count == 0 ? null : args[0], includeCompleted: true, cancellationToken: cancellationToken);
    }

    private static async Task<object> RunSummaryAsync(IBackendClient backend, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        if (args.Count is < 1 or > 2 || !Enum.TryParse<SummaryGrouping>(args[0], true, out var grouping))
        {
            throw new CliUsageException("summary requires a grouping (day, task, project, activity, activitygroup, tag, or lane) and optionally a number of days.");
        }

        var days = args.Count == 2 && int.TryParse(args[1], out var parsedDays) ? parsedDays : 30;
        if (days is < 1 or > 366)
        {
            throw new CliUsageException("summary days must be between 1 and 366.");
        }

        var now = DateTimeOffset.UtcNow;
        return await backend.GetSummaryAsync(now.AddDays(-days), now, grouping, TimeZoneInfo.Local.Id, cancellationToken);
    }

    private static async Task<object> RunHistoryAsync(IBackendClient backend, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        if (args.Count > 1)
        {
            throw new CliUsageException("history accepts an optional JSON HistoryQuery object. Use call get-history for named arguments.");
        }

        var query = args.Count == 0
            ? new HistoryQuery(DateTimeOffset.UtcNow.AddDays(-30), DateTimeOffset.UtcNow)
            : DeserializeArgument<HistoryQuery>(args[0], "history query");
        return await backend.GetHistoryAsync(query, cancellationToken);
    }

    private static async Task<object> RunCalendarAsync(IBackendClient backend, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        if (args.Count is < 1 or > 2 || args[0] is not ("blocks" or "events"))
        {
            throw new CliUsageException("calendar requires 'blocks' or 'events' and an optional JSON CalendarRangeQuery object.");
        }

        var query = args.Count == 2
            ? DeserializeArgument<CalendarRangeQuery>(args[1], "calendar range query")
            : new CalendarRangeQuery(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(7));
        return args[0] == "blocks"
            ? await backend.GetCalendarRangeAsync(query, cancellationToken)
            : await backend.GetCalendarEventsRangeAsync(query, cancellationToken);
    }

    private static async Task<object> RunDoctorAsync(IBackendClient backend, CliOptions options, CancellationToken cancellationToken)
    {
        var bootstrap = await backend.GetBootstrapAsync(cancellationToken);
        return new
        {
            ready = true,
            workspaceId = bootstrap.Workspace.Id,
            workspaceName = bootstrap.Workspace.Name,
            host = bootstrap.Capabilities.HostMode,
            endpoint = options.Endpoint ?? Environment.GetEnvironmentVariable("SNOOK_DAEMON_ENDPOINT"),
            contract = $"{ContractInfo.Major}.{ContractInfo.Minor}",
            capabilities = bootstrap.Capabilities
        };
    }

    private static async Task RunWatchAsync(IBackendClient backend, IReadOnlyList<string> args, TextWriter stdout, CancellationToken cancellationToken)
    {
        if (args.Count != 0)
        {
            throw new CliUsageException("watch does not accept arguments.");
        }

        var outputGate = new object();
        EventHandler<ChangeNotification> handler = (_, notification) =>
        {
            lock (outputGate)
            {
                WriteJson(new { type = "change", change = notification }, stdout);
                stdout.Flush();
            }
        };
        backend.Changed += handler;
        try
        {
            var bootstrap = await backend.GetBootstrapAsync(cancellationToken);
            lock (outputGate)
            {
                WriteJson(new { type = "ready", cursor = bootstrap.CommittedCursor, capabilities = bootstrap.Capabilities }, stdout);
                stdout.Flush();
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        finally
        {
            backend.Changed -= handler;
        }
    }

    private static async Task<object> RunCallAsync(IBackendClient backend, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        if (args.Count is < 1 or > 2)
        {
            throw new CliUsageException("call requires a method name and optionally a JSON object of named arguments. Run 'snook api' for methods and shapes.");
        }

        var method = ResolveContractMethod(args[0]);
        using var argumentDocument = args.Count == 2 ? JsonDocument.Parse(args[1]) : JsonDocument.Parse("{}");
        if (argumentDocument.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new CliUsageException("call arguments must be a JSON object keyed by the documented parameter names.");
        }

        var supplied = argumentDocument.RootElement.EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value, StringComparer.OrdinalIgnoreCase);
        var parameters = method.GetParameters();
        var parameterNames = parameters.Where(parameter => parameter.ParameterType != typeof(CancellationToken))
            .Select(parameter => parameter.Name!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unknown = supplied.Keys.Where(key => !parameterNames.Contains(key)).ToArray();
        if (unknown.Length > 0)
        {
            throw new CliUsageException($"Unknown argument(s) for {method.Name}: {string.Join(", ", unknown)}.");
        }

        var values = new object?[parameters.Length];
        for (var index = 0; index < parameters.Length; index++)
        {
            var parameter = parameters[index];
            if (parameter.ParameterType == typeof(CancellationToken))
            {
                values[index] = cancellationToken;
            }
            else if (supplied.TryGetValue(parameter.Name!, out var value))
            {
                values[index] = value.ValueKind == JsonValueKind.Null ? null : value.Deserialize(parameter.ParameterType, JsonOptions);
            }
            else if (parameter.HasDefaultValue)
            {
                values[index] = parameter.DefaultValue;
            }
            else
            {
                throw new CliUsageException($"Missing required argument '{parameter.Name}' for {method.Name}.");
            }
        }

        try
        {
            var task = (Task)(method.Invoke(backend, values) ?? throw new InvalidOperationException($"{method.Name} did not return a task."));
            await task;
            return task.GetType().GetProperty("Result")?.GetValue(task) ?? new { ok = true };
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }

    private static MethodInfo ResolveContractMethod(string name)
    {
        var normalized = NormalizeMethodName(name);
        var methods = typeof(IBackendClient).GetMethods()
            .Where(method => method.ReturnType != typeof(ValueTask) && NormalizeMethodName(method.Name) == normalized).ToArray();
        return methods.Length == 1
            ? methods[0]
            : throw new CliUsageException($"Unknown backend method '{name}'. Run 'snook api' to list the public contract.");
    }

    private static object GetApiSchema() => new
    {
        contract = new { major = ContractInfo.Major, minor = ContractInfo.Minor },
        invocation = "snook call <method-name> '<json object with named arguments>'",
        conventions = new
        {
            methodNames = "case-insensitive; hyphens and the Async suffix are optional",
            enums = "use names such as High, Foreground, Earlier, or StopNow",
            timestamps = "ISO 8601 UTC instants, for example 2026-09-13T15:30:00Z",
            dates = "ISO dates, for example 2026-09-13",
            mutationRequests = "Use { operationId, clientDeviceId, expectedRevision }. Reuse operationId when retrying the same mutation; expectedRevision is required by revision-checked mutations.",
            nullValues = "Use JSON null for optional IDs, text, dates, or timestamps."
        },
        methods = typeof(IBackendClient).GetMethods().Where(method => method.ReturnType != typeof(ValueTask))
            .OrderBy(method => method.Name, StringComparer.Ordinal).Select(method => new
            {
                name = method.Name,
                command = ToKebab(method.Name.EndsWith("Async", StringComparison.Ordinal) ? method.Name[..^5] : method.Name),
                returns = TypeName(method.ReturnType.IsGenericType ? method.ReturnType.GetGenericArguments()[0] : typeof(void)),
                arguments = method.GetParameters().Where(parameter => parameter.ParameterType != typeof(CancellationToken)).Select(parameter => new
                {
                    name = parameter.Name,
                    type = TypeName(parameter.ParameterType),
                    required = !parameter.HasDefaultValue,
                    defaultValue = parameter.HasDefaultValue ? parameter.DefaultValue : null
                })
            })
    };

    private static Invocation ParseInvocation(string[] args)
    {
        var options = new CliOptions();
        var position = 0;
        while (position < args.Length && args[position].StartsWith("--", StringComparison.Ordinal))
        {
            var option = args[position++];
            if (option == "--help")
            {
                return new Invocation("help", [], options);
            }

            if (position == args.Length)
            {
                throw new CliUsageException($"{option} requires a value.");
            }

            var value = args[position++];
            options = option switch
            {
                "--data-dir" => options with { DataDirectory = value },
                "--host" => options with { Host = value },
                "--endpoint" => options with { Endpoint = value },
                "--token" => options with { Token = value },
                "--token-file" => options with { TokenFile = value },
                _ => throw new CliUsageException($"Unknown global option '{option}'.")
            };
        }

        return position == args.Length
            ? new Invocation("help", [], options)
            : new Invocation(args[position++].ToLowerInvariant(), args[position..], options);
    }

    private static T DeserializeArgument<T>(string json, string description) => JsonSerializer.Deserialize<T>(json, JsonOptions)
        ?? throw new CliUsageException($"{description} must be valid JSON.");

    private static string NormalizeMethodName(string value)
    {
        var compact = string.Concat(value.Where(char.IsLetterOrDigit));
        return compact.EndsWith("async", StringComparison.OrdinalIgnoreCase) ? compact[..^5].ToLowerInvariant() : compact.ToLowerInvariant();
    }

    private static string ToKebab(string value) => string.Concat(value.Select((character, index) => index > 0 && char.IsUpper(character)
        ? $"-{char.ToLowerInvariant(character)}" : char.ToLowerInvariant(character).ToString()));

    private static string TypeName(Type type)
    {
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Nullable<>))
        {
            return $"{TypeName(type.GetGenericArguments()[0])}?";
        }

        return type.IsGenericType
            ? $"{type.Name[..type.Name.IndexOf('`')]}<{string.Join(", ", type.GetGenericArguments().Select(TypeName))}>"
            : type.Name;
    }

    private static void WriteJson(object value, TextWriter writer) => writer.WriteLine(JsonSerializer.Serialize(value, JsonOptions));

    private static void PrintUsage(TextWriter writer)
    {
        writer.WriteLine("Snook — structured JSON workspace client");
        writer.WriteLine();
        writer.WriteLine("Usage: snook [global options] <command> [arguments]");
        writer.WriteLine("Global options: --data-dir PATH --host embedded|daemon --endpoint URL --token TOKEN --token-file PATH");
        writer.WriteLine("Commands: bootstrap | tasks [search] | summary GROUP [days] | history [HistoryQuery JSON]");
        writer.WriteLine("          calendar blocks|events [Range JSON] | doctor | watch | api | call METHOD [JSON OBJECT]");
        writer.WriteLine();
        writer.WriteLine("Example: snook call create-task '{\"projectId\":\"...\",\"title\":\"Write brief\",\"priority\":\"High\"}'");
        writer.WriteLine("Mutation: snook call complete-task '{\"taskId\":\"...\",\"request\":{\"operationId\":\"...\",\"clientDeviceId\":\"...\",\"expectedRevision\":4}}'");
        writer.WriteLine("Run 'snook api' for every method and its exact named arguments.");
    }

    private static int ReadDaemonPort()
    {
        var value = Environment.GetEnvironmentVariable("SNOOK_DAEMON_PORT");
        return int.TryParse(value, out var port) && port is >= 1024 and <= 65535 ? port : 43871;
    }

    private sealed record CliOptions(string? DataDirectory = null, string? Host = null, string? Endpoint = null, string? Token = null, string? TokenFile = null);
    private sealed record Invocation(string Command, IReadOnlyList<string> Arguments, CliOptions Options);
    private sealed class CliUsageException(string message) : Exception(message);
}
