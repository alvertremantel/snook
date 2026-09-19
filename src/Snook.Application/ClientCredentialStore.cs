using Snook.Domain;

namespace Snook.Application;

/// <summary>Device-local secret storage. Profiles contain only returned references.</summary>
public interface IClientCredentialStore
{
    Task<string> StoreAsync(string credential, string label, CancellationToken cancellationToken = default);
    Task<string> RetrieveAsync(string reference, CancellationToken cancellationToken = default);
    Task DeleteAsync(string reference, CancellationToken cancellationToken = default);
}

/// <summary>
/// Explicit owner-only file fallback for automation and Linux installations without
/// Secret Service. It is never selected implicitly while creating a credential.
/// </summary>
public sealed class OwnerOnlyFileCredentialStore : IClientCredentialStore
{
    private const int MaximumCredentialBytes = 1024;
    private readonly string _directory;

    public OwnerOnlyFileCredentialStore(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        if (!Path.IsPathFullyQualified(dataDirectory)) throw Invalid();
        _directory = Path.Combine(Path.GetFullPath(dataDirectory), "Snook", "client-credentials");
    }

    public async Task<string> StoreAsync(string credential, string label, CancellationToken cancellationToken = default)
    {
        credential = NormalizeCredential(credential);
        if (string.IsNullOrWhiteSpace(label) || label.Trim().Length > 100 || label.Any(char.IsControl)) throw Invalid();
        EnsureDirectory();
        var id = Guid.NewGuid();
        var path = PathFor(id);
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.Asynchronous | FileOptions.WriteThrough
        };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        var completed = false;
        try
        {
            await using var stream = new FileStream(path, options);
            await using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
            await writer.WriteAsync(credential.AsMemory(), cancellationToken);
            await writer.FlushAsync(cancellationToken);
            stream.Flush(flushToDisk: true);
            completed = true;
            return $"owner-file:{id:N}";
        }
        finally
        {
            if (!completed)
            {
                try { File.Delete(path); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    public async Task<string> RetrieveAsync(string reference, CancellationToken cancellationToken = default)
    {
        try
        {
            var path = Resolve(reference);
            CheckFile(path);
            if (new FileInfo(path).Length is <= 0 or > MaximumCredentialBytes) throw Invalid();
            return NormalizeCredential(await File.ReadAllTextAsync(path, cancellationToken));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new SnookException(SnookErrorCode.StoreUnavailable,
                "The protected device credential is missing or cannot be read safely. Re-pair this device; SQLite was not opened.", exception);
        }
    }

    public Task DeleteAsync(string reference, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = Resolve(reference);
        if (File.Exists(path))
        {
            CheckFile(path);
            File.Delete(path);
        }
        return Task.CompletedTask;
    }

    private void EnsureDirectory()
    {
        var parent = Path.GetDirectoryName(_directory)!;
        CheckAncestors(parent);
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(_directory);
        else Directory.CreateDirectory(_directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        CheckAncestors(_directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private string Resolve(string reference)
    {
        if (!reference.StartsWith("owner-file:", StringComparison.Ordinal)
            || !Guid.TryParseExact(reference[11..], "N", out var id) || id == Guid.Empty) throw Invalid();
        CheckAncestors(_directory);
        return PathFor(id);
    }

    private string PathFor(Guid id) => Path.Combine(_directory, id.ToString("N") + ".token");

    private static string NormalizeCredential(string credential)
    {
        var value = credential.Trim('\uFEFF', '\r', '\n', ' ');
        if (value.Length is < 32 or > 512 || value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
            throw Invalid();
        return value;
    }

    private static void CheckAncestors(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
            if (File.Exists(current) || Directory.Exists(current))
            {
                var attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw Invalid();
            }
    }

    private static void CheckFile(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0
            || !OperatingSystem.IsWindows() && (File.GetUnixFileMode(path) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite
                | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
            throw Invalid();
    }

    private static SnookException Invalid() => new(SnookErrorCode.ValidationFailed,
        "The protected credential reference or owner-only credential file is invalid or unsafe.");
}
