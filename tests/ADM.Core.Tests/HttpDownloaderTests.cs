using System.Diagnostics;
using ADM.Core;

namespace ADM.Core.Tests;

public sealed class HttpDownloaderTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "adm-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    DownloadItem NewItem(string url) => new() { Url = url, Directory = _dir, FileName = "file.bin", MaxConnections = 8 };

    static Task RunAsync(DownloadItem item, Settings? settings = null) =>
        new HttpDownloader(new RateLimiter(), settings ?? new Settings(), (_, _) => { }).RunAsync(item, CancellationToken.None);

    [Fact]
    public async Task Multi_connection_download_reproduces_the_file()
    {
        await using var server = await TestServer.StartAsync(6 * 1024 * 1024);
        var item = NewItem(server.FileUrl);

        await RunAsync(item);

        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
        Assert.True(server.MaxConcurrentRequests > 2, $"saw only {server.MaxConcurrentRequests} parallel requests");
    }

    [Fact]
    public async Task Server_connection_limit_caps_parallel_requests()
    {
        await using var server = await TestServer.StartAsync(6 * 1024 * 1024);
        var settings = new Settings { ServerLimits = { new ServerLimit { Sites = "127.0.0.1", Connections = 2 } } };
        var item = NewItem(server.FileUrl);

        await RunAsync(item, settings);

        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
        Assert.True(server.MaxConcurrentRequests <= 2, $"saw {server.MaxConcurrentRequests} parallel requests");
    }

    [Fact]
    public async Task Per_download_speed_limit_slows_the_transfer()
    {
        await using var server = await TestServer.StartAsync(384 * 1024, TimeSpan.Zero);
        var item = NewItem(server.FileUrl);
        item.SpeedLimitKBps = 128;

        var watch = Stopwatch.StartNew();
        await RunAsync(item);

        // 384 KB at 128 KB/s is three seconds; the bucket may hold up to one second of burst.
        Assert.True(watch.Elapsed >= TimeSpan.FromSeconds(1.5), $"took only {watch.Elapsed}");
        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
    }

    [Fact]
    public async Task Overwrite_replaces_an_existing_file_instead_of_numbering()
    {
        await using var server = await TestServer.StartAsync(64 * 1024, TimeSpan.Zero);
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "file.bin"), "old copy");
        var item = NewItem(server.FileUrl);
        item.OverwritePath = Path.Combine(_dir, "file.bin");

        await RunAsync(item);

        Assert.Equal("file.bin", item.FileName);
        Assert.Equal(server.Payload, File.ReadAllBytes(Path.Combine(_dir, "file.bin")));
        Assert.False(File.Exists(Path.Combine(_dir, "file (1).bin")));
    }

    [Fact]
    public async Task Overwrite_is_limited_to_the_recorded_file()
    {
        // Permission to replace other.bin must not let the download replace file.bin, e.g.
        // after the server or a category change gave it a different name or folder.
        await using var server = await TestServer.StartAsync(64 * 1024, TimeSpan.Zero);
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "file.bin"), "keep me");
        var item = NewItem(server.FileUrl);
        item.OverwritePath = Path.Combine(_dir, "other.bin");

        await RunAsync(item);

        Assert.Equal("keep me", File.ReadAllText(Path.Combine(_dir, "file.bin")));
        Assert.Equal("file (1).bin", item.FileName);
        Assert.Equal(server.Payload, File.ReadAllBytes(Path.Combine(_dir, "file (1).bin")));
    }
}
