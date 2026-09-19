using System.Text.Json;
using Snook.Domain;

namespace Snook.Persistence.Sqlite;

public sealed partial class SqliteStore
{
    private const long MaximumBackupBytes = 16L * 1024 * 1024 * 1024;
    private const int MaximumManifestBytes = 16 * 1024;

    private async Task<VerifiedRestoreInput> StageVerifiedBackupAsync(string sourcePath, CancellationToken cancellationToken)
    {
        ValidateBackupSourceFile(sourcePath, minimumBytes: 100, MaximumBackupBytes);
        foreach (var suffix in new[] { "-wal", "-shm", "-journal", ".owner" })
            if (ExistingAttributes(sourcePath + suffix) is not null)
                throw InvalidOutput("Restore requires a standalone verified backup, not a database with live or recovery sidecars. Create a new online backup from its owning host.");
        var manifestPath = sourcePath + ".manifest.json";
        if (ExistingAttributes(manifestPath) is null)
            throw InvalidBackup("The backup manifest is missing. Restore requires the matching .manifest.json file; do not recreate a manifest for an unverified or partial file.");
        ValidateBackupSourceFile(manifestPath, minimumBytes: 1, MaximumManifestBytes);
        var manifest = await ReadBackupManifestAsync(manifestPath, cancellationToken);
        await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 64 * 1024, useAsync: true);
        if (source.Length != manifest.Bytes)
            throw InvalidBackup("The backup byte count does not match its manifest. The current workspace was not replaced.");

        var directory = Path.Combine(Path.GetDirectoryName(_databasePath)!, $".snook-restore-{Guid.NewGuid():N}");
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
        else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var staged = new VerifiedRestoreInput(directory, manifest.CreatedAtUtc);
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan, BufferSize = 64 * 1024 };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var destination = new FileStream(staged.Path, options))
            {
                var buffer = new byte[64 * 1024];
                var remaining = manifest.Bytes;
                while (remaining > 0)
                {
                    var count = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken);
                    if (count == 0) throw InvalidBackup("The backup was truncated while being read. The current workspace was not replaced.");
                    await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                    remaining -= count;
                }
                if (await source.ReadAsync(buffer.AsMemory(0, 1), cancellationToken) != 0)
                    throw InvalidBackup("The backup changed size while being read. The current workspace was not replaced.");
            }
            if (!(await HashFileAsync(staged.Path, cancellationToken)).Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase))
                throw InvalidBackup("The backup SHA-256 does not match its manifest. The current workspace was not replaced.");
            return staged;
        }
        catch
        {
            staged.Dispose();
            throw;
        }
    }

    private static void ValidateBackupSourceFile(string path, long minimumBytes, long maximumBytes)
    {
        var attributes = ExistingAttributes(path);
        if (attributes is null) throw new SnookException(SnookErrorCode.NotFound, "The selected backup file was not found.");
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
            throw InvalidOutput("Backup and manifest inputs must be ordinary files, not directories, devices or symbolic links.");
        var length = new FileInfo(path).Length;
        if (length < minimumBytes || length > maximumBytes)
            throw InvalidBackup("The backup or manifest size is invalid. Backups are limited to 16 GiB and manifests to 16 KiB.");
    }

    private static async Task<VerifiedBackupManifest> ReadBackupManifestAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: MaximumManifestBytes, useAsync: true);
        var buffer = new byte[MaximumManifestBytes + 1];
        var count = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken);
        if (count > MaximumManifestBytes) throw InvalidBackup("The backup manifest exceeds 16 KiB.");
        try
        {
            using var document = JsonDocument.Parse(buffer.AsMemory(0, count), new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new JsonException();
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
                if (!names.Add(property.Name)) throw new JsonException();
            if (!root.TryGetProperty("formatVersion", out var version) || !version.TryGetInt32(out var format) || format != 1
                || !root.TryGetProperty("bytes", out var bytes) || !bytes.TryGetInt64(out var size) || size is < 100 or > MaximumBackupBytes
                || !root.TryGetProperty("createdAtUtc", out var created) || !created.TryGetDateTimeOffset(out var when) || when.Offset != TimeSpan.Zero
                || !root.TryGetProperty("databasePath", out var original) || original.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(original.GetString()) || original.GetString()!.Length > 4096
                || !root.TryGetProperty("integrity", out var integrity) || integrity.GetString() != "sqlite-backup-verified"
                || !root.TryGetProperty("sha256", out var hash) || hash.ValueKind != JsonValueKind.String
                || hash.GetString() is not { Length: 64 } hashText || !hashText.All(char.IsAsciiHexDigit))
                throw new JsonException();
            // The original databasePath is informational: valid pairs may be moved
            // across directories/platforms. Never use it as an input/output path.
            return new VerifiedBackupManifest(size, hashText, DateTimeOffset.FromUnixTimeMilliseconds(when.ToUnixTimeMilliseconds()));
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
        {
            throw new SnookException(SnookErrorCode.SchemaIncompatible,
                "The backup manifest is malformed, incomplete or unsupported. Restore requires a format-1 verified backup manifest.", exception);
        }
    }

    private static SnookException InvalidBackup(string message) => new(SnookErrorCode.SchemaIncompatible, message);
    private sealed record VerifiedBackupManifest(long Bytes, string Sha256, DateTimeOffset CreatedAtUtc);

    private sealed class VerifiedRestoreInput(string directory, DateTimeOffset backupCreatedAtUtc) : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(directory, "workspace.db");
        public DateTimeOffset BackupCreatedAtUtc { get; } = backupCreatedAtUtc;
        public void Dispose()
        {
            try
            {
                foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" }) File.Delete(Path + suffix);
                Directory.Delete(directory);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
