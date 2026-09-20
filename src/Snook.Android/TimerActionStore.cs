using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Snook.Android;

internal enum TimerActionKind : byte
{
    Open = 1,
    Pause = 2,
    Resume = 3,
    Stop = 4
}

internal sealed record TimerActionRecord(
    Guid WorkspaceId,
    Guid SessionId,
    long ExpectedRevision,
    TimerActionKind Kind,
    Guid OperationId,
    Guid ClientDeviceId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc);

/// <summary>
/// Bounded, Keystore-authenticated lookup for opaque PendingIntent tokens. One
/// small record per token lets a cold receiver recover the exact operation receipt
/// without putting workspace IDs, revisions or actions in intent extras.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "Owned for the Android application process lifetime.")]
internal sealed partial class TimerActionStore
{
    private const int PlainSize = 90;
    private const int MaximumFiles = 128;
    private readonly string _directory;
    private readonly KeystoreProtector _protector = new("org.snook.timer-actions.v1");
    private readonly SemaphoreSlim _gate = new(1, 1);

    internal TimerActionStore(string noBackupRoot)
    {
        _directory = Path.Combine(noBackupRoot, "timer-actions");
    }

    internal async Task<string> GetOrCreateAsync(Guid workspaceId, Guid sessionId, long revision,
        TimerActionKind kind, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        ValidateIdentity(workspaceId, sessionId, revision, kind);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => GetOrCreateCore(workspaceId, sessionId, revision, kind, now),
                cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    internal async Task<TimerActionRecord> ResolveAsync(string token, DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ValidateToken(token);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                var record = LoadCore(Path.Combine(_directory, token + ".bin"), token);
                if (record.ExpiresAtUtc <= now) throw new InvalidDataException("The timer action expired.");
                return record;
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    internal async Task RetainAsync(IReadOnlySet<string> tokens, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        foreach (var token in tokens) ValidateToken(token);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                if (!Directory.Exists(_directory)) return;
                foreach (var path in Directory.EnumerateFiles(_directory))
                {
                    var name = Path.GetFileName(path);
                    if (name.EndsWith(".tmp", StringComparison.Ordinal)
                        || (name.EndsWith(".bin", StringComparison.Ordinal)
                            && !tokens.Contains(name[..^4])))
                        File.Delete(path);
                }
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    internal Task ClearAsync(CancellationToken cancellationToken = default)
        => RetainAsync(new HashSet<string>(StringComparer.Ordinal), cancellationToken);

    private string GetOrCreateCore(Guid workspaceId, Guid sessionId, long revision,
        TimerActionKind kind, DateTimeOffset now)
    {
        Directory.CreateDirectory(_directory);
        var valid = new List<(string Token, TimerActionRecord Record)>();
        foreach (var path in Directory.EnumerateFiles(_directory, "*.bin").Take(MaximumFiles + 1))
        {
            var token = Path.GetFileNameWithoutExtension(path);
            try
            {
                ValidateToken(token);
                var existing = LoadCore(path, token);
                if (existing.ExpiresAtUtc <= now)
                {
                    File.Delete(path);
                    continue;
                }
                if (existing.WorkspaceId == workspaceId && existing.SessionId == sessionId
                    && existing.ExpectedRevision == revision && existing.Kind == kind)
                    return token;
                valid.Add((token, existing));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or InvalidDataException or CryptographicException or ArgumentException)
            {
                File.Delete(path);
            }
        }
        foreach (var stale in valid.OrderBy(item => item.Record.CreatedAtUtc)
                     .Take(Math.Max(0, valid.Count - MaximumFiles + 1)))
            File.Delete(Path.Combine(_directory, stale.Token + ".bin"));

        var created = new TimerActionRecord(workspaceId, sessionId, revision, kind,
            Guid.NewGuid(), Guid.NewGuid(), now, now.AddDays(7));
        var plain = Encode(created);
        try
        {
            for (var attempt = 0; attempt < 4; attempt++)
            {
                var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                var destination = Path.Combine(_directory, token + ".bin");
                if (File.Exists(destination)) continue;
                var encrypted = _protector.Protect(plain, Purpose(token));
                var staging = Path.Combine(_directory, token + ".tmp");
                try
                {
                    using (var stream = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        stream.Write(encrypted);
                        stream.Flush(flushToDisk: true);
                    }
                    File.Move(staging, destination);
                    return token;
                }
                finally { File.Delete(staging); }
            }
            throw new IOException("A unique timer action could not be created.");
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    private TimerActionRecord LoadCore(string path, string token)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is < 30 or > 256) throw new InvalidDataException("Invalid timer action size.");
        var encrypted = new byte[(int)stream.Length];
        stream.ReadExactly(encrypted);
        var plain = _protector.Unprotect(encrypted, Purpose(token));
        try { return Decode(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    private static byte[] Encode(TimerActionRecord record)
    {
        var bytes = new byte[PlainSize];
        bytes[0] = 1;
        record.WorkspaceId.TryWriteBytes(bytes.AsSpan(1, 16));
        record.SessionId.TryWriteBytes(bytes.AsSpan(17, 16));
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(33, 8), record.ExpectedRevision);
        bytes[41] = (byte)record.Kind;
        record.OperationId.TryWriteBytes(bytes.AsSpan(42, 16));
        record.ClientDeviceId.TryWriteBytes(bytes.AsSpan(58, 16));
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(74, 8), record.CreatedAtUtc.ToUnixTimeMilliseconds());
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(82, 8), record.ExpiresAtUtc.ToUnixTimeMilliseconds());
        return bytes;
    }

    private static TimerActionRecord Decode(byte[] bytes)
    {
        if (bytes.Length != PlainSize || bytes[0] != 1) throw new InvalidDataException("Invalid timer action record.");
        var record = new TimerActionRecord(new Guid(bytes.AsSpan(1, 16)), new Guid(bytes.AsSpan(17, 16)),
            BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(33, 8)), (TimerActionKind)bytes[41],
            new Guid(bytes.AsSpan(42, 16)), new Guid(bytes.AsSpan(58, 16)),
            DateTimeOffset.FromUnixTimeMilliseconds(BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(74, 8))),
            DateTimeOffset.FromUnixTimeMilliseconds(BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(82, 8))));
        ValidateIdentity(record.WorkspaceId, record.SessionId, record.ExpectedRevision, record.Kind);
        if (record.OperationId == Guid.Empty || record.ClientDeviceId == Guid.Empty
            || record.ExpiresAtUtc <= record.CreatedAtUtc || record.ExpiresAtUtc - record.CreatedAtUtc > TimeSpan.FromDays(8))
            throw new InvalidDataException("Invalid timer action fields.");
        return record;
    }

    private static void ValidateIdentity(Guid workspaceId, Guid sessionId, long revision, TimerActionKind kind)
    {
        if (workspaceId == Guid.Empty || sessionId == Guid.Empty || revision < 1
            || kind is < TimerActionKind.Open or > TimerActionKind.Stop)
            throw new ArgumentException("Invalid timer action identity.");
    }

    private static void ValidateToken(string token)
    {
        if (token is null || !TokenPattern().IsMatch(token)) throw new ArgumentException("Invalid timer action token.", nameof(token));
    }

    private static string Purpose(string token) => "timer-action-" + token;

    [GeneratedRegex("^[A-F0-9]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();
}
