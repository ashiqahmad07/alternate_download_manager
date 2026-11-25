using System.Text.Json;
using System.Text.Json.Nodes;

namespace ADM.Core;

public sealed record PageLink(string Url, string Text, bool IsImage)
{
    /// <summary>The last part of the address's path, or its host when the path has none.</summary>
    public string FileName
    {
        get
        {
            var uri = new Uri(Url);
            var name = Uri.UnescapeDataString(uri.AbsolutePath.Split('/').LastOrDefault() ?? "");
            return name.Length > 0 ? name : uri.Host;
        }
    }
}

/// <summary>The links of a web page, as the browser extension's "Download all links" sends them.</summary>
public sealed class PageLinks
{
    PageLinks(List<PageLink> links, string? referrer, Dictionary<string, string> cookies, Dictionary<string, string> proxies, string? userAgent)
    {
        Links = links;
        Referrer = referrer;
        Cookies = cookies;
        Proxies = proxies;
        UserAgent = userAgent;
    }

    public IReadOnlyList<PageLink> Links { get; }
    public string? Referrer { get; }
    /// <summary>One cookie header per origin, such as "https://example.com".</summary>
    public IReadOnlyDictionary<string, string> Cookies { get; }
    /// <summary>The browser's proxy per origin, as in <see cref="BrowserProxy.Parse"/>.</summary>
    public IReadOnlyDictionary<string, string> Proxies { get; }
    public string? UserAgent { get; }

    /// <summary>Reads a "links" message; anything that is not an http or https link is dropped.</summary>
    public static PageLinks FromMessage(JsonNode message)
    {
        var links = new List<PageLink>();
        foreach (var node in message["links"] as JsonArray ?? new JsonArray())
        {
            if (Text(node?["url"]) is not { } url || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                continue;
            links.Add(new PageLink(uri.ToString(), Text(node!["text"]) ?? "", node["image"]?.GetValueKind() == JsonValueKind.True));
        }

        return new PageLinks(links, Text(message["referrer"]), ByOrigin(message["cookies"]), ByOrigin(message["proxies"]), Text(message["userAgent"]));
    }

    static Dictionary<string, string> ByOrigin(JsonNode? node)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (node is JsonObject map)
            foreach (var (origin, entry) in map)
                if (Text(entry) is { Length: > 0 } value) values[origin] = value;
        return values;
    }

    public DownloadRequest ToRequest(PageLink link) => new()
    {
        Url = link.Url,
        Referrer = Referrer,
        Cookies = Cookies.GetValueOrDefault(new Uri(link.Url).GetLeftPart(UriPartial.Authority)),
        UserAgent = UserAgent,
        BrowserProxy = Proxies.GetValueOrDefault(new Uri(link.Url).GetLeftPart(UriPartial.Authority)),
    };

    /// <param name="types">Space-separated file types with * wildcards; empty keeps every type.</param>
    public IEnumerable<PageLink> Filter(string types, bool hideImages)
    {
        var patterns = types.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(t => t.TrimStart('.')).ToList();
        return Links.Where(l => !(hideImages && l.IsImage)
            && (patterns.Count == 0 || patterns.Any(p => SiteList.Wildcard(p, Path.GetExtension(l.FileName).TrimStart('.')))));
    }

    static string? Text(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;
}
