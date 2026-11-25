using System.Text.Json.Serialization;

namespace ADM.Core;

/// <summary>A named list of file masks, as the step that chooses files offers them; see <see cref="GrabberMask"/>.</summary>
public sealed class GrabberFilter
{
    public string Name { get; set; } = "";
    public string Mask { get; set; } = "";

    // The picture, video and sound names a browser shows or plays, and the files pages are made of.
    internal const string Pictures = "*.jpg,*.jpeg,*.jpe,*.jfif,*.png,*.gif,*.webp,*.avif,*.bmp,*.svg,*.ico,*.tif,*.tiff,*.heic";
    internal const string Video = "*.mp4,*.m4v,*.mkv,*.webm,*.avi,*.mov,*.wmv,*.flv,*.mpg,*.mpeg,*.3gp,*.ogv,*.ts";
    internal const string Audio = "*.mp3,*.m4a,*.aac,*.ogg,*.oga,*.opus,*.wav,*.flac,*.wma,*.aif,*.aiff,*.mid,*.midi";
    internal const string Pages = "*.html,*.htm,*.xhtml,*.xht,*.shtml,*.php,*.asp,*.aspx,*.jsp,*.cgi,*.css,*.js,*.mjs";
    internal const string PageParts = "*.css,*.js,*.mjs,*.woff,*.woff2,*.ttf,*.otf,*.eot";

    /// <summary>The filters ADM comes with; the user may change them, and they are saved in the settings.</summary>
    public static List<GrabberFilter> Defaults() =>
    [
        new() { Name = "Pictures", Mask = Pictures },
        new() { Name = "Video", Mask = Video },
        new() { Name = "Audio", Mask = Audio },
        new() { Name = "PDF documents", Mask = "*.pdf" },
        new() { Name = "Programs", Mask = "*.exe,*.msi,*.msix,*.msixbundle,*.appx,*.apk,*.dmg,*.deb,*.rpm,*.appimage" },
        new() { Name = "Archives", Mask = "*.zip,*.rar,*.7z,*.gz,*.tgz,*.tar,*.bz2,*.xz,*.zst,*.iso,*.cab" },
        new() { Name = "All files", Mask = "*.*" },
        new() { Name = "The start page complete", Mask = "<start page>," + Pictures + "," + PageParts },
    ];
}

/// <summary>
/// The settings a project starts from: which pages to explore, which files to keep, and whether links are converted.
/// Where to save, the start page and the login are the project's own, and a template never sets them.
/// </summary>
public sealed class GrabberTemplate
{
    public string Name { get; set; } = "";
    public bool WholeSite { get; set; }
    public int SiteLevels { get; set; } = 2;
    public int OtherLevels { get; set; }
    public bool NoParents { get; set; }
    public bool MainDomain { get; set; }
    public string ExploreOnly { get; set; } = "";
    public string ExploreNever { get; set; } = "";
    public string Include { get; set; } = "*";
    public string Exclude { get; set; } = "";
    public bool ThisSiteOnly { get; set; } = true;
    public bool HideCopies { get; set; }
    public string FilesOnlyIn { get; set; } = "";
    public string FilesNeverIn { get; set; } = "";
    public long? MinSizeKB { get; set; }
    public long? MaxSizeKB { get; set; }
    public bool ConvertLinks { get; set; } = true;

    /// <summary>One of ADM's own, which can't be changed or deleted.</summary>
    [JsonIgnore] public bool BuiltIn { get; init; }

    /// <summary>The part of a project's settings a template holds.</summary>
    public static GrabberTemplate From(string name, GrabberSettings s) => new()
    {
        Name = name,
        WholeSite = s.WholeSite,
        SiteLevels = s.SiteLevels,
        OtherLevels = s.OtherLevels,
        NoParents = s.NoParents,
        MainDomain = s.MainDomain,
        ExploreOnly = s.ExploreOnly,
        ExploreNever = s.ExploreNever,
        Include = s.Include,
        Exclude = s.Exclude,
        ThisSiteOnly = s.ThisSiteOnly,
        HideCopies = s.HideCopies,
        FilesOnlyIn = s.FilesOnlyIn,
        FilesNeverIn = s.FilesNeverIn,
        MinSizeKB = s.MinSizeKB,
        MaxSizeKB = s.MaxSizeKB,
        ConvertLinks = s.ConvertLinks,
    };
}

public enum TemplateSaved { Saved, Replaced, NameTaken, BuiltInName, NoName }

