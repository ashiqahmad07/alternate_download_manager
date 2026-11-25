using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using ADM.Core;

namespace ADM.Core.Tests;

// The fixtures are yt-dlp answers cut down by hand, in tests/fixtures/site/.
public sealed class SiteVideoTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "adm-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    internal static string FixturePath(string name, [CallerFilePath] string here = "") =>
        Path.Combine(Path.GetDirectoryName(here)!, "..", "fixtures", "site", name);

    static SiteVideoInfo Fixture(string name) => SiteVideoInfo.Parse(File.ReadAllText(FixturePath(name)));

    /// <summary>
    /// A stand-in for yt-dlp: it writes its arguments to args.txt, then answers with answer.json, or with error.txt on
    /// standard error and exit code 1, after waiting if wait.txt exists.
    /// </summary>
    internal static string FakeYtDlpIn(string dir)
    {
        var path = Path.Combine(dir, "tools", "yt-dlp.cmd");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """
            @echo off
            echo %* > "%~dp0args.txt"
            if exist "%~dp0wait.txt" ping -n 30 127.0.0.1 >nul
            if exist "%~dp0error.txt" (
              type "%~dp0error.txt" 1>&2
              exit /b 1
            )
            type "%~dp0answer.json"
            """);
        return path;
    }

    string Tools => Path.Combine(_dir, "tools");
    string Args => File.ReadAllText(Path.Combine(Tools, "args.txt"));

    YtDlp NewReader(TimeSpan? timeout = null, int? answerLimit = null)
    {
        var reader = new YtDlp(FakeYtDlpIn(_dir), null, Path.Combine(_dir, "cache"));
        if (timeout is { } t) reader = new YtDlp(reader.Program, null, Path.Combine(_dir, "cache")) { Timeout = t };
        if (answerLimit is { } limit) reader = new YtDlp(reader.Program, null, Path.Combine(_dir, "cache")) { AnswerLimit = limit };
        return reader;
    }

    void Answer(string fixture) => File.Copy(FixturePath(fixture), Path.Combine(Tools, "answer.json"), true);

    void Fails(params string[] lines) => File.WriteAllLines(Path.Combine(Tools, "error.txt"), lines);

    static string[] Labels(SiteVideoInfo info) => SiteChoices.For(info).Select(c => c.Label).ToArray();

    [Fact]
    public void Youtube_answer_offers_each_resolution_as_mp4_then_original_then_audio()
    {
        var choices = SiteChoices.For(Fixture("youtube.json"));

        Assert.Equal(new[] { "2160p MP4 (VP9)", "1080p MP4", "720p60 MP4", "360p MP4", "Original", "Audio only" }, choices.Select(c => c.Label));
        Assert.Equal(new[] { "mp4:2160", "mp4:1080", "mp4:720", "mp4:360", "original", "audio" }, choices.Select(c => c.Key));
    }

    [Fact]
    public void Mp4_prefers_h264_and_aac()
    {
        var info = Fixture("youtube.json");

        var pick = SiteChoices.Pick(info, "mp4:1080");

        Assert.Equal(("137", "140", ".mp4"), (pick.Main.Id, pick.Audio?.Id, pick.Extension));
        Assert.Equal(53_000_000, SiteChoices.For(info).Single(c => c.Key == "mp4:1080").Size);
    }

    [Fact]
    public void Mp4_above_1080_takes_vp9_as_it_is()
    {
        var pick = SiteChoices.Pick(Fixture("youtube.json"), "mp4:2160");

        Assert.Equal(("313", "140", ".mp4"), (pick.Main.Id, pick.Audio?.Id, pick.Extension));
    }

    [Fact]
    public void Original_keeps_the_best_as_it_is()
    {
        var pick = SiteChoices.Pick(Fixture("youtube.json"), "original");
        var mixed = SiteChoices.Pick(Fixture("mixed.json"), "original");

        Assert.Equal(("313", "251", ".webm"), (pick.Main.Id, pick.Audio?.Id, pick.Extension));
        Assert.Equal(("401", "251", ".mkv"), (mixed.Main.Id, mixed.Audio?.Id, mixed.Extension));
    }

    [Fact]
    public void Audio_only_is_aac_as_m4a()
    {
        var pick = SiteChoices.Pick(Fixture("youtube.json"), "audio");

        Assert.Equal(("140", null, ".m4a"), (pick.Main.Id, pick.Audio?.Id, pick.Extension));
    }

    [Fact]
    public void Missing_resolution_takes_the_next_lower()
    {
        var info = Fixture("youtube.json");

        Assert.Equal("137", SiteChoices.Pick(info, "mp4:1440").Main.Id);
        Assert.Equal("134", SiteChoices.Pick(info, "mp4:144").Main.Id);
    }

    [Fact]
    public void One_file_with_both_is_used_when_nothing_better()
    {
        var info = Fixture("facebook.json");

        var hd = SiteChoices.Pick(info, "mp4:720");
        var full = SiteChoices.Pick(info, "mp4:1080");

        Assert.Equal(("hd", null, ".mp4"), (hd.Main.Id, hd.Audio?.Id, hd.Extension));
        Assert.Equal(("dash-1080", null), (full.Main.Id, full.Audio?.Id));
        Assert.DoesNotContain("Audio only", Labels(info));
    }

    [Fact]
    public void A_file_yt_dlp_knows_nothing_about_is_offered_on_its_own()
    {
        // As Facebook's answer was on 2026-11-19: its sd and hd files, with both picture and sound, have no codec or size.
        var info = Fixture("facebook-real.json");

        Assert.Equal(new[] { "720p MP4 (AV1)", "540p MP4 (AV1)", "360p MP4 (AV1)", "HD MP4", "SD MP4", "Original", "Audio only" }, Labels(info));
        var hd = SiteChoices.Pick(info, "file:hd");
        Assert.Equal(("hd", null, ".mp4"), (hd.Main.Id, hd.Audio?.Id, hd.Extension));
        Assert.Equal("HD", SiteChoices.Describe("file:hd"));
        Assert.Equal(("4726832047642357v", "4726632900995605a"), (SiteChoices.Pick(info, "original").Main.Id, SiteChoices.Pick(info, "original").Audio?.Id));
    }

    [Fact]
    public void A_file_gone_from_the_site_falls_back_to_the_original()
    {
        Assert.Equal("4726832047642357v", SiteChoices.Pick(Fixture("facebook-real.json"), "file:gone").Main.Id);
    }

    [Fact]
    public void Shorter_side_is_the_resolution()
    {
        Assert.Equal(new[] { "1080p MP4", "Original", "Audio only" }, Labels(Fixture("vertical.json")));
    }

    [Fact]
    public void Hls_formats_are_offered()
    {
        var info = Fixture("hls-only.json");

        Assert.Equal(new[] { "720p MP4", "480p MP4", "Original" }, Labels(info));
        Assert.Equal("m3u8_native", SiteChoices.Pick(info, "mp4:720").Main.Protocol);
    }

    [Fact]
    public void A_whole_file_comes_before_a_stream_of_the_same_quality()
    {
        // As YouTube's answer was on 2026-11-21: a stream's bitrate is its playlist's peak, so it looked better than the
        // file of the same quality, and yt-dlp gives no size for it, so the download's size wasn't known until it ended.
        static JsonObject Format(string id, string protocol, string ext, string vcodec, string acodec, int height, double tbr, long? size) => new()
        {
            ["format_id"] = id, ["url"] = "https://a.com/" + id, ["protocol"] = protocol, ["ext"] = ext,
            ["vcodec"] = vcodec, ["acodec"] = acodec, ["height"] = height, ["fps"] = height > 0 ? (JsonNode)60 : null, ["tbr"] = tbr, ["filesize"] = size,
        };
        var answer = new JsonObject
        {
            ["title"] = "t",
            ["formats"] = new JsonArray(
                Format("140", "https", "m4a", "none", "mp4a.40.2", 0, 129, 83_737_521),
                Format("312", "m3u8_native", "mp4", "avc1.64002A", "none", 1080, 7834, null),
                Format("299", "https", "mp4", "avc1.64002A", "none", 1080, 3970, 2_567_577_108),
                Format("628", "m3u8_native", "mp4", "vp09.00.51.0", "none", 2160, 85909, null),
                Format("315", "https", "webm", "vp9", "none", 2160, 18044, 11_670_563_145)),
        };
        var info = SiteVideoInfo.Parse(answer.ToJsonString());

        Assert.Equal(("299", "140"), (SiteChoices.Pick(info, "mp4:1080").Main.Id, SiteChoices.Pick(info, "mp4:1080").Audio?.Id));
        Assert.Equal(2_567_577_108 + 83_737_521, SiteChoices.For(info).Single(c => c.Key == "mp4:1080").Size);
        Assert.Equal("315", SiteChoices.Pick(info, "original").Main.Id);
        Assert.Equal("315", SiteChoices.Pick(info, "mp4:2160").Main.Id);
    }

    [Fact]
    public void The_files_a_download_has_parts_of_are_kept_while_they_are_the_quality_chosen()
    {
        var info = Fixture("youtube.json");

        // 720p60 is the choice's own; the parts came from the 720p file, and the read still has it.
        Assert.Equal("298", SiteChoices.Pick(info, "mp4:720").Main.Id);
        Assert.Equal(("136", "140"), (SiteChoices.Pick(info, "mp4:720", "136:5|140:6").Main.Id, SiteChoices.Pick(info, "mp4:720", "136:5|140:6").Audio?.Id));
        // The site numbered its formats afresh, so the number is now another quality; or the file is gone.
        Assert.Equal("298", SiteChoices.Pick(info, "mp4:720", "134:5|140:6").Main.Id);
        Assert.Equal("298", SiteChoices.Pick(info, "mp4:720", "999:5|140:6").Main.Id);
        Assert.Equal("298", SiteChoices.Pick(info, "mp4:720", null).Main.Id);
    }

    [Fact]
    public void Unusable_formats_are_left_out()
    {
        var info = Fixture("unusable.json");

        Assert.Empty(SiteChoices.For(info));
        var e = Assert.Throws<InvalidDataException>(() => SiteChoices.Pick(info, "original"));
        Assert.Equal("This video can't be downloaded: its site offers no format ADM can fetch.", e.Message);
    }

    [Fact]
    public void Answer_that_is_not_ytdlps_is_refused()
    {
        Assert.Throws<InvalidDataException>(() => SiteVideoInfo.Parse("<html>"));
        Assert.Throws<InvalidDataException>(() => SiteVideoInfo.Parse("[1, 2]"));
    }

    [Theory]
    [InlineData("../../evil")]
    [InlineData("mp4\\..\\x")]
    [InlineData("exe:stream")]
    [InlineData("toolongext")]
    [InlineData("")]
    public void Extension_from_a_site_is_letters_and_digits_only(string ext)
    {
        var answer = new JsonObject
        {
            ["title"] = "t",
            ["formats"] = new JsonArray(new JsonObject
            {
                ["format_id"] = "1", ["url"] = "https://a.com/v", ["protocol"] = "https", ["ext"] = ext,
                ["vcodec"] = "avc1", ["acodec"] = "mp4a.40.2", ["height"] = 720,
            }),
        };

        var info = SiteVideoInfo.Parse(answer.ToJsonString());

        Assert.Equal("mp4", Assert.Single(info.Formats).Extension);
        Assert.Equal(".mp4", SiteChoices.Pick(info, "original").Extension);
    }

    [Fact]
    public void When_a_file_becomes_available_is_read()
    {
        // yt-dlp gives it as Unix seconds, a fraction among them, as YouTube refuses its files until the preroll ad would have played.
        static JsonObject Format(string id, JsonNode? availableAt) => new()
        {
            ["format_id"] = id, ["url"] = "https://a.com/" + id, ["protocol"] = "https", ["ext"] = "mp4",
            ["vcodec"] = "avc1", ["acodec"] = "mp4a.40.2", ["height"] = 720, ["available_at"] = availableAt,
        };
        var answer = new JsonObject { ["title"] = "t", ["formats"] = new JsonArray(Format("1", 1795109820.5), Format("2", null), Format("3", 1e300)) };

        var formats = SiteVideoInfo.Parse(answer.ToJsonString()).Formats;

        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1_795_109_820_500), formats[0].AvailableAt);
        Assert.Null(formats[1].AvailableAt);
        Assert.Null(formats[2].AvailableAt);
    }

    [Fact]
    public void Format_headers_are_read()
    {
        var format = Fixture("youtube.json").Formats.Single(f => f.Id == "18");

        Assert.Equal("Mozilla/5.0 (test)", format.Headers["User-Agent"]);
        Assert.Equal(new Uri("https://rr1.googlevideo.com/videoplayback?itag=18"), format.Url);
    }

    [Fact]
    public void Json_reply_lists_title_and_choices()
    {
        var reply = JsonNode.Parse(SiteChoices.Json(Fixture("facebook.json")))!;

        Assert.True((bool)reply["ok"]!);
        Assert.Equal("A Facebook video", (string?)reply["title"]);
        var first = reply["choices"]![0]!;
        Assert.Equal("mp4:1080", (string?)first["key"]);
        Assert.Equal("1080p MP4", (string?)first["label"]);
        Assert.Null(first["size"]);
        var youtube = JsonNode.Parse(SiteChoices.Json(Fixture("youtube.json")))!;
        Assert.Equal(53_000_000, (long)youtube["choices"]![1]!["size"]!);
    }

    [Fact]
    public void Describe_names_a_key()
    {
        Assert.Equal("1080p MP4", SiteChoices.Describe("mp4:1080"));
        Assert.Equal("Original", SiteChoices.Describe("original"));
        Assert.Equal("Audio only", SiteChoices.Describe("audio"));
    }

    [Fact]
    public void Address_comes_after_the_end_of_options()
    {
        var reader = new YtDlp(@"C:\t\yt-dlp.exe", new JsRuntime("deno", @"C:\t\deno.exe"), @"C:\t\cache");

        var args = reader.ArgumentsFor("https://a.com/-x").ToList();

        foreach (var expected in new[] { "-J", "--ignore-config", "--no-playlist", "--no-progress" }) Assert.Contains(expected, args);
        Assert.Equal("utf-8", args[args.IndexOf("--encoding") + 1]);
        Assert.Equal(@"C:\t\cache", args[args.IndexOf("--cache-dir") + 1]);
        Assert.Equal(@"deno:C:\t\deno.exe", args[args.IndexOf("--js-runtimes") + 1]);
        Assert.Equal(new[] { "--", "https://a.com/-x" }, args.TakeLast(2));
    }

    [Fact]
    public void Without_a_runtime_none_is_named()
    {
        Assert.DoesNotContain("--js-runtimes", new YtDlp("yt-dlp.exe", null, "cache").ArgumentsFor("https://a.com/"));
    }

    [Fact]
    public async Task Reads_a_non_ascii_title()
    {
        var reader = NewReader();
        Answer("youtube.json");

        var info = await reader.ReadAsync("https://www.youtube.com/watch?v=abc123", CancellationToken.None);

        Assert.Equal("ভিডিও: পরীক্ষা/১", info.Title);
        Assert.Contains("https://www.youtube.com/watch?v=abc123", Args);
    }

    [Fact]
    public async Task Error_line_is_the_message()
    {
        var reader = NewReader();
        Fails("WARNING: x", "ERROR: [youtube] abc: Video unavailable");

        var e = await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadAsync("https://a.com/", CancellationToken.None));

        Assert.Equal("[youtube] abc: Video unavailable", e.Message);
    }

    [Fact]
    public async Task Network_error_is_an_outage()
    {
        var reader = NewReader();
        Fails("ERROR: [youtube] abc: Unable to download webpage: <urlopen error [Errno 11001] getaddrinfo failed>");

        var e = await Assert.ThrowsAsync<HttpRequestException>(() => reader.ReadAsync("https://a.com/", CancellationToken.None));

        Assert.True(Net.IsConnectionFailure(e, CancellationToken.None));
    }

    [Fact]
    public async Task Hung_ytdlp_is_stopped()
    {
        var reader = NewReader(timeout: TimeSpan.FromSeconds(1));
        Answer("youtube.json");
        File.WriteAllText(Path.Combine(Tools, "wait.txt"), "");
        var clock = Stopwatch.StartNew();

        await Assert.ThrowsAsync<TimeoutException>(() => reader.ReadAsync("https://a.com/", CancellationToken.None));

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"took {clock.Elapsed}");
    }

    [Fact]
    public async Task Endless_answer_is_refused()
    {
        var reader = NewReader(answerLimit: 1000);
        File.WriteAllText(Path.Combine(Tools, "answer.json"), new string('x', 5000), Encoding.ASCII);

        await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadAsync("https://a.com/", CancellationToken.None));
    }

    [Fact]
    public async Task Reply_without_ytdlp_lists_what_is_missing()
    {
        var without = JsonNode.Parse(await SiteChoices.ReplyAsync(null, new[] { VideoTool.YtDlp, VideoTool.Ffmpeg }, "https://a.com/v", CancellationToken.None))!;
        Assert.False((bool)without["ok"]!);
        Assert.False(string.IsNullOrEmpty((string?)without["error"]));
        Assert.Equal(new[] { "yt-dlp", "ffmpeg" }, without["missingTools"]!.AsArray().Select(n => (string?)n));

        var reader = NewReader();
        Answer("facebook.json");
        var with = JsonNode.Parse(await SiteChoices.ReplyAsync(reader, new[] { VideoTool.Ffmpeg }, "https://a.com/v", CancellationToken.None))!;
        Assert.True((bool)with["ok"]!);
        Assert.Equal(4, with["choices"]!.AsArray().Count);
        Assert.Equal(new[] { "ffmpeg" }, with["missingTools"]!.AsArray().Select(n => (string?)n));

        File.Delete(Path.Combine(Tools, "args.txt"));
        var local = JsonNode.Parse(await SiteChoices.ReplyAsync(reader, Array.Empty<VideoTool>(), "file:///C:/x.html", CancellationToken.None))!;
        Assert.False((bool)local["ok"]!);
        Assert.False(File.Exists(Path.Combine(Tools, "args.txt")), "yt-dlp was run for an address that isn't on the web");
    }

    [Fact]
    public async Task Reply_says_what_ytdlp_said()
    {
        var reader = NewReader();
        Fails("ERROR: [generic] Unsupported URL: https://a.com/v");

        var reply = JsonNode.Parse(await SiteChoices.ReplyAsync(reader, Array.Empty<VideoTool>(), "https://a.com/v", CancellationToken.None))!;

        Assert.False((bool)reply["ok"]!);
        Assert.Equal("[generic] Unsupported URL: https://a.com/v", (string?)reply["error"]);
    }

    [Fact]
    public void A_page_of_videos_is_listed_without_reading_each()
    {
        // ADM turns such a page down either way; this way yt-dlp says so in one request, not one for each video.
        Assert.Contains("--flat-playlist", new YtDlp("yt-dlp.exe", null, "cache").ArgumentsFor("https://a.com/"));
    }

    YtDlp KeepingReader() => new(FakeYtDlpIn(_dir), null, Path.Combine(_dir, "cache")) { KeptFolder = Path.Combine(_dir, "reads") };

    bool Ran => File.Exists(Path.Combine(Tools, "args.txt"));

    [Fact]
    public async Task The_menus_read_is_kept_so_asking_again_runs_no_ytdlp()
    {
        var reader = KeepingReader();
        Answer("facebook.json");
        await SiteChoices.ReplyAsync(reader, Array.Empty<VideoTool>(), "https://a.com/v", CancellationToken.None);
        File.Delete(Path.Combine(Tools, "args.txt"));

        var again = JsonNode.Parse(await SiteChoices.ReplyAsync(reader, Array.Empty<VideoTool>(), "https://a.com/v", CancellationToken.None))!;

        Assert.True((bool)again["ok"]!);
        Assert.Equal(4, again["choices"]!.AsArray().Count);
        Assert.False(Ran, "yt-dlp read again a page it read a moment ago");
    }

    [Fact]
    public async Task A_kept_read_stays_kept_for_each_download_of_its_page()
    {
        // A second quality picked from the menu starts as soon as the first did, rather than after yt-dlp reads again.
        var reader = KeepingReader();
        Answer("facebook.json");
        await reader.ReadAsync("https://a.com/v", keep: true, CancellationToken.None);

        Assert.Null(reader.Kept("https://a.com/other"));
        Assert.NotNull(reader.Kept("https://a.com/v"));
        Assert.NotNull(reader.Kept("https://a.com/v"));
    }

    [Fact]
    public async Task A_read_kept_longer_than_its_time_is_not_used()
    {
        var reader = KeepingReader();
        Answer("facebook.json");
        await reader.ReadAsync("https://a.com/v", keep: true, CancellationToken.None);
        foreach (var file in Directory.GetFiles(reader.KeptFolder!))
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow - reader.KeptFor - TimeSpan.FromMinutes(1));

        Assert.Null(reader.Kept("https://a.com/v"));
    }

    [Fact]
    public async Task Only_a_read_asked_to_be_kept_that_succeeded_is_kept()
    {
        var reader = KeepingReader();
        Answer("facebook.json");
        await reader.ReadAsync("https://a.com/v", CancellationToken.None);
        Fails("ERROR: [generic] Unsupported URL: https://a.com/w");
        await SiteChoices.ReplyAsync(reader, Array.Empty<VideoTool>(), "https://a.com/w", CancellationToken.None);

        Assert.Null(reader.Kept("https://a.com/v"));
        Assert.Null(reader.Kept("https://a.com/w"));
    }
}
