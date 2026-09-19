using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Snook.Contracts;
using Snook.Domain;
using Snook.Persistence.Sqlite;
using Xunit;

namespace Snook.Application.Tests;

public sealed class RestoreVerificationTests
{
    [Fact]
    public async Task InvalidRestoreInputsLeaveTheWorkspaceUntouchedAndValidMovedPairsRestore()
    {
        var directory = Directory.CreateTempSubdirectory("snook-restore-verification-");
        try
        {
            var database = Path.Combine(directory.FullName, "workspace.db");
            await using var backend = new SnookBackend(new SqliteStore(database));
            await backend.InitializeAsync();
            await ExerciseAsync(backend, database, directory.FullName);
        }
        finally { directory.Delete(true); }
    }

    internal static async Task ExerciseAsync(IBackendClient backend, string database, string root)
    {
        var beforeBackup = await backend.GetBootstrapAsync();
        var directory = Directory.CreateDirectory(Path.Combine(root, "restore-verification"));
        var backup = await backend.CreateBackupAsync(Path.Combine(directory.FullName, "verified.db"));
        var sourceBytes = await File.ReadAllBytesAsync(backup.Path);
        var sourceManifest = await File.ReadAllTextAsync(backup.ManifestPath!);
        var afterBackup = await backend.CreateBoardAsync("Restore verification survivor");
        var beforeFailure = await backend.GetBootstrapAsync();
        var databaseBytes = await File.ReadAllBytesAsync(database);
        var originalArchives = Directory.GetFiles(Path.GetDirectoryName(database)!, Path.GetFileName(database) + ".before-restore-*").Order().ToArray();
        var candidate = Path.Combine(directory.FullName, "candidate.db");
        var manifestPath = candidate + ".manifest.json";
        await File.WriteAllBytesAsync(candidate, sourceBytes);
        await RejectAsync(candidate, SnookErrorCode.SchemaIncompatible); // A bare DB is not a verified pair.

        foreach (var text in new[] { "null", "[]", "{}", "{", new string(' ', 16385),
            sourceManifest.Insert(1, "\"formatVersion\":1,") })
        {
            await File.WriteAllTextAsync(manifestPath, text);
            await RejectAsync(candidate, SnookErrorCode.SchemaIncompatible);
        }
        foreach (var (property, value) in new (string, JsonNode?)[]
        {
            ("formatVersion", 2), ("formatVersion", "1"), ("bytes", -1), ("bytes", sourceBytes.LongLength + 1),
            ("bytes", 16L * 1024 * 1024 * 1024 + 1), ("bytes", null), ("sha256", "bad"), ("sha256", new string('g', 64)),
            ("sha256", new string('0', 64)), ("integrity", "unchecked"), ("integrity", true),
            ("databasePath", ""), ("databasePath", null), ("createdAtUtc", "bad"), ("createdAtUtc", 0)
        })
        {
            var manifest = JsonNode.Parse(sourceManifest)!.AsObject();
            manifest[property] = value;
            await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString());
            await RejectAsync(candidate, SnookErrorCode.SchemaIncompatible);
        }

        await File.WriteAllTextAsync(manifestPath, sourceManifest);
        var changed = (byte[])sourceBytes.Clone();
        changed[^1] ^= 1;
        await File.WriteAllBytesAsync(candidate, changed);
        var mismatch = await RejectAsync(candidate, SnookErrorCode.SchemaIncompatible);
        Assert.Contains("SHA-256", mismatch.Message, StringComparison.Ordinal);
        await File.WriteAllBytesAsync(candidate, new byte[256]);
        await BackupFixture.WriteManifestAsync(candidate); // Valid hash, invalid SQLite.
        await RejectAsync(candidate, SnookErrorCode.SchemaIncompatible);
        await File.WriteAllBytesAsync(candidate, sourceBytes);
        await File.WriteAllTextAsync(manifestPath, sourceManifest);

        foreach (var suffix in new[] { "-wal", "-shm", "-journal", ".owner" })
        {
            await File.WriteAllTextAsync(candidate + suffix, "Sidecar sentinel");
            await RejectAsync(candidate, SnookErrorCode.ValidationFailed);
            Assert.Equal("Sidecar sentinel", await File.ReadAllTextAsync(candidate + suffix));
            File.Delete(candidate + suffix);
        }
        foreach (var invalid in new[] { "", "relative.db", database, database + ".owner", directory.FullName })
            await RejectAsync(invalid, SnookErrorCode.ValidationFailed);
        await RejectAsync(Path.Combine(directory.FullName, "missing.db"), SnookErrorCode.NotFound);

