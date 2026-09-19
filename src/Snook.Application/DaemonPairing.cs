using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Snook.Domain;

namespace Snook.Application;

public sealed record PairingInvitation(
    int Version,
    string ServeOrigin,
    string PairingCode,
    DateTimeOffset ExpiresAtUtc,
    string DeviceName,
    string? IntendedTailscaleLogin);

public sealed record PairedDeviceInfo(
    Guid CredentialId,
    string DisplayName,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? LastSeenAtUtc,
    string? TailscaleLogin,
    IReadOnlyList<string> Scopes,
    bool Revoked,
    DateTimeOffset? PendingUntilUtc = null);

public sealed record PairingIdentity(
    Uri ServeOrigin,
    Guid ServerId,
    Guid WorkspaceId,
    string WorkspaceName,
    Version Contract,
    Guid CredentialId,
    string DeviceName,
    string? TailscaleLogin);

public sealed class DaemonAdministrationClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private readonly HttpClient _client;
    private readonly Uri _endpoint;
    private readonly string _recoveryToken;

    public DaemonAdministrationClient(Uri endpoint, string recoveryToken, HttpMessageHandler? handler = null)
    {
        var validated = DaemonEndpoint.Parse(endpoint.OriginalString, DaemonEndpointKind.LocalLoopback);
        _endpoint = validated.Origin;
        _recoveryToken = Guard.Required(recoveryToken, nameof(recoveryToken), 512);
        _client = new HttpClient(handler ?? SecureHandler()) { Timeout = TimeSpan.FromSeconds(15) };
    }

    public async Task<PairingInvitation> CreatePairingAsync(string deviceName, Uri serveOrigin,
        string? intendedTailscaleLogin = null, CancellationToken cancellationToken = default)
    {
        var expectedOrigin = DaemonEndpoint.Parse(serveOrigin.OriginalString, DaemonEndpointKind.TailscaleServe).Origin;
        var response = await SendAsync(HttpMethod.Post, "transport/v1/admin/pairings",
            new { deviceName, serveOrigin = serveOrigin.AbsoluteUri, intendedTailscaleLogin }, cancellationToken);
        var invitation = DeserializeResponse<PairingInvitation>(response, JsonOptions);
        if (invitation.Version != 1 || !Uri.TryCreate(invitation.ServeOrigin, UriKind.Absolute, out var returnedOrigin)
            || DaemonEndpoint.Parse(returnedOrigin.OriginalString, DaemonEndpointKind.TailscaleServe).Origin != expectedOrigin
            || !ValidCode(invitation.PairingCode) || !ValidText(invitation.DeviceName, 100)
            || invitation.IntendedTailscaleLogin is { } login && !ValidLogin(login)
            || invitation.ExpiresAtUtc == default || invitation.ExpiresAtUtc.Offset != TimeSpan.Zero) throw InvalidResponse();
        return invitation;
    }

    public async Task<IReadOnlyList<PairedDeviceInfo>> ListDevicesAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(HttpMethod.Get, "transport/v1/admin/devices", null, cancellationToken);
        var devices = DeserializeResponse<PairedDeviceInfo[]>(response, JsonOptions);
        if (devices.Length > 256 || devices.Select(device => device.CredentialId).Distinct().Count() != devices.Length
            || devices.Any(device => device.CredentialId == Guid.Empty || !ValidText(device.DisplayName, 100)
                || device.CreatedAtUtc == default || device.CreatedAtUtc.Offset != TimeSpan.Zero
                || device.LastSeenAtUtc is { Offset: var offset } && offset != TimeSpan.Zero
                || device.PendingUntilUtc is { Offset: var pendingOffset } && pendingOffset != TimeSpan.Zero
                || device.PendingUntilUtc <= device.CreatedAtUtc
                || device.Revoked && device.PendingUntilUtc is not null
                || device.PendingUntilUtc is not null && device.LastSeenAtUtc is not null
                || device.TailscaleLogin is { } login && !ValidLogin(login)
                || device.Scopes is null || device.Scopes.Count is < 1 or > 8
                || device.Scopes.Any(scope => !ValidText(scope, 64)))) throw InvalidResponse();
        return devices;
    }

    public async Task RevokeDeviceAsync(Guid credentialId, CancellationToken cancellationToken = default)
        => _ = await SendAsync(HttpMethod.Post, "transport/v1/admin/devices/revoke", new { credentialId }, cancellationToken);

    public async Task CancelPairingAsync(string pairingCode, CancellationToken cancellationToken = default)
        => _ = await SendAsync(HttpMethod.Post, "transport/v1/admin/pairings/cancel", new { pairingCode }, cancellationToken);

    private async Task<JsonElement> SendAsync(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, new Uri(_endpoint, path));
        request.Headers.Add("X-Snook-Token", _recoveryToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (body is not null) request.Content = Json(body);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        return await ReadResponseAsync(response, cancellationToken);
    }

    internal static async Task<JsonElement> ReadResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await response.Content.LoadIntoBufferAsync(256 * 1024, cancellationToken);
        JsonDocument document;
        try { document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken),
            new JsonDocumentOptions { MaxDepth = 32, CommentHandling = JsonCommentHandling.Disallow }); }
        catch (JsonException exception)
        {
            throw new SnookException(SnookErrorCode.StoreUnavailable,
                "Daemon returned an invalid or incompatible pairing response.", exception);
        }
        using (document)
        {
            try { RejectDuplicateProperties(document.RootElement); }
            catch (JsonException exception) { throw new SnookException(SnookErrorCode.StoreUnavailable,
                "Daemon returned an invalid or incompatible pairing response.", exception); }
            if (!response.IsSuccessStatusCode)
            {
                var message = document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty("error", out var error)
                    && error.ValueKind == JsonValueKind.Object
                    && error.TryGetProperty("message", out var detail)
                    && detail.ValueKind == JsonValueKind.String ? detail.GetString() : null;
                throw new SnookException(SnookErrorCode.StoreUnavailable, message ?? "Daemon administration request failed.");
            }
            return document.RootElement.Clone();
        }
    }

    internal static StringContent Json(object body)
        => new(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json");

    internal static HttpClientHandler SecureHandler() => new() { AllowAutoRedirect = false, UseProxy = false };
    internal static bool ValidCode(string? value) => value is { Length: >= 20 and <= 64 }
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
    internal static bool ValidText(string? value, int maximum) => !string.IsNullOrWhiteSpace(value)
        && value.Length <= maximum && !value.Any(char.IsControl);
    internal static bool ValidLogin(string value) => ValidText(value, 254) && !value.Any(char.IsWhiteSpace)
        && string.Equals(value, value.Normalize(NormalizationForm.FormKC).ToLowerInvariant(), StringComparison.Ordinal);
    internal static T DeserializeResponse<T>(JsonElement element, JsonSerializerOptions options)
    {
        try { return element.Deserialize<T>(options) ?? throw InvalidResponse(); }
        catch (JsonException exception) { throw new SnookException(SnookErrorCode.StoreUnavailable,
            "Daemon returned invalid pairing metadata.", exception); }
    }
    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            // Web JSON property binding is case-insensitive, so casing variants
            // must not be able to bind the same field twice.
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new JsonException("Duplicate JSON property.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicateProperties(item);
    }
    private static SnookException InvalidResponse() => new(SnookErrorCode.StoreUnavailable, "Daemon returned invalid pairing metadata.");
    public void Dispose() => _client.Dispose();
}

