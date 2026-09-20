using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Snook.Domain;

namespace Snook.Application;

public sealed record DaemonPairingChallenge(
    string Code,
    Uri ServeOrigin,
    Guid InstanceId,
    Guid WorkspaceId,
    DateTimeOffset ExpiresAtUtc);

public sealed record DaemonPairingPreview(
    Guid EnrollmentId,
    Uri ServeOrigin,
    string TailnetLogin,
    string DeviceName,
    Guid InstanceId,
    Guid WorkspaceId,
    DateTimeOffset ExpiresAtUtc);

public sealed record DaemonPairingCredential(
    Guid CredentialId,
    string Credential,
    Uri ServeOrigin,
    string TailnetLogin,
    string DeviceName,
    Guid InstallationId,
    Guid InstanceId,
    Guid WorkspaceId,
    DateTimeOffset CreatedAtUtc);

public sealed record DaemonPairedDevice(
    Guid CredentialId,
    Uri ServeOrigin,
    string TailnetLogin,
    string DeviceName,
    Guid InstallationId,
    Guid InstanceId,
    Guid WorkspaceId,
    DateTimeOffset CreatedAtUtc);

public sealed record DaemonPairingRevocation(Guid CredentialId, bool Revoked);
public sealed record DaemonWorkspaceRebind(DaemonPairedDevice Device,
    int RevokedOtherDeviceCount);

/// <summary>
/// Private daemon-operational enrollment state. It stores only SHA-256 digests of
/// pairing secrets and client credentials; neither belongs to workspace SQLite.
/// </summary>
public sealed class DaemonPairingRegistry : IDisposable
{
    public const int MaximumChallenges = 32;
    public const int MaximumPendingEnrollments = 32;
    public const int MaximumDevices = 128;
    public const int ChallengeAttemptLimit = 5;
    public static readonly TimeSpan ChallengeLifetime = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan EnrollmentLifetime = TimeSpan.FromMinutes(2);
    private const int MaximumStateBytes = 262_144;
    private const int MaximumDeviceNameLength = 80;
    private const int MaximumIdentityLength = 254;
    private const int CredentialLength = 64;
    private const int ChallengePartLength = 32;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _path;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private PairingState? _state;
    private bool _disposed;

    public DaemonPairingRegistry(string path, TimeProvider? clock = null)
    {
        _path = Path.GetFullPath(path ?? throw new ArgumentNullException(nameof(path)));
        _clock = clock ?? TimeProvider.System;
    }

    public event EventHandler<Guid>? CredentialRevoked;

