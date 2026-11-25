using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace ADM.Core;

public enum PageLinkKind
{
    /// <summary>A link to follow, which takes a level.</summary>
    Link,
    /// <summary>A page shown as part of this one.</summary>
    Frame,
    /// <summary>A picture, a video or the like, which is a file whatever its address looks like.</summary>
    Embedded,
    Style,
    Script,
    Refresh,
    Base,
}

/// <param name="Start">Where the raw value is in the text, to replace it.</param>
/// <param name="Value">What it says, decoded.</param>
/// <param name="Text">The words of a link.</param>
/// <param name="ScriptText">Found in a script's text, where it can't be replaced safely.</param>
/// <param name="Remove">Attributes of its element that a file on disk can't satisfy: integrity and crossorigin.</param>
/// <param name="Raw">In a stylesheet or a style block, where an entity is only text.</param>
public readonly record struct PageLinkSpan(int Start, int Length, string Value, PageLinkKind Kind, string Text = "", bool ScriptText = false, IReadOnlyList<(int Start, int Length)>? Remove = null, bool Raw = false);

/// <summary>
/// Finds the links in a web page, a stylesheet or a script, which all come from a server: one pass with no
/// backtracking, in which nothing is scanned twice, so time goes with the size however broken the text is.
/// Whatever is never closed ends the pass, since nothing later could close it.
/// </summary>
public static class PageParser
{
    const int MaxLinkText = 200;
    const int MaxAttributes = 64;
    const int MaxScriptString = 2048;

    readonly record struct Attribute(int NameStart, int NameLength, int ValueStart, int ValueLength, int End);

    public static List<PageLinkSpan> Html(string text, Func<string, string>? decode = null)
    {
        var found = new List<PageLinkSpan>();
        var attributes = new List<Attribute>(MaxAttributes);
        int link = -1;
        var words = new StringBuilder();
        int i = 0;
        while (i < text.Length)
        {
            int open = text.IndexOf('<', i);
            if (link >= 0 && words.Length < MaxLinkText * 4)
                words.Append(text, i, Math.Min((open < 0 ? text.Length : open) - i, MaxLinkText * 4));
            if (open < 0 || open + 1 >= text.Length) break;
            i = open;
            char next = text[i + 1];
            if (next == '!' && string.CompareOrdinal(text, i, "<!--", 0, 4) == 0)
            {
                i = After(text, "-->", i + 4);
            }
            else if (next is '!' or '?')
            {
                i = After(text, ">", i + 2);
            }
            else if (next == '/')
            {
                int nameStart = i + 2;
                int end = text.IndexOf('>', nameStart);
                if (end < 0) break;
                if (IsName(text, nameStart, end, "a")) EndLink(found, ref link, words, decode);
                i = end + 1;
            }
            else if (char.IsAsciiLetter(next))
            {
                i = ReadTag(text, i, found, attributes, ref link, words, decode);
            }
            else
            {
                i++;
            }
        }
        EndLink(found, ref link, words, decode);
        return found;
    }

    static int After(string text, string close, int from)
    {
        int at = text.IndexOf(close, from, StringComparison.Ordinal);
        return at < 0 ? text.Length : at + close.Length;
    }

    static bool IsName(string text, int start, int end, string name)
    {
        int stop = start;
        while (stop < end && !IsSpace(text[stop]) && text[stop] != '/') stop++;
        return stop - start == name.Length && string.Compare(text, start, name, 0, name.Length, StringComparison.OrdinalIgnoreCase) == 0;
    }

    static bool IsSpace(char c) => c is ' ' or '\t' or '\n' or '\r' or '\f';

    static void EndLink(List<PageLinkSpan> found, ref int link, StringBuilder words, Func<string, string>? decode)
    {
        if (link < 0) return;
        found[link] = found[link] with { Text = CleanText(words.ToString(), decode) };
        link = -1;
        words.Clear();
    }

    static string CleanText(string raw, Func<string, string>? decode)
    {
        var text = WebUtility.HtmlDecode(decode?.Invoke(raw) ?? raw);
        var clean = new StringBuilder(Math.Min(text.Length, MaxLinkText));
        foreach (var c in text)
        {
            if (clean.Length == MaxLinkText) break;
            if (!char.IsWhiteSpace(c)) clean.Append(c);
            else if (clean.Length > 0 && clean[^1] != ' ') clean.Append(' ');
        }
        return clean.ToString().TrimEnd();
    }

