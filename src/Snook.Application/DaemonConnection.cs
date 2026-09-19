using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Snook.Domain;

namespace Snook.Application;

/// <summary>Shared desktop/CLI connection resolution. Never opens a workspace.</summary>
public static class DaemonConnection
{
    private static readonly JsonSerializerOptions DescriptorJsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static async Task<DaemonBackendClient> ConnectAsync(string dataRoot, string? endpoint = null,
        string? tokenFile = null, string? token = null, CancellationToken cancellationToken = default)
        => await ConnectCoreAsync(dataRoot, endpoint, tokenFile, token, true, cancellationToken);

    public static Task<DaemonBackendClient> ConnectProfileAsync(ClientConnectionProfile profile, string? token = null,
        CancellationToken cancellationToken = default)
        => ConnectProfileCoreAsync(profile, token, null, cancellationToken);

    private static async Task<DaemonBackendClient> ConnectProfileCoreAsync(ClientConnectionProfile profile, string? token,
        IClientCredentialStore? credentialStore, CancellationToken cancellationToken)
    {
        profile = ClientProfileStore.Validate(profile);
        if (string.IsNullOrWhiteSpace(token) && profile.CredentialReference is not null)
        {
            credentialStore ??= ClientCredentialStores.ForReference(profile.DataDirectory, profile.CredentialReference);
            token = await credentialStore.RetrieveAsync(profile.CredentialReference, cancellationToken);
        }
        return await ConnectCoreAsync(profile.DataDirectory, profile.Endpoint, profile.TokenFile, token, false,
            cancellationToken, profile.ExpectedServerId, profile.ExpectedWorkspaceId);
    }

    private static async Task<DaemonBackendClient> ConnectCoreAsync(string dataRoot, string? endpoint,
        string? tokenFile, string? token, bool readEnvironment, CancellationToken cancellationToken,
        Guid? expectedServerId = null, Guid? expectedWorkspaceId = null)
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
                    using var json = JsonDocument.Parse(await File.ReadAllTextAsync(descriptorPath, cancellationToken),
                        new JsonDocumentOptions { MaxDepth = 8, CommentHandling = JsonCommentHandling.Disallow });
                    if (json.RootElement.ValueKind != JsonValueKind.Object
                        || json.RootElement.EnumerateObject().Select(item => item.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count()
                            != json.RootElement.EnumerateObject().Count()) throw new JsonException("Invalid endpoint descriptor.");
                    var descriptor = json.RootElement.Deserialize<DaemonEndpointDescriptor>(DescriptorJsonOptions);
                    if (descriptor is null || descriptor.Version != 1 || descriptor.InstanceId == Guid.Empty
                        || string.IsNullOrWhiteSpace(descriptor.Endpoint)
                        || descriptor.ContractMajor < 0 || descriptor.ContractMinor < 0
                        || descriptor.ServerId == Guid.Empty || descriptor.WorkspaceId == Guid.Empty)
                        throw new JsonException("Invalid endpoint descriptor.");
                    endpoint = DaemonEndpoint.Parse(descriptor.Endpoint, DaemonEndpointKind.LocalLoopback).Origin.AbsoluteUri;
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
            token = await ReadPrivateTokenFileAsync(tokenFile, cancellationToken);
        }
        return new DaemonBackendClient(uri, token, expectedServerId: expectedServerId, expectedWorkspaceId: expectedWorkspaceId);
    }

    public static async Task<string> ReadPrivateTokenFileAsync(string tokenFile, CancellationToken cancellationToken = default)
    {
        try
        {
            var info = new FileInfo(tokenFile);
            var attributes = File.GetAttributes(tokenFile);
            if (info.Length is <= 0 or > 1024 || info.LinkTarget is not null
                || (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0
                || !OperatingSystem.IsWindows() && (File.GetUnixFileMode(tokenFile) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite
                    | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
                throw new SnookException(SnookErrorCode.ValidationFailed, "Daemon token file must be a bounded private regular file, not a symbolic link.");
            var token = (await File.ReadAllTextAsync(tokenFile, cancellationToken)).Trim('\uFEFF', '\r', '\n', ' ');
            return Guard.Required(token, "daemon token", 512);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new SnookException(SnookErrorCode.StoreUnavailable,
                "Cannot read the daemon token file. Start snookd for this data directory or select --token-file. SQLite will not be opened in daemon mode.", exception);
        }
    }
}

/// <summary>Private host discovery metadata; contains no credential or workspace content.</summary>
public sealed record DaemonEndpointDescriptor(int Version, Guid InstanceId, string Endpoint, int ContractMajor, int ContractMinor,
    Guid? ServerId = null, Guid? WorkspaceId = null);

internal static class DaemonTransportFailure
{
    public static SnookException From(HttpRequestException exception, DaemonEndpointKind endpointKind,
        bool uncertainWrite)
    {
        var message = exception.HttpRequestError switch
        {
            HttpRequestError.NameResolutionError when endpointKind == DaemonEndpointKind.TailscaleServe =>
                "The .ts.net daemon name could not be resolved. Check that Tailscale is connected and MagicDNS is available.",
            HttpRequestError.NameResolutionError =>
                "The local daemon name could not be resolved. Check the selected loopback endpoint.",
            HttpRequestError.SecureConnectionError =>
                "TLS certificate or handshake validation failed. Verify the canonical .ts.net Serve URL and system trust; no certificate bypass is supported.",
            HttpRequestError.ConnectionError when endpointKind == DaemonEndpointKind.TailscaleServe =>
                "The tailnet daemon could not be reached. Check Tailscale connectivity, the grant, Serve mapping, and snookd; several of these conditions can look identical.",
            HttpRequestError.ConnectionError =>
                "The local daemon could not be reached. Check that snookd is running on the selected loopback port.",
            _ => "The daemon transport failed before a valid authenticated response was received."
        };
        if (uncertainWrite)
            message += " A write may have committed; reuse its operation ID and unchanged arguments when retrying.";
        return new SnookException(SnookErrorCode.StoreUnavailable, message, exception);
    }
}
