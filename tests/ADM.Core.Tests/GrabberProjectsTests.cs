using System.Text;
using ADM.Core;

namespace ADM.Core.Tests;

public sealed class GrabberProjectsTests : IDisposable
{
    const string Site = "http://www.site.test";
    // A Monday, well in the past, so a scheduled start can't be mistaken for one at the real time.
    static readonly DateTime Day = new(2026, 1, 5);

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

    string StatePath => Path.Combine(_dir, "state", "state.json");
    string SaveFolder => Path.Combine(_dir, "saved");

    string Saved(params string[] parts) => Path.Combine(new[] { SaveFolder, "www.site.test" }.Concat(parts).ToArray());

    sealed class Harness
    {
        public required DownloadManager Manager;
        public required LockedContext Ui;
        public required GrabberProjects Projects;
        public GrabberProject Project = null!;

        public DownloadQueue Queue => On(() => Projects.QueueOf(Project)!);
        public GrabberRun Run => On(() => Projects.RunOf(Project)!);

        public T On<T>(Func<T> read)
        {
            T value = default!;
            Ui.Run(() => value = read());
            return value;
        }

        /// <summary>Runs on the UI, as the app does, so what it awaits comes back there.</summary>
        public async Task OnUi(Func<Task> action)
        {
            Task task = null!;
            Ui.Run(() =>
            {
                var before = SynchronizationContext.Current;
                SynchronizationContext.SetSynchronizationContext(Ui);
                try
                {
                    task = action();
                }
                finally
                {
                    SynchronizationContext.SetSynchronizationContext(before);
                }
            });
            await task;
        }

        public bool Start(GrabberRunMode mode = GrabberRunMode.ExploreAndDownload) => On(() => Projects.Start(Project, mode));