    /// <summary>Reads the tag that starts at <paramref name="at"/>, and the raw text of an element that holds no tags. Returns where to go on.</summary>
    static int ReadTag(string text, int at, List<PageLinkSpan> found, List<Attribute> attributes, ref int link, StringBuilder words, Func<string, string>? decode)
    {
        int i = at + 1;
        int nameStart = i;
        while (i < text.Length && !IsSpace(text[i]) && text[i] != '/' && text[i] != '>') i++;
        var name = text[nameStart..Math.Min(i, nameStart + 16)].ToLowerInvariant();
        attributes.Clear();

        while (true)
        {
            while (i < text.Length && (IsSpace(text[i]) || text[i] == '/')) i++;
            // Never closed, so this is no tag, and nothing after it can be one either.
            if (i >= text.Length) return text.Length;
            if (text[i] == '>') { i++; break; }

            int attributeName = i;
            if (text[i] == '=') i++;
            while (i < text.Length && !IsSpace(text[i]) && text[i] != '=' && text[i] != '>' && text[i] != '/') i++;
            int attributeNameLength = i - attributeName;
            while (i < text.Length && IsSpace(text[i])) i++;
            int valueStart = -1, valueLength = 0;
            if (i < text.Length && text[i] == '=')
            {
                i++;
                while (i < text.Length && IsSpace(text[i])) i++;
                if (i >= text.Length) return text.Length;
                if (text[i] is '"' or '\'')
                {
                    int close = text.IndexOf(text[i], i + 1);
                    if (close < 0) return text.Length;
                    valueStart = i + 1;
                    valueLength = close - valueStart;
                    i = close + 1;
                }
                else
                {
                    valueStart = i;
                    while (i < text.Length && !IsSpace(text[i]) && text[i] != '>') i++;
                    valueLength = i - valueStart;
                }
            }
            if (attributes.Count < MaxAttributes) attributes.Add(new Attribute(attributeName, attributeNameLength, valueStart, valueLength, i));
        }

        ReadElement(text, name, attributes, found, ref link, words, decode);

        switch (name)
        {
            case "script":
            {
                int end = text.IndexOf("</script", i, StringComparison.OrdinalIgnoreCase);
                var body = end < 0 ? text.Length : end;
                foreach (var span in Script(text.AsSpan(i, body - i).ToString()))
                    found.Add(span with { Start = span.Start + i });
                return end < 0 ? text.Length : end;
            }
            case "style":
            {
                int end = text.IndexOf("</style", i, StringComparison.OrdinalIgnoreCase);
                var body = end < 0 ? text.Length : end;
                ReadCss(text, i, body, found, decode, entities: false);
                return end < 0 ? text.Length : end;
            }
            case "textarea" or "title" or "xmp":
            {
                int end = text.IndexOf("</" + name, i, StringComparison.OrdinalIgnoreCase);
                return end < 0 ? text.Length : end;
            }
        }
        return i;
    }

    static void ReadElement(string text, string name, List<Attribute> attributes, List<PageLinkSpan> found, ref int link, StringBuilder words, Func<string, string>? decode)
    {
        int before = found.Count;
        switch (name)
        {
            case "a":
                EndLink(found, ref link, words, decode);
                if (Add(text, attributes, "href", PageLinkKind.Link, found, decode)) link = found.Count - 1;
                break;
            case "area":
                Add(text, attributes, "href", PageLinkKind.Link, found, decode);
                break;
            case "link":
                ReadLinkElement(text, attributes, found, decode);
                break;
            case "img" or "source" or "video":
                Add(text, attributes, "src", PageLinkKind.Embedded, found, decode);
                AddSrcset(text, attributes, "srcset", found, decode);
                Add(text, attributes, "data-src", PageLinkKind.Embedded, found, decode);
                AddSrcset(text, attributes, "data-srcset", found, decode);
                Add(text, attributes, "data-original", PageLinkKind.Embedded, found, decode);
                Add(text, attributes, "data-lazy-src", PageLinkKind.Embedded, found, decode);
                if (name == "video") Add(text, attributes, "poster", PageLinkKind.Embedded, found, decode);
                break;
            case "audio" or "track" or "embed":
                Add(text, attributes, "src", PageLinkKind.Embedded, found, decode);
                break;
            case "object":
                Add(text, attributes, "data", PageLinkKind.Embedded, found, decode);
                break;
            case "input":
                if (string.Equals(Value(text, attributes, "type"), "image", StringComparison.OrdinalIgnoreCase))
                    Add(text, attributes, "src", PageLinkKind.Embedded, found, decode);
                break;
            case "script":
                Add(text, attributes, "src", PageLinkKind.Script, found, decode);
                break;
            case "frame" or "iframe":
                Add(text, attributes, "src", PageLinkKind.Frame, found, decode);
                break;
            case "base":
                Add(text, attributes, "href", PageLinkKind.Base, found, decode);
                break;
            case "meta":
                ReadRefresh(text, attributes, found, decode);
                break;
        }
        if (found.Count > before && Removable(text, attributes) is { } remove) found[before] = found[before] with { Remove = remove };
        Add(text, attributes, "background", PageLinkKind.Embedded, found, decode);
        if (Find(text, attributes, "style") is { ValueStart: >= 0 } style)
            ReadCss(text, style.ValueStart, style.ValueStart + style.ValueLength, found, decode, entities: true);
    }

