using Snook.Domain;

namespace Snook.Persistence.Sqlite;

public sealed partial class SqliteStore
{
    private async Task<T> WriteArtifactAsync<T>(string destinationPath, bool backup,
        Func<MaintenanceOutput, Task<T>> write, CancellationToken cancellationToken)
    {
        EnsureInitialized();
        var destination = ValidateOutputPath(destinationPath);
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            // Keep all export queries in one writer-free interval, including the
            // independently queried journal/habit/correction data. Restore also
            // takes this gate and cannot replace the DB halfway through an export.
            using var output = new MaintenanceOutput(destination, backup);
            var result = await write(output);
            cancellationToken.ThrowIfCancellationRequested();
            output.Publish();
            // Publication is the commit point; do not report cancellation after it.
            return result;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new SnookException(SnookErrorCode.StoreUnavailable,
                "The artifact could not be published. Check destination access and free space. Existing files are never replaced; inspect the destination before choosing a new name.", exception);
        }
        finally { _writeGate.Release(); }
    }

    private string ValidateOutputPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 4096 || !Path.IsPathFullyQualified(path))
            throw InvalidOutput("Use a fully qualified artifact path of at most 4096 characters.");
        try
        {
            var root = Path.GetPathRoot(path)!;
            var components = path[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
            if (components.Length > 128 || components.Any(part => part is "." or ".." || part.IndexOfAny(['\0', ':', '*', '?', '"', '<', '>', '|']) >= 0
                || part.EndsWith(' ') || part.EndsWith('.') || IsDeviceName(part)))
                throw InvalidOutput("Artifact paths cannot contain traversal segments or special file-name characters.");
            var fullPath = Path.GetFullPath(path);
            if (string.IsNullOrEmpty(Path.GetFileName(fullPath))) throw InvalidOutput("Select an artifact file, not a directory.");
            var directory = Path.GetDirectoryName(fullPath)!;
            foreach (var workspacePath in new[] { _databasePath, ResolveWorkspacePath(_databasePath) })
            {
                if (!string.Equals(directory, Path.GetDirectoryName(workspacePath), StringComparison.OrdinalIgnoreCase)) continue;
                var name = Path.GetFileName(fullPath);
                var databaseName = Path.GetFileName(workspacePath);
                if (name.Equals(databaseName, StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith(databaseName + ".", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith(databaseName + "-", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("daemon.", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("client-profile.json", StringComparison.OrdinalIgnoreCase))
                    throw InvalidOutput("Workspace files, SQLite sidecars, ownership files and daemon credentials/discovery are not artifact destinations.");
            }
            ValidateOutputAncestors(directory);
            return fullPath;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new SnookException(SnookErrorCode.ValidationFailed, "The artifact destination path is invalid.", exception);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new SnookException(SnookErrorCode.ValidationFailed, "The artifact destination cannot be inspected safely.", exception);
        }
    }

    private static SnookException InvalidOutput(string message) => new(SnookErrorCode.ValidationFailed, message);

    private static bool IsDeviceName(string component)
    {
        var stem = component.Split('.')[0];
        return stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || stem.Length == 4 && stem[3] is >= '1' and <= '9'
                && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase));
    }

    private static string ResolveWorkspacePath(string path)
    {
        // An embedded host may have been opened through a directory link. Reserve
        // its real sidecar/credential names too, even though output links themselves
        // are forbidden. Resolve each component, not just the final database file.
        var current = Path.GetPathRoot(path)!;
        foreach (var component in path[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            if (ExistingAttributes(current) is { } attributes && (attributes & FileAttributes.ReparsePoint) != 0)
            {
                FileSystemInfo info = (attributes & FileAttributes.Directory) != 0 ? new DirectoryInfo(current) : new FileInfo(current);
                current = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName
                    ?? throw InvalidOutput("The workspace path cannot be resolved safely.");
            }
        }
        return current;
    }

    private static FileAttributes? ExistingAttributes(string path)
    {
        try { return File.GetAttributes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static void ValidateOutputAncestors(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            if (ExistingAttributes(current) is { } attributes
                && ((attributes & FileAttributes.ReparsePoint) != 0 || (attributes & FileAttributes.Directory) == 0))
                throw InvalidOutput("Artifact directories must be ordinary directories, not symbolic links, junctions or files.");
        }
    }

    private sealed class MaintenanceOutput : IDisposable
    {
        private readonly bool _backup;
        private readonly string _stagingDirectory;
        public string Destination { get; }
        public string StagingPath { get; }
        public string ManifestPath => Destination + ".manifest.json";
        public string StagingManifestPath => StagingPath + ".manifest.json";

        public MaintenanceOutput(string destination, bool backup)
        {
            Destination = destination;
            _backup = backup;
            CheckUnused();
            var parent = Path.GetDirectoryName(destination)!;
            ValidateOutputAncestors(parent);
            Directory.CreateDirectory(parent);
            ValidateOutputAncestors(parent);
            _stagingDirectory = Path.Combine(parent, $".snook-artifact-{Guid.NewGuid():N}");
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(_stagingDirectory);
            else Directory.CreateDirectory(_stagingDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            StagingPath = Path.Combine(_stagingDirectory, "artifact");
        }

        private void CheckUnused()
        {
            if (ExistingAttributes(Destination) is not null
                || _backup && new[] { ManifestPath, Destination + "-wal", Destination + "-shm", Destination + "-journal", Destination + ".owner" }
                    .Any(path => ExistingAttributes(path) is not null))
                throw InvalidOutput("The artifact destination or a backup companion already exists. Choose a new, unused file name; existing files are never replaced.");
        }

        public void Publish()
        {
            ValidateOutputAncestors(Path.GetDirectoryName(Destination)!);
            CheckUnused();
            MakePrivateAndFlush(StagingPath);
            if (_backup) MakePrivateAndFlush(StagingManifestPath);
            File.Move(StagingPath, Destination, overwrite: false);
            // A pair cannot be renamed atomically. Publish the manifest last as
            // the completion marker; an interrupted publication may leave a valid
            // DB without its manifest. Never delete/overwrite a final path on error.
            if (_backup) File.Move(StagingManifestPath, ManifestPath, overwrite: false);
        }

        private static void MakePrivateAndFlush(string path)
        {
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
            stream.Flush(flushToDisk: true);
        }

        public void Dispose()
        {
            // Only remove known private staging files, never caller-owned paths.
            // Cleanup failure must not turn a completed publication into failure.
            try
            {
                foreach (var suffix in new[] { "", ".manifest.json", "-wal", "-shm", "-journal" })
                    File.Delete(StagingPath + suffix);
                Directory.Delete(_stagingDirectory);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
