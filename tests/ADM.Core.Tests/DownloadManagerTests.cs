using ADM.Core;

namespace ADM.Core.Tests;

public sealed class DownloadManagerTests : IDisposable
{
    // Port 9 (discard) is never contacted: these downloads are only queued, not started.
    const string Url = "http://127.0.0.1:9/f.zip";

    readonly string _dir = Path.Combine(Path.GetTempPath(), "adm-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    string StatePath => Path.Combine(_dir, "state.json");

    DownloadManager NewManager(DuplicateAction onDuplicate = DuplicateAction.Ask)
    {
        var m = new DownloadManager(StatePath, new SynchronizationContext());
        m.State.Settings.DownloadRoot = _dir;
        m.State.Settings.OnDuplicate = onDuplicate;
        return m;
    }

    [Fact]
    public void Duplicate_link_reuses_the_existing_download_when_set_to_resume()
    {
        var m = NewManager(DuplicateAction.Resume);
        var first = m.Add(new DownloadRequest { Url = Url }, startNow: false);
        var second = m.Add(new DownloadRequest { Url = Url }, startNow: false);

        Assert.Same(first, second);
        Assert.Single(m.Items);
    }

    [Theory]
    [InlineData(DuplicateAction.AddNumbered)]
    [InlineData(DuplicateAction.Ask)] // with nobody to ask, the engine falls back to a numbered copy
    public void Duplicate_link_is_added_again(DuplicateAction policy)
    {
        var m = NewManager(policy);
        var first = m.Add(new DownloadRequest { Url = Url }, startNow: false);
        var second = m.Add(new DownloadRequest { Url = Url }, startNow: false);

        Assert.NotSame(first, second);
        Assert.Equal(2, m.Items.Count);
    }

    [Fact]
    public void Overwrite_replaces_the_existing_entry_and_targets_its_file()
    {
        var m = NewManager(DuplicateAction.Overwrite);
        var first = m.Add(new DownloadRequest { Url = Url, FileName = "f.zip" }, startNow: false);
        var second = m.Add(new DownloadRequest { Url = Url }, startNow: false);

        Assert.NotSame(first, second);
        Assert.Same(second, Assert.Single(m.Items));
        Assert.Equal(first.FullPath, second.FullPath);
        Assert.Equal(first.FullPath, second.OverwritePath);
    }

    [Fact]
    public void Overwrite_falls_back_to_a_numbered_copy_when_the_existing_file_is_unknown()
    {
        // The old entry never learned its file name, so there is no known file to replace; the
        // name the server picks later must not be allowed to overwrite whatever has that name.
        var m = NewManager(DuplicateAction.Overwrite);
        m.Add(new DownloadRequest { Url = Url }, startNow: false);
        var second = m.Add(new DownloadRequest { Url = Url }, startNow: false);

        Assert.Equal(2, m.Items.Count);
        Assert.Null(second.OverwritePath);
    }

    [Fact]
    public void An_explicit_choice_overrides_the_setting()
    {
        var m = NewManager(DuplicateAction.Resume);
        m.Add(new DownloadRequest { Url = Url }, startNow: false);
        m.Add(new DownloadRequest { Url = Url }, startNow: false, whenDuplicate: DuplicateAction.AddNumbered);

        Assert.Equal(2, m.Items.Count);
    }

    [Fact]
    public void FindDuplicate_matches_by_url()
    {
        var m = NewManager();
        var item = m.Add(new DownloadRequest { Url = Url }, startNow: false);

        Assert.Same(item, m.FindDuplicate(Url));
        Assert.Null(m.FindDuplicate("http://127.0.0.1:9/other.zip"));
    }

    // Every choice of a site's video shares the page's address, and every quality of a stream its playlist's.
    const string Page = "https://www.youtube.com/watch?v=abc";
    const string Playlist = "http://127.0.0.1:9/master.m3u8";

    [Fact]
    public void Another_choice_of_the_same_video_is_not_a_duplicate()
    {
        var m = NewManager(DuplicateAction.Resume);
        var hd = m.Add(new DownloadRequest { Url = Page, Kind = DownloadKind.Site, SiteChoice = "mp4:1080" }, startNow: false);
        m.Add(new DownloadRequest { Url = Page, Kind = DownloadKind.Site, SiteChoice = "mp4:720" }, startNow: false);
        m.Add(new DownloadRequest { Url = Page, Kind = DownloadKind.Site, SiteChoice = "audio" }, startNow: false);

        Assert.Equal(3, m.Items.Count);
        Assert.Same(hd, m.Add(new DownloadRequest { Url = Page, Kind = DownloadKind.Site, SiteChoice = "mp4:1080" }, startNow: false));
        Assert.Equal(3, m.Items.Count);
    }

    [Fact]
    public void Another_quality_or_the_audio_of_the_same_stream_is_not_a_duplicate()
    {
        var m = NewManager();
        var best = m.Add(new DownloadRequest { Url = Playlist, Kind = DownloadKind.Hls }, startNow: false);
        var low = m.Add(new DownloadRequest { Url = Playlist, Kind = DownloadKind.Hls, HlsBandwidth = 800_000 }, startNow: false);

        Assert.Same(best, m.FindDuplicate(new DownloadRequest { Url = Playlist, Kind = DownloadKind.Hls }));
        Assert.Same(low, m.FindDuplicate(new DownloadRequest { Url = Playlist, Kind = DownloadKind.Hls, HlsBandwidth = 800_000 }));
        Assert.Null(m.FindDuplicate(new DownloadRequest { Url = Playlist, Kind = DownloadKind.Hls, HlsBandwidth = 2_500_000 }));
        Assert.Null(m.FindDuplicate(new DownloadRequest { Url = Playlist, Kind = DownloadKind.Hls, HlsAudioOnly = true }));
    }

    static async Task<DownloadItem> DownloadAsync(DownloadManager m, DownloadRequest request)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnCompleted(DownloadItem _) => done.TrySetResult();
        void OnFailed(DownloadItem i) => done.TrySetException(new Exception(i.Error));
        m.DownloadCompleted += OnCompleted;
        m.DownloadFailed += OnFailed;
        try
        {
            var item = m.Add(request, startNow: true);
            await done.Task.WaitAsync(TimeSpan.FromSeconds(30));
            return item;
        }
        finally
        {
            m.DownloadCompleted -= OnCompleted;
            m.DownloadFailed -= OnFailed;
        }
    }