    public async Task<DaemonPairingChallenge> CreateChallengeAsync(Uri serveOrigin,
        Guid instanceId, Guid workspaceId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serveOrigin);
        serveOrigin = DaemonBackendClient.ValidateEndpoint(serveOrigin.OriginalString,
            DaemonEndpointKind.TailnetServe);
        ValidateIdentities(instanceId, workspaceId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var state = await StateAsync(cancellationToken).ConfigureAwait(false);
            var now = _clock.GetUtcNow();
            Cleanup(state, now);
            if (state.Challenges.Count >= MaximumChallenges)
                throw new SnookException(SnookErrorCode.StoreUnavailable,
                    "Too many pairing challenges are active. Wait for one to expire or restart pairing later.");
            var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            var expires = now + ChallengeLifetime;
            state.Challenges.Add(new(id, Digest(secret), serveOrigin.AbsoluteUri,
                instanceId, workspaceId, expires, ChallengeAttemptLimit));
            await SaveAsync(state, cancellationToken).ConfigureAwait(false);
            return new DaemonPairingChallenge(id + "." + secret, serveOrigin,
                instanceId, workspaceId, expires);
        }
        finally { _gate.Release(); }
    }

    public async Task<DaemonPairingPreview> BeginAsync(string code, Guid installationId,
        string deviceName, string tailnetLogin, Guid instanceId, Guid workspaceId,
        CancellationToken cancellationToken = default)
    {
        var (challengeId, secret) = ParseChallenge(code);
        if (installationId == Guid.Empty)
            throw new SnookException(SnookErrorCode.ValidationFailed, "Installation identity is required.");
        deviceName = RequiredText(deviceName, "deviceName", MaximumDeviceNameLength);
        tailnetLogin = NormalizeIdentity(tailnetLogin);
        ValidateIdentities(instanceId, workspaceId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var state = await StateAsync(cancellationToken).ConfigureAwait(false);
            var now = _clock.GetUtcNow();
            Cleanup(state, now);
            var challenge = state.Challenges.SingleOrDefault(item => item.Id == challengeId);
            if (challenge is null || challenge.ExpiresAtUtc <= now || challenge.AttemptsRemaining <= 0
                || challenge.InstanceId != instanceId || challenge.WorkspaceId != workspaceId)
            {
                await SaveAsync(state, cancellationToken).ConfigureAwait(false);
                throw PairingRejected();
            }
            if (!Matches(secret, challenge.SecretDigest))
            {
                var remaining = challenge.AttemptsRemaining - 1;
                state.Challenges.Remove(challenge);
                if (remaining > 0) state.Challenges.Add(challenge with { AttemptsRemaining = remaining });
                await SaveAsync(state, cancellationToken).ConfigureAwait(false);
                throw PairingRejected();
            }
            if (state.Pending.Count >= MaximumPendingEnrollments)
                throw new SnookException(SnookErrorCode.StoreUnavailable,
                    "Too many pairing confirmations are pending. Wait for one to expire.");
            if (state.Devices.Any(item => item.InstallationId == installationId))
                throw new SnookException(SnookErrorCode.InvalidTransition,
                    "This installation is already paired. Revoke or repair its existing pairing first.");

            state.Challenges.Remove(challenge);
            var enrollment = new PendingRecord(Guid.NewGuid(), challenge.ServeOrigin,
                tailnetLogin, deviceName, installationId, instanceId, workspaceId,
                now + EnrollmentLifetime);
            state.Pending.Add(enrollment);
            await SaveAsync(state, cancellationToken).ConfigureAwait(false);
            return Preview(enrollment);
        }
        finally { _gate.Release(); }
    }

    public async Task<DaemonPairingCredential> CompleteAsync(Guid enrollmentId,
        string tailnetLogin, Guid instanceId, Guid workspaceId,
        CancellationToken cancellationToken = default)
    {
        if (enrollmentId == Guid.Empty)
            throw new SnookException(SnookErrorCode.ValidationFailed, "Enrollment identity is required.");
        tailnetLogin = NormalizeIdentity(tailnetLogin);
        ValidateIdentities(instanceId, workspaceId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var state = await StateAsync(cancellationToken).ConfigureAwait(false);
            var now = _clock.GetUtcNow();
            Cleanup(state, now);
            var pending = state.Pending.SingleOrDefault(item => item.EnrollmentId == enrollmentId);
            if (pending is null || pending.ExpiresAtUtc <= now
                || pending.InstanceId != instanceId || pending.WorkspaceId != workspaceId
                || !string.Equals(pending.TailnetLogin, tailnetLogin, StringComparison.Ordinal))
            {
                await SaveAsync(state, cancellationToken).ConfigureAwait(false);
                throw PairingRejected();
            }
            if (state.Devices.Count >= MaximumDevices)
                throw new SnookException(SnookErrorCode.StoreUnavailable,
                    "The paired-device limit has been reached. Revoke an unused device first.");

            // Persist the digest and consume the enrollment before returning the only
            // plaintext credential. A lost response deliberately requires new pairing.
            var credential = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var record = new DeviceRecord(Guid.NewGuid(), Digest(credential),
                pending.ServeOrigin, pending.TailnetLogin, pending.DeviceName,
                pending.InstallationId, instanceId, workspaceId, now);
            state.Pending.Remove(pending);
            state.Devices.Add(record);
            await SaveAsync(state, cancellationToken).ConfigureAwait(false);
            return new DaemonPairingCredential(record.CredentialId, credential,
                new Uri(record.ServeOrigin), record.TailnetLogin, record.DeviceName,
                record.InstallationId, record.InstanceId, record.WorkspaceId,
                record.CreatedAtUtc);
        }
        finally { _gate.Release(); }
    }

    public async Task<Guid?> AuthenticateAsync(string? credential, string? tailnetLogin,
        Guid instanceId, Guid workspaceId, CancellationToken cancellationToken = default)
    {
        if (!ValidCredential(credential) || !TryNormalizeIdentity(tailnetLogin, out var identity)
            || instanceId == Guid.Empty || workspaceId == Guid.Empty) return null;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var state = await StateAsync(cancellationToken).ConfigureAwait(false);
            var digest = SHA256.HashData(Encoding.UTF8.GetBytes(credential!));
            foreach (var device in state.Devices)
            {
                if (device.InstanceId == instanceId && device.WorkspaceId == workspaceId
                    && string.Equals(device.TailnetLogin, identity, StringComparison.Ordinal)
                    && FixedEquals(digest, device.CredentialDigest))
                    return device.CredentialId;
            }
            return null;
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<DaemonPairedDevice>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var state = await StateAsync(cancellationToken).ConfigureAwait(false);
            return state.Devices.Select(device => new DaemonPairedDevice(
                device.CredentialId, new Uri(device.ServeOrigin), device.TailnetLogin,
                device.DeviceName, device.InstallationId, device.InstanceId,
                device.WorkspaceId, device.CreatedAtUtc)).ToArray();
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> RevokeAsync(Guid credentialId,
        CancellationToken cancellationToken = default)
    {
        if (credentialId == Guid.Empty)
            throw new SnookException(SnookErrorCode.ValidationFailed, "Credential identity is required.");
        var removed = false;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var state = await StateAsync(cancellationToken).ConfigureAwait(false);
            removed = state.Devices.RemoveAll(item => item.CredentialId == credentialId) == 1;
            if (removed) await SaveAsync(state, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
        if (removed) CredentialRevoked?.Invoke(this, credentialId);
        return removed;
    }

    public async Task<DaemonPairedDevice> RenameAsync(Guid credentialId, string deviceName,
        CancellationToken cancellationToken = default)
    {
        if (credentialId == Guid.Empty)
            throw new SnookException(SnookErrorCode.ValidationFailed, "Credential identity is required.");
        deviceName = RequiredText(deviceName, "deviceName", MaximumDeviceNameLength);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var state = await StateAsync(cancellationToken).ConfigureAwait(false);
            var index = state.Devices.FindIndex(item => item.CredentialId == credentialId);
            if (index < 0)
                throw new SnookException(SnookErrorCode.NotFound, "Paired device was not found.");
            var renamed = state.Devices[index] with { DeviceName = deviceName };
            state.Devices[index] = renamed;
            await SaveAsync(state, cancellationToken).ConfigureAwait(false);
            return new DaemonPairedDevice(renamed.CredentialId, new Uri(renamed.ServeOrigin),
                renamed.TailnetLogin, renamed.DeviceName, renamed.InstallationId,
                renamed.InstanceId, renamed.WorkspaceId, renamed.CreatedAtUtc);
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// After a verified server-side workspace replacement, retain only the device
    /// that explicitly authorized it and bind that credential to the new identity.
    /// Every other credential and every pending enrollment is invalidated.
    /// </summary>
    public async Task<DaemonWorkspaceRebind> RebindWorkspaceAsync(Guid credentialId,
        Guid oldWorkspaceId, Guid newWorkspaceId, CancellationToken cancellationToken = default)
    {
        if (credentialId == Guid.Empty || oldWorkspaceId == Guid.Empty || newWorkspaceId == Guid.Empty)
            throw new SnookException(SnookErrorCode.ValidationFailed,
                "Credential and workspace identities are required for restore handoff.");
        Guid[] revoked = [];
        DaemonPairedDevice result;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var state = await StateAsync(cancellationToken).ConfigureAwait(false);
            var keeper = state.Devices.SingleOrDefault(item => item.CredentialId == credentialId)
                ?? throw new SnookException(SnookErrorCode.NotFound,
                    "The restoring paired device is no longer registered for this workspace.");
            if (keeper.WorkspaceId == newWorkspaceId)
                return new(ToDevice(keeper), 0);
            if (keeper.WorkspaceId != oldWorkspaceId)
                throw new SnookException(SnookErrorCode.RevisionConflict,
                    "The restoring paired device is bound to another workspace.");
            if (oldWorkspaceId == newWorkspaceId)
                return new(ToDevice(keeper), 0);
            revoked = state.Devices.Where(item => item.CredentialId != credentialId)
                .Select(item => item.CredentialId).ToArray();
            var rebound = keeper with { WorkspaceId = newWorkspaceId };
            var replacement = new PairingState(state.Version, [], [], [rebound]);
            await SaveAsync(replacement, cancellationToken).ConfigureAwait(false);
            result = ToDevice(rebound);
        }
        finally { _gate.Release(); }
        foreach (var revokedId in revoked) CredentialRevoked?.Invoke(this, revokedId);
        return new(result, revoked.Length);
    }

    private async Task<PairingState> StateAsync(CancellationToken cancellationToken)
    {
        if (_state is not null) return _state;
        if (!File.Exists(_path)) return _state = new(1, [], [], []);
        var info = new FileInfo(_path);
        if (info.LinkTarget is not null || info.Length is <= 0 or > MaximumStateBytes
            || !OperatingSystem.IsWindows() && (File.GetUnixFileMode(_path)
                & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                    | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
            throw new SnookException(SnookErrorCode.StoreUnavailable,
                "Pairing registry must be a bounded private regular file.");
        try
        {
            await using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read,
                FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var state = await JsonSerializer.DeserializeAsync<PairingState>(stream,
                JsonOptions, cancellationToken).ConfigureAwait(false);
            ValidateState(state);
            return _state = state!;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or JsonException or FormatException)
        {
            throw new SnookException(SnookErrorCode.StoreUnavailable,
                "Pairing registry is unavailable or invalid. Repair it before accepting device credentials.", exception);
        }
    }

    private async Task SaveAsync(PairingState state, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_path)
            ?? throw new InvalidOperationException("Pairing registry has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous
            };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var stream = new FileStream(temporary, options))
            {
                await JsonSerializer.SerializeAsync(stream, state, JsonOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
                if (stream.Length > MaximumStateBytes)
                    throw new SnookException(SnookErrorCode.StoreUnavailable,
                        "Pairing registry exceeded its storage limit.");
            }
            File.Move(temporary, _path, overwrite: true);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(_path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            _state = state;
        }
        finally { File.Delete(temporary); }
    }

    private static void Cleanup(PairingState state, DateTimeOffset now)
    {
        state.Challenges.RemoveAll(item => item.ExpiresAtUtc <= now || item.AttemptsRemaining <= 0);
        state.Pending.RemoveAll(item => item.ExpiresAtUtc <= now);
    }

    private static void ValidateState(PairingState? state)
    {
        if (state is null || state.Version != 1 || state.Challenges is null
            || state.Pending is null || state.Devices is null
            || state.Challenges.Count > MaximumChallenges
            || state.Pending.Count > MaximumPendingEnrollments
            || state.Devices.Count > MaximumDevices
            || state.Challenges.Any(item => !ValidChallengePart(item.Id)
                || !ValidDigest(item.SecretDigest) || item.AttemptsRemaining is < 1 or > ChallengeAttemptLimit
                || item.InstanceId == Guid.Empty || item.WorkspaceId == Guid.Empty
                || !ValidOrigin(item.ServeOrigin))
            || state.Pending.Any(item => item.EnrollmentId == Guid.Empty
                || item.InstallationId == Guid.Empty || item.InstanceId == Guid.Empty
                || item.WorkspaceId == Guid.Empty || !ValidOrigin(item.ServeOrigin)
                || !TryNormalizeIdentity(item.TailnetLogin, out _)
                || !ValidText(item.DeviceName, MaximumDeviceNameLength))
            || state.Devices.Any(item => item.CredentialId == Guid.Empty
                || item.InstallationId == Guid.Empty || item.InstanceId == Guid.Empty
                || item.WorkspaceId == Guid.Empty || !ValidDigest(item.CredentialDigest)
                || !ValidOrigin(item.ServeOrigin) || !TryNormalizeIdentity(item.TailnetLogin, out _)
                || !ValidText(item.DeviceName, MaximumDeviceNameLength))
            || state.Challenges.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != state.Challenges.Count
            || state.Pending.Select(item => item.EnrollmentId).Distinct().Count() != state.Pending.Count
            || state.Devices.Select(item => item.CredentialId).Distinct().Count() != state.Devices.Count
            || state.Devices.Select(item => item.InstallationId).Distinct().Count() != state.Devices.Count)
            throw new JsonException("Invalid pairing registry.");
    }

    private static (string Id, string Secret) ParseChallenge(string code)
    {
        if (code is null) throw PairingRejected();
        var separator = code.IndexOf('.', StringComparison.Ordinal);
        if (separator != ChallengePartLength || code.Length != ChallengePartLength * 2 + 1)
            throw PairingRejected();
        var id = code[..separator];
        var secret = code[(separator + 1)..];
        if (!ValidChallengePart(id) || !ValidChallengePart(secret)) throw PairingRejected();
        return (id, secret);
    }

    private static string RequiredText(string value, string name, int maximum)
    {
        if (!ValidText(value, maximum))
            throw new SnookException(SnookErrorCode.ValidationFailed,
                $"{name} is required, limited to {maximum} characters, and cannot contain controls.");
        return value.Trim();
    }

    private static bool ValidText(string? value, int maximum) => value is not null
        && value.Length <= maximum && !string.IsNullOrWhiteSpace(value)
        && !value.Any(char.IsControl);

    private static string NormalizeIdentity(string value)
    {
        if (!TryNormalizeIdentity(value, out var normalized))
            throw new SnookException(SnookErrorCode.ValidationFailed,
                "A bounded Tailscale user login is required for pairing.");
        return normalized;
    }

    private static bool TryNormalizeIdentity(string? value, out string normalized)
    {
        normalized = value?.Trim().ToLowerInvariant() ?? string.Empty;
        return normalized.Length is > 0 and <= MaximumIdentityLength
            && !normalized.Any(character => char.IsControl(character) || char.IsWhiteSpace(character));
    }

    private static void ValidateIdentities(Guid instanceId, Guid workspaceId)
    {
        if (instanceId == Guid.Empty || workspaceId == Guid.Empty)
            throw new SnookException(SnookErrorCode.ValidationFailed,
                "Daemon instance and workspace identities are required.");
    }

    private static bool ValidOrigin(string value)
    {
        try
        {
            _ = DaemonBackendClient.ValidateEndpoint(value, DaemonEndpointKind.TailnetServe);
            return true;
        }
        catch (SnookException) { return false; }
    }

    private static bool ValidCredential(string? value) => value?.Length == CredentialLength
        && value.All(char.IsAsciiHexDigit);

    private static bool ValidChallengePart(string? value) => value?.Length == ChallengePartLength
        && value.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F');

    private static string Digest(string value)
        => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static bool Matches(string value, string digest)
        => FixedEquals(SHA256.HashData(Encoding.UTF8.GetBytes(value)), digest);

    private static bool FixedEquals(byte[] supplied, string digest)
    {
        try { return CryptographicOperations.FixedTimeEquals(supplied, Convert.FromBase64String(digest)); }
        catch (FormatException) { return false; }
    }

    private static bool ValidDigest(string digest)
    {
        try { return Convert.FromBase64String(digest).Length == 32; }
        catch (FormatException) { return false; }
    }

    private static DaemonPairingPreview Preview(PendingRecord pending) => new(
        pending.EnrollmentId, new Uri(pending.ServeOrigin), pending.TailnetLogin,
        pending.DeviceName, pending.InstanceId, pending.WorkspaceId, pending.ExpiresAtUtc);

    private static DaemonPairedDevice ToDevice(DeviceRecord device) => new(
        device.CredentialId, new Uri(device.ServeOrigin), device.TailnetLogin,
        device.DeviceName, device.InstallationId, device.InstanceId,
        device.WorkspaceId, device.CreatedAtUtc);

    private static SnookException PairingRejected() => new(SnookErrorCode.StoreUnavailable,
        "Pairing code or confirmation is invalid, expired, already used, or bound to another identity.");

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _gate.Dispose();
    }

    private sealed record PairingState(int Version, List<ChallengeRecord> Challenges,
        List<PendingRecord> Pending, List<DeviceRecord> Devices);
    private sealed record ChallengeRecord(string Id, string SecretDigest, string ServeOrigin,
        Guid InstanceId, Guid WorkspaceId, DateTimeOffset ExpiresAtUtc, int AttemptsRemaining);
    private sealed record PendingRecord(Guid EnrollmentId, string ServeOrigin,
        string TailnetLogin, string DeviceName, Guid InstallationId, Guid InstanceId,
        Guid WorkspaceId, DateTimeOffset ExpiresAtUtc);
    private sealed record DeviceRecord(Guid CredentialId, string CredentialDigest,
        string ServeOrigin, string TailnetLogin, string DeviceName, Guid InstallationId,
        Guid InstanceId, Guid WorkspaceId, DateTimeOffset CreatedAtUtc);
}