    static List<(int Start, int Length)>? Removable(string text, List<Attribute> attributes)
    {
        List<(int Start, int Length)>? remove = null;
        foreach (var name in new[] { "integrity", "crossorigin" })
            if (Find(text, attributes, name) is { } attribute)
                (remove ??= new()).Add((attribute.NameStart, attribute.End - attribute.NameStart));
        return remove;
    }

    static void ReadLinkElement(string text, List<Attribute> attributes, List<PageLinkSpan> found, Func<string, string>? decode)
    {
        var rel = (Value(text, attributes, "rel") ?? "").ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        PageLinkKind? kind = null;
        foreach (var word in rel)
        {
            if (word == "stylesheet") kind = PageLinkKind.Style;
            else if (word is "icon" or "preload" or "apple-touch-icon" or "mask-icon") kind ??= PageLinkKind.Embedded;
            else if (word is "alternate" or "next" or "prev") kind ??= PageLinkKind.Link;
        }
        if (kind != null) Add(text, attributes, "href", kind.Value, found, decode);
    }

    static void ReadRefresh(string text, List<Attribute> attributes, List<PageLinkSpan> found, Func<string, string>? decode)
    {
        if (!string.Equals(Value(text, attributes, "http-equiv"), "refresh", StringComparison.OrdinalIgnoreCase)) return;
        if (Find(text, attributes, "content") is not { ValueStart: >= 0 } content) return;
        int stop = content.ValueStart + content.ValueLength;
        int url = IndexOfIgnoreCase(text, "url", content.ValueStart, stop);
        if (url < 0) return;
        int i = url + 3;
        while (i < stop && IsSpace(text[i])) i++;
        if (i >= stop || text[i] != '=') return;
        i++;
        while (i < stop && IsSpace(text[i])) i++;
        if (i < stop && text[i] is '\'' or '"')
        {
            int close = text.IndexOf(text[i], i + 1, stop - i - 1);
            int end = close < 0 ? stop : close;
            AddValue(text, i + 1, end - i - 1, PageLinkKind.Refresh, found, decode);
        }
        else
        {
            AddValue(text, i, stop - i, PageLinkKind.Refresh, found, decode);
        }
    }

    static int IndexOfIgnoreCase(string text, string value, int start, int stop) =>
        stop <= start ? -1 : text.IndexOf(value, start, stop - start, StringComparison.OrdinalIgnoreCase);

    static Attribute? Find(string text, List<Attribute> attributes, string name)
    {
        foreach (var attribute in attributes)
            if (attribute.NameLength == name.Length && string.Compare(text, attribute.NameStart, name, 0, name.Length, StringComparison.OrdinalIgnoreCase) == 0)
                return attribute;
        return null;
    }

    static string? Value(string text, List<Attribute> attributes, string name) =>
        Find(text, attributes, name) is { ValueStart: >= 0 } attribute ? WebUtility.HtmlDecode(text.Substring(attribute.ValueStart, attribute.ValueLength)).Trim() : null;

    static bool Add(string text, List<Attribute> attributes, string name, PageLinkKind kind, List<PageLinkSpan> found, Func<string, string>? decode)
    {
        if (Find(text, attributes, name) is not { ValueStart: >= 0 } attribute) return false;
        return AddValue(text, attribute.ValueStart, attribute.ValueLength, kind, found, decode);
    }

