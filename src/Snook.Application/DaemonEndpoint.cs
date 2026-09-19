using System.Net;
using Snook.Domain;

namespace Snook.Application;

public enum DaemonEndpointKind
{
    LocalLoopback,
    TailscaleServe
}

/// <summary>A validated daemon origin. Paths are always built relative to this root.</summary>
public sealed record DaemonEndpoint
{
    private DaemonEndpoint(Uri origin, DaemonEndpointKind kind)
    {
        Origin = origin;
        Kind = kind;
    }

    public Uri Origin { get; }
    public DaemonEndpointKind Kind { get; }

    public static DaemonEndpoint Parse(string value, DaemonEndpointKind? requiredKind = null)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 4096
            || !Uri.TryCreate(value, UriKind.Absolute, out var origin))
            throw Invalid();

        var kind = Classify(origin, value);
        if (requiredKind is not null && kind != requiredKind) throw Invalid();
        return new DaemonEndpoint(origin, kind);
    }

    private static DaemonEndpointKind Classify(Uri origin, string original)
    {
        if (!origin.IsAbsoluteUri || origin.UserInfo.Length != 0 || origin.AbsolutePath != "/"
            || origin.Query.Length != 0 || origin.Fragment.Length != 0)
            throw Invalid();

        if (origin.Scheme == Uri.UriSchemeHttp && origin.IsLoopback)
        {
            var host = origin.Host.Trim('[', ']');
            var originalHost = OriginalAuthorityHost(origin);
            var allowed = string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
                ? string.Equals(originalHost, "localhost", StringComparison.OrdinalIgnoreCase)
                : IPAddress.TryParse(host, out var address) && (address.AddressFamily switch
                {
                    System.Net.Sockets.AddressFamily.InterNetwork => originalHost == "127.0.0.1",
                    System.Net.Sockets.AddressFamily.InterNetworkV6 => address.Equals(IPAddress.IPv6Loopback),
                    _ => false
                });
            if (!allowed)
                throw Invalid();
            return DaemonEndpointKind.LocalLoopback;
        }

        if (origin.Scheme != Uri.UriSchemeHttps || !origin.IsDefaultPort || origin.HostNameType != UriHostNameType.Dns)
            throw Invalid();

        // Remote origins are deliberately narrower than ordinary HTTPS. Requiring
        // canonical ASCII serialization avoids Unicode/punycode and authority
        // normalization surprises before any credential is attached.
        if (!string.Equals(origin.AbsoluteUri, original, StringComparison.Ordinal)
            || !string.Equals(origin.Host, origin.IdnHost, StringComparison.Ordinal)
            || origin.Host.EndsWith('.'))
            throw Invalid();

        var labels = origin.Host.Split('.');
        if (labels.Length != 4 || labels[^2] != "ts" || labels[^1] != "net"
            || labels.Any(label => !ValidDnsLabel(label)))
            throw Invalid();

        return DaemonEndpointKind.TailscaleServe;
    }

    private static string OriginalAuthorityHost(Uri origin)
    {
        var authority = origin.OriginalString.AsSpan((origin.Scheme.Length + 3)..);
        var slash = authority.IndexOf('/');
        if (slash >= 0) authority = authority[..slash];
        if (authority.StartsWith("["))
        {
            var close = authority.IndexOf(']');
            return close > 0 ? authority[1..close].ToString() : string.Empty;
        }
        var colon = authority.LastIndexOf(':');
        return (colon >= 0 ? authority[..colon] : authority).ToString();
    }

    private static bool ValidDnsLabel(string label)
        => label.Length is >= 1 and <= 63
            && label[0] != '-' && label[^1] != '-'
            && !label.StartsWith("xn--", StringComparison.Ordinal)
            && label.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');

    private static SnookException Invalid() => new(SnookErrorCode.ValidationFailed,
        "Daemon endpoint must be a canonical HTTP loopback root or an HTTPS <machine>.<tailnet>.ts.net root on port 443, with no credentials, path, query, or fragment.");
}
