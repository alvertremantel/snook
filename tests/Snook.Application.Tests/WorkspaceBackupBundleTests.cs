using System.IO.Compression;
using Snook.Domain;
using Snook.Persistence.Sqlite;
using Snook.UI;
using Xunit;

namespace Snook.Application.Tests;

public sealed class WorkspaceBackupBundleTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NonSeekableDocumentAndZip64DirectoryStageExactBytes(bool zip64)
    {
        var bytes = Container(WorkspaceBackupBundle.DatabaseEntry, WorkspaceBackupBundle.ManifestEntry);
        if (zip64) bytes = WithZip64Directory(bytes);
        var directory = Directory.CreateTempSubdirectory("snook-input-");
        try
        {
            await using var document = new NonSeekableDocument(bytes);
            var database = await WorkspaceBackupBundle.StageAsync(document, directory.FullName);
            Assert.Equal(new byte[100], await File.ReadAllBytesAsync(database));
            Assert.Equal(new byte[1], await File.ReadAllBytesAsync(database + ".manifest.json"));
            Assert.True(document.CanRead);
            await Assert.ThrowsAsync<IOException>(() => WorkspaceBackupBundle.StageAsync(document, directory.FullName));
        }
        finally { directory.Delete(true); }
    }

    [Theory]
    [InlineData("../workspace.db", "workspace.db.manifest.json", 1)]
    [InlineData("workspace.db", "workspace.db", 1)]
    [InlineData("workspace.db", "workspace.db.manifest.json/", 1)]
    [InlineData("workspace.db", "workspace.db.manifest.json", 16385)]
    public async Task InvalidEntriesAreRejectedBeforeExtraction(string first, string second, int manifestSize)
    {
        var directory = Directory.CreateTempSubdirectory("snook-input-invalid-");
        try
        {
            await using var document = new NonSeekableDocument(Container(first, second, manifestSize));
            await Assert.ThrowsAsync<InvalidDataException>(() => WorkspaceBackupBundle.StageAsync(document, directory.FullName));
            Assert.Single(directory.GetFiles()); // Only the private incoming container, never extracted paths.
        }
        finally { directory.Delete(true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidDirectoryBudgetAndCancelledInputDoNotExtract(bool cancelled)
    {
        var bytes = Container(WorkspaceBackupBundle.DatabaseEntry, WorkspaceBackupBundle.ManifestEntry);
        // Forge a central-directory size above the pre-allocation budget.
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(bytes.Length - 10, 4), 32769);
        var directory = Directory.CreateTempSubdirectory("snook-input-budget-");
        try
        {
            await using var document = new NonSeekableDocument(bytes);
            using var cancellation = new CancellationTokenSource();
            if (cancelled) cancellation.Cancel();
            if (cancelled)
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WorkspaceBackupBundle.StageAsync(document, directory.FullName, cancellation.Token));
            else
                await Assert.ThrowsAsync<InvalidDataException>(() => WorkspaceBackupBundle.StageAsync(document, directory.FullName));
            Assert.Single(directory.GetFiles());
        }
        finally { directory.Delete(true); }
    }

    private static byte[] Container(string first, string second, int manifestSize = 1)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var database = archive.CreateEntry(first, CompressionLevel.SmallestSize).Open()) database.Write(new byte[100]);
            using (var manifest = archive.CreateEntry(second, CompressionLevel.SmallestSize).Open()) manifest.Write(new byte[manifestSize]);
        }
        return output.ToArray();
    }

    private static byte[] WithZip64Directory(byte[] original)
    {
        var end = original.Length - 22;
        var size = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(original.AsSpan(end + 12, 4));
        var offset = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(original.AsSpan(end + 16, 4));
        using var result = new MemoryStream();
        using var writer = new BinaryWriter(result);
        writer.Write(original, 0, end);
        writer.Write(0x06064b50U); writer.Write(44UL);
        writer.Write((ushort)45); writer.Write((ushort)45);
        writer.Write(0U); writer.Write(0U); writer.Write(2UL); writer.Write(2UL);
        writer.Write((ulong)size); writer.Write((ulong)offset);
        writer.Write(0x07064b50U); writer.Write(0U); writer.Write((ulong)end); writer.Write(1U);
        writer.Write(0x06054b50U); writer.Write((ushort)0); writer.Write((ushort)0);
        writer.Write(ushort.MaxValue); writer.Write(ushort.MaxValue);
        writer.Write(uint.MaxValue); writer.Write(uint.MaxValue); writer.Write((ushort)0);
        return result.ToArray();
    }

    private sealed class NonSeekableDocument(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => base.ReadAsync(buffer[..Math.Min(buffer.Length, 37)], cancellationToken);
    }

    [Fact]
    public async Task DocumentExportsUsePlatformActionsAndCaptureTheCsvRangeOnce()
    {
        var directory = Directory.CreateTempSubdirectory("snook-document-export-");
        try
        {
            await using var backend = new SnookBackend(new SqliteStore(Path.Combine(directory.FullName, "live.db")));
            await backend.InitializeAsync();
            var clock = new ExportClock();
            await using var model = new MainWindowViewModel(backend, clock, ownsBackend: false);
            await model.InitializeAsync();
            Assert.Equal("Restore latest backup", model.RestoreActionLabel);
            var reviewCalls = 0;
            model.BackupReviewAsync = () => { reviewCalls++; return Task.FromResult("Review only; workspace unchanged"); };
            Assert.Equal("Review backup to restore", model.RestoreActionLabel);
            await ((AsyncCommand)model.RestoreCommand).ExecuteAsync(null);
            Assert.Equal(1, reviewCalls);
            Assert.Equal("Review only; workspace unchanged", model.StatusMessage);
            var jsonCalls = 0;
            model.JsonDocumentExportAsync = () => { jsonCalls++; return Task.FromResult("JSON picker cancelled"); };
            await ((AsyncCommand)model.ExportCommand).ExecuteAsync(null);
            Assert.Equal(1, jsonCalls);
            Assert.Equal("JSON picker cancelled", model.StatusMessage);
            (DateTimeOffset Start, DateTimeOffset End)? range = null;
            model.CsvDocumentExportAsync = (start, end) =>
            {
                range = (start, end);
                return Task.FromResult("CSV read-back verified");
            };
            await ((AsyncCommand)model.ExportCsvCommand).ExecuteAsync(null);
            Assert.Equal((clock.GetUtcNow().AddDays(-30), clock.GetUtcNow().AddDays(1)), range);
            Assert.Equal("CSV read-back verified", model.StatusMessage);
        }
        finally { directory.Delete(true); }
    }

    private sealed class ExportClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
    }

    [Fact]
    public async Task BundleRetainsExactManifestAndRestoresThroughExistingVerifiedPath()
    {
        var directory = Directory.CreateTempSubdirectory("snook-backup-bundle-");
        try
        {
            await using var backend = new SnookBackend(new SqliteStore(Path.Combine(directory.FullName, "live.db")));
            await backend.InitializeAsync();
            var project = (await backend.GetBootstrapAsync()).Projects[0];
            await backend.CreateTaskAsync(project.Id, "Before backup");
            var stage = Directory.CreateDirectory(Path.Combine(directory.FullName, "stage")).FullName;
            var bundle = await WorkspaceBackupBundle.CreateAsync(backend, stage);
            await backend.CreateTaskAsync(project.Id, "After backup");
            var restored = Directory.CreateDirectory(Path.Combine(directory.FullName, "restore")).FullName;
            await using (var document = File.OpenRead(bundle))
                Assert.Equal(Path.Combine(restored, WorkspaceBackupBundle.DatabaseEntry),
                    await WorkspaceBackupBundle.StageAsync(document, restored));
            Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(stage, WorkspaceBackupBundle.ManifestEntry)),
                await File.ReadAllBytesAsync(Path.Combine(restored, WorkspaceBackupBundle.ManifestEntry)));
            var restorePath = Path.Combine(restored, WorkspaceBackupBundle.DatabaseEntry);
            var originalDatabase = await File.ReadAllBytesAsync(restorePath);
            var corruptDatabase = originalDatabase.ToArray();
            corruptDatabase[^1] ^= 1;
            await File.WriteAllBytesAsync(restorePath, corruptDatabase);
            await Assert.ThrowsAsync<SnookException>(() => backend.RestoreBackupAsync(restorePath));
            Assert.Single(await backend.SearchTasksAsync("After backup"));
            await File.WriteAllBytesAsync(restorePath, originalDatabase);
            await backend.RestoreBackupAsync(restorePath);
            Assert.Single(await backend.SearchTasksAsync("Before backup"));
            Assert.Empty(await backend.SearchTasksAsync("After backup"));
            await using var model = new MainWindowViewModel(backend, ownsBackend: false);
            await model.InitializeAsync();
            model.BackupExportAsync = () => Task.FromResult("Native picker cancelled");
            await ((AsyncCommand)model.BackupCommand).ExecuteAsync(null);
            Assert.Equal("Native picker cancelled", model.StatusMessage);
        }
        finally { directory.Delete(true); }
    }
}
