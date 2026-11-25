using System.Net;

namespace ADM.Core;

/// <summary>
/// The proxy a browser uses, as the extension reports it with a download: <c>http://h:p</c>,
/// <c>https://h:p</c>, <c>socks4://h:p</c>, <c>socks5://h:p</c> or <c>pac:{url}</c>. A download that fails on
/// its own route tries this one, since the browser reached the file from where it is.
/// </summary>
public static class BrowserProxy
{

    /// <summary>The route the text names; null if it names none ADM can use.</summary>
    public static ProxySettings? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (text.StartsWith("pac:", StringComparison.Ordinal))
        {
            var script = text["pac:".Length..];
            return Uri.TryCreate(script, UriKind.Absolute, out var address) && address.UserInfo.Length == 0
                && (address.Scheme == Uri.UriSchemeHttp || address.Scheme == Uri.UriSchemeHttps)
                ? new ProxySettings { Mode = ProxyMode.Script, ScriptUrl = script }
                : null;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var proxy) || proxy.Host.Length == 0 || proxy.Port <= 0
            || proxy.UserInfo.Length > 0 || proxy.PathAndQuery != "/" || proxy.Fragment.Length > 0)
            return null;
        ProxyKind? kind = proxy.Scheme switch
        {
            "http" => ProxyKind.Http,
            "https" => ProxyKind.Https,
            "socks4" => ProxyKind.Socks4,
            "socks5" => ProxyKind.Socks5,
            _ => null,
        };
        // The browser's list of exceptions has already been applied to this address.
        return kind == null ? null : new ProxySettings { Mode = ProxyMode.Manual, Kind = kind.Value, Host = proxy.Host, Port = proxy.Port, Bypass = "" };
    }

    /// <summary>The text if it names a route, for saving with a download. What it doesn't name is dropped, a login among it, so none reaches the state file.</summary>
    internal static string? Usable(string? text) => Parse(text) != null ? text : null;

    /// <summary>
    /// Whether both send requests the same way. The Windows settings name no server to compare, so "System"
    /// never matches; at worst that is one more try through the same proxy.
    /// </summary>
    internal static bool SameRoute(ProxySettings a, ProxySettings b) => a.Mode == b.Mode && a.Mode switch
    {
        ProxyMode.Manual => a.Kind == b.Kind && a.Port == b.Port && string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase),
        ProxyMode.Script => a.ScriptUrl == b.ScriptUrl,
        _ => false,
    };

    /// <summary>
    /// The route to try after <paramref name="failure"/>: no answer at all, or a 403, on a web address, with the
    /// option on and a proxy from the browser that isn't the route that just failed. Null otherwise.
    /// </summary>
    internal static ProxySettings? RouteAfter(Exception failure, DownloadItem item, Settings settings, ProxySettings own, CancellationToken ct)
    {
        if (!settings.UseBrowserProxyOnErrors) return null;
        if (failure is not HttpRequestException { StatusCode: HttpStatusCode.Forbidden } && !Net.IsConnectionFailure(failure, ct)) return null;
        if (!Uri.TryCreate(item.Url, UriKind.Absolute, out var address) || (address.Scheme != Uri.UriSchemeHttp && address.Scheme != Uri.UriSchemeHttps)) return null;
        return Parse(item.BrowserProxy) is { } route && !SameRoute(route, own) ? route : null;
    }
}
