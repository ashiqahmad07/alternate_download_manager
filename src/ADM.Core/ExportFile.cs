using System.Text;

namespace ADM.Core;

/// <param name="Skipped">Entries that can't be taken: forms to post, other kinds of address, and entries cut off.</param>
public sealed record ExportFileContents(IReadOnlyList<DownloadRequest> Requests, int Skipped);

/// <summary>
/// IDM's export file. Each entry runs from a "&lt;" line to a "&gt;" line: the address first, then "name: value" lines.
/// The file is plain text, so what ADM writes leaves out cookies and logins; what it reads may carry cookies.
/// </summary>
public static class ExportFile
{
    const string LineEnd = "\r\n";

    static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <exception cref="FormatException">The text has no entries.</exception>
    public static ExportFileContents Parse(string text)
    {
        var requests = new List<DownloadRequest>();
        int skipped = 0;
        bool anyEntry = false;
        List<string>? entry = null;
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } raw)
        {
            var line = raw.Trim();
            if (line == "<")
            {
                // The entry before this one never ended.
                if (entry != null) skipped++;
                entry = new List<string>();
            }
            else if (line == ">")
            {
                if (entry == null) continue;
                anyEntry = true;
                if (ToRequest(entry) is { } request) requests.Add(request);
                else skipped++;
                entry = null;
            }
            else if (line.Length > 0)
            {
                entry?.Add(line);
            }
        }
        if (!anyEntry) throw new FormatException("This isn't an IDM export file, or the file is damaged.");
        if (entry != null) skipped++;
        return new ExportFileContents(requests, skipped);
    }

    /// <summary>The entry as a request; null when ADM can't fetch it.</summary>
    static DownloadRequest? ToRequest(List<string> entry)
    {
        if (entry.Count == 0 || !Uri.TryCreate(entry[0], UriKind.Absolute, out var uri) || !Links.IsDownloadable(uri)) return null;
        var request = new DownloadRequest { Url = entry[0] };
        foreach (var line in entry.Skip(1))
        {
            int colon = line.IndexOf(':');
            if (colon < 0) continue;
            var value = line[(colon + 1)..].Trim();
            var given = value.Length > 0 ? value : null;
            switch (line[..colon].Trim().ToLowerInvariant())
            {
                // A form to post: ADM only fetches with GET, which would bring back something else.
                case "pd": return null;
                case "referer": request.Referrer = given; break;
                case "cookie": request.Cookies = given; break;
                case "user-agent": request.UserAgent = given; break;
            }
        }
        return request;
    }

    /// <summary>The file's text: UTF-8 when its bytes are valid UTF-8, and the Windows ANSI code page otherwise.</summary>
    public static string ReadText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        try
        {
            return StrictUtf8.GetString(bytes).TrimStart('\uFEFF');
        }
        catch (DecoderFallbackException)
        {
            // Code page 0 is the system's own.
            return (CodePagesEncodingProvider.Instance.GetEncoding(0) ?? Encoding.Latin1).GetString(bytes);
        }
    }

    /// <summary>The export file for these downloads, in the order given. Each value stays on its own line.</summary>
    public static string Write(IEnumerable<DownloadItem> items)
    {
        var text = new StringBuilder();
        foreach (var item in items)
        {
            if (Address(item.Url) is not { } address) continue;
            text.Append('<').Append(LineEnd).Append(address).Append(LineEnd);
            if (Address(item.Referrer) is { } referrer) text.Append("referer: ").Append(referrer).Append(LineEnd);
            if (!string.IsNullOrEmpty(item.UserAgent) && item.UserAgent != Settings.DefaultUserAgent && !BreaksLine(item.UserAgent))
                text.Append("User-Agent: ").Append(item.UserAgent).Append(LineEnd);
            text.Append('>').Append(LineEnd);
        }
        return text.ToString();
    }

    /// <summary>
    /// The address as it was given, without a login, so that importing it again finds the same download;
    /// null when it isn't an http, https or ftp address, or would break a line, which System.Uri allows.
    /// </summary>
    static string? Address(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && Links.IsDownloadable(uri) && !BreaksLine(url)
            ? Links.SplitLogin(url).Url
            : null;

    static bool BreaksLine(string value) => value.AsSpan().IndexOfAny('\r', '\n') >= 0;
}
