using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Snook.Application;
using Snook.Contracts;
using Snook.Domain;
using Snook.Persistence.Sqlite;
using Snook.UI;
using Xunit;

namespace Snook.Application.Tests;

public sealed class RssTests
{
    internal const string Fixture = """
        <rss version="2.0"><channel><title>Reading room</title>
        <item><guid>a</guid><title>First &amp; best</title><link>https://example.com/a</link><description><![CDATA[<p>Cached words</p><script>bad()</script>]]></description><pubDate>Tue, 22 Sep 2026 12:00:00 GMT</pubDate></item>
        <item><guid>b</guid><title>Second story</title><description>Another story</description></item>
        </channel></rss>
        """;
    private static OperationRequest Request(long? revision = null) => new(Guid.NewGuid(), Guid.NewGuid(), revision);

    [Fact]
    public void ParsesRssAndAtomWithoutExecutingOrFetchingMarkup()
    {
        var rss = RssFeedReader.Parse(Encoding.UTF8.GetBytes(Fixture), new Uri("https://example.com/feed"));
        Assert.Equal(2, rss.Items.Count);
        Assert.Equal("First & best", rss.Items[0].Title);
        Assert.Equal("Cached words", rss.Items[0].Content);
        Assert.Equal(DateTimeOffset.Parse("2026-09-22T12:00:00Z", CultureInfo.InvariantCulture), rss.Items[0].PublishedAtUtc);
        var atom = RssFeedReader.Parse(Encoding.UTF8.GetBytes("""
            <feed xmlns="http://www.w3.org/2005/Atom" xml:base="https://example.com/blog/"><title>Atom</title>
            <entry><id>stable</id><title>Entry</title><link rel="self" href="feed"/><link href="article"/>
            <content type="xhtml"><div xmlns="http://www.w3.org/1999/xhtml"><p>Hello</p></div></content><updated>2026-09-22T07:00:00-05:00</updated></entry>
            <entry><id>stable</id><title>Duplicate</title></entry>
            <entry><title>No ID</title><link href="javascript:alert(1)"/><summary>Text</summary></entry></feed>
            """), new Uri("https://example.com/feed"));
        Assert.Equal(2, atom.Items.Count);
        Assert.Equal("https://example.com/blog/article", atom.Items[0].Url);
        Assert.Equal("Hello", atom.Items[0].Content);
        Assert.Equal(TimeSpan.Zero, atom.Items[0].PublishedAtUtc!.Value.Offset);
        Assert.Null(atom.Items[1].Url);
        Assert.Throws<SnookException>(() => RssFeedReader.Parse(Encoding.UTF8.GetBytes("<!DOCTYPE rss [<!ENTITY secret SYSTEM 'file:///etc/passwd'>]><rss><channel><title>&secret;</title></channel></rss>"), new Uri("https://example.com")));
        Assert.Throws<SnookException>(() => RssFeedReader.Parse(Encoding.UTF8.GetBytes("<html/>"), new Uri("https://example.com")));
        Assert.Throws<SnookException>(() => RssFeedReader.Parse(new byte[RssFeedReader.MaximumBytes + 1], new Uri("https://example.com")));
        Assert.Throws<SnookException>(() => RssFeedReader.Parse(Encoding.UTF8.GetBytes("<rss><channel>" + string.Concat(Enumerable.Repeat("<item><title>x</title></item>", 501)) + "</channel></rss>"), new Uri("https://example.com")));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("169.254.169.254")]
    [InlineData("172.16.0.2")]
    [InlineData("192.168.1.1")]
    [InlineData("100.100.100.100")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("fe80::1")]
    [InlineData("fd00::1")]
    [InlineData("2002:7f00:1::")]
    public void RejectsNonPublicAddresses(string address) => Assert.False(RssFeedReader.IsPublicAddress(IPAddress.Parse(address)));

    [Fact]
    public async Task RefreshIsBoundedAndFailedDownloadsLeaveCacheUnchanged()
    {
        using var handler = new FeedHandler();
        using var client = new HttpClient(handler);
        var reader = new RssFeedReader(client);
        handler.Redirect = true;
        await Assert.ThrowsAsync<SnookException>(() => reader.FetchAsync("https://example.com/feed"));
        Assert.Equal(6, handler.Calls);
        handler.Redirect = false;
        handler.Text = new string('x', RssFeedReader.MaximumBytes + 1);
        await Assert.ThrowsAsync<SnookException>(() => reader.FetchAsync("https://example.com/feed"));
        await Assert.ThrowsAsync<SnookException>(() => reader.FetchAsync("file:///etc/passwd"));
        await Assert.ThrowsAsync<SnookException>(() => reader.FetchAsync("https://user:secret@example.com/feed"));
    }

