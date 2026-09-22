namespace Snook.Domain;

public sealed record RssFeed(Guid Id, string Url, string Title, DateTimeOffset? RefreshedAtUtc,
    DateTimeOffset? DeletedAtUtc, long Revision);
public sealed record RssArticle(Guid Id, Guid FeedId, string ExternalId, string Title, string? Url,
    string Content, DateTimeOffset? PublishedAtUtc, DateTimeOffset ReceivedAtUtc,
    bool IsRead, bool IsStarred, long Revision);
public sealed record RssItem(string ExternalId, string Title, string? Url, string Content, DateTimeOffset? PublishedAtUtc);
public sealed record RssDocument(string Title, IReadOnlyList<RssItem> Items);
public sealed record RssArticleQuery(Guid? FeedId = null, string? Search = null, bool UnreadOnly = false,
    bool StarredOnly = false, int Offset = 0, int Limit = 50);
public sealed record RssArticlePage(IReadOnlyList<RssArticle> Items, bool HasMore);

public static class RssRules
{
    public const int MaximumFeeds = 200;
    public const int MaximumItems = 500;
    public const int MaximumArticlesPerFeed = 10000;
    public static Uri FeedUri(string url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Length > 2048 || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Fragment) || !uri.IsDefaultPort)
            throw new SnookException(SnookErrorCode.ValidationFailed, "Enter an HTTP or HTTPS feed URL with a default port and without credentials or a fragment.");
        return uri;
    }
}
