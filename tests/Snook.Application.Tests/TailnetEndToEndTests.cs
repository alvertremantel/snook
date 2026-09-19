using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Snook.Application;
using Snook.Contracts;
using Xunit;

namespace Snook.Application.Tests;

public sealed class TailnetEndToEndTests
{
    [Fact]
    public async Task OfficialPairingProfileRpcSseAndRevocationFlowThroughServeBoundary()
    {
        var hostRoot = Directory.CreateTempSubdirectory("snook-tailnet-host-");
        var clientRoot = Directory.CreateTempSubdirectory("snook-tailnet-client-");
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
        process.StartInfo.Environment["SNOOK_DATA_DIR"] = hostRoot.FullName;
        process.StartInfo.Environment["SNOOK_DAEMON_PORT"] = port.ToString(CultureInfo.InvariantCulture);
        Assert.True(process.Start());

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            Assert.NotNull(await process.StandardOutput.ReadLineAsync(timeout.Token));
            var loopback = new Uri($"http://127.0.0.1:{port}/");
            var serveOrigin = new Uri("https://snook-host.example-tailnet.ts.net/");
            var recoveryToken = await File.ReadAllTextAsync(
                Path.Combine(hostRoot.FullName, "Snook", "daemon.token"), timeout.Token);

            using var administrator = new DaemonAdministrationClient(loopback, recoveryToken);
            var invitation = await administrator.CreatePairingAsync("Travel laptop", serveOrigin,
                "user@example.com", timeout.Token);

            var pairingProxy = new LoopbackServeHandler(loopback, "user@example.com");
            await using var pending = await TailnetPairingClient.RedeemAsync(serveOrigin,
                invitation.PairingCode, "Travel laptop", pairingProxy, timeout.Token);
            Assert.Equal(serveOrigin, pending.Identity.ServeOrigin);
            Assert.Equal("user@example.com", pending.Identity.TailscaleLogin);

            var credentialStore = new OwnerOnlyFileCredentialStore(clientRoot.FullName);
            var profile = await pending.ConfirmAsync(clientRoot.FullName, credentialStore,
                "Travel laptop", timeout.Token);
            var profiles = new ClientProfileStore(clientRoot.FullName);
            var saved = await profiles.SaveAsync(profile, expectedStamp: null, timeout.Token);
            pending.Complete();

            Assert.Equal("tailscale-serve", saved.Profile?.TransportKind);
            var serializedProfile = await File.ReadAllTextAsync(profiles.ProfilePath, timeout.Token);
            Assert.DoesNotContain(recoveryToken, serializedProfile, StringComparison.Ordinal);
            Assert.DoesNotContain("credential\"", serializedProfile, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(Path.Combine(clientRoot.FullName, "Snook", "workspace.db")));

            var credential = await credentialStore.RetrieveAsync(profile.CredentialReference!, timeout.Token);
            var backendProxy = new LoopbackServeHandler(loopback, "user@example.com");
            await using var backend = new DaemonBackendClient(serveOrigin, credential, backendProxy,
                profile.ExpectedServerId, profile.ExpectedWorkspaceId);
            var firstConnection = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondConnection = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var connectionCount = 0;
            var boardChanged = new TaskCompletionSource<ChangeNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
            backend.Changed += (_, change) =>
            {
                if (change.ChangeKind == "reconnected")
                {
                    if (Interlocked.Increment(ref connectionCount) == 1) firstConnection.TrySetResult();
                    else secondConnection.TrySetResult();
                }
                if (change.AggregateType == "board" && change.ChangeKind == "created")
                    boardChanged.TrySetResult(change);
            };

            await firstConnection.Task.WaitAsync(timeout.Token);
            var bootstrap = await backend.GetBootstrapAsync(timeout.Token);
            Assert.Equal(profile.ExpectedWorkspaceId, bootstrap.Workspace.Id);
            var board = await backend.CreateBoardAsync("Through Tailscale Serve",
                cancellationToken: timeout.Token);
            var notification = await boardChanged.Task.WaitAsync(timeout.Token);
            Assert.Equal(board.Id, notification.AggregateId);
            Assert.True(backend.IsConnected);
            Assert.All(backendProxy.RequestAuthorities, authority =>
                Assert.Equal(serveOrigin.Authority, authority));

            backendProxy.DropChangeStreams();
            await secondConnection.Task.WaitAsync(timeout.Token);
            Assert.True(backend.IsConnected);
            Assert.Equal(profile.ExpectedWorkspaceId,
                (await backend.GetBootstrapAsync(timeout.Token)).Workspace.Id);

            var paired = Assert.Single(await administrator.ListDevicesAsync(timeout.Token),
                device => device.CredentialId == pending.Identity.CredentialId);
            Assert.Null(paired.PendingUntilUtc);
            Assert.Equal("user@example.com", paired.TailscaleLogin);
            await administrator.RevokeDeviceAsync(paired.CredentialId, timeout.Token);

            var revoked = await Assert.ThrowsAsync<Snook.Domain.SnookException>(
                () => backend.GetBootstrapAsync(timeout.Token));
            Assert.Equal(Snook.Domain.SnookErrorCode.StoreUnavailable, revoked.Code);
            Assert.False(File.Exists(Path.Combine(clientRoot.FullName, "Snook", "workspace.db")));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(true);
                await process.WaitForExitAsync();
            }
            hostRoot.Delete(true);
            clientRoot.Delete(true);
        }
    }

    /// <summary>
    /// Test-only stand-in for Tailscale Serve. The application still addresses the
    /// canonical HTTPS origin; this boundary forwards to the unchanged loopback
    /// daemon and supplies the identity header Serve would add.
    /// </summary>
    private sealed class LoopbackServeHandler : HttpMessageHandler
    {
        private readonly Uri _loopback;
        private readonly string _login;
        private readonly object _streamSync = new();
        private CancellationTokenSource _changeStreams = new();
        private readonly HttpMessageInvoker _inner = new(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false
        });
        public ConcurrentQueue<string> RequestAuthorities { get; } = [];

        public LoopbackServeHandler(Uri loopback, string login)
        {
            _loopback = loopback;
            _login = login;
        }

        public void DropChangeStreams()
        {
            CancellationTokenSource dropped;
            lock (_streamSync)
            {
                dropped = _changeStreams;
                _changeStreams = new CancellationTokenSource();
            }
            dropped.Cancel();
            dropped.Dispose();
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestAuthorities.Enqueue(request.RequestUri!.Authority);
            using var forwarded = new HttpRequestMessage(request.Method,
                new Uri(_loopback, request.RequestUri.PathAndQuery));
            foreach (var header in request.Headers)
            {
                if (header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase)
                    || header.Key.StartsWith("Tailscale-", StringComparison.OrdinalIgnoreCase)) continue;
                forwarded.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            forwarded.Headers.TryAddWithoutValidation("Tailscale-User-Login", _login);
            if (request.Content is not null)
            {
                forwarded.Content = new ByteArrayContent(
                    await request.Content.ReadAsByteArrayAsync(cancellationToken));
                foreach (var header in request.Content.Headers)
                    forwarded.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            var response = await _inner.SendAsync(forwarded, cancellationToken);
            if (request.RequestUri.AbsolutePath == "/v1/changes" && response.IsSuccessStatusCode)
            {
                CancellationToken streamCancellation;
                lock (_streamSync) streamCancellation = _changeStreams.Token;
                var content = response.Content;
                var stream = await content.ReadAsStreamAsync(cancellationToken);
                var wrapped = new StreamContent(new InterruptibleStream(stream, content, streamCancellation));
                foreach (var header in content.Headers)
                    wrapped.Headers.TryAddWithoutValidation(header.Key, header.Value);
                response.Content = wrapped;
            }
            return response;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                lock (_streamSync)
                {
                    _changeStreams.Cancel();
                    _changeStreams.Dispose();
                }
                _inner.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    private sealed class InterruptibleStream(Stream inner, HttpContent owner,
        CancellationToken interruption) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, interruption);
            return await inner.ReadAsync(buffer, linked.Token);
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) owner.Dispose();
            base.Dispose(disposing);
        }
        public override async ValueTask DisposeAsync()
        {
            owner.Dispose();
            await base.DisposeAsync();
        }
    }
}
