namespace ADM.Core;

/// <summary>Which parts of a download the Find bar looks in.</summary>
[Flags]
public enum SearchTargets
{
    None = 0,
    Names = 1,
    Descriptions = 2,
    /// <summary>The site, the link, the page it came from and the referer.</summary>
    Addresses = 4,
}

public sealed record SearchQuery(string Text, SearchTargets In, bool MatchCase, bool WholeString);

/// <summary>The Find bar's search: which downloads match, and where Find next goes from the selection.</summary>
public static class DownloadSearch
{
    public static bool Matches(DownloadItem item, SearchQuery query) =>
        query.Text.Length > 0 && Fields(item, query.In).Any(field => Match(field, query));

    /// <summary>
    /// The first match after <paramref name="after"/> in list order. With none below it, the search goes on
    /// from the top and says it wrapped; the selection itself can be what it comes back to.
    /// </summary>
    public static (DownloadItem? Found, bool Wrapped) Next(IReadOnlyList<DownloadItem> ordered, DownloadItem? after, SearchQuery query)
    {
        var start = after == null ? 0 : IndexOf(ordered, after) + 1;
        for (int i = start; i < ordered.Count; i++)
            if (Matches(ordered[i], query)) return (ordered[i], false);
        for (int i = 0; i < start; i++)
            if (Matches(ordered[i], query)) return (ordered[i], true);
        return (null, false);
    }

    static int IndexOf(IReadOnlyList<DownloadItem> ordered, DownloadItem item)
    {
        for (int i = 0; i < ordered.Count; i++)
            if (ReferenceEquals(ordered[i], item)) return i;
        return -1;
    }

    static IEnumerable<string?> Fields(DownloadItem item, SearchTargets targets)
    {
        if (targets.HasFlag(SearchTargets.Names)) yield return item.FileName;
        if (targets.HasFlag(SearchTargets.Descriptions)) yield return item.Description;
        if (targets.HasFlag(SearchTargets.Addresses))
        {
            yield return Uri.TryCreate(item.Url, UriKind.Absolute, out var uri) ? uri.Host : null;
            yield return item.Url;
            yield return item.PageUrl;
            yield return item.Referrer;
        }
    }

    static bool Match(string? field, SearchQuery query)
    {
        if (string.IsNullOrEmpty(field)) return false;
        var comparison = query.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        return query.WholeString ? string.Equals(field, query.Text, comparison) : field.Contains(query.Text, comparison);
    }
}
