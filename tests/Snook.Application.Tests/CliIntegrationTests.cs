using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Snook.Cli;
using Snook.Contracts;
using Snook.Domain;
using Xunit;

namespace Snook.Application.Tests;

public sealed class CliIntegrationTests
{
    private static readonly string[] BatchTags = ["release", "cli"];
    private static readonly JsonSerializerOptions CliJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    [Fact]
    public async Task ApiDescribesEveryPublicBackendMethod()
    {
        var result = await RunCliAsync("api");

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.StandardOutput);
        var methodNames = document.RootElement.GetProperty("methods")
            .EnumerateArray().Select(method => method.GetProperty("name").GetString()).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(typeof(IBackendClient).GetMethods().Count(method => method.ReturnType != typeof(ValueTask)), methodNames.Count);
        Assert.Contains("CreateTaskAsync", methodNames);
        Assert.Contains("RestoreBackupAsync", methodNames);
        Assert.Contains("UpsertCalendarEventExceptionAsync", methodNames);
        Assert.Contains("CorrectSessionAsync", methodNames);
        Assert.Contains("BulkUpdateTasksAsync", methodNames);
        Assert.Equal(500, document.RootElement.GetProperty("cliCommands").GetProperty("taskBatch").GetProperty("limit").GetInt32());
        foreach (var name in new[] { "CreateBoardAsync", "CreateProjectAsync", "CreateActivityAsync", "CreateActivityGroupAsync",
            "CreateCalendarAsync", "CreateCalendarEventAsync", "CreateScheduleBlockAsync", "CreateTaskAsync", "AddTaskLinkAsync",
            "AddTaskTagAsync", "AddProjectTagAsync", "AddActivityTagAsync", "AddTaskDependencyAsync" })
        {
            var method = document.RootElement.GetProperty("methods").EnumerateArray().Single(item => item.GetProperty("name").GetString() == name);
            var request = method.GetProperty("arguments").EnumerateArray().Single(item => item.GetProperty("name").GetString() == "request");
            Assert.Equal("OperationRequest", request.GetProperty("type").GetString());
            Assert.False(request.GetProperty("required").GetBoolean());
        }
    }

    [Fact]
    public async Task EmbeddedCliCanCreateAndMutateWorkspaceDataThroughNamedJsonArguments()
    {
        var directory = Directory.CreateTempSubdirectory("snook-cli-test-");
        try
        {
            var dataDirectory = directory.FullName;
            var bootstrap = await RunCliAsync("--data-dir", dataDirectory, "bootstrap");
            Assert.Equal(0, bootstrap.ExitCode);
            var projectId = ReadId(bootstrap.StandardOutput, "projects", 0);

            var created = await RunCliAsync("--data-dir", dataDirectory, "call", "create-task", JsonSerializer.Serialize(new
            {
                projectId,
                title = "Document the CLI",
                priority = "High"
            }));
            Assert.Equal(0, created.ExitCode);
            using var createdDocument = JsonDocument.Parse(created.StandardOutput);
            var taskId = createdDocument.RootElement.GetProperty("id").GetString();
            var revision = createdDocument.RootElement.GetProperty("revision").GetInt64();
            Assert.Equal("Open", createdDocument.RootElement.GetProperty("status").GetString());

            var operationId = Guid.NewGuid();
            var clientDeviceId = Guid.NewGuid();
            var completed = await RunCliAsync("--data-dir", dataDirectory, "call", "complete-task", JsonSerializer.Serialize(new
            {
                taskId,
                request = new { operationId, clientDeviceId, expectedRevision = revision }
            }));
            Assert.Equal(0, completed.ExitCode);
            using var completedDocument = JsonDocument.Parse(completed.StandardOutput);
            Assert.Equal("Completed", completedDocument.RootElement.GetProperty("status").GetString());

            var retry = await RunCliAsync("--data-dir", dataDirectory, "call", "complete-task", JsonSerializer.Serialize(new
            {
                taskId,
                request = new { operationId, clientDeviceId, expectedRevision = revision }
            }));
            Assert.Equal(0, retry.ExitCode);
            using var retryDocument = JsonDocument.Parse(retry.StandardOutput);
            Assert.Equal(completedDocument.RootElement.GetProperty("revision").GetInt64(), retryDocument.RootElement.GetProperty("revision").GetInt64());

            var taskDetails = await RunCliAsync("--data-dir", dataDirectory, "call", "get-task-details", JsonSerializer.Serialize(new { taskId }));
            Assert.Equal(0, taskDetails.ExitCode);
            Assert.Contains("Document the CLI", taskDetails.StandardOutput, StringComparison.Ordinal);
            await AssertCallerCreateRetryAsync(["--data-dir", dataDirectory]);
            await AssertMaintenanceOutputsAsync(["--data-dir", dataDirectory], dataDirectory);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task HabitsHelperReadsProgressWithoutMutatingAndValidatesBounds()
    {
        var directory = Directory.CreateTempSubdirectory("snook-cli-habits-");
        try
        {
            var day = DateOnly.FromDateTime(DateTime.UtcNow);
            var created = await RunCliAsync("--data-dir", directory.FullName, "call", "create-habit", JsonSerializer.Serialize(new
            {
                definition = new HabitDefinition("CLI habit", "Daily", day, "UTC"),
                request = new OperationRequest(Guid.NewGuid(), Guid.NewGuid())
            }, CliJsonOptions));
            Assert.True(created.ExitCode == 0, created.StandardError);
            var habit = JsonSerializer.Deserialize<Habit>(created.StandardOutput, CliJsonOptions)!;
            var completed = await RunCliAsync("--data-dir", directory.FullName, "call", "set-habit-completion", JsonSerializer.Serialize(new
            {
                habitId = habit.Id, day, completed = true,
                request = new OperationRequest(Guid.NewGuid(), Guid.NewGuid(), habit.Revision)
            }, CliJsonOptions));
            Assert.True(completed.ExitCode == 0, completed.StandardError);
            var before = await RunCliAsync("--data-dir", directory.FullName, "bootstrap");
            var result = await RunCliAsync("--data-dir", directory.FullName, "habits", "{\"days\":7}");
            Assert.True(result.ExitCode == 0, result.StandardError);
            var progress = Assert.Single(JsonSerializer.Deserialize<HabitProgress[]>(result.StandardOutput, CliJsonOptions)!);
            Assert.Equal(habit.Id, progress.Habit.Id);
            Assert.Equal(1, progress.CurrentStreak);
            Assert.Equal(day, Assert.Single(progress.CheckIns).Date);
            var after = await RunCliAsync("--data-dir", directory.FullName, "bootstrap");
            using var beforeJson = JsonDocument.Parse(before.StandardOutput);
            using var afterJson = JsonDocument.Parse(after.StandardOutput);
            Assert.Equal(beforeJson.RootElement.GetProperty("committedCursor").GetInt64(), afterJson.RootElement.GetProperty("committedCursor").GetInt64());
            Assert.NotEqual(0, (await RunCliAsync("--data-dir", directory.FullName, "habits", "{\"days\":367}")).ExitCode);
            Assert.NotEqual(0, (await RunCliAsync("--data-dir", directory.FullName, "habits", "null")).ExitCode);
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public async Task EmbeddedCliPlansAppliesAndSafelyRetriesTaskBatches()
    {
        var directory = Directory.CreateTempSubdirectory("snook-cli-batch-test-");
        try
        {
            var dataDirectory = directory.FullName;
            var bootstrap = await RunCliAsync("--data-dir", dataDirectory, "bootstrap");
            Assert.Equal(0, bootstrap.ExitCode);
            var projectId = ReadId(bootstrap.StandardOutput, "projects", 0);
            var first = await CreateTaskAsync(dataDirectory, projectId, "Release alpha", "Low");
            var second = await CreateTaskAsync(dataDirectory, projectId, "Release beta", "Low");
            var unselected = await CreateTaskAsync(dataDirectory, projectId, "Unrelated task", "Medium");
            var operationId = Guid.NewGuid();
            var clientDeviceId = Guid.NewGuid();
            var planRequest = JsonSerializer.Serialize(new
            {
                selection = new { projectId, search = "Release" },
                update = new
                {
                    description = "Shared from CLI",
                    priority = "Urgent",
                    changeDueDate = true,
                    dueDate = "2026-10-02",
                    starred = true,
                    tagsToAdd = BatchTags
                },
                operationId,
                clientDeviceId
            });

            var plan = await RunCliAsync("--data-dir", dataDirectory, "task-batch", "plan", planRequest);
            Assert.Equal(0, plan.ExitCode);
            using (var planDocument = JsonDocument.Parse(plan.StandardOutput))
            {
                var root = planDocument.RootElement;
                Assert.Equal("task-batch-plan", root.GetProperty("kind").GetString());
                Assert.Equal(2, root.GetProperty("count").GetInt32());
                Assert.Equal(operationId, root.GetProperty("request").GetProperty("operationId").GetGuid());
                Assert.Equal(clientDeviceId, root.GetProperty("request").GetProperty("clientDeviceId").GetGuid());
                Assert.Collection(root.GetProperty("preview").EnumerateArray(),
                    item => Assert.Equal("Release alpha", item.GetProperty("title").GetString()),
                    item => Assert.Equal("Release beta", item.GetProperty("title").GetString()));
            }

            var concurrent = await RunCliAsync("--data-dir", dataDirectory, "call", "update-task", JsonSerializer.Serialize(new
            {
                taskId = second.Id,
                update = new
                {
                    title = "Release beta",
                    description = "Concurrent edit",
                    priority = "Low",
                    dueDate = (string?)null,
                    defaultActivityId = (Guid?)null,
                    starred = false
                },
                request = new { operationId = Guid.NewGuid(), clientDeviceId, expectedRevision = second.Revision }
            }));
            Assert.Equal(0, concurrent.ExitCode);

            var staleApply = await RunCliAsync("--data-dir", dataDirectory, "task-batch", "apply", plan.StandardOutput);
            Assert.Equal(2, staleApply.ExitCode);
            using (var errorDocument = JsonDocument.Parse(staleApply.StandardError))
            {
                Assert.Equal("RevisionConflict", errorDocument.RootElement.GetProperty("error").GetProperty("code").GetString());
            }

            using var unchanged = await GetTaskDetailsAsync(dataDirectory, first.Id);
            Assert.Equal(string.Empty, unchanged.RootElement.GetProperty("task").GetProperty("description").GetString());
            Assert.Equal("Low", unchanged.RootElement.GetProperty("task").GetProperty("priority").GetString());

            var refreshedPlan = await RunCliAsync("--data-dir", dataDirectory, "task-batch", "plan", planRequest);
            Assert.Equal(0, refreshedPlan.ExitCode);
            var applied = await RunCliAsync("--data-dir", dataDirectory, "task-batch", "apply", refreshedPlan.StandardOutput);
            Assert.Equal(0, applied.ExitCode);
            using var appliedDocument = JsonDocument.Parse(applied.StandardOutput);
            Assert.Equal("task-batch-result", appliedDocument.RootElement.GetProperty("kind").GetString());
            Assert.Equal(2, appliedDocument.RootElement.GetProperty("count").GetInt32());
            Assert.All(appliedDocument.RootElement.GetProperty("tasks").EnumerateArray(), task =>
            {
                Assert.Equal("Shared from CLI", task.GetProperty("description").GetString());
                Assert.Equal("Urgent", task.GetProperty("priority").GetString());
                Assert.Equal("2026-10-02", task.GetProperty("dueDate").GetString());
                Assert.True(task.GetProperty("starred").GetBoolean());
            });

            var retry = await RunCliAsync("--data-dir", dataDirectory, "task-batch", "apply", refreshedPlan.StandardOutput);
            Assert.Equal(0, retry.ExitCode);
            using var retryDocument = JsonDocument.Parse(retry.StandardOutput);
            Assert.Equal(
                appliedDocument.RootElement.GetProperty("tasks")[0].GetProperty("revision").GetInt64(),
                retryDocument.RootElement.GetProperty("tasks")[0].GetProperty("revision").GetInt64());

            using var details = await GetTaskDetailsAsync(dataDirectory, first.Id);
            Assert.Collection(details.RootElement.GetProperty("tags").EnumerateArray()
                    .OrderBy(tag => tag.GetProperty("displayName").GetString(), StringComparer.Ordinal),
                tag => Assert.Equal("cli", tag.GetProperty("displayName").GetString()),
                tag => Assert.Equal("release", tag.GetProperty("displayName").GetString()));
            using var untouched = await GetTaskDetailsAsync(dataDirectory, unselected.Id);
            Assert.Equal("Medium", untouched.RootElement.GetProperty("task").GetProperty("priority").GetString());
            Assert.Equal(string.Empty, untouched.RootElement.GetProperty("task").GetProperty("description").GetString());

            var unsafeSelection = await RunCliAsync("--data-dir", dataDirectory, "task-batch", "plan",
                "{\"selection\":{},\"update\":{\"starred\":true}}");
            Assert.Equal(64, unsafeSelection.ExitCode);
            Assert.Contains("selection.all", unsafeSelection.StandardError, StringComparison.Ordinal);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task DaemonCliUsesTheRemoteClientInsteadOfOpeningTheWorkspaceDatabase()
    {
        var directory = Directory.CreateTempSubdirectory("snook-cli-daemon-test-");
        using var portProbe = new TcpListener(IPAddress.Loopback, 0);
        portProbe.Start();
        var port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
        portProbe.Stop();
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        process.StartInfo.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "snookd.dll"));
        process.StartInfo.Environment["SNOOK_DATA_DIR"] = directory.FullName;
        process.StartInfo.Environment["SNOOK_DAEMON_PORT"] = port.ToString(CultureInfo.InvariantCulture);

        try
        {
            Assert.True(process.Start());
            await WaitForReadyAsync(process.StandardOutput, process.StandardError);
            var token = await WaitForFileTextAsync(Path.Combine(directory.FullName, "Snook", "daemon.token"));
            var result = await RunCliAsync(
                "--data-dir", directory.FullName,
                "--host", "daemon",
                "--endpoint", $"http://127.0.0.1:{port}/",
                "--token", token,
                "call", "create-board", "{\"name\":\"Remote board\"}");

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("Remote board", result.StandardOutput, StringComparison.Ordinal);

            var bootstrap = await RunCliAsync(
                "--data-dir", directory.FullName,
                "--host", "daemon",
                "--endpoint", $"http://127.0.0.1:{port}/",
                "--token", token,
                "bootstrap");
            Assert.Equal(0, bootstrap.ExitCode);
            var projectId = ReadId(bootstrap.StandardOutput, "projects", 0);
            var created = await RunCliAsync(
                "--data-dir", directory.FullName,
                "--host", "daemon",
                "--endpoint", $"http://127.0.0.1:{port}/",
                "--token", token,
                "call", "create-task", JsonSerializer.Serialize(new { projectId, title = "Remote batch task" }));
            Assert.Equal(0, created.ExitCode);
            using var createdDocument = JsonDocument.Parse(created.StandardOutput);
            var taskId = createdDocument.RootElement.GetProperty("id").GetGuid();

            var plan = await RunCliAsync(
                "--data-dir", directory.FullName,
                "--host", "daemon",
                "--endpoint", $"http://127.0.0.1:{port}/",
                "--token", token,
                "task-batch", "plan", JsonSerializer.Serialize(new
                {
                    selection = new { taskIds = new[] { taskId } },
                    update = new { starred = true }
                }));
            Assert.Equal(0, plan.ExitCode);
            var applied = await RunCliAsync(
                "--data-dir", directory.FullName,
                "--host", "daemon",
                "--endpoint", $"http://127.0.0.1:{port}/",
                "--token", token,
                "task-batch", "apply", plan.StandardOutput);
            Assert.Equal(0, applied.ExitCode);
            using var appliedDocument = JsonDocument.Parse(applied.StandardOutput);
            Assert.True(appliedDocument.RootElement.GetProperty("tasks")[0].GetProperty("starred").GetBoolean());
            var habits = await RunCliAsync("--data-dir", directory.FullName, "--host", "daemon",
                "--endpoint", $"http://127.0.0.1:{port}/", "--token", token, "habits");
            Assert.True(habits.ExitCode == 0, habits.StandardError);
            Assert.Equal("[]", habits.StandardOutput.Trim());
            await JournalCliTests.AssertJournalCliAsync(["--data-dir", directory.FullName, "--host", "daemon",
                "--endpoint", $"http://127.0.0.1:{port}/", "--token", token]);
            await AssertDaemonWatchAsync(directory.FullName, port, token);
            await AssertCallerCreateRetryAsync(["--data-dir", directory.FullName, "--host", "daemon",
                "--endpoint", $"http://127.0.0.1:{port}/", "--token", token]);
            await AssertMaintenanceOutputsAsync(["--data-dir", directory.FullName, "--host", "daemon",
                "--endpoint", $"http://127.0.0.1:{port}/", "--token", token], directory.FullName);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }

            directory.Delete(recursive: true);
        }
    }

    private static async Task AssertMaintenanceOutputsAsync(string[] options, string directory)
    {
        foreach (var method in new[] { "create-backup", "export-json", "export-csv" })
        {
            var destinationPath = Path.Combine(directory, "cli-" + method);
            var arguments = method == "export-csv"
                ? JsonSerializer.Serialize(new { destinationPath, rangeStartUtc = "2026-09-01T00:00:00Z", rangeEndUtc = "2026-10-01T00:00:00Z" })
                : JsonSerializer.Serialize(new { destinationPath });
            var first = await RunCliAsync([.. options, "call", method, arguments]);
            Assert.True(first.ExitCode == 0, first.StandardError);
            var bytes = await File.ReadAllBytesAsync(destinationPath);
            if (method == "export-json")
            {
                using var result = JsonDocument.Parse(first.StandardOutput);
                Assert.Equal(5, result.RootElement.GetProperty("schemaVersion").GetInt32());
                using var export = JsonDocument.Parse(bytes);
                Assert.Equal(5, export.RootElement.GetProperty("schemaVersion").GetInt32());
                Assert.True(export.RootElement.TryGetProperty("settings", out _));
                Assert.True(export.RootElement.TryGetProperty("committedCursor", out _));
                Assert.True(export.RootElement.TryGetProperty("taskLinks", out _));
                Assert.True(export.RootElement.TryGetProperty("taskDependencies", out _));
            }
            var repeat = await RunCliAsync([.. options, "call", method, arguments]);
            Assert.Equal(2, repeat.ExitCode);
            Assert.Contains("ValidationFailed", repeat.StandardError, StringComparison.Ordinal);
            Assert.DoesNotContain(directory, repeat.StandardError, StringComparison.Ordinal);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(destinationPath));
        }
        var sourcePath = Path.Combine(directory, "cli-create-backup");
        var manifestPath = sourcePath + ".manifest.json";
        var manifest = await File.ReadAllTextAsync(manifestPath);
        await File.WriteAllTextAsync(manifestPath, "{}");
        var invalidRestore = await RunCliAsync([.. options, "call", "restore-backup", JsonSerializer.Serialize(new { sourcePath })]);
        Assert.Equal(2, invalidRestore.ExitCode);
        Assert.Contains("SchemaIncompatible", invalidRestore.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain(directory, invalidRestore.StandardError, StringComparison.Ordinal);
        await File.WriteAllTextAsync(manifestPath, manifest);
        var restored = await RunCliAsync([.. options, "call", "restore-backup", JsonSerializer.Serialize(new { sourcePath })]);
        Assert.True(restored.ExitCode == 0, restored.StandardError);
    }

    private static async Task AssertCallerCreateRetryAsync(string[] options)
    {
        var request = new OperationRequest(Guid.NewGuid(), Guid.NewGuid());
        var arguments = JsonSerializer.Serialize(new { name = "CLI create receipt", request }, CliJsonOptions);
        var created = await RunCliAsync([.. options, "call", "create-board", arguments]);
        Assert.Equal(0, created.ExitCode);
        using var document = JsonDocument.Parse(created.StandardOutput);
        var id = document.RootElement.GetProperty("id").GetGuid();
        var updated = await RunCliAsync([.. options, "call", "update-board", JsonSerializer.Serialize(new
        {
            boardId = id, update = new BoardUpdate("CLI later board name"),
            request = new OperationRequest(Guid.NewGuid(), request.ClientDeviceId, 1)
        }, CliJsonOptions)]);
        Assert.Equal(0, updated.ExitCode);
        var replay = await RunCliAsync([.. options, "call", "create-board", arguments]);
        Assert.Equal(0, replay.ExitCode);
        Assert.Equal(created.StandardOutput, replay.StandardOutput);
        var changed = await RunCliAsync([.. options, "call", "create-board",
            JsonSerializer.Serialize(new { name = "Changed create payload", request }, CliJsonOptions)]);
        Assert.Equal(2, changed.ExitCode);
        Assert.Contains("ValidationFailed", changed.StandardError, StringComparison.Ordinal);
    }

    private static async Task AssertDaemonWatchAsync(string directory, int port, string token)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        using var output = new LineWriter();
        using var error = new StringWriter(CultureInfo.InvariantCulture);
        string[] options = ["--data-dir", directory, "--host", "daemon", "--endpoint",
            $"http://127.0.0.1:{port}/", "--token", token];
        var watch = Program.RunAsync([.. options, "watch"], output, error, cancellation.Token);
        try
        {
            var ready = false;
            var connected = false;
            while (!ready || !connected)
            {
                using var record = await ReadRecordAsync();
                var kind = record.RootElement.GetProperty("type").GetString();
                ready |= kind == "ready";
                connected |= kind == "change" && record.RootElement.GetProperty("change")
                    .GetProperty("changeKind").GetString() == "reconnected";
            }
            var result = await RunCliAsync([.. options, "call", "create-board", "{\"name\":\"Watch newline\\nboard\"}"]);
            Assert.Equal(0, result.ExitCode);
            using var created = JsonDocument.Parse(result.StandardOutput);
            var id = created.RootElement.GetProperty("id").GetGuid();
            using var change = await ReadRecordAsync();
            Assert.Equal("change", change.RootElement.GetProperty("type").GetString());
            Assert.Equal(id, change.RootElement.GetProperty("change").GetProperty("aggregateId").GetGuid());
        }
        finally
        {
            await cancellation.CancelAsync();
            Assert.Equal(130, await watch.WaitAsync(TimeSpan.FromSeconds(10)));
        }

        async Task<JsonDocument> ReadRecordAsync()
        {
            var line = await output.Lines.Reader.ReadAsync(timeout.Token);
            Assert.DoesNotContain('\n', line);
            Assert.DoesNotContain('\r', line);
            return JsonDocument.Parse(line);
        }
    }

    private sealed class LineWriter : TextWriter
    {
        public System.Threading.Channels.Channel<string> Lines { get; } = System.Threading.Channels.Channel.CreateUnbounded<string>();
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;
        public override void WriteLine(string? value) => Lines.Writer.TryWrite(value ?? string.Empty);
    }

    private static async Task<(int ExitCode, string StandardOutput, string StandardError)> RunCliAsync(params string[] args)
    {
        await using var output = new StringWriter(CultureInfo.InvariantCulture);
        await using var error = new StringWriter(CultureInfo.InvariantCulture);
        var exitCode = await Program.RunAsync(args, output, error);
        return (exitCode, output.ToString(), error.ToString());
    }

    private static string ReadId(string json, string arrayName, int index)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty(arrayName)[index].GetProperty("id").GetString()!;
    }

    private static async Task<TaskItem> CreateTaskAsync(string dataDirectory, string projectId, string title, string priority)
    {
        var result = await RunCliAsync("--data-dir", dataDirectory, "call", "create-task",
            JsonSerializer.Serialize(new { projectId, title, priority }));
        Assert.Equal(0, result.ExitCode);
        return JsonSerializer.Deserialize<TaskItem>(result.StandardOutput, CliJsonOptions)!;
    }

    private static async Task<JsonDocument> GetTaskDetailsAsync(string dataDirectory, Guid taskId)
    {
        var result = await RunCliAsync("--data-dir", dataDirectory, "call", "get-task-details", JsonSerializer.Serialize(new { taskId }));
        Assert.Equal(0, result.ExitCode);
        return JsonDocument.Parse(result.StandardOutput);
    }

    private static async Task WaitForReadyAsync(StreamReader output, StreamReader error)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            var line = await output.ReadLineAsync(timeout.Token);
            if (line is null)
            {
                throw new InvalidOperationException($"snookd exited before readiness: {await error.ReadToEndAsync(timeout.Token)}");
            }

            using var document = JsonDocument.Parse(line);
            if (document.RootElement.TryGetProperty("ready", out var ready) && ready.GetBoolean())
            {
                return;
            }
        }
    }

    private static async Task<string> WaitForFileTextAsync(string path)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!File.Exists(path))
        {
            await Task.Delay(50, timeout.Token);
        }

        return (await File.ReadAllTextAsync(path, timeout.Token)).Trim();
    }
}
