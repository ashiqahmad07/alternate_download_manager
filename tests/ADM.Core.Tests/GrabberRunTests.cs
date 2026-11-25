using System.Text;
using System.Text.RegularExpressions;
using ADM.Core;

namespace ADM.Core.Tests;

public sealed class GrabberRunTests : IDisposable
{
    const string Site = "http://www.site.test";

    readonly string _dir = Path.Combine(Path.GetTempPath(), "adm-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        for (int i = 0; i < 5; i++)
        {
            try
            {
                Directory.Delete(_dir, true);
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(100);
            }
        }
    }

    string StatePath => Path.Combine(_dir, "state.json");
    string ProjectFolder => Path.Combine(_dir, "grabber", "p");
    string SaveFolder => Path.Combine(_dir, "saved");

    string Saved(params string[] parts) => Path.Combine(new[] { SaveFolder, "www.site.test" }.Concat(parts).ToArray());

    sealed class Harness
    {
        public required DownloadManager Manager;
        public required LockedContext Ui;
        public required GrabberRun Run;
        public required DownloadQueue Queue;
        public required GrabberProject Project;

        public void Start() => Ui.Run(() => Manager.StartQueue(Queue));
        public void Stop() => Ui.Run(() => Manager.StopQueue(Queue));

        public async Task Finished()
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(120);
            while (true)
            {
                bool running = false;
                Ui.Run(() =>
                {
                    Manager.Tick(DateTime.Now);
                    running = Queue.IsRunning;
                });
                if (!running) return;
                if (DateTime.UtcNow > deadline) throw new TimeoutException();
                await Task.Delay(20);
            }
        }