    static bool AddValue(string text, int start, int length, PageLinkKind kind, List<PageLinkSpan> found, Func<string, string>? decode, bool raw = false)
    {
        var value = CleanValue(text.Substring(start, length), decode, raw);
        if (value.Length == 0) return false;
        found.Add(new PageLinkSpan(start, length, value, kind, Raw: raw));
        return true;
    }

    // Browsers take out tabs and line breaks, and leading and trailing spaces. The bytes come first: an
    // entity already stands for a character, and isn't bytes of the page's encoding.
    static string CleanValue(string text, Func<string, string>? decode, bool raw)
    {
        var read = decode?.Invoke(text) ?? text;
        var value = (raw ? read : WebUtility.HtmlDecode(read)).Trim();
        return value.IndexOfAny(new[] { '\t', '\n', '\r' }) < 0 ? value : value.Replace("\t", "").Replace("\n", "").Replace("\r", "");
    }

    static void AddSrcset(string text, List<Attribute> attributes, string name, List<PageLinkSpan> found, Func<string, string>? decode)
    {
        if (Find(text, attributes, name) is not { ValueStart: >= 0 } attribute) return;
        int i = attribute.ValueStart;
        int stop = attribute.ValueStart + attribute.ValueLength;
        while (i < stop)
        {
            while (i < stop && (IsSpace(text[i]) || text[i] == ',')) i++;
            int start = i;
            while (i < stop && !IsSpace(text[i])) i++;
            int end = i;
            // A comma at the end of the address closes the candidate; one inside belongs to it.
            bool closed = end > start && text[end - 1] == ',';
            while (end > start && text[end - 1] == ',') end--;
            if (end > start) AddValue(text, start, end - start, PageLinkKind.Embedded, found, decode);
            if (closed) continue;
            while (i < stop && text[i] != ',') i++;
        }
    }

    public static List<PageLinkSpan> Css(string text, Func<string, string>? decode = null)
    {
        var found = new List<PageLinkSpan>();
        ReadCss(text, 0, text.Length, found, decode, entities: false);
        return found;
    }

    /// <param name="entities">In a style attribute, a quote can be written as an entity.</param>
    static void ReadCss(string text, int start, int stop, List<PageLinkSpan> found, Func<string, string>? decode, bool entities)
    {
        int i = start;
        while (i < stop)
        {
            char c = text[i];
            if (c == '/' && i + 1 < stop && text[i + 1] == '*')
            {
                int close = stop - i - 2 > 0 ? text.IndexOf("*/", i + 2, stop - i - 2, StringComparison.Ordinal) : -1;
                if (close < 0) return;
                i = close + 2;
            }
            else if (c is '"' or '\'')
            {
                i = SkipCssString(text, i, stop);
            }
            else if (c is 'u' or 'U' && IsAt(text, i, stop, "url("))
            {
                i = ReadCssUrl(text, i + 4, stop, PageLinkKind.Embedded, found, decode, entities);
            }
            else if (c == '@' && IsAt(text, i, stop, "@import"))
            {
                i = ReadCssImport(text, i + 7, stop, found, decode, entities);
            }
            else
            {
                i++;
            }
        }
    }

    static bool IsAt(string text, int at, int stop, string word) =>
        at + word.Length <= stop && string.Compare(text, at, word, 0, word.Length, StringComparison.OrdinalIgnoreCase) == 0;

    /// <summary>Goes past a string, which ends at its quote, or at the end of its line if it is never closed.</summary>
    static int SkipCssString(string text, int at, int stop)
    {
        char quote = text[at];
        int i = at + 1;
        while (i < stop)
        {
            char c = text[i];
            if (c == '\\') { i += 2; continue; }
            if (c == quote) return i + 1;
            if (c == '\n') return i;
            i++;
        }
        return stop;
    }

    static int ReadCssImport(string text, int at, int stop, List<PageLinkSpan> found, Func<string, string>? decode, bool entities)
    {
        int i = at;
        while (i < stop && IsSpace(text[i])) i++;
        if (IsAt(text, i, stop, "url(")) return ReadCssUrl(text, i + 4, stop, PageLinkKind.Style, found, decode, entities);
        if (i < stop && text[i] is '"' or '\'')
        {
            int end = SkipCssString(text, i, stop);
            if (end > i + 1 && text[end - 1] == text[i]) AddCssValue(text, i + 1, end - i - 2, PageLinkKind.Style, found, decode, entities);
            return end;
        }
        return i;
    }

