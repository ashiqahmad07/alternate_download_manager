using System.Text.Json.Nodes;
using ADM.Core;

namespace ADM.Core.Tests;

/// <summary>A site's video, read by a stand-in for yt-dlp whose formats name files on <see cref="TestServer"/>, and two files joined.</summary>
[Collection(nameof(SpeedTests))]
public sealed class SiteDownloadTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "adm-tests", Guid.NewGuid().ToString("N"));
    const int MiB = 1024 * 1024;
    const string Page = "https://www.youtube.example/watch?v=abc";

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    // The stand-in for yt-dlp has its own folder, as the one for ffmpeg writes args.txt beside itself too.
    string ReaderFolder => Path.Combine(_dir, "reader");
    string FfmpegArgs => File.ReadAllText(Path.Combine(_dir, "tools", "args.txt"));

    YtDlp NewReader() => new(SiteVideoTests.FakeYtDlpIn(ReaderFolder), null, Path.Combine(_dir, "cache"));

    void Answer(params JsonObject[] formats)
    {
        SiteVideoTests.FakeYtDlpIn(ReaderFolder);
        var answer = new JsonObject { ["title"] = "A video", ["webpage_url"] = Page, ["formats"] = new JsonArray(formats) };
        File.WriteAllText(Path.Combine(ReaderFolder, "tools", "answer.json"), answer.ToJsonString());
    }

    static JsonObject Format(string id, string url, string ext, string vcodec, string acodec, int height = 0, string protocol = "https", string? agent = null)
    {
        var format = new JsonObject
        {
            ["format_id"] = id, ["url"] = url, ["protocol"] = protocol, ["ext"] = ext, ["vcodec"] = vcodec, ["acodec"] = acodec,
            ["width"] = height * 16 / 9, ["height"] = height, ["tbr"] = 1000,
        };
        if (agent != null) format["http_headers"] = new JsonObject { ["User-Agent"] = agent };
        return format;
    }

    static JsonObject Video(TestServer server, string query = "", string name = "video.mp4") =>
        Format("137", server.SiteUrl(name) + query, "mp4", "avc1.640028", "none", 1080, agent: "yt-test-agent");

    static JsonObject Audio(TestServer server, string query = "") =>
        Format("140", server.SiteUrl("audio.m4a") + query, "m4a", "none", "mp4a.40.2");

    DownloadItem SiteItem(string choice) => new()
    {
        Url = Page, Directory = _dir, FileName = "video.mp4", Kind = DownloadKind.Site, SiteChoice = choice, MaxConnections = 8,
        Cookies = "session=secret",
    };

    HlsDownloader NewDownloader(string? ffmpeg = null, bool reader = true) =>
        new(new RateLimiter(), new Settings(), ffmpeg, reader ? NewReader() : null);

    string[] FilesInFolder() => Directory.GetFiles(_dir).Select(f => Path.GetFileName(f)).Order().ToArray();

    static long LengthOf(string range)
    {
        var ends = range["bytes=".Length..].Split('-');
        return long.Parse(ends[1]) - long.Parse(ends[0]) + 1;
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

    /// <summary>Stops the download once its first piece is on disk; one connection at 4 MiB a second takes about a second a piece.</summary>
    async Task PauseAfterFirstPieceAsync(DownloadItem item, YtDlp? reader = null)
    {
        item.MaxConnections = 1;
        item.SpeedLimitKBps = 4096;
        using var cts = new CancellationTokenSource();
        var run = new HlsDownloader(new RateLimiter(), new Settings(), null, reader ?? NewReader()).RunAsync(item, cts.Token);
        await Until(() => item.HlsSegmentsDone >= 1);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        item.SpeedLimitKBps = 0;
        item.MaxConnections = 8;
    }

    [Fact]
    public async Task Site_video_downloads_both_files_and_joins_them()
    {
        await using var server = await TestServer.StartAsync(9 * MiB + 5, TimeSpan.Zero);
        Answer(Video(server), Audio(server));
        var item = SiteItem("mp4:1080");

        await NewDownloader(HlsDownloaderTests.FakeFfmpegIn(_dir)).RunAsync(item, CancellationToken.None);

        Assert.Equal("video.mp4", item.FileName);
        Assert.Equal(server.SiteBytes("video.mp4"), File.ReadAllBytes(item.FullPath));
        Assert.Contains("-map 1:a:0", FfmpegArgs);
        Assert.Contains("-f mp4", FfmpegArgs);
        Assert.Equal(4, item.HlsSegmentCount);
        var ranges = server.Ranges("/site/video.mp4").Concat(server.Ranges("/site/audio.m4a")).ToList();
        Assert.Contains("bytes=0-0", ranges);
        Assert.All(ranges, range => Assert.True(LengthOf(range) <= DashManifest.MaxPartBytes, range));
    }

    [Fact]
    public async Task Original_webm_is_joined_as_webm()
    {
        await using var server = await TestServer.StartAsync(256 * 1024, TimeSpan.Zero);
        Answer(Format("248", server.SiteUrl("video.webm"), "webm", "vp9", "none", 1080), Format("251", server.SiteUrl("audio.webm"), "webm", "none", "opus"));
        var item = SiteItem("original");

        await NewDownloader(HlsDownloaderTests.FakeFfmpegIn(_dir)).RunAsync(item, CancellationToken.None);

        Assert.Equal("video.webm", item.FileName);
        Assert.Contains("-f webm", FfmpegArgs);
        Assert.DoesNotContain("faststart", FfmpegArgs);
        Assert.Equal(server.SiteBytes("video.webm"), File.ReadAllBytes(item.FullPath));
    }

    /// <summary>A format that yt-dlp says the site refuses until <paramref name="at"/>, to the millisecond, as JSON carries it.</summary>
    static JsonObject AvailableAt(JsonObject format, DateTimeOffset at)
    {
        format["available_at"] = at.ToUnixTimeMilliseconds() / 1000.0;
        return format;
    }

    [Fact]
    public async Task Files_are_asked_for_once_the_site_makes_them_available()
    {
        // As YouTube's are: refused with a 403 for the seconds its preroll ad would have played.
        await using var server = await TestServer.StartAsync(256 * 1024, TimeSpan.Zero);
        server.SiteAvailableAt = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.AddSeconds(2).ToUnixTimeMilliseconds());
        Answer(AvailableAt(Video(server), server.SiteAvailableAt), AvailableAt(Audio(server), server.SiteAvailableAt));
        var item = SiteItem("mp4:1080");

        var run = NewDownloader(HlsDownloaderTests.FakeFfmpegIn(_dir)).RunAsync(item, CancellationToken.None);
        await Until(() => item.StatusNote == StatusWords.Current.WaitingForVideo || run.IsCompleted);
        Assert.False(run.IsCompleted, "the download did not wait for its files");
        await run;

        Assert.Equal(server.SiteBytes("video.mp4"), File.ReadAllBytes(item.FullPath));
        Assert.Null(item.StatusNote);
    }

    /// <summary>A format whose size yt-dlp gives.</summary>
    static JsonObject Sized(JsonObject format, long size)
    {
        format["filesize"] = size;
        return format;
    }

    [Fact]
    public async Task While_it_waits_for_its_files_the_download_has_the_size_the_site_gave()
    {
        // So the dialog asking about it can say how big it is before the site serves a byte.
        await using var server = await TestServer.StartAsync(256 * 1024, TimeSpan.Zero);
        server.SiteAvailableAt = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.AddSeconds(2).ToUnixTimeMilliseconds());
        Answer(Sized(AvailableAt(Video(server), server.SiteAvailableAt), 5_000_000), Sized(AvailableAt(Audio(server), server.SiteAvailableAt), 700_000));
        var item = SiteItem("mp4:1080");

        var run = NewDownloader(HlsDownloaderTests.FakeFfmpegIn(_dir)).RunAsync(item, CancellationToken.None);
        await Until(() => item.StatusNote == StatusWords.Current.WaitingForVideo || run.IsCompleted);
        Assert.False(run.IsCompleted, "the download did not wait for its files");
        Assert.Equal(5_700_000, item.TotalBytes);
        await run;
    }

    [Fact]
    public async Task A_file_the_site_gives_no_size_for_leaves_the_size_unknown_while_it_waits()
    {
        await using var server = await TestServer.StartAsync(256 * 1024, TimeSpan.Zero);
        server.SiteAvailableAt = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.AddSeconds(2).ToUnixTimeMilliseconds());
        Answer(Sized(AvailableAt(Video(server), server.SiteAvailableAt), 5_000_000), AvailableAt(Audio(server), server.SiteAvailableAt));
        var item = SiteItem("mp4:1080");

        var run = NewDownloader(HlsDownloaderTests.FakeFfmpegIn(_dir)).RunAsync(item, CancellationToken.None);
        await Until(() => item.StatusNote == StatusWords.Current.WaitingForVideo || run.IsCompleted);
        Assert.False(run.IsCompleted, "the download did not wait for its files");
        Assert.Equal(-1, item.TotalBytes);
        await run;
    }

    [Fact]
    public async Task A_time_far_off_is_waited_for_no_longer_than_the_longest_wait_then_the_sites_answer_stands()
    {
        await using var server = await TestServer.StartAsync(256 * 1024, TimeSpan.Zero);
        server.SiteAvailableAt = DateTimeOffset.UtcNow.AddHours(1);
        Answer(AvailableAt(Video(server), server.SiteAvailableAt), AvailableAt(Audio(server), server.SiteAvailableAt));
        var downloader = new HlsDownloader(new RateLimiter(), new Settings(), null, NewReader()) { LongestSiteWait = TimeSpan.FromMilliseconds(200) };

        var e = await Assert.ThrowsAsync<HttpRequestException>(() => downloader.RunAsync(SiteItem("mp4:1080"), CancellationToken.None));

        Assert.Equal(System.Net.HttpStatusCode.Forbidden, e.StatusCode);
    }

    [Fact]
    public async Task Format_headers_go_with_each_request()
    {
        await using var server = await TestServer.StartAsync(256 * 1024, TimeSpan.Zero);
        Answer(Video(server), Audio(server));

        await NewDownloader().RunAsync(SiteItem("mp4:1080"), CancellationToken.None);

        var video = server.SiteRequests.Where(r => r.Path == "/site/video.mp4").ToList();
        Assert.NotEmpty(video);
        Assert.All(video, r => Assert.Equal("yt-test-agent", r.UserAgent));
    }

    [Fact]
    public async Task Files_get_no_browser_cookies()
    {
        await using var server = await TestServer.StartAsync(256 * 1024, TimeSpan.Zero);
        Answer(Video(server), Audio(server));

        await NewDownloader().RunAsync(SiteItem("mp4:1080"), CancellationToken.None);

        Assert.NotEmpty(server.SiteRequests);
        Assert.All(server.SiteRequests, r => Assert.Equal("", r.Cookie));
    }

    [Fact]
    public async Task Each_start_reads_the_page_again()
    {
        await using var server = await TestServer.StartAsync(9 * MiB + 5, TimeSpan.Zero);
        Answer(Video(server), Audio(server));
        var item = SiteItem("mp4:1080");
        await PauseAfterFirstPieceAsync(item);
        var before = server.SiteRequests.Count;
        Answer(Video(server, "?fresh=2"), Audio(server, "?fresh=2"));

        await NewDownloader().RunAsync(item, CancellationToken.None);

        var after = server.SiteRequests.Skip(before).ToList();
        Assert.NotEmpty(after);
        Assert.All(after, r => Assert.Equal("?fresh=2", r.Query));
        Assert.Equal(1, server.Ranges("/site/video.mp4").Count(r => r == $"bytes=0-{DashManifest.MaxPartBytes - 1}"));
        Assert.Equal(server.SiteBytes("video.mp4"), File.ReadAllBytes(item.FullPath));
        Assert.Equal(server.SiteBytes("audio.m4a"), File.ReadAllBytes(Path.Combine(_dir, "video (audio).m4a")));
    }

    YtDlp KeepingReader() => new(SiteVideoTests.FakeYtDlpIn(ReaderFolder), null, Path.Combine(_dir, "cache")) { KeptFolder = Path.Combine(_dir, "reads") };

    string ReaderArgs => Path.Combine(ReaderFolder, "tools", "args.txt");

    [Fact]
    public async Task Each_download_picked_from_the_menu_starts_from_the_menus_read()
    {
        // yt-dlp needn't run again, so a second quality starts as soon as the first did, and the time YouTube makes
        // its files wait has run since the menu's read.
        await using var server = await TestServer.StartAsync(256 * 1024, TimeSpan.Zero);
        Answer(Video(server), Audio(server));
        var reader = KeepingReader();
        await reader.ReadAsync(Page, keep: true, CancellationToken.None);
        File.Delete(ReaderArgs);
        var video = SiteItem("mp4:1080");
        var sound = SiteItem("audio");
        sound.FileName = "sound.m4a";

        await new HlsDownloader(new RateLimiter(), new Settings(), null, reader).RunAsync(video, CancellationToken.None);
        await new HlsDownloader(new RateLimiter(), new Settings(), null, reader).RunAsync(sound, CancellationToken.None);

        Assert.False(File.Exists(ReaderArgs), "yt-dlp read the page again");
        Assert.Equal(server.SiteBytes("video.mp4"), File.ReadAllBytes(video.FullPath));
        Assert.Equal(server.SiteBytes("audio.m4a"), File.ReadAllBytes(sound.FullPath));
        Assert.NotNull(reader.Kept(Page));
    }

    [Fact]
    public async Task A_download_started_before_reads_the_page_again_and_keeps_that_read()
    {
        // The addresses it started with may be gone; the read now is the freshest for the next download of the page.
        await using var server = await TestServer.StartAsync(9 * MiB + 5, TimeSpan.Zero);
        Answer(Video(server), Audio(server));
        var reader = KeepingReader();
        await reader.ReadAsync(Page, keep: true, CancellationToken.None);
        var item = SiteItem("mp4:1080");
        await PauseAfterFirstPieceAsync(item, reader);
        var before = server.SiteRequests.Count;
        Answer(Video(server, "?fresh=2"), Audio(server, "?fresh=2"));
        File.Delete(ReaderArgs);

        await new HlsDownloader(new RateLimiter(), new Settings(), null, reader).RunAsync(item, CancellationToken.None);

        Assert.True(File.Exists(ReaderArgs), "the page was not read again");
        Assert.All(server.SiteRequests.Skip(before), r => Assert.Equal("?fresh=2", r.Query));
        Assert.All(reader.Kept(Page)!.Formats, f => Assert.Equal("?fresh=2", f.Url.Query));
        Assert.Equal(server.SiteBytes("video.mp4"), File.ReadAllBytes(item.FullPath));
    }

    [Fact]
    public async Task The_menus_read_whose_files_are_gone_is_read_again_and_kept_instead()
    {
        await using var server = await TestServer.StartAsync(256 * 1024, TimeSpan.Zero);
        Answer(Video(server, name: "gone.mp4"), Audio(server));
        var reader = KeepingReader();
        await reader.ReadAsync(Page, keep: true, CancellationToken.None);
        Answer(Video(server), Audio(server));
        var item = SiteItem("mp4:1080");

        await new HlsDownloader(new RateLimiter(), new Settings(), null, reader).RunAsync(item, CancellationToken.None);

        Assert.Equal(server.SiteBytes("video.mp4"), File.ReadAllBytes(item.FullPath));
        // So the next download of the page doesn't try the gone files first.
        Assert.DoesNotContain(reader.Kept(Page)!.Formats, f => f.Url.AbsolutePath.EndsWith("gone.mp4", StringComparison.Ordinal));
    }

    [Fact]
    public async Task While_ytdlp_reads_the_page_the_download_says_so()
    {
        // Rather than "Downloading" with nothing arriving.
        Answer(Format("137", "https://files.example/v.mp4", "mp4", "avc1.640028", "mp4a.40.2", 1080));
        File.WriteAllText(Path.Combine(ReaderFolder, "tools", "wait.txt"), "");
        var item = SiteItem("mp4:1080");
        using var cts = new CancellationTokenSource();

        var run = NewDownloader().RunAsync(item, cts.Token);
        await Until(() => item.StatusNote == StatusWords.Current.ReadingVideo || run.IsCompleted);
        Assert.False(run.IsCompleted, "the download did not wait for yt-dlp");
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Null(item.StatusNote);
    }

    [Fact]
    public async Task The_video_and_its_sound_are_asked_for_side_by_side()
    {
        // One after the other, the first bytes came a round trip later: over a second for YouTube's files.
        await using var server = await TestServer.StartAsync(256 * 1024, TimeSpan.Zero);
        Answer(Video(server), Audio(server));
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.SiteHold = hold;
        var item = SiteItem("mp4:1080");
        string[] AskedFor() => server.SiteRequests.Select(r => r.Path).Distinct().Order(StringComparer.Ordinal).ToArray();

        var run = NewDownloader().RunAsync(item, CancellationToken.None);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (AskedFor().Length < 2 && !run.IsCompleted && DateTime.UtcNow < deadline) await Task.Delay(10);
        var asked = AskedFor();
        hold.SetResult();
        await run;

        Assert.Equal(new[] { "/site/audio.m4a", "/site/video.mp4" }, asked);
        Assert.Equal(server.SiteBytes("video.mp4"), File.ReadAllBytes(item.FullPath));
    }

    [Fact]
    public async Task Progress_counts_bytes_as_they_arrive_as_the_files_sizes_are_known()
    {
        // Counting parts, the bar stood still and then leapt, as parts fetched side by side finish together.
        await using var server = await TestServer.StartAsync(9 * MiB + 5, TimeSpan.Zero);
        Answer(Video(server), Audio(server));
        var item = SiteItem("mp4:1080");

        await PauseAfterFirstPieceAsync(item);

        Assert.Equal(server.SiteBytes("video.mp4").Length + server.SiteBytes("audio.m4a").Length, item.TotalBytes);
        Assert.Equal(100.0 * item.DownloadedBytes / item.TotalBytes, item.Progress, 6);
    }

    [Fact]
    public void A_stream_of_known_size_counts_bytes_and_one_of_unknown_size_counts_parts()
    {
        var known = new DownloadItem { Kind = DownloadKind.Site, HlsSegmentCount = 4, HlsSegmentsDone = 0, TotalBytes = 1000 };
        known.AddDownloaded(250);
        var unknown = new DownloadItem { Kind = DownloadKind.Site, HlsSegmentCount = 4, HlsSegmentsDone = 1 };
        unknown.AddDownloaded(999);

        Assert.Equal((25.0, 25.0), (known.Progress, unknown.Progress));
    }

    [Fact]
    public async Task Changed_files_start_over()
    {
        await using var server = await TestServer.StartAsync(9 * MiB + 5, TimeSpan.Zero);
        Answer(Video(server), Audio(server));
        var item = SiteItem("mp4:1080");
        await PauseAfterFirstPieceAsync(item);
        Answer(Video(server, name: "other.mp4"), Audio(server));

        await NewDownloader().RunAsync(item, CancellationToken.None);

        Assert.Equal(server.SiteBytes("other.mp4"), File.ReadAllBytes(item.FullPath));
        Assert.Equal(server.SiteBytes("audio.m4a"), File.ReadAllBytes(Path.Combine(_dir, "video (audio).m4a")));
    }

    [Fact]
    public async Task A_resumed_download_carries_on_with_the_files_its_parts_came_from()
    {
        // As those paused on a YouTube stream before ADM ranked a whole file first: the read now ranks another file of
        // the same quality first, and the gigabytes already downloaded aren't thrown away for it.
        await using var server = await TestServer.StartAsync(9 * MiB + 5, TimeSpan.Zero);
        Answer(Video(server), Audio(server));
        var item = SiteItem("mp4:1080");
        await PauseAfterFirstPieceAsync(item);
        var better = Format("299", server.SiteUrl("other.mp4"), "mp4", "avc1.64002a", "none", 1080);
        better["tbr"] = 4000;
        Answer(better, Video(server), Audio(server));

        await NewDownloader().RunAsync(item, CancellationToken.None);

        Assert.Equal(server.SiteBytes("video.mp4"), File.ReadAllBytes(item.FullPath));
    }

    [Fact]
    public async Task Hls_format_downloads_through_the_hls_reader()
    {
        await using var server = await TestServer.StartAsync(256 * 1024, TimeSpan.Zero);
        Answer(Format("hls-720", server.HlsFmp4Url, "mp4", "avc1.64001f", "mp4a.40.2", 720, protocol: "m3u8_native"));
        var item = SiteItem("mp4:720");

        await NewDownloader().RunAsync(item, CancellationToken.None);

        Assert.Equal("video.mp4", item.FileName);
        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
    }

    [Fact]
    public async Task Without_ytdlp_the_download_says_what_to_do()
    {
        var e = await Assert.ThrowsAsync<InvalidDataException>(() => NewDownloader(reader: false).RunAsync(SiteItem("mp4:720"), CancellationToken.None));

        Assert.Contains("yt-dlp", e.Message);
        Assert.Contains("Options > Video", e.Message);
    }

    DownloadItem JoinedItem(TestServer server) => new()
    {
        Url = server.SiteUrl("video.mp4"), AudioUrl = server.SiteUrl("audio.m4a"), Directory = _dir, FileName = "video.mp4",
        Kind = DownloadKind.Joined, MaxConnections = 8,
    };

    [Fact]
    public async Task Joined_files_download_and_join()
    {
        await using var server = await TestServer.StartAsync(256 * 1024, TimeSpan.Zero);
        var item = JoinedItem(server);

        await NewDownloader(HlsDownloaderTests.FakeFfmpegIn(_dir), reader: false).RunAsync(item, CancellationToken.None);

        Assert.Equal("video.mp4", item.FileName);
        Assert.Equal(server.SiteBytes("video.mp4"), File.ReadAllBytes(item.FullPath));
        Assert.Contains("-map 1:a:0", FfmpegArgs);
        Assert.Contains("-f mp4", FfmpegArgs);
    }

    [Fact]
    public async Task Joined_without_ffmpeg_keeps_the_sound_beside()
    {
        await using var server = await TestServer.StartAsync(256 * 1024, TimeSpan.Zero);
        var item = JoinedItem(server);

        await NewDownloader(reader: false).RunAsync(item, CancellationToken.None);

        Assert.Equal(new[] { "video (audio).m4a", "video.mp4" }, FilesInFolder());
        Assert.Equal(server.SiteBytes("video.mp4"), File.ReadAllBytes(Path.Combine(_dir, "video.mp4")));
        Assert.Equal(server.SiteBytes("audio.m4a"), File.ReadAllBytes(Path.Combine(_dir, "video (audio).m4a")));
    }

    [Fact]
    public async Task Without_ffmpeg_a_file_keeps_its_own_container()
    {
        await using var server = await TestServer.StartAsync(256 * 1024, TimeSpan.Zero);
        Answer(Format("43", server.SiteUrl("video.webm"), "webm", "vp8", "vorbis", 360));
        var item = SiteItem("mp4:360");

        await NewDownloader().RunAsync(item, CancellationToken.None);

        Assert.Equal("video.webm", item.FileName);
        Assert.Equal(server.SiteBytes("video.webm"), File.ReadAllBytes(item.FullPath));
    }

    [Fact]
    public void Site_download_survives_a_save_and_old_state_loads()
    {
        var path = Path.Combine(_dir, "state.json");
        Directory.CreateDirectory(_dir);
        var item = SiteItem("mp4:720");
        item.AudioUrl = "https://a.example/b.m4a";
        item.SiteFormats = "137:5|140:6";

        Store.Save(path, new AppState { Downloads = { item } });
        var restored = Assert.Single(Store.Load(path).Downloads);

        Assert.Equal((DownloadKind.Site, "mp4:720", "https://a.example/b.m4a", "137:5|140:6"),
            (restored.Kind, restored.SiteChoice, restored.AudioUrl, restored.SiteFormats));

        File.WriteAllText(path, """{"Downloads":[{"Url":"https://a.com/f.zip","FileName":"f.zip","Directory":"C:\\x","Kind":"Http"}]}""");
        var old = Assert.Single(Store.Load(path).Downloads);
        Assert.Equal((DownloadKind.Http, (string?)null, (string?)null, (string?)null), (old.Kind, old.SiteChoice, old.AudioUrl, old.SiteFormats));
    }

    [Fact]
    public void Add_keeps_the_choice_and_the_sound()
    {
        var manager = new DownloadManager(Path.Combine(_dir, "state.json"), new SynchronizationContext());

        var site = manager.Add(new DownloadRequest { Url = Page, Kind = DownloadKind.Site, SiteChoice = "mp4:1080", Directory = _dir }, startNow: false);
        var joined = manager.Add(new DownloadRequest { Url = "https://v.example/v.mp4", AudioUrl = "https://v.example/a.m4a", Kind = DownloadKind.Joined, Directory = _dir }, startNow: false);

        Assert.Equal(("mp4:1080", "video.mp4"), (site.SiteChoice, site.FileName));
        Assert.Equal(("https://v.example/a.m4a", "video.mp4"), (joined.AudioUrl, joined.FileName));
    }
}