        if (!OperatingSystem.IsWindows())
        {
            var link = Path.Combine(directory.FullName, "link.db");
            File.CreateSymbolicLink(link, backup.Path);
            await RejectAsync(link, SnookErrorCode.ValidationFailed);
            var parentLink = Path.Combine(directory.FullName, "parent-link");
            Directory.CreateSymbolicLink(parentLink, Path.GetDirectoryName(backup.Path)!);
            await RejectAsync(Path.Combine(parentLink, Path.GetFileName(backup.Path)), SnookErrorCode.ValidationFailed);
            File.Delete(manifestPath);
            File.CreateSymbolicLink(manifestPath, backup.ManifestPath!);
            await RejectAsync(candidate, SnookErrorCode.ValidationFailed);
            File.Delete(manifestPath);
            await File.WriteAllTextAsync(manifestPath, sourceManifest);
        }

        // The original recorded path is informational. Relocating a pair does not
        // change its bytes or require trusting the path written in the manifest.
        var movedManifest = JsonNode.Parse(sourceManifest)!.AsObject();
        movedManifest["databasePath"] = "C:\\old-machine\\backups\\workspace.db";
        movedManifest["sha256"] = backup.Sha256.ToUpperInvariant();
        await File.WriteAllTextAsync(manifestPath, movedManifest.ToJsonString());
        var notification = new TaskCompletionSource<ChangeNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<ChangeNotification> handler = (_, change) => { if (change.ChangeKind == "restored") notification.TrySetResult(change); };
        backend.Changed += handler;
        try
        {
            var restored = await backend.RestoreBackupAsync(candidate);
            Assert.Equal(candidate, restored.SourcePath);
            Assert.Equal(backup.Sha256, restored.Sha256);
            var change = await notification.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(beforeBackup.CommittedCursor, change.Cursor);
            Assert.Equal(beforeBackup.Workspace.Id, change.AggregateId);
            Assert.Equal(Guid.Empty, change.OperationId);
            Assert.Equal("workspace", change.AggregateType);
            Assert.DoesNotContain((await backend.GetBootstrapAsync()).Boards, item => item.Id == afterBackup.Id);
        }
        finally { backend.Changed -= handler; }
        Assert.Equal(sourceBytes, await File.ReadAllBytesAsync(backup.Path));
        Assert.Equal(sourceManifest, await File.ReadAllTextAsync(backup.ManifestPath!));
        Assert.Equal(sourceBytes, await File.ReadAllBytesAsync(candidate));
        Assert.False(File.Exists(candidate + "-wal"));
        Assert.False(File.Exists(candidate + "-shm"));
        Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(database)!, ".snook-restore-*"));

        async Task<SnookException> RejectAsync(string path, SnookErrorCode code)
        {
            var error = await Assert.ThrowsAsync<SnookException>(() => backend.RestoreBackupAsync(path));
            Assert.Equal(code, error.Code);
            Assert.DoesNotContain(root, error.Message, StringComparison.Ordinal);
            Assert.Equal(databaseBytes, await File.ReadAllBytesAsync(database));
            Assert.Equal(beforeFailure.CommittedCursor, (await backend.GetBootstrapAsync()).CommittedCursor);
            Assert.Contains((await backend.GetBootstrapAsync()).Boards, item => item.Id == afterBackup.Id);
            Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(database)!, ".snook-restore-*"));
            Assert.Equal(originalArchives, Directory.GetFiles(Path.GetDirectoryName(database)!, Path.GetFileName(database) + ".before-restore-*").Order().ToArray());
            return error;
        }
    }

    [Fact]
    public async Task CancellationAfterRestoreCommitDoesNotFailResultOrReorderReentrantNotifications()
    {
        var directory = Directory.CreateTempSubdirectory("snook-restore-commit-");
        try
        {
            var clock = new RestoreClock();
            await using var backend = new SnookBackend(new SqliteStore(Path.Combine(directory.FullName, "workspace.db"), clock), clock);
            await backend.InitializeAsync();
            var before = await backend.GetBootstrapAsync();
            var backup = await backend.CreateBackupAsync(Path.Combine(directory.FullName, "backup.db"));
            await backend.CreateBoardAsync("Later removed board");
            using var cancellation = new CancellationTokenSource();
            var changes = new List<ChangeNotification>();
            Task<Board>? reentrant = null;
            backend.Changed += (_, change) =>
            {
                if (change.ChangeKind != "restored") return;
                cancellation.Cancel();
                reentrant = backend.CreateBoardAsync("After restore observer");
                throw new InvalidOperationException("An observer must not fail the committed restore or starve later observers.");
            };
            backend.Changed += (_, change) => changes.Add(change);
            var result = await backend.RestoreBackupAsync(backup.Path, cancellation.Token);
            Assert.True(cancellation.IsCancellationRequested);
            Assert.Equal(backup.Sha256, result.Sha256);
            Assert.NotNull(reentrant);
            var created = await reentrant.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Collection(changes,
                restore => { Assert.Equal("restored", restore.ChangeKind); Assert.Equal(before.CommittedCursor, restore.Cursor); Assert.Equal(clock.GetUtcNow(), restore.CommittedAtUtc); },
                create => { Assert.Equal(created.Id, create.AggregateId); Assert.True(create.Cursor > changes[0].Cursor); });
            Assert.Single((await backend.GetBootstrapAsync()).Boards, board => board.Name == "After restore observer");
            Assert.Empty(Directory.GetDirectories(directory.FullName, ".snook-restore-*"));
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public async Task CancellationBeforeStagingLeavesTheLiveWorkspaceAndBackupUnchanged()
    {
        var directory = Directory.CreateTempSubdirectory("snook-restore-cancel-");
        try
        {
            var database = Path.Combine(directory.FullName, "workspace.db");
            await using var backend = new SnookBackend(new SqliteStore(database));
            await backend.InitializeAsync();
            var backup = await backend.CreateBackupAsync(Path.Combine(directory.FullName, "backup.db"));
            var marker = await backend.CreateBoardAsync("Keep after cancellation");
            var bytes = await File.ReadAllBytesAsync(database);
            using var cancellation = new CancellationTokenSource();
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => backend.RestoreBackupAsync(backup.Path, cancellation.Token));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(database));
            Assert.Contains((await backend.GetBootstrapAsync()).Boards, board => board.Id == marker.Id);
            Assert.Empty(Directory.GetDirectories(directory.FullName, ".snook-restore-*"));
            Assert.Empty(Directory.GetFiles(directory.FullName, "workspace.db.before-restore-*"));
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public async Task MissingActiveDatabaseWithRestoreRecoveryFilesNeverInitializesAnEmptyWorkspace()
    {
        var directory = Directory.CreateTempSubdirectory("snook-restore-interrupted-");
        try
        {
            var database = Path.Combine(directory.FullName, "workspace.db");
            await using (var backend = new SnookBackend(new SqliteStore(database)))
            {
                await backend.InitializeAsync();
                await backend.CreateBoardAsync("Retained before interrupted activation");
            }
            var archive = database + ".before-restore-interrupted";
            File.Move(database, archive);
            var retained = await File.ReadAllBytesAsync(archive);
            await using var candidate = new SqliteStore(database);
            var error = await Assert.ThrowsAsync<SnookException>(() => candidate.InitializeAsync());
            Assert.Equal(SnookErrorCode.StoreUnavailable, error.Code);
            Assert.Contains("recovery", error.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(database));
            Assert.False(File.Exists(database + ".owner"));
            Assert.Equal(retained, await File.ReadAllBytesAsync(archive));
        }
        finally { directory.Delete(true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestoreExcludesReadsAndRetainsOwnershipUntilConnectionDrain(bool disposeWhileRestoring)
    {
        var directory = Directory.CreateTempSubdirectory("snook-restore-connections-");
        using var clock = new PausedRestoreClock();
        Task<RestoreResult>? restoring = null;
        SnookBackend? backend = null;
        try
        {
            var database = Path.Combine(directory.FullName, "workspace.db");
            backend = new SnookBackend(new SqliteStore(database, clock));
            await backend.InitializeAsync();
            var backup = await backend.CreateBackupAsync(Path.Combine(directory.FullName, "backup.db"));
            var later = await backend.CreateBoardAsync("Removed by restore");
            clock.Arm();
            restoring = Task.Run(() => backend.RestoreBackupAsync(backup.Path));
            await clock.AtCommit.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(File.Exists(database));
            if (disposeWhileRestoring)
            {
                var disposing = backend.DisposeAsync().AsTask();
                Assert.False(disposing.IsCompleted);
                await using (var competing = new SqliteStore(database))
                    Assert.Equal(SnookErrorCode.StoreUnavailable, (await Assert.ThrowsAsync<SnookException>(() => competing.InitializeAsync())).Code);
                clock.Continue.Set();
                await restoring;
                await disposing;
                await using var reopened = new SnookBackend(new SqliteStore(database));
                await reopened.InitializeAsync();
                Assert.DoesNotContain((await reopened.GetBootstrapAsync()).Boards, board => board.Id == later.Id);
            }
            else
            {
                var reading = backend.GetBootstrapAsync();
                Assert.False(reading.IsCompleted);
                clock.Continue.Set();
                await restoring;
                Assert.DoesNotContain((await reading).Boards, board => board.Id == later.Id);
            }
        }
        finally
        {
            clock.Continue.Set();
            if (restoring is not null) await restoring;
            if (backend is not null) await backend.DisposeAsync();
            directory.Delete(true);
        }
    }

    private sealed class PausedRestoreClock : TimeProvider, IDisposable
    {
        private int _remaining = -1;
        public TaskCompletionSource AtCommit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Continue { get; } = new();
        public void Arm() => Volatile.Write(ref _remaining, 2); // archive name, then committed notification
        public override DateTimeOffset GetUtcNow()
        {
            if (Interlocked.Decrement(ref _remaining) == 0)
            {
                AtCommit.TrySetResult();
                if (!Continue.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Test did not release the restore commit.");
            }
            return new DateTimeOffset(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);
        }
        public void Dispose() => Continue.Dispose();
    }

    private sealed class RestoreClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);
    }
}
