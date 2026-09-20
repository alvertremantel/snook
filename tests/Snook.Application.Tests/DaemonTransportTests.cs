using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
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
    [InlineData("https://snook-host.tail123.ts.net/")]
    [InlineData("https://snook-service.ts.net:443/")]
    public void TailnetClientAcceptsOnlyCanonicalServeOrigins(string endpoint)
    {
        var result = DaemonBackendClient.ValidateEndpoint(endpoint, DaemonEndpointKind.TailnetServe);
        Assert.Equal(new Uri(endpoint), result);
    }

    [Theory]
    [InlineData("http://snook-host.tail123.ts.net/")]
    [InlineData("https://snook-host.tail123.ts.net:8443/")]
    [InlineData("https://ts.net/")]
    [InlineData("https://snook-host.tail123.ts.net.example/")]
    [InlineData("https://127.0.0.1/")]
    [InlineData("https://user:secret@snook-host.tail123.ts.net/")]
    [InlineData("https://snook-host.tail123.ts.net/path")]
    [InlineData("https://snook-host.tail123.ts.net/?target=other")]
    [InlineData("https://snook-host.tail123.ts.net/#fragment")]
    public void TailnetClientRejectsNonServeOrMutableOrigins(string endpoint)
    {
        var error = Assert.Throws<SnookException>(() =>
            DaemonBackendClient.ValidateEndpoint(endpoint, DaemonEndpointKind.TailnetServe));
        Assert.Equal(SnookErrorCode.ValidationFailed, error.Code);
        Assert.DoesNotContain("secret", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TailnetClientUsesDeviceCredentialHeaderInsteadOfAdministratorToken()
    {
        using var handler = new CredentialHeaderHandler();
        await using var client = new DaemonBackendClient(
            new Uri("https://snook-host.tail123.ts.net/"), new string('A', 64),
            DaemonEndpointKind.TailnetServe, handler);
        await Assert.ThrowsAsync<SnookException>(() => client.GetBootstrapAsync());
        Assert.NotEmpty(handler.Requests);
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal(new string('A', 64), Assert.Single(request.DeviceTokens));
            Assert.Empty(request.AdminTokens);
            Assert.Equal("snook-host.tail123.ts.net", request.Host);
        });
    }

    [Theory]
    [InlineData("99.0")]
    [InlineData("1.0")]
    [InlineData("1.5")]
    [InlineData("1.6")]
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

    [Fact]
    public async Task TailnetPairingBindsIdentitySurvivesRestartAndRevocationClosesStream()
    {
        var directory = Directory.CreateTempSubdirectory("snook-tailnet-pairing-");
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        Process? process = null;
        try
        {
            async Task<(Process Process, JsonElement Ready)> StartAsync()
            {
                var started = new Process
                {
                    StartInfo = new ProcessStartInfo("dotnet")
                    {
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    }
                };
                started.StartInfo.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "snookd.dll"));
                started.StartInfo.Environment["SNOOK_DATA_DIR"] = directory.FullName;
                started.StartInfo.Environment["SNOOK_DAEMON_PORT"] = port.ToString(CultureInfo.InvariantCulture);
                started.StartInfo.Environment["SNOOK_TRUST_TAILSCALE_SERVE_HEADERS"] = "1";
                Assert.True(started.Start());
                var line = await started.StandardOutput.ReadLineAsync(timeout.Token);
                Assert.NotNull(line);
                using var document = JsonDocument.Parse(line);
                Assert.True(document.RootElement.GetProperty("ready").GetBoolean());
                Assert.True(document.RootElement.GetProperty("tailnetPairingEnabled").GetBoolean());
                return (started, document.RootElement.Clone());
            }

            async Task StopAsync(Process running)
            {
                if (!OperatingSystem.IsWindows())
                {
                    using var stop = Process.Start(new ProcessStartInfo("kill")
                    {
                        ArgumentList = { "-TERM", running.Id.ToString(CultureInfo.InvariantCulture) },
                        UseShellExecute = false
                    });
                    Assert.NotNull(stop);
                    await stop.WaitForExitAsync(timeout.Token);
                }
                else running.Kill(entireProcessTree: true);
                await running.WaitForExitAsync(timeout.Token);
                if (!OperatingSystem.IsWindows()) Assert.Equal(0, running.ExitCode);
            }

            static StringContent Body(object value) => new(JsonSerializer.Serialize(value),
                Encoding.UTF8, "application/json");

            var first = await StartAsync();
            process = first.Process;
            var instanceId = first.Ready.GetProperty("instanceId").GetGuid();
            var endpoint = new Uri($"http://127.0.0.1:{port}/");
            var tokenPath = Path.Combine(directory.FullName, "Snook", "daemon.token");
            var adminToken = await File.ReadAllTextAsync(tokenPath, timeout.Token);
            using var admin = new HttpClient { BaseAddress = endpoint };
            using (var unauthorizedBody = Body(new { serveOrigin = "https://snook-host.tail123.ts.net/" }))
            using (var unauthorized = await admin.PostAsync("v1/pairing/challenges", unauthorizedBody, timeout.Token))
                Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
            admin.DefaultRequestHeaders.Add("X-Snook-Token", adminToken);

            string challengeCode;
            Guid workspaceId;
            using (var challengeBody = Body(new { serveOrigin = "https://snook-host.tail123.ts.net/" }))
            using (var challengeResponse = await admin.PostAsync("v1/pairing/challenges", challengeBody, timeout.Token))
            {
                Assert.Equal(HttpStatusCode.OK, challengeResponse.StatusCode);
                using var challenge = JsonDocument.Parse(await challengeResponse.Content.ReadAsStringAsync(timeout.Token));
                challengeCode = challenge.RootElement.GetProperty("code").GetString()!;
                workspaceId = challenge.RootElement.GetProperty("workspaceId").GetGuid();
                Assert.Equal(instanceId, challenge.RootElement.GetProperty("instanceId").GetGuid());
            }

            var installationId = Guid.NewGuid();
            using var unauthenticatedPairing = new HttpClient { BaseAddress = endpoint };
            using (var missingIdentityBody = Body(new { code = challengeCode, installationId, deviceName = "Android phone" }))
            using (var missingIdentity = await unauthenticatedPairing.PostAsync(
                       "v1/pairing/begin", missingIdentityBody, timeout.Token))
                Assert.Equal(HttpStatusCode.Unauthorized, missingIdentity.StatusCode);

            Guid enrollmentId;
            using var pairing = new HttpClient { BaseAddress = endpoint };
            pairing.DefaultRequestHeaders.Add("Tailscale-User-Login", "jones@example.com");
            using (var beginBody = Body(new { code = challengeCode, installationId, deviceName = "Android phone" }))
            using (var beginResponse = await pairing.PostAsync("v1/pairing/begin", beginBody, timeout.Token))
            {
                Assert.Equal(HttpStatusCode.OK, beginResponse.StatusCode);
                using var preview = JsonDocument.Parse(await beginResponse.Content.ReadAsStringAsync(timeout.Token));
                enrollmentId = preview.RootElement.GetProperty("enrollmentId").GetGuid();
                Assert.Equal("jones@example.com", preview.RootElement.GetProperty("tailnetLogin").GetString());
                Assert.Equal(instanceId, preview.RootElement.GetProperty("instanceId").GetGuid());
                Assert.Equal(workspaceId, preview.RootElement.GetProperty("workspaceId").GetGuid());
            }

            using var wrongIdentity = new HttpClient { BaseAddress = endpoint };
            wrongIdentity.DefaultRequestHeaders.Add("Tailscale-User-Login", "attacker@example.com");
            using (var wrongBody = Body(new { enrollmentId }))
            using (var wrongResponse = await wrongIdentity.PostAsync("v1/pairing/complete", wrongBody, timeout.Token))
                Assert.Equal(HttpStatusCode.BadRequest, wrongResponse.StatusCode);

            string credential;
            Guid credentialId;
            using (var completeBody = Body(new { enrollmentId }))
            using (var completeResponse = await pairing.PostAsync("v1/pairing/complete", completeBody, timeout.Token))
            {
                Assert.Equal(HttpStatusCode.OK, completeResponse.StatusCode);
                using var completed = JsonDocument.Parse(await completeResponse.Content.ReadAsStringAsync(timeout.Token));
                credential = completed.RootElement.GetProperty("credential").GetString()!;
                credentialId = completed.RootElement.GetProperty("credentialId").GetGuid();
            }
            var registryPath = Path.Combine(directory.FullName, "Snook", "android-pairings.json");
            var registryText = await File.ReadAllTextAsync(registryPath, timeout.Token);
            Assert.DoesNotContain(credential, registryText, StringComparison.Ordinal);
            Assert.DoesNotContain(challengeCode, registryText, StringComparison.Ordinal);
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(registryPath));

            using (var managementHandler = new ServeLoopbackHandler(endpoint, "jones@example.com"))
            await using (var management = new DaemonBackendClient(
                new Uri("https://snook-host.tail123.ts.net/"), credential,
                DaemonEndpointKind.TailnetServe, managementHandler))
            {
                var renamed = await management.RenameCurrentPairedDeviceAsync(
                    "Daily phone", timeout.Token);
                Assert.Equal(credentialId, renamed.CredentialId);
                Assert.Equal("Daily phone", renamed.DeviceName);
            }
            using (var listedAfterRename = await admin.GetAsync("v1/pairing/devices", timeout.Token))
            {
                Assert.Equal(HttpStatusCode.OK, listedAfterRename.StatusCode);
                Assert.Contains("Daily phone",
                    await listedAfterRename.Content.ReadAsStringAsync(timeout.Token),
                    StringComparison.Ordinal);
            }

            using (var device = DeviceClient(endpoint, credential, "jones@example.com"))
            {
                using (var health = await device.GetAsync("v1/health", timeout.Token))
                    Assert.Equal(HttpStatusCode.OK, health.StatusCode);
                using var bootstrapBody = Body(new { method = "GetBootstrapAsync", args = Array.Empty<object>() });
                using var bootstrapResponse = await device.PostAsync("v1/call", bootstrapBody, timeout.Token);
                Assert.Equal(HttpStatusCode.OK, bootstrapResponse.StatusCode);
            }
            using (var serve = new ServeLoopbackHandler(endpoint, "jones@example.com"))
            await using (var attachedSession = new WorkspaceSession(_ =>
                throw new InvalidOperationException("Independent fallback must not run.")))
            {
                var tailnetClient = await attachedSession.OpenAttachedAsync(_ =>
                    Task.FromResult<IBackendClient>(new DaemonBackendClient(
                        new Uri("https://snook-host.tail123.ts.net/"), credential,
                        DaemonEndpointKind.TailnetServe, serve)), timeout.Token);
                var tailnetBootstrap = await tailnetClient.GetBootstrapAsync(timeout.Token);
                Assert.True(tailnetBootstrap.Capabilities.DaemonClient);
                var connection = Assert.IsAssignableFrom<IBackendConnectionState>(tailnetClient);
                Assert.Equal("Paired daemon · snook-host.tail123.ts.net", connection.ConnectionLabel);
                await TaskBatchTests.AssertBatchContractAsync(tailnetClient);
                await HabitTests.AssertHabitContractAsync(tailnetClient);
                await JournalTests.AssertJournalContractAsync(tailnetClient);
                var transfer = Assert.IsAssignableFrom<IBackendDocumentTransfer>(tailnetClient);
                Assert.True(transfer.CanDownloadDocuments);
                await using var backup = new MemoryStream();
                Assert.Equal(await transfer.DownloadBackupAsync(backup, timeout.Token), backup.Length);
                backup.Position = 0;
                using (var archive = new ZipArchive(backup, ZipArchiveMode.Read, leaveOpen: true))
                    Assert.Equal([WorkspaceBackupBundle.DatabaseEntry, WorkspaceBackupBundle.ManifestEntry],
                        archive.Entries.Select(entry => entry.FullName).OrderBy(name => name, StringComparer.Ordinal));
                await using var json = new MemoryStream();
                Assert.Equal(await transfer.DownloadJsonAsync(json, timeout.Token), json.Length);
                using (var exported = JsonDocument.Parse(json.ToArray()))
                    Assert.True(exported.RootElement.TryGetProperty("workspace", out _));
                await using var csv = new MemoryStream();
                Assert.Equal(await transfer.DownloadCsvAsync(csv,
                    DateTimeOffset.UtcNow.AddDays(-30), DateTimeOffset.UtcNow.AddDays(1), timeout.Token), csv.Length);
                Assert.Contains("session_id", Encoding.UTF8.GetString(csv.ToArray()), StringComparison.Ordinal);
            }
            var incomingRoot = Path.Combine(directory.FullName, "incoming-workspace");
            Directory.CreateDirectory(incomingRoot);
            Guid restoredWorkspaceId;
            string bundlePath;
            await using (var incomingBackend = new SnookBackend(new Persistence.Sqlite.SqliteStore(
                Path.Combine(incomingRoot, "workspace.db"))))
            {
                await incomingBackend.InitializeAsync(timeout.Token);
                restoredWorkspaceId = (await incomingBackend.GetBootstrapAsync(timeout.Token)).Workspace.Id;
                await incomingBackend.CreateBoardAsync("Restored through attached upload",
                    cancellationToken: timeout.Token);
                var bundleRoot = Path.Combine(incomingRoot, "bundle");
                Directory.CreateDirectory(bundleRoot);
                bundlePath = await WorkspaceBackupBundle.CreateAsync(incomingBackend,
                    bundleRoot, timeout.Token);
            }
            Assert.NotEqual(workspaceId, restoredWorkspaceId);
            using (var restoreHandler = new ServeLoopbackHandler(endpoint, "jones@example.com"))
            await using (var restoreClient = new DaemonBackendClient(
                new Uri("https://snook-host.tail123.ts.net/"), credential,
                DaemonEndpointKind.TailnetServe, restoreHandler))
            await using (var bundle = File.OpenRead(bundlePath))
            {
                var cancelledStage = await restoreClient.StageRestoreBackupAsync(bundle, timeout.Token);
                await restoreClient.DiscardStagedBackupAsync(cancelledStage.UploadId, timeout.Token);
                await Assert.ThrowsAsync<SnookException>(() =>
                    restoreClient.RestoreStagedBackupAsync(cancelledStage, timeout.Token));

                bundle.Position = 0;
                var staleStage = await restoreClient.StageRestoreBackupAsync(bundle, timeout.Token);
                _ = await restoreClient.CreateBoardAsync("Mutation after restore review",
                    cancellationToken: timeout.Token);
                await Assert.ThrowsAsync<SnookException>(() =>
                    restoreClient.RestoreStagedBackupAsync(staleStage, timeout.Token));
                await restoreClient.DiscardStagedBackupAsync(staleStage.UploadId, timeout.Token);

                bundle.Position = 0;
                var stage = await restoreClient.StageRestoreBackupAsync(bundle, timeout.Token);
                Assert.Equal(workspaceId, stage.CurrentWorkspaceId);
                Assert.Equal(restoredWorkspaceId, stage.Incoming.Workspace.Id);
                Assert.Equal(workspaceId,
                    (await restoreClient.GetBootstrapAsync(timeout.Token)).Workspace.Id);
                var restored = await restoreClient.RestoreStagedBackupAsync(stage, timeout.Token);
                Assert.Equal(restoredWorkspaceId, restored.WorkspaceId);
                Assert.Equal(0, restored.RevokedOtherDeviceCount);
                Assert.Equal(string.Empty, restored.Restore.SourcePath);
                Assert.Contains("Restored through attached upload",
                    (await restoreClient.GetBootstrapAsync(timeout.Token)).Boards.Select(item => item.Name));
            }
            Assert.Empty(Directory.EnumerateDirectories(Path.Combine(directory.FullName, "Snook"), ".transfer-*"));
            Assert.Empty(Directory.EnumerateDirectories(Path.Combine(directory.FullName, "Snook"), ".restore-upload-*"));
            using (var impostor = DeviceClient(endpoint, credential, "other@example.com"))
            {
                using (var health = await impostor.GetAsync("v1/health", timeout.Token))
                    Assert.Equal(HttpStatusCode.Unauthorized, health.StatusCode);
                using var renameBody = Body(new { deviceName = "Attacker rename" });
                using var rename = await impostor.PostAsync("v1/pairing/self/rename",
                    renameBody, timeout.Token);
                Assert.Equal(HttpStatusCode.Unauthorized, rename.StatusCode);
            }

            await StopAsync(process);
            process.Dispose();
            process = null;
            var second = await StartAsync();
            process = second.Process;
            Assert.Equal(instanceId, second.Ready.GetProperty("instanceId").GetGuid());
            using var restartedDevice = DeviceClient(endpoint, credential, "jones@example.com");
            using (var health = await restartedDevice.GetAsync("v1/health", timeout.Token))
                Assert.Equal(HttpStatusCode.OK, health.StatusCode);

            using var streamRequest = new HttpRequestMessage(HttpMethod.Get, "v1/changes");
            using var streamResponse = await restartedDevice.SendAsync(streamRequest,
                HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            Assert.Equal(HttpStatusCode.OK, streamResponse.StatusCode);
            await using var stream = await streamResponse.Content.ReadAsStreamAsync(timeout.Token);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            Assert.Equal(": connected", await reader.ReadLineAsync(timeout.Token));
            Assert.Equal(string.Empty, await reader.ReadLineAsync(timeout.Token));

            using (var list = await admin.GetAsync("v1/pairing/devices", timeout.Token))
            {
                Assert.Equal(HttpStatusCode.OK, list.StatusCode);
                var listed = await list.Content.ReadAsStringAsync(timeout.Token);
                Assert.Contains(credentialId.ToString(), listed, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(credential, listed, StringComparison.Ordinal);
                Assert.DoesNotContain(challengeCode, listed, StringComparison.Ordinal);
            }
            using (var revokeHandler = new ServeLoopbackHandler(endpoint, "jones@example.com"))
            await using (var revoker = new DaemonBackendClient(
                new Uri("https://snook-host.tail123.ts.net/"), credential,
                DaemonEndpointKind.TailnetServe, revokeHandler))
            {
                var revoked = await revoker.RevokeCurrentPairedDeviceAsync(timeout.Token);
                Assert.Equal(credentialId, revoked.CredentialId);
                Assert.True(revoked.Revoked);
            }
            using (var closeBudget = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                Assert.Null(await reader.ReadLineAsync(closeBudget.Token));
            using (var health = await restartedDevice.GetAsync("v1/health", timeout.Token))
                Assert.Equal(HttpStatusCode.Unauthorized, health.StatusCode);
        }
        finally
        {
            if (process is not null)
            {
                if (!process.HasExited) process.Kill(true);
                await process.WaitForExitAsync();
                process.Dispose();
            }
            directory.Delete(true);
        }

        static HttpClient DeviceClient(Uri endpoint, string credential, string identity)
        {
            var client = new HttpClient { BaseAddress = endpoint };
            client.DefaultRequestHeaders.Add("X-Snook-Device-Token", credential);
            client.DefaultRequestHeaders.Add("Tailscale-User-Login", identity);
            return client;
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

    private sealed class ServeLoopbackHandler : DelegatingHandler
    {
        private readonly Uri _loopback;
        private readonly string _identity;

        internal ServeLoopbackHandler(Uri loopback, string identity)
            : base(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false })
        {
            _loopback = loopback;
            _identity = identity;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var incoming = request.RequestUri
                ?? throw new InvalidOperationException("Tailnet client sent no request URI.");
            request.RequestUri = new Uri(_loopback, incoming.PathAndQuery);
            request.Headers.Remove("Tailscale-User-Login");
            request.Headers.Add("Tailscale-User-Login", _identity);
            return base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class CredentialHeaderHandler : HttpMessageHandler
    {
        private readonly ConcurrentQueue<CapturedRequest> _requests = new();
        public IReadOnlyList<CapturedRequest> Requests => _requests.ToArray();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _requests.Enqueue(new(request.RequestUri?.Host ?? string.Empty,
                request.Headers.GetValues("X-Snook-Device-Token").ToArray(),
                request.Headers.TryGetValues("X-Snook-Token", out var admin)
                    ? admin.ToArray() : []));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                    { ready = true, contract = "99.0" }), Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed record CapturedRequest(string Host, string[] DeviceTokens, string[] AdminTokens);
}
