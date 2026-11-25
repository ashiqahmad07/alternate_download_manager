using System.Diagnostics;
using ADM.Core;

namespace ADM.Core.Tests;

/// <summary>
/// What the progress window's details show, as IDM's do: a row for each connection, with what it has brought and
/// what it is doing, and a strip of the file marking where each connection is at work.
/// </summary>
public sealed class ConnectionsViewTests : IDisposable
{
    const int MiB = 1024 * 1024;

    readonly string _dir = Path.Combine(Path.GetTempPath(), "adm-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    DownloadItem NewStream(string url, DownloadKind kind = DownloadKind.Hls, string fileName = "video.ts") =>
        new() { Url = url, Directory = _dir, FileName = fileName, Kind = kind, MaxConnections = 4 };

    // Without an ffmpeg, the parts stay as they are whatever is on this machine's PATH.
    static Task RunAsync(DownloadItem item, CancellationToken ct = default) =>
        new HlsDownloader(new RateLimiter(), new Settings(), null).RunAsync(item, ct);

    static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException();
            await Task.Delay(20);
        }
    }

    [Fact]
    public void A_file_lists_each_connection_by_what_it_is_doing()
    {
        var asking = new Segment { Start = 0, End = 99, Active = true };
        asking.Asked(Stopwatch.GetTimestamp(), new CancellationTokenSource());
        var receiving = new Segment { Start = 100, End = 199, Downloaded = 40, Active = true };
        receiving.Connected(Stopwatch.GetTimestamp(), new CancellationTokenSource());
        var waiting = new Segment { Start = 200, End = 229, Downloaded = 10, Active = true };
        waiting.Waiting(new CancellationTokenSource());
        var idle = new Segment { Start = 230, End = 249, Downloaded = 5 };
        var done = new Segment { Start = 250, End = 299, Downloaded = 50 };
        var item = new DownloadItem { TotalBytes = 300, SegmentsSnapshot = [asking, receiving, waiting, idle, done] };

        var rows = item.ConnectionsNow();

        Assert.Equal([ConnectionStage.Asking, ConnectionStage.Receiving, ConnectionStage.Waiting, ConnectionStage.Idle, ConnectionStage.Complete],
            rows.Select(r => r.Stage));
        Assert.Equal([0L, 40, 10, 5, 50], rows.Select(r => r.Downloaded));
    }

    [Fact]
    public void A_file_s_strip_marks_where_each_connection_started()
    {
        var first = new Segment { Start = 0, End = 99, Downloaded = 100 };
        var second = new Segment { Start = 100, End = 299, Downloaded = 40, Active = true };
        var item = new DownloadItem { TotalBytes = 300, SegmentsSnapshot = [first, second] };

        var strip = item.StripNow()!;

        Assert.Equal(300, strip.Total);
        Assert.Equal([new StripPiece(0, 100, 100, false, false), new StripPiece(100, 200, 40, true, true)], strip.Pieces);
    }

    [Fact]
    public void A_file_of_unknown_size_has_no_strip()
    {
        var item = new DownloadItem { SegmentsSnapshot = [new Segment { Start = 0, End = long.MaxValue, Downloaded = 10, Active = true }] };

        Assert.Null(item.StripNow());
    }

    [Fact]
    public void A_completed_download_has_no_strip()
    {
        var item = new DownloadItem { TotalBytes = 100, Status = DownloadStatus.Completed, SegmentsSnapshot = [new Segment { Start = 0, End = 99, Downloaded = 100 }] };

        Assert.Null(item.StripNow());
    }

    [Fact]
    public async Task A_stream_lists_its_connections_and_what_each_brought()
    {
        await using var server = await TestServer.StartAsync(256 * 1024, TimeSpan.Zero);
        var item = NewStream(server.HlsTsUrl);

        await RunAsync(item);

        var rows = item.ConnectionsNow();
        Assert.InRange(rows.Count, 1, 4);
        Assert.Equal(server.Payload.Length, rows.Sum(r => r.Downloaded));
        // The run is over, so none of them is at work.
        Assert.All(rows, r => Assert.Equal(ConnectionStage.Idle, r.Stage));
        Assert.Equal(0, item.ActiveConnections);
    }

    [Fact]
    public async Task A_stream_s_connections_say_they_are_asking_until_answered()
    {
        await using var server = await TestServer.StartAsync(256 * 1024, TimeSpan.FromSeconds(2));
        var item = NewStream(server.HlsTsUrl);
        using var stop = new CancellationTokenSource();

        var run = RunAsync(item, stop.Token);
        await Until(() => item.ConnectionsNow().Count(r => r.Stage == ConnectionStage.Asking) == 4);
        Assert.Equal(4, item.ActiveConnections);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        Assert.All(item.ConnectionsNow(), r => Assert.Equal(ConnectionStage.Idle, r.Stage));
        // Nothing was kept, so the strip has nothing filled and no connection at work.
        Assert.Empty(item.StripNow()!.Pieces);
    }

    [Fact]
    public async Task A_stream_whose_parts_sizes_are_known_is_drawn_by_bytes()
    {
        await using var server = await TestServer.StartAsync(9 * MiB + 5, TimeSpan.Zero);
        var item = NewStream(server.DashUrl("whole"), DownloadKind.Dash, "video.mp4");

        await RunAsync(item);

        var strip = item.StripNow()!;
        long total = server.Payload.Length;
        Assert.Equal(total, strip.Total);
        // Kept parts side by side are one stretch.
        Assert.Equal([new StripPiece(0, total, total, false, false)], strip.Pieces);
    }

    [Fact]
    public async Task A_stream_whose_parts_sizes_are_not_known_is_drawn_by_parts()
    {
        await using var server = await TestServer.StartAsync(256 * 1024, TimeSpan.Zero);
        var item = NewStream(server.HlsTsUrl);

        await RunAsync(item);

        var strip = item.StripNow()!;
        Assert.Equal(TestServer.HlsParts, strip.Total);
        Assert.Equal([new StripPiece(0, TestServer.HlsParts, TestServer.HlsParts, false, false)], strip.Pieces);
    }

    [Fact]
    public void A_stream_not_run_in_this_session_has_no_connections_or_strip()
    {
        var item = NewStream("https://example.test/video.m3u8");

        Assert.Empty(item.ConnectionsNow());
        Assert.Null(item.StripNow());
    }
}
