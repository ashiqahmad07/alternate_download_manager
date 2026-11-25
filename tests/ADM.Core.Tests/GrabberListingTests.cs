using ADM.Core;

namespace ADM.Core.Tests;

public sealed class GrabberListingTests : IDisposable
{
    const string Site = "http://www.site.test";

    readonly string _dir = Path.Combine(Path.GetTempPath(), "adm-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    static GrabberEntry File(string path, string? from = null) =>
        new() { Url = Site + path, Referrer = from == null ? null : Site + from, Matched = true, State = GrabberState.Done };

    static List<GrabberEntry> Entries() =>
    [
        File("/", null),
        File("/a/1.zip", "/"),
        File("/a/2.zip", "/"),
        File("/a/b/3.zip", "/page.html"),
        File("/4.zip", "/page.html"),
        new GrabberEntry { Url = Site + "/page.html", Referrer = Site + "/", State = GrabberState.Done },
        new GrabberEntry { Url = "http://cdn.test/x/5.png", Referrer = Site + "/", Matched = true, State = GrabberState.Done },
    ];

    static string Shape(GrabberNode node, string indent = "") =>
        indent + $"{node.Label} {node.Count}\n" + string.Concat(node.Children.Select(c => Shape(c, indent + "  ")));

    [Fact]
    public void Tree_has_all_files_by_link_and_by_folder()
    {
        var tree = GrabberListing.Tree(Entries(), "All", "By link", "By folder");
        Assert.Equal(
            """
            All 6
              By link 6
                / 1
                http://www.site.test/ 3
                http://www.site.test/page.html 2
              By folder 6
                cdn.test 1
                  x 1
                www.site.test 5
                  a 3
                    b 1

            """.Replace("\r\n", "\n"), Shape(tree));
    }

    [Theory]
    [InlineData(GrabberListing.AllKey, "/,/a/1.zip,/a/2.zip,/a/b/3.zip,/4.zip,cdn")]
    [InlineData(GrabberListing.LinkPrefix + Site + "/page.html", "/a/b/3.zip,/4.zip")]
    [InlineData(GrabberListing.LinkPrefix, "/,/a/1.zip,/a/2.zip,/a/b/3.zip,/4.zip,cdn")]
    [InlineData(GrabberListing.FolderPrefix + "www.site.test/a/", "/a/1.zip,/a/2.zip,/a/b/3.zip")]
    [InlineData(GrabberListing.FolderPrefix + "www.site.test/a/b/", "/a/b/3.zip")]
    [InlineData(GrabberListing.FolderPrefix + "cdn.test/", "cdn")]
    public void Each_branch_shows_its_files(string key, string expected)
    {
        var shown = Entries().Where(GrabberListing.Filter(key)).Select(e => e.Url.StartsWith(Site) ? e.Url[Site.Length..] : "cdn");
        Assert.Equal(expected.Split(','), shown);
    }

    [Fact]
    public void A_big_list_makes_its_tree_quickly()
    {
        var entries = Enumerable.Range(0, 100_000)
            .Select(i => File($"/{i % 1000}/{i / 1000 % 10}/{i}.zip", $"/{i % 500}.html"))
            .ToList();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var tree = GrabberListing.Tree(entries, "All", "By link", "By folder");
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"took {clock.Elapsed}");
        Assert.Equal(100_000, tree.Count);
        Assert.Equal(500, tree.Children[0].Children.Count);
        Assert.Equal(1000, tree.Children[1].Children.Single().Children.Count);
    }

    [Theory]
    [InlineData(GrabberState.Waiting, GrabberFileState.None, false, GrabberEntryStatus.Waiting)]
    [InlineData(GrabberState.Failed, GrabberFileState.None, false, GrabberEntryStatus.NotFound)]
    [InlineData(GrabberState.Done, GrabberFileState.None, false, GrabberEntryStatus.Listed)]
    [InlineData(GrabberState.Done, GrabberFileState.Queued, false, GrabberEntryStatus.Queued)]
    [InlineData(GrabberState.Done, GrabberFileState.Complete, false, GrabberEntryStatus.Saved)]
    [InlineData(GrabberState.Failed, GrabberFileState.Complete, false, GrabberEntryStatus.Saved)]
    [InlineData(GrabberState.Done, GrabberFileState.Failed, false, GrabberEntryStatus.NotSaved)]
    [InlineData(GrabberState.Done, GrabberFileState.None, true, GrabberEntryStatus.Changed)]
    [InlineData(GrabberState.Done, GrabberFileState.Queued, true, GrabberEntryStatus.Queued)]
    public void Status_says_where_an_entry_stands(GrabberState state, GrabberFileState file, bool changed, GrabberEntryStatus expected) =>
        Assert.Equal(expected, GrabberListing.StatusOf(new GrabberEntry { State = state, FileState = file, Changed = changed }));