    static int ReadCssUrl(string text, int at, int stop, PageLinkKind kind, List<PageLinkSpan> found, Func<string, string>? decode, bool entities)
    {
        int i = at;
        while (i < stop && IsSpace(text[i])) i++;
        if (i >= stop) return stop;
        if (text[i] is '"' or '\'')
        {
            int end = SkipCssString(text, i, stop);
            if (end > i + 1 && text[end - 1] == text[i]) AddCssValue(text, i + 1, end - i - 2, kind, found, decode, entities);
            return end;
        }
        if (entities && QuoteEntity(text, i, stop) is { } entity)
        {
            int valueStart = i + entity.Length;
            int close = stop - valueStart > 0 ? text.IndexOf(entity, valueStart, stop - valueStart, StringComparison.Ordinal) : -1;
            if (close < 0) return stop;
            AddCssValue(text, valueStart, close - valueStart, kind, found, decode, entities);
            return close + entity.Length;
        }
        int paren = text.IndexOf(')', i, stop - i);
        if (paren < 0) return stop;
        int last = paren;
        while (last > i && IsSpace(text[last - 1])) last--;
        AddCssValue(text, i, last - i, kind, found, decode, entities);
        return paren + 1;
    }

    static string? QuoteEntity(string text, int at, int stop)
    {
        foreach (var entity in new[] { "&quot;", "&#34;", "&#39;", "&apos;" })
            if (IsAt(text, at, stop, entity)) return entity;
        return null;
    }

    static void AddCssValue(string text, int start, int length, PageLinkKind kind, List<PageLinkSpan> found, Func<string, string>? decode, bool entities)
    {
        if (length <= 0 || IsAt(text, start, start + length, "data:")) return;
        AddValue(text, start, length, kind, found, decode, raw: !entities);
    }

    /// <summary>
    /// The addresses written out as strings in a script, which isn't run. An address built by an expression isn't found.
    /// Each counts as a link, so a page is explored and a file listed. Comments aren't told apart from code, because
    /// finding where a comment starts takes reading the script as a whole: a line's apostrophe ends with the line.
    /// </summary>
    public static List<PageLinkSpan> Script(string text, Func<string, string>? decode = null)
    {
        var found = new List<PageLinkSpan>();
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '\\') { i += 2; continue; }
            if (c is not ('"' or '\'' or '`')) { i++; continue; }
            int start = i + 1;
            int end = start;
            bool closed = false;
            while (end < text.Length)
            {
                char inner = text[end];
                if (inner == '\\') { end += 2; continue; }
                if (inner == c) { closed = true; break; }
                if (inner == '\n' && c != '`') break;
                end++;
            }
            // Never closed: there is nothing left that could close it.
            if (end >= text.Length) return found;
            if (closed && end - start <= MaxScriptString && end > start)
            {
                var value = text[start..end].Replace("\\/", "/");
                if (decode != null) value = decode(value);
                if (IsAddress(value)) found.Add(new PageLinkSpan(start, end - start, value, PageLinkKind.Link, ScriptText: true));
            }
            i = closed ? end + 1 : end;
        }
        return found;
    }

    static bool IsAddress(string value)
    {
        foreach (var c in value)
            if (c <= ' ' || c is '<' or '>' or '{' or '}' or '$' or '\\' or '"' or '\'' or '`' or '|' or '^') return false;
        if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase))
            return Uri.TryCreate(value, UriKind.Absolute, out _);
        if (value.Contains(':')) return false;
        if (value.StartsWith("//")) return Uri.TryCreate("http:" + value, UriKind.Absolute, out var uri) && uri.Host.Contains('.');
        int cut = value.IndexOfAny(new[] { '?', '#' });
        var path = cut < 0 ? value : value[..cut];
        return WebFileTypes.IsKnown(Path.GetExtension(path));
    }
}

