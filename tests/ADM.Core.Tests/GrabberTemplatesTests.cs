using ADM.Core;

namespace ADM.Core.Tests;

public sealed class GrabberTemplatesTests : IDisposable
{
    const string Site = "http://www.site.test";
    const string Cdn = "http://cdn.test";
    const string Other = "http://other.test";

    readonly string _dir = Path.Combine(Path.GetTempPath(), "adm-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    string StatePath => Path.Combine(_dir, "state.json");

    // One site with something for every template: pages three deep, another site's page, pictures on a CDN, and a stylesheet's own files.
    static void WriteSite(SiteTestServer site)
    {
        site.Page(Site + "/",
            "<link rel=stylesheet href=/style.css><script src=/app.js></script><img src=/pics/top.jpg><img src=http://cdn.test/logo.png>" +
            "<a href=/a.html>a</a><a href=/song.mp3>s</a><a href=/movie.mp4>m</a><a href=/doc.pdf>d</a><a href=/setup.exe>e</a>" +
            "<a href=http://other.test/page.html>o</a>");
        site.Put(Site + "/style.css", "b{background:url(/pics/bg.png)} @font-face{src:url(/f.woff2)}"u8.ToArray(), "text/css");
        site.Put(Site + "/app.js", "var x = 1;"u8.ToArray(), "application/javascript");
        site.Page(Site + "/a.html", "<img src=/pics/a.png><a href=/b.html>b</a><a href=/track.mp3>t</a><a href=/clip.webm>c</a>");
        site.Page(Site + "/b.html", "<img src=/pics/b.gif><a href=/c.html>c</a><a href=/deep.mp3>d</a><a href=/deep.mp4>v</a><a href=/data.zip>z</a>");
        site.Page(Site + "/c.html", "<a href=/c.zip>z</a>");
        site.Page(Other + "/page.html", "<img src=/x.jpg><a href=/far.mp3>f</a>");
        foreach (var file in new[]
        {
            "/pics/top.jpg", "/song.mp3", "/movie.mp4", "/doc.pdf", "/setup.exe", "/pics/bg.png", "/f.woff2", "/pics/a.png",
            "/track.mp3", "/clip.webm", "/pics/b.gif", "/deep.mp3", "/deep.mp4", "/data.zip", "/c.zip",
        })
            site.File(Site + file);
        site.File(Cdn + "/logo.png");
        site.File(Other + "/x.jpg");
        site.File(Other + "/far.mp3");
    }

    // What a template lists, run as a project made from it. Converting links makes sense only where pages are saved.
    async Task Lists(string template, params string[] expected)
    {
        await using var site = await SiteTestServer.StartAsync();
        WriteSite(site);
        var t = Assert.Single(GrabberTemplates.BuiltIn, t => t.Name == template);
        var settings = new GrabberSettings { StartUrl = Site + "/" };
        GrabberTemplates.Apply(t, settings);
        var project = new GrabberProject { Name = "test", Settings = settings };
        await new GrabberCrawler(project, _dir, new Settings { Proxy = site.Proxy }, new RateLimiter()).RunAsync(CancellationToken.None);

        var listed = project.Entries.Where(e => e.Matched).ToList();
        Assert.Equal(expected.Order(), listed.Select(e => e.Url).Order());
        Assert.Equal(listed.Any(e => e.Kind == GrabberKind.Page), settings.ConvertLinks);
    }

    static readonly string[] SiteToLevelTwo =
    [
        Site + "/", Site + "/style.css", Site + "/app.js", Site + "/pics/top.jpg", Site + "/a.html", Site + "/song.mp3", Site + "/movie.mp4",
        Site + "/doc.pdf", Site + "/setup.exe", Site + "/pics/bg.png", Site + "/f.woff2", Site + "/pics/a.png", Site + "/b.html",
        Site + "/track.mp3", Site + "/clip.webm", Site + "/pics/b.gif", Site + "/c.html", Site + "/deep.mp3", Site + "/deep.mp4", Site + "/data.zip",
    ];

    [Fact]
    public Task Custom_is_a_new_project_s_settings() => Lists(GrabberTemplates.Custom, SiteToLevelTwo);

    [Fact]
    public Task All_pictures_of_a_site() => Lists("All pictures of a site",
        Site + "/pics/top.jpg", Cdn + "/logo.png", Site + "/pics/bg.png", Site + "/pics/a.png", Site + "/pics/b.gif");

    [Fact]
    public Task All_pictures_on_a_page() => Lists("All pictures on a page",
        Site + "/pics/top.jpg", Cdn + "/logo.png", Site + "/pics/bg.png");

    [Fact]
    public Task Audio_from_a_page_and_the_pages_it_links_to() => Lists("Audio from a page and the pages it links to",
        Site + "/song.mp3", Site + "/track.mp3", Other + "/far.mp3");

    [Fact]
    public Task All_video_of_a_site() => Lists("All video of a site",
        Site + "/movie.mp4", Site + "/clip.webm", Site + "/deep.mp4");

    [Fact]
    public Task Every_file_of_a_site_but_its_pages_and_pictures() => Lists("Every file of a site but its pages and pictures",
        Site + "/song.mp3", Site + "/movie.mp4", Site + "/doc.pdf", Site + "/setup.exe", Site + "/f.woff2", Site + "/track.mp3",
        Site + "/clip.webm", Site + "/deep.mp3", Site + "/deep.mp4", Site + "/data.zip", Site + "/c.zip");

    [Fact]
    public Task A_whole_site() => Lists("A whole site", [.. SiteToLevelTwo, Site + "/c.zip"]);

    [Fact]
    public Task A_page_complete_to_read_offline() => Lists("A page complete, to read offline",
        Site + "/", Site + "/style.css", Site + "/app.js", Site + "/pics/top.jpg", Cdn + "/logo.png", Site + "/pics/bg.png", Site + "/f.woff2");

    [Fact]
    public void Every_template_is_tested()
    {
        var tested = typeof(GrabberTemplatesTests).GetMethods().Count(m => m.ReturnType == typeof(Task) && m.GetCustomAttributes(typeof(FactAttribute), false).Length > 0);
        Assert.Equal(GrabberTemplates.BuiltIn.Count, tested);
    }

    [Fact]
    public void Apply_leaves_the_start_page_and_folder()
    {
        var settings = new GrabberSettings
        {
            StartUrl = Site + "/start.html",
            UserName = "user",
            ProtectedPassword = Secrets.Protect("pass"),
            ProtectedCookies = Secrets.Protect("a=1"),
            SaveMode = GrabberSaveMode.CategoryFolder,
            Category = "Video",
            Folder = @"D:\Sites",
            KeepSubfolders = false,
            Overwrite = true,
            DownloadAtOnce = 7,
            ExploreAtOnce = 6,
            StartAtOnce = true,
            LinkTextAsDescription = false,
        };
        var before = System.Text.Json.JsonSerializer.Serialize(settings);
        foreach (var template in GrabberTemplates.BuiltIn)
        {
            GrabberTemplates.Apply(template, settings);
            var after = System.Text.Json.JsonSerializer.Deserialize<GrabberSettings>(before)!;
            GrabberTemplates.Apply(template, after);
            Assert.Equal((Site + "/start.html", "user", "pass", "a=1"), (settings.StartUrl, settings.UserName, Secrets.Unprotect(settings.ProtectedPassword), Secrets.Unprotect(settings.ProtectedCookies)));
            Assert.Equal((GrabberSaveMode.CategoryFolder, "Video", @"D:\Sites", false, true), (settings.SaveMode, settings.Category, settings.Folder, settings.KeepSubfolders, settings.Overwrite));
            Assert.Equal((7, 6, true, false), (settings.DownloadAtOnce, settings.ExploreAtOnce, settings.StartAtOnce, settings.LinkTextAsDescription));
            // Whatever came before, a template leaves the same settings.
            Assert.Equal(System.Text.Json.JsonSerializer.Serialize(after), System.Text.Json.JsonSerializer.Serialize(settings));
        }
    }

    [Fact]
    public void Own_template_survives_a_restart()
    {
        var state = new AppState();
        var from = new GrabberSettings { StartUrl = Site + "/", SiteLevels = 4, OtherLevels = 2, Include = "*.zip", Exclude = "big*", MinSizeKB = 10, ExploreNever = "/private/", ConvertLinks = false };
        Assert.Equal(TemplateSaved.Saved, GrabberTemplates.Save(state.Settings, " Zips ", from));
        Store.Save(StatePath, state);

        var loaded = Store.Load(StatePath).Settings;
        var template = GrabberTemplates.Find(loaded, "zips");
        Assert.NotNull(template);
        Assert.False(template.BuiltIn);
        Assert.Equal("Zips", template.Name);
        var applied = new GrabberSettings();
        GrabberTemplates.Apply(template, applied);
        Assert.Equal((4, 2, "*.zip", "big*", 10L, "/private/", false), (applied.SiteLevels, applied.OtherLevels, applied.Include, applied.Exclude, applied.MinSizeKB, applied.ExploreNever, applied.ConvertLinks));
        Assert.Equal(GrabberTemplates.BuiltIn.Count + 1, GrabberTemplates.All(loaded).Count());

        Assert.True(GrabberTemplates.Delete(loaded, "ZIPS"));
        Assert.Empty(loaded.GrabberTemplates);
    }

    [Fact]
    public void Built_in_name_is_refused()
    {
        var settings = new Settings();
        Assert.Equal(TemplateSaved.BuiltInName, GrabberTemplates.Save(settings, "a WHOLE site", new GrabberSettings()));
        Assert.Equal(TemplateSaved.NoName, GrabberTemplates.Save(settings, "  ", new GrabberSettings()));
        Assert.Empty(settings.GrabberTemplates);
        Assert.False(GrabberTemplates.Delete(settings, "A whole site"));
        Assert.Contains(GrabberTemplates.All(settings), t => t.Name == "A whole site" && t.BuiltIn);
    }

    [Fact]
    public void Same_name_asks_to_replace()
    {
        var settings = new Settings();
        GrabberTemplates.Save(settings, "Mine", new GrabberSettings { Include = "*.zip" });
        Assert.Equal(TemplateSaved.NameTaken, GrabberTemplates.Save(settings, "MINE", new GrabberSettings { Include = "*.pdf" }));
        Assert.Equal("*.zip", Assert.Single(settings.GrabberTemplates).Include);
        Assert.Equal(TemplateSaved.Replaced, GrabberTemplates.Save(settings, "MINE", new GrabberSettings { Include = "*.pdf" }, replace: true));
        var replaced = Assert.Single(settings.GrabberTemplates);
        Assert.Equal(("MINE", "*.pdf"), (replaced.Name, replaced.Include));
    }

    [Fact]
    public void Old_settings_load_with_the_defaults()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(StatePath, """{ "Settings": { "DownloadRoot": "D:\\Downloads" } }""");

        var settings = Store.Load(StatePath).Settings;

        Assert.Equal(GrabberFilter.Defaults().Select(f => (f.Name, f.Mask)), settings.GrabberFilters.Select(f => (f.Name, f.Mask)));
        Assert.Empty(settings.GrabberTemplates);
    }

