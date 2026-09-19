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
    [InlineData("http://127.0.0.2:43871/")]
    [InlineData("http://127.1:43871/")]
    public void ClientRejectsUnsafeEndpointsBeforeSendingCredentials(string endpoint)
    {
        var error = Assert.Throws<SnookException>(() => new DaemonBackendClient(new Uri(endpoint), "test-token"));
        Assert.Equal(SnookErrorCode.ValidationFailed, error.Code);
    }

    [Theory]
    [InlineData("http://localhost:43871/", DaemonEndpointKind.LocalLoopback)]
    [InlineData("http://127.0.0.1:43871/", DaemonEndpointKind.LocalLoopback)]
    [InlineData("http://[::1]:43871/", DaemonEndpointKind.LocalLoopback)]
    [InlineData("https://snook-host.example-tailnet.ts.net/", DaemonEndpointKind.TailscaleServe)]
    public void EndpointPolicyClassifiesOnlySupportedOrigins(string value, DaemonEndpointKind expected)
        => Assert.Equal(expected, DaemonEndpoint.Parse(value).Kind);

    [Theory]
    [InlineData("http://snook-host.example-tailnet.ts.net/")]
    [InlineData("https://snook-host.example-tailnet.ts.net:8443/")]
    [InlineData("https://snook-host.example-tailnet.ts.net/path")]
    [InlineData("https://127.0.0.1/")]
    [InlineData("https://example.com/")]
    [InlineData("https://*.example-tailnet.ts.net/")]
    [InlineData("https://extra.snook-host.example-tailnet.ts.net/")]
    [InlineData("https://SNook-host.example-tailnet.ts.net/")]
    [InlineData("https://xn--bcher-kva.example-tailnet.ts.net/")]
    public void EndpointPolicyRejectsRemoteOriginAmbiguity(string value)
        => Assert.Throws<SnookException>(() => DaemonEndpoint.Parse(value));

    [Fact]
    public async Task TailnetRedirectAndTlsFailuresFailClosedWithoutCredentialForwarding()
    {
        using var redirect = new RedirectHandler();
        await using (var client = new DaemonBackendClient(new Uri("https://snook-host.example-tailnet.ts.net/"), "test-token", redirect))
        {
            Assert.Equal(DaemonEndpointKind.TailscaleServe, client.EndpointKind);
            await Assert.ThrowsAsync<SnookException>(() => client.GetBootstrapAsync());
        }
        Assert.NotEmpty(redirect.Requests);
        Assert.All(redirect.Requests, request =>
        {
            Assert.Equal("snook-host.example-tailnet.ts.net", request.Host);
            Assert.True(request.HadCredential);
        });

        foreach (var (failure, expected) in new[]
        {
            (HttpRequestError.NameResolutionError, ".ts.net daemon name could not be resolved"),
            (HttpRequestError.SecureConnectionError, "TLS certificate or handshake validation failed"),
            (HttpRequestError.ConnectionError, "several of these conditions can look identical")
        })
        {
            using var failedTransport = new ThrowingHandler(failure);
            await using var unavailable = new DaemonBackendClient(new Uri("https://snook-host.example-tailnet.ts.net/"),
                "test-token", failedTransport);
            var error = await Assert.ThrowsAsync<SnookException>(() => unavailable.GetBootstrapAsync());
            Assert.Equal(SnookErrorCode.StoreUnavailable, error.Code);
            Assert.Contains(expected, error.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ExpectedServerAndWorkspaceIdentityMismatchStopsBeforeRpc()
    {
        using var handler = new IdentityHealthHandler(Guid.NewGuid(), Guid.NewGuid());
        await using var client = new DaemonBackendClient(new Uri("https://snook-host.example-tailnet.ts.net/"),
            "test-token", handler, Guid.NewGuid(), Guid.NewGuid());
        var error = await Assert.ThrowsAsync<SnookException>(() => client.GetBootstrapAsync());
        Assert.Equal(SnookErrorCode.StoreUnavailable, error.Code);
        Assert.Equal(0, handler.RpcCalls);
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

    [Theory]
    [InlineData("{\"ready\":true,\"Ready\":false,\"contract\":\"1.6\"}")]
    [InlineData("{\"ready\":true,\"contract\":\"1.6\",\"serverId\":\"11111111-1111-1111-1111-111111111111\",\"ServerId\":\"22222222-2222-2222-2222-222222222222\"}")]
    public async Task DuplicateHealthFieldsFailBeforeRpc(string json)
    {
        using var handler = new RawHealthHandler(json);
        await using var client = new DaemonBackendClient(new Uri("http://127.0.0.1:43871/"), "test-token", handler);
        var error = await Assert.ThrowsAsync<SnookException>(() => client.GetBootstrapAsync());
        Assert.Equal(SnookErrorCode.StoreUnavailable, error.Code);
        Assert.Equal(0, handler.RpcCalls);
    }

    [Fact]
    public async Task MalformedErrorEnvelopesRemainRedactedProtocolFailures()
    {
        using (var handler = new MalformedErrorHandler("{\"error\":\"not-an-object\"}"))
        using (var administrator = new DaemonAdministrationClient(
            new Uri("http://127.0.0.1:43871/"), "test-token", handler))
        {
            var error = await Assert.ThrowsAsync<SnookException>(() => administrator.ListDevicesAsync());
            Assert.Equal("Daemon administration request failed.", error.Message);
        }

        using var rpcHandler = new MalformedErrorHandler("{\"error\":{\"code\":42,\"message\":[]}}", healthy: true);
        await using var client = new DaemonBackendClient(
            new Uri("http://127.0.0.1:43871/"), "test-token", rpcHandler);
        var rpcError = await Assert.ThrowsAsync<SnookException>(() => client.GetBootstrapAsync());
        Assert.Equal(SnookErrorCode.InternalError, rpcError.Code);
        Assert.Contains("Daemon call", rpcError.Message, StringComparison.Ordinal);

        var duplicateId = Guid.NewGuid();
        using var duplicateHandler = new MalformedErrorHandler(
            $"{{\"result\":{{\"id\":\"{duplicateId}\",\"Id\":\"{Guid.NewGuid()}\"}}}}",
            healthy: true, status: HttpStatusCode.OK);
        await using var duplicateClient = new DaemonBackendClient(
            new Uri("http://127.0.0.1:43871/"), "test-token", duplicateHandler);
        var duplicateError = await Assert.ThrowsAsync<SnookException>(
            () => duplicateClient.GetBootstrapAsync());
        Assert.Equal(SnookErrorCode.StoreUnavailable, duplicateError.Code);
        Assert.Contains("invalid response", duplicateError.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ChangeStreamLinesAreBounded()
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(new string('x', 64 * 1024 + 1)));
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var error = await Assert.ThrowsAsync<SnookException>(
            () => DaemonBackendClient.ReadSseLineAsync(reader, CancellationToken.None));
        Assert.Contains("oversized", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ChangeStreamIdleReadsTimeOutButCallerCancellationIsPreserved()
    {
        await using var stream = new NeverEndingReadStream();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var timeout = await Assert.ThrowsAsync<IOException>(() =>
            DaemonBackendClient.ReadSseLineWithTimeoutAsync(reader,
                TimeSpan.FromMilliseconds(25), CancellationToken.None));
        Assert.Contains("heartbeat timed out", timeout.Message, StringComparison.OrdinalIgnoreCase);

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DaemonBackendClient.ReadSseLineWithTimeoutAsync(reader,
                TimeSpan.FromMinutes(1), cancelled.Token));
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
            using var diagnosis = JsonDocument.Parse(stderr.ToString());
            Assert.False(diagnosis.RootElement.GetProperty("ready").GetBoolean());
            Assert.Equal("credential", diagnosis.RootElement.GetProperty("error").GetProperty("stage").GetString());
            Assert.Equal("failed", diagnosis.RootElement.GetProperty("stages").GetProperty("credential").GetString());
            Assert.DoesNotContain(directory.FullName, stderr.ToString(), StringComparison.Ordinal);
            Assert.False(Directory.Exists(Path.Combine(directory.FullName, "Snook")));

            var snookRoot = Directory.CreateDirectory(Path.Combine(directory.FullName, "Snook"));
            var tokenPath = Path.Combine(snookRoot.FullName, "daemon.token");
            await File.WriteAllTextAsync(tokenPath, new string('a', 64));
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(tokenPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            var instanceId = Guid.NewGuid();
            var serverId = Guid.NewGuid();
            var workspaceId = Guid.NewGuid();
            foreach (var descriptor in new[]
            {
                JsonSerializer.Serialize(new { version = 1, instanceId,
                    endpoint = "https://snook-host.example-tailnet.ts.net/", contractMajor = 1, contractMinor = 6,
                    serverId, workspaceId }),
                JsonSerializer.Serialize(new { version = 1, instanceId,
                    endpoint = "http://127.0.0.1:43871/", contractMajor = 1, contractMinor = 6,
                    serverId, workspaceId, unsupportedFutureField = true }),
                $"{{\"version\":1,\"instanceId\":\"{instanceId}\",\"endpoint\":\"http://127.0.0.1:43871/\","
                    + $"\"Endpoint\":\"http://127.0.0.1:43872/\",\"contractMajor\":1,\"contractMinor\":6,"
                    + $"\"serverId\":\"{serverId}\",\"workspaceId\":\"{workspaceId}\"}}"
            })
            {
                var descriptorPath = Path.Combine(snookRoot.FullName, "daemon.endpoint.json");
                await File.WriteAllTextAsync(descriptorPath, descriptor);
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(descriptorPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                await Assert.ThrowsAsync<SnookException>(() => DaemonConnection.ConnectAsync(directory.FullName));
                Assert.False(File.Exists(Path.Combine(snookRoot.FullName, "workspace.db")));
            }
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
            using (var forgedIdentity = new HttpRequestMessage(HttpMethod.Get, "v1/health"))
            {
                forgedIdentity.Headers.TryAddWithoutValidation("Tailscale-User-Login", "owner@example.com");
                using var unauthorized = await http.SendAsync(forgedIdentity, timeout.Token);
                Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
            }
            http.DefaultRequestHeaders.Add("X-Snook-Token", token);
            foreach (var malformed in new[]
            {
                "{\"deviceName\":\"Strict client\",\"serveOrigin\":\"https://snook-host.example-tailnet.ts.net/\",\"unsupportedFutureField\":true}",
                "{\"pairingCode\":\"abcdefghijklmnopqrstuvwxyz123456\",\"PairingCode\":\"abcdefghijklmnopqrstuvwxyz654321\"}"
            })
            {
                var route = malformed.Contains("deviceName", StringComparison.Ordinal)
                    ? "transport/v1/admin/pairings" : "transport/v1/admin/pairings/cancel";
                using var content = new StringContent(malformed, Encoding.UTF8, "application/json");
                using var rejected = await http.PostAsync(route, content, timeout.Token);
                Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
            }
            using (var health = await http.GetAsync("v1/health", timeout.Token))
            {
                var healthJson = JsonDocument.Parse(await health.Content.ReadAsStringAsync(timeout.Token));
                Assert.NotEqual(Guid.Empty, healthJson.RootElement.GetProperty("serverId").GetGuid());
                Assert.NotEqual(Guid.Empty, healthJson.RootElement.GetProperty("workspaceId").GetGuid());
            }

            using (var createOutput = new StringWriter(CultureInfo.InvariantCulture))
            using (var createError = new StringWriter(CultureInfo.InvariantCulture))
            {
                Assert.Equal(0, await Cli.Program.RunAsync(["--no-profile", "--host", "daemon", "--data-dir", directory.FullName,
                    "pair", "create", "Cancelled laptop", "https://snook-host.example-tailnet.ts.net/"],
                    createOutput, createError, timeout.Token));
                using var createdInvitation = JsonDocument.Parse(createOutput.ToString());
                var cancelledCode = createdInvitation.RootElement.GetProperty("pairingCode").GetString()!;
                var codePath = Path.Combine(directory.FullName, "cancel-pairing-code.txt");
                await File.WriteAllTextAsync(codePath, cancelledCode, timeout.Token);
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(codePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                using var cancelOutput = new StringWriter(CultureInfo.InvariantCulture);
                using var cancelError = new StringWriter(CultureInfo.InvariantCulture);
                Assert.Equal(0, await Cli.Program.RunAsync(["--no-profile", "--host", "daemon", "--data-dir", directory.FullName,
                    "pair", "cancel", codePath], cancelOutput, cancelError, timeout.Token));
                Assert.Contains("\"cancelled\": true", cancelOutput.ToString(), StringComparison.Ordinal);
                Assert.DoesNotContain(cancelledCode, cancelOutput.ToString(), StringComparison.Ordinal);
                Assert.DoesNotContain(cancelledCode, cancelError.ToString(), StringComparison.Ordinal);
                using var cancelledClient = new HttpClient { BaseAddress = endpoint };
                using var cancelledContent = new StringContent(JsonSerializer.Serialize(new
                    { pairingCode = cancelledCode, deviceName = "Cancelled laptop" }), Encoding.UTF8, "application/json");
                using var cancelledResponse = await cancelledClient.PostAsync("transport/v1/pair", cancelledContent, timeout.Token);
                Assert.Equal(HttpStatusCode.Unauthorized, cancelledResponse.StatusCode);
            }

            using (var provisionalInvitationContent = new StringContent(JsonSerializer.Serialize(new
            {
                deviceName = "Abandoned confirmation",
                serveOrigin = "https://snook-host.example-tailnet.ts.net/"
            }), Encoding.UTF8, "application/json"))
            using (var provisionalInvitationResponse = await http.PostAsync(
                "transport/v1/admin/pairings", provisionalInvitationContent, timeout.Token))
            {
                Assert.Equal(HttpStatusCode.OK, provisionalInvitationResponse.StatusCode);
                using var provisionalInvitation = JsonDocument.Parse(
                    await provisionalInvitationResponse.Content.ReadAsStringAsync(timeout.Token));
                var provisionalCode = provisionalInvitation.RootElement.GetProperty("pairingCode").GetString();
                using var provisionalClient = new HttpClient { BaseAddress = endpoint };
                using var provisionalRedeemContent = new StringContent(JsonSerializer.Serialize(new
                {
                    pairingCode = provisionalCode,
                    deviceName = "Abandoned confirmation"
                }), Encoding.UTF8, "application/json");
                using var provisionalRedeem = await provisionalClient.PostAsync(
                    "transport/v1/pair", provisionalRedeemContent, timeout.Token);
                Assert.Equal(HttpStatusCode.OK, provisionalRedeem.StatusCode);
                using var provisionalIdentity = JsonDocument.Parse(
                    await provisionalRedeem.Content.ReadAsStringAsync(timeout.Token));
                var provisionalCredential = provisionalIdentity.RootElement.GetProperty("credential").GetString()!;
                var provisionalId = provisionalIdentity.RootElement.GetProperty("credentialId").GetGuid();
                provisionalClient.DefaultRequestHeaders.Add("X-Snook-Token", provisionalCredential);
                using var provisionalHealth = await provisionalClient.GetAsync("v1/health", timeout.Token);
                Assert.Equal(HttpStatusCode.OK, provisionalHealth.StatusCode);
                using var selfCancelContent = new StringContent(JsonSerializer.Serialize(new
                    { credentialId = provisionalId }), Encoding.UTF8, "application/json");
                using var selfCancel = await provisionalClient.PostAsync(
                    "transport/v1/pairings/cancel", selfCancelContent, timeout.Token);
                Assert.Equal(HttpStatusCode.OK, selfCancel.StatusCode);
                using var rejectedAfterCancel = await provisionalClient.GetAsync("v1/health", timeout.Token);
                Assert.Equal(HttpStatusCode.Unauthorized, rejectedAfterCancel.StatusCode);
            }

            string issuedCredential;
            Guid issuedCredentialId;
            using (var pairingContent = new StringContent(JsonSerializer.Serialize(new
            {
                deviceName = "Remote laptop",
                serveOrigin = "https://snook-host.example-tailnet.ts.net/",
                intendedTailscaleLogin = "user@example.com"
            }), Encoding.UTF8, "application/json"))
            using (var pairingResponse = await http.PostAsync("transport/v1/admin/pairings", pairingContent, timeout.Token))
            {
                Assert.Equal(HttpStatusCode.OK, pairingResponse.StatusCode);
                using var invitation = JsonDocument.Parse(await pairingResponse.Content.ReadAsStringAsync(timeout.Token));
                var code = invitation.RootElement.GetProperty("pairingCode").GetString();
                Assert.NotNull(code);
                Assert.True(code.Length >= 20);
                using var redeemClient = new HttpClient { BaseAddress = endpoint };
                using var mismatchContent = new StringContent(JsonSerializer.Serialize(new
                    { pairingCode = code, deviceName = "Wrong device" }), Encoding.UTF8, "application/json");
                using var mismatchRequest = new HttpRequestMessage(HttpMethod.Post, "transport/v1/pair")
                    { Content = mismatchContent };
                mismatchRequest.Headers.TryAddWithoutValidation("Tailscale-User-Login", "User@Example.COM");
                using var mismatchResponse = await redeemClient.SendAsync(mismatchRequest, timeout.Token);
                Assert.Equal(HttpStatusCode.Unauthorized, mismatchResponse.StatusCode);
                using var redeemContent = new StringContent(JsonSerializer.Serialize(new { pairingCode = code, deviceName = "Remote laptop" }), Encoding.UTF8, "application/json");
                using var redeemRequest = new HttpRequestMessage(HttpMethod.Post, "transport/v1/pair") { Content = redeemContent };
                redeemRequest.Headers.TryAddWithoutValidation("Tailscale-User-Login", "User@Example.COM");
                using var redeemResponse = await redeemClient.SendAsync(redeemRequest, timeout.Token);
                Assert.Equal(HttpStatusCode.OK, redeemResponse.StatusCode);
                Assert.Contains("no-store", redeemResponse.Headers.CacheControl?.ToString(),
                    StringComparison.OrdinalIgnoreCase);
                using var redeemed = JsonDocument.Parse(await redeemResponse.Content.ReadAsStringAsync(timeout.Token));
                issuedCredential = redeemed.RootElement.GetProperty("credential").GetString()!;
                issuedCredentialId = redeemed.RootElement.GetProperty("credentialId").GetGuid();
                Assert.Equal("https://snook-host.example-tailnet.ts.net/", redeemed.RootElement.GetProperty("serveOrigin").GetString());

                using var replayContent = new StringContent(JsonSerializer.Serialize(new { pairingCode = code, deviceName = "Replay" }), Encoding.UTF8, "application/json");
                using var replay = await redeemClient.PostAsync("transport/v1/pair", replayContent, timeout.Token);
                Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
            }
            var authPath = Path.Combine(directory.FullName, "Snook", "daemon-auth.json");
            Assert.DoesNotContain(issuedCredential, await File.ReadAllTextAsync(authPath, timeout.Token), StringComparison.Ordinal);
            if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(authPath));
            using (var pairedClient = new HttpClient { BaseAddress = endpoint })
            {
                pairedClient.DefaultRequestHeaders.Add("X-Snook-Token", issuedCredential);
                using var pairedHealth = await pairedClient.GetAsync("v1/health", timeout.Token);
                Assert.Equal(HttpStatusCode.OK, pairedHealth.StatusCode);
                using (var pendingDevices = await http.GetAsync("transport/v1/admin/devices", timeout.Token))
                {
                    using var pendingInventory = JsonDocument.Parse(
                        await pendingDevices.Content.ReadAsStringAsync(timeout.Token));
                    var pending = Assert.Single(pendingInventory.RootElement.EnumerateArray(),
                        item => item.GetProperty("credentialId").GetGuid() == issuedCredentialId);
                    Assert.Equal(JsonValueKind.String, pending.GetProperty("pendingUntilUtc").ValueKind);
                    Assert.Equal(JsonValueKind.Null, pending.GetProperty("lastSeenAtUtc").ValueKind);
                }
                using var callContent = new StringContent("{\"method\":\"GetBootstrapAsync\",\"args\":[]}", Encoding.UTF8, "application/json");
                using var pairedRpc = await pairedClient.PostAsync("v1/call", callContent, timeout.Token);
                Assert.Equal(HttpStatusCode.OK, pairedRpc.StatusCode);
                using var streamRequest = new HttpRequestMessage(HttpMethod.Get, "v1/changes");
                using var pairedStream = await pairedClient.SendAsync(streamRequest, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                Assert.Equal(HttpStatusCode.OK, pairedStream.StatusCode);
            }
            using (var devices = await http.GetAsync("transport/v1/admin/devices", timeout.Token))
            {
                Assert.Equal(HttpStatusCode.OK, devices.StatusCode);
                using var inventory = JsonDocument.Parse(await devices.Content.ReadAsStringAsync(timeout.Token));
                var paired = Assert.Single(inventory.RootElement.EnumerateArray(),
                    item => item.GetProperty("credentialId").GetGuid() == issuedCredentialId);
                Assert.Equal(JsonValueKind.String, paired.GetProperty("lastSeenAtUtc").ValueKind);
                Assert.Equal("user@example.com", paired.GetProperty("tailscaleLogin").GetString());
                Assert.False(paired.GetProperty("revoked").GetBoolean());
                Assert.Equal(JsonValueKind.Null, paired.GetProperty("pendingUntilUtc").ValueKind);
            }
            using var activeStreamClient = new HttpClient { BaseAddress = endpoint };
            activeStreamClient.DefaultRequestHeaders.Add("X-Snook-Token", issuedCredential);
            using var activeStreamResponse = await activeStreamClient.SendAsync(
                new HttpRequestMessage(HttpMethod.Get, "v1/changes"), HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);
            Assert.Equal(HttpStatusCode.OK, activeStreamResponse.StatusCode);
            await using var activeStream = await activeStreamResponse.Content.ReadAsStreamAsync(timeout.Token);
            using var activeReader = new StreamReader(activeStream);
            Assert.Equal(": connected", await activeReader.ReadLineAsync(timeout.Token));
            Assert.Equal(string.Empty, await activeReader.ReadLineAsync(timeout.Token));
            using (var revokeContent = new StringContent(JsonSerializer.Serialize(new { credentialId = issuedCredentialId }), Encoding.UTF8, "application/json"))
            using (var revoke = await http.PostAsync("transport/v1/admin/devices/revoke", revokeContent, timeout.Token))
                Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
            Assert.Null(await activeReader.ReadLineAsync(timeout.Token));
            string? genericUnauthorized = null;
            foreach (var rejectedCredential in new[] { issuedCredential, new string('a', 43),
                new string('a', 513), "short" })
            {
                using var rejectedClient = new HttpClient { BaseAddress = endpoint };
                rejectedClient.DefaultRequestHeaders.Add("X-Snook-Token", rejectedCredential);
                foreach (var route in new[] { "v1/health", "v1/changes" })
                {
                    using var rejected = await rejectedClient.GetAsync(route, timeout.Token);
                    Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
                    var body = await rejected.Content.ReadAsStringAsync(timeout.Token);
                    genericUnauthorized ??= body;
                    Assert.Equal(genericUnauthorized, body);
                }
                using var rejectedRpcContent = new StringContent("{}", Encoding.UTF8, "application/json");
                using var rejectedRpc = await rejectedClient.PostAsync("v1/call", rejectedRpcContent, timeout.Token);
                Assert.Equal(HttpStatusCode.Unauthorized, rejectedRpc.StatusCode);
                Assert.Equal(genericUnauthorized, await rejectedRpc.Content.ReadAsStringAsync(timeout.Token));
            }
            // Contract 1.6 appends an optional request. Older wire calls still omit it.
            using (var legacyContent = new StringContent("{\"method\":\"CreateBoardAsync\",\"args\":[\"Legacy omitted request\"]}", Encoding.UTF8, "application/json"))
            using (var legacyResponse = await http.PostAsync("v1/call", legacyContent, timeout.Token))
                Assert.Equal(HttpStatusCode.OK, legacyResponse.StatusCode);
            foreach (var body in new[] { "{", "null", "[]", "{}",
                "{\"method\":\"GetBootstrapAsync\",\"Method\":\"GetBootstrapAsync\",\"args\":[]}",
                "{\"method\":\"GetBootstrapAsync\",\"args\":[],\"future\":true}",
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

            var originalWorkspaceId = (await client.GetBootstrapAsync(timeout.Token)).Workspace.Id;
            using var identityHealthResponse = await http.GetAsync("v1/health", timeout.Token);
            using var identityHealth = JsonDocument.Parse(
                await identityHealthResponse.Content.ReadAsStringAsync(timeout.Token));
            var serverId = identityHealth.RootElement.GetProperty("serverId").GetGuid();
            var foreignDirectory = Directory.CreateDirectory(Path.Combine(directory.FullName, "foreign-workspace"));
            await using (var foreign = new Snook.Application.SnookBackend(
                new Persistence.Sqlite.SqliteStore(Path.Combine(foreignDirectory.FullName, "workspace.db"))))
            {
                await foreign.InitializeAsync(timeout.Token);
                var foreignWorkspaceId = (await foreign.GetBootstrapAsync(timeout.Token)).Workspace.Id;
                Assert.NotEqual(originalWorkspaceId, foreignWorkspaceId);
                var foreignBackup = await foreign.CreateBackupAsync(
                    Path.Combine(directory.FullName, "foreign-workspace-backup.db"), timeout.Token);
                await using var pinned = new DaemonBackendClient(endpoint, token,
                    expectedServerId: serverId, expectedWorkspaceId: originalWorkspaceId);
                Assert.Equal(originalWorkspaceId, (await pinned.GetBootstrapAsync(timeout.Token)).Workspace.Id);

                await client.RestoreBackupAsync(foreignBackup.Path, timeout.Token);
                using (var changedHealth = await http.GetAsync("v1/health", timeout.Token))
                {
                    using var changedIdentity = JsonDocument.Parse(
                        await changedHealth.Content.ReadAsStringAsync(timeout.Token));
                    Assert.Equal(foreignWorkspaceId,
                        changedIdentity.RootElement.GetProperty("workspaceId").GetGuid());
                }
                var mismatch = await Assert.ThrowsAsync<SnookException>(
                    () => pinned.GetBootstrapAsync(timeout.Token));
                Assert.Contains("identity", mismatch.Message, StringComparison.OrdinalIgnoreCase);

                await client.RestoreBackupAsync(exportBackup.Path, timeout.Token);
                Assert.Equal(originalWorkspaceId, (await client.GetBootstrapAsync(timeout.Token)).Workspace.Id);
            }

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

    private sealed class RawHealthHandler(string json) : HttpMessageHandler
    {
        private int _rpcCalls;
        public int RpcCalls => Volatile.Read(ref _rpcCalls);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath != "/v1/health") Interlocked.Increment(ref _rpcCalls);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class RedirectHandler : HttpMessageHandler
    {
        public List<(string Host, bool HadCredential)> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri!.Host, request.Headers.Contains("X-Snook-Token")));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect)
            {
                Headers = { Location = new Uri("https://attacker.example/") }
            });
        }
    }

    private sealed class ThrowingHandler(HttpRequestError error) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException(error, "simulated tailnet transport failure");
    }

    private sealed class IdentityHealthHandler(Guid serverId, Guid workspaceId) : HttpMessageHandler
    {
        public int RpcCalls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath != "/v1/health") RpcCalls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    ready = true,
                    contract = $"{ContractInfo.Major}.{ContractInfo.Minor}",
                    serverId,
                    workspaceId
                }), Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class MalformedErrorHandler(string json, bool healthy = false,
        HttpStatusCode status = HttpStatusCode.BadRequest) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (healthy && request.RequestUri?.AbsolutePath == "/v1/health")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        ready = true,
                        contract = $"{ContractInfo.Major}.{ContractInfo.Minor}"
                    }), Encoding.UTF8, "application/json")
                });
            }
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class NeverEndingReadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }
}
