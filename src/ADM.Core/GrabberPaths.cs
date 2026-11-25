using System.Security.Cryptography;
using System.Text;

namespace ADM.Core;

/// <summary>
/// Where a project's files go. An address comes from a server, so whatever it says, a path stays under its
/// folder and means the same to Windows as it looks. No two addresses get one path, whatever their case.
/// </summary>
public sealed class GrabberPaths
{
    const int MaxFolderName = 100;
    const int MaxQueryInName = 40;

    static readonly HashSet<string> Reserved = new(
        new[] { "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$", "COM\u00b9", "COM\u00b2", "COM\u00b3", "LPT\u00b9", "LPT\u00b2", "LPT\u00b3" }
            .Concat(Enumerable.Range(0, 10).SelectMany(i => new[] { "COM" + i, "LPT" + i })),
        StringComparer.OrdinalIgnoreCase);

    readonly HashSet<string> _files = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _folders = new(StringComparer.OrdinalIgnoreCase);
    readonly bool _keepSubfolders;
    readonly bool _overwrite;

    /// <param name="keepSubfolders">Files go under their host and the site's own folders.</param>
    /// <param name="overwrite">A file already on disk may be replaced; otherwise the new one gets a number.</param>
    public GrabberPaths(bool keepSubfolders, bool overwrite)
    {
        _keepSubfolders = keepSubfolders;
        _overwrite = overwrite;
    }

    /// <summary>Remembers a path taken in an earlier run, so nothing else gets it.</summary>
    public void Taken(string path)
    {
        var full = Path.GetFullPath(path);
        _files.Add(full);
        for (var folder = Path.GetDirectoryName(full); folder != null; folder = Path.GetDirectoryName(folder))
            _folders.Add(folder);
    }

    /// <summary>The path for a new file, which then counts as taken.</summary>
    /// <param name="root">The folder that everything goes under.</param>
    /// <param name="name">What the server calls the file, or what the address does.</param>
    /// <param name="isPage">HTML: a query makes a new page, and the name has to open from disk.</param>
    public string Place(string root, Uri address, string name, bool isPage)
    {
        var top = Path.GetFullPath(root);
        var folder = top;
        if (_keepSubfolders)
        {
            folder = IntoFolder(folder, SafeName(address.IsDefaultPort ? address.Host : address.Host + "_" + address.Port, MaxFolderName));
            var segments = address.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            // The last segment is the file, unless the address ends as a folder does.
            int folders = address.AbsolutePath.EndsWith('/') ? segments.Length : segments.Length - 1;
            for (int i = 0; i < folders; i++)
                folder = IntoFolder(folder, SafeName(Uri.UnescapeDataString(segments[i]), MaxFolderName));
        }

        var file = FileNameFor(address, name, isPage);
        var path = Numbered(folder, file);
        var full = Path.GetFullPath(path);
        // Everything above is meant to make this impossible; if it ever isn't, the file still stays in its folder.
        if (!full.StartsWith(Path.TrimEndingDirectorySeparator(top) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            full = Numbered(top, Hash(address.AbsoluteUri) + Path.GetExtension(file));
        _files.Add(full);
        return full;
    }

    string IntoFolder(string parent, string name)
    {
        var path = Path.Combine(parent, name);
        // A file of that name came first: the folder has to go by another.
        while (_files.Contains(path) || File.Exists(path)) path += "_";
        _folders.Add(path);
        return path;
    }

    string Numbered(string folder, string file)
    {
        var path = Path.Combine(folder, file);
        var stem = Path.GetFileNameWithoutExtension(file);
        var extension = Path.GetExtension(file);
        for (int i = 2; IsTaken(path); i++) path = Path.Combine(folder, $"{stem}_{i}{extension}");
        return path;
    }

    bool IsTaken(string path) =>
        _files.Contains(path) || _folders.Contains(path) || Directory.Exists(path) || (!_overwrite && File.Exists(path));

    static string FileNameFor(Uri address, string name, bool isPage)
    {
        var file = SafeFileName(name);
        var query = address.Query.TrimStart('?');
        if (query.Length == 0)
            return isPage && !IsHtmlName(file) ? SafeFileName(file + ".html") : file;

        // What's after a ? usually makes a different file, or at least a different page.
        var text = SafeName(Uri.UnescapeDataString(query), MaxQueryInName + 1);
        if (text.Length > MaxQueryInName) text = Hash(query);
        if (isPage)
            return SafeFileName(IsHtmlName(file) ? Path.GetFileNameWithoutExtension(file) + "_" + text + Path.GetExtension(file) : file + "_" + text + ".html");
        return SafeFileName(Path.GetFileNameWithoutExtension(file) + "_" + text + Path.GetExtension(file));
    }

    static bool IsHtmlName(string file) =>
        Path.GetExtension(file).Equals(".html", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(file).Equals(".htm", StringComparison.OrdinalIgnoreCase);

    static string SafeFileName(string name)
    {
        var clean = FileNames.Sanitize(name);
        return IsReserved(clean) ? "_" + clean : clean;
    }

    /// <summary>A name that means the same on disk as it looks: no separators, streams, dots or spaces at the end, or devices.</summary>
    static string SafeName(string name, int longest)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => invalid.Contains(c) || c == '\\' || c == '/' || c == ':' ? '_' : c).ToArray()).Trim(' ', '.');
        if (clean.Length > longest) clean = clean[..longest].Trim(' ', '.');
        if (clean.Length == 0) return "_";
        return IsReserved(clean) ? "_" + clean : clean;
    }

    // Windows reads CON.txt, and "CON .txt", as the device.
    static bool IsReserved(string name)
    {
        var stem = name;
        int dot = stem.IndexOf('.');
        if (dot >= 0) stem = stem[..dot];
        return Reserved.Contains(stem.TrimEnd(' '));
    }

    static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)), 0, 8);
}
