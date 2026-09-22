using System.Security.Cryptography;
using Snook.Application;

namespace Snook.Android;

internal sealed class EncryptedUtilityEditorStore(string directory, Guid workspaceId)
{
    private readonly string _path = Path.Combine(directory, "utility-editor-draft.bin");
    private readonly KeystoreProtector _protector = new("org.snook.utility-editor.v1",
        UtilityEditorRecoveryDraft.MaximumEncodedBytes);
    private readonly string _purpose = $"utility-editor-{workspaceId:N}";
    private readonly object _gate = new();
    private Task _writes = Task.CompletedTask;
    private UtilityEditorRecoveryDraft? _lastDraft;

    internal Task<UtilityEditorRecoveryDraft?> LoadAsync() => Task.Run(() =>
    {
        if (!File.Exists(_path)) return null;
        using var stream = File.OpenRead(_path);
        if (stream.Length is < 1 or > UtilityEditorRecoveryDraft.MaximumEncodedBytes + 64)
            throw new InvalidDataException("Invalid encrypted utility-editor recovery size.");
        var encrypted = new byte[(int)stream.Length];
        stream.ReadExactly(encrypted);
        var plain = _protector.Unprotect(encrypted, _purpose);
        try
        {
            var draft = UtilityEditorRecoveryDraft.Decode(plain, workspaceId,
                DateTimeOffset.UtcNow);
            _lastDraft = draft;
            return draft;
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    });

    internal Task SaveAsync(UtilityEditorRecoveryDraft? draft)
    {
        lock (_gate)
        {
            var previous = _writes;
            _writes = Task.Run(async () =>
            {
                try { await previous.ConfigureAwait(false); }
                catch (Exception) { /* A failed write must not prevent an explicit retry. */ }
                if (draft is null)
                {
                    File.Delete(_path);
                    _lastDraft = null;
                    return;
                }
                if (_lastDraft is { } previousDraft && Equivalent(previousDraft, draft)) return;
                var plain = draft.Encode();
                byte[] encrypted;
                try { encrypted = _protector.Protect(plain, _purpose); }
                finally { CryptographicOperations.ZeroMemory(plain); }
                Directory.CreateDirectory(directory);
                var staging = _path + ".tmp";
                try
                {
                    using (var stream = new FileStream(staging, FileMode.Create,
                               FileAccess.Write, FileShare.None))
                    {
                        stream.Write(encrypted);
                        stream.Flush(flushToDisk: true);
                    }
                    File.Move(staging, _path, overwrite: true);
                    _lastDraft = draft;
                }
                finally
                {
                    File.Delete(staging);
                }
            });
            return _writes;
        }
    }

    private static bool Equivalent(UtilityEditorRecoveryDraft left,
        UtilityEditorRecoveryDraft right)
        => left.Version == right.Version
           && left.WorkspaceId == right.WorkspaceId
           && left.Kind == right.Kind
           && left.EntityId == right.EntityId
           && left.ExpectedRevision == right.ExpectedRevision
           && left.PendingRequest == right.PendingRequest
           && left.TargetRevisions.SequenceEqual(right.TargetRevisions)
           && left.Values.Count == right.Values.Count
           && left.Values.All(pair => right.Values.TryGetValue(pair.Key, out var value)
                                      && pair.Value == value);
}
