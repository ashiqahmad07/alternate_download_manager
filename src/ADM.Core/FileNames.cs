using System.Net.Http.Headers;
using System.Text.RegularExpressions;

namespace ADM.Core;

public static class FileNames
{
    static readonly Dictionary<string, string> ExtensionByMime = new(StringComparer.OrdinalIgnoreCase)
    {
        ["video/mp4"] = ".mp4",
        ["video/webm"] = ".webm",
        ["video/x-matroska"] = ".mkv",
        ["video/mp2t"] = ".ts",
        ["audio/mpeg"] = ".mp3",
        ["audio/mp4"] = ".m4a",
        ["audio/ogg"] = ".ogg",
        ["audio/webm"] = ".webm",
        ["application/pdf"] = ".pdf",
        ["application/zip"] = ".zip",
        ["application/x-msdownload"] = ".exe",
    };

    /// <summary>The extension for a media type; null when it has none listed here.</summary>
    internal static string? ExtensionFor(string mediaType) => ExtensionByMime.GetValueOrDefault(mediaType);

    public static string FromResponse(ContentDispositionHeaderValue? disposition, string url, string? mediaType)
    {
        var name = disposition?.FileNameStar ?? disposition?.FileName;
        name = name?.Trim('"', ' ');
        if (string.IsNullOrWhiteSpace(name) && Uri.TryCreate(url, UriKind.Absolute, out var uri))
            name = Uri.UnescapeDataString(uri.AbsolutePath.Split('/').LastOrDefault() ?? "");
        if (string.IsNullOrWhiteSpace(name)) name = "download";
        if (!Path.HasExtension(name) && mediaType != null && ExtensionByMime.TryGetValue(mediaType, out var ext))
            name += ext;
        return Sanitize(name);
    }

    public static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim(' ', '.');
        if (clean.Length > 180) clean = clean[..(180 - Path.GetExtension(clean).Length)] + Path.GetExtension(clean);
        return clean.Length == 0 ? "download" : clean;
    }

    /// <summary>Returns <paramref name="path"/> or "name (n).ext" when the file already exists.</summary>
    /// <summary>A name without the number <see cref="Unique"/> gave it: name (1).ext becomes name.ext.</summary>
    public static string WithoutCopyNumber(string name)
    {
        var numbered = Regex.Match(Path.GetFileNameWithoutExtension(name), @"^(.+) \(\d+\)$");
        return numbered.Success ? numbered.Groups[1].Value + Path.GetExtension(name) : name;
    }

    public static string Unique(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (int i = 1; ; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }
}
