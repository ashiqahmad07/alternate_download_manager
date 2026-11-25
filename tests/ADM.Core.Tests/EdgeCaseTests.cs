using ADM.Core;

namespace ADM.Core.Tests;

/// <summary>Older part files, a refresh of a numbered download, a crash once a file has landed, and sync checks with little to go on.</summary>
public sealed class EdgeCaseTests : IDisposable
{
    const int MB = 1024 * 1024;

    readonly string _dir = Path.Combine(Path.GetTempPath(), "adm-tests", Guid.NewGuid().ToString("N"));

    public EdgeCaseTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    string StatePath => Path.Combine(_dir, "state", "state.json");

    string Files => Path.Combine(_dir, "files");

    DownloadManager NewManager()
    {
        var m = new DownloadManager(StatePath, new LockedContext());
        m.State.Settings.DownloadRoot = Files;
        return m;
    }

    static async Task RunQueue(DownloadManager m, DownloadQueue queue)
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnFinished(DownloadQueue q) { if (q == queue) finished.TrySetResult(); }
        void OnFailed(DownloadItem i) => finished.TrySetException(new Exception(i.Error));
        m.QueueFinished += OnFinished;
        m.DownloadFailed += OnFailed;
        try
        {
            m.StartQueue(queue);
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            m.QueueFinished -= OnFinished;
            m.DownloadFailed -= OnFailed;
        }
    }

    // ---- Older part files

    DownloadItem HalfDone(string url, long total)
    {
        var item = new DownloadItem { Url = url, Directory = _dir, FileName = "file.bin", MaxConnections = 1, TotalBytes = total, ResumeSupported = true, ResumeKnown = true };
        item.Segments.Add(new Segment { Start = 0, End = total - 1, Downloaded = total / 2 });
        return item;
    }

    static HttpDownloader NoSpaceLeft() => new(new RateLimiter(), new Settings(), (_, _) => { }) { FreeSpace = _ => 0 };

    [Fact]
    public async Task Older_part_file_needs_no_more_space()
    {
        // From before part files were sparse: it holds the whole file's space already.
        await using var server = await TestServer.StartAsync(2 * MB, TimeSpan.Zero);
        var item = HalfDone(server.FileUrl, server.Payload.Length);
        File.WriteAllBytes(item.PartPath, server.Payload);

        await NoSpaceLeft().RunAsync(item, CancellationToken.None);

        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
    }

    [Fact]
    public async Task Sparse_part_file_still_needs_its_bytes_left()
    {
        await using var server = await TestServer.StartAsync(2 * MB, TimeSpan.Zero);
        var item = HalfDone(server.FileUrl, server.Payload.Length);
        using (var file = File.Open(item.PartPath, FileMode.Create, FileAccess.ReadWrite))
        {
            Assert.True(PartFile.TrySetSparse(file.SafeFileHandle, true), "this volume has no sparse files");
            file.SetLength(server.Payload.Length);
            file.Write(server.Payload, 0, server.Payload.Length / 2);
        }

        var e = await Assert.ThrowsAsync<IOException>(() => NoSpaceLeft().RunAsync(item, CancellationToken.None));

        Assert.Contains("Not enough disk space", e.Message);
    }

    // ---- Refresh by file name

    [Theory]
    [InlineData("name (1).ext", "name.ext")]
    [InlineData("name.tar (12).gz", "name.tar.gz")]
    [InlineData("name (1)", "name")]
    [InlineData("name.ext", "name.ext")]
    [InlineData("(1).ext", "(1).ext")]
    [InlineData("name (one).ext", "name (one).ext")]
    public void The_copy_number_comes_off_a_name(string name, string without) =>
        Assert.Equal(without, FileNames.WithoutCopyNumber(name));

    DownloadItem AwaitingNewAddress(DownloadManager m, string fileName)
    {
        var item = m.Add(new DownloadRequest { Url = "https://old.example/" + fileName, FileName = fileName }, startNow: false);
        m.BeginRefresh(item);
        return item;
    }

    [Fact]
    public void Refresh_matches_a_numbered_download()
    {
        var m = NewManager();
        var item = AwaitingNewAddress(m, "file (1).bin");

        Assert.Same(item, m.FindRefreshTarget(new DownloadRequest { Url = "https://new.example/file.bin" }, DateTime.Now));
        Assert.Same(item, m.FindRefreshTarget(new DownloadRequest { Url = "https://new.example/x", FileName = "file (1).bin" }, DateTime.Now));
    }

    [Fact]
    public void Refresh_does_not_match_another_file()
    {
        var m = NewManager();
        AwaitingNewAddress(m, "file (1).bin");

        Assert.Null(m.FindRefreshTarget(new DownloadRequest { Url = "https://new.example/other.bin" }, DateTime.Now));
        Assert.Null(m.FindRefreshTarget(new DownloadRequest { Url = "https://new.example/file (2).bin" }, DateTime.Now));
    }

    // ---- A crash once the file has landed

    DownloadItem SavedLanded(DownloadStatus status, bool fileThere, string url = "http://127.0.0.1:9/file.bin", string scanner = "scanner.exe")
    {
        var item = new DownloadItem { Url = url, Directory = Files, FileName = "file.bin", TotalBytes = 3, Status = status, Landed = true };
        var state = new AppState();
        state.Settings.VirusScanner = scanner;
        state.Downloads.Add(item);
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        Directory.CreateDirectory(Files);
        Store.Save(StatePath, state);
        if (fileThere) File.WriteAllBytes(item.FullPath, new byte[] { 1, 2, 3 });
        return item;
    }

    [Fact]
    public async Task Downloader_says_when_the_file_has_landed()
    {
        await using var server = await TestServer.StartAsync(64 * 1024, TimeSpan.Zero);
        var http = new DownloadItem { Url = server.FileUrl, Directory = _dir, FileName = "file.bin" };
        var hls = new DownloadItem { Url = server.HlsTsUrl, Directory = _dir, FileName = "video.ts", Kind = DownloadKind.Hls };

        await new HttpDownloader(new RateLimiter(), new Settings(), (_, _) => { }).RunAsync(http, CancellationToken.None);
        await new HlsDownloader(new RateLimiter(), new Settings(), null).RunAsync(hls, CancellationToken.None);

        Assert.True(http.Landed);
        Assert.True(hls.Landed);
    }

    [Theory]
    [InlineData(DownloadStatus.Downloading)]
    [InlineData(DownloadStatus.Paused)]
    public void Crash_after_landing_completes_on_the_next_start(DownloadStatus status)
    {
        SavedLanded(status, fileThere: true);

        var m = NewManager();
        var item = Assert.Single(m.Items);

        Assert.Equal(DownloadStatus.Completed, item.Status);
        Assert.False(item.Landed);
        Assert.NotNull(item.ScanResult);
        Assert.NotNull(item.CompletedAt);
        Assert.Equal(new[] { "file.bin" }, Directory.GetFiles(Files).Select(Path.GetFileName));
    }

    [Fact]
    public void Crash_after_landing_with_no_scanner_has_no_note()
    {
        SavedLanded(DownloadStatus.Downloading, fileThere: true, scanner: "");

        var item = Assert.Single(NewManager().Items);

        Assert.Equal(DownloadStatus.Completed, item.Status);
        Assert.Null(item.ScanResult);
    }

    [Fact]
    public async Task Crash_after_landing_with_the_file_gone_downloads_again()
    {
        await using var server = await TestServer.StartAsync(64 * 1024, TimeSpan.Zero);
        SavedLanded(DownloadStatus.Downloading, fileThere: false, server.FileUrl, scanner: "");

        var m = NewManager();
        var item = Assert.Single(m.Items);

        Assert.Equal(DownloadStatus.Paused, item.Status);
        Assert.False(item.Landed);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        m.DownloadCompleted += _ => done.TrySetResult();
        m.DownloadFailed += i => done.TrySetException(new Exception(i.Error));
        m.Start(item);
        await done.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
    }

    [Fact]
    public async Task Starting_again_means_the_file_has_not_landed()
    {
        // Paused just as the file landed. Saved during the new run, this would complete it with the old file.
        await using var server = await TestServer.StartAsync(64 * 1024, TimeSpan.Zero);
        var ui = new LockedContext();
        var m = new DownloadManager(StatePath, ui);
        m.State.Settings.DownloadRoot = Files;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        m.DownloadCompleted += _ => done.TrySetResult();
        DownloadItem item = null!;
        ui.Run(() =>
        {
            item = m.Add(new DownloadRequest { Url = server.FileUrl, FileName = "file.bin" }, startNow: false);
            item.Landed = true;
            m.Start(item);
            Assert.False(item.Landed);
        });

        await done.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void A_completed_download_is_left_as_it_was()
    {
        var saved = SavedLanded(DownloadStatus.Completed, fileThere: true);

        var item = Assert.Single(NewManager().Items);

        Assert.Equal(DownloadStatus.Completed, item.Status);
        Assert.Null(item.ScanResult);
        Assert.Equal(saved.CompletedAt, item.CompletedAt);
    }

    // ---- Sync checks: a server with nothing to compare

    [Fact]
    public async Task Sync_check_with_nothing_to_compare_reads_only_the_start()
    {
        await using var server = await TestServer.StartAsync(4 * MB, TimeSpan.Zero);
        var m = NewManager();
        var item = m.Add(new DownloadRequest { Url = server.NoInfoUrl }, startNow: false, m.SyncQueue);
        await RunQueue(m, m.SyncQueue);
        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
        var completedAt = item.CompletedAt;
        server.NoInfoSent = 0;

        await RunQueue(m, m.SyncQueue);

        Assert.Equal(completedAt, item.CompletedAt);
        Assert.True(server.NoInfoSent < server.Payload.Length, $"the server sent {server.NoInfoSent} bytes");
    }

    [Fact]
    public async Task Sync_check_with_nothing_to_compare_sees_a_changed_start()
    {
        await using var server = await TestServer.StartAsync(4 * MB, TimeSpan.Zero);
        var m = NewManager();
        var item = m.Add(new DownloadRequest { Url = server.NoInfoUrl }, startNow: false, m.SyncQueue);
        await RunQueue(m, m.SyncQueue);

        server.NoInfoFlipped = true;
        await RunQueue(m, m.SyncQueue);

        Assert.Equal(server.FlippedPayload, File.ReadAllBytes(item.FullPath));
    }

    [Fact]
    public async Task Sync_check_with_nothing_to_compare_sees_a_shorter_file()
    {
        await using var server = await TestServer.StartAsync(32 * 1024, TimeSpan.Zero);
        var m = NewManager();
        var item = m.Add(new DownloadRequest { Url = server.NoInfoUrl }, startNow: false, m.SyncQueue);
        await RunQueue(m, m.SyncQueue);

        server.NoInfoLength = 16 * 1024;
        await RunQueue(m, m.SyncQueue);

        Assert.Equal(16 * 1024, new FileInfo(item.FullPath).Length);
    }

    [Fact]
    public async Task Sync_check_gives_up_on_a_server_that_never_answers()
    {
        await using var server = await TestServer.StartAsync(MB, TimeSpan.Zero);
        server.NeverAnswerFirst = 1;
        var item = new DownloadItem { Url = server.HoldingUrl, Directory = _dir, FileName = "file.bin", TotalBytes = 3 };
        File.WriteAllBytes(item.FullPath, new byte[] { 1, 2, 3 });
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var e = await Assert.ThrowsAnyAsync<Exception>(() =>
            HttpDownloader.HasChangedAsync(item, new Settings(), cts.Token, new RetryPolicy { AnswerTimeout = TimeSpan.FromMilliseconds(300) }));

        Assert.False(cts.IsCancellationRequested, "it waited until the test's own limit");
        Assert.True(Net.IsConnectionFailure(e, CancellationToken.None), $"ended with {e.GetType().Name}: {e.Message}");
    }

    // ---- Sync checks: HLS

    DownloadItem AddHls(DownloadManager m, string url) =>
        m.Add(new DownloadRequest { Url = url, Kind = DownloadKind.Hls, FileName = "video.ts" }, startNow: false, m.SyncQueue);

    [Fact]
    public async Task Sync_check_reads_an_hls_playlist()
    {
        await using var server = await TestServer.StartAsync(MB, TimeSpan.Zero);
        server.HlsSyncParts = TestServer.HlsParts - 1;
        var m = NewManager();
        var item = AddHls(m, server.HlsSyncUrl);
        await RunQueue(m, m.SyncQueue);
        var completedAt = item.CompletedAt;
        var first = new FileInfo(item.FullPath).Length;
        var hits = server.Hits("/hls/part/0");

        await RunQueue(m, m.SyncQueue);

        Assert.Equal(completedAt, item.CompletedAt);
        Assert.Equal(hits, server.Hits("/hls/part/0"));

        server.HlsSyncParts = TestServer.HlsParts;
        await RunQueue(m, m.SyncQueue);

        Assert.True(new FileInfo(item.FullPath).Length > first, "the new part was not downloaded");
        Assert.Equal("video.ts", item.FileName);
        Assert.Equal(new[] { "video.ts" }, Directory.GetFiles(item.Directory, "video*").Select(Path.GetFileName));
    }

    [Fact]
    public async Task Sync_check_ignores_tokens_in_hls_addresses()
    {
        await using var server = await TestServer.StartAsync(MB, TimeSpan.Zero);
        var m = NewManager();
        var item = AddHls(m, server.HlsTokensUrl);
        await RunQueue(m, m.SyncQueue);
        var completedAt = item.CompletedAt;

        await RunQueue(m, m.SyncQueue);

        Assert.Equal(completedAt, item.CompletedAt);
    }

    [Fact]
    public async Task Older_hls_download_counts_as_changed_once()
    {
        await using var server = await TestServer.StartAsync(MB, TimeSpan.Zero);
        var m = NewManager();
        var item = AddHls(m, server.HlsTsUrl);
        await RunQueue(m, m.SyncQueue);
        item.HlsFingerprint = null;
        var hits = server.Hits("/hls/part/0");

        await RunQueue(m, m.SyncQueue);
        Assert.Equal(hits + 1, server.Hits("/hls/part/0"));
        Assert.NotNull(item.HlsFingerprint);

        await RunQueue(m, m.SyncQueue);
        Assert.Equal(hits + 1, server.Hits("/hls/part/0"));
    }

    [Fact]
    public async Task Live_recording_in_a_sync_queue_is_not_checked()
    {
        await using var server = await TestServer.StartAsync(MB, TimeSpan.Zero);
        var m = NewManager();
        var item = AddHls(m, server.HlsLiveUrl());
        item.IsLive = true;
        item.Status = DownloadStatus.Completed;
        Directory.CreateDirectory(item.Directory);
        File.WriteAllBytes(item.FullPath, new byte[] { 1, 2, 3 });

        m.StartQueue(m.SyncQueue);
        await Task.Delay(500);

        Assert.Equal(0, server.Hits("/hls/live.m3u8"));
        Assert.Equal(DownloadStatus.Completed, item.Status);
    }

    [Fact]
    public async Task Hls_sync_check_with_the_file_gone_downloads_again()
    {
        await using var server = await TestServer.StartAsync(MB, TimeSpan.Zero);
        var m = NewManager();
        var item = AddHls(m, server.HlsTsUrl);
        await RunQueue(m, m.SyncQueue);
        File.Delete(item.FullPath);

        await RunQueue(m, m.SyncQueue);

        Assert.True(File.Exists(item.FullPath));
    }
}
