namespace ADM.Core;

/// <summary>Where an address stands, as the grabber's list shows it.</summary>
public enum GrabberEntryStatus
{
    /// <summary>Still to be looked at by a crawl.</summary>
    Waiting,
    /// <summary>A crawl couldn't reach it; see <see cref="GrabberEntry.Error"/>.</summary>
    NotFound,
    /// <summary>In the list, and not saved.</summary>
    Listed,
    /// <summary>Waiting its turn to be saved, or being saved.</summary>
    Queued,
    Saved,
    /// <summary>Saving it failed; see <see cref="GrabberEntry.Error"/>.</summary>
    NotSaved,
    /// <summary>Saved once, and the server's copy has changed since.</summary>
    Changed,
}

/// <summary>A branch of the grabber's tree: all the files, the files by the page that links to them, or by folder.</summary>
/// <param name="Key">What <see cref="GrabberListing.Filter"/> takes to show this branch's files.</param>
public sealed record GrabberNode(string Label, string Key, int Count, IReadOnlyList<GrabberNode> Children);

/// <summary>Counts for the statistics window.</summary>
public readonly record struct GrabberStatistics(
    int PagesRead, int Listed, int Checked, int Saved, long SavedBytes, int Waiting, int NotFound, int NotSaved, int Changed);

/// <summary>The grabber's list, as its window shows it: the tree to choose files by, each file's status, and the counts.</summary>
public static class GrabberListing
{
    public const string AllKey = "all";
    public const string LinkPrefix = "link:";
    public const string FolderPrefix = "folder:";

    public static GrabberEntryStatus StatusOf(GrabberEntry e) => e switch
    {
        { Changed: true, FileState: GrabberFileState.None } => GrabberEntryStatus.Changed,
        { FileState: GrabberFileState.Complete } => GrabberEntryStatus.Saved,
        { FileState: GrabberFileState.Queued } => GrabberEntryStatus.Queued,
        { FileState: GrabberFileState.Failed } => GrabberEntryStatus.NotSaved,
        { State: GrabberState.Failed } => GrabberEntryStatus.NotFound,
        { State: GrabberState.Waiting } => GrabberEntryStatus.Waiting,
        _ => GrabberEntryStatus.Listed,
    };

    /// <summary>The page an entry was found on, or none for the start page.</summary>
    static string LinkOf(GrabberEntry e) => e.Referrer ?? "";

    /// <summary>The folder an address is in, as host and path, without its file name.</summary>
    public static string FolderOf(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var address)) return "";
        var path = address.AbsolutePath;
        return address.Authority + path[..(path.LastIndexOf('/') + 1)];
    }

    /// <summary>
    /// The tree for the files in the list: all of them; each page that links to some, with how many; and the folders
    /// they're in, host first, a level for each folder. One pass over the entries, however many there are.
    /// </summary>
    public static GrabberNode Tree(IReadOnlyCollection<GrabberEntry> entries, string allLabel, string byLinkLabel, string byFolderLabel)
    {
        var files = entries.Where(e => e.Matched).ToList();
        var byLink = files.GroupBy(LinkOf, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new GrabberNode(g.Key.Length == 0 ? "/" : g.Key, LinkPrefix + g.Key, g.Count(), []))
            .ToList();

        // Each file counts in its folder and in every folder above it.
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var folder = FolderOf(file.Url);
            for (int end = folder.IndexOf('/'); end >= 0; end = folder.IndexOf('/', end + 1))
            {
                var above = folder[..(end + 1)];
                counts[above] = counts.GetValueOrDefault(above) + 1;
            }
        }
        // Each folder under the one above it: "host/a/b/" under "host/a/", and a host under nothing.
        var below = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var folder in counts.Keys)
        {
            var parent = folder.Length < 2 ? "" : folder[..(folder.LastIndexOf('/', folder.Length - 2) + 1)];
            if (!below.TryGetValue(parent, out var list)) below[parent] = list = new List<string>();
            list.Add(folder);
        }

        List<GrabberNode> Children(string parent) =>
            (below.GetValueOrDefault(parent) ?? [])
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .Select(f => new GrabberNode(f[parent.Length..].TrimEnd('/'), FolderPrefix + f, counts[f], Children(f)))
                .ToList();

        return new GrabberNode(allLabel, AllKey, files.Count,
        [
            new GrabberNode(byLinkLabel, LinkPrefix, files.Count, byLink),
            new GrabberNode(byFolderLabel, FolderPrefix, files.Count, Children("")),
        ]);
    }

    /// <summary>Which entries a branch of the tree shows: only those in the list, and of those, the branch's.</summary>
    public static Func<GrabberEntry, bool> Filter(string key)
    {
        if (key.StartsWith(LinkPrefix, StringComparison.Ordinal) && key.Length > LinkPrefix.Length)
        {
            var page = key[LinkPrefix.Length..];
            return e => e.Matched && LinkOf(e) == page;
        }
        if (key.StartsWith(FolderPrefix, StringComparison.Ordinal) && key.Length > FolderPrefix.Length)
        {
            var folder = key[FolderPrefix.Length..];
            return e => e.Matched && FolderOf(e.Url).StartsWith(folder, StringComparison.Ordinal);
        }
        return e => e.Matched;
    }

    /// <summary>The counts a project's statistics show; takes the project's lock.</summary>
    public static GrabberStatistics Statistics(GrabberProject project)
    {
        int read = 0, listed = 0, checkedCount = 0, saved = 0, waiting = 0, notFound = 0, notSaved = 0, changed = 0;
        long bytes = 0;
        lock (project.SyncRoot)
        {
            foreach (var e in project.Entries)
            {
                if (e.Read) read++;
                if (e.State == GrabberState.Waiting) waiting++;
                else if (e.State == GrabberState.Failed) notFound++;
                if (!e.Matched) continue;
                listed++;
                if (e.Checked) checkedCount++;
                switch (StatusOf(e))
                {
                    case GrabberEntryStatus.Saved:
                        saved++;
                        if (e.Size > 0) bytes += e.Size;
                        break;
                    case GrabberEntryStatus.NotSaved:
                        notSaved++;
                        break;
                    case GrabberEntryStatus.Changed:
                        changed++;
                        break;
                }
            }
        }
        return new GrabberStatistics(read, listed, checkedCount, saved, bytes, waiting, notFound, notSaved, changed);
    }
}