        public async Task Finished(DateTime? at = null)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(120);
            while (true)
            {
                bool running = false;
                Ui.Run(() =>
                {
                    Manager.Tick(at ?? DateTime.Now);
                    running = Projects.QueueOf(Project)!.IsRunning || Projects.RunOf(Project)?.Busy == true;
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

        public async Task Shutdown() => await OnUi(Manager.ShutdownAsync);
    }

    Harness New(SiteTestServer site, Action<GrabberSettings>? change = null)
    {
        var h = Load(site);
        var settings = new GrabberSettings
        {
            StartUrl = Site + "/",
            WholeSite = true,
            Folder = SaveFolder,
            KeepSubfolders = true,
            ConvertLinks = true,
            StartAtOnce = true,
            ThisSiteOnly = true,
        };
        change?.Invoke(settings);
        h.Ui.Run(() => h.Project = h.Projects.Create("site", settings));
        return h;
    }

    // ADM starting with the state as it was left.
    Harness Load(SiteTestServer site)
    {
        var ui = new LockedContext();
        DownloadManager manager = null!;
        GrabberProjects projects = null!;
        ui.Run(() =>
        {
            manager = new DownloadManager(StatePath, ui) { RetryDelay = TimeSpan.FromMilliseconds(10) };
            manager.State.Settings.Proxy = site.Proxy;
            manager.State.Settings.DownloadRoot = _dir;
            projects = new GrabberProjects(manager);
        });
        return new Harness { Manager = manager, Ui = ui, Projects = projects };
    }

    static void WriteSite(SiteTestServer site)
    {
        site.Page(Site + "/", "<a href=/a.html>a</a><a href=/b.html>b</a><a href=/a.zip>z</a>");
        site.Page(Site + "/a.html", "<p>a");
        site.Page(Site + "/b.html", "<p>b");
        site.File(Site + "/a.zip", 3000);
    }

    [Fact]
    public async Task Project_survives_a_restart()
    {
        await using var site = await SiteTestServer.StartAsync();
        WriteSite(site);
        var first = New(site, s => s.Include = "*.zip");
        Assert.True(first.Start(GrabberRunMode.ExploreOnly));
        await first.Finished();
        var id = first.Project.Id;
        var queueId = first.Queue.Id;
        await first.Shutdown();

        var second = Load(site);
        var info = Assert.Single(second.Projects.All);
        Assert.Equal((id, "site", queueId), (info.Id, info.Name, info.QueueId));
        second.Ui.Run(() => second.Project = second.Projects.Open(id)!);
        Assert.Equal("*.zip", second.Project.Settings.Include);
        Assert.Equal(Site + "/a.zip", Assert.Single(second.Project.Entries, e => e.Matched).Url);
        Assert.Equal(id, second.Queue.GrabberProjectId);
        Assert.Same(second.Queue, second.On(() => second.Manager.Queues.Single(q => q.Id == queueId)));
        await second.Shutdown();
    }

    [Fact]
    public async Task Names_never_clash()
    {
        await using var site = await SiteTestServer.StartAsync();
        var h = New(site);
        GrabberProject other = null!;
        h.Ui.Run(() => other = h.Projects.Create("SITE", new GrabberSettings { StartUrl = Site + "/" }));
        Assert.Equal("SITE (2)", other.Name);
        h.Ui.Run(() =>
        {
            other.Name = "site";
            h.Projects.Save(other);
        });
        Assert.Equal("site (2)", other.Name);
        Assert.Equal(["site", "site (2)"], h.Projects.All.Select(p => p.Name));
        await h.Shutdown();
    }

    [Fact]
    public async Task Delete_keeps_the_saved_files()
    {
        await using var site = await SiteTestServer.StartAsync();
        WriteSite(site);
        var h = New(site);
        h.Start();
        await h.Finished();
        Assert.True(File.Exists(Saved("a.zip")));
        var folder = h.Projects.FolderOf(h.Project.Id);
        Assert.True(Directory.Exists(Path.Combine(folder, "bodies")));

        await h.OnUi(() => h.Projects.DeleteAsync(h.Project.Id));
        Assert.Equal(site.Body(Site + "/a.zip"), File.ReadAllBytes(Saved("a.zip")));
        Assert.True(File.Exists(Saved("index.html")));
        Assert.False(Directory.Exists(folder));
        Assert.Empty(h.Projects.All);
        await h.Shutdown();
        Assert.Empty(Store.Load(StatePath).GrabberProjects);
    }

    [Fact]
    public async Task Delete_removes_its_queue_and_waiting_items()
    {
        await using var site = await SiteTestServer.StartAsync();
        site.Page(Site + "/", "<a href=/a.zip>a</a><a href=/b.zip>b</a>");
        site.File(Site + "/a.zip", 4 * 1024 * 1024).KBps = 64;
        site.File(Site + "/b.zip", 4 * 1024 * 1024).KBps = 64;
        var h = New(site, s => s.DownloadAtOnce = 1);
        var mine = h.On(() => h.Manager.Add(new DownloadRequest { Url = site.BaseUrl + "/elsewhere.zip", Directory = _dir }, startNow: false));
        h.Start();
        DownloadItem running = null!;
        await h.Until(() =>
        {
            h.Manager.Tick(DateTime.Now);
            running = h.Manager.Items.FirstOrDefault(i => i.GrabberProjectId != null && h.Manager.IsRunning(i) && i.DownloadedBytes > 0)!;
            return running != null;
        });
        var queue = h.Queue;

        await h.OnUi(() => h.Projects.DeleteAsync(h.Project.Id));
        h.Ui.Run(() =>
        {
            Assert.DoesNotContain(h.Manager.Items, i => i.GrabberProjectId != null);
            Assert.DoesNotContain(queue, h.Manager.Queues);
            // The user's own download is left alone.
            Assert.Contains(mine, h.Manager.Items);
            Assert.Equal(DownloadQueue.MainId, mine.QueueId);
        });
        Assert.False(File.Exists(running.PartPath));
        await h.Shutdown();
    }

    [Fact]
    public async Task Only_one_run_per_project()
    {
        await using var site = await SiteTestServer.StartAsync();
        WriteSite(site);
        site.Hold = TimeSpan.FromMilliseconds(300);
        var h = New(site);
        Assert.True(h.Start(GrabberRunMode.ExploreOnly));
        Assert.False(h.Start(GrabberRunMode.ExploreAndDownload));
        // Closing it while it runs leaves it open, and running.
        await h.OnUi(() => h.Projects.CloseAsync(h.Project));
        Assert.NotNull(h.On(() => h.Projects.RunOf(h.Project)));
        await h.Finished();
        Assert.Single(site.AskedFor("/"));
        Assert.False(Directory.Exists(SaveFolder));
        await h.Shutdown();
    }

    // The project, saved and closed, with its schedule set to start at 10:00.
    async Task<Harness> Scheduled(SiteTestServer site, GrabberRunMode mode, Action<GrabberSettings>? change = null, Action<QueueSchedule>? schedule = null)
    {
        var h = New(site, change);
        h.Ui.Run(() =>
        {
            h.Projects.SetScheduledRun(h.Project, mode);
            var s = h.Queue.Schedule;
            s.Enabled = true;
            s.StartTime = TimeSpan.FromHours(10);
            schedule?.Invoke(s);
        });
        await h.OnUi(() => h.Projects.CloseAsync(h.Project));
        Assert.Null(h.On(() => h.Projects.RunOf(h.Project)));
        return h;
    }

    // What its schedule does, on a manager just started, as at 10:00 of the day.
    async Task<Harness> RestartedAndTicked(Harness before, SiteTestServer site, DateTime at)
    {
        var id = before.Project.Id;
        await before.Shutdown();
        var h = Load(site);
        h.Ui.Run(() => h.Manager.Tick(at));
        h.Ui.Run(() => h.Project = h.Projects.Open(id)!);
        return h;
    }

    [Fact]
    public async Task Scheduled_project_starts_at_its_time()
    {
        await using var site = await SiteTestServer.StartAsync();
        WriteSite(site);
        var h = await Scheduled(site, GrabberRunMode.ExploreAndDownload);
        var at = Day.AddHours(10).AddSeconds(30);
        h.Ui.Run(() => h.Manager.Tick(at.AddMinutes(-5)));
        Assert.Empty(site.Requests);

        h = await RestartedAndTicked(h, site, at);
        Assert.True(h.Queue.IsRunning);
        await h.Finished(at);
        Assert.Equal(site.Body(Site + "/a.zip"), File.ReadAllBytes(Saved("a.zip")));
        Assert.True(File.Exists(Saved("b.html")));
        await h.Shutdown();
    }

    [Fact]
    public async Task Scheduled_explore_only_downloads_nothing()
    {
        await using var site = await SiteTestServer.StartAsync();
        WriteSite(site);
        var h = await Scheduled(site, GrabberRunMode.ExploreOnly);
        var at = Day.AddHours(10).AddSeconds(30);
        // Closed, and not restarted: the schedule still reaches it, and loads it again.
        h.Ui.Run(() =>
        {
            h.Manager.Tick(at);
            h.Project = h.Projects.Open(h.Project.Id)!;
        });
        Assert.True(h.Queue.IsRunning);
        await h.Finished(at);
        Assert.Single(site.AskedFor("/b.html"));
        Assert.Equal(4, h.Project.Entries.Count(e => e.Matched));
        Assert.False(Directory.Exists(SaveFolder));
        await h.Shutdown();
    }

    [Fact]
    public async Task Scheduled_download_checked_explores_nothing()
    {
        await using var site = await SiteTestServer.StartAsync();
        WriteSite(site);
        var h = New(site, s => s.StartAtOnce = false);
        h.Start(GrabberRunMode.ExploreOnly);
        await h.Finished();
        h.Ui.Run(() => h.Project.Entries.Single(e => e.Url == Site + "/a.zip").Checked = true);
        h.Ui.Run(() => h.Projects.Save(h.Project));
        var at = Day.AddHours(10).AddSeconds(30);
        h.Ui.Run(() =>
        {
            h.Projects.SetScheduledRun(h.Project, GrabberRunMode.DownloadChecked);
            h.Queue.Schedule.Enabled = true;
            h.Queue.Schedule.StartTime = TimeSpan.FromHours(10);
        });
        await h.OnUi(() => h.Projects.CloseAsync(h.Project));
        site.ClearRequests();

        h = await RestartedAndTicked(h, site, at);
        await h.Finished(at);
        Assert.Equal(site.Body(Site + "/a.zip"), File.ReadAllBytes(Saved("a.zip")));
        Assert.False(File.Exists(Saved("b.html")));
        Assert.Empty(site.AskedFor("/"));
        Assert.Empty(site.AskedFor("/b.html"));
        await h.Shutdown();
    }

    [Fact]
    public async Task Schedule_set_while_it_is_open_runs_what_it_says()
    {
        await using var site = await SiteTestServer.StartAsync();
        WriteSite(site);
        var h = New(site);
        var at = Day.AddHours(10).AddSeconds(30);
        h.Ui.Run(() =>
        {
            h.Projects.SetScheduledRun(h.Project, GrabberRunMode.ExploreOnly);
            h.Queue.Schedule.Enabled = true;
            h.Queue.Schedule.StartTime = TimeSpan.FromHours(10);
            h.Manager.Tick(at);
        });
        await h.Finished(at);
        Assert.Equal(4, h.Project.Entries.Count(e => e.Matched));
        Assert.False(Directory.Exists(SaveFolder));
        await h.Shutdown();
    }

    [Fact]
    public async Task Stop_time_stops_a_project()
    {
        await using var site = await SiteTestServer.StartAsync();
        site.Page(Site + "/", "<a href=/slow.zip>s</a>");
        site.File(Site + "/slow.zip", 4 * 1024 * 1024).KBps = 64;
        var h = await Scheduled(site, GrabberRunMode.ExploreAndDownload, schedule: s => s.StopTime = TimeSpan.FromHours(11));
        h = await RestartedAndTicked(h, site, Day.AddHours(10).AddSeconds(30));
        await h.Until(() =>
        {
            h.Manager.Tick(Day.AddHours(10).AddMinutes(1));
            return h.Manager.Items.Any(i => i.GrabberProjectId != null && h.Manager.IsRunning(i));
        });

        h.Ui.Run(() => h.Manager.Tick(Day.AddHours(11).AddSeconds(30)));
        Assert.False(h.Queue.IsRunning);
        await h.Until(() => !h.Run.Busy && h.Manager.Items.All(i => !h.Manager.IsRunning(i)));
        Assert.False(File.Exists(Saved("slow.zip")));
        await h.Shutdown();
    }

    [Fact]
    public async Task Finished_project_reports_once()
    {
        await using var site = await SiteTestServer.StartAsync();
        WriteSite(site);
        var h = New(site);
        var finished = new List<DownloadQueue>();
        h.Ui.Run(() => h.Manager.QueueFinished += finished.Add);
        h.Start();
        await h.Finished();
        for (int i = 0; i < 5; i++)
        {
            h.Ui.Run(() => h.Manager.Tick(DateTime.Now));
            await Task.Delay(20);
        }
        Assert.Equal(h.Queue, Assert.Single(finished));
        await h.Shutdown();
    }

    // Pages with nothing converted, so each saved file is exactly what was saved of it.
    Harness Unconverted(SiteTestServer site) => New(site, s => s.ConvertLinks = false);

    [Fact]
    public async Task Rerun_fetches_only_the_changed_page()
    {
        await using var site = await SiteTestServer.StartAsync();
        WriteSite(site);
        foreach (var page in new[] { "/", "/b.html" }) site.Page(Site + page, Encoding.UTF8.GetString(site.Body(Site + page))).ETag = $"\"{page}-1\"";
        // A server may give only the time it last changed.
        var modified = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        site.Page(Site + "/a.html", "<p>a").LastModified = modified;
        var h = Unconverted(site);
        h.Start();
        await h.Finished();
        File.WriteAllText(Saved("a.html"), "mine");

        site.Page(Site + "/b.html", "<p>b, changed").ETag = "\"/b.html-2\"";
        site.ClearRequests();
        h.Start();
        await h.Finished();
        Assert.Equal(modified.ToString("R"), Assert.Single(site.AskedFor("/a.html")).IfModifiedSince);
        Assert.Equal("\"/b.html-1\"", Assert.Single(site.AskedFor("/b.html")).IfNoneMatch);
        Assert.Equal("mine", File.ReadAllText(Saved("a.html")));
        Assert.Equal("<p>b, changed", File.ReadAllText(Saved("b.html")));
        // The start page wasn't sent again, so its links came from its copy, and they still lead somewhere.
        Assert.Equal("\"/-1\"", Assert.Single(site.AskedFor("/")).IfNoneMatch);
        Assert.Single(site.AskedFor("/a.zip"));
        await h.Shutdown();
    }

    [Fact]
    public async Task Rerun_with_no_validators_uses_the_hash()
    {
        await using var site = await SiteTestServer.StartAsync();
        WriteSite(site);
        var h = Unconverted(site);
        h.Start();
        await h.Finished();
        File.WriteAllText(Saved("a.html"), "mine");
        File.WriteAllText(Saved("b.html"), "mine");

        site.Page(Site + "/b.html", "<p>b, changed");
        site.ClearRequests();
        h.Start();
        await h.Finished();
        Assert.Equal("", Assert.Single(site.AskedFor("/a.html")).IfNoneMatch);
        Assert.Equal("mine", File.ReadAllText(Saved("a.html")));
        Assert.Equal("<p>b, changed", File.ReadAllText(Saved("b.html")));
        await h.Shutdown();
    }

    [Fact]
    public async Task Rerun_finds_new_files()
    {
        await using var site = await SiteTestServer.StartAsync();
        WriteSite(site);
        var h = New(site);
        h.Start();
        await h.Finished();

        site.Page(Site + "/b.html", "<p>b <a href=new.zip>new</a>");
        site.File(Site + "/new.zip", 2000);
        h.Start();
        await h.Finished();
        Assert.Equal(site.Body(Site + "/new.zip"), File.ReadAllBytes(Saved("new.zip")));
        Assert.Contains("href=new.zip", File.ReadAllText(Saved("b.html")));
        await h.Shutdown();
    }

    [Fact]
    public async Task Rerun_redownloads_a_changed_file()
    {
        await using var site = await SiteTestServer.StartAsync();
        site.Page(Site + "/", "<a href=/a.zip>a</a><a href=/same.zip>s</a>");
        site.File(Site + "/a.zip", 3000);
        site.File(Site + "/same.zip", 2000);
        var h = New(site);
        h.Start();
        await h.Finished();

        var changed = site.File(Site + "/a.zip", 5000);
        site.ClearRequests();
        h.Start();
        await h.Finished();
        Assert.Equal(changed.Body, File.ReadAllBytes(Saved("a.zip")));
        Assert.False(File.Exists(Saved("a_2.zip")));
        var entry = h.Project.Entries.Single(e => e.Url == Site + "/a.zip");
        Assert.Equal((GrabberFileState.Complete, false, 5000L), (entry.FileState, entry.Changed, entry.Size));
        // The file that didn't change was only asked about.
        Assert.Equal("bytes=0-0", Assert.Single(site.AskedFor("/same.zip")).Range);
        await h.Shutdown();
    }

    [Fact]
    public async Task Exploring_again_only_notes_what_changed()
    {
        await using var site = await SiteTestServer.StartAsync();
        site.Page(Site + "/", "<a href=/a.zip>a</a><a href=/b.zip>b</a>");
        site.File(Site + "/a.zip", 3000);
        site.File(Site + "/b.zip", 3000);
        var h = New(site);
        h.Start();
        await h.Finished();
        var before = File.ReadAllBytes(Saved("a.zip"));
        // The user doesn't want this one kept up to date.
        h.Ui.Run(() => h.Project.Entries.Single(e => e.Url == Site + "/b.zip").Checked = false);

        var a = site.File(Site + "/a.zip", 4000);
        site.File(Site + "/b.zip", 4000);
        h.Start(GrabberRunMode.ExploreOnly);
        await h.Finished();
        Assert.Equal(before, File.ReadAllBytes(Saved("a.zip")));
        var entry = h.Project.Entries.Single(e => e.Url == Site + "/a.zip");
        Assert.Equal((GrabberFileState.None, true), (entry.FileState, entry.Changed));

        h.Start(GrabberRunMode.DownloadChecked);
        await h.Finished();
        Assert.Equal(a.Body, File.ReadAllBytes(Saved("a.zip")));
        Assert.Equal(3000, new FileInfo(Saved("b.zip")).Length);
        await h.Shutdown();
    }

    [Fact]
    public async Task Changed_file_left_unchecked_is_not_downloaded()
    {
        await using var site = await SiteTestServer.StartAsync();
        WriteSite(site);
        var h = New(site);
        h.Start();
        await h.Finished();
        h.Ui.Run(() => h.Project.Entries.Single(e => e.Url == Site + "/a.zip").Checked = false);

        site.File(Site + "/a.zip", 4000);
        site.ClearRequests();
        h.Start();
        await h.Finished();
        Assert.Equal(3000, new FileInfo(Saved("a.zip")).Length);
        Assert.Equal("bytes=0-0", Assert.Single(site.AskedFor("/a.zip")).Range);
        await h.Shutdown();
    }

    [Fact]
    public async Task Updating_downloads_what_is_new_even_when_files_wait_to_be_checked()
    {
        await using var site = await SiteTestServer.StartAsync();
        WriteSite(site);
        var h = New(site, s => s.StartAtOnce = false);
        h.Start(GrabberRunMode.ExploreOnly);
        await h.Finished();
        h.Ui.Run(() => { foreach (var e in h.Project.Entries) e.Checked = true; });
        h.Start(GrabberRunMode.DownloadChecked);
        await h.Finished();

        site.Page(Site + "/b.html", "<p>b <a href=new.zip>new</a>");
        site.File(Site + "/new.zip", 2000);
        h.Start(GrabberRunMode.ExploreAndDownload);
        await h.Finished();
        Assert.False(File.Exists(Saved("new.zip")));

        site.Page(Site + "/a.html", "<p>a <a href=newer.zip>newer</a>");
        site.File(Site + "/newer.zip", 2000);
        h.Start(GrabberRunMode.Sync);
        await h.Finished();
        Assert.True(File.Exists(Saved("newer.zip")));
        await h.Shutdown();
    }

    [Fact]
    public async Task Rerun_keeps_files_the_server_lost()
    {
        await using var site = await SiteTestServer.StartAsync();
        WriteSite(site);
        var h = New(site);
        h.Start();
        await h.Finished();

        site.Remove(Site + "/a.zip");
        site.Remove(Site + "/b.html");
        h.Start();
        await h.Finished();
        Assert.True(File.Exists(Saved("a.zip")));
        Assert.True(File.Exists(Saved("b.html")));
        Assert.All(h.Project.Entries.Where(e => e.Url is Site + "/a.zip" or Site + "/b.html"),
            e => Assert.Equal((GrabberState.Failed, GrabberFileState.Complete), (e.State, e.FileState)));
        // Still offline, as the files are still there.
        var index = File.ReadAllText(Saved("index.html"));
        Assert.Contains("href=a.zip", index);
        Assert.Contains("href=b.html", index);
        await h.Shutdown();
    }

    [Fact]
    public async Task Sync_repeats_and_overwrites()
    {
        await using var site = await SiteTestServer.StartAsync();
        WriteSite(site);
        var h = await Scheduled(site, GrabberRunMode.Sync, schedule: s => s.RepeatMinutes = 30);
        Assert.Equal(QueueKind.Sync, h.Queue.Kind);
        Assert.True(h.Project.Settings.Overwrite);
        var first = Day.AddHours(10).AddSeconds(30);
        h = await RestartedAndTicked(h, site, first);
        await h.Finished(first);
        Assert.Equal(site.Body(Site + "/a.zip"), File.ReadAllBytes(Saved("a.zip")));

        var changed = site.File(Site + "/a.zip", 7000);
        File.WriteAllText(Saved("new.zip"), "not the site's");
        site.Page(Site + "/b.html", "<p>b <a href=new.zip>new</a>");
        site.File(Site + "/new.zip", 2000);
        var again = first.AddMinutes(31);
        h.Ui.Run(() => h.Manager.Tick(again));
        Assert.True(h.Queue.IsRunning);
        await h.Finished(again);
        Assert.Equal(changed.Body, File.ReadAllBytes(Saved("a.zip")));
        // With overwriting on, a file already there is replaced rather than numbered.
        Assert.Equal(site.Body(Site + "/new.zip"), File.ReadAllBytes(Saved("new.zip")));
        Assert.False(File.Exists(Saved("new_2.zip")));
        await h.Shutdown();
    }

    [Fact]
    public async Task Crawl_counts_toward_the_download_limit()
    {
        await using var site = await SiteTestServer.StartAsync();
        site.Page(Site + "/", "<p>" + new string('x', 300 * 1024));
        var h = New(site);
        h.Ui.Run(() =>
        {
            h.Manager.State.Settings.QuotaEnabled = true;
            h.Manager.State.Settings.QuotaMB = 1000;
            h.Manager.State.Settings.QuotaHours = 1;
        });
        h.Start(GrabberRunMode.ExploreOnly);
        await h.Finished();
        h.Ui.Run(() => h.Manager.Tick(DateTime.Now));
        Assert.InRange(h.On(() => h.Manager.State.Quota.Bytes), 300 * 1024, 301 * 1024);
        await h.Shutdown();
    }

    [Fact]
    public async Task Crawl_holds_while_the_limit_holds()
    {
        await using var site = await SiteTestServer.StartAsync();
        WriteSite(site);
        var h = New(site);
        h.Ui.Run(() =>
        {
            var s = h.Manager.State.Settings;
            (s.QuotaEnabled, s.QuotaMB, s.QuotaHours) = (true, 1, 1);
            h.Manager.State.Quota.Add(2 * 1024 * 1024, DateTime.Now, 1);
            h.Manager.Tick(DateTime.Now);
            Assert.NotNull(h.Manager.LimitHoldsUntil);
        });
        h.Start(GrabberRunMode.ExploreOnly);
        await Task.Delay(500);
        h.Ui.Run(() => h.Manager.Tick(DateTime.Now));
        Assert.Empty(site.Requests);
        Assert.True(h.Queue.IsRunning);

        h.Ui.Run(() => h.Manager.State.Settings.QuotaEnabled = false);
        await h.Finished();
        Assert.Single(site.AskedFor("/b.html"));
        await h.Shutdown();
    }

    [Fact]
    public async Task Crawl_obeys_the_speed_limit()
    {
        await using var site = await SiteTestServer.StartAsync();
        site.Page(Site + "/", "<p>" + new string('x', 1024 * 1024));
        var h = New(site);
        h.Ui.Run(() =>
        {
            h.Manager.State.Settings.SpeedLimitKBps = 1024;
            h.Manager.ApplySettings();
        });
        var clock = System.Diagnostics.Stopwatch.StartNew();
        h.Start(GrabberRunMode.ExploreOnly);
        await h.Finished();
        Assert.True(clock.Elapsed > TimeSpan.FromMilliseconds(700), $"took {clock.Elapsed}");
        await h.Shutdown();
    }

    [Fact]
    public async Task No_space_stops_with_a_message()
    {
        await using var site = await SiteTestServer.StartAsync();
        WriteSite(site);
        var h = New(site);
        var finished = new List<DownloadQueue>();
        h.Ui.Run(() =>
        {
            h.Manager.QueueFinished += finished.Add;
            h.Run.FreeSpace = _ => 10;
        });
        h.Start();
        await h.Finished();
        var error = h.On(() => h.Run.Error);
        Assert.NotNull(error);
        Assert.StartsWith("Not enough disk space on ", error);
        Assert.Contains("(needs ", error);
        Assert.False(Directory.Exists(SaveFolder));
        Assert.Empty(finished);
        // The start page waits for the next run, which finds room.
        Assert.Equal(GrabberState.Waiting, h.Project.Entries.Single(e => e.Url == Site + "/").State);
        h.Ui.Run(() => h.Run.FreeSpace = _ => long.MaxValue);
        h.Start();
        await h.Finished();
        Assert.Null(h.On(() => h.Run.Error));
        Assert.True(File.Exists(Saved("a.zip")));
        await h.Shutdown();
    }
}
