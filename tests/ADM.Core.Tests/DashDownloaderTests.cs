using System.Diagnostics;
using ADM.Core;

namespace ADM.Core.Tests;

public sealed class DashDownloaderTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "adm-tests", Guid.NewGuid().ToString("N"));

    static readonly TimeSpan Short = TimeSpan.FromMilliseconds(50);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    string StatePath => Path.Combine(_dir, "state.json");

    string FfmpegArgs => File.ReadAllText(Path.Combine(_dir, "tools", "args.txt"));

    DownloadItem NewItem(string url, string fileName = "video.mp4") =>
        new() { Url = url, Directory = _dir, FileName = fileName, Kind = DownloadKind.Dash, MaxConnections = 8 };

    static HlsDownloader NewDownloader(Settings? settings = null, string? ffmpeg = null, RateLimiter? limiter = null) =>
        new(limiter ?? new RateLimiter(), settings ?? new Settings(), ffmpeg);

    // Without an ffmpeg, the tracks stay as they are whatever is on this machine's PATH.
    static Task RunAsync(DownloadItem item, Settings? settings = null, string? ffmpeg = null, CancellationToken ct = default) =>
        NewDownloader(settings, ffmpeg).RunAsync(item, ct);

    static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException();
            await Task.Delay(10);
        }
    }

    string[] FilesInFolder() => Directory.GetFiles(_dir).Select(Path.GetFileName).Order().ToArray()!;

    [Theory]
    [InlineData("number")]
    [InlineData("time")]
    [InlineData("list")]
    [InlineData("list-ranges")]
    [InlineData("segment-base")]
    [InlineData("whole")]
    [InlineData("periods-same-init")]
    [InlineData("periods-own-init")]
    public async Task Each_form_downloads_to_identical_bytes(string name)
    {
        await using var server = await TestServer.StartAsync(256 * 1024, TimeSpan.Zero);
        var item = NewItem(server.DashUrl(name));

        await RunAsync(item);

        Assert.Equal("video.mp4", item.FileName);
        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
        Assert.Equal(server.Payload.Length, item.TotalBytes);
        Assert.Equal(item.HlsSegmentCount, item.HlsSegmentsDone);
        Assert.Equal(new[] { "video.mp4" }, FilesInFolder());
    }

    [Fact]
    public async Task A_period_with_the_same_initialisation_does_not_fetch_it_again()
    {
        await using var server = await TestServer.StartAsync(256 * 1024, TimeSpan.Zero);
        var item = NewItem(server.DashUrl("periods-same-init"));

        await RunAsync(item);

        Assert.Equal(1, server.Hits("/hls/part/0"));
        Assert.Equal(TestServer.HlsParts, item.HlsSegmentCount);
    }

    [Theory]
    [InlineData("segment-base")]
    [InlineData("whole")]
    [InlineData("list-ranges")]
    public async Task One_file_is_asked_for_only_in_ranges(string name)
    {
        await using var server = await TestServer.StartAsync(256 * 1024, TimeSpan.Zero);
        var item = NewItem(server.DashUrl(name));

        await RunAsync(item);

        Assert.NotEmpty(server.Ranges(TestServer.DashWholePath));
        Assert.All(server.Ranges(TestServer.DashWholePath), range => Assert.StartsWith("bytes=", range));
    }

    [Fact]
    public async Task Redirected_manifest_finds_its_parts()
    {
        await using var server = await TestServer.StartAsync(64 * 1024, TimeSpan.Zero);
        var item = NewItem(server.BaseUrl + "/dash/moved/number.mpd");

        await RunAsync(item);

        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
    }

    const int MiB = 1024 * 1024;

    static long LengthOf(string range)
    {
        var ends = range["bytes=".Length..].Split('-');
        return long.Parse(ends[1]) - long.Parse(ends[0]) + 1;
    }

    [Fact]
    public async Task Whole_file_is_fetched_in_pieces()
    {
        await using var server = await TestServer.StartAsync(9 * MiB + 5, TimeSpan.Zero);
        var item = NewItem(server.DashUrl("whole"));

        await RunAsync(item);

        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
        Assert.Equal(3, item.HlsSegmentCount);
        var ranges = server.Ranges(TestServer.DashWholePath);
        Assert.Contains("bytes=0-0", ranges);
        Assert.All(ranges, range => Assert.True(LengthOf(range) <= DashManifest.MaxPartBytes, range));
    }

    [Fact]
    public async Task Whole_file_without_ranges_still_downloads()
    {
        await using var server = await TestServer.StartAsync(256 * 1024, TimeSpan.Zero);
        var item = NewItem(server.DashUrl("whole-noranges"));

        await RunAsync(item);

        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
        Assert.Equal(1, item.HlsSegmentCount);
    }

    [Fact]
    public void Long_range_is_cut_into_pieces_that_join_back()
    {
        var address = new Uri("https://a.com/f.mp4");
        var pieces = DashManifest.Pieces(new DashPart(address, new ByteRange(10, 2L * DashManifest.MaxPartBytes + 1))).ToList();

        Assert.Equal(new[]
        {
            new ByteRange(10, DashManifest.MaxPartBytes),
            new ByteRange(10 + DashManifest.MaxPartBytes, DashManifest.MaxPartBytes),
            new ByteRange(10 + 2L * DashManifest.MaxPartBytes, 1),
        }, pieces.Select(p => p.Range));
    }

    [Fact]
    public void Part_that_is_short_enough_stays_as_it_is()
    {
        var whole = new DashPart(new Uri("https://a.com/f.mp4"));
        var exact = new DashPart(whole.Uri, new ByteRange(0, DashManifest.MaxPartBytes));

        Assert.Equal(whole, Assert.Single(DashManifest.Pieces(whole)));
        Assert.Equal(exact, Assert.Single(DashManifest.Pieces(exact)));
    }

    [Fact]
    public async Task Many_parts_over_several_periods_are_refused_too()
    {
        await using var server = await TestServer.StartAsync(1024, TimeSpan.Zero);
        var item = NewItem(server.DashUrl("periods-too-many"));

        var e = await Assert.ThrowsAsync<InvalidDataException>(() => RunAsync(item));

        Assert.Equal($"The manifest lists more than {DashManifest.MaxParts} parts.", e.Message);
    }

    [Fact]
    public async Task Downloads_the_best_quality_by_default()
    {
        await using var server = await TestServer.StartAsync(256 * 1024, TimeSpan.Zero);
        var item = NewItem(server.DashUrl("qualities"));

        await RunAsync(item);

        Assert.Equal(server.VariantPayload(2), File.ReadAllBytes(item.FullPath));
    }

    [Fact]
    public async Task Downloads_the_chosen_quality()
    {
        await using var server = await TestServer.StartAsync(256 * 1024, TimeSpan.Zero);
        var item = NewItem(server.DashUrl("qualities"));
        item.HlsBandwidth = TestServer.HlsBandwidths[1];

        await RunAsync(item);

        Assert.Equal(server.VariantPayload(1), File.ReadAllBytes(item.FullPath));
    }

    [Fact]
    public async Task Audio_is_kept_beside_the_video_without_ffmpeg()
    {
        await using var server = await TestServer.StartAsync(256 * 1024, TimeSpan.Zero);
        var item = NewItem(server.DashUrl("av"));

        await RunAsync(item);

        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
        Assert.Equal(server.AudioPayload, File.ReadAllBytes(Path.Combine(_dir, "video (audio).m4a")));
        Assert.Equal(2 * TestServer.HlsParts, item.HlsSegmentCount);
    }

    [Fact]
    public async Task Ffmpeg_joins_video_and_audio()
    {
        await using var server = await TestServer.StartAsync(256 * 1024, TimeSpan.Zero);
        var item = NewItem(server.DashUrl("av"));

        await RunAsync(item, ffmpeg: HlsDownloaderTests.FakeFfmpegIn(_dir));

        Assert.Contains("-map 0:v:0 -map 1:a:0", FfmpegArgs);
        Assert.Equal("video.mp4", item.FileName);
        Assert.Equal(new[] { "video.mp4" }, FilesInFolder());
    }

    [Fact]
    public async Task Audio_only_downloads_just_the_audio()
    {
        await using var server = await TestServer.StartAsync(256 * 1024, TimeSpan.Zero);
        var item = NewItem(server.DashUrl("av"));
        item.HlsAudioOnly = true;

        await RunAsync(item);

        Assert.Equal("video.m4a", item.FileName);
        Assert.Equal(server.AudioPayload, File.ReadAllBytes(item.FullPath));
        Assert.Equal(new[] { "video.m4a" }, FilesInFolder());
    }

    [Fact]
    public async Task Audio_only_with_no_audio_fails_clearly()
    {
        await using var server = await TestServer.StartAsync(64 * 1024, TimeSpan.Zero);
        var item = NewItem(server.DashUrl("number"));
        item.HlsAudioOnly = true;

        var e = await Assert.ThrowsAsync<InvalidDataException>(() => RunAsync(item));

        Assert.Equal("This stream has no separate audio track to download on its own.", e.Message);
    }

    [Theory]
    [InlineData("manifest.mpd", "manifest.mp4")]
    [InlineData("film", "film.mp4")]
    [InlineData("film.mp4", "film.mp4")]
    [InlineData("film.MP4", "film.MP4")]
    public async Task Name_follows_the_container(string given, string expected)
    {
        await using var server = await TestServer.StartAsync(64 * 1024, TimeSpan.Zero);
        var item = NewItem(server.DashUrl("number"), given);

        await RunAsync(item);

        Assert.Equal(expected, item.FileName);
        Assert.True(File.Exists(Path.Combine(_dir, expected)));
    }

    [Fact]
    public async Task Webm_stays_webm_even_with_ffmpeg()
    {
        await using var server = await TestServer.StartAsync(64 * 1024, TimeSpan.Zero);
        var item = NewItem(server.DashUrl("webm"));

        await RunAsync(item, ffmpeg: HlsDownloaderTests.FakeFfmpegIn(_dir));

        Assert.Equal("video.webm", item.FileName);
        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
        Assert.False(File.Exists(Path.Combine(_dir, "tools", "args.txt")), "ffmpeg was run on one track that needs nothing done");
    }

    [Fact]
    public async Task Webm_audio_beside_the_video_keeps_its_own_extension()
    {
        await using var server = await TestServer.StartAsync(64 * 1024, TimeSpan.Zero);
        var item = NewItem(server.DashUrl("webm-av"));

        await RunAsync(item);

        Assert.Equal(new[] { "video (audio).webm", "video.webm" }, FilesInFolder());
    }

    [Fact]
    public async Task Webm_tracks_joined_by_ffmpeg_are_mp4()
    {
        await using var server = await TestServer.StartAsync(64 * 1024, TimeSpan.Zero);
        var item = NewItem(server.DashUrl("webm-av"));

        await RunAsync(item, ffmpeg: HlsDownloaderTests.FakeFfmpegIn(_dir));

        Assert.Equal("video.mp4", item.FileName);
        Assert.Equal(new[] { "video.mp4" }, FilesInFolder());
    }

    [Fact]
    public async Task Ts_track_is_repackaged_by_ffmpeg()
    {
        await using var server = await TestServer.StartAsync(64 * 1024, TimeSpan.Zero);
        var item = NewItem(server.DashUrl("ts"));

        await RunAsync(item, ffmpeg: HlsDownloaderTests.FakeFfmpegIn(_dir));

        Assert.Equal("video.mp4", item.FileName);
        Assert.Contains("-f mp4", FfmpegArgs);
    }

    [Fact]
    public async Task Ts_track_stays_ts_without_ffmpeg()
    {
        await using var server = await TestServer.StartAsync(64 * 1024, TimeSpan.Zero);
        var item = NewItem(server.DashUrl("ts"));

        await RunAsync(item);

        Assert.Equal("video.ts", item.FileName);
    }

    /// <summary>Runs one connection at a time until <paramref name="parts"/> parts are saved, then cancels, as a pause does.</summary>
    static async Task PauseAfterAsync(DownloadItem item, int parts)
    {
        item.MaxConnections = 1;
        // Slow enough to be caught partway, at about two parts a second.
        item.SpeedLimitKBps = 64;
        using var cts = new CancellationTokenSource();
        var run = NewDownloader().RunAsync(item, cts.Token);
        await Until(() => item.HlsSegmentsDone >= parts);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        item.SpeedLimitKBps = 0;
    }

    static string[] SavedParts(DownloadItem item) =>
        Directory.GetFiles(item.PartsFolderPath, "*.seg").Select(Path.GetFileNameWithoutExtension).Order().ToArray()!;

    static void AssertAskedOnce(TestServer server, string[] saved)
    {
        Assert.NotEmpty(saved);
        // Parts are named by their place in the track, which is also their number on this server.
        foreach (var part in saved)
            Assert.Equal(1, server.Hits($"/hls/part/{int.Parse(part)}"));
    }

    [Fact]
    public async Task Pause_and_resume_gives_identical_bytes()
    {
        await using var server = await TestServer.StartAsync(256 * 1024, TimeSpan.Zero);
        var item = NewItem(server.DashUrl("number"));
        await PauseAfterAsync(item, 3);
        var saved = SavedParts(item);

        await RunAsync(item);

        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
        AssertAskedOnce(server, saved);
        Assert.False(Directory.Exists(item.PartsFolderPath));
    }

    [Fact]
    public async Task Restart_resumes_from_saved_state()
    {
        await using var server = await TestServer.StartAsync(256 * 1024, TimeSpan.Zero);
        var item = NewItem(server.DashUrl("number"));
        await PauseAfterAsync(item, 3);
        var saved = SavedParts(item);
        Store.Save(StatePath, new AppState { Downloads = { item } });

        var restored = Assert.Single(Store.Load(StatePath).Downloads);
        Assert.Equal(DownloadKind.Dash, restored.Kind);
        await RunAsync(restored);

        Assert.Equal(server.Payload, File.ReadAllBytes(restored.FullPath));
        AssertAskedOnce(server, saved);
    }

    [Fact]
    public async Task Byte_ranges_resume_too()
    {
        await using var server = await TestServer.StartAsync(256 * 1024, TimeSpan.Zero);
        var item = NewItem(server.DashUrl("list-ranges"));
        // As a pause leaves it: the first two in their files, the rest to fetch.
        var bytes = 256 * 1024 / TestServer.HlsParts;
        Directory.CreateDirectory(item.PartsFolderPath);
        File.WriteAllBytes(Path.Combine(item.PartsFolderPath, "000000.seg"), server.Payload[..bytes]);
        File.WriteAllBytes(Path.Combine(item.PartsFolderPath, "000001.seg"), server.Payload[bytes..(2 * bytes)]);
        item.HlsSegmentCount = TestServer.HlsParts;

        await RunAsync(item);

        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
        Assert.DoesNotContain($"bytes=0-{bytes - 1}", server.Ranges(TestServer.DashWholePath));
    }

    [Fact]
    public async Task Paused_download_renamed_keeps_its_parts()
    {
        await using var server = await TestServer.StartAsync(256 * 1024, TimeSpan.Zero);
        var item = NewItem(server.DashUrl("number"));
        await PauseAfterAsync(item, 3);
        var saved = SavedParts(item);
        // What Move/Rename does: the folder of parts goes with the name, whatever the name says.
        var before = item.PartsFolderPath;
        item.FileName = "film.mkv";
        Directory.Move(before, item.PartsFolderPath);

        await RunAsync(item);

        Assert.Equal("film.mkv", item.FileName);
        Assert.Equal(server.Payload, File.ReadAllBytes(Path.Combine(_dir, "film.mkv")));
        AssertAskedOnce(server, saved);
    }

    [Fact]
    public async Task Outage_waits_instead_of_failing()
    {
        await using var server = await TestServer.StartAsync(MiB, TimeSpan.Zero);
        server.HlsOutageAfterParts = 2;
        server.HlsOutage = TimeSpan.FromSeconds(1);
        var item = NewItem(server.DashUrl("number"));
        item.Status = DownloadStatus.Downloading;
        var run = new HlsDownloader(new RateLimiter(), new Settings(), null) { Retry = new RetryPolicy { FirstBackoff = Short, MaxBackoff = Short } }
            .RunAsync(item, CancellationToken.None);
        var seen = new HashSet<string>();

        await Until(() => { seen.Add(item.StatusText); return run.IsCompleted; });
        await run;

        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
        Assert.Contains(seen, s => s.StartsWith("Reconnecting ("));
    }

    [Fact]
    public async Task Starting_while_the_server_is_down_waits()
    {
        await using var server = await TestServer.StartAsync(64 * 1024, TimeSpan.Zero);
        server.Down = true;
        var item = NewItem(server.DashUrl("whole"));
        var run = new HlsDownloader(new RateLimiter(), new Settings(), null) { Retry = new RetryPolicy { FirstBackoff = Short, MaxBackoff = Short } }
            .RunAsync(item, CancellationToken.None);
        await Until(() => (item.StatusNote ?? "").StartsWith("Reconnecting ("));

        server.Down = false;
        await run.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
    }

    [Fact]
    public async Task Per_download_speed_limit_slows_it()
    {
        await using var server = await TestServer.StartAsync(384 * 1024, TimeSpan.Zero);
        var item = NewItem(server.DashUrl("number"));
        item.SpeedLimitKBps = 128;

        var watch = Stopwatch.StartNew();
        await RunAsync(item);

        // 384 KB at 128 KB/s is three seconds; the bucket may hold up to one second of burst.
        Assert.True(watch.Elapsed >= TimeSpan.FromSeconds(1.5), $"took only {watch.Elapsed}");
        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
    }

    [Fact]
    public async Task Global_speed_limit_slows_it()
    {
        await using var server = await TestServer.StartAsync(384 * 1024, TimeSpan.Zero);
        var item = NewItem(server.DashUrl("whole"));

        var watch = Stopwatch.StartNew();
        await NewDownloader(limiter: new RateLimiter { BytesPerSecond = 128 * 1024 }).RunAsync(item, CancellationToken.None);

        Assert.True(watch.Elapsed >= TimeSpan.FromSeconds(1.5), $"took only {watch.Elapsed}");
        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
    }

    [Fact]
    public async Task Server_connection_limit_caps_parallel_parts()
    {
        await using var server = await TestServer.StartAsync(256 * 1024);
        var settings = new Settings { ServerLimits = { new ServerLimit { Sites = "127.0.0.1", Connections = 2 } } };
        var item = NewItem(server.DashUrl("number"));

        await RunAsync(item, settings);

        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
        Assert.True(server.MaxConcurrentRequests <= 2, $"saw {server.MaxConcurrentRequests} parallel requests");
    }

    [Fact]
    public async Task Several_connections_are_used()
    {
        await using var server = await TestServer.StartAsync(256 * 1024);
        var item = NewItem(server.DashUrl("number"));

        await RunAsync(item);

        Assert.True(server.MaxConcurrentRequests >= 2, $"saw {server.MaxConcurrentRequests} parallel requests");
    }

    [Fact]
    public async Task Redownload_replaces_the_file()
    {
        await using var server = await TestServer.StartAsync(64 * 1024, TimeSpan.Zero);
        var item = NewItem(server.DashUrl("number"));
        await RunAsync(item);
        File.WriteAllText(item.FullPath, "damaged");

        // What DownloadManager.Restart resets on a finished download.
        item.OverwritePath = item.FullPath;
        item.HlsSegmentCount = 0;
        item.HlsSegmentsDone = 0;
        item.DownloadedBytes = 0;
        await RunAsync(item);

        Assert.Equal("video.mp4", item.FileName);
        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
        Assert.Equal(new[] { "video.mp4" }, FilesInFolder());
    }

    [Fact]
    public async Task Sync_check_sees_no_change()
    {
        await using var server = await TestServer.StartAsync(64 * 1024, TimeSpan.Zero);
        var item = NewItem(server.DashUrl("changing"));
        await RunAsync(item);

        Assert.False(await HlsDownloader.HasChangedAsync(item, new Settings(), CancellationToken.None));
    }

    [Fact]
    public async Task Sync_check_sees_a_changed_manifest()
    {
        await using var server = await TestServer.StartAsync(64 * 1024, TimeSpan.Zero);
        var item = NewItem(server.DashUrl("changing"));
        await RunAsync(item);

        server.DashChanged = true;

        Assert.True(await HlsDownloader.HasChangedAsync(item, new Settings(), CancellationToken.None));
    }

    [Fact]
    public async Task Sync_check_ignores_tokens_in_queries()
    {
        await using var server = await TestServer.StartAsync(64 * 1024, TimeSpan.Zero);
        var item = NewItem(server.DashUrl("tokens"));
        await RunAsync(item);

        Assert.False(await HlsDownloader.HasChangedAsync(item, new Settings(), CancellationToken.None));
    }

    [Fact]
    public async Task Sync_check_sees_a_whole_file_change_size()
    {
        await using var server = await TestServer.StartAsync(64 * 1024, TimeSpan.Zero);
        var item = NewItem(server.DashUrl("sized"));
        await RunAsync(item);
        Assert.False(await HlsDownloader.HasChangedAsync(item, new Settings(), CancellationToken.None));

        server.DashChanged = true;

        Assert.True(await HlsDownloader.HasChangedAsync(item, new Settings(), CancellationToken.None));
    }

    [Fact]
    public async Task Parts_are_flushed_before_they_count()
    {
        // Parts closed without a flush lose their bytes, so the joined file is right only if each part was flushed.
        await using var server = await TestServer.StartAsync(256 * 1024, TimeSpan.Zero);
        var item = NewItem(server.DashUrl("whole"));
        var downloader = new HlsDownloader(new RateLimiter(), new Settings(), null) { OpenPartFile = path => new LossyPartFile(path) };

        await downloader.RunAsync(item, CancellationToken.None);

        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
    }

    [Fact]
    public async Task Parts_use_the_temp_folder()
    {
        await using var server = await TestServer.StartAsync(256 * 1024, TimeSpan.Zero);
        var temp = Path.Combine(_dir, "temp");
        Directory.CreateDirectory(temp);
        var settings = new Settings { TempFolder = temp };
        var item = NewItem(server.DashUrl("number"));
        item.MaxConnections = 1;
        item.SpeedLimitKBps = 64;
        using var cts = new CancellationTokenSource();

        var run = RunAsync(item, settings, ct: cts.Token);
        await Until(() => item.HlsSegmentsDone >= 1);
        Assert.True(Directory.Exists(Path.Combine(temp, $"{item.Id:N}.admparts")));
        Assert.False(Directory.Exists(Path.Combine(_dir, "video.mp4.admparts")));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task Part_on_another_site_gets_no_cookies()
    {
        await using var server = await TestServer.StartAsync(64 * 1024, TimeSpan.Zero);
        var item = NewItem(server.DashUrl("other-site"));
        item.Cookies = "session=secret";

        await RunAsync(item);

        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
        Assert.NotEmpty(server.CookiesSeen);
        Assert.All(server.CookiesSeen, seen => Assert.Equal(("localhost", ""), seen));
    }

    [Fact]
    public async Task Part_on_the_same_site_gets_its_cookies()
    {
        await using var server = await TestServer.StartAsync(64 * 1024, TimeSpan.Zero);
        var item = NewItem(server.DashUrl("same-site"));
        item.Cookies = "session=secret";

        await RunAsync(item);

        Assert.NotEmpty(server.CookiesSeen);
        Assert.All(server.CookiesSeen, seen => Assert.Equal(("127.0.0.1", "session=secret"), seen));
    }

    [Theory]
    [InlineData("drm", "Streams protected with DRM are not supported.")]
    [InlineData("live", "Live DASH streams are not supported.")]
    public async Task Refused_manifest_fails_without_retrying(string name, string message)
    {
        await using var server = await TestServer.StartAsync(64 * 1024, TimeSpan.Zero);
        var item = NewItem(server.DashUrl(name));

        var e = await Assert.ThrowsAsync<NotSupportedException>(() => RunAsync(item));

        Assert.Equal(message, e.Message);
        Assert.Equal(1, server.Hits($"/dash/{name}.mpd"));
        Assert.Equal(0, server.Hits("/hls/part/0"));
    }

    [Fact]
    public async Task Page_instead_of_a_manifest_fails_clearly()
    {
        await using var server = await TestServer.StartAsync(64 * 1024, TimeSpan.Zero);
        var item = NewItem(server.DashUrl("not-a-manifest"));

        var e = await Assert.ThrowsAsync<InvalidDataException>(() => RunAsync(item));

        Assert.Equal("The URL did not return a DASH manifest.", e.Message);
    }

    [Fact]
    public async Task Manifest_too_large_stops_being_read()
    {
        await using var server = await TestServer.StartAsync(1024, TimeSpan.Zero);
        var item = NewItem(server.DashUrl("too-large"));

        var e = await Assert.ThrowsAsync<InvalidDataException>(() => RunAsync(item));

        Assert.Equal("The server sent a playlist or manifest too large to read.", e.Message);
    }

    [Fact]
    public void Dash_is_always_resumable()
    {
        Assert.Equal("Yes", new DownloadItem { Kind = DownloadKind.Dash }.ResumeText);
    }

    [Fact]
    public void Progress_counts_parts()
    {
        var item = new DownloadItem { Kind = DownloadKind.Dash, HlsSegmentCount = 4, HlsSegmentsDone = 1 };

        Assert.Equal(25, item.Progress);
    }

    DownloadManager NewManager()
    {
        var m = new DownloadManager(StatePath, new SynchronizationContext());
        m.State.Settings.DownloadRoot = _dir;
        return m;
    }

    static async Task<DownloadItem> DownloadAsync(DownloadManager m, DownloadRequest request)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        m.DownloadCompleted += _ => done.TrySetResult();
        m.DownloadFailed += i => done.TrySetException(new Exception(i.Error));
        var item = m.Add(request, startNow: true);
        await done.Task.WaitAsync(TimeSpan.FromSeconds(30));
        return item;
    }

    [Theory]
    [InlineData(DownloadKind.Dash, "video.mp4")]
    [InlineData(DownloadKind.Hls, "video.ts")]
    public void Nameless_stream_is_named_for_its_kind_from_the_start(DownloadKind kind, string expected)
    {
        var item = NewManager().Add(new DownloadRequest { Url = "http://127.0.0.1:9/stream", Kind = kind }, startNow: false);

        Assert.Equal(expected, item.FileName);
    }

    [Fact]
    public async Task Dash_download_completes_through_the_manager()
    {
        await using var server = await TestServer.StartAsync(64 * 1024, TimeSpan.Zero);
        var m = NewManager();

        var item = await DownloadAsync(m, new DownloadRequest { Url = server.DashUrl("number"), Kind = DownloadKind.Dash });

        Assert.Equal(DownloadStatus.Completed, item.Status);
        Assert.Equal("video.mp4", item.FileName);
        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
    }

    [Fact]
    public async Task Dash_ignores_a_hash()
    {
        await using var server = await TestServer.StartAsync(64 * 1024, TimeSpan.Zero);
        var m = NewManager();
        var request = new DownloadRequest { Url = server.DashUrl("number"), Kind = DownloadKind.Dash, Sha256 = new string('a', 64) };

        var item = await DownloadAsync(m, request);

        Assert.Equal(DownloadStatus.Completed, item.Status);
        Assert.Null(item.ExpectedSha256);
    }

    [Fact]
    public async Task Restart_redownloads_a_finished_dash_video_in_place()
    {
        await using var server = await TestServer.StartAsync(64 * 1024, TimeSpan.Zero);
        var m = NewManager();
        var item = await DownloadAsync(m, new DownloadRequest { Url = server.DashUrl("number"), Kind = DownloadKind.Dash });
        File.WriteAllText(item.FullPath, "damaged");

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        m.DownloadCompleted += _ => done.TrySetResult();
        m.DownloadFailed += i => done.TrySetException(new Exception(i.Error));
        m.Restart(item);
        await done.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
        Assert.Single(Directory.GetFiles(item.Directory));
    }

    [Fact]
    public async Task Sync_queue_downloads_a_changed_manifest_again()
    {
        await using var server = await TestServer.StartAsync(64 * 1024, TimeSpan.Zero);
        var m = NewManager();
        var item = m.Add(new DownloadRequest { Url = server.DashUrl("changing"), Kind = DownloadKind.Dash, FileName = "video.mp4" }, startNow: false, m.SyncQueue);
        await RunQueueAsync(m);
        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
        var first = server.Hits("/hls/part/1");

        await RunQueueAsync(m);
        Assert.Equal(first, server.Hits("/hls/part/1"));

        server.DashChanged = true;
        await RunQueueAsync(m);
        Assert.Equal(first + 1, server.Hits("/hls/part/1"));
    }

    static async Task RunQueueAsync(DownloadManager m)
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnFinished(DownloadQueue _) => finished.TrySetResult();
        void OnFailed(DownloadItem i) => finished.TrySetException(new Exception(i.Error));
        m.QueueFinished += OnFinished;
        m.DownloadFailed += OnFailed;
        try
        {
            m.StartQueue(m.SyncQueue);
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            m.QueueFinished -= OnFinished;
            m.DownloadFailed -= OnFailed;
        }
    }
}
