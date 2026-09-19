using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Snook.Contracts;
using Snook.Domain;
using Snook.Persistence.Sqlite;

namespace Snook.Application;

// Device-local client configuration, never a workspace/export or wire DTO.
public sealed record ClientConnectionProfile(
    int Version,
    string Host,
    string DataDirectory,
    string? Endpoint = null,
    string? TokenFile = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TransportKind = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CredentialReference = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? ExpectedServerId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? ExpectedWorkspaceId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? FriendlyName = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? ContractMajor = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? ContractMinor = null);
public sealed record ClientProfileSnapshot(ClientConnectionProfile? Profile, string? Stamp);

public sealed class ClientProfileStore
{
    public const string FileName = "client-profile.json";
    private const int MaximumBytes = 16 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    public string ProfilePath { get; }

    public ClientProfileStore(string launchDataDirectory)
        => ProfilePath = Path.Combine(ValidateDirectory(launchDataDirectory), "Snook", FileName);

    public async Task<ClientProfileSnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            ValidateAncestors(Path.GetDirectoryName(ProfilePath)!);
            if (!Exists(ProfilePath)) return new(null, null);
            CheckFile(ProfilePath);
            if (new FileInfo(ProfilePath).Length is <= 0 or > MaximumBytes) throw InvalidProfile();
            await using var file = new FileStream(ProfilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
            var bytes = new byte[MaximumBytes + 1];
            var length = 0;
            while (length < bytes.Length)
            {
                var count = await file.ReadAsync(bytes.AsMemory(length), cancellationToken);
                if (count == 0) break;
                length += count;
            }
            if (length > MaximumBytes) throw InvalidProfile();
            using var json = JsonDocument.Parse(bytes.AsMemory(0, length));
            if (json.RootElement.ValueKind != JsonValueKind.Object
                || json.RootElement.EnumerateObject().Select(item => item.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count()
                    != json.RootElement.EnumerateObject().Count()) throw InvalidProfile();
            var profile = JsonSerializer.Deserialize<ClientConnectionProfile>(bytes.AsSpan(0, length), JsonOptions) ?? throw InvalidProfile();
            profile = Validate(profile);
            return new(profile, Convert.ToHexString(SHA256.HashData(bytes.AsSpan(0, length))));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new SnookException(SnookErrorCode.StoreUnavailable, "The saved client profile cannot be read safely. Open connection settings or move the invalid profile aside; no workspace was opened.", exception);
        }
    }

