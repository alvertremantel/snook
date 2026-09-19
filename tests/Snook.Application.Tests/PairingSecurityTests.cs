using Snook.Daemon;
using Xunit;

namespace Snook.Application.Tests;

public sealed class PairingSecurityTests
{
    private static readonly string[] WorkspaceScopes = ["workspace:full"];

    [Fact]
    public void PairingExpiresUsesVerifierAndRedeemsExactlyOnceUnderRace()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero));
        var random = new PairingRandom(Enumerable.Range(0, 16).Select(index => (byte)index).ToArray());
        var sessions = new PairingSessions(clock, random);
        var invitation = sessions.Arm("Laptop", new Uri("https://snook-host.example-tailnet.ts.net/"), null);
        Assert.Equal("AAECAwQFBgcICQoLDA0ODw", invitation.PairingCode);
        var results = Enumerable.Range(0, 32).AsParallel()
            .Select(index => sessions.Redeem(invitation.PairingCode, $"source-{index}", null, "Laptop")).ToArray();
        Assert.Single(results, result => result is not null);
        Assert.Null(sessions.Redeem(invitation.PairingCode, "later", null, "Laptop"));

        var expiring = sessions.Arm("Tablet", new Uri("https://snook-host.example-tailnet.ts.net/"), null);
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Null(sessions.Redeem(expiring.PairingCode, "source", null, "Tablet"));

        var cancelled = sessions.Arm("Desktop", new Uri("https://snook-host.example-tailnet.ts.net/"), null);
        Assert.True(sessions.Cancel(cancelled.PairingCode));
        Assert.False(sessions.Cancel(cancelled.PairingCode));
        Assert.Null(sessions.Redeem(cancelled.PairingCode, "source", null, "Desktop"));
    }

    [Fact]
    public void IntendedLoginIsNormalizedRequiredAndRateLimitedWithoutLeakingState()
    {
        var sessions = new PairingSessions(new ManualTimeProvider(DateTimeOffset.UtcNow));
        var invitation = sessions.Arm("Laptop", new Uri("https://snook-host.example-tailnet.ts.net/"), " User@Example.COM ");
        Assert.Throws<Snook.Domain.SnookException>(() => sessions.Arm("Bad login", new Uri("https://snook-host.example-tailnet.ts.net/"), "two users"));
        Assert.Null(PairingSessions.NormalizeLogin(new string('x', 255)));
        Assert.Null(sessions.Redeem(invitation.PairingCode, "source", null, "Laptop"));
        Assert.Null(sessions.Redeem(invitation.PairingCode, "source", "user@example.com", "Wrong device"));
        Assert.Null(sessions.Redeem(invitation.PairingCode, "source", "user@example.com", new string('x', 101)));
        var redeemed = sessions.Redeem(invitation.PairingCode, "source", "user@example.com", " Laptop ");
        Assert.NotNull(redeemed);
        Assert.Equal("user@example.com", redeemed.IntendedLogin);

        var limited = sessions.Arm("Phone", new Uri("https://snook-host.example-tailnet.ts.net/"), null);
        for (var attempt = 0; attempt < 8; attempt++)
            Assert.Null(sessions.Redeem("aaaaaaaaaaaaaaaaaaaaaa", "same-source", null, "Phone"));
        Assert.Null(sessions.Redeem(limited.PairingCode, "same-source", null, "Phone"));
    }

    [Fact]
    public void GlobalAttemptBudgetCannotBeEvadedByCyclingSourceSignals()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero));
        var sessions = new PairingSessions(clock);
        var invitation = sessions.Arm("Laptop", new Uri("https://snook-host.example-tailnet.ts.net/"), null);
        for (var attempt = 0; attempt < 128; attempt++)
            Assert.Null(sessions.Redeem("aaaaaaaaaaaaaaaaaaaaaa", $"forged-source-{attempt}", null, "Laptop"));
        Assert.Null(sessions.Redeem(invitation.PairingCode, "new-source", null, "Laptop"));

        clock.Advance(TimeSpan.FromMinutes(5));
        var afterWindow = sessions.Arm("Tablet", new Uri("https://snook-host.example-tailnet.ts.net/"), null);
        Assert.NotNull(sessions.Redeem(afterWindow.PairingCode, "new-source", null, "Tablet"));
    }

    [Fact]
    public void PairingSessionCountIsBoundedAndExpiredSessionsFreeCapacity()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero));
        var sessions = new PairingSessions(clock, new PairingRandom(new byte[16]));
        for (var index = 0; index < 16; index++)
            _ = sessions.Arm($"Device {index}", new Uri("https://snook-host.example-tailnet.ts.net/"), null);
        Assert.Throws<Snook.Domain.SnookException>(() => sessions.Arm(
            "Too many", new Uri("https://snook-host.example-tailnet.ts.net/"), null));
        clock.Advance(TimeSpan.FromMinutes(5));
        _ = sessions.Arm("After expiry", new Uri("https://snook-host.example-tailnet.ts.net/"), null);
    }

    [Fact]
    public async Task FailedProfileValidationDeletesJustStoredCredential()
    {
        var store = new TrackingCredentialStore();
        var identity = new PairingIdentity(new Uri("https://snook-host.example-tailnet.ts.net/"), Guid.NewGuid(), Guid.NewGuid(),
            "Workspace", new Version(1, 6), Guid.NewGuid(), "Laptop", null);
        using var client = new HttpClient(new PairingConfirmationHandler(identity, validHealth: true));
        await using var pending = new PendingDaemonPairing(client,
            identity,
            "abcdefghijklmnopqrstuvwxyzABCDEFGH12345678_-");

        await Assert.ThrowsAsync<Snook.Domain.SnookException>(() => pending.ConfirmAsync("relative", store));
        Assert.Equal(store.StoredReference, store.DeletedReference);

        var untouched = new TrackingCredentialStore();
        using var invalidClient = new HttpClient(new PairingConfirmationHandler(identity, validHealth: false));
        await using var invalid = new PendingDaemonPairing(invalidClient, identity,
            "abcdefghijklmnopqrstuvwxyzABCDEFGH12345678_-");
        await Assert.ThrowsAsync<Snook.Domain.SnookException>(() => invalid.ConfirmAsync(Path.GetTempPath(), untouched));
        Assert.False(untouched.Stored);
    }

    [Fact]
    public async Task PairingClientRejectsMalformedIdentityContractAndCredentialResponses()
    {
        var origin = new Uri("https://snook-host.example-tailnet.ts.net/");
        var mutations = new Action<System.Text.Json.Nodes.JsonObject>[]
        {
            json => json["serveOrigin"] = "https://other.example-tailnet.ts.net/",
            json => json["serverId"] = Guid.Empty,
            json => json["contract"] = "1.5",
            json => json["deviceName"] = "Another device",
            json => json["tailscaleLogin"] = "User@Example.COM",
            json => json["unsupportedFutureField"] = true,
            json => json["credential"] = "not-valid"
        };
        foreach (var mutate in mutations)
        {
            var json = ValidPairingResponse();
            mutate(json);
            await Assert.ThrowsAsync<Snook.Domain.SnookException>(() => TailnetPairingClient.RedeemAsync(origin,
                "abcdefghijklmnopqrstuvwxyz123456", "Laptop", new JsonHandler(json.ToJsonString())));
        }

        var unused = new JsonHandler(ValidPairingResponse().ToJsonString());
        await Assert.ThrowsAsync<Snook.Domain.SnookException>(() => TailnetPairingClient.RedeemAsync(
            origin, "short", "Laptop", unused));
        Assert.Equal(0, unused.Calls);

        var raw = ValidPairingResponse().ToJsonString();
        var duplicate = $"{{\"serverId\":\"{Guid.NewGuid()}\",{raw[1..]}";
        await Assert.ThrowsAsync<Snook.Domain.SnookException>(() => TailnetPairingClient.RedeemAsync(origin,
            "abcdefghijklmnopqrstuvwxyz123456", "Laptop", new JsonHandler(duplicate)));
        var casingDuplicate = $"{{\"ServerId\":\"{Guid.NewGuid()}\",{raw[1..]}";
        await Assert.ThrowsAsync<Snook.Domain.SnookException>(() => TailnetPairingClient.RedeemAsync(origin,
            "abcdefghijklmnopqrstuvwxyz123456", "Laptop", new JsonHandler(casingDuplicate)));

        var tlsFailure = await Assert.ThrowsAsync<Snook.Domain.SnookException>(() => TailnetPairingClient.RedeemAsync(origin,
            "abcdefghijklmnopqrstuvwxyz123456", "Laptop", new FailingTransportHandler()));
        Assert.Contains("TLS certificate or handshake validation failed", tlsFailure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AdministrationClientRejectsMalformedOrFutureDeviceInventory()
    {
        var id = Guid.NewGuid();
        object Device(Guid credentialId, DateTimeOffset createdAtUtc) => new
        {
            credentialId,
            displayName = "Laptop",
            createdAtUtc,
            lastSeenAtUtc = (DateTimeOffset?)null,
            tailscaleLogin = "user@example.com",
            scopes = WorkspaceScopes,
            revoked = false
        };
        var utc = new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);
        var invalid = new[]
        {
            System.Text.Json.JsonSerializer.Serialize(new[] { Device(id, utc), Device(id, utc) }),
            System.Text.Json.JsonSerializer.Serialize(new[] { Device(id, utc.ToOffset(TimeSpan.FromHours(1))) }),
            System.Text.Json.JsonSerializer.Serialize(new[] { new
            {
                credentialId = id,
                displayName = "Laptop",
                createdAtUtc = utc,
                lastSeenAtUtc = (DateTimeOffset?)null,
                tailscaleLogin = "User@Example.COM",
                scopes = WorkspaceScopes,
                revoked = false,
                unsupportedFutureField = true
            } })
        };
        foreach (var json in invalid)
        {
            using var administrator = new DaemonAdministrationClient(new Uri("http://127.0.0.1:43871/"),
                "abcdefghijklmnopqrstuvwxyz123456", new JsonHandler(json));
            await Assert.ThrowsAsync<Snook.Domain.SnookException>(() => administrator.ListDevicesAsync());
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }

    private sealed class TrackingCredentialStore : IClientCredentialStore
    {
        public string StoredReference { get; } = "owner-file:" + Guid.NewGuid().ToString("N");
        public string? DeletedReference { get; private set; }
        public bool Stored { get; private set; }
        public Task<string> StoreAsync(string credential, string label, CancellationToken cancellationToken = default)
        {
            Stored = true;
            return Task.FromResult(StoredReference);
        }
        public Task<string> RetrieveAsync(string reference, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task DeleteAsync(string reference, CancellationToken cancellationToken = default)
        {
            DeletedReference = reference;
            return Task.CompletedTask;
        }
    }

    private sealed class PairingConfirmationHandler(PairingIdentity identity, bool validHealth) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(request.RequestUri?.AbsolutePath == "/v1/health"
                    ? System.Text.Json.JsonSerializer.Serialize(new
                    {
                        ready = true,
                        contract = identity.Contract.ToString(),
                        serverId = validHealth ? identity.ServerId : Guid.NewGuid(),
                        workspaceId = identity.WorkspaceId
                    })
                    : "{}", System.Text.Encoding.UTF8, "application/json")
            });
    }

    private static System.Text.Json.Nodes.JsonObject ValidPairingResponse() => new()
    {
        ["serveOrigin"] = "https://snook-host.example-tailnet.ts.net/",
        ["serverId"] = Guid.NewGuid(),
        ["workspaceId"] = Guid.NewGuid(),
        ["workspaceName"] = "Workspace",
        ["contract"] = "1.6",
        ["credentialId"] = Guid.NewGuid(),
        ["deviceName"] = "Laptop",
        ["tailscaleLogin"] = null,
        ["credential"] = "abcdefghijklmnopqrstuvwxyzABCDEFGH12345678_-"
    };

    private sealed class JsonHandler(string json) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class FailingTransportHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException(HttpRequestError.SecureConnectionError, "simulated pairing TLS failure");
    }

    private sealed class PairingRandom(byte[] first) : IDaemonRandomSource
    {
        private int _call;
        public byte[] GetBytes(int count)
        {
            Assert.Equal(16, count);
            if (_call++ == 0) return first.ToArray();
            return Enumerable.Repeat((byte)_call, count).ToArray();
        }
        public Guid NewGuid() => Guid.NewGuid();
    }
}
