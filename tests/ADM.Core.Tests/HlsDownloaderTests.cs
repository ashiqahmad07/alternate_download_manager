using System.Diagnostics;
using ADM.Core;

namespace ADM.Core.Tests;

public sealed class HlsDownloaderTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "adm-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    DownloadItem NewItem(string url, string fileName = "video.ts") =>
        new() { Url = url, Directory = _dir, FileName = fileName, Kind = DownloadKind.Hls, MaxConnections = 8 };

    // Without an ffmpeg, TS stays TS whatever is on this machine's PATH.
    static Task RunAsync(DownloadItem item, Settings? settings = null, string? ffmpeg = null) =>
        new HlsDownloader(new RateLimiter(), settings ?? new Settings(), ffmpeg).RunAsync(item, CancellationToken.None);

    /// <summary>
    /// A stand-in for ffmpeg that copies its first -i input to its last argument, as a remux would,
    /// and records its arguments in <see cref="FfmpegArgs"/>.
    /// </summary>
    string FakeFfmpeg() => FakeFfmpegIn(_dir);

    internal static string FakeFfmpegIn(string dir)
    {
        var path = Path.Combine(dir, "tools", "ffmpeg.cmd");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """
            @echo off
            setlocal
            echo %* > "%~dp0args.txt"
            :next
            if "%~2"=="" goto last
            if "%~1"=="-i" if not defined source set "source=%~2"
            shift
            goto next
            :last
            copy /y "%source%" "%~1" >nul
            """);
        return path;
    }

    string FfmpegArgs => File.ReadAllText(Path.Combine(_dir, "tools", "args.txt"));

    string[] FilesInFolder() => Directory.GetFiles(_dir).Select(Path.GetFileName).ToArray()!;

    [Fact]
    public async Task Downloads_a_ts_playlist()
    {
        await using var server = await TestServer.StartAsync(256 * 1024, TimeSpan.Zero);
        var item = NewItem(server.HlsTsUrl);

        await RunAsync(item);

        Assert.Equal("video.ts", item.FileName);
        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
        Assert.Equal(server.Payload.Length, item.DownloadedBytes);
        Assert.Equal(TestServer.HlsParts, item.HlsSegmentsDone);
    }

    [Fact]
    public async Task Downloads_a_fragmented_mp4_playlist()
    {
        await using var server = await TestServer.StartAsync(256 * 1024, TimeSpan.Zero);
        var item = NewItem(server.HlsFmp4Url);

        await RunAsync(item);

        Assert.Equal("video.mp4", item.FileName);
        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
        Assert.Equal(server.Payload.Length, item.DownloadedBytes);
    }

    [Fact]
    public async Task Decrypts_an_aes128_playlist()
    {
        await using var server = await TestServer.StartAsync(256 * 1024, TimeSpan.Zero);
        var item = NewItem(server.HlsAesUrl);

        await RunAsync(item);

        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
        // Progress counts the decrypted bytes kept, not the padded ones received.
        Assert.Equal(server.Payload.Length, item.DownloadedBytes);
    }

    [Fact]
    public async Task Part_on_another_site_gets_no_cookies()
    {
        await using var server = await TestServer.StartAsync(64 * 1024, TimeSpan.Zero);
        var item = NewItem(server.HlsOtherSiteUrl);
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
        var item = NewItem(server.HlsSameSiteUrl);
        item.Cookies = "session=secret";

        await RunAsync(item);

        Assert.NotEmpty(server.CookiesSeen);
        Assert.All(server.CookiesSeen, seen => Assert.Equal(("127.0.0.1", "session=secret"), seen));
    }

    [Fact]
    public async Task Playlist_on_another_site_gets_no_cookies()
    {
        await using var server = await TestServer.StartAsync(64 * 1024, TimeSpan.Zero);
        var item = NewItem(server.HlsOtherSiteMasterUrl);
        item.Cookies = "session=secret";

        await RunAsync(item);

        Assert.Equal(("localhost", ""), Assert.Single(server.CookiesSeen));
    }

    [Fact]
    public async Task Key_on_another_site_gets_no_cookies()
    {
        await using var server = await TestServer.StartAsync(64 * 1024, TimeSpan.Zero);
        var item = NewItem(server.HlsOtherSiteKeyUrl);
        item.Cookies = "session=secret";

        await RunAsync(item);

        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
        Assert.NotEmpty(server.CookiesSeen);
        Assert.All(server.CookiesSeen, seen => Assert.Equal(("localhost", ""), seen));
    }

    [Fact]
    public async Task Redirected_playlist_finds_its_parts()
    {
        await using var server = await TestServer.StartAsync(64 * 1024, TimeSpan.Zero);
        var item = NewItem(server.HlsMovedUrl);

        await RunAsync(item);

        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
    }

    [Fact]
    public async Task Redirected_master_playlist_finds_its_qualities()
    {
        await using var server = await TestServer.StartAsync(64 * 1024, TimeSpan.Zero);
        var item = NewItem(server.HlsMovedMasterUrl);

        await RunAsync(item);

        Assert.Equal(server.VariantPayload(2), File.ReadAllBytes(item.FullPath));
    }

    [Fact]
    public async Task Redirected_quality_finds_its_parts()
    {
        await using var server = await TestServer.StartAsync(64 * 1024, TimeSpan.Zero);
        var item = NewItem(server.HlsMovedQualityUrl);

        await RunAsync(item);

        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
    }

    [Fact]
    public async Task Redirected_live_playlist_is_reloaded_from_where_it_was_asked()
    {
        // Where a redirect ends can be signed and expire, so each reload starts from the address it was given.
        await using var server = await TestServer.StartAsync(1024, TimeSpan.Zero);
        var item = NewItem(server.BaseUrl + TestServer.HlsMovedLivePath);
        var hls = new HlsDownloader(new RateLimiter(), new Settings(), null) { LivePoll = TimeSpan.FromMilliseconds(50) };
        using var stop = new CancellationTokenSource();

        var run = hls.RunAsync(item, CancellationToken.None, stop.Token);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (server.Hits(TestServer.HlsMovedLivePath) < 3 && DateTime.UtcNow < deadline) await Task.Delay(20);
        stop.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.True(server.Hits(TestServer.HlsMovedLivePath) >= 3, $"asked {server.Hits(TestServer.HlsMovedLivePath)} times");
        Assert.True(File.Exists(item.FullPath));
    }

    [Fact]
    public async Task Per_download_speed_limit_slows_an_hls_download()
    {
        await using var server = await TestServer.StartAsync(384 * 1024, TimeSpan.Zero);
        var item = NewItem(server.HlsTsUrl);
        item.SpeedLimitKBps = 128;

        var watch = Stopwatch.StartNew();
        await RunAsync(item);

        // 384 KB at 128 KB/s is three seconds; the bucket may hold up to one second of burst.
        Assert.True(watch.Elapsed >= TimeSpan.FromSeconds(1.5), $"took only {watch.Elapsed}");
        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
    }

    [Fact]
    public async Task Server_connection_limit_caps_parallel_segments()
    {
        await using var server = await TestServer.StartAsync(256 * 1024);
        var settings = new Settings { ServerLimits = { new ServerLimit { Sites = "127.0.0.1", Connections = 2 } } };
        var item = NewItem(server.HlsTsUrl);

        await RunAsync(item, settings);

        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
        Assert.True(server.MaxConcurrentRequests <= 2, $"saw {server.MaxConcurrentRequests} parallel requests");
    }

    [Fact]
    public async Task Failed_segment_attempt_is_not_counted_twice()
    {
        // Every segment's first attempt breaks off halfway; only the retried copies may count.
        await using var server = await TestServer.StartAsync(256 * 1024, TimeSpan.Zero);
        var item = NewItem(server.HlsFlakyUrl);

        await RunAsync(item);

        Assert.Equal(2 * TestServer.HlsParts, server.HlsFlakyRequests);
        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
        Assert.Equal(server.Payload.Length, item.DownloadedBytes);
    }

    [Fact]
    public async Task Overwrite_replaces_the_recorded_file()
    {
        await using var server = await TestServer.StartAsync(256 * 1024, TimeSpan.Zero);
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "video.ts"), "old copy");
        var item = NewItem(server.HlsTsUrl);
        item.OverwritePath = Path.Combine(_dir, "video.ts");

        await RunAsync(item);

        Assert.Equal("video.ts", item.FileName);
        Assert.Equal(server.Payload, File.ReadAllBytes(Path.Combine(_dir, "video.ts")));
        Assert.Equal(new[] { "video.ts" }, FilesInFolder());
    }

    [Fact]
    public async Task Overwrite_is_limited_to_the_recorded_file()
    {
        await using var server = await TestServer.StartAsync(256 * 1024, TimeSpan.Zero);
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "video.ts"), "keep me");
        var item = NewItem(server.HlsTsUrl);
        item.OverwritePath = Path.Combine(_dir, "other.ts");

        await RunAsync(item);

        Assert.Equal("keep me", File.ReadAllText(Path.Combine(_dir, "video.ts")));
        Assert.Equal("video (1).ts", item.FileName);
        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
    }

    [Fact]
    public async Task Remuxed_redownload_replaces_the_mp4()
    {
        await using var server = await TestServer.StartAsync(256 * 1024, TimeSpan.Zero);
        var ffmpeg = FakeFfmpeg();
        var item = NewItem(server.HlsTsUrl);
        await RunAsync(item, ffmpeg: ffmpeg);
        Assert.Equal("video.mp4", item.FileName);
        File.WriteAllText(item.FullPath, "damaged");

        // What DownloadManager.Restart resets on a finished download.
        item.OverwritePath = item.FullPath;
        item.HlsSegmentCount = 0;
        item.HlsSegmentsDone = 0;
        item.DownloadedBytes = 0;
        item.TotalBytes = -1;
        await RunAsync(item, ffmpeg: ffmpeg);

        Assert.Equal("video.mp4", item.FileName);
        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
        Assert.Equal(new[] { "video.mp4" }, FilesInFolder());
    }

    [Fact]
    public async Task Downloads_the_best_quality_by_default()
    {
        await using var server = await TestServer.StartAsync(256 * 1024, TimeSpan.Zero);
        var item = NewItem(server.HlsMasterUrl);

        await RunAsync(item);

        Assert.Equal(server.VariantPayload(2), File.ReadAllBytes(item.FullPath));
    }

    [Fact]
    public async Task Downloads_the_chosen_quality()
    {
        await using var server = await TestServer.StartAsync(256 * 1024, TimeSpan.Zero);
        var item = NewItem(server.HlsMasterUrl);
        item.HlsBandwidth = TestServer.HlsBandwidths[1];

        await RunAsync(item);

        Assert.Equal(server.VariantPayload(1), File.ReadAllBytes(item.FullPath));
    }

    [Fact]
    public async Task Separate_audio_is_kept_next_to_the_video_without_ffmpeg()
    {
        await using var server = await TestServer.StartAsync(256 * 1024, TimeSpan.Zero);
        var item = NewItem(server.HlsAvUrl);

        await RunAsync(item);

        Assert.Equal(new[] { "video (audio).aac", "video.en.vtt", "video.ts" }, FilesInFolder().Order());
        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
        Assert.Equal(server.AudioPayload, File.ReadAllBytes(Path.Combine(_dir, "video (audio).aac")));
        // Video and audio parts both count toward progress.
        Assert.Equal(2 * TestServer.HlsParts, item.HlsSegmentCount);
    }

    [Fact]
    public async Task Ffmpeg_joins_the_separate_audio_to_the_video()
    {
        await using var server = await TestServer.StartAsync(256 * 1024, TimeSpan.Zero);
        var item = NewItem(server.HlsAvUrl);

        await RunAsync(item, ffmpeg: FakeFfmpeg());

        Assert.Equal(new[] { "video.en.vtt", "video.mp4" }, FilesInFolder().Order());
        var args = FfmpegArgs;
        Assert.Matches(@"-i \S*video\.ts\.admpart\S* -i \S*audio\.joined\S* -map 0:v:0 -map 1:a:0 -c copy", args);
    }

    [Fact]
    public async Task Subtitles_are_joined_into_one_webvtt_file()
    {
        await using var server = await TestServer.StartAsync(256 * 1024, TimeSpan.Zero);
        var item = NewItem(server.HlsAvUrl);

        await RunAsync(item);

        Assert.Equal(
            "WEBVTT\n\n00:00:01.000 --> 00:00:02.000\nHello\n\n00:00:05.000 --> 00:00:06.000\nWorld\n",
            File.ReadAllText(Path.Combine(_dir, "video.en.vtt")));
    }

    // ---- Subtitle timing

    const string Cue = "\n00:01.000 --> 00:02.000\nHello\n";

    static string Segment(string map, string cues = Cue) => "WEBVTT\n" + map + "\n" + cues;

    [Fact]
    public void Timestamp_map_moves_cues()
    {
        var cues = HlsDownloader.ShiftCues(Segment("X-TIMESTAMP-MAP=MPEGTS:900000,LOCAL:00:00:00.000"), videoStart: 0);

        Assert.Contains("00:11.000 --> 00:12.000", cues);
        Assert.Contains("Hello", cues);
    }

    [Fact]
    public void A_map_can_name_local_time_first()
    {
        var cues = HlsDownloader.ShiftCues(Segment("X-TIMESTAMP-MAP=LOCAL:00:00:00.000,MPEGTS:900000"), videoStart: 0);

        Assert.Contains("00:11.000 --> 00:12.000", cues);
    }

    [Fact]
    public void Video_start_is_taken_off()
    {
        var cues = HlsDownloader.ShiftCues(Segment("X-TIMESTAMP-MAP=MPEGTS:900000,LOCAL:00:00:00.000"), videoStart: 900000);

        Assert.Contains("00:01.000 --> 00:02.000", cues);
    }

    [Fact]
    public void Local_time_in_the_map_counts()
    {
        var cues = HlsDownloader.ShiftCues(Segment("X-TIMESTAMP-MAP=MPEGTS:900000,LOCAL:00:00:10.000"), videoStart: 0);

        Assert.Contains("00:01.000 --> 00:02.000", cues);
    }

    [Fact]
    public void The_clock_wrapping_is_handled()
    {
        // The video starts a second before the 33-bit clock wraps, and the map is a second after it.
        var cues = HlsDownloader.ShiftCues(Segment("X-TIMESTAMP-MAP=MPEGTS:90000,LOCAL:00:00:00.000"), videoStart: (1L << 33) - 90000);

        Assert.Contains("00:03.000 --> 00:04.000", cues);
    }

    [Fact]
    public void Short_cue_times_stay_valid()
    {
        var cues = HlsDownloader.ShiftCues(
            Segment("X-TIMESTAMP-MAP=MPEGTS:900000,LOCAL:00:00:00.000", "\n01:00.000 --> 01:02.000\nOne\n\n00:00:01.000 --> 00:00:02.000 align:start\nTwo\n"),
            videoStart: 0);

        Assert.Contains("01:10.000 --> 01:12.000\nOne", cues);
        Assert.Contains("00:00:11.000 --> 00:00:12.000 align:start\nTwo", cues);
    }

    [Fact]
    public void Cues_passing_an_hour_gain_their_hours()
    {
        var cues = HlsDownloader.ShiftCues(Segment("X-TIMESTAMP-MAP=MPEGTS:324000000,LOCAL:00:00:00.000"), videoStart: 0);

        Assert.Contains("01:00:01.000 --> 01:00:02.000", cues);
    }

    [Fact]
    public void A_cue_before_the_video_starts_stays_at_zero()
    {
        var cues = HlsDownloader.ShiftCues(Segment("X-TIMESTAMP-MAP=MPEGTS:0,LOCAL:00:00:00.000"), videoStart: 900000);

        Assert.Contains("00:00.000 --> 00:00.000", cues);
    }

    [Theory]
    [InlineData("X-TIMESTAMP-MAP=MPEGTS:abc,LOCAL:00:00:00.000")]
    [InlineData("X-TIMESTAMP-MAP=MPEGTS:900000")]
    [InlineData("X-TIMESTAMP-MAP=MPEGTS:900000,LOCAL:soon")]
    [InlineData("X-TIMESTAMP-MAP=")]
    [InlineData("NOTE no map here")]
    public void Malformed_timestamp_map_leaves_cues_alone(string map)
    {
        var cues = HlsDownloader.ShiftCues(Segment(map), videoStart: 0);

        Assert.Contains("00:01.000 --> 00:02.000", cues);
    }

    [Fact]
    public void A_cue_time_that_cannot_be_read_is_left_as_it_came()
    {
        var cues = HlsDownloader.ShiftCues(Segment("X-TIMESTAMP-MAP=MPEGTS:900000,LOCAL:00:00:00.000", "\nsoon --> later\nHello\n"), videoStart: 0);

        Assert.Contains("soon --> later", cues);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(900000)]
    [InlineData((1L << 33) - 1)]
    public void First_pts_is_read_from_a_ts_part(long pts) =>
        Assert.Equal(pts, HlsDownloader.FirstPts(TestServer.TsPacketWithPts(pts)));

    [Fact]
    public void First_pts_of_something_else_is_unknown()
    {
        Assert.Null(HlsDownloader.FirstPts(new byte[1000]));
        Assert.Null(HlsDownloader.FirstPts(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public async Task Subtitles_line_up_with_a_ts_video()
    {
        // The subtitles' map says 10 seconds in, and the video starts at 5 seconds, so a cue at 1 second moves to 6.
        await using var server = await TestServer.StartAsync(64 * 1024, TimeSpan.Zero);
        server.HlsPtsStart = 5 * 90000;
        var item = NewItem(server.HlsPtsUrl);
        item.HlsSubtitles = true;

        await RunAsync(item);

        var vtt = File.ReadAllText(Path.Combine(_dir, "video.en.vtt"));
        Assert.Contains("00:00:06.000 --> 00:00:07.000", vtt);
    }

    // ---- Byte ranges

    [Fact]
    public async Task Byte_range_playlist_joins_its_ranges()
    {
        await using var server = await TestServer.StartAsync(256 * 1024, TimeSpan.Zero);
        var item = NewItem(server.HlsByteRangeUrl);

        await RunAsync(item);

        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
        Assert.Equal(TestServer.HlsParts, server.Ranges("/hls/whole.bin").Distinct().Count());
    }

    [Fact]
    public async Task Byte_range_without_offset_follows_the_last_part()
    {
        await using var server = await TestServer.StartAsync(256 * 1024, TimeSpan.Zero);
        var item = NewItem(server.HlsByteRangeFollowingUrl);

        await RunAsync(item);

        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
    }

    [Fact]
    public async Task Byte_range_with_nothing_before_it_is_malformed()
    {
        await using var server = await TestServer.StartAsync(256 * 1024, TimeSpan.Zero);

        await Assert.ThrowsAsync<InvalidDataException>(() => RunAsync(NewItem(server.HlsByteRangeMalformedUrl)));
    }

    [Fact]
    public async Task Byte_range_map_is_fetched_with_its_range()
    {
        await using var server = await TestServer.StartAsync(256 * 1024, TimeSpan.Zero);
        var item = NewItem(server.HlsByteRangeMapUrl);

        await RunAsync(item);

        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
    }

    [Fact]
    public async Task Byte_range_with_aes_decrypts_each_part()
    {
        await using var server = await TestServer.StartAsync(256 * 1024, TimeSpan.Zero);
        var item = NewItem(server.HlsByteRangeAesUrl);

        await RunAsync(item);

        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
    }

    [Fact]
    public async Task Byte_range_download_resumes()
    {
        await using var server = await TestServer.StartAsync(256 * 1024, TimeSpan.Zero);
        var item = NewItem(server.HlsByteRangeUrl);
        // As a pause leaves it: the first part in its file, the rest to fetch.
        var length = 256 * 1024 / TestServer.HlsParts;
        Directory.CreateDirectory(item.PartsFolderPath);
        File.WriteAllBytes(Path.Combine(item.PartsFolderPath, "000000.seg"), server.Payload[..length]);
        item.HlsSegmentCount = TestServer.HlsParts;

        await RunAsync(item);

        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
        Assert.DoesNotContain($"bytes=0-{length - 1}", server.Ranges("/hls/whole.bin"));
    }

    [Fact]
    public async Task Server_that_ignores_ranges_fails_clearly()
    {
        await using var server = await TestServer.StartAsync(256 * 1024, TimeSpan.Zero);
        var item = NewItem(server.HlsByteRangeNoRangesUrl);
        item.MaxConnections = 1;

        var e = await Assert.ThrowsAnyAsync<Exception>(() => RunAsync(item));

        Assert.Contains("byte ranges", e.Message);
        Assert.Equal(1, server.Hits("/noranges.bin"));
    }

    [Fact]
    public async Task Server_sending_another_range_fails_clearly()
    {
        await using var server = await TestServer.StartAsync(256 * 1024, TimeSpan.Zero);
        var item = NewItem(server.HlsByteRangeWrongUrl);

        var e = await Assert.ThrowsAnyAsync<Exception>(() => RunAsync(item));

        Assert.Contains("byte ranges", e.Message);
        Assert.False(File.Exists(item.FullPath), "the wrong bytes were saved");
    }

    [Fact]
    public async Task Short_range_is_tried_again()
    {
        await using var server = await TestServer.StartAsync(256 * 1024, TimeSpan.Zero);
        var item = NewItem(server.HlsByteRangeShortUrl);

        await new HlsDownloader(new RateLimiter(), new Settings(), null)
        {
            Retry = new RetryPolicy { FirstBackoff = TimeSpan.FromMilliseconds(20), MaxBackoff = TimeSpan.FromMilliseconds(20) },
        }.RunAsync(item, CancellationToken.None);

        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
        // Each part's first answer brought half of it; the next try asks only for what is still missing.
        var starts = server.Ranges("/hls/shortwhole.bin").Select(r => long.Parse(r["bytes=".Length..].Split('-')[0])).ToList();
        Assert.True(starts.Count > TestServer.HlsParts, $"only {starts.Count} requests");
        for (int i = 0; i < TestServer.HlsParts; i++)
            Assert.Single(starts, (long)server.Payload.Length * i / TestServer.HlsParts);
    }

    [Fact]
    public async Task Sync_check_sees_a_changed_range()
    {
        await using var server = await TestServer.StartAsync(256 * 1024, TimeSpan.Zero);
        var item = NewItem(server.HlsByteRangeUrl);
        await RunAsync(item);
        Assert.False(await HlsDownloader.HasChangedAsync(item, new Settings(), CancellationToken.None));

        server.HlsByteRangeShift = 1;

        Assert.True(await HlsDownloader.HasChangedAsync(item, new Settings(), CancellationToken.None));
    }

    [Fact]
    public async Task Byte_range_subtitles_leave_the_video_without_them()
    {
        await using var server = await TestServer.StartAsync(256 * 1024, TimeSpan.Zero);
        var item = NewItem(server.HlsByteRangeSubtitlesUrl);
        item.HlsSubtitles = true;

        await RunAsync(item);

        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
        Assert.Equal(new[] { "video.ts" }, FilesInFolder());
    }

    [Fact]
    public async Task Subtitles_can_be_left_out()
    {
        await using var server = await TestServer.StartAsync(256 * 1024, TimeSpan.Zero);
        var item = NewItem(server.HlsAvUrl);
        item.HlsSubtitles = false;

        await RunAsync(item);

        Assert.DoesNotContain(FilesInFolder(), f => f.EndsWith(".vtt"));
    }

    [Fact]
    public async Task Audio_only_downloads_just_the_audio_track()
    {
        await using var server = await TestServer.StartAsync(256 * 1024, TimeSpan.Zero);
        var item = NewItem(server.HlsAvUrl);
        item.HlsAudioOnly = true;

        await RunAsync(item);

        Assert.Equal(new[] { "video.ts" }, FilesInFolder());
        Assert.Equal(server.AudioPayload, File.ReadAllBytes(item.FullPath));
    }

    [Fact]
    public async Task Audio_only_fails_clearly_when_the_stream_has_no_separate_audio()
    {
        await using var server = await TestServer.StartAsync(256 * 1024, TimeSpan.Zero);
        var item = NewItem(server.HlsMasterUrl);
        item.HlsAudioOnly = true;

        var e = await Assert.ThrowsAsync<InvalidDataException>(() => RunAsync(item));
        Assert.Contains("no separate audio track", e.Message);
    }
}
