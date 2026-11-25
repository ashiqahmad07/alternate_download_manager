using System.Text;

namespace ADM.Core;

/// <summary>
/// Converts a saved page or stylesheet for offline browsing: a link to a saved file becomes relative, and any
/// other becomes absolute, so it still leads where it did. Every other byte stays as it was.
/// </summary>
public static class GrabberRewriter
{
    /// <param name="kind">A page or a stylesheet; anything else is given back as it is.</param>
    /// <param name="address">Where the text came from, after any redirects.</param>
    /// <param name="path">Where the text will be saved.</param>
    /// <param name="saved">Where an address has been saved, or null if it hasn't.</param>
    public static byte[] Rewrite(byte[] bytes, string? charset, GrabberKind kind, Uri address, string path, Func<Uri, string?> saved)
    {
        if (kind is not (GrabberKind.Page or GrabberKind.Style)) return bytes;
        var body = PageBody.From(bytes, charset);
        var links = kind == GrabberKind.Page ? PageParser.Html(body.Text, body.Decode) : PageParser.Css(body.Text, body.Decode);

        var from = address;
        var firstBase = links.FindIndex(l => l.Kind == PageLinkKind.Base);
        if (firstBase >= 0 && Uri.TryCreate(address, links[firstBase].Value, out var written) && Links.IsDownloadable(written)) from = written;

        var folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var pieces = new List<(int Start, int Length, string Text)>();
        foreach (var link in links)
        {
            if (link.ScriptText) continue;
            if (link.Kind == PageLinkKind.Base)
            {
                // With every link now whole or relative to the file, a base would only lead them astray.
                pieces.Add((link.Start, link.Length, ""));
                continue;
            }
            if (Converted(link, from, folder, saved) is not { } text) continue;
            pieces.Add((link.Start, link.Length, text));
            if (link.Remove != null && IsRelative(text))
                foreach (var attribute in link.Remove) pieces.Add((attribute.Start, attribute.Length, ""));
        }
        return pieces.Count == 0 ? bytes : body.Encode(Applied(body.Text, pieces));
    }

    static bool IsRelative(string text) => !text.Contains("://", StringComparison.Ordinal);

    /// <summary>What a link should say now; null to leave it as it is.</summary>
    static string? Converted(PageLinkSpan link, Uri from, string folder, Func<Uri, string?> saved)
    {
        // Only a place on this page.
        if (link.Value.StartsWith('#')) return null;
        if (!Uri.TryCreate(from, link.Value, out var target) || !Links.IsDownloadable(target)) return null;
        var fragment = target.Fragment;
        var address = new Uri(target.GetLeftPart(UriPartial.Query));
        return Safe(saved(address) is { } file ? Relative(folder, file) : AbsoluteOf(address), link.Raw) + Safe(fragment, link.Raw);
    }

    static string Relative(string folder, string file) =>
        string.Join('/', Path.GetRelativePath(folder, file).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Select(Segment));

    // A folder on another drive leaves its letter in the first segment, as "D:", and it has to stay.
    static string Segment(string segment) =>
        segment.Length == 2 && segment[1] == ':' ? "file:///" + segment : Uri.EscapeDataString(segment);

    static string AbsoluteOf(Uri address)
    {
        var text = address.AbsoluteUri;
        if (text.All(char.IsAscii)) return text;
        return new UriBuilder(address) { Host = address.IdnHost }.Uri.AbsoluteUri;
    }

    // Whatever the attribute's quoting, or a CSS url(), this can't end it. In an attribute, '&' could start an entity; in CSS it's only itself.
    static string Safe(string text, bool raw)
    {
        var safe = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            switch (c)
            {
                case '&': safe.Append(raw ? "&" : "&amp;"); break;
                case '\'': safe.Append("%27"); break;
                case '"': safe.Append("%22"); break;
                case '(': safe.Append("%28"); break;
                case ')': safe.Append("%29"); break;
                case ' ': safe.Append("%20"); break;
                case '<': safe.Append("%3C"); break;
                case '>': safe.Append("%3E"); break;
                default: safe.Append(c); break;
            }
        }
        return safe.ToString();
    }

    static string Applied(string text, List<(int Start, int Length, string Text)> pieces)
    {
        pieces.Sort((a, b) => a.Start.CompareTo(b.Start));
        var result = new StringBuilder(text.Length);
        int at = 0;
        foreach (var (start, length, replacement) in pieces)
        {
            if (start < at) continue;
            result.Append(text, at, start - at).Append(replacement);
            at = start + length;
        }
        return result.Append(text, at, text.Length - at).ToString();
    }
}
