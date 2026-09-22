using Snook.Contracts;
using Snook.Domain;

namespace Snook.Application;

internal sealed partial class WorkspaceBackendClient
{
    public Task<IReadOnlyList<RssFeed>> GetRssFeedsAsync(bool includeDeleted = false, CancellationToken cancellationToken = default)
        => RunAsync(() => _backend.GetRssFeedsAsync(includeDeleted, cancellationToken), cancellationToken);
    public Task<RssFeed> SubscribeRssAsync(string url, OperationRequest request, CancellationToken cancellationToken = default)
        => RunAsync(() => _backend.SubscribeRssAsync(url, request, cancellationToken), cancellationToken);
    public Task<RssFeed> RefreshRssAsync(Guid feedId, OperationRequest request, CancellationToken cancellationToken = default)
        => RunAsync(() => _backend.RefreshRssAsync(feedId, request, cancellationToken), cancellationToken);
    public Task<RssFeed> SetRssFeedDeletedAsync(Guid feedId, bool deleted, OperationRequest request, CancellationToken cancellationToken = default)
        => RunAsync(() => _backend.SetRssFeedDeletedAsync(feedId, deleted, request, cancellationToken), cancellationToken);
    public Task<RssArticlePage> GetRssArticlesAsync(RssArticleQuery query, CancellationToken cancellationToken = default)
        => RunAsync(() => _backend.GetRssArticlesAsync(query, cancellationToken), cancellationToken);
    public Task<RssArticle> SetRssArticleStateAsync(Guid articleId, bool isRead, bool isStarred, OperationRequest request, CancellationToken cancellationToken = default)
        => RunAsync(() => _backend.SetRssArticleStateAsync(articleId, isRead, isStarred, request, cancellationToken), cancellationToken);
}