    internal static async Task AssertSubscriptionContractAsync(IBackendClient backend)
    {
        var url = "https://example.com/" + Guid.NewGuid() + "/feed";
        var request = Request();
        var feed = await backend.SubscribeRssAsync(url, request);
        Assert.Equal(feed, await backend.SubscribeRssAsync(url, request));
        await Assert.ThrowsAsync<SnookException>(() => backend.SubscribeRssAsync(url + "other", request));
        await Assert.ThrowsAsync<SnookException>(() => backend.SubscribeRssAsync(url, Request()));
        await Assert.ThrowsAsync<SnookException>(() => backend.SetRssFeedDeletedAsync(feed.Id, true, Request(99)));
        var deleted = await backend.SetRssFeedDeletedAsync(feed.Id, true, Request(feed.Revision));
        Assert.DoesNotContain(await backend.GetRssFeedsAsync(), row => row.Id == feed.Id);
        Assert.Contains(await backend.GetRssFeedsAsync(true), row => row.Id == feed.Id);
        feed = await backend.SetRssFeedDeletedAsync(feed.Id, false, Request(deleted.Revision));
        Assert.Null(feed.DeletedAtUtc);
        Assert.Empty((await backend.GetRssArticlesAsync(new RssArticleQuery(feed.Id))).Items);
        await Assert.ThrowsAsync<SnookException>(() => backend.GetRssArticlesAsync(new RssArticleQuery(Limit: 101)));
        await Assert.ThrowsAsync<SnookException>(() => backend.RefreshRssAsync(feed.Id, Request(99)));
        await Assert.ThrowsAsync<SnookException>(() => backend.SetRssArticleStateAsync(Guid.NewGuid(), true, true, Request(1)));
    }

