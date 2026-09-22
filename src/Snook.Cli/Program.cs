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
    private static readonly string[] GrantSources = ["group:snook-users"];
    private static readonly string[] GrantDestinations = ["tag:snook-host"];
    private static readonly string[] GrantPorts = ["tcp:443"];
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

            if (invocation.Command == "tailnet")
                return await RunTailnetAsync(invocation.Options, invocation.Arguments, stdout, cancellationToken);
            if (invocation.Command == "pair")
                return await RunPairAsync(invocation.Options, invocation.Arguments, stdout, stderr, cancellationToken);
            if (invocation.Command == "devices")
                return await RunDeviceAdministrationAsync(invocation.Options, invocation.Arguments, stdout, cancellationToken);
            if (invocation.Command == "doctor")
            {
                var diagnosis = await RunDoctorCommandAsync(invocation.Options, cancellationToken);
                WriteJson(diagnosis.Result, diagnosis.ExitCode == 0 ? stdout : stderr);
                return diagnosis.ExitCode;
            }

            await using var backend = await CreateBackendAsync(invocation.Options, cancellationToken);
            if (invocation.Command == "pairing")
            {
                var pairingResult = await RunPairingAsync(backend, invocation.Arguments, cancellationToken);
                WriteJson(pairingResult, stdout);
                return 0;
            }
            if (invocation.Command == "watch")
            {
                await RunWatchAsync(backend, invocation.Arguments, stdout, cancellationToken);
                return 0;
            }

            var result = invocation.Command switch
            {
                "bootstrap" => await backend.GetBootstrapAsync(cancellationToken),
                "tasks" => await RunTasksAsync(backend, invocation.Arguments, cancellationToken),
                "rss-feeds" => await RunRssFeedsAsync(backend, invocation.Arguments, cancellationToken),
                "rss-articles" => await RunRssArticlesAsync(backend, invocation.Arguments, cancellationToken),
                "habits" => await RunHabitsAsync(backend, invocation.Arguments, cancellationToken),
                "journals" => await RunJournalsAsync(backend, invocation.Arguments, cancellationToken),
                "journal-entries" => await RunJournalEntriesAsync(backend, invocation.Arguments, cancellationToken),
                "summary" => await RunSummaryAsync(backend, invocation.Arguments, cancellationToken),
                "history" => await RunHistoryAsync(backend, invocation.Arguments, cancellationToken),
                "calendar" => await RunCalendarAsync(backend, invocation.Arguments, cancellationToken),
                "task-batch" => await RunTaskBatchAsync(backend, invocation.Arguments, cancellationToken),
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
        catch (Exception)
        {
            WriteJson(new { error = new { code = SnookErrorCode.InternalError.ToString(),
                message = "The command failed unexpectedly. Check configuration and retry; sensitive exception details were not emitted." } }, stderr);
            return 1;
        }
    }

    private static async Task<IBackendClient> CreateBackendAsync(CliOptions options, CancellationToken cancellationToken)
    {
        if (options.IgnoreProfile && options.Host is null && Environment.GetEnvironmentVariable("SNOOK_HOST_MODE") is null)
            throw new CliUsageException("--no-profile requires an explicit --host embedded|daemon (or SNOOK_HOST_MODE).");
        var launchRoot = ClientProfileStore.LaunchDataDirectory(options.DataDirectory);
        var saved = options.IgnoreProfile ? null : (await new ClientProfileStore(launchRoot).ReadAsync(cancellationToken)).Profile;
        var profile = ResolveProfile(saved, launchRoot, options);
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

    private static async Task<object> RunPairingAsync(IBackendClient backend,
        IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        if (backend is not DaemonBackendClient { EndpointKind: DaemonEndpointKind.LocalLoopback } daemon)
            throw new CliUsageException("pairing administration requires --host daemon and an authenticated HTTP loopback endpoint.");
        if (args.Count == 1 && args[0] == "list")
            return new { devices = await daemon.ListAndroidPairedDevicesAsync(cancellationToken) };
        if (args.Count == 2 && args[0] == "create")
        {
            if (!Uri.TryCreate(args[1], UriKind.Absolute, out var origin))
                throw new CliUsageException("pairing create requires an HTTPS *.ts.net Serve origin.");
            return await daemon.CreatePairingChallengeAsync(origin, cancellationToken);
        }
        if (args.Count == 2 && args[0] == "revoke"
            && Guid.TryParse(args[1], out var credentialId) && credentialId != Guid.Empty)
            return await daemon.RevokeAndroidPairedDeviceAsync(credentialId, cancellationToken);
        if (args.Count == 3 && args[0] == "rename"
            && Guid.TryParse(args[1], out credentialId) && credentialId != Guid.Empty)
            return await daemon.RenameAndroidPairedDeviceAsync(credentialId, args[2], cancellationToken);
        throw new CliUsageException("pairing requires create HTTPS_ORIGIN, list, rename CREDENTIAL_ID NAME, or revoke CREDENTIAL_ID.");
    }

    private static async Task<object> RunRssFeedsAsync(IBackendClient backend, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        if (args.Count > 1 || args.Count == 1 && args[0] != "--include-deleted")
            throw new CliUsageException("rss-feeds accepts only --include-deleted.");
        return await backend.GetRssFeedsAsync(args.Count == 1, cancellationToken);
    }

    private static async Task<object> RunRssArticlesAsync(IBackendClient backend, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        if (args.Count > 1) throw new CliUsageException("rss-articles accepts an optional RssArticleQuery JSON object.");
        return await backend.GetRssArticlesAsync(args.Count == 0 ? new RssArticleQuery()
            : DeserializeArgument<RssArticleQuery>(args[0], "RSS article query"), cancellationToken);
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

    private static async Task<(int ExitCode, object Result)> RunDoctorCommandAsync(CliOptions options,
        CancellationToken cancellationToken)
    {
        if (options.IgnoreProfile && options.Host is null && Environment.GetEnvironmentVariable("SNOOK_HOST_MODE") is null)
            throw new CliUsageException("--no-profile requires an explicit --host embedded|daemon (or SNOOK_HOST_MODE).");
        ClientConnectionProfile? profile = null;
        IBackendClient? backend = null;
        try
        {
            var launchRoot = ClientProfileStore.LaunchDataDirectory(options.DataDirectory);
            var saved = options.IgnoreProfile ? null : (await new ClientProfileStore(launchRoot).ReadAsync(cancellationToken)).Profile;
            profile = ResolveProfile(saved, launchRoot, options);
            var token = options.Token ?? (profile.TokenFile is null ? Environment.GetEnvironmentVariable("SNOOK_DAEMON_TOKEN") : null);
            backend = await ClientProfileStore.OpenAsync(profile, token, cancellationToken: cancellationToken);
            var bootstrap = await backend.GetBootstrapAsync(cancellationToken);
            if (backend is DaemonBackendClient streamClient)
            {
                for (var attempt = 0; attempt < 20 && !streamClient.IsConnected; attempt++)
                    await Task.Delay(100, cancellationToken);
                if (!streamClient.IsConnected)
                    throw new DoctorStageException("stream", "The authenticated change stream did not become ready.");
            }
            var daemon = backend as DaemonBackendClient;
            return (0, new
            {
                ready = true,
                workspaceId = bootstrap.Workspace.Id,
                host = bootstrap.Capabilities.HostMode,
                endpoint = daemon?.Endpoint.AbsoluteUri,
                endpointKind = daemon?.EndpointKind.ToString(),
                contract = $"{ContractInfo.Major}.{ContractInfo.Minor}",
                stages = daemon is null ? null : new
                {
                    canonicalOrigin = "passed",
                    dns = daemon.EndpointKind == DaemonEndpointKind.TailscaleServe ? "passed" : "not-applicable",
                    tls = daemon.EndpointKind == DaemonEndpointKind.TailscaleServe ? "passed" : "not-applicable",
                    connectivity = "passed",
                    authentication = "passed",
                    contract = "passed",
                    identity = "passed",
                    expectedServerId = daemon.ExpectedServerId,
                    actualServerId = daemon.NegotiatedServerId,
                    expectedWorkspaceId = daemon.ExpectedWorkspaceId,
                    actualWorkspaceId = daemon.NegotiatedWorkspaceId ?? bootstrap.Workspace.Id,
                    stream = "passed"
                },
                capabilities = bootstrap.Capabilities
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            var failedStage = ClassifyDoctorFailure(exception, profile);
            var endpoint = profile?.Endpoint;
            DaemonEndpointKind? endpointKind = null;
            if (endpoint is not null)
            {
                try { endpointKind = DaemonEndpoint.Parse(endpoint).Kind; }
                catch (SnookException) { }
            }
            var tailnet = endpointKind == DaemonEndpointKind.TailscaleServe || profile?.TransportKind == "tailscale-serve";
            string Stage(string name, bool applicable = true) => !applicable ? "not-applicable"
                : name == failedStage.Stage ? "failed" : "not-run";
            return (2, new
            {
                ready = false,
                host = profile?.Host ?? options.Host ?? "unresolved",
                endpoint,
                endpointKind = endpointKind?.ToString() ?? profile?.TransportKind,
                stages = new
                {
                    canonicalOrigin = Stage("canonical-origin"),
                    credential = Stage("credential", profile?.Host == "daemon"),
                    dns = Stage("dns", tailnet),
                    tls = Stage("tls", tailnet),
                    connectivity = Stage("connectivity", profile?.Host == "daemon"),
                    authentication = Stage("authentication", profile?.Host == "daemon"),
                    contract = Stage("contract", profile?.Host == "daemon"),
                    identity = Stage("identity", tailnet),
                    stream = Stage("stream", profile?.Host == "daemon"),
                    workspace = Stage("workspace", profile?.Host == "embedded"),
                    expectedServerId = profile?.ExpectedServerId,
                    expectedWorkspaceId = profile?.ExpectedWorkspaceId
                },
                error = new { code = exception is SnookException snook ? snook.Code.ToString() : SnookErrorCode.StoreUnavailable.ToString(),
                    stage = failedStage.Stage, message = failedStage.Message }
            });
        }
        finally { if (backend is not null) await backend.DisposeAsync(); }
    }

    private static (string Stage, string Message) ClassifyDoctorFailure(Exception exception, ClientConnectionProfile? profile)
    {
        if (exception is DoctorStageException doctor) return (doctor.Stage, doctor.Message);
        var tailnet = profile?.TransportKind == "tailscale-serve";
        if (!tailnet && profile?.Endpoint is { } endpoint)
        {
            try { tailnet = DaemonEndpoint.Parse(endpoint).Kind == DaemonEndpointKind.TailscaleServe; }
            catch (SnookException) { }
        }
        var http = FindInner<HttpRequestException>(exception);
        if (http is not null)
        {
            return http.HttpRequestError switch
            {
                HttpRequestError.NameResolutionError when tailnet => ("dns", "The .ts.net name could not be resolved through the operating system. Check Tailscale and MagicDNS."),
                HttpRequestError.NameResolutionError => ("connectivity", "The local daemon name could not be resolved. Check the selected loopback endpoint."),
                HttpRequestError.SecureConnectionError => ("tls", "TLS certificate or handshake validation failed. Check the canonical .ts.net name and system trust; no bypass is supported."),
                HttpRequestError.ConnectionError when tailnet => ("connectivity", "The tailnet daemon could not be reached. Check Tailscale connectivity, grants, Serve, and snookd."),
                HttpRequestError.ConnectionError => ("connectivity", "The local daemon could not be reached. Check that snookd is running on the selected loopback port."),
                _ => ("connectivity", "The daemon transport failed before a valid authenticated response was received.")
            };
        }
        var message = exception.Message;
        if (message.Contains("credential", StringComparison.OrdinalIgnoreCase)
            || message.Contains("token file", StringComparison.OrdinalIgnoreCase))
            return ("credential", "The selected protected credential could not be loaded safely. Unlock the native store, select the intended owner-only file, or re-pair.");
        if (message.Contains("authentication", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase))
            return ("authentication", "Daemon authentication was rejected. The credential may be unknown or revoked.");
        if (message.Contains("contract", StringComparison.OrdinalIgnoreCase)
            || message.Contains("protocol", StringComparison.OrdinalIgnoreCase)
            || message.Contains("health metadata", StringComparison.OrdinalIgnoreCase))
            return ("contract", "The daemon contract is incompatible. Upgrade daemon and client together.");
        if (message.Contains("identity", StringComparison.OrdinalIgnoreCase))
            return ("identity", "The actual server or workspace identity did not match the saved profile.");
        if (profile is null || exception is SnookException { Code: SnookErrorCode.ValidationFailed })
            return ("canonical-origin", "Connection configuration is invalid or unsafe. Open connection settings; no workspace was opened.");
        return profile.Host == "embedded" ? ("workspace", "The embedded workspace could not be opened safely.")
            : ("connectivity", "The daemon did not complete the required health, authentication, and negotiation stages.");
    }

    private static T? FindInner<T>(Exception exception) where T : Exception
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
            if (current is T match) return match;
        return null;
    }

    private static async Task<int> RunTailnetAsync(CliOptions options, IReadOnlyList<string> args,
        TextWriter stdout, CancellationToken cancellationToken)
    {
        if (args.Count is < 1 or > 2 || args[0] is not ("status" or "setup" or "disable")
            || args.Count == 2 && args[1] != "--apply")
            throw new CliUsageException("tailnet requires 'status', 'setup [--apply]', or 'disable --apply'. Setup never enables Funnel or edits tailnet policy.");
        if (args[0] == "disable" && args.Count != 2)
            throw new CliUsageException("tailnet disable requires --apply and removes only the exact mapping recorded by Snook setup.");
        var local = await OpenLocalAdministratorAsync(options, cancellationToken);
        await using var client = local.Client;
        var manager = new TailscaleServeManager();
        TailscaleServeStatus status;
        var receiptPath = Path.Combine(local.DataDirectory, "Snook", "tailscale-serve.json");
        if (args is ["setup", "--apply"])
        {
            status = await manager.ApplyAsync(client.Endpoint, async (planned, token) =>
            {
                if (planned.ServeOrigin is null)
                    throw new SnookException(SnookErrorCode.StoreUnavailable,
                        "Tailscale did not return a Serve origin to record; no mapping was changed.");
                await EnsureServeReceiptAsync(receiptPath, planned.ServeOrigin, planned.Upstream, token);
            }, cancellationToken);
        }
        else if (args is ["disable", "--apply"])
        {
            var receipt = await ReadServeReceiptAsync(receiptPath, cancellationToken);
            status = await manager.DisableExactAsync(client.Endpoint, receipt.Origin, receipt.Upstream, cancellationToken);
            File.Delete(receiptPath);
        }
        else status = await manager.InspectAsync(client.Endpoint, cancellationToken);
        WriteJson(new
        {
            status.State,
            serveOrigin = status.ServeOrigin?.AbsoluteUri,
            upstream = status.Upstream.AbsoluteUri,
            status.Running,
            status.MagicDnsEnabled,
            status.HttpsEnabled,
            status.FunnelConflict,
            status.CurrentRootProxy,
            preview = string.Join(' ', status.PreviewCommand),
            status.Guidance,
            funnel = "unsupported-public",
            grantTemplate = new
            {
                grants = new[] { new { src = GrantSources, dst = GrantDestinations, ip = GrantPorts } }
            },
            nextSteps = BuildTailnetNextSteps(status)
        }, stdout);
        return status.State is "ready" or "ready-to-apply" or "https-consent-required" or "disabled" ? 0 : 2;
    }

    internal static TailnetSetupNextSteps BuildTailnetNextSteps(TailscaleServeStatus status)
    {
        var origin = status.ServeOrigin?.AbsoluteUri ?? "SERVE_URL";
        return new TailnetSetupNextSteps(
            $"snook-cli --host daemon --data-dir HOST_DATA_ROOT pair create DEVICE_NAME {origin} [LOGIN]",
            $"snook-cli --data-dir CLIENT_DATA_ROOT pair redeem {origin} PRIVATE_CODE_FILE DEVICE_NAME [native|owner-file]");
    }

    internal static async Task WriteServeReceiptAsync(string path, Uri origin, Uri upstream, CancellationToken cancellationToken)
        => await WriteServeReceiptAsync(path, origin, upstream, replaceExisting: true, cancellationToken);

    private static async Task WriteServeReceiptAsync(string path, Uri origin, Uri upstream, bool replaceExisting,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path)!;
        ValidateServeReceiptAncestors(directory);
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
        else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        ValidateServeReceiptAncestors(directory);
        var staging = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
                Options = FileOptions.Asynchronous | FileOptions.WriteThrough };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using var stream = new FileStream(staging, options);
            await JsonSerializer.SerializeAsync(stream, new { version = 1, origin = origin.AbsoluteUri, upstream = upstream.AbsoluteUri }, cancellationToken: cancellationToken);
            stream.Flush(flushToDisk: true);
            if (File.Exists(path)) CheckServeReceiptFile(path, requirePrivate: true);
            File.Move(staging, path, replaceExisting);
        }
        finally { if (File.Exists(staging)) File.Delete(staging); }
    }

    internal static async Task EnsureServeReceiptAsync(string path, Uri origin, Uri upstream,
        CancellationToken cancellationToken)
    {
        if (File.Exists(path))
        {
            await RequireMatchingServeReceiptAsync(path, origin, upstream, cancellationToken);
            return;
        }

        try
        {
            await WriteServeReceiptAsync(path, origin, upstream, replaceExisting: false, cancellationToken);
        }
        catch (IOException) when (File.Exists(path))
        {
            await RequireMatchingServeReceiptAsync(path, origin, upstream, cancellationToken);
        }
    }

    private static async Task RequireMatchingServeReceiptAsync(string path, Uri origin, Uri upstream,
        CancellationToken cancellationToken)
    {
        var existing = await ReadServeReceiptAsync(path, cancellationToken);
        if (existing.Origin != origin || existing.Upstream != upstream)
            throw new SnookException(SnookErrorCode.StoreUnavailable,
                "A different Serve setup receipt already exists; no mapping was changed.");
    }

    internal static async Task<(Uri Origin, Uri Upstream)> ReadServeReceiptAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            ValidateServeReceiptAncestors(Path.GetDirectoryName(path)!);
            var info = new FileInfo(path);
            CheckServeReceiptFile(path, requirePrivate: true);
            if (info.Length is <= 0 or > 8192) throw UnsafeServeReceipt();
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path, cancellationToken),
                new JsonDocumentOptions { MaxDepth = 8, CommentHandling = JsonCommentHandling.Disallow });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new JsonException();
            var properties = root.EnumerateObject().ToArray();
            if (properties.Length != 3
                || properties.Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() != 3
                || properties.Any(property => property.Name is not ("version" or "origin" or "upstream"))
                || !root.TryGetProperty("version", out var version) || !version.TryGetInt32(out var versionNumber) || versionNumber != 1
                || !root.TryGetProperty("origin", out var originElement) || originElement.ValueKind != JsonValueKind.String
                || !Uri.TryCreate(originElement.GetString(), UriKind.Absolute, out var origin)
                || !root.TryGetProperty("upstream", out var upstreamElement) || upstreamElement.ValueKind != JsonValueKind.String
                || !Uri.TryCreate(upstreamElement.GetString(), UriKind.Absolute, out var upstream)) throw new JsonException();
            return (DaemonEndpoint.Parse(origin.OriginalString, DaemonEndpointKind.TailscaleServe).Origin,
                DaemonEndpoint.Parse(upstream.OriginalString, DaemonEndpointKind.LocalLoopback).Origin);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException)
        {
            throw new SnookException(SnookErrorCode.StoreUnavailable, "The Serve setup receipt is missing, corrupt, or unsafe; no mapping was changed.", exception);
        }
    }

    private static void ValidateServeReceiptAncestors(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current))
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw UnsafeServeReceipt();
    }

    private static void CheckServeReceiptFile(string path, bool requirePrivate)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0
            || requirePrivate && !OperatingSystem.IsWindows() && (File.GetUnixFileMode(path)
                & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                    | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
            throw UnsafeServeReceipt();
    }

    private static SnookException UnsafeServeReceipt() => new(SnookErrorCode.ValidationFailed,
        "The Serve setup receipt is missing or unsafe; no mapping was changed.");

    private static async Task<int> RunPairAsync(CliOptions options, IReadOnlyList<string> args,
        TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        if (args.Count == 2 && args[0] == "cancel")
        {
            var code = await ReadPrivatePairingCodeAsync(args[1], cancellationToken);
            var local = await OpenLocalAdministratorAsync(options, cancellationToken);
            await using var client = local.Client;
            using var administrator = new DaemonAdministrationClient(client.Endpoint, local.RecoveryToken);
            await administrator.CancelPairingAsync(code, cancellationToken);
            WriteJson(new { cancelled = true }, stdout);
            return 0;
        }
        if (args.Count is >= 3 and <= 4 && args[0] == "create")
        {
            if (!Uri.TryCreate(args[2], UriKind.Absolute, out var serveOrigin))
                throw new CliUsageException("SERVE_URL must be an absolute canonical .ts.net HTTPS root.");
            var local = await OpenLocalAdministratorAsync(options, cancellationToken);
            await using var client = local.Client;
            using var administrator = new DaemonAdministrationClient(client.Endpoint, local.RecoveryToken);
            var invitation = await administrator.CreatePairingAsync(args[1], serveOrigin,
                args.Count == 4 ? args[3] : null, cancellationToken);
            WriteJson(new
            {
                invitation.Version,
                invitation.ServeOrigin,
                invitation.PairingCode,
                invitation.ExpiresAtUtc,
                invitation.DeviceName,
                invitation.IntendedTailscaleLogin,
                warning = "Both devices must be on the intended tailnet. Treat the single-use code as a short-lived secret. Funnel is public and unsupported."
            }, stdout);
            return 0;
        }
        if (args.Count is 4 or 5 && args[0] == "redeem")
        {
            if (!Uri.TryCreate(args[1], UriKind.Absolute, out var serveOrigin))
                throw new CliUsageException("SERVE_URL must be an absolute canonical .ts.net HTTPS root.");
            var code = await ReadPrivatePairingCodeAsync(args[2], cancellationToken);
            await using var pending = await TailnetPairingClient.RedeemAsync(serveOrigin, code, args[3], cancellationToken: cancellationToken);
            var identity = pending.Identity;
            var phrase = $"{identity.ServerId} {identity.WorkspaceId}";
            WriteJson(new
            {
                confirmationRequired = true,
                serveOrigin = identity.ServeOrigin.AbsoluteUri,
                identity.ServerId,
                identity.WorkspaceId,
                identity.WorkspaceName,
                contract = identity.Contract.ToString(),
                identity.CredentialId,
                identity.DeviceName,
                identity.TailscaleLogin,
                prompt = $"Type the exact server and workspace IDs separated by one space: {phrase}"
            }, stdout);
            await stderr.WriteLineAsync("Review the identity JSON above. To confirm, type the exact server and workspace IDs separated by one space; any other input revokes the issued credential.");
            var confirmation = await Console.In.ReadLineAsync(cancellationToken);
            if (!string.Equals(confirmation, phrase, StringComparison.Ordinal))
            {
                await pending.CancelAsync(cancellationToken);
                throw new CliUsageException("Pairing confirmation did not match; the issued credential was revoked and no profile was saved.");
            }
            var launchRoot = ClientProfileStore.LaunchDataDirectory(options.DataDirectory);
            var profiles = new ClientProfileStore(launchRoot);
            var current = options.IgnoreProfile ? new ClientProfileSnapshot(null, null) : await profiles.ReadAsync(cancellationToken);
            var storeChoice = args.Count == 5 ? args[4] : "native";
            IClientCredentialStore credentialStore = storeChoice switch
            {
                "native" => ClientCredentialStores.CreateNative(),
                "owner-file" => new OwnerOnlyFileCredentialStore(launchRoot),
                _ => throw new CliUsageException("Credential store must be 'native' or the explicitly selected 'owner-file' fallback.")
            };
            var profile = await pending.ConfirmAsync(launchRoot, credentialStore, args[3], cancellationToken);
            await profiles.SaveAsync(profile, current.Stamp, cancellationToken);
            pending.Complete();
            try
            {
                await using var verified = await ClientProfileStore.OpenAsync(profile,
                    cancellationToken: cancellationToken);
                _ = await verified.GetBootstrapAsync(cancellationToken);
            }
            catch (Exception exception) when (exception is SnookException or IOException
                or HttpRequestException or OperationCanceledException)
            {
                throw new SnookException(SnookErrorCode.StoreUnavailable,
                    "The reviewed profile was saved, but its provisional credential could not be activated. Retry a connection promptly; if the five-minute window expires, pair again.", exception);
            }
            WriteJson(new { paired = true, profile = profiles.ProfilePath, identity.ServerId, identity.WorkspaceId,
                credentialStore = storeChoice, credentialReference = profile.CredentialReference }, stdout);
            return 0;
        }
        throw new CliUsageException("pair requires 'create DEVICE_NAME SERVE_URL [LOGIN]' or 'cancel PRIVATE_CODE_FILE' locally, or 'redeem SERVE_URL PRIVATE_CODE_FILE DEVICE_NAME [native|owner-file]' on the client.");
    }

    private static async Task<string> ReadPrivatePairingCodeAsync(string path, CancellationToken cancellationToken)
    {
        if (!Path.IsPathFullyQualified(path)) throw new CliUsageException("PRIVATE_CODE_FILE must be an absolute owner-only file path.");
        try
        {
            var info = new FileInfo(path);
            if (info.Length is <= 0 or > 1024 || info.LinkTarget is not null
                || !OperatingSystem.IsWindows() && (File.GetUnixFileMode(path) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite
                    | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
                throw new CliUsageException("PRIVATE_CODE_FILE must be a bounded owner-only regular file, not a link.");
            var code = (await File.ReadAllTextAsync(path, cancellationToken)).Trim();
            if (code.Length is < 20 or > 64 || code.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
                throw new CliUsageException("PRIVATE_CODE_FILE does not contain a valid bounded pairing code.");
            return code;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new CliUsageException("PRIVATE_CODE_FILE could not be read safely.");
        }
    }

    private static async Task<int> RunDeviceAdministrationAsync(CliOptions options, IReadOnlyList<string> args,
        TextWriter stdout, CancellationToken cancellationToken)
    {
        if (args.Count is < 1 or > 2 || args[0] is not ("list" or "revoke") || args[0] == "revoke" && args.Count != 2)
            throw new CliUsageException("devices requires 'list' or 'revoke CREDENTIAL_ID'. Run it against the host's local daemon profile.");
        var local = await OpenLocalAdministratorAsync(options, cancellationToken);
        await using var client = local.Client;
        using var administrator = new DaemonAdministrationClient(client.Endpoint, local.RecoveryToken);
        if (args[0] == "list") WriteJson(await administrator.ListDevicesAsync(cancellationToken), stdout);
        else
        {
            if (!Guid.TryParse(args[1], out var id) || id == Guid.Empty) throw new CliUsageException("CREDENTIAL_ID must be a nonempty GUID.");
            await administrator.RevokeDeviceAsync(id, cancellationToken);
            WriteJson(new { revoked = true, credentialId = id }, stdout);
        }
        return 0;
    }

    private static async Task<LocalAdministrator> OpenLocalAdministratorAsync(CliOptions options, CancellationToken cancellationToken)
    {
        var launchRoot = ClientProfileStore.LaunchDataDirectory(options.DataDirectory);
        var saved = options.IgnoreProfile ? null : (await new ClientProfileStore(launchRoot).ReadAsync(cancellationToken)).Profile;
        var profile = ResolveProfile(saved, launchRoot, options);
        if (profile.Host != "daemon" || profile.TransportKind == "tailscale-serve")
            throw new CliUsageException("Host administration requires a local daemon profile. Use --no-profile --host daemon --data-dir HOST_DATA_ROOT if a tailnet profile is saved.");
        var token = options.Token ?? (profile.TokenFile is null ? Environment.GetEnvironmentVariable("SNOOK_DAEMON_TOKEN") : null);
        var tokenPath = profile.TokenFile ?? Path.Combine(profile.DataDirectory, "Snook", "daemon.token");
        token ??= await DaemonConnection.ReadPrivateTokenFileAsync(tokenPath, cancellationToken);
        var client = await DaemonConnection.ConnectProfileAsync(profile, token, cancellationToken);
        try { _ = await client.GetBootstrapAsync(cancellationToken); return new LocalAdministrator(client, token, profile.DataDirectory); }
        catch { await client.DisposeAsync(); throw; }
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
            rss = new { read = "rss-feeds [--include-deleted] | rss-articles [RssArticleQuery JSON]", write = "call subscribe-rss | refresh-rss | set-rss-feed-deleted | set-rss-article-state" },
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
                "--transport" => options with { TransportKind = value },
                "--credential-ref" => options with { CredentialReference = value },
                "--expect-server" => options with { ExpectedServerId = value },
                "--expect-workspace" => options with { ExpectedWorkspaceId = value },
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

    private static ClientConnectionProfile ResolveProfile(ClientConnectionProfile? saved, string launchRoot, CliOptions options)
    {
        Guid? ParseExpected(string? value, string option)
        {
            if (value is null) return null;
            if (!Guid.TryParse(value, out var parsed) || parsed == Guid.Empty)
                throw new CliUsageException($"{option} requires a nonempty GUID.");
            return parsed;
        }
        return ClientProfileStore.Resolve(saved, launchRoot, options.Host, options.DataDirectory, options.Endpoint,
            options.TokenFile, transportKind: options.TransportKind, credentialReference: options.CredentialReference,
            expectedServerId: ParseExpected(options.ExpectedServerId, "--expect-server"),
            expectedWorkspaceId: ParseExpected(options.ExpectedWorkspaceId, "--expect-workspace"));
    }

    private static void PrintUsage(TextWriter writer)
    {
        writer.WriteLine("Snook — structured JSON workspace client");
        writer.WriteLine();
        writer.WriteLine("Usage: snook-cli [global options] <command> [arguments]");
        writer.WriteLine("Global options: --data-dir PATH --host embedded|daemon --endpoint URL --token TOKEN --token-file PATH --no-profile");
        writer.WriteLine("Tailnet options: --transport tailscale-serve --credential-ref REF --expect-server GUID --expect-workspace GUID");
        writer.WriteLine("Clients read <launch-data-dir>/Snook/client-profile.json. Flags override environment, then saved settings. --no-profile ignores that file explicitly.");
        writer.WriteLine("Commands: bootstrap | tasks [search] | summary GROUP [days] | history [HistoryQuery JSON]");
        writer.WriteLine("          rss-feeds [--include-deleted] | rss-articles [RssArticleQuery JSON]");
        writer.WriteLine("          habits [HabitQuery JSON] (read-only daily progress and check-ins)");
        writer.WriteLine("          journals [--include-deleted] | journal-entries [JournalEntryQuery JSON]");
        writer.WriteLine("          call create-journal|update-journal|set-journal-deleted JSON");
        writer.WriteLine("          call create-journal-entry|update-journal-entry|set-journal-entry-deleted JSON");
        writer.WriteLine("          call get-journal-entry|get-journal-tags JSON");
        writer.WriteLine("          calendar blocks|events [Range JSON] | task-batch plan|apply JSON | doctor | watch | api");
        writer.WriteLine("          tailnet status|setup [--apply]|disable --apply | pair create DEVICE SERVE_URL [LOGIN]");
        writer.WriteLine("          pair cancel PRIVATE_CODE_FILE");
        writer.WriteLine("          pair redeem SERVE_URL PRIVATE_CODE_FILE DEVICE [native|owner-file]");
        writer.WriteLine("          devices list|revoke CREDENTIAL_ID");
        writer.WriteLine("          pairing create HTTPS_ORIGIN | pairing list | pairing rename CREDENTIAL_ID NAME | pairing revoke CREDENTIAL_ID");
        writer.WriteLine("          call METHOD [JSON OBJECT]");
        writer.WriteLine();
        writer.WriteLine("Example: snook-cli call create-task '{\"projectId\":\"...\",\"title\":\"Write brief\",\"priority\":\"High\"}'");
        writer.WriteLine("Mutation: snook-cli call complete-task '{\"taskId\":\"...\",\"request\":{\"operationId\":\"...\",\"clientDeviceId\":\"...\",\"expectedRevision\":4}}'");
        writer.WriteLine("Run 'snook-cli api' for every method and its exact named arguments.");
    }

    private sealed record CliOptions(string? DataDirectory = null, string? Host = null, string? Endpoint = null,
        string? Token = null, string? TokenFile = null, bool IgnoreProfile = false, string? TransportKind = null,
        string? CredentialReference = null, string? ExpectedServerId = null, string? ExpectedWorkspaceId = null);
    private sealed record Invocation(string Command, IReadOnlyList<string> Arguments, CliOptions Options);
    private sealed record LocalAdministrator(DaemonBackendClient Client, string RecoveryToken, string DataDirectory);
    private sealed class DoctorStageException(string stage, string message) : Exception(message)
    {
        public string Stage { get; } = stage;
    }
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
