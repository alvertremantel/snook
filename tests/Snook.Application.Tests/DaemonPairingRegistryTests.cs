using Snook.Application;
using Snook.Domain;
using Xunit;

namespace Snook.Application.Tests;

public sealed class DaemonPairingRegistryTests
{
    private static readonly Uri Origin = new("https://snook-host.tail123.ts.net/");

    [Fact]
    public async Task PairingPersistsOnlyDigestsBindsIdentityAndRevokes()
    {
        var directory = Directory.CreateTempSubdirectory("snook-pairing-");
        var path = Path.Combine(directory.FullName, "android-pairings.json");
        var clock = new PairingClock(new DateTimeOffset(2026, 9, 20, 18, 0, 0, TimeSpan.Zero));
        var instanceId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var installationId = Guid.NewGuid();
        string credential;
        Guid credentialId;
        try
        {
            using (var registry = new DaemonPairingRegistry(path, clock))
            {
                var challenge = await registry.CreateChallengeAsync(Origin, instanceId, workspaceId);
                var secret = challenge.Code.Split('.')[1];
                var challengeFile = await File.ReadAllTextAsync(path);
                Assert.DoesNotContain(challenge.Code, challengeFile, StringComparison.Ordinal);
                Assert.DoesNotContain(secret, challengeFile, StringComparison.Ordinal);

                var preview = await registry.BeginAsync(challenge.Code, installationId,
                    "Jones phone", "JONES@EXAMPLE.COM", instanceId, workspaceId);
                Assert.Equal("jones@example.com", preview.TailnetLogin);
                Assert.Equal(Origin, preview.ServeOrigin);
                var paired = await registry.CompleteAsync(preview.EnrollmentId,
                    "jones@example.com", instanceId, workspaceId);
                credential = paired.Credential;
                credentialId = paired.CredentialId;
                Assert.Matches("^[A-F0-9]{64}$", credential);
                Assert.DoesNotContain(credential, await File.ReadAllTextAsync(path), StringComparison.Ordinal);
                Assert.Equal(credentialId, await registry.AuthenticateAsync(credential,
                    "JONES@EXAMPLE.COM", instanceId, workspaceId));
                Assert.Null(await registry.AuthenticateAsync(credential,
                    "other@example.com", instanceId, workspaceId));
                Assert.Null(await registry.AuthenticateAsync(credential,
                    "jones@example.com", instanceId, Guid.NewGuid()));
                await Assert.ThrowsAsync<SnookException>(() => registry.CompleteAsync(
                    preview.EnrollmentId, "jones@example.com", instanceId, workspaceId));

                var listed = Assert.Single(await registry.ListAsync());
                Assert.Equal(credentialId, listed.CredentialId);
                Assert.Equal(installationId, listed.InstallationId);
                Assert.Equal("Jones phone", listed.DeviceName);
                var renamed = await registry.RenameAsync(credentialId, "Jones foldable");
                Assert.Equal("Jones foldable", renamed.DeviceName);
            }

            using (var reopened = new DaemonPairingRegistry(path, clock))
            {
                Assert.Equal(credentialId, await reopened.AuthenticateAsync(credential,
                    "jones@example.com", instanceId, workspaceId));
                Assert.Equal("Jones foldable", Assert.Single(await reopened.ListAsync()).DeviceName);
                var revoked = new List<Guid>();
                reopened.CredentialRevoked += (_, id) => revoked.Add(id);
                Assert.True(await reopened.RevokeAsync(credentialId));
                Assert.Equal([credentialId], revoked);
                Assert.Null(await reopened.AuthenticateAsync(credential,
                    "jones@example.com", instanceId, workspaceId));
                Assert.False(await reopened.RevokeAsync(credentialId));
            }
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public async Task AttemptAndExpiryBoundsFailClosedWithGenericErrors()
    {
        var directory = Directory.CreateTempSubdirectory("snook-pairing-expiry-");
        var clock = new PairingClock(new DateTimeOffset(2026, 9, 20, 18, 0, 0, TimeSpan.Zero));
        var instanceId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        try
        {
            using var registry = new DaemonPairingRegistry(
                Path.Combine(directory.FullName, "android-pairings.json"), clock);
            var exhausted = await registry.CreateChallengeAsync(Origin, instanceId, workspaceId);
            var id = exhausted.Code[..32];
            for (var attempt = 0; attempt < DaemonPairingRegistry.ChallengeAttemptLimit; attempt++)
            {
                var error = await Assert.ThrowsAsync<SnookException>(() => registry.BeginAsync(
                    id + "." + new string('0', 32), Guid.NewGuid(), "Phone",
                    "user@example.com", instanceId, workspaceId));
                Assert.Equal(SnookErrorCode.StoreUnavailable, error.Code);
                Assert.DoesNotContain("attempt", error.Message, StringComparison.OrdinalIgnoreCase);
            }
            await Assert.ThrowsAsync<SnookException>(() => registry.BeginAsync(exhausted.Code,
                Guid.NewGuid(), "Phone", "user@example.com", instanceId, workspaceId));

            var expiringChallenge = await registry.CreateChallengeAsync(Origin, instanceId, workspaceId);
            clock.Advance(DaemonPairingRegistry.ChallengeLifetime + TimeSpan.FromSeconds(1));
            await Assert.ThrowsAsync<SnookException>(() => registry.BeginAsync(expiringChallenge.Code,
                Guid.NewGuid(), "Phone", "user@example.com", instanceId, workspaceId));

            var pendingChallenge = await registry.CreateChallengeAsync(Origin, instanceId, workspaceId);
            var preview = await registry.BeginAsync(pendingChallenge.Code, Guid.NewGuid(),
                "Phone", "user@example.com", instanceId, workspaceId);
            clock.Advance(DaemonPairingRegistry.EnrollmentLifetime + TimeSpan.FromSeconds(1));
            await Assert.ThrowsAsync<SnookException>(() => registry.CompleteAsync(preview.EnrollmentId,
                "user@example.com", instanceId, workspaceId));
            Assert.Empty(await registry.ListAsync());
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public async Task ExistingInstallationCannotSilentlyReplaceItsCredential()
    {
        var directory = Directory.CreateTempSubdirectory("snook-pairing-repair-");
        var path = Path.Combine(directory.FullName, "android-pairings.json");
        var instanceId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var installationId = Guid.NewGuid();
        try
        {
            using var registry = new DaemonPairingRegistry(path);
            var first = await registry.CreateChallengeAsync(Origin, instanceId, workspaceId);
            var preview = await registry.BeginAsync(first.Code, installationId, "Phone",
                "user@example.com", instanceId, workspaceId);
            _ = await registry.CompleteAsync(preview.EnrollmentId, "user@example.com",
                instanceId, workspaceId);

            var second = await registry.CreateChallengeAsync(Origin, instanceId, workspaceId);
            var error = await Assert.ThrowsAsync<SnookException>(() => registry.BeginAsync(
                second.Code, installationId, "Renamed phone", "user@example.com",
                instanceId, workspaceId));
            Assert.Equal(SnookErrorCode.InvalidTransition, error.Code);
            Assert.Single(await registry.ListAsync());
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public async Task WorkspaceRestoreRebindsAuthorizingDeviceAndRevokesEveryOtherDevice()
    {
        var directory = Directory.CreateTempSubdirectory("snook-pairing-rebind-");
        var path = Path.Combine(directory.FullName, "android-pairings.json");
        var instanceId = Guid.NewGuid();
        var oldWorkspace = Guid.NewGuid();
        var newWorkspace = Guid.NewGuid();
        try
        {
            string firstSecret;
            Guid firstId;
            string secondSecret;
            Guid secondId;
            using (var registry = new DaemonPairingRegistry(path))
            {
                async Task<DaemonPairingCredential> PairAsync(string name)
                {
                    var challenge = await registry.CreateChallengeAsync(Origin, instanceId, oldWorkspace);
                    var preview = await registry.BeginAsync(challenge.Code, Guid.NewGuid(), name,
                        "user@example.com", instanceId, oldWorkspace);
                    return await registry.CompleteAsync(preview.EnrollmentId,
                        "user@example.com", instanceId, oldWorkspace);
                }

                var first = await PairAsync("Restoring phone");
                var second = await PairAsync("Other tablet");
                firstSecret = first.Credential;
                firstId = first.CredentialId;
                secondSecret = second.Credential;
                secondId = second.CredentialId;
                _ = await registry.CreateChallengeAsync(Origin, instanceId, oldWorkspace);
                var revoked = new List<Guid>();
                registry.CredentialRevoked += (_, id) => revoked.Add(id);

                var rebound = await registry.RebindWorkspaceAsync(firstId,
                    oldWorkspace, newWorkspace);

                Assert.Equal(firstId, rebound.Device.CredentialId);
                Assert.Equal(newWorkspace, rebound.Device.WorkspaceId);
                Assert.Equal(1, rebound.RevokedOtherDeviceCount);
                Assert.Equal([secondId], revoked);
                Assert.Equal(firstId, await registry.AuthenticateAsync(firstSecret,
                    "user@example.com", instanceId, newWorkspace));
                Assert.Null(await registry.AuthenticateAsync(firstSecret,
                    "user@example.com", instanceId, oldWorkspace));
                Assert.Null(await registry.AuthenticateAsync(secondSecret,
                    "user@example.com", instanceId, newWorkspace));
                Assert.Single(await registry.ListAsync());
            }

            using var reopened = new DaemonPairingRegistry(path);
            Assert.Equal(firstId, await reopened.AuthenticateAsync(firstSecret,
                "user@example.com", instanceId, newWorkspace));
            Assert.Null(await reopened.AuthenticateAsync(secondSecret,
                "user@example.com", instanceId, newWorkspace));
            Assert.Equal(newWorkspace, Assert.Single(await reopened.ListAsync()).WorkspaceId);
        }
        finally { directory.Delete(true); }
    }

    private sealed class PairingClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        internal void Advance(TimeSpan duration) => _now += duration;
    }
}