    [Fact]
    public async Task CachedReaderSupportsReplaySearchPaginationRestoreAndRestart()
    {
        var directory = Directory.CreateTempSubdirectory("snook-rss-");
        using var handler = new FeedHandler();
        using var client = new HttpClient(handler);
        var path = Path.Combine(directory.FullName, "workspace.db");
        var subscribe = Request();
        var refresh = Request(1);
        RssFeed saved;
        RssArticle article;
        var stateRequest = Request(1);
        try
        {
            await using (var backend = new SnookBackend(new SqliteStore(path), rssReader: new RssFeedReader(client)))
            {
                await backend.InitializeAsync();
                await AssertSubscriptionContractAsync(backend);
                var feed = await backend.SubscribeRssAsync("https://example.com/feed", subscribe);
                var notifications = new List<ChangeNotification>();
                backend.Changed += (_, change) => notifications.Add(change);
                saved = await backend.RefreshRssAsync(feed.Id, refresh);
                var page = await backend.GetRssArticlesAsync(new RssArticleQuery(feed.Id, Limit: 1));
                Assert.Single(page.Items);
                Assert.True(page.HasMore);
                Assert.Single((await backend.GetRssArticlesAsync(new RssArticleQuery(feed.Id, Offset: 1, Limit: 1))).Items);
                article = Assert.Single((await backend.GetRssArticlesAsync(new RssArticleQuery(feed.Id, Search: "First"))).Items);
                article = await backend.SetRssArticleStateAsync(article.Id, true, true, stateRequest);
                Assert.Single((await backend.GetRssArticlesAsync(new RssArticleQuery(feed.Id, UnreadOnly: true))).Items);
                Assert.Equal(article, Assert.Single((await backend.GetRssArticlesAsync(new RssArticleQuery(feed.Id, StarredOnly: true))).Items));
                var latest = await backend.RefreshRssAsync(feed.Id, Request(saved.Revision));
                Assert.Equal(2, (await backend.GetRssArticlesAsync(new RssArticleQuery(feed.Id))).Items.Count);
                Assert.Equal(article, Assert.Single((await backend.GetRssArticlesAsync(new RssArticleQuery(feed.Id, StarredOnly: true))).Items));
                var cursor = (await backend.GetBootstrapAsync()).CommittedCursor;
                handler.Text = "<invalid/>";
                await Assert.ThrowsAsync<SnookException>(() => backend.RefreshRssAsync(feed.Id, Request(latest.Revision)));
                Assert.Equal(cursor, (await backend.GetBootstrapAsync()).CommittedCursor);
                var calls = handler.Calls;
                Assert.Equal(saved, await backend.RefreshRssAsync(feed.Id, refresh));
                Assert.Equal(calls, handler.Calls);
                Assert.Equal(3, notifications.Count);
                var backup = await backend.CreateBackupAsync(Path.Combine(directory.FullName, "rss.db"));
                var export = await backend.ExportJsonAsync(Path.Combine(directory.FullName, "rss.json"));
                using var json = JsonDocument.Parse(await File.ReadAllTextAsync(export.Path));
                Assert.Equal(2, json.RootElement.GetProperty("rss").GetProperty("articles").GetArrayLength());
                await backend.SetRssFeedDeletedAsync(feed.Id, true, Request(latest.Revision));
                Assert.Empty((await backend.GetRssArticlesAsync(new RssArticleQuery(feed.Id))).Items);
                await backend.RestoreBackupAsync(backup.Path);
            }
            await using (var backend = new SnookBackend(new SqliteStore(path), rssReader: new RssFeedReader(client)))
            {
                await backend.InitializeAsync();
                Assert.Equal(saved, await backend.RefreshRssAsync(saved.Id, refresh));
                Assert.Equal(article, await backend.SetRssArticleStateAsync(article.Id, true, true, stateRequest));
                Assert.Equal(article, Assert.Single((await backend.GetRssArticlesAsync(new RssArticleQuery(saved.Id, StarredOnly: true))).Items));
            }
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public async Task SharedUiSubscribesRefreshesReadsAndRetainsUrlDraft()
    {
        var directory = Directory.CreateTempSubdirectory("snook-rss-ui-");
        try
        {
            using var handler = new FeedHandler();
            using var client = new HttpClient(handler);
            await using var backend = new SnookBackend(new SqliteStore(Path.Combine(directory.FullName, "workspace.db")), rssReader: new RssFeedReader(client));
            await backend.InitializeAsync();
            await using var vm = new MainWindowViewModel(backend);
            await vm.SelectSectionForScreenshotAsync("RSS", null);
            vm.RssUrl = "https://example.com/feed";
            await ((AsyncCommand)vm.SubscribeRssCommand).ExecuteAsync(null);
            Assert.Single(vm.RssFeeds);
            Assert.Empty(vm.RssUrl);
            await ((AsyncCommand)vm.RefreshRssCommand).ExecuteAsync(null);
            Assert.Equal(2, vm.RssArticles.Count);
            vm.RssUrl = "https://unsaved.example.com/feed";
            await ((AsyncCommand)vm.RssArticles[0].ToggleStarCommand).ExecuteAsync(null);
            Assert.Equal("https://unsaved.example.com/feed", vm.RssUrl);
            vm.RssStarredOnly = true;
            await ((AsyncCommand)vm.ApplyRssFilterCommand).ExecuteAsync(null);
            Assert.Single(vm.RssArticles);
            await ((AsyncCommand)vm.RssArticles[0].ToggleReadCommand).ExecuteAsync(null);
            Assert.True(vm.RssArticles[0].Article.IsRead);
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public async Task UpgradeFromElevenAndChecksumValidation()
    {
        var directory = Directory.CreateTempSubdirectory("snook-rss-migration-");
        var path = Path.Combine(directory.FullName, "workspace.db");
        try
        {
            await using (var backend = new SnookBackend(new SqliteStore(path))) { await backend.InitializeAsync(); }
            await SqlAsync("DROP TABLE rss_articles; DROP TABLE rss_feeds; DELETE FROM schema_migrations WHERE sequence=12;");
            await using (var backend = new SnookBackend(new SqliteStore(path)))
            {
                await backend.InitializeAsync();
                Assert.Empty(await backend.GetRssFeedsAsync());
            }
            await SqlAsync("UPDATE schema_migrations SET checksum='tampered' WHERE sequence=12;");
            await using var invalid = new SnookBackend(new SqliteStore(path));
            Assert.Equal(SnookErrorCode.SchemaIncompatible, (await Assert.ThrowsAsync<SnookException>(() => invalid.InitializeAsync())).Code);
        }
        finally { directory.Delete(true); }
        async Task SqlAsync(string sql)
        {
            await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }
    }

    private sealed class FeedHandler : HttpMessageHandler
    {
        public string Text { get; set; } = Fixture;
        public int Calls { get; private set; }
        public bool Redirect { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            var response = new HttpResponseMessage(Redirect ? HttpStatusCode.Redirect : HttpStatusCode.OK) { Content = new StringContent(Text) };
            if (Redirect) response.Headers.Location = new Uri("https://example.com/redirect");
            return Task.FromResult(response);
        }
    }
}
