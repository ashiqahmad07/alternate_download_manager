using System.Diagnostics;
using System.Text;
using ADM.Core;

namespace ADM.Core.Tests;

public sealed class GrabberCrawlerTests : IDisposable
{
    const string Site = "http://www.site.test";
    const string Other = "http://other.test";

    readonly string _dir = Path.Combine(Path.GetTempPath(), "adm-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    static RetryPolicy Quick(TimeSpan? window = null) => new()
    {
        FirstBackoff = TimeSpan.FromMilliseconds(10),
        MaxBackoff = TimeSpan.FromMilliseconds(50),
        ReconnectWindow = window ?? TimeSpan.FromSeconds(30),
    };

    GrabberProject NewProject(string start, Action<GrabberSettings>? change = null)
    {
        var settings = new GrabberSettings { StartUrl = start, ThisSiteOnly = false };
        change?.Invoke(settings);
        return new GrabberProject { Name = "test", Settings = settings };
    }

    GrabberCrawler NewCrawler(SiteTestServer site, GrabberProject project, RetryPolicy? retry = null, Settings? settings = null) =>
        new(project, _dir, settings ?? new Settings { Proxy = site.Proxy }, new RateLimiter()) { Retry = retry ?? Quick() };

    async Task<GrabberProject> Crawl(SiteTestServer site, string start, Action<GrabberSettings>? change = null)
    {
        var project = NewProject(start, change);
        await NewCrawler(site, project).RunAsync(CancellationToken.None);
        return project;
    }

    // What the crawl read as pages, and what it lists, each as the addresses end up.
    static List<string> Explored(GrabberProject project) =>
        project.Entries.Where(e => e.Kind == GrabberKind.Page && e.State == GrabberState.Done && e.Read).Select(e => e.Url).Order().ToList();

    static List<string> Listed(GrabberProject project) =>
        project.Entries.Where(e => e.Matched).Select(e => e.Url).Order().ToList();

    static List<string> Addresses(string host, params string[] paths) => paths.Select(p => host + p).Order().ToList();

    // The site in IDM's picture of link levels. Pages 1 to 8 are on this site; 9, 10 and 11 are the first on other sites.
    static void WriteDiagramSite(SiteTestServer site)
    {
        site.Page(Site + "/", "<a href=/1.html>1</a><a href=/2.html>2</a><a href=http://nine.test/9.html>9</a>");
        site.Page(Site + "/1.html", "<a href=/3.html>3</a><a href=/4.html>4</a>");
        site.Page(Site + "/2.html", "<a href=/5.html>5</a>");
        site.Page(Site + "/3.html", "<a href=http://ten.test/10.html>10</a>");
        site.Page(Site + "/4.html", "<a href=/6.html>6</a><a href=/7.html>7</a>");
        site.Page(Site + "/5.html", "<a href=/8.html>8</a>");
        site.Page(Site + "/6.html", "<a href=http://eleven.test/11.html>11</a><a href=/deep6.html>deep</a>");
        site.Page(Site + "/7.html", "<a href=/deep7.html>deep</a>");
        site.Page(Site + "/8.html", "<a href=/deep8.html>deep</a>");
        foreach (var deep in new[] { "/deep6.html", "/deep7.html", "/deep8.html" })
            site.Page(Site + deep, "<a href=/deeper.html>deeper</a>");
        site.Page(Site + "/deeper.html", "deeper");
        site.Page("http://nine.test/9.html", "<a href=http://far.test/far9.html>far</a>");
        site.Page("http://ten.test/10.html", "<a href=http://far.test/far10.html>far</a>");
        site.Page("http://eleven.test/11.html", "<a href=http://far.test/far11.html>far</a>");
        site.Page("http://far.test/far9.html", "far");
        site.Page("http://far.test/far10.html", "far");
        site.Page("http://far.test/far11.html", "far");
    }

    [Fact]
    public async Task Level_0_explores_only_the_start_page()
    {
        await using var site = await SiteTestServer.StartAsync();
        WriteDiagramSite(site);
        var project = await Crawl(site, Site + "/", s => { s.SiteLevels = 0; s.OtherLevels = 0; s.Include = "*.zip"; });
        Assert.Equal(new[] { Site + "/" }, Explored(project));
        Assert.Single(site.Requests);
    }

    [Fact]
    public async Task Levels_match_the_diagram()
    {
        await using var site = await SiteTestServer.StartAsync();
        WriteDiagramSite(site);
        var project = await Crawl(site, Site + "/", s => { s.SiteLevels = 3; s.OtherLevels = 1; s.Include = "*.zip"; });
        var expected = Addresses(Site, "/", "/1.html", "/2.html", "/3.html", "/4.html", "/5.html", "/6.html", "/7.html", "/8.html")
            .Concat(new[] { "http://nine.test/9.html", "http://ten.test/10.html", "http://eleven.test/11.html" }).Order();
        Assert.Equal(expected, Explored(project));
    }

    [Fact]
    public async Task One_level_and_none_on_other_sites_matches_the_diagram()
    {
        await using var site = await SiteTestServer.StartAsync();
        WriteDiagramSite(site);
        var project = await Crawl(site, Site + "/", s => { s.SiteLevels = 1; s.OtherLevels = 0; s.Include = "*.zip"; });
        Assert.Equal(Addresses(Site, "/", "/1.html", "/2.html"), Explored(project));
    }

    [Fact]
    public async Task Two_levels_on_other_sites_go_one_further()
    {
        await using var site = await SiteTestServer.StartAsync();
        WriteDiagramSite(site);
        var project = await Crawl(site, Site + "/", s => { s.SiteLevels = 0; s.OtherLevels = 2; s.Include = "*.zip"; });
        Assert.Equal(new[] { Site + "/", "http://far.test/far9.html", "http://nine.test/9.html" }.Order(), Explored(project));
    }

    [Fact]
    public async Task Whole_site_finds_every_page_and_none_elsewhere()
    {
        await using var site = await SiteTestServer.StartAsync();
        WriteDiagramSite(site);
        var project = await Crawl(site, Site + "/", s => { s.WholeSite = true; s.Include = "*.zip"; });
        Assert.Equal(Addresses(Site, "/", "/1.html", "/2.html", "/3.html", "/4.html", "/5.html", "/6.html", "/7.html", "/8.html",
            "/deep6.html", "/deep7.html", "/deep8.html", "/deeper.html"), Explored(project));
    }

    [Fact]
    public async Task Whole_site_goes_nowhere_else_whatever_the_other_levels()
    {
        await using var site = await SiteTestServer.StartAsync();
        WriteDiagramSite(site);
        var project = await Crawl(site, Site + "/", s => { s.WholeSite = true; s.OtherLevels = 3; s.Include = "*.zip"; });
        Assert.All(Explored(project), address => Assert.StartsWith(Site, address));
    }

    [Fact]
    public async Task Shorter_route_found_later_brings_a_page_in()
    {
        await using var site = await SiteTestServer.StartAsync();
        // /b.html is first seen two links away, from /a.html, which /slow.html beats it to. It is also one link away.
        site.Page(Site + "/", "<a href=/a.html>a</a><a href=/hold.html>hold</a>");
        site.Page(Site + "/a.html", "<a href=/b.html>b</a>");
        site.Page(Site + "/hold.html", "<a href=/a.html>a</a><a href=/b.html>b</a>");
        site.Page(Site + "/b.html", "<a href=/c.html>c</a>");
        site.Page(Site + "/c.html", "c");
        var project = await Crawl(site, Site + "/", s => { s.SiteLevels = 2; s.Include = "*.zip"; });
        Assert.Equal(Addresses(Site, "/", "/a.html", "/b.html", "/hold.html"), Explored(project));

        // Other sites' levels are counted apart: a page reached with fewer hops away gets explored after all.
        await using var two = await SiteTestServer.StartAsync();
        two.Page(Site + "/", "<a href=/1.html>1</a><a href=http://x.test/x.html>x</a>");
        two.Page(Site + "/1.html", "<a href=http://x.test/y.html>y</a>");
        two.Page("http://x.test/x.html", "<a href=http://x.test/y.html>y</a>");
        two.Page("http://x.test/y.html", "<a href=http://x.test/z.html>z</a>");
        two.Page("http://x.test/z.html", "z");
        var other = await Crawl(two, Site + "/", s => { s.SiteLevels = 1; s.OtherLevels = 1; s.Include = "*.zip"; });
        Assert.Contains("http://x.test/y.html", Explored(other));
        Assert.DoesNotContain("http://x.test/z.html", Explored(other));
    }

    [Fact]
    public async Task Better_route_to_a_page_already_read_follows_its_links()
    {
        await using var site = await SiteTestServer.StartAsync();
        // x.test/y.html is first read two hops away, from x.test/x.html, which leaves z.html a hop too far. Then /2.html
        // shows it is one hop away, so its links count for more, and they come from the body it kept.
        site.Page(Site + "/", "<a href=http://x.test/x.html>x</a><a href=/1.html>1</a>");
        site.Page("http://x.test/x.html", "<a href=http://x.test/y.html>y</a>");
        site.Page(Site + "/1.html", "<a href=/2.html>2</a>");
        site.Page(Site + "/2.html", "<a href=http://x.test/y.html>y</a>");
        site.Page("http://x.test/y.html", "<a href=http://x.test/z.html>z</a>");
        site.Page("http://x.test/z.html", "z");
        var project = await Crawl(site, Site + "/", s => { s.SiteLevels = 2; s.OtherLevels = 2; s.ExploreAtOnce = 1; s.Include = "*.zip"; });
        Assert.Contains("http://x.test/z.html", Explored(project));
        Assert.Single(site.AskedFor("/y.html"));
    }

    [Fact]
    public async Task Files_are_not_held_back_by_levels()
    {
        await using var site = await SiteTestServer.StartAsync();
        site.Page(Site + "/", "<a href=/a.zip>a</a><img src=/b.png><a href=/1.html>1</a>");
        site.Page(Site + "/1.html", "<a href=/c.zip>c</a>");
        site.File(Site + "/a.zip");
        site.File(Site + "/b.png");
        site.File(Site + "/c.zip");
        var project = await Crawl(site, Site + "/", s => { s.SiteLevels = 0; s.Include = "*.zip,*.png"; });
        Assert.Equal(Addresses(Site, "/a.zip", "/b.png"), Listed(project));
    }

    [Fact]
    public async Task Linked_page_past_the_levels_is_listed_but_not_read()
    {
        await using var site = await SiteTestServer.StartAsync();
        site.Page(Site + "/", "<a href=/1.html>1</a>");
        site.Page(Site + "/1.html", "<a href=/2.html>2</a>");
        var project = await Crawl(site, Site + "/", s => { s.SiteLevels = 0; s.Include = "*"; });
        Assert.Equal(Addresses(Site, "/", "/1.html"), Listed(project));
        Assert.Equal(new[] { Site + "/" }, Explored(project));
        Assert.Empty(site.AskedFor("/2.html"));
    }

    [Fact]
    public async Task No_parents_stays_under_the_start_folder()
    {
        await using var site = await SiteTestServer.StartAsync();
        site.Page(Site + "/docs/start.html", "<a href=/index.html>up</a><a href=/other/o.html>across</a><a href=page.html>in</a><a href=sub/s.html>under</a><a href=../up.zip>z</a><img src=/images/i.png>");
        site.Page(Site + "/index.html", "up");
        site.Page(Site + "/other/o.html", "across");
        site.Page(Site + "/docs/page.html", "in");
        site.Page(Site + "/docs/sub/s.html", "under");
        site.File(Site + "/up.zip");
        site.File(Site + "/images/i.png");
        var project = await Crawl(site, Site + "/docs/start.html", s => { s.WholeSite = true; s.NoParents = true; s.Include = "*.zip,*.png"; });
        Assert.Equal(Addresses(Site, "/docs/page.html", "/docs/start.html", "/docs/sub/s.html"), Explored(project));
        Assert.Equal(Addresses(Site, "/images/i.png", "/up.zip"), Listed(project));
    }

    [Fact]
    public async Task Main_domain_takes_in_other_hosts()
    {
        await using var site = await SiteTestServer.StartAsync();
        site.Page(Site + "/", "<a href=http://site.test/a.html>a</a><a href=http://cdn.site.test/b.html>b</a><a href=http://a.b.site.test/c.html>c</a><a href=http://other.test/d.html>d</a><a href=http://notsite.test/e.html>e</a>");
        foreach (var address in new[] { "http://site.test/a.html", "http://cdn.site.test/b.html", "http://a.b.site.test/c.html", "http://other.test/d.html", "http://notsite.test/e.html" })
            site.Page(address, "x");
        var without = await Crawl(site, Site + "/", s => { s.SiteLevels = 1; s.Include = "*.zip"; });
        Assert.Equal(new[] { Site + "/" }, Explored(without));
        var with = await Crawl(site, Site + "/", s => { s.SiteLevels = 1; s.MainDomain = true; s.Include = "*.zip"; });
        Assert.Equal(new[] { "http://a.b.site.test/c.html", "http://cdn.site.test/b.html", "http://site.test/a.html", Site + "/" }.Order(), Explored(with));
    }

    [Theory]
    [InlineData("www.example.com", "example.com")]
    [InlineData("example.com", "example.com")]
    [InlineData("a.b.example.com", "example.com")]
    [InlineData("www.example.co.uk", "example.co.uk")]
    [InlineData("a.b.example.co.uk", "example.co.uk")]
    [InlineData("example.co.uk", "example.co.uk")]
    [InlineData("foo.co.uk", "foo.co.uk")]
    [InlineData("www.example.de", "example.de")]
    [InlineData("www.example.com.pk", "example.com.pk")]
    [InlineData("localhost", "localhost")]
    [InlineData("127.0.0.1", "127.0.0.1")]
    [InlineData("[::1]", "[::1]")]
    public void Main_domain_of(string host, string expected) => Assert.Equal(expected, GrabberCrawler.MainDomainOf(host));

    [Fact]
    public async Task Explore_lists_are_obeyed()
    {
        await using var site = await SiteTestServer.StartAsync();
        site.Page(Site + "/start.html", "<a href=/keep/a.html>a</a><a href=/drop/b.html>b</a><a href=/keep/logout.html>c</a>");
        site.Page(Site + "/keep/a.html", "a");
        site.Page(Site + "/drop/b.html", "b");
        site.Page(Site + "/keep/logout.html", "c");
        var project = await Crawl(site, Site + "/start.html", s =>
        {
            s.WholeSite = true;
            s.ExploreOnly = "/keep/*";
            s.ExploreNever = "*logout*";
            s.Include = "*.zip";
        });
        Assert.Equal(Addresses(Site, "/keep/a.html", "/start.html"), Explored(project));
        Assert.Empty(site.AskedFor("/keep/logout.html"));
    }

    [Fact]
    public async Task Masks_and_sizes_choose_the_files()
    {
        await using var site = await SiteTestServer.StartAsync();
        site.Page(Site + "/", "<a href=/small.zip>1</a><a href=/fit.zip>2</a><a href=/big.zip>3</a><a href=/fit.png>4</a><a href=/fit.txt>5</a><a href=/skip.zip>6</a>");
        site.File(Site + "/small.zip", 1024);
        site.File(Site + "/fit.zip", 4096);
        site.File(Site + "/big.zip", 20480);
        site.File(Site + "/fit.png", 4096);
        site.File(Site + "/fit.txt", 4096);
        site.File(Site + "/skip.zip", 4096);
        var project = await Crawl(site, Site + "/", s =>
        {
            s.SiteLevels = 0;
            s.Include = "*.zip,*.png";
            s.Exclude = "skip*";
            s.MinSizeKB = 2;
            s.MaxSizeKB = 8;
        });
        Assert.Equal(Addresses(Site, "/fit.png", "/fit.zip"), Listed(project));
        Assert.Equal(4096, project.Entries.Single(e => e.Url == Site + "/fit.zip").Size);
        // Told apart by name, these need no request.
        Assert.Empty(site.AskedFor("/fit.txt"));
        Assert.Empty(site.AskedFor("/skip.zip"));
    }

    [Fact]
    public async Task Unknown_size_passes_the_size_filters()
    {
        await using var site = await SiteTestServer.StartAsync();
        site.Page(Site + "/", "<a href=ftp://www.site.test/a.zip>a</a>");
        var project = await Crawl(site, Site + "/", s => { s.SiteLevels = 0; s.Include = "*.zip"; s.MinSizeKB = 2; });
        Assert.Equal(new[] { "ftp://www.site.test/a.zip" }, Listed(project));
        Assert.Single(site.Requests);
    }

    [Fact]
    public async Task Size_comes_from_asking_for_the_first_byte()
    {
        await using var site = await SiteTestServer.StartAsync();
        site.Page(Site + "/", "<a href=/a.zip>a</a>");
        site.File(Site + "/a.zip", 5000, "application/zip");
        var project = await Crawl(site, Site + "/", s => { s.SiteLevels = 0; s.Include = "*.zip"; });
        Assert.Equal("bytes=0-0", Assert.Single(site.AskedFor("/a.zip")).Range);
        var entry = project.Entries.Single(e => e.Url == Site + "/a.zip");
        Assert.Equal((5000L, "application/zip", GrabberKind.File, GrabberState.Done), (entry.Size, entry.ContentType, entry.Kind, entry.State));
    }

    [Fact]
    public async Task A_file_by_its_name_is_never_asked_for_whole()
    {
        await using var site = await SiteTestServer.StartAsync();
        site.Page(Site + "/", "<a href=/a.zip>a</a>");
        site.File(Site + "/a.zip", 5000);
        await Crawl(site, Site + "/", s => { s.SiteLevels = 3; s.Include = "*"; });
        Assert.Equal("bytes=0-0", Assert.Single(site.AskedFor("/a.zip")).Range);
    }

    [Fact]
    public async Task A_name_that_can_make_any_file_is_asked_past_the_levels()
    {
        await using var site = await SiteTestServer.StartAsync();
        site.Page(Site + "/", "<a href=/get.php?id=1>1</a>");
        site.File(Site + "/get.php?id=1", 2000, "application/zip").Disposition = "attachment; filename=\"wanted.zip\"";
        var project = await Crawl(site, Site + "/", s => { s.SiteLevels = 0; s.Include = "*.zip"; });
        Assert.Equal(new[] { Site + "/get.php?id=1" }, Listed(project));
    }

    [Fact]
    public async Task Validators_are_kept()
    {
        await using var site = await SiteTestServer.StartAsync();
        var modified = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var page = site.Page(Site + "/", "<a href=/a.zip>a</a>");
        page.ETag = "\"page\"";
        page.LastModified = modified;
        site.File(Site + "/a.zip").ETag = "\"file\"";
        var project = await Crawl(site, Site + "/", s => { s.SiteLevels = 0; s.Include = "*.zip"; });
        var start = project.Entries.Single(e => e.Url == Site + "/");
        Assert.Equal(("\"page\"", modified.ToString("R")), (start.ETag, start.LastModified));
        Assert.Equal("\"file\"", project.Entries.Single(e => e.Matched).ETag);
    }

    [Fact]
    public async Task This_site_only_asks_nothing_of_other_sites()
    {
        await using var site = await SiteTestServer.StartAsync();
        site.Page(Site + "/", "<a href=/a.zip>a</a><a href=http://other.test/b.zip>b</a><img src=http://other.test/c.png><link rel=stylesheet href=http://other.test/d.css>");
        site.File(Site + "/a.zip");
        var project = await Crawl(site, Site + "/", s => { s.SiteLevels = 0; s.ThisSiteOnly = true; s.Include = "*"; });
        Assert.Equal(Addresses(Site, "/", "/a.zip"), Listed(project));
        Assert.DoesNotContain(site.Requests, r => r.Host == "other.test");
    }

    [Fact]
    public async Task Other_sites_files_are_listed_without_the_setting()
    {
        await using var site = await SiteTestServer.StartAsync();
        site.Page(Site + "/", "<a href=http://other.test/b.zip>b</a>");
        site.File(Other + "/b.zip");
        var project = await Crawl(site, Site + "/", s => { s.SiteLevels = 0; s.Include = "*.zip"; });
        Assert.Equal(new[] { Other + "/b.zip" }, Listed(project));
    }

    [Fact]
    public async Task File_lists_are_obeyed()
    {
        await using var site = await SiteTestServer.StartAsync();
        site.Page(Site + "/", "<a href=/keep/a.zip>a</a><a href=/drop/b.zip>b</a><a href=/keep/old/c.zip>c</a>");
        site.File(Site + "/keep/a.zip");
        site.File(Site + "/drop/b.zip");
        site.File(Site + "/keep/old/c.zip");
        var project = await Crawl(site, Site + "/", s => { s.SiteLevels = 0; s.Include = "*.zip"; s.FilesOnlyIn = "/keep/*"; s.FilesNeverIn = "*/old/*"; });
        Assert.Equal(new[] { Site + "/keep/a.zip" }, Listed(project));
    }

    [Fact]
    public async Task Hides_copies_with_the_same_name_and_size()
    {
        await using var site = await SiteTestServer.StartAsync();
        site.Page(Site + "/", "<a href=/one/a.zip>1</a><a href=/two/a.zip>2</a><a href=/three/a.zip>3</a>");
        site.File(Site + "/one/a.zip", 2000);
        site.File(Site + "/two/a.zip", 2000);
        site.File(Site + "/three/a.zip", 3000);
        var project = await Crawl(site, Site + "/", s => { s.SiteLevels = 0; s.Include = "*.zip"; s.HideCopies = true; s.KeepSubfolders = false; });
        Assert.Equal(2, Listed(project).Count);
        Assert.Contains(Site + "/three/a.zip", Listed(project));
    }

    [Fact]
    public async Task Copies_are_kept_with_the_sites_folders()
    {
        await using var site = await SiteTestServer.StartAsync();
        site.Page(Site + "/", "<a href=/one/a.zip>1</a><a href=/two/a.zip>2</a>");
        site.File(Site + "/one/a.zip", 2000);
        site.File(Site + "/two/a.zip", 2000);
        var project = await Crawl(site, Site + "/", s => { s.SiteLevels = 0; s.Include = "*.zip"; s.HideCopies = true; s.KeepSubfolders = true; });
        Assert.Equal(2, Listed(project).Count);
    }

    [Fact]
    public async Task Name_from_the_answer_decides_the_mask()
    {
        await using var site = await SiteTestServer.StartAsync();
        site.Page(Site + "/", "<a href=/get.php?id=1>1</a><a href=/get.php?id=2>2</a>");
        site.File(Site + "/get.php?id=1", 2000, "application/zip").Disposition = "attachment; filename=\"wanted.zip\"";
        site.File(Site + "/get.php?id=2", 2000, "application/zip").Disposition = "attachment; filename=\"other.rar\"";
        var project = await Crawl(site, Site + "/", s => { s.SiteLevels = 1; s.Include = "*.zip"; });
        Assert.Equal(new[] { Site + "/get.php?id=1" }, Listed(project));
        Assert.Equal("wanted.zip", project.Entries.Single(e => e.Matched).FileName);
    }

    [Fact]
    public async Task Link_without_an_extension_can_be_a_file()
    {
        await using var site = await SiteTestServer.StartAsync();
        site.Page(Site + "/", "<a href=/download/5>file</a><a href=/about>page</a>");
        site.File(Site + "/download/5", 3000, "application/pdf");
        site.Page(Site + "/about", "<a href=/in-about.zip>z</a>");
        site.File(Site + "/in-about.zip");
        var project = await Crawl(site, Site + "/", s => { s.SiteLevels = 1; s.Include = "*.pdf,*.zip"; });
        Assert.Equal(Addresses(Site, "/download/5", "/in-about.zip"), Listed(project));
        var file = project.Entries.Single(e => e.Url == Site + "/download/5");
        Assert.Equal((GrabberKind.File, 3000L), (file.Kind, file.Size));
        Assert.Equal(GrabberKind.Page, project.Entries.Single(e => e.Url == Site + "/about").Kind);
    }

    [Fact]
    public async Task Same_file_linked_twice_is_one_entry()
    {
        await using var site = await SiteTestServer.StartAsync();
        site.Page(Site + "/", "<a href=/a.zip>1</a><a href=/a.zip#part>2</a><a href='http://WWW.SITE.TEST:80/a.zip'>3</a><a href=./x/../a.zip>4</a>");
        site.File(Site + "/a.zip");
        var project = await Crawl(site, Site + "/", s => { s.SiteLevels = 0; s.Include = "*.zip"; });
        Assert.Equal(new[] { Site + "/a.zip" }, Listed(project));
        Assert.Single(site.AskedFor("/a.zip"));
    }

    [Fact]
    public async Task Link_text_and_referrer_are_kept()
    {
        await using var site = await SiteTestServer.StartAsync();
        site.Page(Site + "/page.html", "<a href=/a.zip>Big archive</a>");
        site.File(Site + "/a.zip");
        var project = await Crawl(site, Site + "/page.html", s => { s.SiteLevels = 0; s.Include = "*.zip"; });
        var entry = project.Entries.Single(e => e.Matched);
        Assert.Equal(("Big archive", Site + "/page.html"), (entry.LinkText, entry.Referrer));
        Assert.Equal(Site + "/page.html", Assert.Single(site.AskedFor("/a.zip")).Referer);
    }

    [Fact]
    public async Task Referrer_to_another_site_is_only_its_origin()
    {
        await using var site = await SiteTestServer.StartAsync();
        site.Page(Site + "/secret/page.html?token=1", "<a href=http://other.test/a.zip>a</a>");
        site.File(Other + "/a.zip");
        await Crawl(site, Site + "/secret/page.html?token=1", s => { s.SiteLevels = 0; s.Include = "*.zip"; });
        Assert.Equal(Site + "/", Assert.Single(site.AskedFor("/a.zip")).Referer);
    }

    [Fact]
    public async Task Non_ascii_links_in_a_stylesheet_and_a_script_are_read_by_their_encoding()
    {
        await using var site = await SiteTestServer.StartAsync();
        site.Page(Site + "/", "<link rel=stylesheet href=/main.css><script src=/main.js></script>");
        site.Put(Site + "/main.css", System.Text.Encoding.UTF8.GetBytes("a{background:url(/café.png)}"), "text/css");
        site.Put(Site + "/main.js", System.Text.Encoding.UTF8.GetBytes("var a = '/thé.png';"), "text/javascript");
        var project = await Crawl(site, Site + "/", s => s.SiteLevels = 0);
        Assert.Contains(project.Entries, e => e.Url == Site + "/caf%C3%A9.png");
        Assert.Contains(project.Entries, e => e.Url == Site + "/th%C3%A9.png");
    }

    [Fact]
    public async Task Stylesheet_images_are_found()
    {
        await using var site = await SiteTestServer.StartAsync();
        site.Page(Site + "/", "<link rel=stylesheet href=/css/main.css>");
        site.Put(Site + "/css/main.css", "@import 'more.css'; a{background:url(../img/a.png)}"u8.ToArray(), "text/css");
        site.Put(Site + "/css/more.css", "b{background:url(b.png)}"u8.ToArray(), "text/css");
        site.File(Site + "/img/a.png");
        site.File(Site + "/css/b.png");
        var project = await Crawl(site, Site + "/", s => { s.SiteLevels = 0; s.Include = "*.png"; });
        Assert.Equal(Addresses(Site, "/css/b.png", "/img/a.png"), Listed(project));
        Assert.Equal(GrabberKind.Style, project.Entries.Single(e => e.Url == Site + "/css/more.css").Kind);
    }

    [Fact]
    public async Task Frames_belong_to_their_page()
    {
        await using var site = await SiteTestServer.StartAsync();
        site.Page(Site + "/", "<frameset><frame src=/left.html></frameset>");
        site.Page(Site + "/left.html", "<a href=/a.zip>a</a>");
        site.File(Site + "/a.zip");
        var project = await Crawl(site, Site + "/", s => { s.SiteLevels = 0; s.Include = "*.zip"; });
        Assert.Equal(new[] { Site + "/a.zip" }, Listed(project));
    }

    [Fact]
    public async Task Frame_on_another_site_counts_as_another_site()
    {
        await using var site = await SiteTestServer.StartAsync();
        site.Page(Site + "/", "<iframe src=http://ads.test/ad.html></iframe>");
        site.Page("http://ads.test/ad.html", "<a href=/a.zip>a</a>");
        await Crawl(site, Site + "/", s => { s.SiteLevels = 3; s.OtherLevels = 0; s.Include = "*.zip"; });
        Assert.Empty(site.AskedFor("/ad.html"));
    }

    [Fact]
    public async Task Script_addresses_are_found()
    {
        await using var site = await SiteTestServer.StartAsync();
        site.Page(Site + "/", "<script src=/app.js></script><script>var a='/inline.zip'</script>");
        site.Put(Site + "/app.js", "var b='/from-file.zip', c='/more.html';"u8.ToArray(), "application/javascript");
        site.File(Site + "/inline.zip");
        site.File(Site + "/from-file.zip");
        site.Page(Site + "/more.html", "<a href=/behind-more.zip>z</a>");
        site.File(Site + "/behind-more.zip");
        var project = await Crawl(site, Site + "/", s => { s.SiteLevels = 1; s.Include = "*.zip"; });
        Assert.Equal(Addresses(Site, "/behind-more.zip", "/from-file.zip", "/inline.zip"), Listed(project));
    }

    [Fact]
    public async Task Script_on_another_site_is_not_read()
    {
        await using var site = await SiteTestServer.StartAsync();
        site.Page(Site + "/", "<script src=http://other.test/lib.js></script>");
        site.Put(Other + "/lib.js", "var a='http://other.test/b.zip'"u8.ToArray(), "application/javascript");
        await Crawl(site, Site + "/", s => { s.SiteLevels = 3; s.OtherLevels = 3; s.Include = "*.zip"; });
        Assert.Empty(site.AskedFor("/lib.js"));
    }

    [Fact]
    public async Task Links_are_taken_relative_to_base()
    {
        await using var site = await SiteTestServer.StartAsync();
        site.Page(Site + "/page.html", "<base href=/files/><a href=a.zip>a</a><base href=/ignored/><a href=b.zip>b</a>");
        site.File(Site + "/files/a.zip");
        site.File(Site + "/files/b.zip");
        var project = await Crawl(site, Site + "/page.html", s => { s.SiteLevels = 0; s.Include = "*.zip"; });
        Assert.Equal(Addresses(Site, "/files/a.zip", "/files/b.zip"), Listed(project));
    }

    [Fact]
    public async Task Relative_links_start_where_redirects_ended()
    {
        await using var site = await SiteTestServer.StartAsync();
        site.Redirect(Site + "/moved.html", Site + "/new/page.html");
        site.Page(Site + "/new/page.html", "<a href=a.zip>a</a>");
        site.File(Site + "/new/a.zip");
        var project = await Crawl(site, Site + "/moved.html", s => { s.SiteLevels = 0; s.Include = "*.zip"; });
        Assert.Equal(new[] { Site + "/new/a.zip" }, Listed(project));
    }

    [Fact]
    public async Task Start_page_redirect_moves_the_site()
    {
        await using var site = await SiteTestServer.StartAsync();
        site.Redirect("http://site.test/", Site + "/home.html");
        site.Page(Site + "/home.html", "<a href=/1.html>1</a><a href=http://site.test/2.html>2</a><a href=http://other.test/3.html>3</a>");
        site.Page(Site + "/1.html", "one");
        site.Page("http://site.test/2.html", "two");
        site.Page(Other + "/3.html", "three");
        var project = await Crawl(site, "http://site.test/", s => { s.SiteLevels = 1; s.Include = "*.zip"; });
        Assert.Equal(new[] { Site + "/1.html", "http://site.test/", "http://site.test/2.html" }.Order(), Explored(project));
        Assert.Equal(new[] { "site.test", "www.site.test" }, project.SiteHosts.Order());
    }

    [Fact]
    public async Task Redirect_off_the_site_is_another_site()
    {
        await using var site = await SiteTestServer.StartAsync();
        site.Page(Site + "/", "<a href=/out.html>out</a>");
        site.Redirect(Site + "/out.html", Other + "/landed.html");
        site.Page(Other + "/landed.html", "<a href=/behind.zip>z</a>");
        site.File(Other + "/behind.zip");
        var none = await Crawl(site, Site + "/", s => { s.SiteLevels = 1; s.OtherLevels = 0; s.Include = "*.zip"; });
        Assert.Empty(Listed(none));
        var one = await Crawl(site, Site + "/", s => { s.SiteLevels = 1; s.OtherLevels = 1; s.Include = "*.zip"; });
        Assert.Equal(new[] { Other + "/behind.zip" }, Listed(one));
    }

    [Fact]
    public async Task Login_goes_only_to_the_start_site()
    {
        await using var site = await SiteTestServer.StartAsync();
        site.Page(Site + "/", "<a href=http://other.test/page.html>x</a><a href=/a.zip>a</a>").Login = "user:pass";
        site.File(Site + "/a.zip").Login = "user:pass";
        site.Page(Other + "/page.html", "x").Login = "user:pass";
        var project = await Crawl(site, Site + "/", s =>
        {
            s.SiteLevels = 1;
            s.OtherLevels = 1;
            s.Include = "*.zip";
            s.UserName = "user";
            s.ProtectedPassword = Secrets.Protect("pass");
        });
        Assert.Equal(new[] { Site + "/a.zip" }, Listed(project));
        Assert.DoesNotContain(site.Requests, r => r.Host == "other.test" && r.Authorization.Length > 0);
        Assert.Contains(site.Requests, r => r.Host == "www.site.test" && r.Authorization.Length > 0);
    }

    [Fact]
    public async Task Wrong_login_fails_the_start_page_without_retrying()
    {
        await using var site = await SiteTestServer.StartAsync();
        site.Page(Site + "/", "x").Login = "user:pass";
        var project = await Crawl(site, Site + "/", s => { s.UserName = "user"; s.ProtectedPassword = Secrets.Protect("wrong"); });
        var entry = Assert.Single(project.Entries);
        Assert.Equal(GrabberState.Failed, entry.State);
        Assert.Contains("401", entry.Error);
        Assert.True(site.AskedFor("/").Count <= 2, "only a challenge and its answer");
    }

    [Fact]
    public async Task Cookies_go_only_to_the_start_origin()
    {
        await using var site = await SiteTestServer.StartAsync();
        site.Page(Site + "/", "<a href=http://other.test/page.html>o</a><a href=http://cdn.site.test/page2.html>c</a><a href=/page3.html>s</a>");
        site.Page(Other + "/page.html", "x");
        site.Page("http://cdn.site.test/page2.html", "x");
        site.Page(Site + "/page3.html", "<a href=/moved.html>m</a>");
        site.Redirect(Site + "/moved.html", Other + "/elsewhere.html");
        site.Page(Other + "/elsewhere.html", "x");
        await Crawl(site, Site + "/", s =>
        {
            s.SiteLevels = 3;
            s.OtherLevels = 3;
            s.Include = "*.zip";
            s.ProtectedCookies = Secrets.Protect("session=1");
        });
        Assert.All(site.Requests.Where(r => r.Host != "www.site.test"), r => Assert.Equal("", r.Cookie));
        Assert.All(site.Requests.Where(r => r.Host == "www.site.test"), r => Assert.Equal("session=1", r.Cookie));
        Assert.Contains(site.Requests, r => r.Host == "other.test");
    }

    [Theory]
    [InlineData("http://localhost/x.html")]
    [InlineData("http://LOCALHOST/x.html")]
    [InlineData("http://sub.localhost/x.html")]
    [InlineData("http://127.0.0.1/x.html")]
    [InlineData("http://127.1.2.3/x.html")]
    [InlineData("http://2130706433/x.html")]
    [InlineData("http://0x7f.0.0.1/x.html")]
    [InlineData("http://10.0.0.1/x.html")]
    [InlineData("http://172.16.0.1/x.html")]
    [InlineData("http://172.31.255.1/x.html")]
    [InlineData("http://192.168.1.1/x.html")]
    [InlineData("http://169.254.169.254/x.html")]
    [InlineData("http://100.64.0.1/x.html")]
    [InlineData("http://0.0.0.0/x.html")]
    [InlineData("http://[::1]/x.html")]
    [InlineData("http://[fe80::1]/x.html")]
    [InlineData("http://[fd00::1]/x.html")]
    [InlineData("http://[::ffff:192.168.1.1]/x.html")]
    public void Private_addresses_are_told_apart(string address) => Assert.True(GrabberCrawler.IsPrivate(new Uri(address)));

    [Theory]
    [InlineData("http://www.site.test/")]
    [InlineData("http://8.8.8.8/")]
    [InlineData("http://172.32.0.1/")]
    [InlineData("http://192.169.0.1/")]
    [InlineData("http://[2001:4860:4860::8888]/")]
    [InlineData("http://localhost.example.com/")]
    public void Public_addresses_are_told_apart(string address) => Assert.False(GrabberCrawler.IsPrivate(new Uri(address)));

    [Fact]
    public async Task Public_page_cannot_reach_private_addresses()
    {
        await using var site = await SiteTestServer.StartAsync();
        site.Page(Site + "/", $"<a href=http://127.0.0.1:{site.Port}/private.html>p</a><img src=http://localhost:{site.Port}/private.png><a href=http://192.168.1.1/router.html>r</a>");
        site.Page("/private.html", "x");
        site.File("/private.png");
        var project = await Crawl(site, Site + "/", s => { s.SiteLevels = 3; s.OtherLevels = 3; s.Include = "*"; });
        Assert.Equal(new[] { Site + "/" }, project.Entries.Select(e => e.Url));
        Assert.Single(site.Requests);
    }

    [Fact]
    public async Task Public_redirect_cannot_reach_private_addresses()
    {
        await using var site = await SiteTestServer.StartAsync();
        site.Page(Site + "/", "<a href=/in.html>in</a>");
        site.Redirect(Site + "/in.html", $"http://localhost:{site.Port}/private.html");
        site.Page("/private.html", "x");
        var project = await Crawl(site, Site + "/", s => { s.SiteLevels = 3; s.Include = "*.zip"; });
        Assert.Empty(site.AskedFor("/private.html"));
        var entry = project.Entries.Single(e => e.Url == Site + "/in.html");
        Assert.Equal(GrabberState.Failed, entry.State);
        Assert.Contains("private", entry.Error);
    }

    [Fact]
    public async Task Start_page_on_a_private_address_may_reach_others()
    {
        await using var site = await SiteTestServer.StartAsync();
        site.Page("/", "<a href=/in.html>in</a>");
        site.Page("/in.html", "in");
        var project = NewProject(site.BaseUrl + "/", s => { s.SiteLevels = 1; s.Include = "*.zip"; });
        await NewCrawler(site, project, settings: new Settings()).RunAsync(CancellationToken.None);
        Assert.Equal(2, Explored(project).Count);
    }

    [Theory]
    [InlineData("mailto:a@b.test")]
    [InlineData("javascript:go()")]
    [InlineData("data:text/html,x")]
    [InlineData("file:///c:/windows/win.ini")]
    [InlineData("tel:123")]
    [InlineData("about:blank")]
    [InlineData("#top")]
    [InlineData("")]
    public async Task Other_schemes_are_left_out(string link)
    {
        await using var site = await SiteTestServer.StartAsync();
        site.Page(Site + "/", $"<a href='{link}'>x</a>");
        var project = await Crawl(site, Site + "/", s => { s.WholeSite = true; s.Include = "*"; });
        Assert.Equal(new[] { Site + "/" }, project.Entries.Select(e => e.Url));
    }

    [Fact]
    public async Task Address_holding_a_login_is_left_out()
    {
        await using var site = await SiteTestServer.StartAsync();
        site.Page(Site + "/", "<a href=http://me:secret@www.site.test/a.zip>a</a>");
        var project = await Crawl(site, Site + "/", s => { s.SiteLevels = 0; s.Include = "*"; });
        Assert.DoesNotContain(project.Entries, e => e.Url.Contains("secret"));
    }

    [Fact]
    public async Task Address_over_the_length_limit_is_left_out()
    {
        await using var site = await SiteTestServer.StartAsync();
        var fits = "/" + new string('a', GrabberCrawler.MaxUrlLength - Site.Length - 1);
        var over = "/" + new string('b', GrabberCrawler.MaxUrlLength - Site.Length);
        site.Page(Site + "/", $"<a href='{fits}'>a</a><a href='{over}'>b</a>");
        site.Page(Site + fits, "fits");
        var project = await Crawl(site, Site + "/", s => { s.SiteLevels = 1; s.Include = "*"; });
        Assert.Contains(Site + fits, project.Entries.Select(e => e.Url));
        Assert.DoesNotContain(Site + over, project.Entries.Select(e => e.Url));
    }

    [Fact]
    public async Task Endless_site_stops_at_the_limit()
    {
        await using var site = await SiteTestServer.StartAsync();
        site.Endless = true;
        var project = NewProject(Site + "/", s => { s.WholeSite = true; s.Include = "*"; });
        var crawler = NewCrawler(site, project);
        crawler.EntryLimit = 50;
        await crawler.RunAsync(CancellationToken.None);
        Assert.True(crawler.LimitReached);
        Assert.Equal(50, project.Entries.Count);
    }

    [Fact]
    public async Task One_entry_below_the_limit_is_not_at_the_limit()
    {
        await using var site = await SiteTestServer.StartAsync();
        site.Page(Site + "/", "<a href=/a.zip>a</a><a href=/b.zip>b</a>");
        site.File(Site + "/a.zip");
        site.File(Site + "/b.zip");
        var project = NewProject(Site + "/", s => { s.SiteLevels = 0; s.Include = "*"; });
        var crawler = NewCrawler(site, project);
        crawler.EntryLimit = 3;
        await crawler.RunAsync(CancellationToken.None);
        Assert.False(crawler.LimitReached);
        Assert.Equal(3, project.Entries.Count);
    }

    [Fact]
    public async Task Page_over_the_limit_is_a_file()
    {
        await using var site = await SiteTestServer.StartAsync();
        var page = "<a href=/inside.zip>z</a>" + new string(' ', 2000);
        site.Page(Site + "/big.html", page);
        site.File(Site + "/inside.zip");
        var project = NewProject(Site + "/big.html", s => { s.SiteLevels = 0; s.Include = "*"; });
        var crawler = NewCrawler(site, project);
        crawler.PageLimit = 1000;
        await crawler.RunAsync(CancellationToken.None);
        var entry = project.Entries.Single(e => e.Url == Site + "/big.html");
        Assert.Equal(GrabberKind.File, entry.Kind);
        Assert.False(entry.Read);
        Assert.Equal(page.Length, entry.Size);
        Assert.Empty(site.AskedFor("/inside.zip"));
    }

    [Fact]
    public async Task Page_at_the_limit_is_read()
    {
        await using var site = await SiteTestServer.StartAsync();
        var page = "<a href=/inside.zip>z</a>" + new string(' ', 975);
        site.Page(Site + "/fits.html", page);
        site.File(Site + "/inside.zip");
        var project = NewProject(Site + "/fits.html", s => { s.SiteLevels = 0; s.Include = "*.zip"; });
        var crawler = NewCrawler(site, project);
        crawler.PageLimit = 1000;
        await crawler.RunAsync(CancellationToken.None);
        Assert.Equal(new[] { Site + "/inside.zip" }, Listed(project));
    }

    [Fact]
    public async Task Compressed_page_is_read()
    {
        await using var site = await SiteTestServer.StartAsync();
        site.Page(Site + "/", "<a href=/a.zip>a</a>").Gzip = true;
        site.File(Site + "/a.zip");
        var project = await Crawl(site, Site + "/", s => { s.SiteLevels = 0; s.Include = "*.zip"; });
        Assert.Equal(new[] { Site + "/a.zip" }, Listed(project));
    }

    [Fact]
    public async Task Compressed_page_stops_at_the_limit()
    {
        await using var site = await SiteTestServer.StartAsync();
        // Small on the wire, with nothing to say how big it will be, and a link only just past the limit.
        var bomb = Encoding.ASCII.GetBytes(new string(' ', 1024 * 1024) + "<a href=/inside.zip>z</a>");
        site.Put(Site + "/bomb.html", bomb, "text/html").Gzip = true;
        site.File(Site + "/inside.zip");
        var project = NewProject(Site + "/bomb.html", s => { s.SiteLevels = 0; s.Include = "*"; });
        var crawler = NewCrawler(site, project);
        crawler.PageLimit = 1024 * 1024;
        await crawler.RunAsync(CancellationToken.None);
        var entry = Assert.Single(project.Entries);
        Assert.Equal((GrabberKind.File, false, false), (entry.Kind, entry.Read, entry.Kept));
        Assert.Empty(site.AskedFor("/inside.zip"));
    }

    [Fact]
    public async Task Missing_page_is_failed_and_the_crawl_goes_on()
    {
        await using var site = await SiteTestServer.StartAsync();
        site.Page(Site + "/", "<a href=/gone.html>g</a><a href=/here.html>h</a>");
        site.Page(Site + "/here.html", "<a href=/a.zip>a</a>");
        site.File(Site + "/a.zip");
        var project = await Crawl(site, Site + "/", s => { s.SiteLevels = 1; s.Include = "*.zip"; });
        var gone = project.Entries.Single(e => e.Url == Site + "/gone.html");
        Assert.Equal(GrabberState.Failed, gone.State);
        Assert.Contains("404", gone.Error);
        Assert.Single(site.AskedFor("/gone.html"));
        Assert.Equal(new[] { Site + "/a.zip" }, Listed(project));
    }

    [Fact]
    public async Task Server_errors_get_three_tries()
    {
        await using var site = await SiteTestServer.StartAsync();
        site.Page(Site + "/", "<a href=/flaky.html>f</a><a href=/down.html>d</a>");
        site.Page(Site + "/flaky.html", "<a href=/a.zip>a</a>").FailTimes = 2;
        site.Page(Site + "/down.html", "x").FailTimes = 100;
        site.File(Site + "/a.zip");
        var project = await Crawl(site, Site + "/", s => { s.SiteLevels = 1; s.Include = "*.zip"; });
        Assert.Equal(new[] { Site + "/a.zip" }, Listed(project));
        Assert.Equal(GrabberState.Failed, project.Entries.Single(e => e.Url == Site + "/down.html").State);
        Assert.Equal(4, site.AskedFor("/down.html").Count);
    }

    [Fact]
    public async Task Outage_waits_instead_of_losing_pages()
    {
        await using var site = await SiteTestServer.StartAsync();
        site.Page(Site + "/", "<a href=/1.html>1</a><a href=/2.html>2</a>");
        site.Page(Site + "/1.html", "<a href=/a.zip>a</a>");
        site.Page(Site + "/2.html", "<a href=/b.zip>b</a>");
        site.File(Site + "/a.zip");
        site.File(Site + "/b.zip");
        site.Down = true;
        var project = NewProject(Site + "/", s => { s.SiteLevels = 1; s.Include = "*.zip"; });
        var running = NewCrawler(site, project).RunAsync(CancellationToken.None);
        await Until(() => site.Requests.Count >= 5);
        Assert.False(running.IsCompleted);
        Assert.All(project.Entries, e => Assert.NotEqual(GrabberState.Failed, e.State));
        site.Down = false;
        await running;
        Assert.Equal(Addresses(Site, "/a.zip", "/b.zip"), Listed(project));
    }

    [Fact]
    public async Task Outage_during_a_crawl_waits_too()
    {
        await using var site = await SiteTestServer.StartAsync();
        const int pages = 8;
        site.Page(Site + "/", "<a href=/1.html>1</a>");
        foreach (var i in Enumerable.Range(1, pages)) site.Page(Site + $"/{i}.html", $"<a href=/{i + 1}.html>next</a>");
        site.Page(Site + $"/{pages + 1}.html", "<a href=/a.zip>a</a>");
        site.File(Site + "/a.zip");
        site.Hold = TimeSpan.FromMilliseconds(50);
        var project = NewProject(Site + "/", s => { s.WholeSite = true; s.Include = "*.zip"; });
        var running = NewCrawler(site, project).RunAsync(CancellationToken.None);
        await Until(() => site.AskedFor("/2.html").Count > 0);
        site.Down = true;
        var asked = site.Requests.Count;
        await Until(() => site.Requests.Count >= asked + 3);
        Assert.False(running.IsCompleted);
        Assert.DoesNotContain(project.Entries, e => e.State == GrabberState.Failed);
        site.Down = false;
        await running;
        Assert.Equal(new[] { Site + "/a.zip" }, Listed(project));
    }

    [Fact]
    public async Task Outage_past_the_reconnect_window_fails_what_is_left()
    {
        await using var site = await SiteTestServer.StartAsync();
        site.Page(Site + "/", "x");
        site.Down = true;
        var project = NewProject(Site + "/");
        await NewCrawler(site, project, Quick(TimeSpan.FromMilliseconds(300))).RunAsync(CancellationToken.None);
        Assert.Equal(GrabberState.Failed, Assert.Single(project.Entries).State);
    }

    [Fact]
    public async Task One_dead_host_does_not_hold_up_the_crawl()
    {
        await using var site = await SiteTestServer.StartAsync();
        // Nothing listens on this port, and the others answer, so this isn't an outage.
        using var closed = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        closed.Start();
        var port = ((System.Net.IPEndPoint)closed.LocalEndpoint).Port;
        closed.Stop();
        site.Page("/", $"<a href=/1.html>1</a><a href=http://127.0.0.1:{port}/dead.html>d</a><a href=/2.html>2</a>");
        site.Page("/1.html", "one");
        site.Page("/2.html", "two");
        var project = NewProject(site.BaseUrl + "/", s => { s.SiteLevels = 1; s.OtherLevels = 1; s.Include = "*.zip"; });
        var crawler = new GrabberCrawler(project, _dir, new Settings(), new RateLimiter()) { Retry = Quick(TimeSpan.FromMinutes(5)) };
        var clock = Stopwatch.StartNew();
        await crawler.RunAsync(CancellationToken.None);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), $"took {clock.Elapsed}");
        Assert.Equal(3, Explored(project).Count);
        Assert.Equal(GrabberState.Failed, project.Entries.Single(e => e.Url.EndsWith("/dead.html")).State);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task At_most_the_set_number_at_once(int atOnce)
    {
        await using var site = await SiteTestServer.StartAsync();
        var links = string.Concat(Enumerable.Range(0, 16).Select(i => $"<a href=/{i}.html>x</a>"));
        site.Page(Site + "/", links);
        foreach (var i in Enumerable.Range(0, 16)) site.Page(Site + $"/{i}.html", "x");
        site.Hold = TimeSpan.FromMilliseconds(100);
        await Crawl(site, Site + "/", s => { s.SiteLevels = 1; s.ExploreAtOnce = atOnce; s.Include = "*.zip"; });
        Assert.Equal(atOnce, site.MaxConcurrent);
    }

