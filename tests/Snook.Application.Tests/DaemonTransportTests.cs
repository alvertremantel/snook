using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Snook.Contracts;
using Snook.Domain;
using Xunit;

namespace Snook.Application.Tests;

public sealed class DaemonTransportTests
{
    [Theory]
    [InlineData("http://example.com/")]
    [InlineData("https://127.0.0.1/")]
    [InlineData("http://user:secret@127.0.0.1/")]
    [InlineData("http://127.0.0.1/path/")]
    [InlineData("http://127.0.0.1/?token=secret")]
    [InlineData("http://127.0.0.1/#fragment")]
    public void ClientRejectsUnsafeEndpointsBeforeSendingCredentials(string endpoint)
    {
        var error = Assert.Throws<SnookException>(() => new DaemonBackendClient(new Uri(endpoint), "test-token"));
        Assert.Equal(SnookErrorCode.ValidationFailed, error.Code);
    }

    [Theory]
    [InlineData("99.0")]
    [InlineData("1.0")]
    [InlineData("1.5")]
    [InlineData("invalid")]
    public async Task IncompatibleHealthPreventsAnyRpc(string contract)
    {
        using var handler = new HealthHandler(contract);
        await using var client = new DaemonBackendClient(new Uri("http://127.0.0.1:43871/"), "test-token", handler);
        var error = await Assert.ThrowsAsync<SnookException>(() => client.CreateBoardAsync("must not reach RPC"));
        Assert.Equal(SnookErrorCode.StoreUnavailable, error.Code);
        Assert.Equal(0, handler.RpcCalls);
    }

    [Fact]
    public async Task MissingDaemonCredentialsNeverCreateDatabase()
    {
        var directory = Directory.CreateTempSubdirectory("snook-connection-");
        try
        {
            await Assert.ThrowsAsync<SnookException>(() => DaemonConnection.ConnectAsync(directory.FullName,
                tokenFile: Path.Combine(directory.FullName, "missing.token")));
            Assert.False(Directory.Exists(Path.Combine(directory.FullName, "Snook")));
            using var stdout = new StringWriter(CultureInfo.InvariantCulture);
            using var stderr = new StringWriter(CultureInfo.InvariantCulture);
            var result = await Cli.Program.RunAsync(["--host", "daemon", "--data-dir", directory.FullName,
                "--token-file", Path.Combine(directory.FullName, "missing.token"), "doctor"], stdout, stderr);
            Assert.Equal(2, result);
            Assert.Contains("StoreUnavailable", stderr.ToString(), StringComparison.Ordinal);
            Assert.False(Directory.Exists(Path.Combine(directory.FullName, "Snook")));
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public async Task DaemonRejectsMalformedCallsAndDeliversConcurrentChangesInOrder()
    {
        var directory = Directory.CreateTempSubdirectory("snook-transport-");
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        process.StartInfo.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "snookd.dll"));
        process.StartInfo.Environment["SNOOK_DATA_DIR"] = directory.FullName;
        process.StartInfo.Environment["SNOOK_DAEMON_PORT"] = port.ToString(CultureInfo.InvariantCulture);
        Assert.True(process.Start());
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            var ready = await process.StandardOutput.ReadLineAsync(timeout.Token);
            Assert.NotNull(ready);
            using var readiness = JsonDocument.Parse(ready);
            Assert.True(readiness.RootElement.GetProperty("ready").GetBoolean());
            var tokenPath = Path.Combine(directory.FullName, "Snook", "daemon.token");
            var token = await File.ReadAllTextAsync(tokenPath, timeout.Token);
            Assert.DoesNotContain(token, ready, StringComparison.Ordinal);
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(tokenPath));
            var endpoint = new Uri($"http://127.0.0.1:{port}/");
            using var http = new HttpClient { BaseAddress = endpoint };
            foreach (var route in new[] { "v1/health", "v1/changes" })
            {
                using var unauthorized = await http.GetAsync(route, timeout.Token);
                Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
            }
            http.DefaultRequestHeaders.Add("X-Snook-Token", token);
            // Contract 1.6 appends an optional request. Older wire calls still omit it.
            using (var legacyContent = new StringContent("{\"method\":\"CreateBoardAsync\",\"args\":[\"Legacy omitted request\"]}", Encoding.UTF8, "application/json"))
            using (var legacyResponse = await http.PostAsync("v1/call", legacyContent, timeout.Token))
                Assert.Equal(HttpStatusCode.OK, legacyResponse.StatusCode);
            foreach (var body in new[] { "{", "null", "[]", "{}",
                "{\"method\":\"CreateBoardAsync\",\"args\":[null]}",
                "{\"method\":\"CreateBoardAsync\",\"args\":[{}]}",
                "{\"method\":\"GetTaskDetailsAsync\",\"args\":[null]}",
                "{\"method\":\"GetBootstrapAsync\",\"args\":[1]}" })
            {
                using var content = new StringContent(body, Encoding.UTF8, "application/json");
                using var response = await http.PostAsync("v1/call", content, timeout.Token);
                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
                var error = await response.Content.ReadAsStringAsync(timeout.Token);
                Assert.Contains("ValidationFailed", error, StringComparison.Ordinal);
                Assert.DoesNotContain(directory.FullName, error, StringComparison.Ordinal);
            }
            foreach (var method in new[] { "InitializeAsync", "DisposeAsync", "GetType", "add_Changed" })
            {
                using var content = new StringContent(JsonSerializer.Serialize(new { method, args = Array.Empty<object>() }), Encoding.UTF8, "application/json");
                using var response = await http.PostAsync("v1/call", content, timeout.Token);
                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
                Assert.Contains("NotFound", await response.Content.ReadAsStringAsync(timeout.Token), StringComparison.Ordinal);
            }

