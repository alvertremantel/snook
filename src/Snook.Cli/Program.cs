using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Snook.Application;
using Snook.Contracts;
using Snook.Domain;

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
    private static readonly JsonSerializerOptions StreamJsonOptions = new(JsonOptions) { WriteIndented = false };

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
                "habits" => await RunHabitsAsync(backend, invocation.Arguments, cancellationToken),
                "journals" => await RunJournalsAsync(backend, invocation.Arguments, cancellationToken),
                "journal-entries" => await RunJournalEntriesAsync(backend, invocation.Arguments, cancellationToken),
                "summary" => await RunSummaryAsync(backend, invocation.Arguments, cancellationToken),
                "history" => await RunHistoryAsync(backend, invocation.Arguments, cancellationToken),
                "calendar" => await RunCalendarAsync(backend, invocation.Arguments, cancellationToken),
                "task-batch" => await RunTaskBatchAsync(backend, invocation.Arguments, cancellationToken),
                "doctor" => await RunDoctorAsync(backend, invocation.Options, cancellationToken),
                "call" => await RunCallAsync(backend, invocation.Arguments, cancellationToken),
                _ => throw new CliUsageException($"Unknown command '{invocation.Command}'.")
            };
            WriteJson(result, stdout);
            return 0;
        }
        catch (CliUsageException exception)
        {
            WriteJson(new { error = new { code = "Usage", message = exception.Message }, hint = "Run 'snook-cli help' for command syntax or 'snook-cli api' for backend methods." }, stderr);
            return 64;
        }
        catch (JsonException exception)
        {
            WriteJson(new { error = new { code = "InvalidJson", message = exception.Message }, hint = "Pass a JSON object with the named arguments shown by 'snook-cli api'." }, stderr);
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
        if (options.IgnoreProfile && options.Host is null && Environment.GetEnvironmentVariable("SNOOK_HOST_MODE") is null)
            throw new CliUsageException("--no-profile requires an explicit --host embedded|daemon (or SNOOK_HOST_MODE).");
        var launchRoot = ClientProfileStore.LaunchDataDirectory(options.DataDirectory);
        var saved = options.IgnoreProfile ? null : (await new ClientProfileStore(launchRoot).ReadAsync(cancellationToken)).Profile;
        var profile = ClientProfileStore.Resolve(saved, launchRoot, options.Host, options.DataDirectory, options.Endpoint, options.TokenFile);
        var token = options.Token ?? (profile.TokenFile is null ? Environment.GetEnvironmentVariable("SNOOK_DAEMON_TOKEN") : null);
        return await ClientProfileStore.OpenAsync(profile, token, cancellationToken: cancellationToken);
    }

    private static async Task<object> RunTasksAsync(IBackendClient backend, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        if (args.Count > 1)
        {
            throw new CliUsageException("tasks accepts at most one search term.");
        }

        return await backend.SearchTasksAsync(args.Count == 0 ? null : args[0], includeCompleted: true, cancellationToken: cancellationToken);
    }

    private static async Task<object> RunHabitsAsync(IBackendClient backend, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        if (args.Count > 1) throw new CliUsageException("habits accepts an optional HabitQuery JSON object.");
        var query = args.Count == 0 ? new HabitQuery() : JsonSerializer.Deserialize<HabitQuery>(args[0], JsonOptions)
            ?? throw new CliUsageException("Provide a HabitQuery JSON object.");
        return await backend.GetHabitsAsync(query, cancellationToken);
    }

    private static async Task<object> RunJournalsAsync(IBackendClient backend, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        if (args.Count > 1 || args.Count == 1 && args[0] != "--include-deleted")
            throw new CliUsageException("journals accepts only --include-deleted. Use call create-journal, update-journal, or set-journal-deleted to write.");
        return await backend.GetJournalsAsync(args.Count == 1, cancellationToken);
    }

    private static async Task<object> RunJournalEntriesAsync(IBackendClient backend, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        if (args.Count > 1) throw new CliUsageException("journal-entries accepts an optional JournalEntryQuery JSON object.");
        var query = args.Count == 0 ? new JournalEntryQuery() : DeserializeArgument<JournalEntryQuery>(args[0], "journal entry query");
        return await backend.GetJournalEntriesAsync(query, cancellationToken);
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

    private static async Task<object> RunTaskBatchAsync(IBackendClient backend, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        if (args.Count != 2 || args[0] is not ("plan" or "apply"))
        {
            throw new CliUsageException("task-batch requires 'plan' with a selection/update JSON object or 'apply' with the resulting plan JSON.");
        }

        if (args[0] == "apply")
        {
            var plan = DeserializeArgument<TaskBatchApply>(args[1], "task batch plan");
            if (plan.Tasks is null || plan.Update is null || plan.Request is null)
            {
                throw new CliUsageException("A task batch plan must contain tasks, update, and request properties.");
            }

            if (plan.Request.OperationId == Guid.Empty || plan.Request.ClientDeviceId == Guid.Empty)
            {
                throw new CliUsageException("A task batch plan requires non-empty operationId and clientDeviceId values.");
            }

            if (plan.Request.ExpectedRevision is not null)
            {
                throw new CliUsageException("Use each task's expectedRevision; request.expectedRevision must be null for a task batch.");
            }

            ValidateTaskBatchUpdate(plan.Update);
            var result = await backend.BulkUpdateTasksAsync(plan.Tasks, plan.Update, plan.Request, cancellationToken);
            return new
            {
                kind = "task-batch-result",
                count = result.Count,
                operationId = plan.Request.OperationId,
                clientDeviceId = plan.Request.ClientDeviceId,
                tasks = result
            };
        }

        var request = DeserializeArgument<TaskBatchPlanRequest>(args[1], "task batch request");
        if (request.Selection is null || request.Update is null)
        {
            throw new CliUsageException("A task batch request must contain selection and update properties.");
        }

        ValidateTaskBatchUpdate(request.Update);
        var selected = await SelectTaskBatchAsync(backend, request.Selection, cancellationToken);
        var operationId = request.OperationId ?? Guid.NewGuid();
        var clientDeviceId = request.ClientDeviceId ?? Guid.NewGuid();
        if (operationId == Guid.Empty || clientDeviceId == Guid.Empty)
        {
            throw new CliUsageException("operationId and clientDeviceId must be non-empty when supplied.");
        }

        var targets = selected.Select(item => new TaskRevision(item.Task.Id, item.Task.Revision)).ToArray();
        return new
        {
            kind = "task-batch-plan",
            count = targets.Length,
            selection = request.Selection,
            tasks = targets,
            update = request.Update,
            request = new OperationRequest(operationId, clientDeviceId),
            preview = selected.Select(item => new
            {
                taskId = item.Task.Id,
                expectedRevision = item.Task.Revision,
                item.Task.Title,
                item.Task.ProjectId,
                item.ProjectName,
                item.BoardName,
                item.Task.Priority,
                item.Task.Status,
                item.Task.DueDate,
                item.Task.Starred,
                archived = item.Task.ArchivedAtUtc is not null
            })
        };
    }

    private static async Task<IReadOnlyList<TaskListItem>> SelectTaskBatchAsync(
        IBackendClient backend,
        TaskBatchSelection selection,
        CancellationToken cancellationToken)
    {
        var ids = selection.TaskIds?.ToArray() ?? [];
        if (ids.Length > 500 || ids.Any(id => id == Guid.Empty) || ids.Distinct().Count() != ids.Length)
        {
            throw new CliUsageException("selection.taskIds must contain at most 500 distinct, non-empty task IDs.");
        }

        if (selection.ProjectId == Guid.Empty || selection.BoardId == Guid.Empty)
        {
            throw new CliUsageException("selection.projectId and selection.boardId must be non-empty when supplied.");
        }

        if (selection.DueOnOrAfter is { Year: < 1900 } || selection.DueOnOrBefore is { Year: < 1900 }
            || selection.DueOnOrAfter > selection.DueOnOrBefore)
        {
            throw new CliUsageException("Selection due dates must be on or after 1900-01-01, and dueOnOrAfter cannot follow dueOnOrBefore.");
        }

        var hasCriterion = ids.Length > 0 || !string.IsNullOrWhiteSpace(selection.Search)
            || selection.ProjectId is not null || selection.BoardId is not null || selection.Status is not null
            || selection.Priority is not null || selection.Starred is not null || selection.HasDueDate is not null
            || selection.DueOnOrAfter is not null || selection.DueOnOrBefore is not null;
        if (!selection.All && !hasCriterion)
        {
            throw new CliUsageException("Choose at least one selection filter, taskIds, or set selection.all to true.");
        }

        var candidates = await backend.SearchTasksAsync(
            selection.Search,
            selection.IncludeCompleted || selection.Status == TaskState.Completed,
            selection.IncludeArchived,
            includeDeleted: false,
            cancellationToken);
        IReadOnlyDictionary<Guid, Guid>? boardsByProject = null;
        if (selection.BoardId is not null)
        {
            var bootstrap = await backend.GetBootstrapAsync(cancellationToken);
            boardsByProject = bootstrap.Projects.ToDictionary(project => project.Id, project => project.BoardId);
        }

        var idSet = ids.ToHashSet();
        var filtered = candidates.Where(item =>
                (idSet.Count == 0 || idSet.Contains(item.Task.Id))
                && (selection.ProjectId is null || item.Task.ProjectId == selection.ProjectId)
                && (selection.BoardId is null || boardsByProject!.GetValueOrDefault(item.Task.ProjectId) == selection.BoardId)
                && (selection.Status is null || item.Task.Status == selection.Status)
                && (selection.Priority is null || item.Task.Priority == selection.Priority)
                && (selection.Starred is null || item.Task.Starred == selection.Starred)
                && (selection.HasDueDate is null || (item.Task.DueDate is not null) == selection.HasDueDate)
                && (selection.DueOnOrAfter is null || item.Task.DueDate >= selection.DueOnOrAfter)
                && (selection.DueOnOrBefore is null || item.Task.DueDate <= selection.DueOnOrBefore))
            .ToArray();
        if (ids.Length > 0 && filtered.Length != ids.Length)
        {
            var matched = filtered.Select(item => item.Task.Id).ToHashSet();
            var missing = ids.Where(id => !matched.Contains(id));
            throw new CliUsageException($"Explicit task IDs were not found or did not match every filter: {string.Join(", ", missing)}.");
        }

        if (filtered.Length == 0)
        {
            throw new CliUsageException("The task batch selection matched no editable tasks.");
        }

        if (filtered.Length > 500)
        {
            throw new CliUsageException($"The task batch selection matched {filtered.Length} tasks; narrow it to at most 500.");
        }

        if (ids.Length > 0)
        {
            var byId = filtered.ToDictionary(item => item.Task.Id);
            return ids.Select(id => byId[id]).ToArray();
        }

        return filtered.OrderBy(item => item.BoardName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.ProjectName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Task.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Task.Id)
            .ToArray();
    }

    private static void ValidateTaskBatchUpdate(BulkTaskUpdate update)
    {
        if (update.Title is not null && (string.IsNullOrWhiteSpace(update.Title) || update.Title.Trim().Length > 300))
        {
            throw new CliUsageException("update.title must contain text and be at most 300 characters.");
        }

        if (update.Description?.Length > 20_000)
        {
            throw new CliUsageException("update.description must be at most 20000 characters.");
        }

        if (update.DueDate is not null && !update.ChangeDueDate)
        {
            throw new CliUsageException("Set update.changeDueDate to true when supplying dueDate.");
        }

        if (update.ChangeDueDate && update.DueDate is { Year: < 1900 })
        {
            throw new CliUsageException("update.dueDate must be on or after 1900-01-01.");
        }

        if (update.DefaultActivityId is not null && !update.ChangeActivity)
        {
            throw new CliUsageException("Set update.changeActivity to true when supplying defaultActivityId.");
        }

        if (update.ProjectId == Guid.Empty || update.DefaultActivityId == Guid.Empty)
        {
            throw new CliUsageException("update.projectId and update.defaultActivityId must be non-empty when supplied.");
        }

        var addTags = ValidateTaskBatchTags(update.TagsToAdd, "tagsToAdd");
        var removeTags = ValidateTaskBatchTags(update.TagsToRemove, "tagsToRemove");
        if (addTags.Intersect(removeTags, StringComparer.OrdinalIgnoreCase).Any())
        {
            throw new CliUsageException("A tag cannot appear in both update.tagsToAdd and update.tagsToRemove.");
        }

        if (update.Title is null && update.Description is null && update.Priority is null && update.Status is null
            && !update.ChangeDueDate && !update.ChangeActivity && update.Starred is null && update.ProjectId is null
            && update.Archived is null && addTags.Length == 0 && removeTags.Length == 0)
        {
            throw new CliUsageException("Choose at least one task field to change.");
        }
    }

    private static string[] ValidateTaskBatchTags(IReadOnlyList<string>? tags, string propertyName)
    {
        if (tags is null) return [];
        if (tags.Count > 50 || tags.Any(tag => string.IsNullOrWhiteSpace(tag) || tag.Trim().Length > 100))
        {
            throw new CliUsageException($"update.{propertyName} must contain at most 50 non-empty names of at most 100 characters.");
        }

        return tags.Select(tag => tag.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
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
            endpoint = (backend as DaemonBackendClient)?.Endpoint.AbsoluteUri,
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
                stdout.WriteLine(JsonSerializer.Serialize(new { type = "change", change = notification }, StreamJsonOptions));
                stdout.Flush();
            }
        };
        backend.Changed += handler;
        try
        {
            var bootstrap = await backend.GetBootstrapAsync(cancellationToken);
            lock (outputGate)
            {
                stdout.WriteLine(JsonSerializer.Serialize(new { type = "ready", cursor = bootstrap.CommittedCursor, capabilities = bootstrap.Capabilities }, StreamJsonOptions));
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
            throw new CliUsageException("call requires a method name and optionally a JSON object of named arguments. Run 'snook-cli api' for methods and shapes.");
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
            : throw new CliUsageException($"Unknown backend method '{name}'. Run 'snook-cli api' to list the public contract.");
    }

    private static object GetApiSchema() => new
    {
        contract = new { major = ContractInfo.Major, minor = ContractInfo.Minor },
        invocation = "snook-cli call <method-name> '<json object with named arguments>'",
        conventions = new
        {
            methodNames = "case-insensitive; hyphens and the Async suffix are optional",
            enums = "use names such as High, Foreground, Earlier, or StopNow",
            timestamps = "ISO 8601 UTC instants, for example 2026-09-13T15:30:00Z",
            dates = "ISO dates, for example 2026-09-13",
            mutationRequests = "Use { operationId, clientDeviceId, expectedRevision }. Reuse operationId when retrying the same mutation; expectedRevision is required by revision-checked mutations.",
            nullValues = "Use JSON null for optional IDs, text, dates, or timestamps."
        },
        cliCommands = new
        {
            journals = new
            {
                read = "snook-cli journals [--include-deleted] | snook-cli journal-entries [JournalEntryQuery JSON]",
                write = "snook-cli call create-journal|update-journal|set-journal-deleted|create-journal-entry|update-journal-entry|set-journal-entry-deleted JSON",
                queryFields = new[] { "journalId", "search", "tag", "includeDeleted", "pageSize", "continuationToken" },
                journalDefinition = new[] { "name", "description" },
                entryDefinition = new[] { "journalId", "title", "content", "occurredAtUtc", "mood", "tags" },
                mood = "Optional integer 1–7; null clears it.",
                tags = "Journal-only string array; at most 20 tags, each up to 50 characters. Updates replace the full entry, including tags.",
                pageSize = "1–100; default 30. Pass the returned continuationToken with the same filters."
            },
            habits = new
            {
                read = "snook-cli habits [HabitQuery JSON]",
                query = TypeName(typeof(HabitQuery)),
                maximumDays = HabitRules.MaximumDays,
                defaultDays = 30,
                timeZone = "Each habit keeps its creation time zone; throughDate is an inclusive civil date."
            },
            taskBatch = new
            {
                plan = "snook-cli task-batch plan '<selection/update JSON object>'",
                apply = "snook-cli task-batch apply '<task-batch-plan JSON>'",
                selection = TypeName(typeof(TaskBatchSelection)),
                selectionFields = new[]
                {
                    "all", "taskIds", "search", "boardId", "projectId", "status", "priority", "starred",
                    "hasDueDate", "dueOnOrAfter", "dueOnOrBefore", "includeCompleted", "includeArchived"
                },
                update = TypeName(typeof(BulkTaskUpdate)),
                limit = 500,
                retry = "Keep and reapply the exact plan JSON. It contains task revisions and a stable operationId."
            }
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
            if (option == "--no-profile")
            {
                options = options with { IgnoreProfile = true };
                continue;
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
        writer.WriteLine("Usage: snook-cli [global options] <command> [arguments]");
        writer.WriteLine("Global options: --data-dir PATH --host embedded|daemon --endpoint URL --token TOKEN --token-file PATH --no-profile");
        writer.WriteLine("Clients read <launch-data-dir>/Snook/client-profile.json. Flags override environment, then saved settings. --no-profile ignores that file explicitly.");
        writer.WriteLine("Commands: bootstrap | tasks [search] | summary GROUP [days] | history [HistoryQuery JSON]");
        writer.WriteLine("          habits [HabitQuery JSON] (read-only daily progress and check-ins)");
        writer.WriteLine("          journals [--include-deleted] | journal-entries [JournalEntryQuery JSON]");
        writer.WriteLine("          call create-journal|update-journal|set-journal-deleted JSON");
        writer.WriteLine("          call create-journal-entry|update-journal-entry|set-journal-entry-deleted JSON");
        writer.WriteLine("          call get-journal-entry|get-journal-tags JSON");
        writer.WriteLine("          calendar blocks|events [Range JSON] | task-batch plan|apply JSON | doctor | watch | api");
        writer.WriteLine("          call METHOD [JSON OBJECT]");
        writer.WriteLine();
        writer.WriteLine("Example: snook-cli call create-task '{\"projectId\":\"...\",\"title\":\"Write brief\",\"priority\":\"High\"}'");
        writer.WriteLine("Mutation: snook-cli call complete-task '{\"taskId\":\"...\",\"request\":{\"operationId\":\"...\",\"clientDeviceId\":\"...\",\"expectedRevision\":4}}'");
        writer.WriteLine("Run 'snook-cli api' for every method and its exact named arguments.");
    }

    private sealed record CliOptions(string? DataDirectory = null, string? Host = null, string? Endpoint = null, string? Token = null, string? TokenFile = null, bool IgnoreProfile = false);
    private sealed record Invocation(string Command, IReadOnlyList<string> Arguments, CliOptions Options);
    private sealed record TaskBatchSelection(
        bool All = false,
        IReadOnlyList<Guid>? TaskIds = null,
        string? Search = null,
        Guid? BoardId = null,
        Guid? ProjectId = null,
        TaskState? Status = null,
        Priority? Priority = null,
        bool? Starred = null,
        bool? HasDueDate = null,
        DateOnly? DueOnOrAfter = null,
        DateOnly? DueOnOrBefore = null,
        bool IncludeCompleted = false,
        bool IncludeArchived = false);
    private sealed record TaskBatchPlanRequest(
        TaskBatchSelection Selection,
        BulkTaskUpdate Update,
        Guid? OperationId = null,
        Guid? ClientDeviceId = null);
    private sealed record TaskBatchApply(
        IReadOnlyList<TaskRevision> Tasks,
        BulkTaskUpdate Update,
        OperationRequest Request);
    private sealed class CliUsageException(string message) : Exception(message);
}