    public async Task<ClientProfileSnapshot> SaveAsync(ClientConnectionProfile profile, string? expectedStamp, CancellationToken cancellationToken = default)
    {
        profile = Validate(profile);
        var directory = Path.GetDirectoryName(ProfilePath)!;
        ValidateAncestors(directory);
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
        else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        ValidateAncestors(directory);
        var lockPath = ProfilePath + ".lock";
        if (Exists(lockPath)) CheckFile(lockPath);
        // The lock file stays in place: deleting it would allow another process
        // to lock a new inode while an existing waiter still owns the old one.
        await using var lease = new FileStream(lockPath, PrivateOptions(FileMode.OpenOrCreate));
        var current = await ReadAsync(cancellationToken);
        if (current.Stamp != expectedStamp)
            throw new SnookException(SnookErrorCode.RevisionConflict, "Connection settings changed in another window. Reopen settings before saving; your entered values have not been written.");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(profile, JsonOptions);
        if (bytes.Length > MaximumBytes) throw InvalidProfile();
        var staging = ProfilePath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var file = new FileStream(staging, PrivateOptions(FileMode.CreateNew)))
            {
                await file.WriteAsync(bytes, cancellationToken);
                file.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            ValidateAncestors(directory);
            if (Exists(ProfilePath)) CheckFile(ProfilePath);
            File.Move(staging, ProfilePath, overwrite: true);
            return new(profile, Convert.ToHexString(SHA256.HashData(bytes)));
        }
        finally { if (File.Exists(staging)) File.Delete(staging); }
    }

    public static ClientConnectionProfile Validate(ClientConnectionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.Version is not (1 or 2) || profile.Host is not ("embedded" or "daemon")) throw InvalidProfile();
        var dataDirectory = ValidateDirectory(profile.DataDirectory);
        var endpoint = profile.Host == "embedded" || string.IsNullOrWhiteSpace(profile.Endpoint) ? null : profile.Endpoint.Trim();
        if (endpoint?.Length > 4096) throw InvalidProfile();
        var transportKind = profile.TransportKind?.Trim().ToLowerInvariant();
        if (profile.Version == 1 && (transportKind is not null || profile.CredentialReference is not null
            || profile.ExpectedServerId is not null || profile.ExpectedWorkspaceId is not null
            || profile.FriendlyName is not null || profile.ContractMajor is not null || profile.ContractMinor is not null))
            throw InvalidProfile();
        if (profile.Version == 2)
        {
            if (profile.Host == "embedded") throw InvalidProfile();
            else if (transportKind is not ("local-loopback" or "tailscale-serve")) throw InvalidProfile();
        }
        if (endpoint is not null)
        {
            var required = transportKind switch
            {
                "local-loopback" => DaemonEndpointKind.LocalLoopback,
                "tailscale-serve" => DaemonEndpointKind.TailscaleServe,
                _ when profile.Version == 1 => DaemonEndpointKind.LocalLoopback,
                _ => (DaemonEndpointKind?)null
            };
            endpoint = DaemonEndpoint.Parse(endpoint, required).Origin.AbsoluteUri;
        }
        if (profile.Version == 2 && transportKind == "tailscale-serve" && endpoint is null) throw InvalidProfile();
        var tokenFile = profile.Host == "embedded" || string.IsNullOrWhiteSpace(profile.TokenFile) ? null : profile.TokenFile.Trim();
        if (tokenFile is not null && (!Path.IsPathFullyQualified(tokenFile) || tokenFile.Length > 4096))
            throw new SnookException(SnookErrorCode.ValidationFailed, "The token file must be an absolute path of at most 4096 characters.");
        var credentialReference = Normalize(profile.CredentialReference, 256);
        var friendlyName = Normalize(profile.FriendlyName, 100);
        if (transportKind != "tailscale-serve" && (credentialReference is not null
            || profile.ExpectedServerId is not null || profile.ExpectedWorkspaceId is not null
            || friendlyName is not null || profile.ContractMajor is not null || profile.ContractMinor is not null))
            throw InvalidProfile();
        if (transportKind == "tailscale-serve" && (tokenFile is null) == (credentialReference is null))
            throw new SnookException(SnookErrorCode.ValidationFailed, "A tailnet daemon profile requires exactly one protected credential reference or explicit owner-only token file.");
        if (transportKind == "tailscale-serve" && (profile.ExpectedServerId is null || profile.ExpectedServerId == Guid.Empty
            || profile.ExpectedWorkspaceId is null || profile.ExpectedWorkspaceId == Guid.Empty)) throw InvalidProfile();
        if (transportKind == "tailscale-serve"
            && (profile.ContractMajor is null) != (profile.ContractMinor is null)) throw InvalidProfile();
        if (profile.ExpectedServerId == Guid.Empty || profile.ExpectedWorkspaceId == Guid.Empty
            || profile.ContractMajor is < 0 || profile.ContractMinor is < 0) throw InvalidProfile();
        return profile with { DataDirectory = dataDirectory, Endpoint = endpoint, TokenFile = tokenFile,
            TransportKind = transportKind, CredentialReference = credentialReference, FriendlyName = friendlyName };
    }

    public static string LaunchDataDirectory(string? selected = null)
        => ValidateDirectory(selected ?? Environment.GetEnvironmentVariable("SNOOK_DATA_DIR")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    public static ClientConnectionProfile Resolve(ClientConnectionProfile? saved, string launchDataDirectory,
        string? host = null, string? dataDirectory = null, string? endpoint = null, string? tokenFile = null,
        Func<string, string?>? environment = null, string? transportKind = null, string? credentialReference = null,
        Guid? expectedServerId = null, Guid? expectedWorkspaceId = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        var selectedHost = (host ?? environment("SNOOK_HOST_MODE") ?? saved?.Host ?? "embedded").ToLowerInvariant();
        var environmentEndpoint = environment("SNOOK_DAEMON_ENDPOINT");
        if (selectedHost == "daemon" && endpoint is null && environmentEndpoint is null && environment("SNOOK_DAEMON_PORT") is { } portText)
        {
            if (!int.TryParse(portText, System.Globalization.CultureInfo.InvariantCulture, out var port) || port is < 1024 or > 65535)
                throw new SnookException(SnookErrorCode.ValidationFailed, "SNOOK_DAEMON_PORT must be between 1024 and 65535.");
            environmentEndpoint = $"http://127.0.0.1:{port}/";
        }
        var environmentTokenFile = environment("SNOOK_DAEMON_TOKEN_FILE");
        var selectedTransport = selectedHost == "daemon"
            ? transportKind?.Trim().ToLowerInvariant() ?? saved?.TransportKind
            : null;
        var version = selectedHost == "daemon" && selectedTransport is not null ? 2 : 1;
        var selectedTokenFile = tokenFile ?? environmentTokenFile ?? saved?.TokenFile;
        var selectedCredentialReference = selectedTransport == "tailscale-serve"
            ? credentialReference ?? (tokenFile is null && environmentTokenFile is null ? saved?.CredentialReference : null)
            : null;
        if (selectedTransport == "tailscale-serve" && credentialReference is not null
            && tokenFile is null && environmentTokenFile is null) selectedTokenFile = null;
        return Validate(new ClientConnectionProfile(version,
            selectedHost,
            dataDirectory ?? environment("SNOOK_DATA_DIR") ?? saved?.DataDirectory ?? launchDataDirectory,
            endpoint ?? environmentEndpoint ?? saved?.Endpoint,
            selectedTokenFile,
            selectedTransport,
            selectedCredentialReference,
            selectedTransport == "tailscale-serve" ? expectedServerId ?? saved?.ExpectedServerId : null,
            selectedTransport == "tailscale-serve" ? expectedWorkspaceId ?? saved?.ExpectedWorkspaceId : null,
            selectedTransport == "tailscale-serve" ? saved?.FriendlyName : null,
            selectedTransport == "tailscale-serve" ? saved?.ContractMajor : null,
            selectedTransport == "tailscale-serve" ? saved?.ContractMinor : null));
    }

    public static async Task<IBackendClient> OpenAsync(ClientConnectionProfile profile, string? token = null,
        bool? allowConcurrentForeground = null, CancellationToken cancellationToken = default)
    {
        profile = Validate(profile);
        if (profile.Host == "daemon")
            return await DaemonConnection.ConnectProfileAsync(profile, token, cancellationToken);
        var backend = new SnookBackend(new SqliteStore(Path.Combine(profile.DataDirectory, "Snook", "workspace.db")),
            allowConcurrentForeground: allowConcurrentForeground);
        try { await backend.InitializeAsync(cancellationToken); return backend; }
        catch { await backend.DisposeAsync(); throw; }
    }

    private static string ValidateDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.Length > 4096)
            throw new SnookException(SnookErrorCode.ValidationFailed, "The data directory must be an absolute path of at most 4096 characters.");
        return Path.GetFullPath(path);
    }

    private static string? Normalize(string? value, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();
        return value.Length <= maximum && !value.Any(char.IsControl) ? value : throw InvalidProfile();
    }

    private static SnookException InvalidProfile() => new(SnookErrorCode.ValidationFailed,
        "The saved connection must be a bounded version-1 local profile or version-2 local-loopback/tailscale-serve profile. Credentials cannot be stored in a profile.");

    private static bool Exists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static void ValidateAncestors(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
            if (Exists(current) && (File.GetAttributes(current) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory)
                throw new SnookException(SnookErrorCode.ValidationFailed, "Connection profile directories cannot be links or files.");
    }

    private static void CheckFile(string path)
    {
        if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0
            || (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(path) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0))
            throw new SnookException(SnookErrorCode.ValidationFailed, "Connection profiles must be private regular files, not symbolic links.");
    }

    private static FileStreamOptions PrivateOptions(FileMode mode)
    {
        var options = new FileStreamOptions { Mode = mode, Access = FileAccess.ReadWrite, Share = FileShare.None, Options = FileOptions.Asynchronous };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return options;
    }
}
