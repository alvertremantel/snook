using System.Security.Cryptography;
using System.Text;
using Snook.Application;

namespace Snook.Android;

internal sealed record AttachedProfileCredential(TailnetAttachedProfile Profile, string Credential);

/// <summary>
/// Stores a catalog of non-secret paired-daemon profiles separately from their
/// credential-id-named Keystore envelopes. One tiny atomic selector identifies
/// the profile used by background/native surfaces. The old singleton profile is
/// migrated without rewriting or decrypting its credential envelope.
/// </summary>
internal sealed class AttachedProfileStore : IDisposable
{
    private const int CredentialLength = 64;
    private const int MaximumEnvelopeBytes = 128;
    private const int MaximumProfiles = 16;
    private readonly string _directory;
    private readonly string _profilesDirectory;
    private readonly string _selectionPath;
    private readonly string _legacyProfilePath;
    private readonly KeystoreProtector _protector = new("org.snook.attached-credential.v1", CredentialLength);
    private readonly SemaphoreSlim _gate = new(1, 1);

    internal AttachedProfileStore(string noBackupRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(noBackupRoot);
        _directory = Path.Combine(noBackupRoot, "attached");
        _profilesDirectory = Path.Combine(_directory, "profiles");
        _selectionPath = Path.Combine(_directory, "selected.txt");
        _legacyProfilePath = Path.Combine(_directory, "profile.json");
    }

    internal bool HasProfile => File.Exists(_legacyProfilePath)
        || Directory.Exists(_profilesDirectory)
        && Directory.EnumerateFiles(_profilesDirectory, "profile-*.json").Any();

