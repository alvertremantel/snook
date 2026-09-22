using System.Text.Json;
using System.Text.Json.Serialization;
using Snook.Domain;

namespace Snook.Application;

/// <summary>
/// Non-secret, device-local description of one paired daemon. The issued device
/// credential is deliberately absent and must be stored by the platform's secret
/// store. This record is configuration, not workspace state.
/// </summary>
public sealed record TailnetAttachedProfile(
    int Version,
    Uri ServeOrigin,
    string TailnetLogin,
    string DeviceName,
    Guid InstallationId,
    Guid CredentialId,
    Guid InstanceId,
    Guid WorkspaceId,
    DateTimeOffset CreatedAtUtc)
{
    public const int CurrentVersion = 1;
    public const int MaximumEncodedBytes = 4096;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static TailnetAttachedProfile FromCredential(DaemonPairingCredential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        return Validate(new(CurrentVersion, credential.ServeOrigin,
            credential.TailnetLogin, credential.DeviceName, credential.InstallationId,
            credential.CredentialId, credential.InstanceId, credential.WorkspaceId,
            credential.CreatedAtUtc));
    }

    public byte[] Encode()
    {
        var validated = Validate(this);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(validated, JsonOptions);
        if (bytes.Length is < 1 or > MaximumEncodedBytes)
            throw InvalidProfile();
        return bytes;
    }

    public static TailnetAttachedProfile Decode(ReadOnlySpan<byte> encoded)
    {
        if (encoded.Length is < 1 or > MaximumEncodedBytes)
            throw InvalidProfile();
        try
        {
            return Validate(JsonSerializer.Deserialize<TailnetAttachedProfile>(encoded, JsonOptions)
                ?? throw InvalidProfile());
        }
        catch (SnookException) { throw; }
        catch (Exception exception) when (exception is JsonException or NotSupportedException
            or UriFormatException or ArgumentException)
        {
            throw new SnookException(SnookErrorCode.ValidationFailed,
                "The attached daemon profile is invalid. Re-pairing is required.", exception);
        }
    }

    public static TailnetAttachedProfile Validate(TailnetAttachedProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.Version != CurrentVersion || profile.InstallationId == Guid.Empty
            || profile.CredentialId == Guid.Empty || profile.InstanceId == Guid.Empty
            || profile.WorkspaceId == Guid.Empty || profile.CreatedAtUtc == default
            || profile.CreatedAtUtc.Offset != TimeSpan.Zero)
            throw InvalidProfile();
        var origin = DaemonBackendClient.ValidateEndpoint(profile.ServeOrigin?.OriginalString
            ?? string.Empty, DaemonEndpointKind.TailscaleServe);
        var login = RequiredText(profile.TailnetLogin, 254, noWhitespace: true);
        var deviceName = RequiredText(profile.DeviceName, 80, noWhitespace: false);
        if (origin != profile.ServeOrigin
            || !string.Equals(login, profile.TailnetLogin, StringComparison.Ordinal)
            || !string.Equals(deviceName, profile.DeviceName, StringComparison.Ordinal))
            throw InvalidProfile();
        return profile;
    }

    private static string RequiredText(string? value, int maximum, bool noWhitespace)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum
            || value.Any(char.IsControl)
            || noWhitespace && value.Any(char.IsWhiteSpace))
            throw InvalidProfile();
        var trimmed = value.Trim();
        if (trimmed.Length != value.Length) throw InvalidProfile();
        return trimmed;
    }

    private static SnookException InvalidProfile() => new(SnookErrorCode.ValidationFailed,
        "The attached daemon profile is invalid. Re-pairing is required.");
}