    [Fact]
    public void Statistics_count_what_the_list_holds()
    {
        var project = new GrabberProject();
        project.Entries.AddRange(
        [
            new GrabberEntry { Url = "1", Read = true, State = GrabberState.Done, Kind = GrabberKind.Page },
            new GrabberEntry { Url = "2", Matched = true, Checked = true, State = GrabberState.Done, FileState = GrabberFileState.Complete, Size = 1000 },
            new GrabberEntry { Url = "3", Matched = true, Checked = true, State = GrabberState.Done, FileState = GrabberFileState.Complete, Size = -1 },
            new GrabberEntry { Url = "4", Matched = true, Checked = true, State = GrabberState.Done, FileState = GrabberFileState.Failed },
            new GrabberEntry { Url = "5", Matched = true, Checked = true, State = GrabberState.Done, Changed = true },
            new GrabberEntry { Url = "6", Matched = true, State = GrabberState.Waiting },
            new GrabberEntry { Url = "7", State = GrabberState.Failed },
        ]);
        Assert.Equal(new GrabberStatistics(PagesRead: 1, Listed: 5, Checked: 4, Saved: 2, SavedBytes: 1000, Waiting: 1, NotFound: 1, NotSaved: 1, Changed: 1),
            GrabberListing.Statistics(project));
    }

    DownloadManager NewManager() => new(Path.Combine(_dir, "state.json"), new LockedContext());

    [Fact]
    public void Add_to_the_main_list_adds_ordinary_downloads()
    {
        var manager = NewManager();
        var projects = new GrabberProjects(manager);
        var project = projects.Create("site", new GrabberSettings
        {
            StartUrl = Site + "/",
            UserName = "user",
            ProtectedPassword = Secrets.Protect("pass"),
            ProtectedCookies = Secrets.Protect("a=1"),
            LinkTextAsDescription = true,
        });
        var mine = File("/a.zip", "/");
        mine.LinkText = "The file";
        mine.FileName = "a.zip";
        var theirs = new GrabberEntry { Url = "http://other.test/b.zip", Referrer = Site + "/", Matched = true };
        var already = File("/c.zip", "/");
        manager.Add(new DownloadRequest { Url = already.Url }, startNow: false);

        Assert.Equal(2, projects.AddToMainList(project, [mine, theirs, already], null));
        var a = manager.Items.Single(i => i.Url == mine.Url);
        Assert.Equal((null, DownloadQueue.MainId, "The file", "a.zip", Site + "/"), (a.GrabberProjectId, a.QueueId, a.Description, a.FileName, a.Referrer));
        Assert.Equal(("user", "pass", "a=1"), (a.UserName, Secrets.Unprotect(a.ProtectedPassword), a.Cookies));
        var b = manager.Items.Single(i => i.Url == theirs.Url);
        Assert.Equal((null, null, null), (b.UserName, b.ProtectedPassword, b.Cookies));
        Assert.Single(manager.Items, i => i.Url == already.Url);
    }

    [Fact]
    public void Add_to_the_main_list_refuses_a_whole_big_site()
    {
        var manager = NewManager();
        var projects = new GrabberProjects(manager);
        var project = projects.Create("site", new GrabberSettings { StartUrl = Site + "/" });
        var many = Enumerable.Range(0, GrabberProjects.MaxAddToMainList + 1).Select(i => File($"/{i}.zip")).ToList();
        Assert.Equal(-1, projects.AddToMainList(project, many, null));
        Assert.Empty(manager.Items);
        Assert.Equal(GrabberProjects.MaxAddToMainList, projects.AddToMainList(project, many.Skip(1).ToList(), null));
    }
}