public sealed class PendingDaemonPairing : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private readonly HttpClient _client;
    private readonly string _credential;
    private bool _completed;
    private bool _confirmed;
    private IClientCredentialStore? _credentialStore;
    private string? _credentialReference;

    internal PendingDaemonPairing(HttpClient client, PairingIdentity identity, string credential)
    {
        _client = client;
        Identity = identity;
        _credential = credential;
    }

    public PairingIdentity Identity { get; }

    public async Task<ClientConnectionProfile> ConfirmAsync(string clientDataDirectory, IClientCredentialStore credentialStore,
        string? friendlyName = null, CancellationToken cancellationToken = default)
    {
        if (_completed || _confirmed) throw new InvalidOperationException("Pairing was already confirmed or completed.");
        await VerifyIssuedCredentialAsync(cancellationToken);
        var reference = await credentialStore.StoreAsync(_credential, friendlyName ?? Identity.DeviceName, cancellationToken);
        try
        {
            var profile = ClientProfileStore.Validate(new ClientConnectionProfile(2, "daemon", clientDataDirectory,
                Identity.ServeOrigin.AbsoluteUri, TransportKind: "tailscale-serve", CredentialReference: reference,
                ExpectedServerId: Identity.ServerId, ExpectedWorkspaceId: Identity.WorkspaceId,
                FriendlyName: friendlyName ?? Identity.DeviceName, ContractMajor: Identity.Contract.Major,
                ContractMinor: Identity.Contract.Minor));
            _confirmed = true;
            _credentialStore = credentialStore;
            _credentialReference = reference;
            return profile;
        }
        catch
        {
            await credentialStore.DeleteAsync(reference, CancellationToken.None);
            throw;
        }
    }

    private async Task VerifyIssuedCredentialAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(Identity.ServeOrigin, "v1/health"));
            request.Headers.Add("X-Snook-Token", _credential);
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var body = await DaemonAdministrationClient.ReadResponseAsync(response, cancellationToken);
            var health = DaemonAdministrationClient.DeserializeResponse<PairingHealthResponse>(body, JsonOptions);
            if (!health.Ready || health.ServerId != Identity.ServerId || health.WorkspaceId != Identity.WorkspaceId
                || !Version.TryParse(health.Contract, out var contract) || contract != Identity.Contract)
                throw new SnookException(SnookErrorCode.StoreUnavailable,
                    "The issued credential did not authenticate the reviewed server and workspace. Nothing was saved; cancel or retry pairing.");
        }
        catch (HttpRequestException exception)
        {
            throw DaemonTransportFailure.From(exception, DaemonEndpointKind.TailscaleServe, uncertainWrite: false);
        }
    }

    public void Complete()
    {
        if (!_confirmed || _completed) throw new InvalidOperationException("Pairing has not been confirmed or was already completed.");
        _completed = true;
        _client.Dispose();
    }

    public async Task CancelAsync(CancellationToken cancellationToken = default)
    {
        if (_completed) return;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(Identity.ServeOrigin, "transport/v1/pairings/cancel"));
            request.Headers.Add("X-Snook-Token", _credential);
            request.Content = DaemonAdministrationClient.Json(new { credentialId = Identity.CredentialId });
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            _ = await DaemonAdministrationClient.ReadResponseAsync(response, cancellationToken);
        }
        finally
        {
            _completed = true;
            _client.Dispose();
            if (_credentialStore is not null && _credentialReference is not null)
                await _credentialStore.DeleteAsync(_credentialReference, CancellationToken.None);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_completed)
        {
            try { await CancelAsync(); }
            catch (Exception exception) when (exception is HttpRequestException or IOException or SnookException
                or OperationCanceledException or JsonException)
            {
                _client.Dispose();
            }
        }
    }

    private sealed record PairingHealthResponse(bool Ready, string Contract, Guid ServerId, Guid WorkspaceId);
}

