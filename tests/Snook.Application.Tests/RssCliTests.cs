using System.Globalization;
using System.Text.Json;
using Snook.Contracts;
using Snook.Domain;
using Xunit;

namespace Snook.Application.Tests;

public sealed class RssCliTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    [Fact]
    public async Task EmbeddedCliReadsAndWritesSubscriptions()
    {
        var directory = Directory.CreateTempSubdirectory("snook-rss-cli-");
        try { await AssertRssCliAsync(["--data-dir", directory.FullName, "--host", "embedded"]); }
        finally { directory.Delete(true); }
    }
    internal static async Task AssertRssCliAsync(string[] prefix)
    {
        async Task<string> RunAsync(params string[] args)
        {
            using var output = new StringWriter(CultureInfo.InvariantCulture);
            using var error = new StringWriter(CultureInfo.InvariantCulture);
            var code = await Snook.Cli.Program.RunAsync([.. prefix, .. args], output, error);
            Assert.True(code == 0, error.ToString());
            return output.ToString();
        }
        async Task<T> CallAsync<T>(string method, object arguments) => JsonSerializer.Deserialize<T>(
            await RunAsync("call", method, JsonSerializer.Serialize(arguments, JsonOptions)), JsonOptions)!;
        static OperationRequest Request(long? revision = null) => new(Guid.NewGuid(), Guid.NewGuid(), revision);
        var create = new { url = "https://example.com/cli-feed", request = Request() };
        var feed = await CallAsync<RssFeed>("subscribe-rss", create);
        Assert.Equal(feed, await CallAsync<RssFeed>("subscribe-rss", create));
        Assert.Contains(feed.Id.ToString(), await RunAsync("rss-feeds"), StringComparison.Ordinal);
        Assert.Empty(JsonSerializer.Deserialize<RssArticlePage>(await RunAsync("rss-articles"), JsonOptions)!.Items);
        feed = await CallAsync<RssFeed>("set-rss-feed-deleted", new { feedId = feed.Id, deleted = true, request = Request(feed.Revision) });
        Assert.DoesNotContain(feed.Id.ToString(), await RunAsync("rss-feeds"), StringComparison.Ordinal);
        Assert.Contains(feed.Id.ToString(), await RunAsync("rss-feeds", "--include-deleted"), StringComparison.Ordinal);
        feed = await CallAsync<RssFeed>("set-rss-feed-deleted", new { feedId = feed.Id, deleted = false, request = Request(feed.Revision) });
        Assert.Null(feed.DeletedAtUtc);
        Assert.Contains("refresh-rss", await RunAsync("api"), StringComparison.Ordinal);
    }
}
