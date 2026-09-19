using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Snook.Application;
using Snook.Contracts;
using Snook.Domain;

namespace Snook.Daemon;

internal sealed record AuthenticatedDevice(Guid CredentialId, string? TailscaleLogin,
    bool IsRecoveryAdministrator, bool IsPending);

internal interface IDaemonRandomSource
{
    byte[] GetBytes(int count);
    Guid NewGuid();
}

internal sealed class SystemDaemonRandomSource : IDaemonRandomSource
{
    public byte[] GetBytes(int count) => RandomNumberGenerator.GetBytes(count);
    public Guid NewGuid() => Guid.NewGuid();
}

internal sealed class DaemonCredentialRegistry : IDisposable
{
    private const int MaximumFileBytes = 256 * 1024;
    private const int MaximumRecords = 256;
    private static readonly TimeSpan LastSeenWriteInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PendingCredentialLifetime = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IDaemonRandomSource _random;
    private RegistryDocument _document;

    private DaemonCredentialRegistry(string path, RegistryDocument document, IDaemonRandomSource random)
    {
        _path = path;
        _document = document;
        _random = random;
    }

    public Guid ServerId => _document.ServerId;

    public static async Task<DaemonCredentialRegistry> OpenAsync(string path, CancellationToken cancellationToken,
        IDaemonRandomSource? random = null)
    {
        random ??= new SystemDaemonRandomSource();
        ValidatePath(path);
        if (!File.Exists(path))
        {
            var serverId = random.NewGuid();
            if (serverId == Guid.Empty) throw new InvalidOperationException("The secure random source returned an empty server ID.");
            var created = new RegistryDocument(1, serverId, []);
            var registry = new DaemonCredentialRegistry(path, created, random);
            await registry.WriteAsync(created, cancellationToken);
            return registry;
        }

        CheckFile(path);
        if (new FileInfo(path).Length is <= 0 or > MaximumFileBytes) throw Corrupt();
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
            using var json = await JsonDocument.ParseAsync(stream,
                new JsonDocumentOptions { MaxDepth = 16, CommentHandling = JsonCommentHandling.Disallow }, cancellationToken);
            RejectDuplicateProperties(json.RootElement);
            var document = json.RootElement.Deserialize<RegistryDocument>(JsonOptions) ?? throw Corrupt();
            Validate(document);
            return new DaemonCredentialRegistry(path, document, random);
        }
        catch (JsonException exception) { throw new SnookException(SnookErrorCode.StoreUnavailable,
            "The daemon host-auth registry is corrupt or from an unsupported future version. Move it aside only through the documented recovery procedure; paired devices remain denied.", exception); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new SnookException(SnookErrorCode.StoreUnavailable,
                "The daemon host-auth registry cannot be read safely. Paired devices remain denied.", exception);
        }
    }

    public AuthenticatedDevice? Authenticate(string supplied, string? tailscaleLogin, DateTimeOffset now)
    {
        if (supplied.Length is < 32 or > 512) return null;
        now = now.ToUniversalTime();
        var token = Encoding.UTF8.GetBytes(supplied);
        var document = Volatile.Read(ref _document);
        try
        {
            DeviceRecord? matched = null;
            // Check every record to avoid leaking its position and use fixed-time
            // comparisons for each verifier.
            foreach (var record in document.Devices)
            {
                var verifier = ComputeVerifier(record.Salt, token);
                if (CryptographicOperations.FixedTimeEquals(verifier, record.Verifier) && !record.Revoked
                    && (record.PendingUntilUtc is null || record.PendingUntilUtc > now)) matched = record;
            }
            return matched is null ? null : new AuthenticatedDevice(matched.CredentialId, tailscaleLogin,
                false, matched.PendingUntilUtc is not null);
        }
        finally { CryptographicOperations.ZeroMemory(token); }
    }

    public IReadOnlyList<PairedDeviceInfo> List()
        => Volatile.Read(ref _document).Devices.Select(record => new PairedDeviceInfo(record.CredentialId, record.DisplayName,
            record.CreatedAtUtc, record.LastSeenAtUtc, record.TailscaleLogin, record.Scopes, record.Revoked,
            record.PendingUntilUtc)).ToArray();

    public bool IsActive(Guid credentialId)
        => credentialId != Guid.Empty && Volatile.Read(ref _document).Devices.Any(
            record => record.CredentialId == credentialId && !record.Revoked
                && record.PendingUntilUtc is null);

    public async Task<(Guid Id, string Credential)> CreateAsync(string displayName, string? tailscaleLogin,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        displayName = PairingSessions.NormalizeName(displayName, "device name", 100);
        var suppliedLogin = tailscaleLogin;
        tailscaleLogin = PairingSessions.NormalizeLogin(tailscaleLogin);
        if (!string.IsNullOrWhiteSpace(suppliedLogin) && tailscaleLogin is null)
            throw new SnookException(SnookErrorCode.ValidationFailed, "The paired Tailscale login is invalid or too long.");
        now = now.ToUniversalTime();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var retained = _document.Devices;
            if (retained.Length >= MaximumRecords)
            {
                var oldestReclaimable = Array.FindIndex(retained, candidate => IsReclaimable(candidate, now)
                    && !retained.Any(other => IsReclaimable(other, now)
                        && other.CreatedAtUtc < candidate.CreatedAtUtc));
                if (oldestReclaimable < 0)
                    throw new SnookException(SnookErrorCode.ValidationFailed,
                        $"At most {MaximumRecords} active paired-device records are allowed; revoke one before pairing another.");
                retained = [.. retained[..oldestReclaimable], .. retained[(oldestReclaimable + 1)..]];
            }
            var credentialRandom = SecureBytes(32);
            string credential;
            try { credential = Base64Url(credentialRandom); }
            finally { CryptographicOperations.ZeroMemory(credentialRandom); }
            var salt = SecureBytes(32);
            var credentialId = _random.NewGuid();
            if (credentialId == Guid.Empty) throw new InvalidOperationException("The secure random source returned an empty credential ID.");
            var credentialBytes = Encoding.UTF8.GetBytes(credential);
            byte[] verifier;
            try { verifier = ComputeVerifier(salt, credentialBytes); }
            finally { CryptographicOperations.ZeroMemory(credentialBytes); }
            var record = new DeviceRecord(credentialId, displayName, now, null, tailscaleLogin,
                ["workspace:full"], false, salt, verifier, now + PendingCredentialLifetime);
            var updated = _document with { Devices = [.. retained, record] };
            await WriteAsync(updated, cancellationToken);
            Volatile.Write(ref _document, updated);
            return (record.CredentialId, credential);
        }
        finally { _gate.Release(); }
    }

    public async Task RevokeAsync(Guid credentialId, CancellationToken cancellationToken)
    {
        if (credentialId == Guid.Empty) throw new SnookException(SnookErrorCode.ValidationFailed, "Credential ID is required.");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var found = false;
            var devices = new DeviceRecord[_document.Devices.Length];
            for (var index = 0; index < devices.Length; index++)
            {
                var record = _document.Devices[index];
                if (record.CredentialId == credentialId)
                {
                    record = record with { Revoked = true, PendingUntilUtc = null };
                    found = true;
                }
                devices[index] = record;
            }
            if (!found) throw new SnookException(SnookErrorCode.NotFound, "Paired device was not found.");
            var updated = _document with { Devices = devices };
            await WriteAsync(updated, cancellationToken);
            Volatile.Write(ref _document, updated);
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> PromoteAsync(Guid credentialId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (credentialId == Guid.Empty) return false;
        now = now.ToUniversalTime();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var devices = _document.Devices.ToArray();
            var index = Array.FindIndex(devices, record => record.CredentialId == credentialId && !record.Revoked);
            if (index < 0) return false;
            if (devices[index].PendingUntilUtc is null) return true;
            if (devices[index].PendingUntilUtc <= now) return false;
            devices[index] = devices[index] with { PendingUntilUtc = null };
            var updated = _document with { Devices = devices };
            await WriteAsync(updated, cancellationToken);
            Volatile.Write(ref _document, updated);
            return true;
        }
        finally { _gate.Release(); }
    }

    public async Task TouchAsync(Guid credentialId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (credentialId == Guid.Empty) return;
        now = now.ToUniversalTime();
        var snapshot = Volatile.Read(ref _document).Devices.FirstOrDefault(record => record.CredentialId == credentialId);
        if (snapshot is null || snapshot.Revoked || snapshot.PendingUntilUtc is not null
            || snapshot.LastSeenAtUtc is { } lastSeen && now - lastSeen < LastSeenWriteInterval) return;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var devices = _document.Devices.ToArray();
            var index = Array.FindIndex(devices, record => record.CredentialId == credentialId
                && !record.Revoked && record.PendingUntilUtc is null);
            if (index < 0 || devices[index].LastSeenAtUtc is { } current && now - current < LastSeenWriteInterval) return;
            devices[index] = devices[index] with { LastSeenAtUtc = now };
            var updated = _document with { Devices = devices };
            await WriteAsync(updated, cancellationToken);
            Volatile.Write(ref _document, updated);
        }
        finally { _gate.Release(); }
    }

    private async Task WriteAsync(RegistryDocument document, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
        if (bytes.Length > MaximumFileBytes) throw Corrupt();
        var staging = _path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
                Options = FileOptions.Asynchronous | FileOptions.WriteThrough };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var stream = new FileStream(staging, options))
            {
                await stream.WriteAsync(bytes, cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            ValidatePath(_path);
            if (File.Exists(_path)) CheckFile(_path);
            File.Move(staging, _path, true);
        }
        finally { if (File.Exists(staging)) File.Delete(staging); }
    }

    private static void Validate(RegistryDocument document)
    {
        if (document.Version != 1 || document.ServerId == Guid.Empty || document.Devices is null
            || document.Devices.Length > MaximumRecords
            || document.Devices.Any(record => record is null)
            || document.Devices.Select(record => record.CredentialId).Distinct().Count() != document.Devices.Length
            || document.Devices.Any(record => record.CredentialId == Guid.Empty
                || record.Salt is not { Length: 32 } || record.Verifier is not { Length: 32 }
                || string.IsNullOrEmpty(record.DisplayName) || record.DisplayName.Length > 100
                || record.CreatedAtUtc == default || record.CreatedAtUtc.Offset != TimeSpan.Zero
                || record.LastSeenAtUtc is { Offset: var offset } && offset != TimeSpan.Zero
                || record.LastSeenAtUtc < record.CreatedAtUtc
                || record.PendingUntilUtc is { Offset: var pendingOffset } && pendingOffset != TimeSpan.Zero
                || record.PendingUntilUtc <= record.CreatedAtUtc
                || record.Revoked && record.PendingUntilUtc is not null
                || record.PendingUntilUtc is not null && record.LastSeenAtUtc is not null
                || record.DisplayName.Any(char.IsControl)
                || record.TailscaleLogin is not null && PairingSessions.NormalizeLogin(record.TailscaleLogin) != record.TailscaleLogin
                || record.Scopes is null || record.Scopes.Length is < 1 or > 8
                || record.Scopes.Distinct(StringComparer.Ordinal).Count() != record.Scopes.Length
                || record.Scopes.Any(scope => string.IsNullOrEmpty(scope) || scope.Length > 64 || scope.Any(char.IsControl)))) throw Corrupt();
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
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

    private static byte[] ComputeVerifier(byte[] salt, byte[] token)
    {
        var input = new byte[salt.Length + token.Length];
        try
        {
            salt.CopyTo(input, 0);
            token.CopyTo(input, salt.Length);
            return SHA256.HashData(input);
        }
        finally { CryptographicOperations.ZeroMemory(input); }
    }

    private byte[] SecureBytes(int count)
    {
        var value = _random.GetBytes(count);
        return value.Length == count ? value
            : throw new InvalidOperationException("The secure random source returned an invalid byte count.");
    }

    private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static bool IsReclaimable(DeviceRecord record, DateTimeOffset now)
        => record.Revoked || record.PendingUntilUtc is not null && record.PendingUntilUtc <= now;

    private static void ValidatePath(string path)
    {
        for (string? current = Path.GetDirectoryName(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw Corrupt();
        if (File.Exists(path)) CheckFile(path);
    }

    private static void CheckFile(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0
            || !OperatingSystem.IsWindows() && (File.GetUnixFileMode(path) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite
                | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0) throw Corrupt();
    }

    private static SnookException Corrupt() => new(SnookErrorCode.StoreUnavailable,
        "The daemon host-auth registry is unsafe, corrupt, oversized, or from an unsupported future version. Paired devices remain denied.");

    public void Dispose() => _gate.Dispose();

    private sealed record RegistryDocument(int Version, Guid ServerId, DeviceRecord[] Devices);
    private sealed record DeviceRecord(Guid CredentialId, string DisplayName, DateTimeOffset CreatedAtUtc,
        DateTimeOffset? LastSeenAtUtc, string? TailscaleLogin, string[] Scopes, bool Revoked,
        byte[] Salt, byte[] Verifier, DateTimeOffset? PendingUntilUtc = null);
}

internal sealed class PairingSessions
{
    private const int MaximumSessions = 16;
    private const int MaximumAttempts = 8;
    private const int MaximumGlobalAttempts = 128;
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    private readonly TimeProvider _timeProvider;
    private readonly IDaemonRandomSource _random;
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SourceAttempts> _sourceAttempts = new(StringComparer.Ordinal);
    private readonly object _sync = new();
    private DateTimeOffset _globalWindowStartedAtUtc;
    private int _globalAttempts;

    public PairingSessions(TimeProvider? timeProvider = null, IDaemonRandomSource? random = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _random = random ?? new SystemDaemonRandomSource();
    }
    public DateTimeOffset UtcNow => _timeProvider.GetUtcNow();

    public PairingInvitation Arm(string deviceName, Uri serveOrigin, string? intendedLogin)
    {
        deviceName = NormalizeName(deviceName, "device name", 100);
        var suppliedLogin = intendedLogin;
        intendedLogin = NormalizeLogin(suppliedLogin);
        if (!string.IsNullOrWhiteSpace(suppliedLogin) && intendedLogin is null)
            throw new SnookException(SnookErrorCode.ValidationFailed, "The intended Tailscale login is invalid or too long.");
        var origin = DaemonEndpoint.Parse(serveOrigin.OriginalString, DaemonEndpointKind.TailscaleServe).Origin;
        var now = _timeProvider.GetUtcNow();
        var randomBytes = _random.GetBytes(16);
        if (randomBytes.Length != 16) throw new InvalidOperationException("The secure random source returned an invalid byte count.");
        string code;
        try { code = Base64Url(randomBytes); }
        finally { CryptographicOperations.ZeroMemory(randomBytes); }
        var key = Key(code);
        lock (_sync)
        {
            Prune(now);
            if (_sessions.Count >= MaximumSessions)
                throw new SnookException(SnookErrorCode.ValidationFailed, $"At most {MaximumSessions} pairing sessions may be armed at once.");
            _sessions.Add(key, new Session(deviceName, origin, intendedLogin, now + Lifetime, 0));
        }
        return new PairingInvitation(1, origin.AbsoluteUri, code, now + Lifetime, deviceName, intendedLogin);
    }

    public bool Cancel(string? code)
    {
        var key = KeyBounded(code);
        lock (_sync) return key is not null && _sessions.Remove(key);
    }

    public Session? Redeem(string? code, string sourceSignal, string? login, string? deviceName)
    {
        var now = _timeProvider.GetUtcNow();
        var key = KeyBounded(code);
        login = NormalizeLogin(login);
        sourceSignal = NormalizeSource(sourceSignal);
        var normalizedDeviceName = TryNormalizeName(deviceName, 100);
        lock (_sync)
        {
            Prune(now);
            if (IsGloballyLimited(now)) return null;
            if (IsSourceLimited(sourceSignal, now)) return null;
            if (key is null || !_sessions.TryGetValue(key, out var session)) return null;
            session = session with { Attempts = session.Attempts + 1 };
            if (session.Attempts > MaximumAttempts)
            {
                _sessions.Remove(key);
                return null;
            }
            if (session.IntendedLogin is not null && !string.Equals(session.IntendedLogin, login, StringComparison.Ordinal)
                || !string.Equals(session.DeviceName, normalizedDeviceName, StringComparison.Ordinal))
            {
                _sessions[key] = session;
                return null;
            }
            _sessions.Remove(key);
            return session;
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _sessions.Clear();
            _sourceAttempts.Clear();
            _globalWindowStartedAtUtc = default;
            _globalAttempts = 0;
        }
    }

    private bool IsSourceLimited(string source, DateTimeOffset now)
    {
        var entry = _sourceAttempts.GetValueOrDefault(source);
        if (entry is null || now - entry.WindowStartedAtUtc >= Lifetime) entry = new SourceAttempts(now, 0);
        entry = entry with { Attempts = entry.Attempts + 1 };
        _sourceAttempts[source] = entry;
        return entry.Attempts > MaximumAttempts;
    }

    private bool IsGloballyLimited(DateTimeOffset now)
    {
        if (_globalWindowStartedAtUtc == default || now - _globalWindowStartedAtUtc >= Lifetime)
        {
            _globalWindowStartedAtUtc = now;
            _globalAttempts = 0;
        }
        _globalAttempts++;
        return _globalAttempts > MaximumGlobalAttempts;
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var key in _sessions.Where(item => item.Value.ExpiresAtUtc <= now).Select(item => item.Key).ToArray()) _sessions.Remove(key);
        foreach (var key in _sourceAttempts.Where(item => now - item.Value.WindowStartedAtUtc >= Lifetime).Select(item => item.Key).ToArray()) _sourceAttempts.Remove(key);
        if (_sourceAttempts.Count > 128)
            foreach (var key in _sourceAttempts.OrderBy(item => item.Value.WindowStartedAtUtc).Take(_sourceAttempts.Count - 128).Select(item => item.Key).ToArray()) _sourceAttempts.Remove(key);
    }

    internal static string NormalizeName(string value, string label, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new SnookException(SnookErrorCode.ValidationFailed, $"{label} is required.");
        value = value.Trim();
        if (value.Length > maximum || value.Any(char.IsControl)) throw new SnookException(SnookErrorCode.ValidationFailed, $"{label} is too long or contains control characters.");
        return value;
    }

    private static string? TryNormalizeName(string? value, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();
        return value.Length <= maximum && !value.Any(char.IsControl) ? value : null;
    }

    public static string? NormalizeLogin(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 254) return null;
        value = value.Trim().Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        return value.Length <= 254 && !value.Any(character => char.IsControl(character) || char.IsWhiteSpace(character))
            ? value
            : null;
    }

    private static string NormalizeSource(string value) => value.Length <= 300 ? value : value[..300];
    private static string? KeyBounded(string? code) => code is { Length: >= 20 and <= 64 }
        && code.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_') ? Key(code) : null;
    private static string Key(string code)
    {
        var bytes = Encoding.UTF8.GetBytes(code);
        try { return Convert.ToHexString(SHA256.HashData(bytes)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal sealed record Session(string DeviceName, Uri ServeOrigin, string? IntendedLogin, DateTimeOffset ExpiresAtUtc, int Attempts);
    private sealed record SourceAttempts(DateTimeOffset WindowStartedAtUtc, int Attempts);
}
