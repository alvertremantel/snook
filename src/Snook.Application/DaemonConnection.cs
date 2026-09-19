using System.Globalization;
using System.Text.Json;
using Snook.Domain;

namespace Snook.Application;

/// <summary>Shared desktop/CLI connection resolution. Never opens a workspace.</summary>
public static class DaemonConnection
{
    public static async Task<DaemonBackendClient> ConnectAsync(string dataRoot, string? endpoint = null,
        string? tokenFile = null, string? token = null, CancellationToken cancellationToken = default)
        => await ConnectCoreAsync(dataRoot, endpoint, tokenFile, token, true, cancellationToken);

    public static Task<DaemonBackendClient> ConnectProfileAsync(ClientConnectionProfile profile, string? token = null,
        CancellationToken cancellationToken = default)
        => ConnectCoreAsync(profile.DataDirectory, profile.Endpoint, profile.TokenFile, token, false, cancellationToken);

    private static async Task<DaemonBackendClient> ConnectCoreAsync(string dataRoot, string? endpoint,
        string? tokenFile, string? token, bool readEnvironment, CancellationToken cancellationToken)
    {
        if (readEnvironment) endpoint ??= Environment.GetEnvironmentVariable("SNOOK_DAEMON_ENDPOINT");
        if (endpoint is null)
        {
            var configuredPort = readEnvironment ? Environment.GetEnvironmentVariable("SNOOK_DAEMON_PORT") : null;
            var port = 43871;
            if (configuredPort is not null && (!int.TryParse(configuredPort, CultureInfo.InvariantCulture, out port) || port is < 1024 or > 65535))
                throw new SnookException(SnookErrorCode.ValidationFailed, "SNOOK_DAEMON_PORT must be between 1024 and 65535.");
            var descriptorPath = Path.Combine(dataRoot, "Snook", "daemon.endpoint.json");
            if (configuredPort is null && File.Exists(descriptorPath))
            {
                try
                {
                    var info = new FileInfo(descriptorPath);
                    if (info.Length > 8192 || info.LinkTarget is not null
                        || (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(descriptorPath)
                            & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                                | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0))
                        throw new SnookException(SnookErrorCode.StoreUnavailable, "Daemon endpoint descriptor must be a small private regular file.");
                    var descriptor = JsonSerializer.Deserialize<DaemonEndpointDescriptor>(await File.ReadAllTextAsync(descriptorPath, cancellationToken));
                    if (descriptor is null || descriptor.Version != 1 || descriptor.InstanceId == Guid.Empty
                        || string.IsNullOrWhiteSpace(descriptor.Endpoint))
                        throw new JsonException("Invalid endpoint descriptor.");
                    endpoint = descriptor.Endpoint;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
                {
                    throw new SnookException(SnookErrorCode.StoreUnavailable, "Cannot read the daemon endpoint descriptor. Restart snookd or select an explicit --endpoint.", exception);
                }
            }
            else endpoint = $"http://127.0.0.1:{port}/";
        }
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
            throw new SnookException(SnookErrorCode.ValidationFailed, "Daemon endpoint must be an absolute HTTP loopback URL.");

        // An explicitly selected token file takes precedence over the legacy token environment variable.
        if (readEnvironment) tokenFile ??= Environment.GetEnvironmentVariable("SNOOK_DAEMON_TOKEN_FILE");
        if (readEnvironment && token is null && tokenFile is null) token = Environment.GetEnvironmentVariable("SNOOK_DAEMON_TOKEN");
        if (string.IsNullOrWhiteSpace(token))
        {
            tokenFile ??= Path.Combine(dataRoot, "Snook", "daemon.token");
            try
            {
                if (new FileInfo(tokenFile).Length > 1024)
                    throw new SnookException(SnookErrorCode.ValidationFailed, "Daemon token file is too large.");
                token = (await File.ReadAllTextAsync(tokenFile, cancellationToken)).Trim('\uFEFF', '\r', '\n', ' ');
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new SnookException(SnookErrorCode.StoreUnavailable,
                    "Cannot read the daemon token file. Start snookd for this data directory or select --token-file. SQLite will not be opened in daemon mode.", exception);
            }
        }
        return new DaemonBackendClient(uri, token);
    }
}

/// <summary>Private host discovery metadata; contains no credential or workspace content.</summary>
public sealed record DaemonEndpointDescriptor(int Version, Guid InstanceId, string Endpoint, int ContractMajor, int ContractMinor);