    [Fact]
    public void Filters_are_the_user_s_to_change()
    {
        var state = new AppState();
        state.Settings.GrabberFilters[0].Mask = "*.jpg";
        state.Settings.GrabberFilters.Add(new GrabberFilter { Name = "Fonts", Mask = "*.woff2" });
        Store.Save(StatePath, state);

        var filters = Store.Load(StatePath).Settings.GrabberFilters;
        Assert.Equal("*.jpg", filters[0].Mask);
        Assert.Equal(("Fonts", "*.woff2"), (filters[^1].Name, filters[^1].Mask));
        Assert.Equal(GrabberFilter.Defaults().Count + 1, filters.Count);
    }

    [Fact]
    public void Every_default_filter_reads_as_masks()
    {
        foreach (var filter in GrabberFilter.Defaults())
        {
            var mask = GrabberMask.Parse(filter.Mask);
            Assert.False(mask.IsEmpty, filter.Name);
        }
        Assert.True(GrabberMask.Parse(GrabberFilter.Defaults().Single(f => f.Name == "Pictures").Mask).Matches("a.PNG", Site + "/a.PNG", Site + "/"));
        Assert.True(GrabberMask.Parse(GrabberFilter.Defaults().Single(f => f.Name == "The start page complete").Mask).Matches("index.html", Site + "/", Site + "/"));
    }
}
