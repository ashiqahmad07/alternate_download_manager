using System.Text.RegularExpressions;

namespace ADM.Core;

/// <summary>
/// Which files a project keeps by name: masks with * as usual, split on commas or semicolons, and the
/// words &lt;start page&gt; for the start page. Addresses come from a server, so nothing here backtracks.
/// </summary>
public sealed class GrabberMask
{
    const string StartPageWord = "<start page>";

    readonly List<Regex> _names = new();
    bool _startPage;

    GrabberMask() { }

    public bool IsEmpty => _names.Count == 0 && !_startPage;

    public static GrabberMask Parse(string? text)
    {
        var mask = new GrabberMask();
        foreach (var part in (text ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (string.Equals(part, StartPageWord, StringComparison.OrdinalIgnoreCase)) mask._startPage = true;
            else mask._names.Add(Wildcards.Compile(part == "*.*" ? "*" : part));
        }
        return mask;
    }

    /// <param name="startUrl">The start page, which the words stand for.</param>
    public bool Matches(string fileName, string url, string? startUrl)
    {
        if (_startPage && string.Equals(url, startUrl, StringComparison.Ordinal)) return true;
        foreach (var name in _names)
            if (name.IsMatch(fileName)) return true;
        return false;
    }
}

/// <summary>
/// Hosts and paths, split on semicolons, with * as usual. An entry with no slash is a host, one starting with a
/// slash is a path, and any other is an address, with or without its scheme. A * in one with no slash also looks
/// through the whole address, so *logout* does what it says.
/// </summary>
public sealed class GrabberPathList
{
    readonly List<Regex> _hosts = new();
    readonly List<Regex> _paths = new();
    readonly List<Regex> _addresses = new();

    GrabberPathList() { }

    public bool IsEmpty => _hosts.Count == 0 && _paths.Count == 0 && _addresses.Count == 0;

    public static GrabberPathList Parse(string? text)
    {
        var list = new GrabberPathList();
        foreach (var part in (text ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.StartsWith('/')) list._paths.Add(Wildcards.Compile(part));
            else if (part.Contains('/')) list._addresses.Add(Wildcards.Compile(part));
            else
            {
                list._hosts.Add(Wildcards.Compile(part));
                if (part.Contains('*')) list._addresses.Add(Wildcards.Compile(part));
            }
        }
        return list;
    }

    public bool Matches(Uri address)
    {
        foreach (var host in _hosts)
            if (host.IsMatch(address.Host)) return true;
        if (_paths.Count > 0)
        {
            // As the address reads, and as a person would type it.
            var path = address.PathAndQuery;
            var readable = Uri.UnescapeDataString(path);
            foreach (var pattern in _paths)
                if (pattern.IsMatch(path) || pattern.IsMatch(readable)) return true;
        }
        if (_addresses.Count > 0)
        {
            var whole = address.AbsoluteUri;
            var bare = address.Host + address.PathAndQuery;
            foreach (var pattern in _addresses)
                if (pattern.IsMatch(whole) || pattern.IsMatch(bare)) return true;
        }
        return false;
    }
}

static class Wildcards
{
    public static Regex Compile(string pattern) => new(
        "^" + Regex.Escape(pattern).Replace(@"\*", ".*") + "$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline | RegexOptions.NonBacktracking);
}
