using System.Diagnostics;
using ADM.Core;

namespace ADM.Core.Tests;

/// <summary>Part files on disk: sparse while downloading, a free-space check up front, and a full disk that ends the download.</summary>
[Collection(nameof(SpeedTests))]
public sealed class PartFileTests : IDisposable
{
    const int MB = 1024 * 1024;

    readonly string _dir = Path.Combine(Path.GetTempPath(), "adm-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

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

    static bool IsSparse(string path) => File.GetAttributes(path).HasFlag(FileAttributes.SparseFile);

    [Fact]
    public async Task Part_file_is_sparse_while_downloading()
    {
        await using var server = await TestServer.StartAsync(4 * MB, TimeSpan.Zero);
        var item = NewItem(server.ThrottledUrl);
        using var cts = new CancellationTokenSource();
        var run = NewDownloader().RunAsync(item, cts.Token);

        await Until(() => item.DownloadedBytes > 0);
        var sparse = IsSparse(item.PartPath);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        Assert.True(sparse, "the part file is not sparse");
    }

    [Fact]
    public async Task Completed_file_is_not_sparse()
    {
        await using var server = await TestServer.StartAsync(2 * MB, TimeSpan.Zero);
        var item = NewItem(server.FileUrl);

        await NewDownloader().RunAsync(item, CancellationToken.None);

        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
        Assert.False(IsSparse(item.FullPath), "the finished file is still sparse");
    }

    [Fact]
    public async Task Not_enough_space_fails_before_downloading()
    {
        await using var server = await TestServer.StartAsync(2 * MB, TimeSpan.Zero);
        var item = NewItem(server.FileUrl);
        var downloader = new HttpDownloader(new RateLimiter(), new Settings(), (_, _) => { }) { FreeSpace = _ => MB };

        var e = await Assert.ThrowsAsync<IOException>(() => downloader.RunAsync(item, CancellationToken.None));

        var drive = Path.GetPathRoot(Path.GetFullPath(_dir))!.TrimEnd('\\');
        Assert.Equal($"Not enough disk space on {drive} (needs 2 MB, 1 MB free).", e.Message);
        Assert.Equal(1, server.Hits("/file.bin")); // the probe, and no transfer
        Assert.False(File.Exists(item.PartPath));
    }

    [Fact]
    public async Task Resume_needs_space_only_for_what_is_left()
    {
        await using var server = await TestServer.StartAsync(2 * MB, TimeSpan.Zero);
        int total = server.Payload.Length, saved = 1536 * 1024;
        var item = NewItem(server.FileUrl);
        item.ResumeSupported = true;
        item.TotalBytes = total;
        item.SegmentsSnapshot = new() { new Segment { Start = 0, End = total - 1, Downloaded = saved } };
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(item.PartPath, server.Payload[..saved]);
        string? askedAbout = null;
        var downloader = new HttpDownloader(new RateLimiter(), new Settings(), (_, _) => { })
        {
            FreeSpace = directory => { askedAbout = directory; return total - saved; },
        };

        await downloader.RunAsync(item, CancellationToken.None);

        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
        Assert.Equal(_dir, askedAbout);
    }

    [Fact]
    public async Task Disk_full_fails_once_and_keeps_progress()
    {
        await using var server = await TestServer.StartAsync(4 * MB, TimeSpan.Zero);
        var item = NewItem(server.FileUrl);
        LossyPartFile? lossy = null;
        var downloader = new HttpDownloader(new RateLimiter(), new Settings(), (_, _) => { })
        {
            CheckpointInterval = TimeSpan.FromMilliseconds(100),
            OpenPartFile = path => lossy = new LossyPartFile(path) { DiskFullAfter = MB },
        };

        var watch = Stopwatch.StartNew();
        var e = await Assert.ThrowsAsync<IOException>(() => downloader.RunAsync(item, CancellationToken.None));

        // Retrying a full disk would back off for minutes; each connection meets it once and stops.
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"took {watch.Elapsed}");
        Assert.Contains("not enough space", e.Message, StringComparison.OrdinalIgnoreCase);
        Assert.InRange(lossy!.DiskFullErrors, 1, item.MaxConnections);
        var kept = item.SegmentsSnapshot;
        Assert.InRange(kept.Sum(s => s.Downloaded), 1, lossy.FlushedBytes);
        var onDisk = File.ReadAllBytes(item.PartPath);
        foreach (var s in kept)
            Assert.Equal(server.Payload.AsSpan((int)s.Start, (int)s.Downloaded).ToArray(), onDisk.AsSpan((int)s.Start, (int)s.Downloaded).ToArray());
    }
}
