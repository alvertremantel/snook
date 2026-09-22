using Snook.Contracts;
using Snook.Domain;

namespace Snook.Application;

public sealed partial class DaemonBackendClient
{
    public async Task<IReadOnlyList<RssFeed>> GetRssFeedsAsync(bool includeDeleted = false, CancellationToken cancellationToken = default)
        => await CallAsync<RssFeed[]>(nameof(GetRssFeedsAsync), cancellationToken, includeDeleted);
    public Task<RssFeed> SubscribeRssAsync(string url, OperationRequest request, CancellationToken cancellationToken = default)
        => CallAsync<RssFeed>(nameof(SubscribeRssAsync), cancellationToken, url, request);
    public Task<RssFeed> RefreshRssAsync(Guid feedId, OperationRequest request, CancellationToken cancellationToken = default)
        => CallAsync<RssFeed>(nameof(RefreshRssAsync), cancellationToken, feedId, request);
    public Task<RssFeed> SetRssFeedDeletedAsync(Guid feedId, bool deleted, OperationRequest request, CancellationToken cancellationToken = default)
        => CallAsync<RssFeed>(nameof(SetRssFeedDeletedAsync), cancellationToken, feedId, deleted, request);
    public Task<RssArticlePage> GetRssArticlesAsync(RssArticleQuery query, CancellationToken cancellationToken = default)
        => CallAsync<RssArticlePage>(nameof(GetRssArticlesAsync), cancellationToken, query);
    public Task<RssArticle> SetRssArticleStateAsync(Guid articleId, bool isRead, bool isStarred, OperationRequest request, CancellationToken cancellationToken = default)
        => CallAsync<RssArticle>(nameof(SetRssArticleStateAsync), cancellationToken, articleId, isRead, isStarred, request);
}