            var descriptorPath = Path.Combine(directory.FullName, "Snook", "daemon.endpoint.json");
            var descriptor = await File.ReadAllTextAsync(descriptorPath, timeout.Token);
            Assert.DoesNotContain(token, descriptor, StringComparison.Ordinal);
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(descriptorPath));
            await using var client = await DaemonConnection.ConnectAsync(directory.FullName, cancellationToken: timeout.Token);
            var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var changes = new ConcurrentQueue<ChangeNotification>();
            client.Changed += (_, change) =>
            {
                if (change.ChangeKind == "reconnected") connected.TrySetResult();
                if (change.AggregateType != "board" || change.ChangeKind != "created") return;
                changes.Enqueue(change);
                if (changes.Count == 24) received.TrySetResult();
            };
            await connected.Task.WaitAsync(timeout.Token);
            var boards = await Task.WhenAll(Enumerable.Range(0, 24).Select(index => client.CreateBoardAsync($"Concurrent {index}", cancellationToken: timeout.Token)));
            await received.Task.WaitAsync(timeout.Token);
            var events = changes.ToArray();
            Assert.Equal(24, events.Length);
            Assert.Equal(boards.Select(board => board.Id).Order(), events.Select(change => change.AggregateId).Order());
            Assert.Equal(events.Select(change => change.Cursor).Order(), events.Select(change => change.Cursor));
            Assert.Equal(24, events.Select(change => change.Cursor).Distinct().Count());

            foreach (var lane in new[] { SessionLane.Foreground, SessionLane.Background })
                foreach (var decision in Enum.GetValues<RecoveryDecision>())
                    await RestoredTimerTests.ExerciseAsync(client, directory.FullName, lane, decision);

            await CommittedNotificationTests.AssertContractAsync(client, Path.Combine(directory.FullName, "Snook", "workspace.db"));
            var receiptScenario = await ExactReceiptTests.ExerciseAsync(client);
            var createScenario = await CallerCreateReceiptTests.ExerciseAsync(client);
            await MaintenanceOutputTests.ExerciseAsync(client, Path.Combine(directory.FullName, "Snook", "workspace.db"), directory.FullName);
            var exportedSnapshot = await JsonExportTests.ExerciseAsync(client, Path.Combine(directory.FullName, "Snook", "workspace.db"), directory.FullName);
            var exportBackup = await client.CreateBackupAsync(Path.Combine(directory.FullName, "daemon-export-source.db"));
            await client.CreateBoardAsync("Temporary after daemon export backup");
            await client.RestoreBackupAsync(exportBackup.Path);
            Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(exportedSnapshot,
                await JsonExportTests.ExportAsync(client, Path.Combine(directory.FullName, "daemon-after-restore.json"))));
            Assert.DoesNotContain(token, await File.ReadAllTextAsync(Path.Combine(directory.FullName, "complete-export.json")), StringComparison.Ordinal);
            await RestoreVerificationTests.ExerciseAsync(client, Path.Combine(directory.FullName, "Snook", "workspace.db"), directory.FullName);

            using var stdout = new StringWriter(CultureInfo.InvariantCulture);
            using var stderr = new StringWriter(CultureInfo.InvariantCulture);
            Assert.Equal(0, await Cli.Program.RunAsync(["--host", "daemon", "--data-dir", directory.FullName,
                "doctor"], stdout, stderr, timeout.Token));
            Assert.Contains("daemon-client", stdout.ToString(), StringComparison.Ordinal);
            await ClientConnectionTests.ExerciseDaemonAsync(directory.FullName, endpoint, tokenPath);

            if (!OperatingSystem.IsWindows())
            {
                var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                client.ConnectionStateChanged += (_, _) => { if (!client.IsConnected) disconnected.TrySetResult(); };
                using var stop = Process.Start(new ProcessStartInfo("kill")
                {
                    ArgumentList = { "-TERM", process.Id.ToString(CultureInfo.InvariantCulture) },
                    UseShellExecute = false
                });
                Assert.NotNull(stop);
                await stop.WaitForExitAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token);
                await disconnected.Task.WaitAsync(timeout.Token);
                Assert.False(client.IsConnected);
                Assert.Equal(0, process.ExitCode);
                Assert.False(File.Exists(descriptorPath));
                Assert.DoesNotContain(token, await process.StandardError.ReadToEndAsync(timeout.Token), StringComparison.Ordinal);
                await using var backend = new SnookBackend(new Persistence.Sqlite.SqliteStore(Path.Combine(directory.FullName, "Snook", "workspace.db")));
                await backend.InitializeAsync(timeout.Token);
                await receiptScenario.VerifyAsync(backend);
                await createScenario.VerifyAsync(backend);
                Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(exportedSnapshot,
                    await JsonExportTests.ExportAsync(backend, Path.Combine(directory.FullName, "embedded-after-daemon.json"))));
                Assert.Equal(24, (await backend.GetBootstrapAsync(timeout.Token)).Boards.Count(board => board.Name.StartsWith("Concurrent ", StringComparison.Ordinal)));
            }
        }
        finally
        {
            if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); }
            directory.Delete(true);
        }
    }

    private sealed class HealthHandler(string contract) : HttpMessageHandler
    {
        public int RpcCalls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath != "/v1/health") RpcCalls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { ready = true, contract }), Encoding.UTF8, "application/json")
            });
        }
    }
}
