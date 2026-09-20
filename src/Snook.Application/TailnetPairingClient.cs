using System.Net;
using System.Text;
using System.Text.Json;
using Snook.Domain;

namespace Snook.Application;

/// <summary>
/// Bounded client for the unauthenticated, single-use portion of daemon pairing.
/// Tailscale Serve supplies observed identity headers; this client never supplies or
/// accepts caller-provided identity headers.
/// </summary>
public sealed class TailnetPairingClient : IAsyncDisposable
{
    private const int MaximumResponseBytes = 65_536;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly Uri _origin;
    private readonly HttpClient _http;
    private readonly TimeProvider _clock;
    private bool _disposed;

    public TailnetPairingClient(Uri origin, HttpMessageHandler? handler = null,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(origin);
        _origin = DaemonBackendClient.ValidateEndpoint(origin.OriginalString,
            DaemonEndpointKind.TailnetServe);
        _http = new HttpClient(handler ?? new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false
        });
        _http.Timeout = Timeout.InfiniteTimeSpan;
        _clock = clock ?? TimeProvider.System;
    }

    public Uri Origin => _origin;

    public async Task<DaemonPairingPreview> BeginAsync(string code,
        Guid installationId, string deviceName, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(code) || code.Length > 128 || code.Any(char.IsControl))
            throw new SnookException(SnookErrorCode.ValidationFailed,
                "Pairing code is required and must be at most 128 characters.");
        if (installationId == Guid.Empty)
            throw new SnookException(SnookErrorCode.ValidationFailed,
                "Installation identity is required.");
        deviceName = RequiredDeviceName(deviceName);
        var preview = await PostAsync<DaemonPairingPreview>("v1/pairing/begin",
            new { code, installationId, deviceName }, cancellationToken).ConfigureAwait(false);
        ValidatePreview(preview, installationId, deviceName);
        return preview;
    }

    public async Task<DaemonPairingCredential> CompleteAsync(DaemonPairingPreview preview,
        Guid installationId, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(preview);
        ValidatePreview(preview, installationId, preview.DeviceName);
        var paired = await PostAsync<DaemonPairingCredential>("v1/pairing/complete",
            new { preview.EnrollmentId }, cancellationToken).ConfigureAwait(false);
        if (paired.CredentialId == Guid.Empty || paired.InstallationId != installationId
            || paired.InstanceId != preview.InstanceId || paired.WorkspaceId != preview.WorkspaceId
            || paired.ServeOrigin != _origin
            || !string.Equals(paired.TailnetLogin, preview.TailnetLogin, StringComparison.Ordinal)
            || !string.Equals(paired.DeviceName, preview.DeviceName, StringComparison.Ordinal)
            || paired.Credential.Length != 64 || !paired.Credential.All(char.IsAsciiHexDigit))
            throw new SnookException(SnookErrorCode.StoreUnavailable,
                "Pairing completion did not match the reviewed daemon, workspace, identity and installation.");
        return paired;
    }

    private async Task<T> PostAsync<T>(string route, object body,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_origin, route));
            request.Headers.Accept.ParseAdd("application/json");
            request.Content = new StringContent(JsonSerializer.Serialize(body, JsonOptions),
                Encoding.UTF8, "application/json");
            using var response = await _http.SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new SnookException(SnookErrorCode.StoreUnavailable,
                    "Pairing requires the configured Tailscale Serve route and an allowed tailnet identity.");
            if (!response.IsSuccessStatusCode)
                throw new SnookException(SnookErrorCode.StoreUnavailable,
                    "Pairing was rejected, expired, already used, or unavailable. Create a new pairing code and retry.");
            await response.Content.LoadIntoBufferAsync(MaximumResponseBytes, deadline.Token).ConfigureAwait(false);
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions,
                deadline.Token).ConfigureAwait(false)
                ?? throw new JsonException("Pairing response was empty.");
        }
        catch (SnookException) { throw; }
        catch (Exception exception) when (exception is HttpRequestException or IOException
            or JsonException || exception is OperationCanceledException
                && !cancellationToken.IsCancellationRequested)
        {
            throw new SnookException(SnookErrorCode.StoreUnavailable,
                "Pairing transport returned an invalid or unavailable response. No local workspace was opened.", exception);
        }
    }

    private void ValidatePreview(DaemonPairingPreview preview, Guid installationId,
        string deviceName)
    {
        ArgumentNullException.ThrowIfNull(preview);
        if (installationId == Guid.Empty || preview.EnrollmentId == Guid.Empty
            || preview.InstanceId == Guid.Empty || preview.WorkspaceId == Guid.Empty
            || preview.ServeOrigin != _origin || preview.ExpiresAtUtc <= _clock.GetUtcNow()
            || string.IsNullOrWhiteSpace(preview.TailnetLogin)
            || preview.TailnetLogin.Length > 254
            || preview.TailnetLogin.Any(character => char.IsControl(character) || char.IsWhiteSpace(character))
            || !string.Equals(preview.DeviceName, RequiredDeviceName(deviceName), StringComparison.Ordinal))
            throw new SnookException(SnookErrorCode.StoreUnavailable,
                "Pairing preview is invalid, expired, or does not match this installation and Serve origin.");
    }

    private static string RequiredDeviceName(string value)
    {
        if (value is null || value.Length > 80 || string.IsNullOrWhiteSpace(value)
            || value.Any(char.IsControl))
            throw new SnookException(SnookErrorCode.ValidationFailed,
                "Device name is required, limited to 80 characters, and cannot contain controls.");
        return value.Trim();
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        _http.Dispose();
        return ValueTask.CompletedTask;
    }
}
