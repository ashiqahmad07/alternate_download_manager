using ADM.Core;

namespace ADM.Core.Tests;

/// <summary>Saved progress must survive a power cut: it may claim only bytes that are on the disk itself.</summary>
[Collection(nameof(SpeedTests))]
public sealed class DurableResumeTests : IDisposable
{
    const int MB = 1024 * 1024;

    readonly string _dir = Path.Combine(Path.GetTempPath(), "adm-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    string StatePath => Path.Combine(_dir, "state.json");

    DownloadItem NewItem(string url) => new() { Url = url, Directory = _dir, FileName = "file.bin", MaxConnections = 8 };

    static HttpDownloader NewDownloader() => new(new RateLimiter(), new Settings(), (_, _) => { });

    static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException();
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task Resume_after_power_loss_reproduces_the_file()
    {
        await using var server = await TestServer.StartAsync(4 * MB, TimeSpan.Zero);
        server.PerRequestKBps = 256;
        var item = NewItem(server.ThrottledUrl);
        LossyPartFile? lossy = null;
        var downloader = new HttpDownloader(new RateLimiter(), new Settings(), (_, _) => { })
        {
            CheckpointInterval = TimeSpan.FromMilliseconds(100),
            OpenPartFile = path => lossy = new LossyPartFile(path),
        };
        using var cts = new CancellationTokenSource();
        var run = downloader.RunAsync(item, cts.Token);

        // The power goes once some progress is on disk and the cache holds more: state.json keeps what
        // was saved at that moment, and whatever the cache held is lost.
        await Until(() => lossy is { FlushedBytes: >= MB } file
            && file.TryPowerLoss(64 * 1024, () => Store.Save(StatePath, new AppState { Downloads = { item } })));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        var restored = Assert.Single(Store.Load(StatePath).Downloads);
        Assert.True(restored.SegmentsSnapshot.Sum(s => s.Downloaded) > 0, "no progress was saved, so nothing was resumed");
        await NewDownloader().RunAsync(restored, CancellationToken.None);

        Assert.Equal(server.Payload, File.ReadAllBytes(restored.FullPath));
    }

    [Fact]
    public async Task Saved_progress_never_runs_ahead_of_flushed_data()
    {
        await using var server = await TestServer.StartAsync(4 * MB, TimeSpan.Zero);
        server.PerRequestKBps = 256;
        var item = NewItem(server.ThrottledUrl);
        LossyPartFile? lossy = null;
        var downloader = new HttpDownloader(new RateLimiter(), new Settings(), (_, _) => { })
        {
            CheckpointInterval = TimeSpan.FromMilliseconds(100),
            OpenPartFile = path => lossy = new LossyPartFile(path),
        };

        var run = downloader.RunAsync(item, CancellationToken.None);
        long highest = 0;
        while (!run.IsCompleted)
        {
            // What a save would write now, then what is on disk; flushed bytes only ever grow.
            var saved = item.SegmentsSnapshot.Sum(s => s.Downloaded);
            var flushed = lossy?.FlushedBytes ?? 0;
            Assert.True(saved <= flushed, $"saved progress claims {saved} bytes, but only {flushed} are on disk");
            highest = Math.Max(highest, saved);
            await Task.Delay(5);
        }
        await run;

        Assert.True(highest > 0, "no progress was ever saved while downloading");
        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
    }

    [Fact]
    public async Task Hls_part_is_flushed_before_it_counts_as_done()
    {
        // Parts closed without a flush lose their bytes, so the joined file is right only if each part was flushed.
        await using var server = await TestServer.StartAsync(256 * 1024, TimeSpan.Zero);
        var item = new DownloadItem { Url = server.HlsTsUrl, Directory = _dir, FileName = "video.ts", Kind = DownloadKind.Hls, MaxConnections = 8 };
        var downloader = new HlsDownloader(new RateLimiter(), new Settings(), ffmpeg: null)
        {
            OpenPartFile = path => new LossyPartFile(path),
        };

        await downloader.RunAsync(item, CancellationToken.None);

        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
    }
}
