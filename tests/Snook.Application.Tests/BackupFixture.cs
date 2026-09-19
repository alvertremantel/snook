using System.Security.Cryptography;
using System.Text.Json;

namespace Snook.Application.Tests;

internal static class BackupFixture
{
    // Only for deliberately constructed historical/corrupt test databases. This
    // is not a production command to bless an unknown backup or bypass checks.
    internal static async Task WriteManifestAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
        var manifest = new { formatVersion = 1, createdAtUtc = DateTimeOffset.UtcNow,
            databasePath = path, bytes = stream.Length, sha256 = hash, integrity = "sqlite-backup-verified" };
        await File.WriteAllTextAsync(path + ".manifest.json", JsonSerializer.Serialize(manifest));
    }
}
