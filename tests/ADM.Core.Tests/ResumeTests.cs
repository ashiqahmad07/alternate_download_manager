using ADM.Core;

namespace ADM.Core.Tests;

/// <summary>Resuming HTTP downloads: after a pause, after a restart, and when the server's copy can't be resumed onto.</summary>
[Collection(nameof(SpeedTests))]
public sealed class ResumeTests : IDisposable
{
    const int MB = 1024 * 1024;

    readonly string _dir = Path.Combine(Path.GetTempPath(), "adm-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    string StatePath => Path.Combine(_dir, "state.json");

    DownloadItem NewItem(string url) => new() { Url = url, Directory = _dir, FileName = "file.bin", MaxConnections = 8 };

    static Task RunAsync(DownloadItem item, CancellationToken ct = default) =>
        new HttpDownloader(new RateLimiter(), new Settings(), (_, _) => { }).RunAsync(item, ct);

    static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException();
            await Task.Delay(20);
        }
    }

    /// <summary>Runs until at least <paramref name="bytes"/> have arrived, then cancels, as a pause does.</summary>
    static async Task PauseAfterAsync(DownloadItem item, long bytes)
    {
        using var cts = new CancellationTokenSource();
        var run = RunAsync(item, cts.Token);
        await Until(() => item.DownloadedBytes >= bytes || run.IsCompleted);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    /// <summary>A part file holding <paramref name="saved"/> bytes that aren't <paramref name="content"/>, and progress that says so.</summary>
    void SeedPartial(DownloadItem item, byte[] content, int saved, string? etag = null)
    {
        item.ResumeSupported = true;
        item.TotalBytes = content.Length;
        item.ETag = etag;
        item.SegmentsSnapshot = new() { new Segment { Start = 0, End = content.Length - 1, Downloaded = saved } };
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(item.PartPath, content[..saved].Select(b => (byte)~b).ToArray());
    }

    /// <summary>The first byte each request asked for, leaving out bytes=0-0 probes.</summary>
    static IEnumerable<long> RangeStarts(IEnumerable<string> ranges) => ranges
        .Where(r => r.StartsWith("bytes=") && r != "bytes=0-0")
        .Select(r => long.Parse(r["bytes=".Length..r.IndexOf('-')]));

    /// <summary>No request starts inside saved bytes, apart from the overlap a continuation asks for again to check them.</summary>
    static void AssertNothingSavedIsRequestedAgain(IEnumerable<Segment> saved, IEnumerable<string> ranges)
    {
        foreach (var start in RangeStarts(ranges))
            Assert.DoesNotContain(saved, s => s.Start <= start && start < s.Start + s.Downloaded - HttpDownloader.OverlapBytes);
    }

    /// <summary>A part file holding the first <paramref name="saved"/> bytes of <paramref name="content"/>, and progress that says so.</summary>
    void SeedSaved(DownloadItem item, byte[] content, int saved)
    {
        item.ResumeSupported = true;
        item.TotalBytes = content.Length;
        item.SegmentsSnapshot = new() { new Segment { Start = 0, End = content.Length - 1, Downloaded = saved } };
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(item.PartPath, content[..saved]);
    }

    [Fact]
    public async Task Pause_and_resume_reproduces_the_file()
    {
        await using var server = await TestServer.StartAsync(4 * MB, TimeSpan.Zero);
        var item = NewItem(server.ThrottledUrl);

        await PauseAfterAsync(item, MB);
        var saved = item.SegmentsSnapshot;
        Assert.InRange(saved.Sum(s => s.Downloaded), 1, 4 * MB - 1);
        var before = server.Ranges("/throttled.bin").Count;

        await RunAsync(item);

        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
        AssertNothingSavedIsRequestedAgain(saved, server.Ranges("/throttled.bin").Skip(before));
    }

    [Fact]
    public async Task Resume_after_reload_continues_from_saved_progress()
    {
        await using var server = await TestServer.StartAsync(4 * MB, TimeSpan.Zero);
        var ui = new LockedContext();
        var m = new DownloadManager(StatePath, ui);
        DownloadItem item = null!;
        ui.Run(() => item = m.Add(new DownloadRequest { Url = server.ThrottledUrl, FileName = "file.bin", Directory = _dir }, startNow: true));

        await Until(() => item.DownloadedBytes >= MB);
        ui.Run(() => m.Pause(item));
        await Until(() => { var running = true; ui.Run(() => running = m.IsRunning(item)); return !running; });
        ui.Run(m.Save);
        var saved = item.SegmentsSnapshot;
        Assert.InRange(saved.Sum(s => s.Downloaded), 1, 4 * MB - 1);
        var before = server.Ranges("/throttled.bin").Count;

        var ui2 = new LockedContext();
        var reloaded = new DownloadManager(StatePath, ui2);
        var again = Assert.Single(reloaded.Items);
        Assert.Equal(DownloadStatus.Paused, again.Status);
        Assert.Equal(saved.Sum(s => s.Downloaded), again.SegmentsSnapshot.Sum(s => s.Downloaded));
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        reloaded.DownloadCompleted += _ => done.TrySetResult();
        reloaded.DownloadFailed += i => done.TrySetException(new Exception(i.Error));
        ui2.Run(() => reloaded.Start(again));
        await done.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(server.Payload, File.ReadAllBytes(again.FullPath));
        AssertNothingSavedIsRequestedAgain(saved, server.Ranges("/throttled.bin").Skip(before));
    }

    [Fact]
    public async Task Changed_etag_starts_over()
    {
        await using var server = await TestServer.StartAsync(2 * MB, TimeSpan.Zero);
        var item = NewItem(server.VersionedUrl);
        SeedPartial(item, server.VersionedPayload(0), 768 * 1024, etag: "\"v0\"");
        server.Version = 1;

        await RunAsync(item);

        Assert.Equal(server.VersionedPayload(1), File.ReadAllBytes(item.FullPath));
        Assert.Equal("\"v1\"", item.ETag);
    }

    [Fact]
    public async Task Changed_file_without_validators_is_detected_on_resume()
    {
        // Same size, no ETag, no Last-Modified: only the bytes show that the file changed.
        await using var server = await TestServer.StartAsync(2 * MB, TimeSpan.Zero);
        server.Validators = false;
        var item = NewItem(server.VersionedUrl);
        SeedSaved(item, server.VersionedPayload(0), 768 * 1024);
        server.Version = 1;

        await RunAsync(item);

        Assert.Equal(server.VersionedPayload(1), File.ReadAllBytes(item.FullPath));
    }

    [Fact]
    public async Task File_that_keeps_changing_fails_after_one_restart()
    {
        await using var server = await TestServer.StartAsync(2 * MB, TimeSpan.Zero);
        server.Validators = false;
        server.KeepsChanging = true;
        var item = NewItem(server.VersionedUrl);
        item.MaxConnections = 1;
        SeedSaved(item, server.VersionedPayload(0), 768 * 1024);

        var e = await Assert.ThrowsAsync<IOException>(() => RunAsync(item));

        Assert.Equal("The file keeps changing on the server.", e.Message);
    }

    [Fact]
    public async Task Overlap_bytes_are_not_counted_twice()
    {
        await using var server = await TestServer.StartAsync(4 * MB, TimeSpan.Zero);
        var item = NewItem(server.ThrottledUrl);
        SeedSaved(item, server.Payload, MB);

        var run = RunAsync(item);
        while (!run.IsCompleted)
        {
            // Counted progress only ever trails the bytes written. Once every byte is in, the segments are
            // cleared while the file is still being finished; the total is checked at the end.
            var counted = item.DownloadedBytes;
            long written;
            bool finished;
            lock (item.SyncRoot)
            {
                written = item.Segments.Sum(s => s.Downloaded);
                finished = item.Segments.Count == 0;
            }
            Assert.True(counted <= written || finished, $"{counted} bytes counted, {written} written");
            await Task.Delay(5);
        }
        await run;

        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
        Assert.Equal(item.TotalBytes, item.DownloadedBytes);
    }

    [Fact]
    public async Task Resume_column_shows_what_the_server_said()
    {
        await using var server = await TestServer.StartAsync(256 * 1024, TimeSpan.Zero);
        var ranged = NewItem(server.FileUrl);
        var whole = NewItem(server.NoRangesUrl);
        whole.FileName = "whole.bin";
        Assert.Equal("", ranged.ResumeText);
        Assert.Null(ranged.Resumable);

        await RunAsync(ranged);
        await RunAsync(whole);

        Assert.Equal("Yes", ranged.ResumeText);
        Assert.Equal("No", whole.ResumeText);
        Assert.False(whole.Resumable);
        Assert.Equal("Yes", new DownloadItem { Kind = DownloadKind.Hls }.ResumeText);
    }

    [Fact]
    public async Task Server_without_ranges_starts_over_with_one_connection()
    {
        await using var server = await TestServer.StartAsync(2 * MB);
        var item = NewItem(server.NoRangesUrl);
        SeedPartial(item, server.Payload, 768 * 1024);

        await RunAsync(item);

        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
        Assert.False(item.ResumeSupported);
        // The probe, then the whole file once, on one connection. (Counting requests at once would also
        // count the probe's unread answer, which the server may still be letting go.)
        Assert.Equal(2, server.Hits("/noranges.bin"));
    }
}
