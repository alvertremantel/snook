namespace Snook.Application;

internal sealed partial class WorkspaceBackendClient
{
    public bool CanDownloadDocuments => _backend is IBackendDocumentTransfer
        { CanDownloadDocuments: true };
    public bool CanRestoreDocuments => _backend is IBackendDocumentTransfer
        { CanRestoreDocuments: true };

    public Task<long> DownloadBackupAsync(Stream destination,
        CancellationToken cancellationToken = default)
        => TransferAsync(transfer => transfer.DownloadBackupAsync(destination, cancellationToken),
            cancellationToken);

    public Task<long> DownloadJsonAsync(Stream destination,
        CancellationToken cancellationToken = default)
        => TransferAsync(transfer => transfer.DownloadJsonAsync(destination, cancellationToken),
            cancellationToken);

    public Task<long> DownloadCsvAsync(Stream destination, DateTimeOffset rangeStartUtc,
        DateTimeOffset rangeEndUtc, CancellationToken cancellationToken = default)
        => TransferAsync(transfer => transfer.DownloadCsvAsync(destination, rangeStartUtc,
            rangeEndUtc, cancellationToken), cancellationToken);

    public Task<RemoteBackupStage> StageRestoreBackupAsync(Stream source,
        CancellationToken cancellationToken = default)
        => TransferAsync(transfer => transfer.StageRestoreBackupAsync(source, cancellationToken),
            cancellationToken);

    public Task<RemoteRestoreResult> RestoreStagedBackupAsync(RemoteBackupStage stage,
        CancellationToken cancellationToken = default)
        => TransferAsync(transfer => transfer.RestoreStagedBackupAsync(stage, cancellationToken),
            cancellationToken);

    public Task DiscardStagedBackupAsync(Guid uploadId,
        CancellationToken cancellationToken = default)
        => TransferAsync(async transfer =>
        {
            await transfer.DiscardStagedBackupAsync(uploadId, cancellationToken)
                .ConfigureAwait(false);
            return true;
        }, cancellationToken);

    private Task<long> TransferAsync(Func<IBackendDocumentTransfer, Task<long>> operation,
        CancellationToken cancellationToken)
    {
        if (_backend is not IBackendDocumentTransfer transfer)
            throw new InvalidOperationException("This backend does not provide document streaming.");
        return RunAsync(() => operation(transfer), cancellationToken);
    }

    private Task<T> TransferAsync<T>(Func<IBackendDocumentTransfer, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        if (_backend is not IBackendDocumentTransfer transfer)
            throw new InvalidOperationException("This backend does not provide document streaming.");
        return RunAsync(() => operation(transfer), cancellationToken);
    }
}