    [Fact]
    public async Task RemoveCompleted_drops_finished_entries_and_keeps_their_files()
    {
        await using var server = await TestServer.StartAsync(16 * 1024, TimeSpan.Zero);
        var m = NewManager();
        var finished = await DownloadAsync(m, new DownloadRequest { Url = server.FileUrl });
        var queued = m.Add(new DownloadRequest { Url = Url }, startNow: false);

        var removed = m.RemoveCompleted();

        Assert.Equal(1, removed);
        Assert.Same(queued, Assert.Single(m.Items));
        Assert.True(File.Exists(finished.FullPath));
    }

    [Fact]
    public async Task Restart_downloads_a_finished_file_again_in_place()
    {
        await using var server = await TestServer.StartAsync(64 * 1024, TimeSpan.Zero);
        var m = NewManager();
        var item = await DownloadAsync(m, new DownloadRequest { Url = server.FileUrl });
        File.WriteAllText(item.FullPath, "damaged");

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        m.DownloadCompleted += _ => done.TrySetResult();
        m.DownloadFailed += i => done.TrySetException(new Exception(i.Error));
        m.Restart(item);
        await done.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Same(item, Assert.Single(m.Items));
        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
        Assert.Single(Directory.GetFiles(item.Directory));
    }

    [Fact]
    public async Task Restart_redownloads_a_finished_hls_video_in_place()
    {
        // Fragmented MP4 is never remuxed, so the result is the same with or without ffmpeg on PATH.
        await using var server = await TestServer.StartAsync(256 * 1024, TimeSpan.Zero);
        var m = NewManager();
        var item = await DownloadAsync(m, new DownloadRequest { Url = server.HlsFmp4Url, Kind = DownloadKind.Hls });
        Assert.Equal("video.mp4", item.FileName);
        File.WriteAllText(item.FullPath, "damaged");

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        m.DownloadCompleted += _ => done.TrySetResult();
        m.DownloadFailed += i => done.TrySetException(new Exception(i.Error));
        m.Restart(item);
        await done.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal("video.mp4", item.FileName);
        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
        Assert.Single(Directory.GetFiles(item.Directory));
    }

    [Fact]
    public async Task Completed_download_forgets_browser_cookies()
    {
        await using var server = await TestServer.StartAsync(64 * 1024, TimeSpan.Zero);
        var m = NewManager();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        m.DownloadCompleted += _ => done.TrySetResult();
        m.DownloadFailed += i => done.TrySetException(new Exception(i.Error));

        var item = m.Add(new DownloadRequest { Url = server.FileUrl, Cookies = "session=secret" }, startNow: true);
        await done.Task.WaitAsync(TimeSpan.FromSeconds(30));
        m.Save();

        Assert.Null(item.Cookies);
        Assert.DoesNotContain("secret", File.ReadAllText(StatePath));
    }
}
