using System.Security.Cryptography;
using Avalonia.Platform.Storage;
using Snook.Application;

namespace Snook.Android;

internal static class DocumentSpike
{
    private const int MaximumBytes = 65536;
    private static readonly byte[] Fixture = Enumerable.Range(0, 32768).Select(index => (byte)(index % 251)).ToArray();
    private static readonly FilePickerFileType FileType = new("Synthetic probe file")
    {
        Patterns = ["*.bin"], MimeTypes = ["application/octet-stream"]
    };

    internal static async Task<string> ExportAsync(IStorageProvider provider)
    {
        if (!provider.CanSave) return "Save picker unavailable.";
        using var selected = await provider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export synthetic probe only", SuggestedFileName = "snook-storage-probe.bin",
            FileTypeChoices = [FileType]
        });
        if (selected is null) return "Export cancelled. No workspace was changed.";
        ValidateSelection(selected);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var source = new MemoryStream(Fixture, writable: false);
        await using var destination = await selected.OpenWriteAsync();
        await BoundedStreamTransfer.CopyAsync(source, destination, MaximumBytes, timeout.Token);
        await destination.FlushAsync(timeout.Token);
        return "Exported 32768 synthetic bytes through a content URI. Reopen this file with Verify. This is not a workspace backup.";
    }

    internal static async Task<string> VerifyAsync(IStorageProvider provider)
    {
        if (!provider.CanOpen) return "Open picker unavailable.";
        var files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Verify synthetic probe only", AllowMultiple = false, FileTypeFilter = [FileType]
        });
        try
        {
            if (files.Count == 0) return "Verification cancelled. No workspace was changed.";
            if (files.Count != 1) throw new IOException("Exactly one document is required.");
            var selected = files[0];
            ValidateSelection(selected);
            var directory = global::Android.App.Application.Context.NoBackupFilesDir?.AbsolutePath
                ?? throw new IOException("Private staging is unavailable.");
            var stage = Path.Combine(directory, $"document-probe-{Guid.NewGuid():N}.tmp");
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await using var source = await selected.OpenReadAsync();
                await using (var destination = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    await BoundedStreamTransfer.CopyAsync(source, destination, MaximumBytes, timeout.Token);
                    await destination.FlushAsync(timeout.Token);
                }
                await using var staged = File.OpenRead(stage);
                var digest = await SHA256.HashDataAsync(staged, timeout.Token);
                if (staged.Length != Fixture.Length || !CryptographicOperations.FixedTimeEquals(digest, SHA256.HashData(Fixture)))
                    return "Rejected: selected file does not match the synthetic fixture. No workspace was changed.";
                return "PASS: content URI reopened, bounded private staging and exact SHA-256 verified. No workspace was changed; temporary stage removed.";
            }
            finally { File.Delete(stage); }
        }
        finally { foreach (var file in files) file.Dispose(); }
    }

    private static void ValidateSelection(IStorageFile file)
    {
        // Never translate an external URI or display name into a local filesystem path.
        if (!file.Path.IsAbsoluteUri || file.Path.Scheme != "content" || file.Path.OriginalString.Length > 8192)
            throw new IOException("A bounded content URI is required.");
    }
}
