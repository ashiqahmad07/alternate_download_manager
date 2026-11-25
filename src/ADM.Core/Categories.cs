namespace ADM.Core;

public static class Categories
{
    public const string General = "General";

    public static List<Category> Defaults() => new()
    {
        New("Compressed", "zip rar 7z gz tgz tar bz2 xz zst iso cab"),
        New("Documents", "pdf doc docx xls xlsx ppt pptx txt rtf odt ods odp epub csv"),
        New("Music", "mp3 wav flac aac m4a ogg oga wma opus aif aiff"),
        New("Programs", "exe msi msix msixbundle appx apk dmg deb rpm appimage"),
        New("Video", "mp4 mkv avi mov webm flv wmv m4v ts mts 3gp mpg mpeg"),
        new Category { Name = General, Folder = "" },
    };

    static Category New(string name, string extensions) => new()
    {
        Name = name,
        Folder = name,
        Extensions = extensions.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList(),
    };

    public static string Resolve(string fileNameOrUrl, IEnumerable<Category> categories)
    {
        var path = fileNameOrUrl;
        if (Uri.TryCreate(fileNameOrUrl, UriKind.Absolute, out var uri) && Links.IsDownloadable(uri))
            path = uri.AbsolutePath;
        var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        if (ext.Length == 0) return General;
        foreach (var c in categories)
            if (c.Extensions.Any(e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase)))
                return c.Name;
        return General;
    }

    public static string FolderFor(string category, AppState state)
    {
        var folder = state.Categories.FirstOrDefault(c => c.Name == category)?.Folder ?? "";
        return Path.IsPathFullyQualified(folder) ? folder : Path.Combine(state.Settings.DownloadRoot, folder);
    }

    /// <summary>
    /// A new category, before <see cref="General"/>, which stays last as the one for everything else. Its folder is
    /// its name under the download root. A name already there gives that category, and nothing is made.
    /// </summary>
    public static Category Add(string name, AppState state)
    {
        name = name.Trim();
        var found = state.Categories.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        if (found != null) return found;

        var made = new Category { Name = name, Folder = FileNames.Sanitize(name) };
        var categories = state.Categories.ToList();
        var general = categories.FindIndex(c => c.Name == General);
        categories.Insert(general < 0 ? categories.Count : general, made);
        // A new list, as Options gives one: a download being added elsewhere may be reading the old one.
        state.Categories = categories;
        return made;
    }

    /// <summary>
    /// Makes <paramref name="folder"/> the category's own. One under the download root is kept as a folder under it,
    /// so it moves with the root, as the ones ADM starts with do.
    /// </summary>
    public static void SetFolder(string category, string folder, AppState state)
    {
        var entry = state.Categories.FirstOrDefault(c => c.Name == category);
        if (entry == null) return;
        var relative = Path.GetRelativePath(state.Settings.DownloadRoot, folder);
        var outside = Path.IsPathFullyQualified(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar);
        entry.Folder = outside ? folder : relative == "." ? "" : relative;
    }
}
