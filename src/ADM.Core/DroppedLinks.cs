namespace ADM.Core;

/// <summary>What a file dropped on the main window holds, by its extension.</summary>
public enum DroppedFileKind { Links, Shortcut, ExportFile, Other }

/// <summary>The links a drop on the main window, or Ctrl+V in it, brings: from text, a browser's link, or files.</summary>
public static class DroppedLinks
{
    /// <summary>
    /// A cURL command whole, for the Add dialog to read as it reads one pasted there; otherwise each link once, in
    /// order. A browser's link format ends in a null, which is no part of the address.
    /// </summary>
    public static List<string> FromText(string text)
    {
        var trimmed = text.Replace('\0', '\n').Trim();
        return CurlCommand.IsCommand(trimmed) ? new() { trimmed } : Links.Find(trimmed);
    }

    /// <summary>The kinds of file the Tasks menu imports, and Windows' Internet shortcuts; anything else holds no links.</summary>
    public static DroppedFileKind KindOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".txt" or ".htm" or ".html" or ".csv" => DroppedFileKind.Links,
        ".url" => DroppedFileKind.Shortcut,
        ".ef2" or ".ief" => DroppedFileKind.ExportFile,
        _ => DroppedFileKind.Other,
    };

    /// <summary>The links in a file of the <see cref="DroppedFileKind.Links"/> or <see cref="DroppedFileKind.Shortcut"/> kind.</summary>
    public static List<string> FromFile(string path, string contents)
    {
        if (KindOf(path) != DroppedFileKind.Shortcut) return Links.Find(contents);
        // An Internet shortcut is an INI file; its address is the URL line.
        foreach (var line in contents.Split('\n'))
        {
            var entry = line.Trim();
            if (entry.StartsWith("URL=", StringComparison.OrdinalIgnoreCase)
                && Uri.TryCreate(entry[4..], UriKind.Absolute, out var uri) && Links.IsDownloadable(uri))
                return new() { entry[4..] };
        }
        return new();
    }
}
