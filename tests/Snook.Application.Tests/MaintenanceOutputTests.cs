using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Snook.Contracts;
using Snook.Domain;
using Snook.Persistence.Sqlite;
using Xunit;

namespace Snook.Application.Tests;

public sealed class MaintenanceOutputTests
{
    [Fact]
    public async Task EmbeddedMaintenanceNeverReplacesExistingFilesOrWorkspacePaths()
    {
        var directory = Directory.CreateTempSubdirectory("snook-maintenance-");
        try
        {
            var database = Path.Combine(directory.FullName, "Snook", "workspace.db");
            await using var backend = new SnookBackend(new SqliteStore(database));
            await backend.InitializeAsync();
            await ExerciseAsync(backend, database, directory.FullName);
        }
        finally { directory.Delete(true); }
    }

    internal static async Task ExerciseAsync(IBackendClient backend, string database, string root)
    {
        var artifacts = Path.Combine(root, "maintenance-artifacts");
        Directory.CreateDirectory(artifacts);
        var existing = Path.Combine(artifacts, "existing.txt");
        await File.WriteAllTextAsync(existing, "Do not replace this file.");
        var before = await backend.GetBootstrapAsync();
        var databaseHash = await HashAsync(database);
        var privateDirectory = Path.GetDirectoryName(database)!;
        var tokenPath = Path.Combine(privateDirectory, "daemon.token");
        var descriptorPath = Path.Combine(privateDirectory, "daemon.endpoint.json");
        var tokenBefore = File.Exists(tokenPath) ? await File.ReadAllTextAsync(tokenPath) : null;
        var descriptorBefore = File.Exists(descriptorPath) ? await File.ReadAllTextAsync(descriptorPath) : null;
        string[] invalidPaths =
        [
            "", "   ", "relative.json", new('x', 4097), Path.Combine(artifacts, "bad\0.json"),
            artifacts + Path.DirectorySeparatorChar, artifacts,
            Path.Combine(artifacts, "..", "escaped.json"), Path.Combine(artifacts, "file:stream"),
            Path.Combine(artifacts, "NUL.json"), Path.Combine(artifacts, "LPT1"), Path.Combine(artifacts, "trailing."),
            existing, database, database + "-wal", database + "-shm", database + "-journal",
            database + ".owner", database + ".restore-forged", database + ".before-restore-forged",
            Path.Combine(privateDirectory, Path.GetFileName(database).ToUpperInvariant()),
            tokenPath, descriptorPath, Path.Combine(privateDirectory, "daemon.token.new")
        ];
        foreach (var path in invalidPaths)
            foreach (var operation in OutputOperations(backend, path))
                await AssertInvalidAsync(operation);

        Assert.Equal("Do not replace this file.", await File.ReadAllTextAsync(existing));
        Assert.Equal(databaseHash, await HashAsync(database));
        Assert.Equal(before.CommittedCursor, (await backend.GetBootstrapAsync()).CommittedCursor);
        if (tokenBefore is not null) Assert.Equal(tokenBefore, await File.ReadAllTextAsync(tokenPath));
        else Assert.False(File.Exists(tokenPath));
        if (descriptorBefore is not null) Assert.Equal(descriptorBefore, await File.ReadAllTextAsync(descriptorPath));
        else Assert.False(File.Exists(descriptorPath));
        Assert.False(File.Exists(database + ".restore-forged"));
        Assert.False(File.Exists(Path.Combine(privateDirectory, "daemon.token.new")));

        foreach (var suffix in new[] { ".manifest.json", "-wal", "-shm", "-journal", ".owner" })
        {
            var destination = Path.Combine(artifacts, "companion" + suffix.Replace('.', '_'));
            await File.WriteAllTextAsync(destination + suffix, "Keep companion.");
            await AssertInvalidAsync(() => backend.CreateBackupAsync(destination));
            Assert.False(File.Exists(destination));
            Assert.Equal("Keep companion.", await File.ReadAllTextAsync(destination + suffix));
        }

        var backup = await backend.CreateBackupAsync(Path.Combine(artifacts, "backup.db"));
        var json = await backend.ExportJsonAsync(Path.Combine(artifacts, "workspace.json"));
        var csv = await backend.ExportCsvAsync(Path.Combine(artifacts, "worklog.csv"), DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        foreach (var path in new[] { backup.Path, backup.ManifestPath!, json.Path, csv.Path })
        {
            var originalHash = await HashAsync(path);
            foreach (var operation in OutputOperations(backend, path)) await AssertInvalidAsync(operation);
            Assert.Equal(originalHash, await HashAsync(path));
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }
        Assert.Equal(backup.Sha256, await HashAsync(backup.Path));
        Assert.Equal(json.Sha256, await HashAsync(json.Path));
        Assert.Equal(csv.Sha256, await HashAsync(csv.Path));
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(backup.ManifestPath!));
        Assert.Equal(backup.Sha256, manifest.RootElement.GetProperty("sha256").GetString());
        Assert.Equal(backup.Bytes, manifest.RootElement.GetProperty("bytes").GetInt64());
        Assert.Equal(backup.Path, manifest.RootElement.GetProperty("databasePath").GetString());
        Assert.DoesNotContain(".snook-artifact-", await File.ReadAllTextAsync(backup.ManifestPath!), StringComparison.Ordinal);
        Assert.Empty(Directory.GetDirectories(artifacts, ".snook-artifact-*"));
    }

