using System.Diagnostics;
using ADM.Core;

namespace ADM.Core.Tests;

/// <summary>Timing tests run on their own, so the other tests' load can't slow them past their limits.</summary>
[CollectionDefinition(nameof(SpeedTests), DisableParallelization = true)]
public sealed class SpeedTestsCollection
{
}

/// <summary>Several connections must make a download faster; these tests guard that.</summary>
[Collection(nameof(SpeedTests))]
public sealed class SpeedTests : IDisposable
{
    const int MB = 1024 * 1024;

    readonly string _dir = Path.Combine(Path.GetTempPath(), "adm-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    DownloadItem NewItem(string url, int connections, string fileName = "file.bin") =>
        new() { Url = url, Directory = _dir, FileName = fileName, MaxConnections = connections };

    static Task RunAsync(DownloadItem item, CancellationToken ct = default) =>
        new HttpDownloader(new RateLimiter(), new Settings(), (_, _) => { }).RunAsync(item, ct);

    [Fact]
    public async Task Eight_connections_beat_one_on_a_throttled_server()
    {
        // Each request gets 512 KB/s, so one connection needs 8 seconds for the 4 MB. Its speed is
        // measured over 1.5 seconds instead of waiting for all of it.
        await using var server = await TestServer.StartAsync(4 * MB, TimeSpan.Zero);
        var one = NewItem(server.ThrottledUrl, 1, "one.bin");
        var watch = Stopwatch.StartNew();
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1.5)))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunAsync(one, cts.Token));
        var oneSpeed = one.DownloadedBytes / watch.Elapsed.TotalSeconds;

        var eight = NewItem(server.ThrottledUrl, 8);
        watch.Restart();
        await RunAsync(eight);
        var elapsed = watch.Elapsed;

        Assert.Equal(server.Payload, File.ReadAllBytes(eight.FullPath));
        Assert.True(elapsed < TimeSpan.FromSeconds(2), $"8 connections took {elapsed}");
        var eightSpeed = server.Payload.Length / elapsed.TotalSeconds;
        Assert.True(eightSpeed >= 4 * oneSpeed, $"8 connections: {eightSpeed / 1024:0} KB/s; one: {oneSpeed / 1024:0} KB/s");
    }

    [Fact]
    public async Task Slow_connection_does_not_hold_up_the_end()
    {
        // The request from byte 0 gets 64 KB/s and the others are not capped. Left with 512 KB, as
        // before, the slow connection would need 8 seconds.
        await using var server = await TestServer.StartAsync(4 * MB, TimeSpan.Zero);
        var item = NewItem(server.SlowStartUrl, 8);

        var watch = Stopwatch.StartNew();
        await RunAsync(item);
        // Read before comparing: xUnit's Assert.Equal takes seconds over megabytes until it has warmed up.
        var elapsed = watch.Elapsed;

        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
        Assert.True(elapsed < TimeSpan.FromSeconds(3), $"took {elapsed}");
    }

    [Fact]
    public async Task Slow_connection_hands_over_its_last_piece()
    {
        // At 16 KB/s, the 64 KB the slow connection keeps after the splits would take it 4 seconds; a fresh
        // connection fetches them at once.
        await using var server = await TestServer.StartAsync(4 * MB, TimeSpan.Zero);
        server.SlowStartKBps = 16;
        var item = NewItem(server.SlowStartUrl, 8);

        var watch = Stopwatch.StartNew();
        await RunAsync(item);
        var elapsed = watch.Elapsed;

        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
        Assert.True(elapsed < TimeSpan.FromSeconds(2.5), $"took {elapsed}");
    }

    [Fact]
    public async Task Stalled_connection_does_not_hold_up_the_end()
    {
        // One connection goes silent after 64 KB without closing. Waiting for it would take the 30-second
        // read timeout; free connections must fetch its bytes instead.
        await using var server = await TestServer.StartAsync(4 * MB, TimeSpan.Zero);
        var item = NewItem(server.StallingUrl, 8);

        var watch = Stopwatch.StartNew();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await RunAsync(item, cts.Token);
        var elapsed = watch.Elapsed;

        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
        Assert.True(elapsed < TimeSpan.FromSeconds(4), $"took {elapsed}");
    }

    [Fact]
    public async Task Long_stalled_connection_does_not_hold_up_the_end()
    {
        // The others need 2.5 seconds for their pieces, so by the time one is free the silent connection's
        // speed has read zero for a while: silence, not a speed still too soon to tell.
        await using var server = await TestServer.StartAsync(10 * MB, TimeSpan.Zero);
        server.PerRequestKBps = 1024;
        var item = NewItem(server.StallingUrl, 4);

        var watch = Stopwatch.StartNew();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await RunAsync(item, cts.Token);
        var elapsed = watch.Elapsed;

        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
        Assert.True(elapsed < TimeSpan.FromSeconds(6), $"took {elapsed}");
    }

    [Fact]
    public async Task Redirect_is_followed_once_per_run()
    {
        await using var server = await TestServer.StartAsync(6 * MB);
        var item = NewItem(server.RedirectUrl, 8);

        await RunAsync(item);

        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
        Assert.Equal(1, server.Hits("/redirect/file.bin"));
        Assert.True(server.Hits("/file.bin") > 2, $"only {server.Hits("/file.bin")} requests reached the file");
    }

    [Fact]
    public async Task First_bytes_come_from_the_probe_connection()
    {
        await using var server = await TestServer.StartAsync(256 * 1024);
        var item = NewItem(server.FileUrl, 8);

        await RunAsync(item);

        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
        Assert.Equal(1, server.Hits("/file.bin"));
    }

    [Fact]
    public async Task Expired_final_address_falls_back_to_the_original()
    {
        await using var server = await TestServer.StartAsync(4 * MB);
        var item = NewItem(server.ExpiringUrl, 8);

        await RunAsync(item);

        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
        Assert.Equal(2, server.Hits("/expiring.bin")); // the probe, then one visit for a fresh address
    }

    [Fact]
    public async Task Server_refusing_extra_connections_still_completes()
    {
        // Two requests at a time: of 8 connections, the 6 refused retire instead of retrying.
        await using var server = await TestServer.StartAsync(4 * MB, TimeSpan.Zero);
        var item = NewItem(server.LimitedUrl, 8);

        await RunAsync(item);

        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
        Assert.True(server.Refused <= 6, $"refused {server.Refused} times");
    }

    [Fact]
    public async Task Learned_limit_caps_the_next_download_from_that_server()
    {
        await using var server = await TestServer.StartAsync(4 * MB, TimeSpan.Zero);
        await RunAsync(NewItem(server.LimitedUrl, 8, "first.bin"));
        var refused = server.Refused;
        Assert.True(refused > 0, "the first download was never refused");

        var second = NewItem(server.LimitedUrl, 8, "second.bin");
        await RunAsync(second);

        Assert.Equal(server.Payload, File.ReadAllBytes(second.FullPath));
        Assert.Equal(refused, server.Refused);
    }

    [Fact]
    public async Task Partly_downloaded_segment_still_splits_for_every_connection()
    {
        // 32 KB in, halving leaves pieces just short of the 512 KB minimum; they must still be shared out.
        await using var server = await TestServer.StartAsync(4 * MB, TimeSpan.Zero);
        int total = server.Payload.Length, saved = 32 * 1024;
        var item = NewItem(server.ThrottledUrl, 8);
        item.ResumeSupported = true;
        item.TotalBytes = total;
        item.SegmentsSnapshot = new() { new Segment { Start = 0, End = total - 1, Downloaded = saved } };
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(item.PartPath, server.Payload[..saved]);

        await RunAsync(item);

        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
        Assert.Equal(8, server.MaxConcurrentRequests);
    }

    [Fact]
    public async Task Cut_short_connection_is_closed_not_drained()
    {
        // The probe asks for all 1.5 MB, and a split leaves it 768 KB. Draining the other 768 KB to reuse
        // the connection would fetch bytes another connection already has, and keep the server busy.
        await using var server = await TestServer.StartAsync(1536 * 1024, TimeSpan.Zero);
        var item = NewItem(server.ThrottledUrl, 2);

        await RunAsync(item);
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(300);
        while (server.ActiveRequests > 0 && DateTime.UtcNow < deadline) await Task.Delay(10);

        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
        Assert.Equal(0, server.ActiveRequests);
    }

    [Fact]
    public async Task Each_connection_is_its_own_tcp_connection()
    {
        // Multiplexing every segment over one connection (HTTP/2, say) would share one connection's speed.
        await using var server = await TestServer.StartAsync(6 * MB);
        var item = NewItem(server.FileUrl, 8);

        await RunAsync(item);

        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
        Assert.True(server.ConnectionIds.Count >= 3, $"only {server.ConnectionIds.Count} TCP connections");
    }

    [Fact]
    public async Task Free_connections_share_a_videos_last_pieces()
    {
        // A video and its sound of 8 MB each are four pieces of 4 MB, each 2 seconds at 2 MB/s a request. With
        // eight connections, the four left over each take half a piece, and the whole takes about one second.
        await using var server = await TestServer.StartAsync(16 * MB, TimeSpan.Zero);
        server.SiteKBps = 2048;
        var item = new DownloadItem
        {
            Url = server.SiteUrl("video.mp4"), AudioUrl = server.SiteUrl("audio.m4a"), Directory = _dir, FileName = "video.mp4",
            Kind = DownloadKind.Joined, MaxConnections = 8,
        };

        var watch = Stopwatch.StartNew();
        await new HlsDownloader(new RateLimiter(), new Settings(), null).RunAsync(item, CancellationToken.None);
        var elapsed = watch.Elapsed;

        Assert.Equal(server.SiteBytes("video.mp4"), File.ReadAllBytes(Path.Combine(_dir, "video.mp4")));
        Assert.Equal(server.SiteBytes("audio.m4a"), File.ReadAllBytes(Path.Combine(_dir, "video (audio).m4a")));
        Assert.Contains(server.Ranges("/site/video.mp4"), range => long.Parse(range["bytes=".Length..].Split('-')[0]) % (4 * MB) != 0);
        Assert.True(elapsed < TimeSpan.FromSeconds(1.6), $"took {elapsed}");
    }
}
