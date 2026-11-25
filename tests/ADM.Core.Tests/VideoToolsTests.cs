using ADM.Core;

namespace ADM.Core.Tests;

[Collection(nameof(SpeedTests))]
public sealed class VideoToolsTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "adm-tests", Guid.NewGuid().ToString("N"));
    readonly HttpClient _client = new();

    public void Dispose()
    {
        _client.Dispose();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    string Folder => Path.Combine(_dir, "tools");
    string OnPath => Path.Combine(_dir, "bin");

    VideoTools NewTools(ToolSources? sources = null, bool path = false) =>
        new(Folder, sources ?? ToolSources.Official, () => path ? OnPath : "");

    static void Touch(string folder, string name, string text = "")
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, name), text);
    }

    string[] FilesInFolder() => !Directory.Exists(Folder) ? [] : Directory.GetFiles(Folder).Select(f => Path.GetFileName(f))
        .Where(n => !n.EndsWith(".lock", StringComparison.Ordinal)).Order().ToArray();

    sealed class Reports : IProgress<ToolProgress>
    {
        public readonly List<ToolProgress> Seen = new();
        public void Report(ToolProgress value) { lock (Seen) Seen.Add(value); }
    }

    [Fact]
    public void Finds_tools_in_its_folder_first()
    {
        foreach (var name in new[] { "yt-dlp.exe", "deno.exe", "ffmpeg.exe" })
        {
            Touch(Folder, name);
            Touch(OnPath, name);
        }

        var tools = NewTools(path: true);

        Assert.Equal(Path.Combine(Folder, "yt-dlp.exe"), tools.YtDlpPath);
        Assert.Equal(new JsRuntime("deno", Path.Combine(Folder, "deno.exe")), tools.Runtime);
        Assert.Equal(Path.Combine(Folder, "ffmpeg.exe"), tools.FfmpegPath);
    }

    [Fact]
    public void Finds_tools_on_path()
    {
        foreach (var name in new[] { "yt-dlp.exe", "deno.exe", "ffmpeg.exe" }) Touch(OnPath, name);

        var tools = NewTools(path: true);

        Assert.Equal(Path.Combine(OnPath, "yt-dlp.exe"), tools.YtDlpPath);
        Assert.Equal(new JsRuntime("deno", Path.Combine(OnPath, "deno.exe")), tools.Runtime);
        Assert.Equal(Path.Combine(OnPath, "ffmpeg.exe"), tools.FfmpegPath);
        Assert.Empty(tools.Missing);
        Assert.Equal(Path.Combine(OnPath, "yt-dlp.exe"), tools.Reader()!.Program);
    }

    [Fact]
    public void Missing_lists_what_is_absent()
    {
        Touch(Folder, "ffmpeg.exe");

        var tools = NewTools();

        Assert.Equal(new[] { VideoTool.YtDlp, VideoTool.JsRuntime }, tools.Missing);
        Assert.Null(tools.Reader());
    }

    [Fact]
    public void Node_major_version_is_read()
    {
        Assert.Equal(26, VideoTools.NodeMajor("v26.12.1\r\n"));
        Assert.Equal(18, VideoTools.NodeMajor("v18.0.0"));
        Assert.Null(VideoTools.NodeMajor("node"));
    }

    [Fact]
    public async Task Installs_each_tool_checked_against_its_sum()
    {
        await using var server = await TestServer.StartAsync(16, TimeSpan.Zero);
        var tools = NewTools(server.ToolSources);
        var reports = new Reports();

        await tools.InstallAsync(_client, new[] { VideoTool.YtDlp, VideoTool.JsRuntime, VideoTool.Ffmpeg }, reports, CancellationToken.None);

        Assert.Equal(new[] { "deno.exe", "ffmpeg.exe", "yt-dlp.exe" }, FilesInFolder());
        Assert.Equal(TestServer.ToolBytes("yt-dlp"), File.ReadAllBytes(Path.Combine(Folder, "yt-dlp.exe")));
        Assert.Equal(TestServer.ToolBytes("deno"), File.ReadAllBytes(Path.Combine(Folder, "deno.exe")));
        Assert.Equal(TestServer.ToolBytes("ffmpeg"), File.ReadAllBytes(Path.Combine(Folder, "ffmpeg.exe")));
        Assert.Empty(tools.Missing);
        Assert.Equal(new[] { VideoTool.YtDlp, VideoTool.JsRuntime, VideoTool.Ffmpeg }, reports.Seen.Select(r => r.Tool).Distinct());
    }

    [Fact]
    public async Task Installs_through_the_redirects_github_answers_with()
    {
        await using var server = await TestServer.StartAsync(16, TimeSpan.Zero);
        var tools = NewTools(server.RedirectedToolSources);
        // The app's own client, which leaves redirects to whoever asks.
        var settings = new Settings { Proxy = new ProxySettings { Mode = ProxyMode.None } };

        await tools.InstallAsync(settings, new[] { VideoTool.YtDlp, VideoTool.JsRuntime, VideoTool.Ffmpeg }, null, CancellationToken.None);

        Assert.Equal(new[] { "deno.exe", "ffmpeg.exe", "yt-dlp.exe" }, FilesInFolder());
        Assert.Equal(TestServer.ToolBytes("yt-dlp"), File.ReadAllBytes(Path.Combine(Folder, "yt-dlp.exe")));
    }

    [Fact]
    public async Task Tampered_download_is_refused_and_nothing_replaced()
    {
        await using var server = await TestServer.StartAsync(16, TimeSpan.Zero);
        server.ToolsTampered = true;
        Touch(Folder, "yt-dlp.exe", "old");
        var tools = NewTools(server.ToolSources);

        var e = await Assert.ThrowsAsync<InvalidDataException>(() => tools.InstallAsync(_client, new[] { VideoTool.YtDlp }, null, CancellationToken.None));

        Assert.Contains("yt-dlp", e.Message);
        Assert.Equal("old", File.ReadAllText(Path.Combine(Folder, "yt-dlp.exe")));
        Assert.Equal(new[] { "yt-dlp.exe" }, FilesInFolder());

        await Assert.ThrowsAsync<InvalidDataException>(() => tools.InstallAsync(_client, new[] { VideoTool.Ffmpeg }, null, CancellationToken.None));
        Assert.Equal(new[] { "yt-dlp.exe" }, FilesInFolder());
    }

    [Fact]
    public async Task Oversized_tool_is_refused()
    {
        await using var server = await TestServer.StartAsync(16, TimeSpan.Zero);
        var tools = new VideoTools(Folder, server.ToolSources, () => "") { ToolLimit = 5 };

        await Assert.ThrowsAsync<InvalidDataException>(() => tools.InstallAsync(_client, new[] { VideoTool.YtDlp }, null, CancellationToken.None));

        Assert.Empty(FilesInFolder());
    }

    [Fact]
    public async Task Update_swaps_in_a_newer_ytdlp_once_a_day()
    {
        await using var server = await TestServer.StartAsync(16, TimeSpan.Zero);
        var now = new DateTime(2026, 11, 19, 12, 0, 0, DateTimeKind.Utc);
        var tools = new VideoTools(Folder, server.ToolSources, () => "") { Now = () => now };
        await tools.InstallAsync(_client, new[] { VideoTool.YtDlp }, null, CancellationToken.None);
        server.ToolsVersion = 2;

        Assert.True(await tools.UpdateYtDlpAsync(_client, CancellationToken.None));
        Assert.Equal(TestServer.ToolBytes("yt-dlp", 2), File.ReadAllBytes(Path.Combine(Folder, "yt-dlp.exe")));

        var asked = server.Hits("/tools/SHA2-256SUMS");
        server.ToolsVersion = 3;
        Assert.False(await tools.UpdateYtDlpAsync(_client, CancellationToken.None));
        Assert.Equal(asked, server.Hits("/tools/SHA2-256SUMS"));

        now = now.AddHours(25);
        Assert.True(await tools.UpdateYtDlpAsync(_client, CancellationToken.None));
        Assert.Equal(TestServer.ToolBytes("yt-dlp", 3), File.ReadAllBytes(Path.Combine(Folder, "yt-dlp.exe")));
    }

    [Fact]
    public async Task Update_leaves_a_ytdlp_from_path_alone()
    {
        await using var server = await TestServer.StartAsync(16, TimeSpan.Zero);
        Touch(OnPath, "yt-dlp.exe", "mine");
        var tools = new VideoTools(Folder, server.ToolSources, () => OnPath);

        Assert.False(await tools.UpdateYtDlpAsync(_client, CancellationToken.None));

        Assert.Equal(0, server.Hits("/tools/SHA2-256SUMS"));
        Assert.Equal("mine", File.ReadAllText(Path.Combine(OnPath, "yt-dlp.exe")));
    }

    [Fact]
    public async Task Update_waits_for_a_running_read()
    {
        await using var server = await TestServer.StartAsync(16, TimeSpan.Zero);
        var tools = NewTools(server.ToolSources);
        await tools.InstallAsync(_client, new[] { VideoTool.YtDlp }, null, CancellationToken.None);
        server.ToolsVersion = 2;

        var reading = await VideoTools.ReadLockAsync(tools.LockFile, CancellationToken.None);
        var update = tools.UpdateYtDlpAsync(_client, CancellationToken.None);
        await Task.Delay(500);

        Assert.False(update.IsCompleted);
        Assert.Equal(TestServer.ToolBytes("yt-dlp"), File.ReadAllBytes(Path.Combine(Folder, "yt-dlp.exe")));
        reading.Dispose();
        Assert.True(await update.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(TestServer.ToolBytes("yt-dlp", 2), File.ReadAllBytes(Path.Combine(Folder, "yt-dlp.exe")));
    }

    [Fact]
    public async Task A_read_waits_for_a_swap()
    {
        var lockFile = Path.Combine(Folder, "yt-dlp.lock");
        var swapping = await VideoTools.SwapLockAsync(lockFile, CancellationToken.None);

        var read = VideoTools.ReadLockAsync(lockFile, CancellationToken.None);
        await Task.Delay(300);

        Assert.False(read.IsCompleted);
        swapping.Dispose();
        (await read.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
    }
}