/// <summary>What an address's extension says about what's behind it.</summary>
public static class WebFileTypes
{
    // A page by its name; some of these make files too, so only an answer settles it.
    static readonly HashSet<string> Static = Set("html htm xhtml xht shtml");
    static readonly HashSet<string> Dynamic = Set("php php3 php4 php5 phtml asp aspx jsp jspx cgi cfm pl py do action");
    static readonly HashSet<string> Files = Set(
        "gif jpg jpeg jpe jfif png bmp tif tiff webp svg ico avif heic " +
        "mp4 mkv avi mov webm flv wmv m4v ts mts 3gp mpg mpeg ogv asf rm rmvb " +
        "mp3 wav flac aac m4a ogg oga wma opus aif aiff mid midi " +
        "zip rar 7z gz tgz tar bz2 xz zst iso cab arj lzh ace " +
        "pdf doc docx xls xlsx ppt pptx txt rtf odt ods odp epub csv xml json chm mobi " +
        "exe msi msix msixbundle appx apk dmg deb rpm appimage bin dll jar " +
        "css js mjs woff woff2 ttf otf eot swf");

    static HashSet<string> Set(string extensions) =>
        new(extensions.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);

    static string Bare(string extension) => extension.TrimStart('.');

    /// <summary>An extension seen on the web, with or without its dot.</summary>
    public static bool IsKnown(string extension) =>
        Static.Contains(Bare(extension)) || Dynamic.Contains(Bare(extension)) || Files.Contains(Bare(extension));

    /// <summary>The name of a page, or of something that makes pages: no extension says the same.</summary>
    public static bool IsPage(string extension) => Bare(extension).Length == 0 || Static.Contains(Bare(extension)) || Dynamic.Contains(Bare(extension));

    /// <summary>A file whatever else it might be, and never worth asking for as a page.</summary>
    public static bool IsFile(string extension) => Files.Contains(Bare(extension));

    /// <summary>The extension is the file's own, so a filter can go by it. "get.php" can give any file.</summary>
    public static bool NameTells(string extension) => Files.Contains(Bare(extension)) || Static.Contains(Bare(extension));
}

/// <summary>
/// A page's bytes, as text that gives them back. Anything but UTF-16 is read one byte to one character, so a page in
/// any encoding, or in none, comes through with only what is changed changing; a link's own value is read by the real encoding.
/// </summary>
public sealed partial class PageBody
{
    const int MetaWindow = 1024;

    [GeneratedRegex(@"charset\s*=\s*[""']?\s*([A-Za-z0-9_:.\-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex MetaCharset();

    static PageBody() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    readonly byte[] _preamble;
    readonly bool _wide;

    PageBody(string text, Encoding encoding, byte[] preamble, bool wide)
    {
        Text = text;
        Encoding = encoding;
        _preamble = preamble;
        _wide = wide;
    }

    public string Text { get; }
    public Encoding Encoding { get; }

    /// <param name="charset">What the server's answer says, if anything.</param>
    public static PageBody From(byte[] bytes, string? charset)
    {
        var data = bytes.AsSpan();
        if (data.StartsWith(new byte[] { 0xFF, 0xFE }))
            return new PageBody(Encoding.Unicode.GetString(data[2..]), Encoding.Unicode, new byte[] { 0xFF, 0xFE }, true);
        if (data.StartsWith(new byte[] { 0xFE, 0xFF }))
            return new PageBody(Encoding.BigEndianUnicode.GetString(data[2..]), Encoding.BigEndianUnicode, new byte[] { 0xFE, 0xFF }, true);
        var text = Encoding.Latin1.GetString(bytes);
        if (data.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) return new PageBody(text, Encoding.UTF8, Array.Empty<byte>(), false);
        var encoding = EncodingNamed(charset);
        if (encoding == null)
        {
            var window = text.Length > MetaWindow ? text[..MetaWindow] : text;
            if (window.Contains("<meta", StringComparison.OrdinalIgnoreCase) && MetaCharset().Match(window) is { Success: true } meta)
                encoding = EncodingNamed(meta.Groups[1].Value);
        }
        return new PageBody(text, encoding ?? Encoding.UTF8, Array.Empty<byte>(), false);
    }

    static Encoding? EncodingNamed(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        try
        {
            return Encoding.GetEncoding(name.Trim().Trim('"', '\''));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>A piece of the text as it was meant to be read, for a link's value.</summary>
    public string Decode(string raw)
    {
        if (_wide) return raw;
        foreach (var c in raw)
            if (c > 0x7F) return Encoding.GetString(Encoding.Latin1.GetBytes(raw));
        return raw;
    }

    /// <summary>Text from <see cref="Text"/>, changed or not, as bytes. What was put in must be ASCII, unless the page is UTF-16.</summary>
    public byte[] Encode(string text) =>
        _wide ? _preamble.Concat(Encoding.GetBytes(text)).ToArray() : Encoding.Latin1.GetBytes(text);
}
