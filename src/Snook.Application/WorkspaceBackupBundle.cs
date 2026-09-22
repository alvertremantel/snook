using System.IO.Compression;
using Snook.Contracts;

namespace Snook.Application;

/// <summary>Packages an existing verified backup and its exact manifest for document providers.</summary>
public static class WorkspaceBackupBundle
{
    public const long MaximumDatabaseBytes = 16L * 1024 * 1024 * 1024;
    public const long MaximumBundleBytes = MaximumDatabaseBytes + 65536;
    public const string DatabaseEntry = "workspace.db";
    public const string ManifestEntry = "workspace.db.manifest.json";

    /// <summary>
    /// Copies an untrusted, possibly non-seekable document into an empty caller-owned private
    /// directory and extracts only the two fixed backup files. This validates the container,
    /// not the backup: callers must still use the backend's verified restore path.
    /// The caller owns cleanup, including partial files on cancellation or failure.
    /// </summary>
    public static async Task<string> StageAsync(Stream document, string stagingDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        var root = Path.GetFullPath(stagingDirectory);
        if (!Directory.Exists(root) || Directory.EnumerateFileSystemEntries(root).Any())
            throw new IOException("An empty private staging directory is required.");
        var bundle = Path.Combine(root, "incoming.snook-backup");
        await using var input = new FileStream(bundle, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        await BoundedStreamTransfer.CopyAsync(document, input, MaximumBundleBytes, cancellationToken).ConfigureAwait(false);
        await input.FlushAsync(cancellationToken).ConfigureAwait(false);
        ValidateDirectoryBudget(input);
        input.Position = 0;
        using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
        if (archive.Entries.Count != 2
            || archive.Entries.Count(entry => entry.FullName == DatabaseEntry) != 1
            || archive.Entries.Count(entry => entry.FullName == ManifestEntry) != 1)
            throw new InvalidDataException("Backup must contain exactly the database and its original manifest.");
        foreach (var entry in archive.Entries)
        {
            var limit = entry.FullName == DatabaseEntry ? MaximumDatabaseBytes : 16384;
            var minimum = entry.FullName == DatabaseEntry ? 100 : 1;
            if (entry.Length < minimum || entry.Length > limit)
                throw new InvalidDataException("Backup entry size is invalid.");
        }
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var source = entry.Open();
            // Names have already been matched exactly; archive paths never select a destination.
            var name = entry.FullName == DatabaseEntry ? DatabaseEntry : ManifestEntry;
            await using var destination = new FileStream(Path.Combine(root, name), FileMode.CreateNew,
                FileAccess.Write, FileShare.None);
            var copied = await BoundedStreamTransfer.CopyAsync(source, destination, entry.Length, cancellationToken).ConfigureAwait(false);
            if (copied != entry.Length) throw new InvalidDataException("Backup entry is truncated.");
        }
        return Path.Combine(root, DatabaseEntry);
    }

    // Gate the central directory before ZipArchive materializes entries. A byte limit on the
    // complete document alone would still permit millions of tiny entries to exhaust memory.
    // Snook bundles have no archive comment or multi-disk records. ZIP64 is needed above 4 GiB.
    private static void ValidateDirectoryBudget(Stream input)
    {
        if (input.Length < 22) throw new InvalidDataException("Backup container is truncated.");
        using var reader = new BinaryReader(input, System.Text.Encoding.UTF8, leaveOpen: true);
        input.Position = input.Length - 22;
        if (reader.ReadUInt32() != 0x06054b50 || reader.ReadUInt16() != 0 || reader.ReadUInt16() != 0)
            throw new InvalidDataException("Unsupported backup container.");
        var diskEntries = reader.ReadUInt16();
        var entries = reader.ReadUInt16();
        ulong size = reader.ReadUInt32();
        ulong offset = reader.ReadUInt32();
        if (reader.ReadUInt16() != 0 || diskEntries != entries)
            throw new InvalidDataException("Unsupported backup directory.");
        var directoryEnd = input.Length - 22;
        if (entries == ushort.MaxValue || size == uint.MaxValue || offset == uint.MaxValue)
        {
            if (input.Length < 98) throw new InvalidDataException("ZIP64 backup is truncated.");
            input.Position = input.Length - 42;
            if (reader.ReadUInt32() != 0x07064b50 || reader.ReadUInt32() != 0)
                throw new InvalidDataException("Unsupported ZIP64 locator.");
            var zip64Offset = reader.ReadUInt64();
            if (reader.ReadUInt32() != 1 || zip64Offset != (ulong)(input.Length - 98))
                throw new InvalidDataException("Unsupported ZIP64 layout.");
            input.Position = (long)zip64Offset;
            if (reader.ReadUInt32() != 0x06064b50 || reader.ReadUInt64() != 44)
                throw new InvalidDataException("Unsupported ZIP64 directory.");
            _ = reader.ReadUInt16(); // Version made by.
            _ = reader.ReadUInt16(); // Version needed.
            if (reader.ReadUInt32() != 0 || reader.ReadUInt32() != 0
                || reader.ReadUInt64() != 2 || reader.ReadUInt64() != 2)
                throw new InvalidDataException("Backup requires two entries on one disk.");
            size = reader.ReadUInt64();
            offset = reader.ReadUInt64();
            directoryEnd = (long)zip64Offset;
        }
        else if (entries != 2) throw new InvalidDataException("Backup requires exactly two entries.");
        if (size > 32768 || offset > (ulong)directoryEnd || size != (ulong)directoryEnd - offset)
            throw new InvalidDataException("Backup directory exceeds its budget or has an invalid location.");
    }

    /// <summary>The caller owns an empty private staging directory and its cleanup.</summary>
    public static async Task<string> CreateAsync(IBackendClient backend, string stagingDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(backend);
        var database = Path.Combine(Path.GetFullPath(stagingDirectory), DatabaseEntry);
        var manifest = database + ".manifest.json";
        var backup = await backend.CreateBackupAsync(database, cancellationToken).ConfigureAwait(false);
        if (backup.Path != database || backup.ManifestPath != manifest || backup.Bytes > MaximumDatabaseBytes)
            throw new InvalidDataException("The backend did not return the requested local backup.");
        var bundle = Path.Combine(stagingDirectory, "workspace.snook-backup");
        await using (var output = new FileStream(bundle, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
            await AddEntryAsync(archive, database, DatabaseEntry, MaximumDatabaseBytes, cancellationToken).ConfigureAwait(false);
            await AddEntryAsync(archive, manifest, ManifestEntry, 16384, cancellationToken).ConfigureAwait(false);
        }
        if (new FileInfo(bundle).Length > MaximumBundleBytes) throw new InvalidDataException("Backup bundle is too large.");
        return bundle;
    }

    private static async Task AddEntryAsync(ZipArchive archive, string path, string name, long limit, CancellationToken cancellationToken)
    {
        await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        await using var destination = archive.CreateEntry(name, CompressionLevel.NoCompression).Open();
        await BoundedStreamTransfer.CopyAsync(source, destination, limit, cancellationToken).ConfigureAwait(false);
    }
}
