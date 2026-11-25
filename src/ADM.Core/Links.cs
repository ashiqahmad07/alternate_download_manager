using System.Text.RegularExpressions;

namespace ADM.Core;

/// <summary>Finds downloadable links in free text or HTML, for imports and clipboard batches.</summary>
public static partial class Links
{
    // Stops at whitespace, quotes, angle brackets and parentheses so links inside HTML
    // attributes or prose "(like this)" come out clean.
    [GeneratedRegex(@"(?:https?|ftp)://[^\s""'<>()\[\]{}]+", RegexOptions.IgnoreCase)]
    private static partial Regex LinkPattern();

    /// <summary>Whether ADM can download from this address: http, https or ftp.</summary>
    public static bool IsDownloadable(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeFtp;

    /// <summary>
    /// An HLS playlist or a DASH manifest, by the extension its address ends with; anything else is a file. The
    /// command line and the Add dialog go by this, and the extension's streamKind follows the same rule
    /// (tests/fixtures/stream-kinds.json checks both).
    /// </summary>
    public static DownloadKind StreamKind(string url) =>
        StreamPattern().Match(url) is { Success: true } match
            ? match.Groups[1].Value.Equals("mpd", StringComparison.OrdinalIgnoreCase) ? DownloadKind.Dash : DownloadKind.Hls
            : DownloadKind.Http;

    [GeneratedRegex(@"\.(m3u8|mpd)(?=[?#]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex StreamPattern();

    /// <summary>
    /// Takes a login out of an address: "ftp://user:pass@host/f" gives "ftp://host/f", "user" and "pass".
    /// The user and the password come back unescaped; an address with no login comes back as it was.
    /// </summary>
    public static (string Url, string? UserName, string? Password) SplitLogin(string url)
    {
        // Only an address System.Uri accepts, and reads a login from, is touched; the scan below has to agree
        // with it, or an address every other check rejects (a backslash before the "@", say) would be
        // rewritten into a valid one for another host.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var before) || before.UserInfo.Length == 0) return (url, null, null);
        int start = url.IndexOf("://", StringComparison.Ordinal);
        if (start < 0) return (url, null, null);
        start += 3;
        int end = url.IndexOfAny(new[] { '/', '?', '#' }, start);
        if (end < 0) end = url.Length;
        int at = end > start ? url.LastIndexOf('@', end - 1, end - start) : -1;
        if (at < 0) return (url, null, null);

        var login = url[start..at];
        int colon = login.IndexOf(':');
        var user = Uri.UnescapeDataString(colon < 0 ? login : login[..colon]);
        var password = colon < 0 ? null : Uri.UnescapeDataString(login[(colon + 1)..]);
        var cleaned = url[..start] + url[(at + 1)..];
        // Taking the login out must leave the same server: scheme, host and port as System.Uri reads them.
        if (!Uri.TryCreate(cleaned, UriKind.Absolute, out var after)
            || before.Scheme != after.Scheme || before.Port != after.Port
            || !string.Equals(before.IdnHost, after.IdnHost, StringComparison.OrdinalIgnoreCase))
            return (url, null, null);
        return (cleaned, user, password);
    }

    /// <summary>Returns each http, https or ftp link once, in the order it first appears.</summary>
    public static List<string> Find(string text)
    {
        var found = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in LinkPattern().Matches(text))
        {
            var url = m.Value.TrimEnd('.', ',', ';', ':', '!', '?');
            if (Uri.TryCreate(url, UriKind.Absolute, out _) && seen.Add(url)) found.Add(url);
        }
        return found;
    }
}