/// <summary>The grabber's templates: ADM's own, then the user's, which are kept in <see cref="Settings.GrabberTemplates"/>.</summary>
public static class GrabberTemplates
{
    public const string Custom = "Custom";

    /// <summary>ADM's own, in the order the first step lists them.</summary>
    public static IReadOnlyList<GrabberTemplate> BuiltIn { get; } =
    [
        // The settings as a new project has them, for the user to set.
        new() { Name = Custom, BuiltIn = true },
        new()
        {
            Name = "All pictures of a site", BuiltIn = true,
            WholeSite = true, Include = GrabberFilter.Pictures, ThisSiteOnly = false, ConvertLinks = false,
        },
        new()
        {
            Name = "All pictures on a page", BuiltIn = true,
            SiteLevels = 0, Include = GrabberFilter.Pictures, ThisSiteOnly = false, ConvertLinks = false,
        },
        new()
        {
            Name = "Audio from a page and the pages it links to", BuiltIn = true,
            SiteLevels = 1, OtherLevels = 1, Include = GrabberFilter.Audio, ThisSiteOnly = false, ConvertLinks = false,
        },
        new()
        {
            Name = "All video of a site", BuiltIn = true,
            WholeSite = true, Include = GrabberFilter.Video, ThisSiteOnly = false, ConvertLinks = false,
        },
        new()
        {
            Name = "Every file of a site but its pages and pictures", BuiltIn = true,
            WholeSite = true, Include = "*", Exclude = GrabberFilter.Pages + "," + GrabberFilter.Pictures, ConvertLinks = false,
        },
        new()
        {
            Name = "A whole site", BuiltIn = true,
            WholeSite = true, Include = "*",
        },
        new()
        {
            Name = "A page complete, to read offline", BuiltIn = true,
            SiteLevels = 0, Include = "<start page>," + GrabberFilter.Pictures + "," + GrabberFilter.PageParts, ThisSiteOnly = false,
        },
    ];

    /// <summary>ADM's own and then the user's.</summary>
    public static IEnumerable<GrabberTemplate> All(Settings settings) => BuiltIn.Concat(settings.GrabberTemplates);

    public static GrabberTemplate? Find(Settings settings, string name) =>
        All(settings).FirstOrDefault(t => string.Equals(t.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Sets what a template holds, and nothing else: the start page, where to save and the login stay. A template that
    /// saves no pages converts no links, as there would be nothing to convert them in.
    /// </summary>
    public static void Apply(GrabberTemplate t, GrabberSettings s)
    {
        s.WholeSite = t.WholeSite;
        s.SiteLevels = t.SiteLevels;
        s.OtherLevels = t.OtherLevels;
        s.NoParents = t.NoParents;
        s.MainDomain = t.MainDomain;
        s.ExploreOnly = t.ExploreOnly;
        s.ExploreNever = t.ExploreNever;
        s.Include = t.Include;
        s.Exclude = t.Exclude;
        s.ThisSiteOnly = t.ThisSiteOnly;
        s.HideCopies = t.HideCopies;
        s.FilesOnlyIn = t.FilesOnlyIn;
        s.FilesNeverIn = t.FilesNeverIn;
        s.MinSizeKB = t.MinSizeKB;
        s.MaxSizeKB = t.MaxSizeKB;
        s.ConvertLinks = t.ConvertLinks;
    }

    /// <summary>
    /// Keeps a project's settings as the user's own template. ADM's own names are refused, as is a name the user has
    /// used, unless <paramref name="replace"/> says to replace that one.
    /// </summary>
    public static TemplateSaved Save(Settings settings, string name, GrabberSettings from, bool replace = false)
    {
        name = name.Trim();
        if (name.Length == 0) return TemplateSaved.NoName;
        if (BuiltIn.Any(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))) return TemplateSaved.BuiltInName;
        var template = GrabberTemplate.From(name, from);
        int at = settings.GrabberTemplates.FindIndex(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
        if (at < 0)
        {
            settings.GrabberTemplates.Add(template);
            return TemplateSaved.Saved;
        }
        if (!replace) return TemplateSaved.NameTaken;
        settings.GrabberTemplates[at] = template;
        return TemplateSaved.Replaced;
    }

    /// <summary>Deletes one of the user's own; ADM's own stay.</summary>
    public static bool Delete(Settings settings, string name) =>
        settings.GrabberTemplates.RemoveAll(t => string.Equals(t.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)) > 0;
}
