using System.Security.Cryptography;
using Snook.Application;

namespace Snook.Android;

internal sealed class EncryptedTaskEditorStore(string directory, Guid workspaceId)
{
    private readonly string _path = Path.Combine(directory, "task-editor-draft.bin");
    private readonly KeystoreProtector _protector = new("org.snook.task-editor.v1",
        TaskEditorRecoveryDraft.MaximumEncodedBytes);
    private readonly string _purpose = $"task-editor-{workspaceId:N}";
    private readonly object _gate = new();
    private Task _writes = Task.CompletedTask;

    internal Task<TaskEditorRecoveryDraft?> LoadAsync() => Task.Run(() =>
    {
        if (!File.Exists(_path)) return null;
        using var stream = File.OpenRead(_path);
        if (stream.Length is < 1 or > TaskEditorRecoveryDraft.MaximumEncodedBytes + 64)
            throw new InvalidDataException("Invalid encrypted task-editor recovery size.");
        var encrypted = new byte[(int)stream.Length];
        stream.ReadExactly(encrypted);
        var plain = _protector.Unprotect(encrypted, _purpose);
        try { return TaskEditorRecoveryDraft.Decode(plain, workspaceId, DateTimeOffset.UtcNow); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    });

    internal Task SaveAsync(TaskEditorRecoveryDraft? draft)
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
                    return;
                }
                var plain = draft.Encode();
                byte[] encrypted;
                try { encrypted = _protector.Protect(plain, _purpose); }
                finally { CryptographicOperations.ZeroMemory(plain); }
                Directory.CreateDirectory(directory);
                var staging = _path + ".tmp";
                try
                {
                    using (var stream = new FileStream(staging, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        stream.Write(encrypted);
                        stream.Flush(flushToDisk: true);
                    }
                    File.Move(staging, _path, overwrite: true);
                }
                finally { File.Delete(staging); }
            });
            return _writes;
        }
    }
}
