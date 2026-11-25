using System.Diagnostics;
using ADM.Core;

namespace ADM.Core.Tests;

/// <summary>A connection that isn't receiving must not hold on to its piece: one asked and never answered, or one waiting to try again.</summary>
[Collection(nameof(SpeedTests))]
public sealed class HoldingTests : IDisposable
{
    const int MB = 1024 * 1024;
    static readonly TimeSpan Short = TimeSpan.FromMilliseconds(20);
    static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);

    readonly string _dir = Path.Combine(Path.GetTempPath(), "adm-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    DownloadItem NewItem(string url, int connections) =>
        new() { Url = url, Directory = _dir, FileName = "file.bin", MaxConnections = connections };

    static async Task RunAsync(DownloadItem item, RetryPolicy retry)
    {
        using var cts = new CancellationTokenSource(Limit);
        await new HttpDownloader(new RateLimiter(), new Settings(), (_, _) => { }) { Retry = retry }.RunAsync(item, cts.Token);
    }

    /// <summary>A download already part done: the first piece has <paramref name="done"/> bytes, and the second has none.</summary>
    DownloadItem PartDone(string url, byte[] onDisk, int connections, long done, long middle)
    {
        var item = NewItem(url, connections);
        item.TotalBytes = onDisk.Length;
        item.ResumeSupported = true;
        item.ResumeKnown = true;
        item.Segments.Add(new Segment { Start = 0, End = middle - 1, Downloaded = done });
        if (middle < onDisk.Length) item.Segments.Add(new Segment { Start = middle, End = onDisk.Length - 1 });
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(item.PartPath, onDisk);
        return item;
    }

    // ---- Asked and never answered

    [Fact]
    public async Task Unanswered_request_hands_over_its_piece()
    {
        await using var server = await TestServer.StartAsync(4 * MB, TimeSpan.Zero);
        server.PerRequestKBps = 2048;
        server.NeverAnswerNext = 1;
        var item = NewItem(server.HoldingUrl, 4);

        var watch = Stopwatch.StartNew();
        await RunAsync(item, new RetryPolicy { UnansweredAfter = TimeSpan.FromMilliseconds(300) });

        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"took {watch.Elapsed}");
        Assert.Equal(1, server.AbortedUnanswered);
    }

    [Fact]
    public async Task Unanswered_request_times_out_when_nothing_else_is_receiving()
    {
        await using var server = await TestServer.StartAsync(2 * MB, TimeSpan.Zero);
        server.NeverAnswerNext = 1;
        var item = PartDone(server.HoldingUrl, server.Payload, 1, MB, 2 * MB);

        await RunAsync(item, new RetryPolicy { FirstBackoff = Short, MaxBackoff = Short, AnswerTimeout = TimeSpan.FromMilliseconds(500) });

        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
    }

    [Fact]
    public async Task Unanswered_first_request_times_out()
    {
        await using var server = await TestServer.StartAsync(MB, TimeSpan.Zero);
        server.NeverAnswerFirst = 1;
        var item = NewItem(server.HoldingUrl, 1);

        await RunAsync(item, new RetryPolicy { FirstBackoff = Short, MaxBackoff = Short, AnswerTimeout = TimeSpan.FromMilliseconds(500) });

        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
    }

    [Fact]
    public async Task Range_that_is_never_answered_ends_as_a_failure()
    {
        await using var server = await TestServer.StartAsync(2 * MB, TimeSpan.Zero);
        server.NeverAnswerNext = int.MaxValue;
        var item = PartDone(server.HoldingUrl, server.Payload, 2, MB, 2 * MB);
        var retry = new RetryPolicy
        {
            FirstBackoff = Short,
            MaxBackoff = Short,
            AnswerTimeout = TimeSpan.FromMilliseconds(200),
            UnansweredAfter = TimeSpan.FromMilliseconds(100),
            ReconnectWindow = TimeSpan.FromSeconds(1),
        };

        var e = await Assert.ThrowsAnyAsync<Exception>(() => RunAsync(item, retry));

        Assert.True(Net.IsConnectionFailure(e, CancellationToken.None), $"ended with {e.GetType().Name}: {e.Message}");
        Assert.DoesNotContain("cancel", e.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Slow_answers_are_left_alone()
    {
        await using var server = await TestServer.StartAsync(4 * MB, TimeSpan.Zero);
        server.PerRequestKBps = 2048;
        server.AnswerAfter = TimeSpan.FromSeconds(1.5);
        var item = NewItem(server.HoldingUrl, 4);

        await RunAsync(item, new RetryPolicy { UnansweredAfter = TimeSpan.FromSeconds(1) });

        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
        Assert.Equal(0, server.AbortedUnanswered);
    }

    // ---- Waiting to try again

    [Fact]
    public async Task Waiting_connection_hands_over_its_piece()
    {
        await using var server = await TestServer.StartAsync(4 * MB, TimeSpan.Zero);
        server.PerRequestKBps = 2048;
        server.DropNextAfter = 64 * 1024;
        var item = NewItem(server.HoldingUrl, 4);
        var thirty = TimeSpan.FromSeconds(30);

        var watch = Stopwatch.StartNew();
        await RunAsync(item, new RetryPolicy { FirstBackoff = thirty, MaxBackoff = thirty });

        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"took {watch.Elapsed}");
    }

    [Fact]
    public async Task Waiting_after_an_http_error_hands_over_its_piece()
    {
        await using var server = await TestServer.StartAsync(4 * MB, TimeSpan.Zero);
        server.PerRequestKBps = 2048;
        server.ErrorNext = 1;
        var item = NewItem(server.HoldingUrl, 4);
        var thirty = TimeSpan.FromSeconds(30);

        var watch = Stopwatch.StartNew();
        await RunAsync(item, new RetryPolicy { FirstBackoff = thirty, MaxBackoff = thirty });

        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"took {watch.Elapsed}");
    }

    [Fact]
    public async Task A_range_that_always_fails_still_fails()
    {
        await using var server = await TestServer.StartAsync(4 * MB, TimeSpan.Zero);
        server.PerRequestKBps = 2048;
        server.FailFrom = 3 * MB;
        var item = NewItem(server.HoldingUrl, 4);
        // Long enough for a free connection to take the piece during every wait. With each starting its tries
        // afresh, only the limit on hand-overs lets this end before the test's own time limit.
        var wait = TimeSpan.FromMilliseconds(200);

        var e = await Assert.ThrowsAsync<HttpRequestException>(() =>
            RunAsync(item, new RetryPolicy { FirstBackoff = wait, MaxBackoff = wait }));

        Assert.Equal(System.Net.HttpStatusCode.InternalServerError, e.StatusCode);
    }

    // ---- The overlap check

    [Fact]
    public async Task Overlap_check_counts_towards_speed()
    {
        await using var server = await TestServer.StartAsync(2 * MB, TimeSpan.Zero);
        server.TrickleFrom = MB - 4096;
        var item = PartDone(server.HoldingUrl, server.Payload, 1, MB, 2 * MB);

        var run = RunAsync(item, new RetryPolicy());
        var deadline = DateTime.UtcNow + Limit;
        while (server.HoldingStarts.Count == 0 && DateTime.UtcNow < deadline) await Task.Delay(10);
        await Task.Delay(1200);

        // Past the second a connection has before it counts as silent, and still inside the check.
        double speed;
        lock (item.SyncRoot) speed = item.Segments[0].SpeedAt(Stopwatch.GetTimestamp());
        Assert.True(speed > 0, "a connection checking its overlap reads as silent");
        await run;
        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
    }

    [Fact]
    public async Task Slow_overlap_check_is_not_cut_short()
    {
        // Both pieces are too small to split, so the second connection is free for most of the first one's check.
        await using var server = await TestServer.StartAsync(MB + 128 * 1024, TimeSpan.Zero);
        server.TrickleFrom = MB - 4096;
        var item = PartDone(server.HoldingUrl, server.Payload, 2, MB, MB + 64 * 1024);

        await RunAsync(item, new RetryPolicy());

        // Asked for again, the check would be done over and over by connections cutting each other off.
        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
        Assert.Equal(1, server.HoldingStarts.Count(start => start == MB - 4096));
    }

    [Fact]
    public async Task Takeover_during_the_overlap_check_still_checks()
    {
        await using var server = await TestServer.StartAsync(2 * MB, TimeSpan.Zero);
        server.StallFrom = MB - 4096;
        // The file changed on the server with nothing to show it but its bytes.
        var onDisk = server.Payload.Select(b => (byte)(b ^ 0xFF)).ToArray();
        var item = PartDone(server.HoldingUrl, onDisk, 2, MB, 2 * MB - 64 * 1024);

        await RunAsync(item, new RetryPolicy());

        // A connection that didn't check would keep the first megabyte as it is on disk.
        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
        Assert.Equal(2, server.HoldingStarts.Count(start => start == MB - 4096));
    }

    [Fact]
    public async Task Handing_over_a_waiting_connection_still_checks()
    {
        await using var server = await TestServer.StartAsync(2 * MB, TimeSpan.Zero);
        server.ErrorFrom = MB - 4096;
        var onDisk = server.Payload.Select(b => (byte)(b ^ 0xFF)).ToArray();
        var item = PartDone(server.HoldingUrl, onDisk, 2, MB, 2 * MB - 64 * 1024);
        var thirty = TimeSpan.FromSeconds(30);

        await RunAsync(item, new RetryPolicy { FirstBackoff = thirty, MaxBackoff = thirty });

        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
    }

    [Fact]
    public async Task Handing_over_after_a_drop_still_checks()
    {
        // The check passes, bytes arrive, the connection drops, and the file changes on the server meanwhile.
        // Its next try would have seen that, so whoever takes its piece must too.
        await using var server = await TestServer.StartAsync(2 * MB, TimeSpan.Zero);
        server.DropFrom = MB - 4096;
        server.DropNextAfter = 256 * 1024;
        server.FlipOnDrop = true;
        var item = PartDone(server.HoldingUrl, server.Payload, 2, MB, 2 * MB - 64 * 1024);
        var thirty = TimeSpan.FromSeconds(30);

        await RunAsync(item, new RetryPolicy { FirstBackoff = thirty, MaxBackoff = thirty });

        Assert.Equal(server.FlippedPayload, File.ReadAllBytes(item.FullPath));
    }

    // ---- FTP goes through the same code

    [Fact]
    public async Task Ftp_unanswered_transfer_hands_over_its_piece()
    {
        await using var server = await FtpTestServer.StartAsync(4 * MB);
        server.NoRetrReplyOnceFrom = 1;
        var item = NewItem(server.FileUrl, 4);

        var watch = Stopwatch.StartNew();
        await RunAsync(item, new RetryPolicy { UnansweredAfter = TimeSpan.FromMilliseconds(500) });

        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), $"took {watch.Elapsed}");
    }

    [Fact]
    public async Task Ftp_waiting_connection_hands_over_its_piece()
    {
        await using var server = await FtpTestServer.StartAsync(4 * MB);
        server.DropOnlyFrom = 1;
        server.DropOnceAfterBytes = 64 * 1024;
        var item = NewItem(server.FileUrl, 4);
        var thirty = TimeSpan.FromSeconds(30);

        var watch = Stopwatch.StartNew();
        await RunAsync(item, new RetryPolicy { FirstBackoff = thirty, MaxBackoff = thirty });

        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), $"took {watch.Elapsed}");
        Assert.Equal(1, server.Drops);
    }
}
