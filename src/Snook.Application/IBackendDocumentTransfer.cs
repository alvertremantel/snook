using Snook.Contracts;

namespace Snook.Application;

/// <summary>
/// Transport-level document capability for clients whose filesystem is
/// not the backend host filesystem. It complements the path-based workspace
/// contract used by embedded and local-daemon composition.
/// </summary>
public interface IBackendDocumentTransfer
{
    bool CanDownloadDocuments { get; }
    bool CanRestoreDocuments { get; }
    Task<long> DownloadBackupAsync(Stream destination, CancellationToken cancellationToken = default);
    Task<long> DownloadJsonAsync(Stream destination, CancellationToken cancellationToken = default);
    Task<long> DownloadCsvAsync(Stream destination, DateTimeOffset rangeStartUtc,
        DateTimeOffset rangeEndUtc, CancellationToken cancellationToken = default);
    Task<RemoteBackupStage> StageRestoreBackupAsync(Stream source,
        CancellationToken cancellationToken = default);
    Task<RemoteRestoreResult> RestoreStagedBackupAsync(RemoteBackupStage stage,
        CancellationToken cancellationToken = default);
    Task DiscardStagedBackupAsync(Guid uploadId,
        CancellationToken cancellationToken = default);
}

public sealed record RemoteBackupStage(Guid UploadId, Guid CurrentWorkspaceId,
    long CurrentCursor, BackupInspection Incoming, DateTimeOffset ExpiresAtUtc);

public sealed record RemoteRestoreResult(RestoreResult Restore, Guid WorkspaceId,
    int RevokedOtherDeviceCount);
