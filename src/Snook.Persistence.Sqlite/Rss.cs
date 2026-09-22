using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Snook.Domain;

namespace Snook.Persistence.Sqlite;

public sealed partial class SqliteStore
{
    private const string RssSql = """
        CREATE TABLE rss_feeds(id TEXT PRIMARY KEY, url TEXT NOT NULL UNIQUE, deleted INTEGER NOT NULL, payload TEXT NOT NULL);
        CREATE TABLE rss_articles(id TEXT PRIMARY KEY, feed_id TEXT NOT NULL REFERENCES rss_feeds(id), external_id TEXT NOT NULL,
            is_read INTEGER NOT NULL, is_starred INTEGER NOT NULL, sort_ms INTEGER NOT NULL, title TEXT NOT NULL, payload TEXT NOT NULL,
            UNIQUE(feed_id,external_id));
        CREATE INDEX rss_articles_order ON rss_articles(sort_ms DESC,id);
        CREATE INDEX rss_articles_feed ON rss_articles(feed_id,sort_ms DESC,id);
        """;
    private static readonly string RssChecksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(RssSql))).ToLowerInvariant();

    private static async Task EnsureRssMigrationAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.CommandText = "SELECT MAX(sequence) FROM schema_migrations;";
        if (Convert.ToInt32(await query.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) >= 12) return;
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await ExecuteMigrationCommandAsync(connection, transaction, RssSql, cancellationToken);
        await ExecuteMigrationCommandAsync(connection, transaction,
            "INSERT INTO schema_migrations(sequence,name,checksum,applied_at_utc_ms) VALUES(12,'rss-reader',$checksum,$applied);",
            cancellationToken, ("$checksum", RssChecksum), ("$applied", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<RssFeed>> GetRssFeedsAsync(bool includeDeleted, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        await using var lease = await OpenConnectionAsync(cancellationToken);
        return await ReadRssJsonAsync<RssFeed>(lease.Connection, null,
            "SELECT payload FROM rss_feeds WHERE $deleted=1 OR deleted=0 ORDER BY url LIMIT 200;", cancellationToken, ("$deleted", includeDeleted));
    }

    public Task<RssFeed> SubscribeRssAsync(string url, Guid operationId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        url = RssRules.FeedUri(url).AbsoluteUri;
        return WriteAsync(async (connection, transaction) =>
        {
            var all = await ReadRssJsonAsync<RssFeed>(connection, transaction, "SELECT payload FROM rss_feeds LIMIT 201;", cancellationToken);
            if (all.Any(feed => feed.Url == url))
                throw new SnookException(SnookErrorCode.ValidationFailed, "This feed is already subscribed. Restore it if it was deleted.");
            if (all.Count >= RssRules.MaximumFeeds) throw new SnookException(SnookErrorCode.ValidationFailed, "The workspace supports up to 200 subscriptions, including deleted feeds.");
            var feed = new RssFeed(Guid.NewGuid(), url, new Uri(url).Host, null, null, 1);
            await SaveRssFeedAsync(connection, transaction, feed, cancellationToken);
            await RecordMutationAsync(connection, transaction, operationId, "rss-feed", feed.Id, "subscribed", 0, 1, nowUtc, cancellationToken);
            return feed;
        }, cancellationToken, new StoreWriteRequest(operationId, "SubscribeRssAsync", new { url }));
    }

    public Task<RssFeed> SetRssFeedDeletedAsync(Guid feedId, bool deleted, long expectedRevision, Guid operationId,
        DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
        => WriteAsync(async (connection, transaction) =>
        {
            var feed = await ReadRssFeedAsync(connection, transaction, feedId, cancellationToken);
            CheckRssRevision(feed.Revision, expectedRevision);
            feed = feed with { DeletedAtUtc = deleted ? MillisecondUtc(nowUtc) : null, Revision = feed.Revision + 1 };
            await SaveRssFeedAsync(connection, transaction, feed, cancellationToken);
            await RecordMutationAsync(connection, transaction, operationId, "rss-feed", feed.Id, deleted ? "deleted" : "restored", expectedRevision, feed.Revision, nowUtc, cancellationToken);
            return feed;
        }, cancellationToken, new StoreWriteRequest(operationId, "SetRssFeedDeletedAsync", new { feedId, deleted, expectedRevision }));

    // Check the durable result before any network access; retries work offline, even after deletion.
    public async Task<RssFeed?> GetRssRefreshReceiptAsync(Guid feedId, long expectedRevision, Guid operationId, CancellationToken cancellationToken)
    {
        EnsureInitialized();
        await using var lease = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await lease.Connection.BeginTransactionAsync(cancellationToken);
        var saved = await ReadExactReceiptAsync<RssFeed>(lease.Connection, transaction,
            RssRefreshRequest(feedId, expectedRevision, operationId), cancellationToken);
        if (saved.Found) return saved.Value;
        if (await ReceiptExistsAsync(lease.Connection, transaction, operationId, cancellationToken))
            throw new SnookException(SnookErrorCode.ValidationFailed, "This operation ID already belongs to another request.");
        return null;
    }

    public Task<RssFeed> ApplyRssRefreshAsync(Guid feedId, RssDocument document, long expectedRevision, Guid operationId,
        DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
        => WriteAsync(async (connection, transaction) =>
        {
            var feed = await ReadRssFeedAsync(connection, transaction, feedId, cancellationToken);
            CheckRssRevision(feed.Revision, expectedRevision);
            if (feed.DeletedAtUtc is not null) throw new SnookException(SnookErrorCode.InvalidTransition, "Restore this feed before refreshing.");
            if (document.Items.Count > RssRules.MaximumItems) throw new SnookException(SnookErrorCode.ValidationFailed, "The feed contains too many items.");
            foreach (var item in document.Items)
            {
                var existing = (await ReadRssJsonAsync<RssArticle>(connection, transaction,
                    "SELECT payload FROM rss_articles WHERE feed_id=$feed AND external_id=$external;", cancellationToken,
                    ("$feed", Id(feedId)), ("$external", item.ExternalId))).SingleOrDefault();
                // Existing items remain immutable: preserve the cached text and the user's read/star revision.
                if (existing is not null) continue;
                var article = new RssArticle(Guid.NewGuid(), feedId, item.ExternalId, item.Title, item.Url, item.Content,
                    item.PublishedAtUtc, MillisecondUtc(nowUtc), false, false, 1);
                await SaveRssArticleAsync(connection, transaction, article, cancellationToken);
            }
            await using var count = connection.CreateCommand();
            count.Transaction = transaction;
            count.CommandText = "SELECT COUNT(*) FROM rss_articles WHERE feed_id=$feed;";
            count.Parameters.AddWithValue("$feed", Id(feedId));
            if (Convert.ToInt32(await count.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) > RssRules.MaximumArticlesPerFeed)
                throw new SnookException(SnookErrorCode.ValidationFailed, "This feed has reached its 10,000 cached article limit. No articles were changed.");
            feed = feed with { Title = document.Title, RefreshedAtUtc = MillisecondUtc(nowUtc), Revision = feed.Revision + 1 };
            await SaveRssFeedAsync(connection, transaction, feed, cancellationToken);
            await RecordMutationAsync(connection, transaction, operationId, "rss-feed", feed.Id, "refreshed", expectedRevision, feed.Revision, nowUtc, cancellationToken);
            return feed;
        }, cancellationToken, RssRefreshRequest(feedId, expectedRevision, operationId));

    public async Task<RssArticlePage> GetRssArticlesAsync(RssArticleQuery query, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        if (query is null || query.Limit is < 1 or > 100 || query.Offset is < 0 or > 2000000 || query.Search?.Length > 200 || query.FeedId == Guid.Empty)
            throw new SnookException(SnookErrorCode.ValidationFailed, "Choose 1–100 articles, a valid offset/feed, and search text up to 200 characters.");
        await using var lease = await OpenConnectionAsync(cancellationToken);
        var rows = await ReadRssJsonAsync<RssArticle>(lease.Connection, null, """
            SELECT a.payload FROM rss_articles a JOIN rss_feeds f ON f.id=a.feed_id
            WHERE f.deleted=0 AND ($feed IS NULL OR a.feed_id=$feed) AND ($unread=0 OR a.is_read=0)
              AND ($starred=0 OR a.is_starred=1) AND ($search='' OR instr(lower(a.title),lower($search))>0)
            ORDER BY a.sort_ms DESC,a.id LIMIT $limit OFFSET $offset;
            """, cancellationToken, ("$feed", query.FeedId is { } id ? Id(id) : DBNull.Value),
            ("$unread", query.UnreadOnly), ("$starred", query.StarredOnly), ("$search", query.Search?.Trim() ?? ""),
            ("$limit", query.Limit + 1), ("$offset", query.Offset));
        return new RssArticlePage(rows.Take(query.Limit).ToArray(), rows.Count > query.Limit);
    }

    public Task<RssArticle> SetRssArticleStateAsync(Guid articleId, bool isRead, bool isStarred, long expectedRevision,
        Guid operationId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
        => WriteAsync(async (connection, transaction) =>
        {
            var article = (await ReadRssJsonAsync<RssArticle>(connection, transaction,
                "SELECT payload FROM rss_articles WHERE id=$id;", cancellationToken, ("$id", Id(articleId)))).SingleOrDefault()
                ?? throw new SnookException(SnookErrorCode.NotFound, "This article no longer exists.");
            var feed = await ReadRssFeedAsync(connection, transaction, article.FeedId, cancellationToken);
            if (feed.DeletedAtUtc is not null) throw new SnookException(SnookErrorCode.InvalidTransition, "Restore the feed before changing its articles.");
            CheckRssRevision(article.Revision, expectedRevision);
            article = article with { IsRead = isRead, IsStarred = isStarred, Revision = article.Revision + 1 };
            await SaveRssArticleAsync(connection, transaction, article, cancellationToken);
            await RecordMutationAsync(connection, transaction, operationId, "rss-article", article.Id, "state-changed", expectedRevision, article.Revision, nowUtc, cancellationToken);
            return article;
        }, cancellationToken, new StoreWriteRequest(operationId, "SetRssArticleStateAsync", new { articleId, isRead, isStarred, expectedRevision }));

    private static StoreWriteRequest RssRefreshRequest(Guid feedId, long expectedRevision, Guid operationId)
        => new(operationId, "RefreshRssAsync", new { feedId, expectedRevision });
    private static void CheckRssRevision(long actual, long expected)
    {
        if (actual != expected) throw new SnookException(SnookErrorCode.RevisionConflict, "This RSS item changed. Reload and try the action again.");
    }
    private static async Task<RssFeed> ReadRssFeedAsync(SqliteConnection connection, SqliteTransaction transaction, Guid id, CancellationToken token)
        => (await ReadRssJsonAsync<RssFeed>(connection, transaction, "SELECT payload FROM rss_feeds WHERE id=$id;", token, ("$id", Id(id)))).SingleOrDefault()
            ?? throw new SnookException(SnookErrorCode.NotFound, "This feed no longer exists.");
    private static Task SaveRssFeedAsync(SqliteConnection connection, SqliteTransaction transaction, RssFeed feed, CancellationToken token)
        => ExecuteMigrationCommandAsync(connection, transaction,
            "INSERT INTO rss_feeds VALUES($id,$url,$deleted,$payload) ON CONFLICT(id) DO UPDATE SET deleted=$deleted,payload=$payload;", token,
            ("$id", Id(feed.Id)), ("$url", feed.Url), ("$deleted", feed.DeletedAtUtc is not null), ("$payload", JsonSerializer.Serialize(feed)));
    private static Task SaveRssArticleAsync(SqliteConnection connection, SqliteTransaction transaction, RssArticle article, CancellationToken token)
        => ExecuteMigrationCommandAsync(connection, transaction, """
            INSERT INTO rss_articles VALUES($id,$feed,$external,$read,$star,$sort,$title,$payload)
            ON CONFLICT(id) DO UPDATE SET is_read=$read,is_starred=$star,payload=$payload;
            """, token, ("$id", Id(article.Id)), ("$feed", Id(article.FeedId)), ("$external", article.ExternalId),
            ("$read", article.IsRead), ("$star", article.IsStarred), ("$sort", (article.PublishedAtUtc ?? article.ReceivedAtUtc).ToUnixTimeMilliseconds()),
            ("$title", article.Title), ("$payload", JsonSerializer.Serialize(article)));
    private static async Task<List<T>> ReadRssJsonAsync<T>(SqliteConnection connection, SqliteTransaction? transaction, string sql,
        CancellationToken token, params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await using var reader = await command.ExecuteReaderAsync(token);
        var result = new List<T>();
        while (await reader.ReadAsync(token)) result.Add(JsonSerializer.Deserialize<T>(reader.GetString(0))
            ?? throw new SnookException(SnookErrorCode.SchemaIncompatible, "Invalid cached RSS data."));
        return result;
    }
    private async Task<object> ExportRssAsync(CancellationToken token)
    {
        await using var lease = await OpenConnectionAsync(token);
        var feeds = await ReadRssJsonAsync<RssFeed>(lease.Connection, null, "SELECT payload FROM rss_feeds ORDER BY id;", token);
        var articles = await ReadRssJsonAsync<RssArticle>(lease.Connection, null, "SELECT payload FROM rss_articles ORDER BY id;", token);
        return new { feeds, articles };
    }
}
