using System.Net;
using System.Text;
using System.Text.Json;
using Snook.Application;
using Snook.Domain;
using Xunit;

namespace Snook.Application.Tests;

public sealed class TailnetPairingClientTests
{
    private static readonly Uri Origin = new("https://snook-host.tail123.ts.net/");
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 19, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task BeginAndCompleteStayOnReviewedOriginAndSendNoIdentityHeader()
    {
        var installationId = Guid.NewGuid();
        var enrollmentId = Guid.NewGuid();
        var instanceId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var credentialId = Guid.NewGuid();
        using var handler = new PairingHandler((request, call) =>
        {
            Assert.Equal(Origin.Host, request.RequestUri?.Host);
            Assert.Equal(call == 1 ? "/v1/pairing/begin" : "/v1/pairing/complete",
                request.RequestUri?.AbsolutePath);
            Assert.False(request.Headers.Contains("Tailscale-User-Login"));
            Assert.False(request.Headers.Contains("X-Snook-Token"));
            Assert.False(request.Headers.Contains("X-Snook-Device-Token"));
            return call == 1
                ? Json(new DaemonPairingPreview(enrollmentId, Origin,
                    "jones@example.com", "Jones phone", instanceId, workspaceId,
                    Now.AddMinutes(2)))
                : Json(new DaemonPairingCredential(credentialId, new string('A', 64),
                    Origin, "jones@example.com", "Jones phone", installationId,
                    instanceId, workspaceId, Now));
        });
        await using var client = new AndroidTailnetPairingClient(Origin, handler,
            new FixedClock(Now));
        var preview = await client.BeginAsync(new string('B', 32) + "." + new string('C', 32),
            installationId, "Jones phone");
        Assert.Equal("jones@example.com", preview.TailnetLogin);
        var completed = await client.CompleteAsync(preview, installationId);
        Assert.Equal(credentialId, completed.CredentialId);
        Assert.Equal(new string('A', 64), completed.Credential);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task CompletionRejectsMetadataSubstitution()
    {
        var installationId = Guid.NewGuid();
        var preview = new DaemonPairingPreview(Guid.NewGuid(), Origin,
            "jones@example.com", "Jones phone", Guid.NewGuid(), Guid.NewGuid(),
            Now.AddMinutes(1));
        using var handler = new PairingHandler((_, _) => Json(new DaemonPairingCredential(
            Guid.NewGuid(), new string('A', 64), Origin, "attacker@example.com",
            preview.DeviceName, installationId, preview.InstanceId, preview.WorkspaceId, Now)));
        await using var client = new AndroidTailnetPairingClient(Origin, handler,
            new FixedClock(Now));
        var error = await Assert.ThrowsAsync<SnookException>(() =>
            client.CompleteAsync(preview, installationId));
        Assert.Equal(SnookErrorCode.StoreUnavailable, error.Code);
    }

    [Fact]
    public async Task RedirectIsRejectedWithoutFollowingAlternateAuthority()
    {
        using var handler = new PairingHandler((_, _) => new HttpResponseMessage(HttpStatusCode.Redirect)
        {
            Headers = { Location = new Uri("https://attacker.example/pair") }
        });
        await using var client = new AndroidTailnetPairingClient(Origin, handler,
            new FixedClock(Now));
        await Assert.ThrowsAsync<SnookException>(() => client.BeginAsync(
            new string('B', 32) + "." + new string('C', 32), Guid.NewGuid(), "Phone"));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task OversizedResponseIsRejectedBeforeDeserialization()
    {
        using var handler = new PairingHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[65_537])
        });
        await using var client = new AndroidTailnetPairingClient(Origin, handler,
            new FixedClock(Now));
        var error = await Assert.ThrowsAsync<SnookException>(() => client.BeginAsync(
            new string('B', 32) + "." + new string('C', 32), Guid.NewGuid(), "Phone"));
        Assert.Equal(SnookErrorCode.StoreUnavailable, error.Code);
    }

    private static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json")
    };

    private sealed class PairingHandler(
        Func<HttpRequestMessage, int, HttpResponseMessage> response) : HttpMessageHandler
    {
        private int _calls;
        public int Calls => _calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(response(request, Interlocked.Increment(ref _calls)));
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
