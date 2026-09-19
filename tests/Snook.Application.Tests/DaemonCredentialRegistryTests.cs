using System.Text.Json;
using Snook.Daemon;
using Snook.Domain;
using Xunit;

namespace Snook.Application.Tests;

public sealed class DaemonCredentialRegistryTests
{
    private static readonly string[] WorkspaceScopes = ["workspace:full"];

    [Fact]
    public async Task RegistryIsPrivateDeterministicAtomicAndSurvivesRestart()
    {
        var root = Directory.CreateTempSubdirectory("snook-auth-registry-");
        try
        {
            var path = Path.Combine(root.FullName, "daemon-auth.json");
            var serverId = Guid.Parse("11111111-1111-1111-1111-111111111111");
            var credentialId = Guid.Parse("22222222-2222-2222-2222-222222222222");
            var credentialBytes = Enumerable.Range(0, 32).Select(index => (byte)index).ToArray();
            var salt = Enumerable.Range(32, 32).Select(index => (byte)index).ToArray();
            var random = new DeterministicRandom([serverId, credentialId], [credentialBytes, salt]);
            string credential;

            using (var registry = await DaemonCredentialRegistry.OpenAsync(path, CancellationToken.None, random))
            {
                Assert.Equal(serverId, registry.ServerId);
                var createdAt = new DateTimeOffset(2026, 9, 19, 14, 0, 0, TimeSpan.FromHours(2));
                var created = await registry.CreateAsync("Travel laptop", " User@Example.COM ",
                    createdAt, CancellationToken.None);
                Assert.Equal(credentialId, created.Id);
                credential = created.Credential;
                Assert.Equal(Convert.ToBase64String(credentialBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_'), credential);
                var authenticated = registry.Authenticate(credential, "user@example.com", createdAt.ToUniversalTime());
                Assert.Equal(credentialId, authenticated?.CredentialId);
                Assert.True(authenticated?.IsPending);
                var listed = Assert.Single(registry.List());
                Assert.Equal("user@example.com", listed.TailscaleLogin);
                Assert.Equal(createdAt.ToUniversalTime(), listed.CreatedAtUtc);
                Assert.Equal(createdAt.ToUniversalTime().AddMinutes(5), listed.PendingUntilUtc);
                await registry.PromoteAsync(credentialId, createdAt.ToUniversalTime().AddMinutes(1), CancellationToken.None);
                Assert.False(registry.Authenticate(credential, null,
                    createdAt.ToUniversalTime().AddMinutes(1))?.IsPending);

                var beforeCancellation = await File.ReadAllBytesAsync(path);
                using var cancelled = new CancellationTokenSource();
                await cancelled.CancelAsync();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => registry.CreateAsync(
                    "Cancelled", null, DateTimeOffset.UtcNow, cancelled.Token));
                Assert.Equal(beforeCancellation, await File.ReadAllBytesAsync(path));
                Assert.Empty(Directory.GetFiles(root.FullName, "*.tmp"));
            }

            Assert.DoesNotContain(credential, await File.ReadAllTextAsync(path), StringComparison.Ordinal);
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));

            using (var reopened = await DaemonCredentialRegistry.OpenAsync(path, CancellationToken.None))
            {
                Assert.Equal(serverId, reopened.ServerId);
                Assert.Equal(credentialId, reopened.Authenticate(credential, null,
                    new DateTimeOffset(2026, 9, 19, 12, 5, 0, TimeSpan.Zero))?.CredentialId);
                var seenAt = new DateTimeOffset(2026, 9, 19, 12, 5, 0, TimeSpan.Zero);
                await reopened.TouchAsync(credentialId, seenAt, CancellationToken.None);
                Assert.Equal(seenAt, Assert.Single(reopened.List()).LastSeenAtUtc);
                await reopened.RevokeAsync(credentialId, CancellationToken.None);
                Assert.Null(reopened.Authenticate(credential, null, seenAt));
                Assert.Null(reopened.Authenticate(new string('a', 43), null, seenAt));
                Assert.True(Assert.Single(reopened.List()).Revoked);
            }

            using var final = await DaemonCredentialRegistry.OpenAsync(path, CancellationToken.None);
            Assert.Null(final.Authenticate(credential, null, DateTimeOffset.UtcNow));
            Assert.True(Assert.Single(final.List()).Revoked);
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public async Task RegistryRejectsFutureCorruptOversizedNonprivateAndLinkedFiles()
    {
        var root = Directory.CreateTempSubdirectory("snook-auth-invalid-");
        try
        {
            var path = Path.Combine(root.FullName, "daemon-auth.json");
            using (var registry = await DaemonCredentialRegistry.OpenAsync(path, CancellationToken.None)) { }
            using var validDocument = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            var serverId = validDocument.RootElement.GetProperty("serverId").GetGuid();

            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { version = 99, serverId, devices = Array.Empty<object>() }));
            MakePrivate(path);
            await Assert.ThrowsAsync<SnookException>(() => DaemonCredentialRegistry.OpenAsync(path, CancellationToken.None));

            await File.WriteAllTextAsync(path, "not json");
            MakePrivate(path);
            await Assert.ThrowsAsync<SnookException>(() => DaemonCredentialRegistry.OpenAsync(path, CancellationToken.None));

            foreach (var nullShape in new[]
            {
                $"{{\"version\":1,\"serverId\":\"{serverId}\",\"devices\":null}}",
                $"{{\"version\":1,\"serverId\":\"{serverId}\",\"devices\":[null]}}"
            })
            {
                await File.WriteAllTextAsync(path, nullShape);
                MakePrivate(path);
                await Assert.ThrowsAsync<SnookException>(() => DaemonCredentialRegistry.OpenAsync(path, CancellationToken.None));
            }

            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
            {
                version = 1,
                serverId,
                devices = Array.Empty<object>(),
                unsupportedFutureField = true
            }));
            MakePrivate(path);
            await Assert.ThrowsAsync<SnookException>(() => DaemonCredentialRegistry.OpenAsync(path, CancellationToken.None));

            await File.WriteAllTextAsync(path,
                $"{{\"version\":1,\"Version\":1,\"serverId\":\"{serverId}\",\"devices\":[]}}");
            MakePrivate(path);
            await Assert.ThrowsAsync<SnookException>(() => DaemonCredentialRegistry.OpenAsync(path, CancellationToken.None));

            var missingCreationTime = new[]
            {
                new
                {
                    credentialId = Guid.NewGuid(),
                    displayName = "Timeless device",
                    createdAtUtc = default(DateTimeOffset),
                    lastSeenAtUtc = (DateTimeOffset?)null,
                    tailscaleLogin = (string?)null,
                    scopes = WorkspaceScopes,
                    revoked = false,
                    salt = new byte[32],
                    verifier = new byte[32]
                }
            };
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { version = 1, serverId, devices = missingCreationTime }));
            MakePrivate(path);
            await Assert.ThrowsAsync<SnookException>(() => DaemonCredentialRegistry.OpenAsync(path, CancellationToken.None));

            var tooMany = Enumerable.Range(0, 257).Select(index => new
            {
                credentialId = Guid.NewGuid(),
                displayName = $"Device {index}",
                createdAtUtc = new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero),
                lastSeenAtUtc = (DateTimeOffset?)null,
                tailscaleLogin = (string?)null,
                scopes = WorkspaceScopes,
                revoked = false,
                salt = new byte[32],
                verifier = new byte[32]
            }).ToArray();
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { version = 1, serverId, devices = tooMany }));
            MakePrivate(path);
            await Assert.ThrowsAsync<SnookException>(() => DaemonCredentialRegistry.OpenAsync(path, CancellationToken.None));

            await File.WriteAllTextAsync(path, new string('x', 256 * 1024 + 1));
            MakePrivate(path);
            await Assert.ThrowsAsync<SnookException>(() => DaemonCredentialRegistry.OpenAsync(path, CancellationToken.None));

            if (!OperatingSystem.IsWindows())
            {
                await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { version = 1, serverId, devices = Array.Empty<object>() }));
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
                await Assert.ThrowsAsync<SnookException>(() => DaemonCredentialRegistry.OpenAsync(path, CancellationToken.None));

                File.Delete(path);
                var target = Path.Combine(root.FullName, "target.json");
                await File.WriteAllTextAsync(target, "sentinel");
                MakePrivate(target);
                File.CreateSymbolicLink(path, target);
                await Assert.ThrowsAsync<SnookException>(() => DaemonCredentialRegistry.OpenAsync(path, CancellationToken.None));
                Assert.Equal("sentinel", await File.ReadAllTextAsync(target));
            }
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public async Task UnconfirmedCredentialExpiresAcrossRestart()
    {
        var root = Directory.CreateTempSubdirectory("snook-auth-pending-");
        try
        {
            var path = Path.Combine(root.FullName, "daemon-auth.json");
            var createdAt = new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);
            string credential;
            using (var registry = await DaemonCredentialRegistry.OpenAsync(path, CancellationToken.None))
            {
                var created = await registry.CreateAsync("Unconfirmed laptop", null, createdAt,
                    CancellationToken.None);
                credential = created.Credential;
                Assert.True(registry.Authenticate(credential, null, createdAt.AddMinutes(4))?.IsPending);
            }

            using var reopened = await DaemonCredentialRegistry.OpenAsync(path, CancellationToken.None);
            Assert.True(reopened.Authenticate(credential, null, createdAt.AddMinutes(4))?.IsPending);
            Assert.Null(reopened.Authenticate(credential, null, createdAt.AddMinutes(5)));
            Assert.Equal(createdAt.AddMinutes(5), Assert.Single(reopened.List()).PendingUntilUtc);
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public async Task FullRegistryReclaimsOldestRevokedRecordButRejectsAllActiveRegistry()
    {
        var root = Directory.CreateTempSubdirectory("snook-auth-capacity-");
        try
        {
            var path = Path.Combine(root.FullName, "daemon-auth.json");
            var serverId = Guid.Parse("11111111-1111-1111-1111-111111111111");
            var records = Enumerable.Range(0, 256).Select(index => new
            {
                credentialId = Guid.NewGuid(),
                displayName = $"Device {index}",
                createdAtUtc = new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero).AddMinutes(index),
                lastSeenAtUtc = (DateTimeOffset?)null,
                tailscaleLogin = (string?)null,
                scopes = WorkspaceScopes,
                revoked = false,
                salt = new byte[32],
                verifier = new byte[32]
            }).ToArray();
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { version = 1, serverId, devices = records }));
            MakePrivate(path);
            using (var full = await DaemonCredentialRegistry.OpenAsync(path, CancellationToken.None))
                await Assert.ThrowsAsync<SnookException>(() => full.CreateAsync("Blocked", null, DateTimeOffset.UtcNow, CancellationToken.None));

            var withRevoked = records.Select((record, index) => new
            {
                record.credentialId,
                record.displayName,
                record.createdAtUtc,
                record.lastSeenAtUtc,
                record.tailscaleLogin,
                record.scopes,
                revoked = index is 2 or 7,
                record.salt,
                record.verifier
            }).ToArray();
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { version = 1, serverId, devices = withRevoked }));
            MakePrivate(path);
            var newId = Guid.Parse("22222222-2222-2222-2222-222222222222");
            var random = new DeterministicRandom([newId], [new byte[32], Enumerable.Repeat((byte)1, 32).ToArray()]);
            using var registry = await DaemonCredentialRegistry.OpenAsync(path, CancellationToken.None, random);
            await registry.CreateAsync("Replacement", null, DateTimeOffset.UtcNow, CancellationToken.None);

            var devices = registry.List();
            Assert.Equal(256, devices.Count);
            Assert.DoesNotContain(devices, device => device.CredentialId == records[2].credentialId);
            Assert.Contains(devices, device => device.CredentialId == records[7].credentialId && device.Revoked);
            Assert.Contains(devices, device => device.CredentialId == newId && !device.Revoked);
        }
        finally { root.Delete(true); }
    }

    private static void MakePrivate(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private sealed class DeterministicRandom(IEnumerable<Guid> ids, IEnumerable<byte[]> bytes) : IDaemonRandomSource
    {
        private readonly Queue<Guid> _ids = new(ids);
        private readonly Queue<byte[]> _bytes = new(bytes.Select(value => value.ToArray()));
        public byte[] GetBytes(int count)
        {
            var value = _bytes.Dequeue();
            Assert.Equal(count, value.Length);
            return value;
        }
        public Guid NewGuid() => _ids.Dequeue();
    }
}
