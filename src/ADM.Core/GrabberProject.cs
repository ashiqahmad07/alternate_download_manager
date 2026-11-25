using System.Text.Json.Serialization;

namespace ADM.Core;

public enum GrabberKind { Unknown, Page, Style, Script, File }

public enum GrabberState { Waiting, Done, Failed }

/// <summary>Where a project's files go: each by its category, all in one category's folder, or all in one folder.</summary>
public enum GrabberSaveMode { Folder, ByCategory, CategoryFolder }

public enum GrabberFileState { None, Queued, Complete, Failed }

/// <summary>What a project explores and keeps.</summary>
public sealed class GrabberSettings
{
    public string StartUrl { get; set; } = "";

    /// <summary>The login offered when the start page's own host asks for one.</summary>
    public string? UserName { get; set; }
    /// <summary>Encrypted with <see cref="Secrets"/>; never plain text.</summary>
    public string? ProtectedPassword { get; set; }
    /// <summary>A Cookie header for the start page's own origin, encrypted with <see cref="Secrets"/>.</summary>
    public string? ProtectedCookies { get; set; }

    // Which pages to explore.
    public bool WholeSite { get; set; }
    public int SiteLevels { get; set; } = 2;
    public int OtherLevels { get; set; }
    /// <summary>Nothing above the start page's folder is explored.</summary>
    public bool NoParents { get; set; }
    /// <summary>Other hosts in the start page's main domain count as this site.</summary>
    public bool MainDomain { get; set; }
    /// <summary>A list as in <see cref="GrabberPathList"/>; empty explores anywhere.</summary>
    public string ExploreOnly { get; set; } = "";
    public string ExploreNever { get; set; } = "";
    public int ExploreAtOnce { get; set; } = 4;

    // Which files to keep.
    /// <summary>Masks as in <see cref="GrabberMask"/>; empty keeps every name.</summary>
    public string Include { get; set; } = "*";
    public string Exclude { get; set; } = "";
    public bool ThisSiteOnly { get; set; } = true;
    /// <summary>Lists only the first file of a name and size.</summary>
    public bool HideCopies { get; set; }
    public string FilesOnlyIn { get; set; } = "";
    public string FilesNeverIn { get; set; } = "";
    public long? MinSizeKB { get; set; }
    public long? MaxSizeKB { get; set; }
    /// <summary>Files found are checked as they are found, and download at once.</summary>
    public bool StartAtOnce { get; set; }

    // Where to save.
    public GrabberSaveMode SaveMode { get; set; }
    public string Category { get; set; } = Categories.General;
    /// <summary>Empty goes under the download folder, by the project's name.</summary>
    public string Folder { get; set; } = "";
    /// <summary>Files go under their host and the site's own folders. Copies in other folders are then kept.</summary>
    public bool KeepSubfolders { get; set; } = true;
    /// <summary>Links in saved pages are made to work from disk, when downloading ends or is stopped.</summary>
    public bool ConvertLinks { get; set; } = true;
    /// <summary>A file already on disk is replaced; otherwise the new one gets a number.</summary>
    public bool Overwrite { get; set; }
    public int DownloadAtOnce { get; set; } = 4;
    /// <summary>A link's words become the description of its download.</summary>
    public bool LinkTextAsDescription { get; set; } = true;
}

/// <summary>One address a crawl has found.</summary>
public sealed class GrabberEntry
{
    public string Url { get; set; } = "";
    /// <summary>Where its redirects ended; null while it is the same.</summary>
    public string? FinalUrl { get; set; }
    /// <summary>The page that links to it.</summary>
    public string? Referrer { get; set; }
    public string? LinkText { get; set; }
    /// <summary>The fewest links from the start page on this site.</summary>
    public int SiteHops { get; set; }
    /// <summary>The fewest links away from this site; 0 while on it.</summary>
    public int OtherHops { get; set; }
    public GrabberKind Kind { get; set; }
    public GrabberState State { get; set; }
    public string? Error { get; set; }

    /// <summary>It passes the file filters, so it is in the project's list.</summary>
    public bool Matched { get; set; }
    public string? FileName { get; set; }
    /// <summary>The media type of the answer, with no parameters.</summary>
    public string? ContentType { get; set; }
    public string? Charset { get; set; }
    public long Size { get; set; } = -1;
    public string? ETag { get; set; }
    public string? LastModified { get; set; }

    /// <summary>The user wants this one saved.</summary>
    public bool Checked { get; set; }
    public GrabberFileState FileState { get; set; }
    /// <summary>Where it's saved, or is being.</summary>
    public string? LocalPath { get; set; }

    /// <summary>The pass of the project's crawl that last asked the server about it; see <see cref="GrabberProject.Pass"/>.</summary>
    public int Pass { get; set; }
    /// <summary>The server's copy changed since it was saved, so it is saved again, in the same place.</summary>
    public bool Changed { get; set; }

    /// <summary>Its text has been read for links.</summary>
    public bool Read { get; set; }
    /// <summary>Its body is in the project's folder; see <see cref="GrabberStore.ReadBody"/>.</summary>
    public bool Kept { get; set; }
    public string? BodyHash { get; set; }
}

/// <summary>A site grabber project, saved in its own file, as big sites make long lists.</summary>
public sealed class GrabberProject
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public GrabberSettings Settings { get; set; } = new();
    /// <summary>What its schedule starts, or starting its queue does when nothing else is said.</summary>
    public GrabberRunMode ScheduledRun { get; set; } = GrabberRunMode.ExploreAndDownload;
    /// <summary>
    /// Each crawl that goes all the way through is a pass. The next one explores again from the start page, and asks
    /// once more about each address it reaches; one stopped partway carries on with the pass it was in.
    /// </summary>
    public int Pass { get; set; }
    /// <summary>The crawl of this pass went all the way through.</summary>
    public bool Explored { get; set; }
    /// <summary>
    /// The kind of run that was stopped, or cut off, before it finished; null once one finishes. Starting that kind
    /// again carries it on, without exploring a site whose crawl went all the way through.
    /// </summary>
    public GrabberRunMode? Unfinished { get; set; }
    /// <summary>The start page's host, and where its redirects ended.</summary>
    public List<string> SiteHosts { get; set; } = new();
    public List<GrabberEntry> Entries { get; set; } = new();

    /// <summary>Guards <see cref="Entries"/> and what is in them while a crawl runs.</summary>
    [JsonIgnore] public object SyncRoot { get; } = new();
}

/// <param name="PagesRead">Pages, stylesheets and scripts read for links.</param>
/// <param name="FilesFound">Addresses in the list.</param>
/// <param name="Waiting">Addresses still to look at.</param>
public readonly record struct GrabberCounts(int PagesRead, int FilesFound, int Failed, int Waiting);
