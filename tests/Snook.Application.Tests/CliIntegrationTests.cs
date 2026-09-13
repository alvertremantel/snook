using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Snook.Cli;
using Snook.Contracts;
using Xunit;

namespace Snook.Application.Tests;

public sealed class CliIntegrationTests
{
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