    [Fact]
    public async Task SymbolicLinksAndLinkedAncestorsCannotRedirectOutputs()
    {
        if (OperatingSystem.IsWindows()) return; // Creating Windows links needs a separate privilege/platform gate.
        var directory = Directory.CreateTempSubdirectory("snook-maintenance-links-");
        try
        {
            await using var backend = new SnookBackend(new SqliteStore(Path.Combine(directory.FullName, "workspace.db")));
            await backend.InitializeAsync();
            var realDirectory = Directory.CreateDirectory(Path.Combine(directory.FullName, "real"));
            var target = Path.Combine(realDirectory.FullName, "target");
            await File.WriteAllTextAsync(target, "Untouched.");
            var linkedFile = Path.Combine(directory.FullName, "linked-file");
            File.CreateSymbolicLink(linkedFile, target);
            var danglingFile = Path.Combine(directory.FullName, "dangling");
            File.CreateSymbolicLink(danglingFile, Path.Combine(realDirectory.FullName, "missing"));
            var linkedDirectory = Path.Combine(directory.FullName, "linked-directory");
            Directory.CreateSymbolicLink(linkedDirectory, realDirectory.FullName);
            var manifestLink = Path.Combine(directory.FullName, "manifest-link.db");
            File.CreateSymbolicLink(manifestLink + ".manifest.json", target);
            foreach (var path in new[] { linkedFile, danglingFile, Path.Combine(linkedDirectory, "new.json"), Path.Combine(linkedDirectory, "new-directory", "new.json") })
                foreach (var operation in OutputOperations(backend, path)) await AssertInvalidAsync(operation);
            await AssertInvalidAsync(() => backend.CreateBackupAsync(manifestLink));
            Assert.Equal("Untouched.", await File.ReadAllTextAsync(target));
            Assert.Single(realDirectory.GetFiles());
            Assert.Empty(realDirectory.GetDirectories());
            Assert.False(File.Exists(manifestLink));
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public async Task EmbeddedWorkspaceDirectoryAliasStillProtectsRealSidecarPaths()
    {
        if (OperatingSystem.IsWindows()) return;
        var directory = Directory.CreateTempSubdirectory("snook-maintenance-workspace-link-");
        try
        {
            var real = Directory.CreateDirectory(Path.Combine(directory.FullName, "real"));
            var alias = Path.Combine(directory.FullName, "alias");
            Directory.CreateSymbolicLink(alias, real.FullName);
            await using var backend = new SnookBackend(new SqliteStore(Path.Combine(alias, "workspace.db")));
            await backend.InitializeAsync();
            foreach (var name in new[] { "workspace.db-journal", "workspace.db.restore-fake", "daemon.token" })
            {
                var destination = Path.Combine(real.FullName, name);
                foreach (var operation in OutputOperations(backend, destination)) await AssertInvalidAsync(operation);
                Assert.False(File.Exists(destination));
            }
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public async Task ConcurrentPublicationHasOneWinnerAndInvalidOrCancelledExportsLeaveNoArtifact()
    {
        var directory = Directory.CreateTempSubdirectory("snook-maintenance-race-");
        try
        {
            await using var backend = new SnookBackend(new SqliteStore(Path.Combine(directory.FullName, "workspace.db")));
            await backend.InitializeAsync();
            var path = Path.Combine(directory.FullName, "contended.json");
            var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
            {
                try { return await backend.ExportJsonAsync(path); }
                catch (SnookException exception) when (exception.Code == SnookErrorCode.ValidationFailed) { return null; }
            }));
            var winner = Assert.Single(results.OfType<ExportResult>());
            Assert.Equal(winner.Sha256, await HashAsync(path));
            var invalid = Path.Combine(directory.FullName, "invalid.csv");
            await AssertInvalidAsync(() => backend.ExportCsvAsync(invalid, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(-1)));
            Assert.False(File.Exists(invalid));
            using var cancellation = new CancellationTokenSource();
            await cancellation.CancelAsync();
            var cancelled = Path.Combine(directory.FullName, "cancelled.json");
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => backend.ExportJsonAsync(cancelled, cancellation.Token));
            Assert.False(File.Exists(cancelled));
            Assert.Empty(Directory.GetDirectories(directory.FullName, ".snook-artifact-*"));
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public async Task BackupIntegrityFailureDoesNotPublishDatabaseOrManifest()
    {
        var directory = Directory.CreateTempSubdirectory("snook-maintenance-integrity-");
        try
        {
            var database = Path.Combine(directory.FullName, "workspace.db");
            await using var backend = new SnookBackend(new SqliteStore(database));
            await backend.InitializeAsync();
            await using (var corruptor = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString()))
            {
                await corruptor.OpenAsync();
                await using var command = corruptor.CreateCommand();
                command.CommandText = "PRAGMA foreign_keys=OFF; UPDATE projects SET board_id='00000000-0000-0000-0000-000000000001';";
                await command.ExecuteNonQueryAsync();
            }
            var backup = Path.Combine(directory.FullName, "rejected.db");
            var error = await Assert.ThrowsAsync<SnookException>(() => backend.CreateBackupAsync(backup));
            Assert.Equal(SnookErrorCode.SchemaIncompatible, error.Code);
            Assert.False(File.Exists(backup));
            Assert.False(File.Exists(backup + ".manifest.json"));
            Assert.Empty(Directory.GetDirectories(directory.FullName, ".snook-artifact-*"));
        }
        finally { directory.Delete(true); }
    }

    private static IEnumerable<Func<Task>> OutputOperations(IBackendClient backend, string path)
    {
        yield return () => backend.CreateBackupAsync(path);
        yield return () => backend.ExportJsonAsync(path);
        yield return () => backend.ExportCsvAsync(path, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    private static async Task AssertInvalidAsync(Func<Task> action)
        => Assert.Equal(SnookErrorCode.ValidationFailed, (await Assert.ThrowsAsync<SnookException>(action)).Code);

    private static async Task<string> HashAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
    }
}
