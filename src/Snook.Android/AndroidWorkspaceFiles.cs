using System.Security.Cryptography;
using Android.Content;
using AndroidX.Core.Content;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Snook.Application;
using Snook.Contracts;

namespace Snook.Android;

internal static class AndroidWorkspaceFiles
{
    internal static async Task<string> ReviewBackupAsync(IBackendClient backend,
        Func<string, WorkspaceRestoreRequest, CancellationToken, Task<string>> restore,
        Func<RemoteBackupStage, CancellationToken, Task<string>>? restoreAttached = null)
    {
        string? stage = null;
        RemoteBackupStage? remoteStage = null;
        IBackendDocumentTransfer? remoteTransfer = null;
        var remoteCommitAttempted = false;
        var phase = "select document";
        try
        {
            var activity = MainActivity.Current;
            var provider = activity?.Content is Control view ? TopLevel.GetTopLevel(view)?.StorageProvider : null;
            if (activity?.CanReviewBackup() != true || provider?.CanOpen != true)
                return "Backup review unavailable. Unlock the device and return to the app.";
            var selection = await provider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Review backup · Snook · On this device",
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("Snook backup")
                    { Patterns = ["*.snook-backup"], MimeTypes = ["application/zip", "application/octet-stream"] }]
            });
            if (selection.Count == 0) return "Backup review cancelled. No workspace data was changed.";
            try
            {
                if (selection.Count != 1) throw new InvalidDataException("Select one backup document.");
                var selected = selection[0];
                if (!selected.Path.IsAbsoluteUri || selected.Path.Scheme != "content" || selected.Path.OriginalString.Length > 8192)
                    throw new InvalidDataException("Select a document through the Android picker.");
                // The picker may outlive its activity. Do not show content or continue
                // on a replacement/background activity; the user can select it again.
                if (!ReferenceEquals(MainActivity.Current, activity) || !activity.CanReviewBackup())
                    return "Backup review cancelled. Return to the app and select the document again.";
                var root = global::Android.App.Application.Context.NoBackupFilesDir?.AbsolutePath
                    ?? throw new IOException("Private staging unavailable.");
                using (var directory = new Java.IO.File(root)) root = directory.CanonicalPath;
                stage = Path.Combine(root, "document-review-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(stage);
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                phase = "stage selected document";
                string database;
                await using (var source = await selected.OpenReadAsync())
                    database = await Task.Run(() => WorkspaceBackupBundle.StageAsync(source, stage, timeout.Token), timeout.Token);
                if (restoreAttached is null && !HasRestoreSpace(root, new FileInfo(database).Length))
                    return "Not enough private storage to verify and restore this backup. Free space and retry. No workspace data was changed.";
                phase = "verify backup";
                BackupInspection incoming;
                BootstrapSnapshot current;
                if (restoreAttached is not null && backend is IBackendDocumentTransfer
                    { CanRestoreDocuments: true } transfer)
                {
                    remoteTransfer = transfer;
                    await using var bundle = File.OpenRead(Path.Combine(stage, "incoming.snook-backup"));
                    remoteStage = await transfer.StageRestoreBackupAsync(bundle, timeout.Token);
                    incoming = remoteStage.Incoming;
                    current = await backend.GetBootstrapAsync(timeout.Token);
                    if (current.Workspace.Id != remoteStage.CurrentWorkspaceId
                        || current.CommittedCursor != remoteStage.CurrentCursor)
                        return "The daemon workspace changed while the backup was uploaded. Review it again; nothing was restored.";
                }
                else
                {
                    incoming = await Task.Run(() => backend.InspectBackupAsync(database,
                        timeout.Token), timeout.Token);
                    current = await backend.GetBootstrapAsync(timeout.Token);
                }
                if (!ReferenceEquals(MainActivity.Current, activity) || !activity.CanReviewBackup())
                    return "Backup verified; review cancelled because the app left the foreground. No workspace data was changed.";
                phase = "show verified identity";
                var confirmed = await activity.ShowBackupReviewAsync(
                    $"{(remoteStage is null ? "On this device" : "Paired daemon")}\nCurrent: {current.Workspace.Name}\n{current.Workspace.Id}\n\n"
                    + $"Incoming: {incoming.Workspace.Name}\n{incoming.Workspace.Id}\n"
                    + $"Backup: {incoming.CreatedAtUtc:u}\n{incoming.Bytes:N0} bytes\n\n"
                    + "Manifest, SHA-256, SQLite integrity and supported schema checked.\n\n"
                    + "Replace all current workspace data with this backup? Unsaved drafts and capture recovery will be discarded. "
                    + "This does not merge data. The previous database is retained privately for recovery."
                    + (remoteStage is not null && incoming.Workspace.Id != current.Workspace.Id
                        ? " Other paired devices will be revoked because the daemon workspace identity changes."
                        : string.Empty));
                if (!confirmed || !ReferenceEquals(MainActivity.Current, activity) || !activity.CanReviewBackup())
                    return "Backup verified. Restore cancelled; no workspace data was changed.";
                if (remoteStage is null && !HasRestoreSpace(root, incoming.Bytes))
                    return "Not enough private storage to restore this backup. Free space and review again. No workspace data was changed.";
                // Once dispatched, only the handoff can describe whether replacement
                // committed. Its result must not be mislabeled as a review failure.
                phase = "restore handoff";
                if (remoteStage is not null && restoreAttached is not null)
                {
                    remoteCommitAttempted = true;
                    return await restoreAttached(remoteStage, timeout.Token);
                }
                return await restore(database, new WorkspaceRestoreRequest(current.Workspace.Id, current.CommittedCursor, incoming), timeout.Token);
            }
            finally { foreach (var selected in selection) selected.Dispose(); }
        }
        catch (Exception exception)
        {
            var reason = exception is Snook.Domain.SnookException failure ? failure.Code.ToString() : exception.GetType().Name;
            global::Android.Util.Log.Warn("SnookRestore", $"{phase}: {reason}");
            if (phase == "restore handoff")
                return "Restore outcome is uncertain. Restart the app and inspect the workspace before retrying.";
            return "Backup review failed. Select a complete Snook backup with its original manifest. No workspace data was changed.";
        }
        finally
        {
            if (remoteStage is not null && remoteTransfer is not null && !remoteCommitAttempted)
            {
                try { await remoteTransfer.DiscardStagedBackupAsync(remoteStage.UploadId); }
                catch (Exception exception)
                {
                    global::Android.Util.Log.Warn("SnookRestore", $"Remote discard: {exception.GetType().Name}");
                }
            }
            if (stage is not null)
            {
                try { Directory.Delete(stage, recursive: true); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    global::Android.Util.Log.Warn("SnookRestore", "Private review cleanup failed.");
                }
            }
        }
    }

    private static bool HasRestoreSpace(string root, long databaseBytes)
    {
        // Additional private staging, journal/migration headroom, and UI rebuild.
        // A conservative preflight, not a reservation: concurrent storage pressure
        // still relies on the backend's staged failure/rollback guarantees.
        using var storage = new global::Android.OS.StatFs(root);
        if (databaseBytes < 100 || databaseBytes > WorkspaceBackupBundle.MaximumDatabaseBytes)
            throw new InvalidDataException("Invalid restore database size.");
        var required = checked(databaseBytes * 4 + 64L * 1024 * 1024);
        return storage.AvailableBytes >= required;
    }

    internal static Task<string> BackupAsync(IBackendClient backend)
        => backend is IBackendDocumentTransfer { CanDownloadDocuments: true } transfer
            ? ExportAsync("Backup", "snook-backup", "application/zip", WorkspaceBackupBundle.MaximumBundleBytes,
                (stage, cancellation) => DownloadAsync(stage, "workspace.snook-backup",
                    (stream, token) => transfer.DownloadBackupAsync(stream, token), cancellation))
            : ExportAsync("Backup", "snook-backup", "application/zip", WorkspaceBackupBundle.MaximumBundleBytes,
                (stage, cancellation) => WorkspaceBackupBundle.CreateAsync(backend, stage, cancellation));

    internal static Task<string> JsonAsync(IBackendClient backend)
        => ExportAsync("JSON", "json", "application/json", WorkspaceBackupBundle.MaximumDatabaseBytes,
            (stage, cancellation) => CreateJsonAsync(backend, stage, cancellation));

    internal static Task<string> CsvAsync(IBackendClient backend, DateTimeOffset start, DateTimeOffset end)
        => ExportAsync("CSV", "csv", "text/csv", WorkspaceBackupBundle.MaximumDatabaseBytes,
            (stage, cancellation) => CreateCsvAsync(backend, stage, start, end, cancellation));

    internal static Task<string> ShareJsonAsync(IBackendClient backend)
        => ShareAsync("JSON", "application/json", WorkspaceBackupBundle.MaximumDatabaseBytes,
            (stage, cancellation) => CreateJsonAsync(backend, stage, cancellation));

    internal static Task<string> ShareCsvAsync(IBackendClient backend, DateTimeOffset start, DateTimeOffset end)
        => ShareAsync("CSV", "text/csv", WorkspaceBackupBundle.MaximumDatabaseBytes,
            (stage, cancellation) => CreateCsvAsync(backend, stage, start, end, cancellation));

    private static async Task<string> CreateJsonAsync(IBackendClient backend, string stage,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(stage, "workspace.json");
        if (backend is IBackendDocumentTransfer { CanDownloadDocuments: true } transfer)
            await DownloadAsync(stage, "workspace.json",
                (stream, token) => transfer.DownloadJsonAsync(stream, token), cancellationToken);
        else await backend.ExportJsonAsync(path, cancellationToken);
        return path;
    }

    private static async Task<string> CreateCsvAsync(IBackendClient backend, string stage,
        DateTimeOffset start, DateTimeOffset end, CancellationToken cancellationToken)
    {
        var path = Path.Combine(stage, "worklog.csv");
        if (backend is IBackendDocumentTransfer { CanDownloadDocuments: true } transfer)
            await DownloadAsync(stage, "worklog.csv",
                (stream, token) => transfer.DownloadCsvAsync(stream, start, end, token), cancellationToken);
        else await backend.ExportCsvAsync(path, start, end, cancellationToken);
        return path;
    }

    private static async Task<string> DownloadAsync(string stage, string name,
        Func<Stream, CancellationToken, Task<long>> download, CancellationToken cancellationToken)
    {
        var path = Path.Combine(stage, name);
        await using var destination = new FileStream(path, FileMode.CreateNew,
            FileAccess.Write, FileShare.None, 65_536, FileOptions.Asynchronous);
        var copied = await download(destination, cancellationToken);
        await destination.FlushAsync(cancellationToken);
        if (copied < 1 || copied != destination.Length)
            throw new InvalidDataException("The downloaded document is incomplete.");
        return path;
    }

    private static async Task<string> ExportAsync(string kind, string extension, string mimeType, long maximumBytes,
        Func<string, CancellationToken, Task<string>> create)
    {
        var provider = MainActivity.Current?.Content is Control view ? TopLevel.GetTopLevel(view)?.StorageProvider : null;
        if (provider?.CanSave != true) return $"{kind} export unavailable. Return to the app and retry.";
        using var selected = await provider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = $"Export {kind} · Snook · On this device",
            SuggestedFileName = $"snook-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.{extension}",
            FileTypeChoices = [new FilePickerFileType($"Snook {kind}") { Patterns = [$"*.{extension}"], MimeTypes = [mimeType] }]
        });
        if (selected is null) return $"{kind} cancelled. No workspace data was changed.";
        if (!selected.Path.IsAbsoluteUri || selected.Path.Scheme != "content" || selected.Path.OriginalString.Length > 8192)
            return $"{kind} rejected: select a document through the Android picker.";
        var root = global::Android.App.Application.Context.NoBackupFilesDir?.AbsolutePath
            ?? throw new IOException("Private staging unavailable.");
        // Android may expose its own private data directory through an alias in
        // the app's mount namespace. Resolve this OS-supplied path, never the
        // external content URI; backend ancestor/symlink checks remain unchanged.
        using (var privateDirectory = new Java.IO.File(root)) root = privateDirectory.CanonicalPath;
        var stage = Path.Combine(root, "document-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        var phase = "create private artifact";
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var artifact = await Task.Run(() => create(stage, timeout.Token), timeout.Token);
            phase = "hash private artifact";
            byte[] digest;
            long bytes;
            await using (var source = File.OpenRead(artifact))
            {
                bytes = source.Length;
                if (bytes > maximumBytes) throw new InvalidDataException("Export exceeds the document size limit.");
                digest = await SHA256.HashDataAsync(source, timeout.Token);
                source.Position = 0;
                phase = "open chosen document";
                await using var destination = await selected.OpenWriteAsync();
                phase = "write chosen document";
                await BoundedStreamTransfer.CopyAsync(source, destination, maximumBytes, timeout.Token);
                await destination.FlushAsync(timeout.Token);
            }
            // Verify the selected document, not just the private source. No URI is
            // persisted, interpreted as a filesystem path, or trusted as seekable.
            phase = "read-back verification";
            await using var reopened = await selected.OpenReadAsync();
            using var algorithm = SHA256.Create();
            await using var verifier = new CryptoStream(Stream.Null, algorithm, CryptoStreamMode.Write);
            var copied = await BoundedStreamTransfer.CopyAsync(reopened, verifier, maximumBytes, timeout.Token);
            await verifier.FlushFinalBlockAsync(timeout.Token);
            if (copied != bytes || algorithm.Hash is not { } actual || !CryptographicOperations.FixedTimeEquals(digest, actual))
                throw new InvalidDataException("Document verification failed.");
            return $"{kind} exported and read-back verified ({bytes:N0} bytes). Keep this unencrypted file private.";
        }
        catch (Exception exception)
        {
            var reason = exception is Snook.Domain.SnookException failure ? failure.Code.ToString() : exception.GetType().Name;
            global::Android.Util.Log.Warn("SnookExport", $"{kind}/{phase}: {reason}");
            return $"{kind} export failed. The selected document may be incomplete; remove it before retrying. Workspace data was not changed.";
        }
        finally { Directory.Delete(stage, recursive: true); }
    }

    private static async Task<string> ShareAsync(string kind, string mimeType, long maximumBytes,
        Func<string, CancellationToken, Task<string>> create)
    {
        var activity = MainActivity.Current;
        if (activity?.CanReviewBackup() != true)
            return $"{kind} sharing unavailable. Unlock the device and return to Snook.";
        var cache = activity.CacheDir?.CanonicalPath
            ?? throw new IOException("Private share staging unavailable.");
        var shareRoot = Path.Combine(cache, "snook-share");
        Directory.CreateDirectory(shareRoot);
        CleanupShares(shareRoot);
        var stage = Path.Combine(shareRoot, "share-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        var dispatched = false;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var artifact = await Task.Run(() => create(stage, timeout.Token), timeout.Token);
            var fullStage = Path.GetFullPath(stage) + Path.DirectorySeparatorChar;
            var fullArtifact = Path.GetFullPath(artifact);
            var bytes = new FileInfo(fullArtifact).Length;
            if (!fullArtifact.StartsWith(fullStage, StringComparison.Ordinal)
                || bytes < 1 || bytes > maximumBytes)
                throw new InvalidDataException("Invalid share artifact.");
            if (!ReferenceEquals(MainActivity.Current, activity) || !activity.CanReviewBackup())
                return $"{kind} sharing cancelled because Snook left the foreground.";
            using var file = new Java.IO.File(fullArtifact);
            using var uri = FileProvider.GetUriForFile(activity,
                activity.PackageName + ".files", file);
            using var send = new Intent(Intent.ActionSend);
            send.SetType(mimeType);
            send.PutExtra(Intent.ExtraStream, uri);
            send.ClipData = ClipData.NewRawUri("Snook export", uri);
            send.AddFlags(ActivityFlags.GrantReadUriPermission);
            using var chooser = Intent.CreateChooser(send, $"Share Snook {kind}")
                ?? throw new InvalidOperationException("Android share sheet is unavailable.");
            chooser.AddFlags(ActivityFlags.GrantReadUriPermission);
            activity.StartActivity(chooser);
            dispatched = true;
            return $"{kind} is ready in the Android share sheet ({bytes:N0} bytes). Access is read-only and temporary.";
        }
        catch (Exception exception)
        {
            global::Android.Util.Log.Warn("SnookShare", $"{kind}: {exception.GetType().Name}");
            return $"{kind} could not be shared. Workspace data was not changed.";
        }
        finally
        {
            if (!dispatched)
            {
                try { Directory.Delete(stage, recursive: true); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    global::Android.Util.Log.Warn("SnookShare", "Private cancellation cleanup failed.");
                }
            }
        }
    }

    private static void CleanupShares(string root)
    {
        var cutoff = DateTime.UtcNow.AddHours(-24);
        var entries = Directory.EnumerateDirectories(root, "share-*", SearchOption.TopDirectoryOnly)
            .Select(path => new DirectoryInfo(path))
            .OrderByDescending(directory => directory.CreationTimeUtc)
            .ToArray();
        foreach (var entry in entries.Where((directory, index) => index >= 16 || directory.CreationTimeUtc < cutoff))
        {
            try { entry.Delete(recursive: true); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                global::Android.Util.Log.Warn("SnookShare", "Private expiry cleanup failed.");
            }
        }
    }
}