    [Fact]
    public async Task Crawl_obeys_the_speed_limit()
    {
        await using var site = await SiteTestServer.StartAsync();
        site.Page(Site + "/", new string(' ', 400 * 1024));
        var limiter = new RateLimiter { BytesPerSecond = 512 * 1024 };
        var project = NewProject(Site + "/", s => { s.Include = "*.zip"; });
        var crawler = new GrabberCrawler(project, _dir, new Settings { Proxy = site.Proxy }, limiter);
        var clock = Stopwatch.StartNew();
        await crawler.RunAsync(CancellationToken.None);
        Assert.True(clock.Elapsed > TimeSpan.FromMilliseconds(500), $"took {clock.Elapsed}");
    }

    static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException();
            await Task.Delay(10);
        }
    }

    string ProjectPath => Path.Combine(_dir, "project.json");

    [Fact]
    public async Task Stopped_crawl_finishes_with_the_same_set()
    {
        await using var site = await SiteTestServer.StartAsync();
        var pages = Enumerable.Range(0, 30).ToList();
        site.Page(Site + "/", string.Concat(pages.Select(i => $"<a href=/{i}.html>x</a>")));
        foreach (var i in pages)
        {
            site.Page(Site + $"/{i}.html", $"<a href=/{i}.zip>z</a><a href=/all.zip>a</a>");
            site.File(Site + $"/{i}.zip");
        }
        site.File(Site + "/all.zip");
        site.Hold = TimeSpan.FromMilliseconds(10);

        var whole = await Crawl(site, Site + "/", s => { s.WholeSite = true; s.Include = "*.zip"; });
        site.ClearRequests();

        var project = NewProject(Site + "/", s => { s.WholeSite = true; s.Include = "*.zip"; });
        // Requests after the 20th wait, so the crawl is stopped partway through however slow the machine.
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        site.HoldAfter(20, stopped.Task);
        using (var stop = new CancellationTokenSource())
        {
            var running = NewCrawler(site, project).RunAsync(stop.Token);
            await Until(() => site.Requests.Count >= 20);
            stop.Cancel();
            await running;
        }
        stopped.SetResult();
        GrabberStore.Save(ProjectPath, project);
        Assert.Contains(project.Entries, e => e.State == GrabberState.Waiting);
        var doneBefore = project.Entries.Where(e => e.State == GrabberState.Done && e.Read).Select(e => new Uri(e.Url).PathAndQuery).ToList();
        Assert.NotEmpty(doneBefore);
        site.ClearRequests();

        var loaded = GrabberStore.Load(ProjectPath)!;
        await NewCrawler(site, loaded).RunAsync(CancellationToken.None);

        Assert.Equal(Listed(whole), Listed(loaded));
        Assert.Equal(Explored(whole), Explored(loaded));
        Assert.All(doneBefore, path => Assert.Empty(site.AskedFor(path)));
    }

    [Fact]
    public async Task Crawl_saves_as_it_goes()
    {
        await using var site = await SiteTestServer.StartAsync();
        site.Page(Site + "/", "<a href=/1.html>1</a>");
        site.Page(Site + "/1.html", "x");
        var project = NewProject(Site + "/", s => { s.SiteLevels = 1; s.Include = "*.zip"; });
        var crawler = NewCrawler(site, project);
        crawler.SavePath = ProjectPath;
        crawler.SaveEvery = TimeSpan.Zero;
        site.Hold = TimeSpan.FromMilliseconds(200);
        var running = crawler.RunAsync(CancellationToken.None);
        await Until(() => Saved()?.Entries.Any(e => e.Url == Site + "/1.html") == true);
        await running;
        Assert.All(GrabberStore.Load(ProjectPath)!.Entries, e => Assert.Equal(GrabberState.Done, e.State));
    }

    // The crawl may be swapping the file in just then.
    GrabberProject? Saved()
    {
        try { return GrabberStore.Load(ProjectPath); }
        catch (IOException) { return null; }
    }

    [Fact]
    public async Task Crash_during_a_save_keeps_the_last_one()
    {
        await using var site = await SiteTestServer.StartAsync();
        site.Page(Site + "/", "x");
        var project = await Crawl(site, Site + "/", s => s.Include = "*");
        GrabberStore.Save(ProjectPath, project);
        project.Name = "newer";
        GrabberStore.Save(ProjectPath, project);
        // A power cut after a swap can leave a broken file of the full length.
        File.WriteAllBytes(ProjectPath, new byte[1000]);
        var loaded = GrabberStore.Load(ProjectPath)!;
        Assert.Equal("test", loaded.Name);
        Assert.Single(loaded.Entries);
        Assert.True(File.Exists(ProjectPath + ".corrupt"));
    }

    [Fact]
    public void Missing_project_loads_as_nothing()
    {
        Assert.Null(GrabberStore.Load(ProjectPath));
    }

    [Fact]
    public async Task Saved_password_and_cookies_are_encrypted()
    {
        await using var site = await SiteTestServer.StartAsync();
        site.Page(Site + "/", "x");
        var project = await Crawl(site, Site + "/", s =>
        {
            s.UserName = "user";
            s.ProtectedPassword = Secrets.Protect("hunter2");
            s.ProtectedCookies = Secrets.Protect("session=topsecret");
        });
        GrabberStore.Save(ProjectPath, project);
        var saved = File.ReadAllText(ProjectPath);
        Assert.DoesNotContain("hunter2", saved);
        Assert.DoesNotContain("topsecret", saved);
        Assert.Contains("dpapi:", saved);
    }

    [Fact]
    public async Task Failed_entries_are_tried_again()
    {
        await using var site = await SiteTestServer.StartAsync();
        site.Page(Site + "/", "<a href=/1.html>1</a>");
        site.Status(Site + "/1.html", 503).FailTimes = 0;
        var project = await Crawl(site, Site + "/", s => { s.SiteLevels = 1; s.Include = "*.zip"; });
        Assert.Equal(GrabberState.Failed, project.Entries.Single(e => e.Url == Site + "/1.html").State);

        site.Page(Site + "/1.html", "<a href=/a.zip>a</a>");
        site.File(Site + "/a.zip");
        site.ClearRequests();
        // As if that crawl had been stopped at the end: carrying on asks only for what failed.
        project.Explored = false;
        await NewCrawler(site, project).RunAsync(CancellationToken.None);
        Assert.Equal(new[] { Site + "/a.zip" }, Listed(project));
        Assert.Empty(site.AskedFor("/"));
    }

    [Fact]
    public async Task A_crawl_after_one_that_went_all_the_way_explores_again()
    {
        await using var site = await SiteTestServer.StartAsync();
        site.Page(Site + "/", "<a href=/1.html>1</a>").ETag = "\"start\"";
        site.Page(Site + "/1.html", "<a href=/a.zip>a</a>");
        site.Page(Site + "/lost.html", "<p>");
        site.File(Site + "/a.zip");
        var project = await Crawl(site, Site + "/", s => { s.SiteLevels = 1; s.Include = "*.zip"; });
        Assert.True(project.Explored);
        Assert.Equal(0, project.Pass);

        // A page found by the first pass that nothing links to now is left as it was.
        project.Entries.Add(new GrabberEntry { Url = Site + "/lost.html", Kind = GrabberKind.Page, State = GrabberState.Done });
        site.Page(Site + "/1.html", "<a href=/a.zip>a</a><a href=/b.zip>b</a>");
        site.File(Site + "/b.zip");
        site.ClearRequests();
        await NewCrawler(site, project).RunAsync(CancellationToken.None);
        Assert.Equal((1, true), (project.Pass, project.Explored));
        Assert.Equal("\"start\"", Assert.Single(site.AskedFor("/")).IfNoneMatch);
        Assert.Single(site.AskedFor("/1.html"));
        Assert.Equal("bytes=0-0", Assert.Single(site.AskedFor("/a.zip")).Range);
        Assert.Empty(site.AskedFor("/lost.html"));
        Assert.Equal(Addresses(Site, "/a.zip", "/b.zip"), Listed(project));
        Assert.All(project.Entries.Where(e => e.Url != Site + "/lost.html"), e => Assert.Equal(1, e.Pass));
    }

    [Fact]
    public async Task What_fails_is_asked_once_a_pass()
    {
        await using var site = await SiteTestServer.StartAsync();
        // One at a time, in the order found: the file fails before the page that links to it again is read.
        site.Page(Site + "/", "<a href=/gone.zip>g</a><a href=/1.html>1</a>");
        site.Page(Site + "/1.html", "<a href=/gone.zip>g</a>");
        site.File(Site + "/gone.zip");
        var project = await Crawl(site, Site + "/", s => { s.SiteLevels = 1; s.ExploreAtOnce = 1; });

        site.Remove(Site + "/gone.zip");
        site.ClearRequests();
        await NewCrawler(site, project).RunAsync(CancellationToken.None);
        Assert.Single(site.AskedFor("/gone.zip"));
        Assert.Equal(GrabberState.Failed, project.Entries.Single(e => e.Url == Site + "/gone.zip").State);
    }

    [Fact]
    public async Task A_stopped_pass_carries_on_without_asking_again()
    {
        await using var site = await SiteTestServer.StartAsync();
        site.Page(Site + "/", string.Concat(Enumerable.Range(0, 20).Select(i => $"<a href=/{i}.html>x</a>")));
        foreach (var i in Enumerable.Range(0, 20)) site.Page(Site + $"/{i}.html", "<p>");
        var project = await Crawl(site, Site + "/", s => s.SiteLevels = 1);
        site.Hold = TimeSpan.FromMilliseconds(100);
        site.ClearRequests();

        // The second pass, stopped partway.
        using (var stop = new CancellationTokenSource())
        {
            var crawl = NewCrawler(site, project).RunAsync(stop.Token);
            while (site.Requests.Count < 6) await Task.Delay(10);
            stop.Cancel();
            await crawl;
        }
        Assert.Equal((1, false), (project.Pass, project.Explored));
        var asked = site.Requests.Select(r => r.PathAndQuery).ToHashSet();

        site.Hold = TimeSpan.Zero;
        site.ClearRequests();
        await NewCrawler(site, project).RunAsync(CancellationToken.None);
        Assert.Equal((1, true), (project.Pass, project.Explored));
        var rest = site.Requests.Select(r => r.PathAndQuery).ToList();
        // Each page once in the pass: what the stopped crawl finished isn't asked for again.
        Assert.Equal(21, asked.Count + rest.Count - rest.Count(asked.Contains));
        Assert.Equal(rest.Count, rest.Distinct().Count());
        Assert.DoesNotContain("/", rest);
    }

    [Fact]
    public async Task Bodies_are_kept_and_read_back()
    {
        await using var site = await SiteTestServer.StartAsync();
        site.Page(Site + "/", "<a href=/a.zip>a</a>");
        site.File(Site + "/a.zip");
        var project = await Crawl(site, Site + "/", s => { s.SiteLevels = 0; s.Include = "*.zip"; });
        var page = project.Entries.Single(e => e.Url == Site + "/");
        Assert.True(page.Kept);
        Assert.Equal("<a href=/a.zip>a</a>", Encoding.UTF8.GetString(GrabberStore.ReadBody(_dir, page)!));
        Assert.Null(GrabberStore.ReadBody(_dir, project.Entries.Single(e => e.Url == Site + "/a.zip")));
    }

    [Fact]
    public async Task Damaged_body_is_read_as_nothing()
    {
        await using var site = await SiteTestServer.StartAsync();
        site.Page(Site + "/", "x");
        var project = await Crawl(site, Site + "/");
        var page = Assert.Single(project.Entries);
        File.WriteAllText(GrabberStore.BodyPath(_dir, page), "changed");
        Assert.Null(GrabberStore.ReadBody(_dir, page));
    }

    [Fact]
    public async Task Counts_and_events_follow_the_crawl()
    {
        await using var site = await SiteTestServer.StartAsync();
        site.Page(Site + "/", "<a href=/1.html>1</a><a href=/a.zip>a</a><a href=/gone.html>g</a>");
        site.Page(Site + "/1.html", "x");
        site.File(Site + "/a.zip");
        var project = NewProject(Site + "/", s => { s.SiteLevels = 1; s.Include = "*.zip"; });
        var crawler = NewCrawler(site, project);
        var found = new List<string>();
        crawler.Found += entry => { lock (found) found.Add(entry.Url); };
        await crawler.RunAsync(CancellationToken.None);
        var counts = crawler.Counts;
        Assert.Equal((2, 1, 1, 0), (counts.PagesRead, counts.FilesFound, counts.Failed, counts.Waiting));
        Assert.Equal(new[] { Site + "/a.zip" }, found);
    }

    [Fact]
    public async Task Refused_start_address_fails_with_a_message()
    {
        await using var site = await SiteTestServer.StartAsync();
        var project = NewProject("not an address");
        await Assert.ThrowsAsync<InvalidDataException>(() => NewCrawler(site, project).RunAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("www.site.test", "http://www.site.test/")]
    [InlineData("www.site.test/a b.html", "http://www.site.test/a%20b.html")]
    [InlineData("  http://www.site.test/x  ", "http://www.site.test/x")]
    [InlineData("https://www.site.test/x#top", "https://www.site.test/x")]
    [InlineData("localhost:8080/x", "http://localhost:8080/x")]
    [InlineData("www.site.test:8080", "http://www.site.test:8080/")]
    public void Start_address_is_made_whole(string typed, string expected)
    {
        Assert.Equal(expected, GrabberCrawler.StartAddress(typed)?.AbsoluteUri);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ftp://www.site.test/")]
    [InlineData("file:///c:/x")]
    [InlineData("javascript:go()")]
    public void Start_address_that_cannot_be_explored(string typed) => Assert.Null(GrabberCrawler.StartAddress(typed));

    [Fact]
    public async Task Legacy_encoded_page_gives_the_right_address()
    {
        await using var site = await SiteTestServer.StartAsync();
        site.Put(Site + "/", Encoding.Latin1.GetBytes("<a href='caf\u00e9.zip'>a</a>"), "text/html; charset=iso-8859-1");
        site.File(Site + "/caf%C3%A9.zip");
        var project = await Crawl(site, Site + "/", s => { s.SiteLevels = 0; s.Include = "*.zip"; });
        Assert.Equal("caf\u00e9.zip", project.Entries.Single(e => e.Matched).FileName);
    }
}
