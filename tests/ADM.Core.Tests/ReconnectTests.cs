using System.Diagnostics;
using System.Net;
using ADM.Core;

namespace ADM.Core.Tests;

/// <summary>Getting through outages: the reconnect window, no network, HTTP errors, and reconnecting after sleep.</summary>
[Collection(nameof(SpeedTests))]
public sealed class ReconnectTests : IDisposable
{
    const int MB = 1024 * 1024;
    static readonly TimeSpan Short = TimeSpan.FromMilliseconds(20);

    readonly string _dir = Path.Combine(Path.GetTempPath(), "adm-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    DownloadItem NewItem(string url, int connections) =>
        new() { Url = url, Directory = _dir, FileName = "file.bin", MaxConnections = connections, Status = DownloadStatus.Downloading };

    static HttpDownloader NewDownloader(RetryPolicy retry) => new(new RateLimiter(), new Settings(), (_, _) => { }) { Retry = retry };

    static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException();
            await Task.Delay(10);
        }
    }

    /// <summary>The status texts shown over <paramref name="time"/>, or until <paramref name="stopAt"/> is one of them.</summary>
    static async Task<HashSet<string>> WatchStatus(DownloadItem item, TimeSpan time, string? stopAt = null)
    {
        var seen = new HashSet<string>();
        var until = DateTime.UtcNow + time;
        while (DateTime.UtcNow < until)
        {
            seen.Add(item.StatusText);
            if (stopAt != null && seen.Contains(stopAt)) break;
            await Task.Delay(10);
        }
        return seen;
    }

    // ---- Before the first byte

    [Fact]
    public async Task Starting_while_the_server_is_down_waits()
    {
        await using var server = await TestServer.StartAsync(MB, TimeSpan.Zero);
        server.Down = true;
        var item = NewItem(server.ThrottledUrl, 4);
        var run = NewDownloader(new RetryPolicy { FirstBackoff = Short, MaxBackoff = Short }).RunAsync(item, CancellationToken.None);

        // More than the quick tries a first request has always had.
        await Until(() => server.Hits("/throttled.bin") > 8);
        var seen = await WatchStatus(item, TimeSpan.FromMilliseconds(300));
        server.Down = false;
        await run.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
        Assert.Contains(seen, s => s.StartsWith("Reconnecting ("));
    }

    [Fact]
    public async Task Status_shows_downloading_again_once_the_first_request_gets_through()
    {
        await using var server = await TestServer.StartAsync(4 * MB, TimeSpan.Zero);
        server.Down = true;
        var item = NewItem(server.ThrottledUrl, 1);
        var run = NewDownloader(new RetryPolicy { FirstBackoff = Short, MaxBackoff = Short }).RunAsync(item, CancellationToken.None);

        await Until(() => server.Hits("/throttled.bin") > 8);
        server.Down = false;
        await Until(() => item.DownloadedBytes > 0);

        Assert.Null(item.StatusNote);
        await run.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Starting_with_no_network_pauses_the_window()
    {
        await using var server = await TestServer.StartAsync(MB, TimeSpan.Zero);
        server.Down = true;
        var item = NewItem(server.ThrottledUrl, 1);
        var retry = new RetryPolicy
        {
            FirstBackoff = Short,
            MaxBackoff = Short,
            ReconnectWindow = TimeSpan.FromMilliseconds(200),
            NetworkAvailable = () => !server.Down,
        };
        var run = NewDownloader(retry).RunAsync(item, CancellationToken.None);

        var seen = await WatchStatus(item, TimeSpan.FromSeconds(1));
        server.Down = false;
        await run.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
        Assert.Contains("Waiting for network", seen);
    }

    [Fact]
    public async Task Starting_outage_ends_when_the_window_does()
    {
        await using var server = await TestServer.StartAsync(MB, TimeSpan.Zero);
        server.Down = true;
        var item = NewItem(server.ThrottledUrl, 1);
        var retry = new RetryPolicy { FirstBackoff = Short, MaxBackoff = Short, ReconnectWindow = TimeSpan.FromMilliseconds(500) };

        var e = await Assert.ThrowsAnyAsync<Exception>(() => NewDownloader(retry).RunAsync(item, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30)));

        Assert.True(Net.IsConnectionFailure(e, CancellationToken.None), $"ended with {e.GetType().Name}: {e.Message}");
        Assert.True(server.Hits("/throttled.bin") > 4, $"only {server.Hits("/throttled.bin")} tries");
    }

    [Fact]
    public async Task Name_that_does_not_exist_fails_without_waiting()
    {
        var item = NewItem("http://adm.invalid/file.bin", 1);
        // A window this long would outlast the test, if a mistyped address waited for it.
        var retry = new RetryPolicy { FirstBackoff = Short, MaxBackoff = Short, NetworkAvailable = () => true };

        var e = await Assert.ThrowsAsync<HttpRequestException>(() => NewDownloader(retry).RunAsync(item, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30)));

        Assert.Null(e.StatusCode);
    }

    [Fact]
    public async Task Name_that_does_not_exist_waits_with_no_network()
    {
        var item = NewItem("http://adm.invalid/file.bin", 1);
        item.Status = DownloadStatus.Downloading;
        // With the network off, every name comes back as not existing.
        var retry = new RetryPolicy { FirstBackoff = Short, MaxBackoff = Short, NetworkAvailable = () => false };
        using var stop = new CancellationTokenSource();
        var run = NewDownloader(retry).RunAsync(item, stop.Token);

        // The name goes to the real resolver, which can take its time under load.
        var seen = await WatchStatus(item, TimeSpan.FromSeconds(30), stopAt: "Waiting for network");
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Contains("Waiting for network", seen);
    }

    [Fact]
    public async Task Hls_name_that_does_not_exist_fails_without_waiting()
    {
        var item = NewHlsItem("http://adm.invalid/video.m3u8");
        var retry = new RetryPolicy { FirstBackoff = Short, MaxBackoff = Short, NetworkAvailable = () => true };

        await Assert.ThrowsAsync<HttpRequestException>(() => NewHls(retry).RunAsync(item, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public async Task Http_error_at_start_still_fails_after_its_tries()
    {
        await using var server = await TestServer.StartAsync(MB, TimeSpan.Zero);
        var item = NewItem(server.ErrorUrl, 1);
        var run = NewDownloader(new RetryPolicy { FirstBackoff = Short, MaxBackoff = Short }).RunAsync(item, CancellationToken.None);

        var e = await Assert.ThrowsAsync<HttpRequestException>(() => run.WaitAsync(TimeSpan.FromSeconds(30)));

        Assert.Equal(HttpStatusCode.InternalServerError, e.StatusCode);
        Assert.Equal(4, server.ServerErrors);
    }

    [Fact]
    public async Task Resuming_while_the_server_is_down_waits()
    {
        await using var server = await TestServer.StartAsync(MB, TimeSpan.Zero);
        var item = NewItem(server.ThrottledUrl, 1);
        item.TotalBytes = server.Payload.Length;
        item.ResumeSupported = true;
        item.ResumeKnown = true;
        item.Segments.Add(new Segment { Start = 0, End = server.Payload.Length - 1, Downloaded = 256 * 1024 });
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(item.PartPath, server.Payload);
        server.Down = true;
        var run = NewDownloader(new RetryPolicy { FirstBackoff = Short, MaxBackoff = Short }).RunAsync(item, CancellationToken.None);

        await Until(() => server.Hits("/throttled.bin") > 8);
        server.Down = false;
        await run.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
    }

    [Fact]
    public async Task Ftp_starting_while_the_server_is_down_waits()
    {
        await using var server = await FtpTestServer.StartAsync(MB);
        server.Down = true;
        var item = NewItem(server.FileUrl, 1);
        var run = NewDownloader(new RetryPolicy { FirstBackoff = Short, MaxBackoff = Short }).RunAsync(item, CancellationToken.None);

        var seen = await WatchStatus(item, TimeSpan.FromMilliseconds(500));
        server.Down = false;
        await run.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
        Assert.Contains(seen, s => s.StartsWith("Reconnecting ("));
    }

    // ---- HLS

    static HlsDownloader NewHls(RetryPolicy retry) => new(new RateLimiter(), new Settings(), null) { Retry = retry };

    DownloadItem NewHlsItem(string url) =>
        new() { Url = url, Directory = _dir, FileName = "video.ts", Kind = DownloadKind.Hls, MaxConnections = 1, Status = DownloadStatus.Downloading };

    [Fact]
    public async Task Hls_starting_while_the_server_is_down_waits()
    {
        await using var server = await TestServer.StartAsync(MB, TimeSpan.Zero);
        server.Down = true;
        var item = NewHlsItem(server.HlsTsUrl);
        var run = NewHls(new RetryPolicy { FirstBackoff = Short, MaxBackoff = Short }).RunAsync(item, CancellationToken.None);

        var seen = await WatchStatus(item, TimeSpan.FromMilliseconds(500));
        server.Down = false;
        await run.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
        Assert.Contains(seen, s => s.StartsWith("Reconnecting ("));
    }

    [Fact]
    public async Task Hls_outage_waits_instead_of_failing()
    {
        await using var server = await TestServer.StartAsync(MB, TimeSpan.Zero);
        server.HlsOutageAfterParts = 2;
        server.HlsOutage = TimeSpan.FromSeconds(1);
        var item = NewHlsItem(server.HlsTsUrl);
        var run = NewHls(new RetryPolicy { FirstBackoff = Short, MaxBackoff = Short }).RunAsync(item, CancellationToken.None);

        await Until(() => server.Down);
        var seen = await WatchStatus(item, TimeSpan.FromMilliseconds(700));
        await run.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
        Assert.Contains(seen, s => s.StartsWith("Reconnecting ("));
    }

    [Fact]
    public async Task Hls_status_clears_after_an_outage()
    {
        await using var server = await TestServer.StartAsync(MB, TimeSpan.Zero);
        server.HlsOutageAfterParts = 2;
        server.HlsOutage = TimeSpan.FromMilliseconds(300);
        var item = NewHlsItem(server.HlsTsUrl);
        var notes = new List<(string? Note, int Done)>();
        item.PropertyChanged += (_, _) => { lock (notes) notes.Add((item.StatusNote, item.HlsSegmentsDone)); };

        await NewHls(new RetryPolicy { FirstBackoff = Short, MaxBackoff = Short }).RunAsync(item, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));

        lock (notes)
        {
            var troubled = notes.FindIndex(n => n.Note != null);
            Assert.True(troubled >= 0, "no note was seen");
            // The part after the outage clears it, with parts still to come. The end of a run clears any note anyway.
            Assert.Contains(notes.Skip(troubled), n => n.Note == null && n.Done < item.HlsSegmentCount);
        }
    }

    [Fact]
    public async Task Hls_outage_ends_when_the_window_does()
    {
        await using var server = await TestServer.StartAsync(MB, TimeSpan.Zero);
        server.HlsOutageAfterParts = 2;
        server.HlsOutage = TimeSpan.FromMinutes(5);
        var item = NewHlsItem(server.HlsTsUrl);
        var retry = new RetryPolicy { FirstBackoff = Short, MaxBackoff = Short, ReconnectWindow = TimeSpan.FromMilliseconds(500) };

        var e = await Assert.ThrowsAnyAsync<Exception>(() => NewHls(retry).RunAsync(item, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20)));

        Assert.True(Net.IsConnectionFailure(e, CancellationToken.None), $"ended with {e.GetType().Name}: {e.Message}");
    }

    [Fact]
    public async Task Hls_no_network_pauses_the_window()
    {
        await using var server = await TestServer.StartAsync(MB, TimeSpan.Zero);
        server.HlsOutageAfterParts = 2;
        server.HlsOutage = TimeSpan.FromSeconds(1);
        var item = NewHlsItem(server.HlsTsUrl);
        var retry = new RetryPolicy
        {
            FirstBackoff = Short,
            MaxBackoff = Short,
            ReconnectWindow = TimeSpan.FromMilliseconds(200),
            NetworkAvailable = () => !server.Down,
        };
        var run = NewHls(retry).RunAsync(item, CancellationToken.None);

        await Until(() => server.Down);
        var seen = await WatchStatus(item, TimeSpan.FromMilliseconds(700));
        await run.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
        Assert.Contains("Waiting for network", seen);
    }

    [Fact]
    public async Task Hls_http_error_still_fails_after_its_tries()
    {
        await using var server = await TestServer.StartAsync(MB, TimeSpan.Zero);
        var item = NewHlsItem(server.HlsErrorUrl);

        var e = await Assert.ThrowsAsync<HttpRequestException>(() =>
            NewHls(new RetryPolicy { FirstBackoff = Short, MaxBackoff = Short }).RunAsync(item, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20)));

        Assert.Equal(HttpStatusCode.InternalServerError, e.StatusCode);
        Assert.Equal(7, server.ServerErrors);
    }

    [Fact]
    public async Task Live_recording_waits_out_an_outage()
    {
        await using var server = await TestServer.StartAsync(1024, TimeSpan.Zero);
        var item = NewHlsItem(server.HlsLiveUrl());
        var hls = new HlsDownloader(new RateLimiter(), new Settings(), null)
        {
            Retry = new RetryPolicy { FirstBackoff = Short, MaxBackoff = Short },
            LivePoll = TimeSpan.FromMilliseconds(50),
        };
        using var stop = new CancellationTokenSource();
        var run = hls.RunAsync(item, CancellationToken.None, stop.Token);

        await Until(() => item.HlsSegmentsDone >= 3);
        server.Down = true;
        // Longer than a reload, so the outage reaches the playlist as well as the parts.
        var seen = await WatchStatus(item, TimeSpan.FromMilliseconds(500));
        var before = item.HlsSegmentsDone;
        server.Down = false;
        await Until(() => item.HlsSegmentsDone > before);
        stop.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(File.Exists(item.FullPath), "the recording wasn't saved");
        Assert.Contains(seen, s => s.StartsWith("Reconnecting ("));
    }

    [Fact]
    public async Task Stopping_a_recording_during_an_outage_saves_what_it_has()
    {
        await using var server = await TestServer.StartAsync(1024, TimeSpan.Zero);
        var item = NewHlsItem(server.HlsLiveUrl());
        var thirty = TimeSpan.FromSeconds(30);
        var hls = new HlsDownloader(new RateLimiter(), new Settings(), null)
        {
            // A wait this long would outlast the test if stopping didn't end it.
            Retry = new RetryPolicy { FirstBackoff = thirty, MaxBackoff = thirty },
            LivePoll = TimeSpan.FromMilliseconds(50),
        };
        using var stop = new CancellationTokenSource();
        var run = hls.RunAsync(item, CancellationToken.None, stop.Token);

        await Until(() => item.HlsSegmentsDone >= 3);
        server.Down = true;
        await Until(() => item.StatusNote != null);
        stop.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(File.Exists(item.FullPath), "the recording wasn't saved");
        Assert.True(new FileInfo(item.FullPath).Length >= 3 * 4096, $"only {new FileInfo(item.FullPath).Length} bytes");
    }

    // ---- While downloading

    [Fact]
    public async Task Network_outage_waits_instead_of_failing()
    {
        await using var server = await TestServer.StartAsync(4 * MB, TimeSpan.Zero);
        var item = NewItem(server.ThrottledUrl, 8);
        var run = NewDownloader(new RetryPolicy { FirstBackoff = Short, MaxBackoff = Short }).RunAsync(item, CancellationToken.None);

        await Until(() => item.DownloadedBytes >= MB);
        server.Down = true;
        var seen = await WatchStatus(item, TimeSpan.FromSeconds(2));
        server.Down = false;
        await run.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
        Assert.Contains(seen, s => s.StartsWith("Reconnecting ("));
    }

    [Fact]
    public async Task Reconnect_window_ends_with_the_last_error()
    {
        await using var server = await TestServer.StartAsync(4 * MB, TimeSpan.Zero);
        var item = NewItem(server.ThrottledUrl, 1);
        var retry = new RetryPolicy { FirstBackoff = Short, MaxBackoff = Short, ReconnectWindow = TimeSpan.FromMilliseconds(1500) };
        var run = NewDownloader(retry).RunAsync(item, CancellationToken.None);

        await Until(() => item.DownloadedBytes >= 256 * 1024);
        var tries = server.Hits("/throttled.bin");
        var watch = Stopwatch.StartNew();
        server.Down = true;
        var e = await Assert.ThrowsAnyAsync<Exception>(() => run.WaitAsync(TimeSpan.FromSeconds(30)));

        // The connection failure itself, reported once the window is over, after more tries than an HTTP error gets.
        // The window is long enough that a try costing 80 ms (60 ms to fail, 20 ms to wait) still makes more than 9.
        Assert.True(Net.IsConnectionFailure(e, CancellationToken.None), $"ended with {e.GetType().Name}: {e.Message}");
        Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(1200), $"gave up after {watch.Elapsed}");
        Assert.True(server.Hits("/throttled.bin") - tries > 9, $"only {server.Hits("/throttled.bin") - tries} tries");
    }

    [Fact]
    public async Task No_network_pauses_the_window()
    {
        await using var server = await TestServer.StartAsync(4 * MB, TimeSpan.Zero);
        var item = NewItem(server.ThrottledUrl, 1);
        var retry = new RetryPolicy
        {
            FirstBackoff = Short,
            MaxBackoff = Short,
            ReconnectWindow = TimeSpan.FromMilliseconds(200),
            NetworkAvailable = () => !server.Down,
        };
        var run = NewDownloader(retry).RunAsync(item, CancellationToken.None);

        await Until(() => item.DownloadedBytes >= 256 * 1024);
        server.Down = true;
        var seen = await WatchStatus(item, TimeSpan.FromSeconds(1));
        server.Down = false;
        await run.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
        Assert.Contains("Waiting for network", seen);
    }

    [Fact]
    public async Task Http_errors_keep_their_retry_limit()
    {
        await using var server = await TestServer.StartAsync(2 * MB, TimeSpan.Zero);
        var item = NewItem(server.ErrorUrl, 1);
        item.ResumeSupported = true;
        item.TotalBytes = server.Payload.Length;
        item.SegmentsSnapshot = new() { new Segment { Start = 0, End = server.Payload.Length - 1, Downloaded = 768 * 1024 } };
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(item.PartPath, server.Payload[..(768 * 1024)]);
        // A window long enough that, were HTTP errors given it, many more than 9 would be seen.
        var retry = new RetryPolicy { FirstBackoff = Short, MaxBackoff = Short, ReconnectWindow = TimeSpan.FromSeconds(2) };

        var e = await Assert.ThrowsAsync<HttpRequestException>(() => NewDownloader(retry).RunAsync(item, CancellationToken.None));

        Assert.Equal(HttpStatusCode.InternalServerError, e.StatusCode);
        Assert.Equal(9, server.ServerErrors); // the first try and 8 retries, as before
    }

    [Fact]
    public async Task Reconnect_ends_the_wait_between_tries()
    {
        // Back from an outage, as after Wi-Fi returns: the app's Reconnect must not leave a connection
        // waiting out a long backoff.
        await using var server = await TestServer.StartAsync(4 * MB, TimeSpan.Zero);
        server.PerRequestKBps = 2048;
        var item = NewItem(server.ThrottledUrl, 1);
        var longWait = TimeSpan.FromSeconds(20);
        var run = NewDownloader(new RetryPolicy { FirstBackoff = longWait, MaxBackoff = longWait }).RunAsync(item, CancellationToken.None);

        await Until(() => item.DownloadedBytes >= 256 * 1024);
        server.Down = true;
        await Until(() => item.StatusText.StartsWith("Reconnecting ("));
        server.Down = false;
        var watch = Stopwatch.StartNew();
        item.Reconnect();
        await run.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(6), $"took {watch.Elapsed} after reconnecting");
        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
    }

    [Fact]
    public async Task Reconnect_restarts_a_stalled_connection_at_once()
    {
        await using var server = await TestServer.StartAsync(4 * MB, TimeSpan.Zero);
        var ui = new LockedContext();
        var m = new DownloadManager(Path.Combine(_dir, "state.json"), ui);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        m.DownloadCompleted += _ => done.TrySetResult();
        m.DownloadFailed += i => done.TrySetException(new Exception(i.Error));
        DownloadItem item = null!;
        var watch = Stopwatch.StartNew();
        ui.Run(() => item = m.Add(new DownloadRequest { Url = server.StallUrl, FileName = "file.bin", Directory = _dir, Connections = 1 }, startNow: true));

        // The one connection sends half the file, then nothing; its read would time out after 30 seconds.
        await Until(() => item.DownloadedBytes >= 2 * MB);
        await Task.Delay(300);
        Assert.False(done.Task.IsCompleted);
        ui.Run(m.Reconnect);
        await done.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"took {watch.Elapsed}");
        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
    }
}
