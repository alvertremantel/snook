using Snook.Contracts;
using Snook.Domain;

namespace Snook.Application;

public sealed partial class SnookBackend
{
    public Task<IReadOnlyList<RssFeed>> GetRssFeedsAsync(bool includeDeleted = false, CancellationToken cancellationToken = default)
        => _store.GetRssFeedsAsync(includeDeleted, cancellationToken);
    public Task<RssFeed> SubscribeRssAsync(string url, OperationRequest request, CancellationToken cancellationToken = default)
    {
        ValidateRssRequest(request, creating: true);
        return _store.SubscribeRssAsync(url, request.OperationId, _clock.GetUtcNow(), cancellationToken);
    }
    public async Task<RssFeed> RefreshRssAsync(Guid feedId, OperationRequest request, CancellationToken cancellationToken = default)
    {
        ValidateRssRequest(request);
        var revision = RequireRevision(request);
        var receipt = await _store.GetRssRefreshReceiptAsync(feedId, revision, request.OperationId, cancellationToken);
        if (receipt is not null) return receipt;
        var feed = (await _store.GetRssFeedsAsync(true, cancellationToken)).SingleOrDefault(feed => feed.Id == feedId)
            ?? throw new SnookException(SnookErrorCode.NotFound, "This feed no longer exists.");
        if (feed.Revision != revision) throw new SnookException(SnookErrorCode.RevisionConflict, "Reload this feed before refreshing.");
        if (feed.DeletedAtUtc is not null) throw new SnookException(SnookErrorCode.InvalidTransition, "Restore this feed before refreshing.");
        var document = await _rssReader.FetchAsync(feed.Url, cancellationToken);
        return await _store.ApplyRssRefreshAsync(feedId, document, revision, request.OperationId, _clock.GetUtcNow(), cancellationToken);
    }
    public Task<RssFeed> SetRssFeedDeletedAsync(Guid feedId, bool deleted, OperationRequest request, CancellationToken cancellationToken = default)
    {
        ValidateRssRequest(request);
        return _store.SetRssFeedDeletedAsync(feedId, deleted, RequireRevision(request), request.OperationId, _clock.GetUtcNow(), cancellationToken);
    }
    public Task<RssArticlePage> GetRssArticlesAsync(RssArticleQuery query, CancellationToken cancellationToken = default)
        => _store.GetRssArticlesAsync(query, cancellationToken);
    public Task<RssArticle> SetRssArticleStateAsync(Guid articleId, bool isRead, bool isStarred, OperationRequest request, CancellationToken cancellationToken = default)
    {
        ValidateRssRequest(request);
        return _store.SetRssArticleStateAsync(articleId, isRead, isStarred, RequireRevision(request), request.OperationId, _clock.GetUtcNow(), cancellationToken);
    }
    private static void ValidateRssRequest(OperationRequest request, bool creating = false)
    {
        if (request is null || request.OperationId == Guid.Empty || request.ClientDeviceId == Guid.Empty
            || (creating ? request.ExpectedRevision is not null : request.ExpectedRevision is null or < 1))
            throw new SnookException(SnookErrorCode.ValidationFailed, "Provide operation and device IDs, and the current revision for existing RSS items.");
    }
}
