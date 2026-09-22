using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Snook.Domain;

namespace Snook.Application;

public sealed partial class RssFeedReader
{
    public const int MaximumBytes = 2 * 1024 * 1024;
    private static readonly HttpClient Client = CreateClient();
    private readonly HttpClient _client;
    public RssFeedReader(HttpClient? client = null) => _client = client ?? Client;

    public async Task<RssDocument> FetchAsync(string url, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        var uri = RssRules.FeedUri(url);
        try
        {
            for (var redirect = 0; redirect <= 5; redirect++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.UserAgent.ParseAdd("Snook/1.0");
                request.Headers.Accept.ParseAdd("application/rss+xml, application/atom+xml, application/xml, text/xml");
                using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
                {
                    if (response.Headers.Location is null) throw InvalidFeed();
                    uri = RssRules.FeedUri(new Uri(uri, response.Headers.Location).AbsoluteUri);
                    continue;
                }
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength > MaximumBytes) throw InvalidFeed();
                await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                using var buffer = new MemoryStream();
                var chunk = new byte[16384];
                int read;
                while ((read = await stream.ReadAsync(chunk, timeout.Token)) != 0)
                {
                    if (buffer.Length + read > MaximumBytes) throw InvalidFeed();
                    buffer.Write(chunk, 0, read);
                }
                return Parse(buffer.ToArray(), uri);
            }
            throw InvalidFeed();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new SnookException(SnookErrorCode.StoreUnavailable, "Feed refresh timed out. Cached articles are still available; retry refresh."); }
        catch (Exception exception) when (exception is HttpRequestException or IOException or SocketException)
        { throw new SnookException(SnookErrorCode.StoreUnavailable, "Could not download this public feed. Check its URL and connection, then retry. Cached articles are unchanged."); }
    }

    public static RssDocument Parse(byte[] data, Uri source)
    {
        if (data.Length > MaximumBytes) throw InvalidFeed();
        try
        {
            using var stream = new MemoryStream(data);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaximumBytes });
            var document = XDocument.Load(reader);
            var root = document.Root ?? throw InvalidFeed();
            XNamespace atom = "http://www.w3.org/2005/Atom";
            var isAtom = root.Name == atom + "feed";
            var channel = isAtom ? root : root.Name == "rss" ? root.Element("channel") : null;
            if (channel is null) throw InvalidFeed();
            var entries = channel.Elements(isAtom ? atom + "entry" : "item").Take(RssRules.MaximumItems + 1).ToArray();
            if (entries.Length > RssRules.MaximumItems) throw InvalidFeed();
            var items = new List<RssItem>();
            foreach (var entry in entries)
            {
                var title = PlainText(Value(entry, isAtom ? atom + "title" : "title"), 500);
                var contentElement = isAtom ? entry.Element(atom + "content") ?? entry.Element(atom + "summary")
                    : entry.Element(XName.Get("encoded", "http://purl.org/rss/1.0/modules/content/")) ?? entry.Element("description");
                var content = PlainText(contentElement?.HasElements == true
                    ? string.Concat(contentElement.Nodes().Select(node => node.ToString())) : contentElement?.Value ?? "", 32000);
                var href = isAtom ? entry.Elements(atom + "link").FirstOrDefault(link => (string?)link.Attribute("rel") is null or "alternate")?.Attribute("href")?.Value
                    : Value(entry, "link");
                var link = ArticleUrl(entry, href, source);
                var identity = Value(entry, isAtom ? atom + "id" : "guid");
                if (string.IsNullOrWhiteSpace(identity)) identity = link ?? title + "\n" + content;
                if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(content)) continue;
                identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
                var dateText = Value(entry, isAtom ? atom + "published" : "pubDate");
                if (isAtom && string.IsNullOrEmpty(dateText)) dateText = Value(entry, atom + "updated");
                dateText = dateText.Replace(" GMT", " +00:00", StringComparison.Ordinal);
                DateTimeOffset? published = DateTimeOffset.TryParse(dateText, CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal, out var date) ? date.ToUniversalTime() : null;
                items.Add(new RssItem(identity, string.IsNullOrWhiteSpace(title) ? "Untitled article" : title, link, content, published));
            }
            var feedTitle = PlainText(Value(channel, isAtom ? atom + "title" : "title"), 200);
            return new RssDocument(string.IsNullOrWhiteSpace(feedTitle) ? source.Host : feedTitle,
                items.DistinctBy(item => item.ExternalId).ToArray());
        }
        catch (XmlException) { throw InvalidFeed(); }
        catch (RegexMatchTimeoutException) { throw InvalidFeed(); }
    }

    private static string Value(XElement element, XName name) => element.Element(name)?.Value ?? "";
    private static string? ArticleUrl(XElement entry, string? href, Uri source)
    {
        if (string.IsNullOrWhiteSpace(href) || href.Length > 2048) return null;
        foreach (var ancestor in entry.AncestorsAndSelf().Reverse())
            if (ancestor.Attribute(XNamespace.Xml + "base") is { } attribute && Uri.TryCreate(source, attribute.Value, out var next)) source = next;
        return Uri.TryCreate(source, href, out var uri) && uri.Scheme is "http" or "https" && string.IsNullOrEmpty(uri.UserInfo)
            ? uri.AbsoluteUri : null;
    }
    private static string PlainText(string text, int limit)
    {
        text = ScriptPattern().Replace(text, " ");
        text = TagPattern().Replace(text, " ");
        text = WebUtility.HtmlDecode(text).Trim();
        return text.Length > limit ? text[..limit] : text;
    }
    [GeneratedRegex(@"<(script|style)\b[^>]*>.*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline, 100)]
    private static partial Regex ScriptPattern();
    [GeneratedRegex(@"<[^>]*>", RegexOptions.Singleline, 100)]
    private static partial Regex TagPattern();
    private static SnookException InvalidFeed() => new(SnookErrorCode.ValidationFailed,
        "Expected an RSS 2.0 or Atom feed up to 2 MiB and 500 items, without XML entities. Cached articles are unchanged.");

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseCookies = false, UseProxy = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            ConnectCallback = async (context, token) =>
            {
                var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, token);
                if (addresses.Length == 0 || addresses.Any(address => !IsPublicAddress(address)))
                    throw new HttpRequestException("RSS destinations must use public addresses.");
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    // Connect to the validated address, rather than resolving the hostname again.
                    await socket.ConnectAsync(new IPEndPoint(addresses[0], context.DnsEndPoint.Port), token);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch { socket.Dispose(); throw; }
            }
        };
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return bytes[0] is not (0 or 10 or 127) && bytes[0] < 224
                && !(bytes[0] == 169 && bytes[1] == 254) && !(bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
                && !(bytes[0] == 192 && bytes[1] == 168) && !(bytes[0] == 100 && bytes[1] is >= 64 and <= 127)
                && !(bytes[0] == 198 && bytes[1] is 18 or 19);
        // Only global unicast IPv6; exclude transition mechanisms that can embed private IPv4.
        return bytes[0] is >= 0x20 and <= 0x3f && !(bytes[0] == 0x20 && bytes[1] == 0x02)
            && !(bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0 && bytes[3] == 0);
    }
}