    internal IReadOnlyList<TailnetAttachedProfile> ListProfiles()
    {
        var profiles = ListProfilesUnsafe(includeLegacy: true);
        if (profiles.Count > MaximumProfiles)
            throw new InvalidDataException("Too many paired daemon profiles are stored.");
        return profiles.OrderBy(profile => profile.DeviceName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(profile => profile.ServeOrigin.Host, StringComparer.OrdinalIgnoreCase)
            .ThenBy(profile => profile.CredentialId).ToArray();
    }

    internal async Task<AttachedProfileCredential?> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await MigrateLegacyAsync(cancellationToken).ConfigureAwait(false);
            var profiles = ListProfilesUnsafe(includeLegacy: false);
            var selected = ReadSelection();
            if (selected is null && profiles.Count == 1)
            {
                selected = profiles[0].CredentialId;
                await ReplaceAsync(_selectionPath,
                    Encoding.ASCII.GetBytes(selected.Value.ToString("N")), cancellationToken).ConfigureAwait(false);
            }
            if (selected is null) return null;
            var profile = profiles.SingleOrDefault(item => item.CredentialId == selected.Value)
                ?? throw new InvalidDataException("The selected paired daemon profile is missing.");
            return await LoadCredentialAsync(profile, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    internal async Task SelectAsync(Guid credentialId,
        CancellationToken cancellationToken = default)
    {
        if (credentialId == Guid.Empty) throw new ArgumentException("Credential identity is required.", nameof(credentialId));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await MigrateLegacyAsync(cancellationToken).ConfigureAwait(false);
            if (!File.Exists(ProfilePath(credentialId)))
                throw new InvalidDataException("The selected paired daemon profile is missing.");
            await ReplaceAsync(_selectionPath,
                Encoding.ASCII.GetBytes(credentialId.ToString("N")), cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    internal async Task SaveAsync(DaemonPairingCredential paired,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paired);
        var profile = TailnetAttachedProfile.FromCredential(paired);
        if (paired.Credential.Length != CredentialLength
            || !paired.Credential.All(char.IsAsciiHexDigit))
            throw new InvalidDataException("The paired credential is invalid.");

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await MigrateLegacyAsync(cancellationToken).ConfigureAwait(false);
            var profiles = ListProfilesUnsafe(includeLegacy: false);
            if (profiles.All(item => item.CredentialId != profile.CredentialId)
                && profiles.Count >= MaximumProfiles)
                throw new InvalidDataException("Remove an unused pairing before adding another one.");
            EnsurePrivateDirectories();
            var plaintext = Encoding.ASCII.GetBytes(paired.Credential);
            byte[] envelope;
            try { envelope = _protector.Protect(plaintext, Purpose(profile)); }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
            var credentialPath = CredentialPath(profile.CredentialId);
            await ReplaceAsync(credentialPath, envelope, cancellationToken).ConfigureAwait(false);
            await ReplaceAsync(ProfilePath(profile.CredentialId), profile.Encode(),
                cancellationToken).ConfigureAwait(false);
            // Commit selection last. A crash leaves either the old selected profile
            // or a complete new profile+credential pair; no metadata/secret mix-up.
            await ReplaceAsync(_selectionPath,
                Encoding.ASCII.GetBytes(profile.CredentialId.ToString("N")), cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    internal async Task ClearSelectedAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await MigrateLegacyAsync(cancellationToken).ConfigureAwait(false);
            var selected = ReadSelection();
            if (selected is not null)
            {
                File.Delete(ProfilePath(selected.Value));
                File.Delete(CredentialPath(selected.Value));
            }
            File.Delete(_selectionPath);
        }
        finally { _gate.Release(); }
    }

    internal async Task RenameSelectedAsync(string deviceName,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await MigrateLegacyAsync(cancellationToken).ConfigureAwait(false);
            var selected = ReadSelection()
                ?? throw new InvalidDataException("Select a paired daemon profile before renaming it.");
            var path = ProfilePath(selected);
            var profile = TailnetAttachedProfile.Decode(await ReadBoundedAsync(path,
                TailnetAttachedProfile.MaximumEncodedBytes, cancellationToken).ConfigureAwait(false));
            var renamed = TailnetAttachedProfile.Validate(profile with { DeviceName = deviceName.Trim() });
            await ReplaceAsync(path, renamed.Encode(), cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    internal async Task RebindSelectedWorkspaceAsync(Guid expectedWorkspaceId,
        Guid restoredWorkspaceId, CancellationToken cancellationToken = default)
    {
        if (expectedWorkspaceId == Guid.Empty || restoredWorkspaceId == Guid.Empty)
            throw new ArgumentException("Current and restored workspace identities are required.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await MigrateLegacyAsync(cancellationToken).ConfigureAwait(false);
            var selected = ReadSelection()
                ?? throw new InvalidDataException("Select a paired daemon profile before rebinding it.");
            var path = ProfilePath(selected);
            var profile = TailnetAttachedProfile.Decode(await ReadBoundedAsync(path,
                TailnetAttachedProfile.MaximumEncodedBytes, cancellationToken).ConfigureAwait(false));
            if (profile.WorkspaceId != expectedWorkspaceId)
                throw new InvalidDataException("The selected pairing changed workspace before restore handoff.");
            var rebound = TailnetAttachedProfile.Validate(profile with
            {
                WorkspaceId = restoredWorkspaceId
            });
            await ReplaceAsync(path, rebound.Encode(), cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task<AttachedProfileCredential> LoadCredentialAsync(TailnetAttachedProfile profile,
        CancellationToken cancellationToken)
    {
        var credentialPath = CredentialPath(profile.CredentialId);
        if (!File.Exists(credentialPath))
            throw new InvalidDataException("The paired credential is missing. Re-pairing is required.");
        var envelope = await ReadBoundedAsync(credentialPath, MaximumEnvelopeBytes,
            cancellationToken).ConfigureAwait(false);
        var plaintext = _protector.Unprotect(envelope, Purpose(profile));
        try
        {
            if (plaintext.Length != CredentialLength)
                throw new InvalidDataException("The paired credential is invalid. Re-pairing is required.");
            var credential = Encoding.ASCII.GetString(plaintext);
            if (credential.Length != CredentialLength || !credential.All(char.IsAsciiHexDigit))
                throw new InvalidDataException("The paired credential is invalid. Re-pairing is required.");
            return new(profile, credential);
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    private async Task MigrateLegacyAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_legacyProfilePath)) return;
        var bytes = await ReadBoundedAsync(_legacyProfilePath,
            TailnetAttachedProfile.MaximumEncodedBytes, cancellationToken).ConfigureAwait(false);
        var profile = TailnetAttachedProfile.Decode(bytes);
        EnsurePrivateDirectories();
        var path = ProfilePath(profile.CredentialId);
        if (!File.Exists(path)) await ReplaceAsync(path, bytes, cancellationToken).ConfigureAwait(false);
        if (!File.Exists(_selectionPath))
            await ReplaceAsync(_selectionPath,
                Encoding.ASCII.GetBytes(profile.CredentialId.ToString("N")), cancellationToken).ConfigureAwait(false);
        File.Delete(_legacyProfilePath);
    }

    private List<TailnetAttachedProfile> ListProfilesUnsafe(bool includeLegacy)
    {
        var result = new Dictionary<Guid, TailnetAttachedProfile>();
        if (includeLegacy && File.Exists(_legacyProfilePath))
        {
            var legacy = TailnetAttachedProfile.Decode(ReadBounded(_legacyProfilePath,
                TailnetAttachedProfile.MaximumEncodedBytes));
            result.Add(legacy.CredentialId, legacy);
        }
        if (!Directory.Exists(_profilesDirectory)) return result.Values.ToList();
        if ((File.GetAttributes(_profilesDirectory) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Attached profile storage must not be a symbolic link.");
        foreach (var path in Directory.EnumerateFiles(_profilesDirectory, "profile-*.json"))
        {
            if (result.Count >= MaximumProfiles + 1)
                throw new InvalidDataException("Too many paired daemon profiles are stored.");
            var profile = TailnetAttachedProfile.Decode(ReadBounded(path,
                TailnetAttachedProfile.MaximumEncodedBytes));
            if (!string.Equals(path, ProfilePath(profile.CredentialId), StringComparison.Ordinal))
                throw new InvalidDataException("A paired daemon profile filename is invalid.");
            if (!result.TryAdd(profile.CredentialId, profile))
                throw new InvalidDataException("A paired daemon profile is duplicated.");
        }
        return result.Values.ToList();
    }

    private Guid? ReadSelection()
    {
        if (!File.Exists(_selectionPath)) return null;
        var text = Encoding.ASCII.GetString(ReadBounded(_selectionPath, 32));
        return Guid.TryParseExact(text, "N", out var selected) && selected != Guid.Empty
            ? selected : throw new InvalidDataException("The selected paired daemon profile is invalid.");
    }

    private void EnsurePrivateDirectories()
    {
        Directory.CreateDirectory(_directory);
        Directory.CreateDirectory(_profilesDirectory);
        if ((File.GetAttributes(_directory) & FileAttributes.ReparsePoint) != 0
            || (File.GetAttributes(_profilesDirectory) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Attached profile storage must not be a symbolic link.");
    }

    private string ProfilePath(Guid credentialId)
    {
        if (credentialId == Guid.Empty) throw new InvalidDataException("Credential identity is required.");
        return Path.Combine(_profilesDirectory, $"profile-{credentialId:N}.json");
    }

    private string CredentialPath(Guid credentialId)
    {
        if (credentialId == Guid.Empty) throw new InvalidDataException("Credential identity is required.");
        return Path.Combine(_directory, $"credential-{credentialId:N}.bin");
    }

    private static string Purpose(TailnetAttachedProfile profile)
        => $"attached-{profile.InstallationId:N}-{profile.CredentialId:N}";

    private static byte[] ReadBounded(string path, int maximum)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Attached profile files must not be symbolic links.");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 4096, FileOptions.SequentialScan);
        if (stream.Length is < 1 || stream.Length > maximum)
            throw new InvalidDataException("Attached profile storage has an invalid size.");
        var data = new byte[(int)stream.Length];
        stream.ReadExactly(data);
        return data;
    }

    private static async Task<byte[]> ReadBoundedAsync(string path, int maximum,
        CancellationToken cancellationToken)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Attached profile files must not be symbolic links.");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length is < 1 || stream.Length > maximum)
            throw new InvalidDataException("Attached profile storage has an invalid size.");
        var data = new byte[(int)stream.Length];
        await stream.ReadExactlyAsync(data, cancellationToken).ConfigureAwait(false);
        return data;
    }

    private static async Task ReplaceAsync(string path, byte[] data,
        CancellationToken cancellationToken)
    {
        var staging = path + ".tmp";
        try
        {
            await using (var stream = new FileStream(staging, FileMode.Create,
                FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(staging, path, overwrite: true);
        }
        finally { File.Delete(staging); }
    }

    public void Dispose() => _gate.Dispose();
}