public static class TailnetPairingClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static async Task<PendingDaemonPairing> RedeemAsync(Uri serveOrigin, string pairingCode,
        string deviceName, HttpMessageHandler? handler = null, CancellationToken cancellationToken = default)
    {
        var endpoint = DaemonEndpoint.Parse(serveOrigin.OriginalString, DaemonEndpointKind.TailscaleServe).Origin;
        if (!DaemonAdministrationClient.ValidCode(pairingCode) || !DaemonAdministrationClient.ValidText(deviceName, 100))
            throw new SnookException(SnookErrorCode.ValidationFailed, "The pairing code or device name is invalid.");
        deviceName = deviceName.Trim();
        var client = new HttpClient(handler ?? DaemonAdministrationClient.SecureHandler()) { Timeout = TimeSpan.FromSeconds(15) };
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint, "transport/v1/pair"))
            {
                Content = DaemonAdministrationClient.Json(new { pairingCode, deviceName })
            };
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var body = await DaemonAdministrationClient.ReadResponseAsync(response, cancellationToken);
            var wire = DaemonAdministrationClient.DeserializeResponse<PairingResponse>(body, JsonOptions);
            if (!Uri.TryCreate(wire.ServeOrigin, UriKind.Absolute, out var returnedOrigin)
                || DaemonEndpoint.Parse(returnedOrigin.OriginalString, DaemonEndpointKind.TailscaleServe).Origin != endpoint
                || wire.ServerId == Guid.Empty || wire.WorkspaceId == Guid.Empty || wire.CredentialId == Guid.Empty
                || !Version.TryParse(wire.Contract, out var contract) || contract.Major != Contracts.ContractInfo.Major
                || contract.Minor < Contracts.ContractInfo.Minor
                || !DaemonAdministrationClient.ValidText(wire.WorkspaceName, 200)
                || wire.DeviceName != deviceName || !DaemonAdministrationClient.ValidText(wire.DeviceName, 100)
                || wire.TailscaleLogin is { } login && !DaemonAdministrationClient.ValidLogin(login)) throw InvalidResponse();
            string credential;
            try { credential = ClientCredentialStores.NormalizeCredential(wire.Credential); }
            catch (SnookException) { throw InvalidResponse(); }
            var identity = new PairingIdentity(endpoint, wire.ServerId, wire.WorkspaceId,
                wire.WorkspaceName, contract, wire.CredentialId, wire.DeviceName, wire.TailscaleLogin);
            return new PendingDaemonPairing(client, identity, credential);
        }
        catch (HttpRequestException exception)
        {
            client.Dispose();
            throw DaemonTransportFailure.From(exception, DaemonEndpointKind.TailscaleServe, uncertainWrite: false);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private sealed record PairingResponse(string ServeOrigin, Guid ServerId, Guid WorkspaceId, string WorkspaceName,
        string Contract, Guid CredentialId, string DeviceName, string? TailscaleLogin, string Credential);

    private static SnookException InvalidResponse() => new(SnookErrorCode.StoreUnavailable,
        "The pairing response was invalid. No credential or profile was saved.");
}