        public async Task Until(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
            while (true)
            {
                bool done = false;
                Ui.Run(() => done = condition());
                if (done) return;
                if (DateTime.UtcNow > deadline) throw new TimeoutException();
                await Task.Delay(10);
            }
        }
    }

    Harness New(SiteTestServer site, string start = Site + "/", Action<GrabberSettings>? change = null, GrabberProject? project = null, GrabberRunMode mode = GrabberRunMode.ExploreAndDownload, TimeSpan? retryDelay = null)
    {
        var ui = new LockedContext();
        var manager = new DownloadManager(StatePath, ui) { RetryDelay = retryDelay ?? TimeSpan.FromMilliseconds(10) };
        manager.State.Settings.Proxy = site.Proxy;
        manager.State.Settings.DownloadRoot = _dir;
        var queue = manager.Queues.FirstOrDefault(q => q.Name == "grabber");
        if (queue == null)
        {
            queue = new DownloadQueue { Name = "grabber" };
            manager.Queues.Add(queue);
        }
        project ??= new GrabberProject
        {
            Name = "test",
            Settings = new GrabberSettings
            {
                StartUrl = start,
                WholeSite = true,
                Folder = SaveFolder,
                KeepSubfolders = true,
                ConvertLinks = true,
                StartAtOnce = true,
                ThisSiteOnly = true,
            },
        };
        change?.Invoke(project.Settings);
        GrabberRun run = null!;
        ui.Run(() => run = new GrabberRun(manager, project, queue, ProjectFolder) { Mode = mode });
        return new Harness { Manager = manager, Ui = ui, Run = run, Queue = queue, Project = project };
    }

    static void WriteSite(SiteTestServer site)
    {
        site.Page(Site + "/", "<link rel=stylesheet href=/css/main.css><a href=/docs/a.html>a</a><a href=/gone.zip>gone</a><img src=/img/logo.png><a href=/files/big.bin>big</a>");
        site.Page(Site + "/docs/a.html", "<a href=../index-link.html>up</a><a href=/>home</a><img src=/img/logo.png><a href=http://other.test/away.html>away</a>");
        site.Page(Site + "/index-link.html", "<a href=docs/a.html>down</a>");
        site.Put(Site + "/css/main.css", "b{background:url(/img/bg.png)}"u8.ToArray(), "text/css");
        site.File(Site + "/img/logo.png", 3000, "image/png");
        site.File(Site + "/img/bg.png", 4000, "image/png");
        site.File(Site + "/files/big.bin", 2 * 1024 * 1024);
    }

    [Fact]
    public async Task Saves_a_site_with_identical_bytes()
    {
        await using var site = await SiteTestServer.StartAsync();
        WriteSite(site);
        var h = New(site);
        h.Start();
        await h.Finished();

        Assert.Equal(site.Body(Site + "/img/logo.png"), File.ReadAllBytes(Saved("img", "logo.png")));
        Assert.Equal(site.Body(Site + "/img/bg.png"), File.ReadAllBytes(Saved("img", "bg.png")));
        Assert.Equal(site.Body(Site + "/files/big.bin"), File.ReadAllBytes(Saved("files", "big.bin")));
        Assert.Contains("logo.png", File.ReadAllText(Saved("index.html")));
        var entry = h.Project.Entries.Single(e => e.Url == Site + "/img/logo.png");
        Assert.Equal(GrabberFileState.Complete, entry.FileState);
        Assert.Equal(Saved("img", "logo.png"), entry.LocalPath);
        Assert.Empty(h.Manager.Items);
        await h.Manager.ShutdownAsync();
    }

    // Each link in a page: what it says, read as a browser opening the file from disk would.
    static IEnumerable<string> LinksOf(string file) =>
        PageParser.Html(File.ReadAllText(file)).Where(l => l.Kind != PageLinkKind.Base && !l.ScriptText).Select(l => l.Value);

    [Fact]
    public async Task Saved_site_opens_from_disk()
    {
        await using var site = await SiteTestServer.StartAsync();
        WriteSite(site);
        var h = New(site);
        h.Start();
        await h.Finished();

        var pages = Directory.GetFiles(SaveFolder, "*.html", SearchOption.AllDirectories);
        Assert.Equal(3, pages.Length);
        int checkedLinks = 0;
        foreach (var page in pages)
        {
            foreach (var link in LinksOf(page))
            {
                checkedLinks++;
                if (Regex.IsMatch(link, "^[a-z]+://"))
                {
                    Assert.Contains(link, new[] { Site + "/gone.zip", "http://other.test/away.html" });
                    continue;
                }
                var target = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(page)!, Uri.UnescapeDataString(link)));
                Assert.True(File.Exists(target), $"{page} links to {link}");
            }
        }
        Assert.True(checkedLinks > 8, checkedLinks.ToString());
        Assert.DoesNotContain(Site, File.ReadAllText(Saved("docs", "a.html")));
        Assert.Contains("url(../img/bg.png)", File.ReadAllText(Saved("css", "main.css")));
        Assert.Contains("css/main.css", File.ReadAllText(Saved("index.html")));
        await h.Manager.ShutdownAsync();
    }

    [Fact]
    public async Task Big_files_use_several_connections()
    {
        await using var site = await SiteTestServer.StartAsync();
        site.Page(Site + "/", "<a href=/big.bin>big</a>");
        site.File(Site + "/big.bin", 8 * 1024 * 1024).KBps = 4096;
        var h = New(site);
        h.Start();
        await h.Finished();
        Assert.True(site.AskedFor("/big.bin").Select(a => a.Range).Distinct().Count() > 2, "only one connection");
        Assert.Equal(site.Body(Site + "/big.bin"), File.ReadAllBytes(Saved("big.bin")));
        await h.Manager.ShutdownAsync();
    }

    [Fact]
    public async Task Page_is_not_asked_for_twice()
    {
        await using var site = await SiteTestServer.StartAsync();
        WriteSite(site);
        var h = New(site);
        h.Start();
        await h.Finished();
        Assert.Single(site.AskedFor("/docs/a.html"));
        Assert.Single(site.AskedFor("/css/main.css"));
        Assert.Single(site.AskedFor("/"));
        await h.Manager.ShutdownAsync();
    }

    [Fact]
    public async Task Only_checked_files_are_saved()
    {
        await using var site = await SiteTestServer.StartAsync();
        site.Page(Site + "/", "<a href=/a.zip>a</a><a href=/b.zip>b</a><a href=/missing.html>m</a>");
        site.File(Site + "/a.zip");
        site.File(Site + "/b.zip");
        var h = New(site, change: s => { s.StartAtOnce = false; s.Include = "*.zip"; }, mode: GrabberRunMode.ExploreOnly);
        h.Start();
        await h.Finished();
        Assert.False(Directory.Exists(SaveFolder));
        Assert.Equal(2, h.Project.Entries.Count(e => e.Matched));
        Assert.All(h.Project.Entries.Where(e => e.Matched), e => Assert.False(e.Checked));

        h.Project.Entries.Single(e => e.Url == Site + "/b.zip").Checked = true;
        h.Run.Mode = GrabberRunMode.DownloadChecked;
        site.ClearRequests();
        h.Start();
        await h.Finished();
        Assert.True(File.Exists(Saved("b.zip")));
        Assert.False(File.Exists(Saved("a.zip")));
        Assert.Empty(site.AskedFor("/"));
        // A page that failed waits for a run that explores.
        Assert.Empty(site.AskedFor("/missing.html"));
        await h.Manager.ShutdownAsync();
    }

    [Fact]
    public async Task Downloads_at_once_is_the_setting()
    {
        await using var site = await SiteTestServer.StartAsync();
        site.Page(Site + "/", string.Concat(Enumerable.Range(0, 8).Select(i => $"<a href=/{i}.zip>x</a>")));
        foreach (var i in Enumerable.Range(0, 8)) site.File(Site + $"/{i}.zip", 500);
        var h = New(site, change: s => s.DownloadAtOnce = 1);
        int most = 0;
        h.Manager.Items.CollectionChanged += (_, _) => most = Math.Max(most, h.Manager.Items.Count);
        h.Start();
        await h.Finished();
        Assert.Equal(1, most);
        await h.Manager.ShutdownAsync();
    }

    [Fact]
    public async Task Files_waiting_for_a_retry_keep_the_list_small()
    {
        await using var site = await SiteTestServer.StartAsync();
        site.Page(Site + "/", string.Concat(Enumerable.Range(0, 12).Select(i => $"<a href=/{i}.zip>x</a>")));
        foreach (var i in Enumerable.Range(0, 12)) site.File(Site + $"/{i}.zip", 500).GoneAfter = 1;
        var h = New(site, change: s => s.DownloadAtOnce = 2, retryDelay: TimeSpan.FromHours(1));
        h.Start();
        await h.Until(() =>
        {
            h.Manager.Tick(DateTime.Now);
            return h.Project.Entries.Count(e => e.Matched) == 13 && h.Manager.Items.Count > 0 && h.Manager.Items.All(i => !h.Manager.IsRunning(i));
        });
        for (int i = 0; i < 10; i++)
        {
            h.Ui.Run(() => h.Manager.Tick(DateTime.Now));
            await Task.Delay(20);
        }
        h.Ui.Run(() => Assert.Equal(2, h.Manager.Items.Count));
        await h.Manager.ShutdownAsync();
    }

    [Fact]
    public async Task Settings_changed_between_runs_apply()
    {
        await using var site = await SiteTestServer.StartAsync();
        site.Page(Site + "/", "<a href=/d/a.zip>a</a>");
        site.File(Site + "/d/a.zip");
        var h = New(site, mode: GrabberRunMode.ExploreOnly);
        h.Start();
        await h.Finished();

        h.Project.Settings.KeepSubfolders = false;
        foreach (var entry in h.Project.Entries) entry.Checked = entry.Url.EndsWith(".zip");
        h.Run.Mode = GrabberRunMode.DownloadChecked;
        h.Start();
        await h.Finished();
        Assert.True(File.Exists(Path.Combine(SaveFolder, "a.zip")));
        await h.Manager.ShutdownAsync();
    }

    // Three slow files, one at a time: once all are found, one is downloading and two are waiting their turn.
    async Task<(Harness Harness, List<GrabberEntry> Waiting)> OneOfThreeUnderWay(SiteTestServer site)
    {
        site.Page(Site + "/", "<a href=/a.zip>a</a><a href=/b.zip>b</a><a href=/c.zip>c</a>");
        foreach (var name in new[] { "a", "b", "c" }) site.File(Site + $"/{name}.zip", 256 * 1024).KBps = 128;
        var h = New(site, change: s => { s.DownloadAtOnce = 1; s.Include = "*.zip"; });
        h.Start();
        List<GrabberEntry> waiting = new();
        await h.Until(() =>
        {
            h.Manager.Tick(DateTime.Now);
            var found = h.Project.Entries.Where(e => e.Checked).ToList();
            waiting = found.Where(e => e.FileState == GrabberFileState.None).ToList();
            return found.Count == 3 && waiting.Count == 2 && h.Run.Crawler == null;
        });
        return (h, waiting);
    }

    [Fact]
    public async Task Unchecked_while_it_waits_is_not_downloaded()
    {
        await using var site = await SiteTestServer.StartAsync();
        var (h, waiting) = await OneOfThreeUnderWay(site);
        h.Ui.Run(() => waiting.ForEach(e => e.Checked = false));
        await h.Finished();
        Assert.All(waiting, e => Assert.Equal((GrabberFileState.None, (string?)null), (e.FileState, e.LocalPath)));
        Assert.Single(h.Project.Entries, e => e.FileState == GrabberFileState.Complete && e.Matched);
        await h.Manager.ShutdownAsync();
    }

    [Fact]
    public async Task What_waited_in_a_stopped_run_is_left_out_of_an_explore_only_one()
    {
        await using var site = await SiteTestServer.StartAsync();
        var (h, waiting) = await OneOfThreeUnderWay(site);
        h.Stop();
        h.Run.Mode = GrabberRunMode.ExploreOnly;
        h.Start();
        await h.Finished();
        Assert.All(waiting, e => Assert.Equal((GrabberFileState.None, (string?)null), (e.FileState, e.LocalPath)));
        await h.Manager.ShutdownAsync();
    }

    [Fact]
    public async Task Explore_only_leaves_checked_files_and_the_disk_alone()
    {
        await using var site = await SiteTestServer.StartAsync();
        site.Page(Site + "/", "<a href=/a.zip>a</a>");
        site.File(Site + "/a.zip");
        var h = New(site, mode: GrabberRunMode.ExploreOnly);
        h.Start();
        await h.Finished();
        foreach (var entry in h.Project.Entries) entry.Checked = true;
        h.Start();
        await h.Finished();
        Assert.False(Directory.Exists(SaveFolder));

        h.Run.Mode = GrabberRunMode.DownloadChecked;
        h.Start();
        await h.Finished();
        Assert.True(File.Exists(Saved("a.zip")));

        // Exploring again doesn't put back what the user has since taken away.
        File.Delete(Saved("index.html"));
        h.Run.Mode = GrabberRunMode.ExploreOnly;
        h.Start();
        await h.Finished();
        Assert.False(File.Exists(Saved("index.html")));
        await h.Manager.ShutdownAsync();
    }

    [Fact]
    public async Task Only_what_is_saved_is_converted()
    {
        await using var site = await SiteTestServer.StartAsync();
        site.Page(Site + "/", "<a href=/a.html>a</a>");
        site.Page(Site + "/a.html", "<p>");
        var h = New(site);
        h.Start();
        await h.Finished();

        // Let go of, with its copy still kept and its place still known.
        var page = h.Project.Entries.Single(e => e.Url == Site + "/a.html");
        page.Checked = false;
        page.FileState = GrabberFileState.None;
        File.Delete(Saved("a.html"));
        h.Run.Mode = GrabberRunMode.DownloadChecked;
        h.Start();
        await h.Finished();
        Assert.False(File.Exists(Saved("a.html")));
        Assert.Contains($"href={Site}/a.html", File.ReadAllText(Saved("index.html")));
        await h.Manager.ShutdownAsync();
    }

    [Fact]
    public async Task A_redirected_page_converts_from_where_it_ended()
    {
        await using var site = await SiteTestServer.StartAsync();
        site.Page(Site + "/", "<a href=/moved.html>m</a>");
        site.Redirect(Site + "/moved.html", Site + "/new/page.html");
        site.Page(Site + "/new/page.html", "<a href=a.zip>a</a>");
        site.File(Site + "/new/a.zip");
        var h = New(site);
        h.Start();
        await h.Finished();
        var moved = h.Project.Entries.Single(e => e.Url == Site + "/moved.html");
        var zip = h.Project.Entries.Single(e => e.Url == Site + "/new/a.zip");
        var relative = Path.GetRelativePath(Path.GetDirectoryName(moved.LocalPath!)!, zip.LocalPath!).Replace('\\', '/');
        Assert.Contains($"href={relative}", File.ReadAllText(moved.LocalPath!));
        await h.Manager.ShutdownAsync();
    }

    [Fact]
    public async Task Stopping_stops_exploring()
    {
        await using var site = await SiteTestServer.StartAsync();
        const int count = 20;
        site.Page(Site + "/", string.Concat(Enumerable.Range(0, count).Select(i => $"<a href=/{i}.html>x</a>")));
        foreach (var i in Enumerable.Range(0, count))
            site.Page(Site + $"/{i}.html", new string('x', 32 * 1024)).KBps = 64;
        var h = New(site);
        h.Start();
        await h.Until(() => h.Project.Entries.Count(e => e.Kind == GrabberKind.Page) > 2);
        h.Stop();
        await h.Until(() => h.Run.Crawler == null);
        Assert.Contains(h.Project.Entries, e => e.State == GrabberState.Waiting);
        await h.Manager.ShutdownAsync();
    }

    [Fact]
    public async Task Pages_waiting_when_stopped_are_saved_by_the_next_run()
    {
        await using var site = await SiteTestServer.StartAsync();
        const int count = 300;
        site.Page(Site + "/", string.Concat(Enumerable.Range(0, count).Select(i => $"<a href=/{i}.html>x</a>")));
        foreach (var i in Enumerable.Range(0, count)) site.Page(Site + $"/{i}.html", "<p>");
        var h = New(site, change: s => s.DownloadAtOnce = 1);
        h.Start();
        await h.Until(() => h.Project.Entries.Count(e => e.FileState == GrabberFileState.Complete) > 0);
        h.Stop();
        h.Start();
        await h.Finished();
        Assert.Equal(count + 1, Directory.GetFiles(Saved(), "*.html").Length);
        await h.Manager.ShutdownAsync();
    }

    [Fact]
    public async Task A_file_that_appears_while_downloading_is_kept()
    {
        await using var site = await SiteTestServer.StartAsync();
        site.Page(Site + "/", "<a href=/a.zip>a</a>");
        var file = site.File(Site + "/a.zip", 256 * 1024);
        // The download stops after its first piece until the other file is there.
        var mineWritten = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        file.RestAfter = mineWritten.Task;
        var h = New(site);
        h.Start();
        await h.Until(() =>
        {
            h.Manager.Tick(DateTime.Now);
            return h.Manager.Items.Any(i => i.DownloadedBytes > 0);
        });
        Directory.CreateDirectory(Saved());
        File.WriteAllText(Saved("a.zip"), "mine");
        mineWritten.SetResult();
        await h.Finished();
        Assert.Equal("mine", File.ReadAllText(Saved("a.zip")));
        var entry = h.Project.Entries.Single(e => e.Url == Site + "/a.zip");
        Assert.NotEqual(Saved("a.zip"), entry.LocalPath);
        Assert.Equal(file.Body, File.ReadAllBytes(entry.LocalPath!));
        Assert.Contains($"href={Uri.EscapeDataString(Path.GetFileName(entry.LocalPath!))}", File.ReadAllText(Saved("index.html")));
        await h.Manager.ShutdownAsync();
    }

    [Fact]
    public async Task A_file_the_project_no_longer_lists_leaves_the_list()
    {
        await using var site = await SiteTestServer.StartAsync();
        var first = New(site);
        first.Ui.Run(() => first.Manager.Items.Add(new DownloadItem
        {
            Url = Site + "/forgotten.zip",
            Directory = SaveFolder,
            FileName = "forgotten.zip",
            Status = DownloadStatus.Queued,
            GrabberProjectId = first.Project.Id,
        }));
        first.Ui.Run(first.Manager.Save);

        var second = New(site, project: first.Project);
        await second.Until(() => second.Manager.Items.Count == 0);
        await second.Manager.ShutdownAsync();
    }

    [Fact]
    public async Task Closing_saves_what_changed_since_the_run()
    {
        await using var site = await SiteTestServer.StartAsync();
        site.Page(Site + "/", "<a href=/a.zip>a</a>");
        site.File(Site + "/a.zip");
        var h = New(site, mode: GrabberRunMode.ExploreOnly);
        h.Start();
        await h.Finished();
        h.Project.Entries.Single(e => e.Url == Site + "/a.zip").Checked = true;
        await h.Manager.ShutdownAsync();
        var loaded = GrabberStore.Load(Path.Combine(ProjectFolder, "project.json"))!;
        Assert.True(loaded.Entries.Single(e => e.Url == Site + "/a.zip").Checked);
    }

    [Fact]
    public async Task Big_project_keeps_the_state_small()
    {
        await using var site = await SiteTestServer.StartAsync();
        const int count = 600;
        site.Page(Site + "/", string.Concat(Enumerable.Range(0, count).Select(i => $"<a href=/{i}.zip>x</a>")));
        foreach (var i in Enumerable.Range(0, count)) site.File(Site + $"/{i}.zip", 500);
        var h = New(site, change: s => s.DownloadAtOnce = 4);
        int most = 0;
        h.Manager.Items.CollectionChanged += (_, _) => most = Math.Max(most, h.Manager.Items.Count);
        h.Start();
        await h.Finished();
        Assert.True(most <= 4, $"{most} in the list at once");
        Assert.Equal(count, Directory.GetFiles(Saved(), "*.zip").Length);
        await h.Manager.ShutdownAsync();
        Assert.True(new FileInfo(StatePath).Length < 64 * 1024, $"{new FileInfo(StatePath).Length} bytes");
    }

    [Fact]
    public async Task Existing_file_gets_a_number()
    {
        await using var site = await SiteTestServer.StartAsync();
        site.Page(Site + "/", "<a href=/a.zip>a</a>");
        site.File(Site + "/a.zip");
        Directory.CreateDirectory(Saved());
        File.WriteAllText(Saved("a.zip"), "mine");
        File.WriteAllText(Saved("index.html"), "mine too");
        var h = New(site);
        h.Start();
        await h.Finished();
        Assert.Equal("mine", File.ReadAllText(Saved("a.zip")));
        Assert.Equal("mine too", File.ReadAllText(Saved("index.html")));
        Assert.Equal(site.Body(Site + "/a.zip"), File.ReadAllBytes(Saved("a_2.zip")));
        // Links lead to the file that really came.
        Assert.Contains("href=a_2.zip", File.ReadAllText(Saved("index_2.html")));
        await h.Manager.ShutdownAsync();
    }

    [Fact]
    public async Task Overwrite_replaces_only_when_set()
    {
        await using var site = await SiteTestServer.StartAsync();
        site.Page(Site + "/", "<a href=/a.zip>a</a>");
        site.File(Site + "/a.zip");
        Directory.CreateDirectory(Saved());
        File.WriteAllText(Saved("a.zip"), "mine");
        File.WriteAllText(Saved("index.html"), "mine too");
        var h = New(site, change: s => s.Overwrite = true);
        h.Start();
        await h.Finished();
        Assert.Equal(site.Body(Site + "/a.zip"), File.ReadAllBytes(Saved("a.zip")));
        Assert.Contains("href=a.zip", File.ReadAllText(Saved("index.html")));
        Assert.Equal(2, Directory.GetFiles(Saved()).Length);
        await h.Manager.ShutdownAsync();
    }

    [Fact]
    public async Task Stopping_converts_what_is_saved()
    {
        await using var site = await SiteTestServer.StartAsync();
        site.Page(Site + "/", "<a href=/small.zip>s</a><a href=/slow.zip>l</a><a href=/next.zip>n</a>");
        site.File(Site + "/small.zip", 100);
        site.File(Site + "/slow.zip", 4 * 1024 * 1024).KBps = 64;
        site.File(Site + "/next.zip", 100);
        var h = New(site, change: s => s.DownloadAtOnce = 2);
        h.Start();
        await h.Until(() =>
        {
            h.Manager.Tick(DateTime.Now);
            return h.Project.Entries.Any(e => e.Url == Site + "/small.zip" && e.FileState == GrabberFileState.Complete) && File.Exists(Saved("index.html"));
        });
        h.Stop();
        await h.Until(() => ReadWhileSaving(Saved("index.html")).Contains("href=small.zip"));
        var text = File.ReadAllText(Saved("index.html"));
        Assert.Contains("href=small.zip", text);
        Assert.Contains($"href={Site}/slow.zip", text);
        await h.Manager.ShutdownAsync();
    }

    // A page read while the run may be swapping it: either side can find the other holding the file for a moment.
    static string ReadWhileSaving(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return new StreamReader(stream).ReadToEnd();
        }
        catch (IOException)
        {
            return "";
        }
    }

    // Two pages and a file saved, and a slow file still coming, so the run can be stopped partway.
    async Task<(Harness Run, byte[] Index)> StoppedWithPagesSaved(SiteTestServer site)
    {
        site.Page(Site + "/", "<a href=/small.zip>s</a><a href=/b.html>b</a><a href=/slow.zip>l</a>");
        site.Page(Site + "/b.html", "<a href=/small.zip>s</a>");
        site.File(Site + "/small.zip", 100);
        site.File(Site + "/slow.zip", 4 * 1024 * 1024).KBps = 64;
        var h = New(site, change: s => s.DownloadAtOnce = 2);
        h.Start();
        await h.Until(() =>
        {
            h.Manager.Tick(DateTime.Now);
            return h.Project.Entries.Count(e => e.FileState == GrabberFileState.Complete) == 3;
        });
        return (h, site.Body(Site + "/"));
    }

    [Fact]
    public async Task Page_held_open_a_moment_is_still_converted()
    {
        await using var site = await SiteTestServer.StartAsync();
        var (h, _) = await StoppedWithPagesSaved(site);
        var held = new FileStream(Saved("index.html"), FileMode.Open, FileAccess.Read, FileShare.Read);
        // Let go of it from a thread of its own, so a busy machine can't hold it past the 1.55 s a swap waits.
        var letGo = new Thread(() =>
        {
            Thread.Sleep(300);
            held.Dispose();
        });
        h.Stop();
        letGo.Start();
        await h.Run.ShutdownAsync();
        letGo.Join();
        Assert.Contains("href=small.zip", File.ReadAllText(Saved("index.html")));
        Assert.Contains("href=small.zip", File.ReadAllText(Saved("b.html")));
        await h.Manager.ShutdownAsync();
    }

    [Fact]
    public async Task Page_held_open_keeps_its_links_and_nothing_is_left_beside_it()
    {
        await using var site = await SiteTestServer.StartAsync();
        var (h, index) = await StoppedWithPagesSaved(site);
        using (new FileStream(Saved("index.html"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            h.Stop();
            await h.Run.ShutdownAsync();
        }
        Assert.Equal(index, File.ReadAllBytes(Saved("index.html")));
        // The other page still gets its links.
        Assert.Contains("href=small.zip", File.ReadAllText(Saved("b.html")));
        Assert.Empty(Directory.GetFiles(SaveFolder, "*.admtmp", SearchOption.AllDirectories));
        await h.Manager.ShutdownAsync();
    }

    [Fact]
    public async Task Pages_still_to_be_written_when_stopped_are_saved_by_the_next_run()
    {
        await using var site = await SiteTestServer.StartAsync();
        site.Page(Site + "/", string.Concat(Enumerable.Range(0, 5).Select(i => $"<a href=/{i}.html>x</a>")));
        foreach (var i in Enumerable.Range(0, 5)) site.Page(Site + $"/{i}.html", $"<p>{i}");
        Directory.CreateDirectory(Saved());
        File.WriteAllText(Saved("index.html"), "mine");
        var h = New(site, change: s => { s.Overwrite = true; s.ConvertLinks = false; });
        // The first page can't be written while this holds it, so the others wait their turn behind it.
        using (new FileStream(Saved("index.html"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            h.Start();
            await h.Until(() =>
            {
                h.Manager.Tick(DateTime.Now);
                return h.Project.Entries.Count(e => e.Kind == GrabberKind.Page && e.FileState == GrabberFileState.Queued) == 6;
            });
            h.Stop();
            await h.Until(() => !h.Run.Busy && h.Project.Entries.Single(e => e.Url == Site + "/").FileState == GrabberFileState.Failed);
        }
        Assert.All(Enumerable.Range(0, 5), i => Assert.False(File.Exists(Saved($"{i}.html"))));

        h.Run.Mode = GrabberRunMode.DownloadChecked;
        h.Start();
        await h.Finished();
        Assert.Equal(site.Body(Site + "/"), File.ReadAllBytes(Saved("index.html")));
        Assert.All(Enumerable.Range(0, 5), i => Assert.Equal($"<p>{i}", File.ReadAllText(Saved($"{i}.html"))));
        await h.Manager.ShutdownAsync();
    }

    [Fact]
    public async Task Pause_and_resume_keeps_the_bytes()
    {
        await using var site = await SiteTestServer.StartAsync();
        site.Page(Site + "/", "<a href=/big.bin>b</a>");
        site.File(Site + "/big.bin", 8 * 1024 * 1024).KBps = 256;
        var h = New(site);
        h.Start();
        await h.Until(() =>
        {
            h.Manager.Tick(DateTime.Now);
            return h.Manager.Items.Any(i => i.DownloadedBytes > 1024 * 1024);
        });
        h.Stop();
        await h.Until(() => h.Manager.Items.All(i => !h.Manager.IsRunning(i)));
        var before = h.Manager.Items.Single().DownloadedBytes;
        Assert.True(before > 0);
        site.ClearRequests();

        h.Start();
        await h.Finished();
        Assert.Equal(site.Body(Site + "/big.bin"), File.ReadAllBytes(Saved("big.bin")));
        // Its one request from the start only asks whether the file is still the same.
        Assert.DoesNotContain(site.AskedFor("/big.bin"), a => a.Range.Length == 0 || (a.Range.StartsWith("bytes=0-") && a.Range != "bytes=0-0"));
        await h.Manager.ShutdownAsync();
    }

    [Fact]
    public async Task Restart_resumes_a_file_partway()
    {
        await using var site = await SiteTestServer.StartAsync();
        site.Page(Site + "/", "<a href=/big.bin>b</a>");
        site.File(Site + "/big.bin", 8 * 1024 * 1024).KBps = 256;
        var first = New(site);
        first.Start();
        await first.Until(() =>
        {
            first.Manager.Tick(DateTime.Now);
            return first.Manager.Items.Any(i => i.DownloadedBytes > 1024 * 1024);
        });
        await first.Manager.ShutdownAsync();
        await Task.Delay(100);
        site.ClearRequests();

        var loaded = GrabberStore.Load(Path.Combine(ProjectFolder, "project.json"))!;
        var second = New(site, project: loaded);
        Assert.Single(second.Manager.Items);
        Assert.Equal(GrabberFileState.Queued, loaded.Entries.Single(e => e.Url == Site + "/big.bin").FileState);
        second.Start();
        await second.Finished();
        Assert.Equal(site.Body(Site + "/big.bin"), File.ReadAllBytes(Saved("big.bin")));
        // Its one request from the start only asks whether the file is still the same.
        Assert.DoesNotContain(site.AskedFor("/big.bin"), a => a.Range.Length == 0 || (a.Range.StartsWith("bytes=0-") && a.Range != "bytes=0-0"));
        Assert.Empty(site.AskedFor("/"));
        await second.Manager.ShutdownAsync();
    }

    [Fact]
    public async Task Restart_records_a_file_that_finished()
    {
        await using var site = await SiteTestServer.StartAsync();
        site.Page(Site + "/", "<a href=/a.zip>a</a>");
        site.File(Site + "/a.zip");
        var first = New(site, change: s => { s.StartAtOnce = false; s.Include = "*.zip"; }, mode: GrabberRunMode.ExploreOnly);
        first.Start();
        await first.Finished();
        await first.Manager.ShutdownAsync();

        // As if ADM closed just as the file landed: complete in the list, and not yet in the project.
        var manager = new DownloadManager(StatePath, new SynchronizationContext());
        Directory.CreateDirectory(Saved());
        File.WriteAllText(Saved("a.zip"), "landed");
        manager.Items.Add(new DownloadItem
        {
            Url = Site + "/a.zip",
            Directory = Saved(),
            FileName = "a.zip",
            Status = DownloadStatus.Completed,
            TotalBytes = 6,
            GrabberProjectId = first.Project.Id,
        });
        manager.Save();

        var second = New(site, project: first.Project);
        await second.Until(() => second.Manager.Items.Count == 0);
        var entry = first.Project.Entries.Single(e => e.Url == Site + "/a.zip");
        Assert.Equal((GrabberFileState.Complete, Saved("a.zip")), (entry.FileState, entry.LocalPath));
        await second.Manager.ShutdownAsync();
    }

    [Fact]
    public async Task Stopped_and_started_at_once_explores_to_the_end()
    {
        await using var site = await SiteTestServer.StartAsync();
        const int count = 12;
        site.Page(Site + "/", string.Concat(Enumerable.Range(0, count).Select(i => $"<a href=/{i}.html>x</a>")));
        foreach (var i in Enumerable.Range(0, count))
            site.Page(Site + $"/{i}.html", new string('x', 32 * 1024)).KBps = 64;
        var h = New(site);
        h.Start();
        await h.Until(() => h.Project.Entries.Count(e => e.Kind == GrabberKind.Page) > 2);
        h.Stop();
        h.Start();
        await h.Finished();
        Assert.All(h.Project.Entries, e => Assert.Equal(GrabberState.Done, e.State));
        Assert.Equal(count + 1, Directory.GetFiles(Saved(), "*.html").Length);
        await h.Manager.ShutdownAsync();
    }

    [Fact]
    public async Task A_run_started_during_the_last_conversion_goes_on()
    {
        await using var site = await SiteTestServer.StartAsync();
        const int count = 200;
        site.Page(Site + "/", "<a href=/later.zip>l</a>" + string.Concat(Enumerable.Range(0, count).Select(i => $"<a href=/{i}.html>x</a>")));
        foreach (var i in Enumerable.Range(0, count)) site.Page(Site + $"/{i}.html", "<p>");
        var later = site.File(Site + "/later.zip", 64 * 1024);
        later.GoneAfter = 1;
        later.KBps = 128;
        var h = New(site);
        h.Queue.RetriesPerFile = 0;
        h.Start();
        // The start page is converted first, with 200 still to go, and the conversion can't report back while this holds the UI.
        await h.Until(() =>
        {
            if (!File.Exists(Saved("index.html")) || !ReadWhileSaving(Saved("index.html")).Contains("href=0.html")) return false;
            later.GoneAfter = null;
            h.Manager.StopQueue(h.Queue);
            h.Manager.StartQueue(h.Queue);
            return true;
        });
        await h.Finished();
        Assert.Equal(later.Body, File.ReadAllBytes(Saved("later.zip")));
        // Converted at the end of the run that fetched it.
        Assert.Contains("href=later.zip", File.ReadAllText(Saved("index.html")));
        await h.Manager.ShutdownAsync();
    }

    [Fact]
    public async Task A_page_past_the_limit_is_saved_as_it_came()
    {
        await using var site = await SiteTestServer.StartAsync();
        site.Page(Site + "/", "<a href=/big.html>b</a>");
        const int limit = 64 * 1024;
        var big = site.Page(Site + "/big.html", "<a href=/>home</a>" + new string(' ', limit));
        var h = New(site, change: s => { s.WholeSite = false; s.SiteLevels = 0; });
        h.Run.ConvertLimit = limit;
        h.Start();
        await h.Finished();
        var entry = h.Project.Entries.Single(e => e.Url == Site + "/big.html");
        Assert.Equal((GrabberFileState.Complete, false), (entry.FileState, entry.Kept));
        Assert.Equal(big.Body, File.ReadAllBytes(Saved("big.html")));
        await h.Manager.ShutdownAsync();
    }

    [Fact]
    public async Task A_link_the_list_is_downloading_is_downloaded_for_the_project_too()
    {
        await using var site = await SiteTestServer.StartAsync();
        site.Page(Site + "/", "<a href=/a.zip>a</a>");
        var file = site.File(Site + "/a.zip", 256 * 1024);
        file.KBps = 256;
        var h = New(site);
        DownloadItem mine = null!;
        h.Ui.Run(() => mine = h.Manager.Add(new DownloadRequest { Url = Site + "/a.zip", FileName = "mine.zip", Directory = _dir }, startNow: true));
        h.Start();
        await h.Finished();
        Assert.Equal(file.Body, File.ReadAllBytes(Saved("a.zip")));
        Assert.Contains(mine, h.Manager.Items);
        await h.Manager.ShutdownAsync();
    }

    [Fact]
    public async Task Failed_file_uses_the_queue_retries()
    {
        await using var site = await SiteTestServer.StartAsync();
        site.Page(Site + "/", "<a href=/fine.zip>f</a><a href=/gone.zip>g</a>");
        site.File(Site + "/fine.zip", 2000);
        // It answers while exploring, and is gone by the time it downloads.
        site.File(Site + "/gone.zip", 2000).GoneAfter = 1;
        var h = New(site, change: s => s.Include = "*.zip");
        h.Queue.RetriesPerFile = 1;
        h.Start();
        await h.Finished();
        var gone = h.Project.Entries.Single(e => e.Url == Site + "/gone.zip");
        Assert.Equal(GrabberFileState.Failed, gone.FileState);
        Assert.Contains("404", gone.Error);
        Assert.Equal(3, site.AskedFor("/gone.zip").Count);
        Assert.True(File.Exists(Saved("fine.zip")));
        Assert.Empty(h.Manager.Items);
        await h.Manager.ShutdownAsync();
    }

    [Fact]
    public async Task Failed_file_is_tried_again_by_the_next_run_in_the_same_place()
    {
        await using var site = await SiteTestServer.StartAsync();
        site.Page(Site + "/", "<a href=/back.zip>b</a>");
        var file = site.File(Site + "/back.zip", 2000);
        file.GoneAfter = 1;
        var h = New(site, change: s => s.Include = "*.zip");
        h.Queue.RetriesPerFile = 0;
        h.Start();
        await h.Finished();
        Assert.Equal(GrabberFileState.Failed, h.Project.Entries.Single(e => e.Url == Site + "/back.zip").FileState);

        file.GoneAfter = null;
        h.Start();
        await h.Finished();
        var entry = h.Project.Entries.Single(e => e.Url == Site + "/back.zip");
        Assert.Equal((GrabberFileState.Complete, Saved("back.zip"), (string?)null), (entry.FileState, entry.LocalPath, entry.Error));
        Assert.Equal(file.Body, File.ReadAllBytes(Saved("back.zip")));
        await h.Manager.ShutdownAsync();
    }

    [Fact]
    public async Task Explore_only_downloads_nothing_even_when_set_to_start_at_once()
    {
        await using var site = await SiteTestServer.StartAsync();
        site.Page(Site + "/", "<a href=/a.zip>a</a>");
        site.File(Site + "/a.zip");
        var h = New(site, mode: GrabberRunMode.ExploreOnly);
        h.Start();
        await h.Finished();
        Assert.False(Directory.Exists(SaveFolder));
        Assert.Contains(h.Project.Entries, e => e.Url == Site + "/a.zip" && e.Matched && e.FileState == GrabberFileState.None);
        await h.Manager.ShutdownAsync();
    }

    [Fact]
    public async Task File_saved_as_queued_and_gone_from_the_list_is_queued_again()
    {
        await using var site = await SiteTestServer.StartAsync();
        site.Page(Site + "/", "<a href=/a.zip>a</a>");
        site.File(Site + "/a.zip");
        var first = New(site);
        first.Start();
        await first.Finished();
        await first.Manager.ShutdownAsync();
        File.Delete(Saved("a.zip"));

        // As if the project's file is newer than the list, which was saved before the file joined it.
        var entry = first.Project.Entries.Single(e => e.Url == Site + "/a.zip");
        entry.FileState = GrabberFileState.Queued;
        var second = New(site, project: first.Project);
        second.Start();
        await second.Finished();
        Assert.Equal(GrabberFileState.Complete, entry.FileState);
        Assert.True(File.Exists(Saved("a.zip")));
        await second.Manager.ShutdownAsync();
    }

    [Fact]
    public async Task Without_a_folder_it_goes_under_the_download_folder()
    {
        await using var site = await SiteTestServer.StartAsync();
        site.Page(Site + "/", "<a href=/a.zip>a</a>");
        site.File(Site + "/a.zip");
        var h = New(site, change: s => s.Folder = "");
        h.Start();
        await h.Finished();
        Assert.True(File.Exists(Path.Combine(_dir, "test", "www.site.test", "a.zip")));
        await h.Manager.ShutdownAsync();
    }

    [Fact]
    public async Task Saved_pages_get_the_mark_of_the_web()
    {
        await using var site = await SiteTestServer.StartAsync();
        site.Page(Site + "/", "<a href=/a.zip>a</a>");
        site.File(Site + "/a.zip");
        var h = New(site);
        h.Start();
        await h.Finished();
        Assert.Contains("ZoneId=3", File.ReadAllText(Saved("index.html") + ":Zone.Identifier"));
        Assert.Contains("ZoneId=3", File.ReadAllText(Saved("a.zip") + ":Zone.Identifier"));
        await h.Manager.ShutdownAsync();
    }

    [Fact]
    public async Task Downloads_respect_the_speed_limit()
    {
        await using var site = await SiteTestServer.StartAsync();
        site.Page(Site + "/", "<a href=/a.bin>a</a>");
        site.File(Site + "/a.bin", 1024 * 1024);
        var h = New(site);
        h.Manager.State.Settings.SpeedLimitKBps = 1024;
        h.Manager.ApplySettings();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        h.Start();
        await h.Finished();
        Assert.True(clock.Elapsed > TimeSpan.FromMilliseconds(700), $"took {clock.Elapsed}");
        await h.Manager.ShutdownAsync();
    }

    [Fact]
    public async Task Login_and_cookies_go_with_the_downloads_of_their_site_only()
    {
        await using var site = await SiteTestServer.StartAsync();
        site.Page(Site + "/", "<a href=/mine.zip>m</a><a href=http://other.test/theirs.zip>t</a>");
        site.File(Site + "/mine.zip", 5000).Login = "user:pass";
        site.File("http://other.test/theirs.zip", 5000).Login = "user:pass";
        var h = New(site, change: s =>
        {
            s.ThisSiteOnly = false;
            s.Include = "*.zip";
            s.UserName = "user";
            s.ProtectedPassword = Secrets.Protect("pass");
            s.ProtectedCookies = Secrets.Protect("session=1");
        });
        h.Queue.RetriesPerFile = 0;
        h.Start();
        await h.Finished();
        Assert.True(File.Exists(Saved("mine.zip")));
        Assert.Contains(site.Requests, r => r.PathAndQuery == "/mine.zip" && r.Cookie == "session=1" && r.Authorization.Length > 0);
        Assert.All(site.AskedFor("/theirs.zip"), r => Assert.Equal(("", ""), (r.Cookie, r.Authorization)));
        await h.Manager.ShutdownAsync();
    }

    [Fact]
    public async Task Another_site_s_downloads_get_neither_login_nor_cookies()
    {
        await using var site = await SiteTestServer.StartAsync();
        site.Page(Site + "/", "<a href=/mine.zip>m</a><a href=http://other.test/theirs.zip>t</a>");
        site.File(Site + "/mine.zip", 5000);
        site.File("http://other.test/theirs.zip", 5000);
        var h = New(site, change: s =>
        {
            s.ThisSiteOnly = false;
            s.Include = "*.zip";
            s.UserName = "user";
            s.ProtectedPassword = Secrets.Protect("pass");
            s.ProtectedCookies = Secrets.Protect("session=1");
        });
        // As each is added: a finished download lets go of its cookies.
        var added = new List<(string Url, string? User, string? Password, string? Cookies)>();
        h.Manager.Items.CollectionChanged += (_, e) =>
            added.AddRange((e.NewItems?.OfType<DownloadItem>() ?? []).Select(i => (i.Url, i.UserName, Secrets.Unprotect(i.ProtectedPassword), i.Cookies)));
        h.Start();
        await h.Finished();
        Assert.True(File.Exists(Path.Combine(SaveFolder, "other.test", "theirs.zip")));
        Assert.Equal((Site + "/mine.zip", "user", "pass", "session=1"), added.Single(i => i.Url == Site + "/mine.zip"));
        Assert.Equal(("http://other.test/theirs.zip", null, null, null), added.Single(i => i.Url == "http://other.test/theirs.zip"));
        await h.Manager.ShutdownAsync();
    }

    [Fact]
    public async Task Each_save_mode()
    {
        await using var site = await SiteTestServer.StartAsync();
        site.Page(Site + "/", "<a href=/d/a.zip>a</a><a href=/d/b.pdf>b</a>");
        site.File(Site + "/d/a.zip");
        site.File(Site + "/d/b.pdf");
        var h = New(site, change: s => { s.Include = "*.zip,*.pdf"; s.SaveMode = GrabberSaveMode.ByCategory; s.KeepSubfolders = false; });
        h.Start();
        await h.Finished();
        Assert.True(File.Exists(Path.Combine(_dir, "Compressed", "a.zip")));
        Assert.True(File.Exists(Path.Combine(_dir, "Documents", "b.pdf")));
        await h.Manager.ShutdownAsync();

        Directory.Delete(ProjectFolder, true);
        var again = New(site, change: s => { s.Include = "*.zip,*.pdf"; s.SaveMode = GrabberSaveMode.CategoryFolder; s.Category = "Music"; s.KeepSubfolders = false; });
        again.Start();
        await again.Finished();
        Assert.True(File.Exists(Path.Combine(_dir, "Music", "a.zip")));
        Assert.True(File.Exists(Path.Combine(_dir, "Music", "b.pdf")));
        await again.Manager.ShutdownAsync();

        Directory.Delete(ProjectFolder, true);
        var flat = New(site, change: s => { s.Include = "*.zip,*.pdf"; s.KeepSubfolders = false; });
        flat.Start();
        await flat.Finished();
        Assert.True(File.Exists(Path.Combine(SaveFolder, "a.zip")));
        await flat.Manager.ShutdownAsync();
    }

    [Fact]
    public async Task A_page_past_its_levels_is_downloaded_and_converted()
    {
        await using var site = await SiteTestServer.StartAsync();
        site.Page(Site + "/", "<a href=/next.html>n</a>");
        site.Page(Site + "/next.html", "<a href=/>home</a><a href=/gone.html>g</a>");
        var h = New(site, change: s => { s.WholeSite = false; s.SiteLevels = 0; });
        h.Start();
        await h.Finished();
        var next = File.ReadAllText(Saved("next.html"));
        Assert.Contains("href=index.html", next);
        Assert.Contains($"href={Site}/gone.html", next);
        await h.Manager.ShutdownAsync();
    }

    [Fact]
    public async Task Without_converting_pages_are_as_they_came()
    {
        await using var site = await SiteTestServer.StartAsync();
        const string page = "<a href=/a.zip>a</a>";
        site.Page(Site + "/", page);
        site.File(Site + "/a.zip");
        var h = New(site, change: s => s.ConvertLinks = false);
        h.Start();
        await h.Finished();
        Assert.Equal(page, File.ReadAllText(Saved("index.html")));
        await h.Manager.ShutdownAsync();
    }

    [Fact]
    public async Task Link_text_is_the_description_when_asked()
    {
        await using var site = await SiteTestServer.StartAsync();
        site.Page(Site + "/", "<a href=/a.bin>The big file</a>");
        site.File(Site + "/a.bin", 2 * 1024 * 1024).KBps = 512;
        var h = New(site, change: s => s.LinkTextAsDescription = true);
        DownloadItem? seen = null;
        h.Start();
        await h.Until(() =>
        {
            h.Manager.Tick(DateTime.Now);
            seen = h.Manager.Items.FirstOrDefault();
            return seen != null;
        });
        Assert.Equal("The big file", seen!.Description);
        Assert.Equal(h.Project.Id, seen.GrabberProjectId);
        await h.Manager.ShutdownAsync();
    }

    [Fact]
    public async Task Finished_project_reports_once()
    {
        await using var site = await SiteTestServer.StartAsync();
        WriteSite(site);
        var h = New(site);
        int finished = 0;
        h.Manager.QueueFinished += q => { if (q == h.Queue) finished++; };
        h.Start();
        await h.Finished();
        for (int i = 0; i < 5; i++)
        {
            h.Ui.Run(() => h.Manager.Tick(DateTime.Now));
            await Task.Delay(20);
        }
        Assert.Equal(1, finished);
        await h.Manager.ShutdownAsync();
    }

    [Fact]
    public async Task Project_is_saved_when_it_ends()
    {
        await using var site = await SiteTestServer.StartAsync();
        WriteSite(site);
        var h = New(site);
        h.Start();
        await h.Finished();
        var loaded = GrabberStore.Load(Path.Combine(ProjectFolder, "project.json"))!;
        Assert.Equal(GrabberFileState.Complete, loaded.Entries.Single(e => e.Url == Site + "/img/logo.png").FileState);
        Assert.Equal(Saved("img", "logo.png"), loaded.Entries.Single(e => e.Url == Site + "/img/logo.png").LocalPath);
        await h.Manager.ShutdownAsync();
    }
}
