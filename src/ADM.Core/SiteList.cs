using System.Text.RegularExpressions;

namespace ADM.Core;

/// <summary>
/// Space-separated site and address patterns, as IDM uses for capture exceptions:
/// "*.example.com" matches the host; a pattern with a slash matches the whole URL.
/// </summary>
public static class SiteList
{
    public static bool Matches(string? list, string url)
    {
        if (string.IsNullOrWhiteSpace(list) || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        foreach (var pattern in list.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            if (Wildcard(pattern, pattern.Contains('/') ? url : uri.Host))
                return true;
        return false;
    }

    /// <summary>Case-insensitive match where * stands for any run of characters.</summary>
    internal static bool Wildcard(string pattern, string text) =>
        Regex.IsMatch(text, "^" + Regex.Escape(pattern).Replace(@"\*", ".*") + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
