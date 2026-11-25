using ADM.Core;

namespace ADM.Core.Tests;

/// <summary>
/// The temporary folder: part files kept away from the download folder until they are complete, and resumes
/// that ignore a server changing a file's modification time.
/// </summary>
public sealed class TempFolderTests : IDisposable
{
    const int MB = 1024 * 1024;

    readonly string _dir = Path.Combine(Path.GetTempPath(), "adm-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    string Files => Path.Combine(_dir, "files");
    string Temp => Path.Combine(_dir, "temp");
    string StatePath => Path.Combine(_dir, "state", "state.json");

    DownloadItem NewItem(string url) => new() { Url = url, Directory = Files, FileName = "file.bin", MaxConnections = 8 };

    Settings WithTemp(string? folder = null) => new() { TempFolder = folder ?? Temp };

    static HttpDownloader Downloader(Settings settings) => new(new RateLimiter(), settings, (_, _) => { });

    static Task RunAsync(DownloadItem item, Settings settings, CancellationToken ct = default) => Downloader(settings).RunAsync(item, ct);

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
    static async Task PauseAfterAsync(DownloadItem item, Settings settings, long bytes)
    {
        using var cts = new CancellationTokenSource();
        var run = RunAsync(item, settings, cts.Token);
        await Until(() => item.DownloadedBytes >= bytes || run.IsCompleted);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    static IEnumerable<string> Entries(string folder) =>
        Directory.Exists(folder) ? Directory.EnumerateFileSystemEntries(folder) : Enumerable.Empty<string>();

    string TempPart(DownloadItem item) => Path.Combine(Temp, $"{item.Id:N}.admpart");

    /// <summary>Whether any request asked for the file from its first byte, leaving out bytes=0-0 probes.</summary>
    static bool StartedOver(IEnumerable<string> ranges) => ranges.Any(r => r.StartsWith("bytes=0-") && r != "bytes=0-0");

    [Fact]
    public async Task Temp_folder_holds_the_part_and_the_finished_file_lands_in_place()
    {
        await using var server = await TestServer.StartAsync(4 * MB, TimeSpan.Zero);
        var item = NewItem(server.ThrottledUrl);

        var run = RunAsync(item, WithTemp());
        await Until(() => item.DownloadedBytes >= MB / 2 || run.IsCompleted);
        Assert.False(run.IsCompleted, "the download finished before it could be looked at");
        Assert.True(File.Exists(TempPart(item)));
        Assert.Empty(Entries(Files));
        await run;

        Assert.Equal(Path.GetFullPath(Temp), item.PartFolder);
        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
        Assert.Empty(Entries(Temp));
    }

    [Fact]
    public async Task Changing_the_temp_folder_keeps_unfinished_parts()
    {
        await using var server = await TestServer.StartAsync(4 * MB, TimeSpan.Zero);
        var item = NewItem(server.ThrottledUrl);
        await PauseAfterAsync(item, WithTemp(), MB);
        Assert.True(File.Exists(TempPart(item)));
        var other = Path.Combine(_dir, "other temp");

        await RunAsync(item, WithTemp(other));

        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
        Assert.Empty(Entries(other));
        Assert.Empty(Entries(Temp));
    }

    [Fact]
    public async Task Temp_part_resumes_after_a_restart()
    {
        await using var server = await TestServer.StartAsync(4 * MB, TimeSpan.Zero);
        var ui = new LockedContext();
        var m = new DownloadManager(StatePath, ui);
        m.State.Settings.TempFolder = Temp;
        DownloadItem item = null!;
        ui.Run(() => item = m.Add(new DownloadRequest { Url = server.ThrottledUrl, FileName = "file.bin", Directory = Files }, startNow: true));
        await Until(() => item.DownloadedBytes >= MB);
        ui.Run(() => m.Pause(item));
        await Until(() => { var running = true; ui.Run(() => running = m.IsRunning(item)); return !running; });
        ui.Run(m.Save);
        var before = server.Ranges("/throttled.bin").Count;

        var ui2 = new LockedContext();
        var reloaded = new DownloadManager(StatePath, ui2);
        var again = Assert.Single(reloaded.Items);
        Assert.Equal(Path.GetFullPath(Temp), again.PartFolder);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        reloaded.DownloadCompleted += _ => done.TrySetResult();
        reloaded.DownloadFailed += i => done.TrySetException(new Exception(i.Error));
        ui2.Run(() => reloaded.Start(again));
        await done.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(server.Payload, File.ReadAllBytes(again.FullPath));
        Assert.False(StartedOver(server.Ranges("/throttled.bin").Skip(before)));
        Assert.Empty(Entries(Temp));
    }

    [Fact]
    public async Task Hls_parts_use_the_temp_folder()
    {
        await using var server = await TestServer.StartAsync(1024, TimeSpan.Zero);
        var item = new DownloadItem { Url = server.HlsLiveUrl(), Directory = Files, FileName = "live.ts", Kind = DownloadKind.Hls, MaxConnections = 4 };
        var downloader = new HlsDownloader(new RateLimiter(), WithTemp(), null) { LivePoll = TimeSpan.FromMilliseconds(50) };
        using var stop = new CancellationTokenSource();

        var run = downloader.RunAsync(item, CancellationToken.None, stop.Token);
        await Until(() => item.HlsSegmentsDone >= 3);
        Assert.True(Directory.Exists(Path.Combine(Temp, $"{item.Id:N}.admparts")));
        Assert.False(Directory.Exists(item.FullPath + ".admparts"));
        stop.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.True(File.ReadAllBytes(item.FullPath).Length >= 3 * 4096);
        Assert.Empty(Entries(Temp));
    }

    [Fact]
    public async Task Moving_an_unfinished_download_leaves_its_temp_part_alone()
    {
        await using var server = await TestServer.StartAsync(4 * MB, TimeSpan.Zero);
        var ui = new LockedContext();
        var m = new DownloadManager(StatePath, ui);
        m.State.Settings.TempFolder = Temp;
        DownloadItem item = null!;
        ui.Run(() => item = m.Add(new DownloadRequest { Url = server.ThrottledUrl, FileName = "file.bin", Directory = Files }, startNow: true));
        await Until(() => item.DownloadedBytes >= MB);
        ui.Run(() => m.Pause(item));
        await Until(() => { var running = true; ui.Run(() => running = m.IsRunning(item)); return !running; });
        var part = TempPart(item);
        Assert.True(File.Exists(part));

        await m.MoveAsync(item, Path.Combine(Files, "sub", "renamed.bin"), replace: false);
        Assert.Equal(part, item.PartPath);
        Assert.True(File.Exists(part));

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        m.DownloadCompleted += _ => done.TrySetResult();
        m.DownloadFailed += i => done.TrySetException(new Exception(i.Error));
        ui.Run(() => m.Start(item));
        await done.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(server.Payload, File.ReadAllBytes(Path.Combine(Files, "sub", "renamed.bin")));
        Assert.False(File.Exists(part));
    }

    [Fact]
    public async Task Failed_assembly_resumes_without_downloading_again()
    {
        await using var server = await TestServer.StartAsync(2 * MB, TimeSpan.Zero);
        var item = NewItem(server.FileUrl);
        var failing = new HttpDownloader(new RateLimiter(), WithTemp(), (_, _) => { })
        {
            MoveIntoPlace = (_, _, _) => throw new IOException("The destination went away."),
        };

        await Assert.ThrowsAsync<IOException>(() => failing.RunAsync(item, CancellationToken.None));
        Assert.True(File.Exists(item.PartPath));
        Assert.False(File.Exists(item.FullPath));
        var saved = item.SegmentsSnapshot;
        Assert.NotEmpty(saved);
        Assert.All(saved, s => Assert.True(s.IsComplete));
        var hits = server.Hits("/file.bin");

        await RunAsync(item, WithTemp());

        Assert.Equal(hits, server.Hits("/file.bin"));
        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
        Assert.False(File.Exists(item.PartPath));
        Assert.Empty(item.SegmentsSnapshot);
    }

    [Fact]
    public void Orphaned_parts_are_listed_only_from_the_temp_folder()
    {
        var m = new DownloadManager(StatePath, new SynchronizationContext());
        m.State.Settings.TempFolder = Temp;
        Directory.CreateDirectory(Temp);
        Directory.CreateDirectory(Files);
        var used = m.Add(new DownloadRequest { Url = "http://127.0.0.1:9/a.bin", FileName = "a.bin", Directory = Files }, startNow: false);
        used.PartFolder = Path.GetFullPath(Temp);
        File.WriteAllBytes(used.PartPath, new byte[16]);
        var orphanFile = Path.Combine(Temp, $"{Guid.NewGuid():N}.admpart");
        var orphanFolder = Path.Combine(Temp, $"{Guid.NewGuid():N}.admparts");
        File.WriteAllBytes(orphanFile, new byte[16]);
        Directory.CreateDirectory(orphanFolder);
        File.WriteAllText(Path.Combine(Temp, "notes.txt"), "the user's own");
        File.WriteAllBytes(Path.Combine(Files, "b.bin.admpart"), new byte[16]);

        var orphans = m.OrphanedParts();

        Assert.Equal(new[] { orphanFile, orphanFolder }.Select(Path.GetFullPath).Order(), orphans.Order());
    }

    [Fact]
    public void No_orphans_without_a_temp_folder()
    {
        var m = new DownloadManager(StatePath, new SynchronizationContext());
        Directory.CreateDirectory(Files);
        File.WriteAllBytes(Path.Combine(Files, "b.bin.admpart"), new byte[16]);

        Assert.Empty(m.OrphanedParts());
    }

    [Fact]
    public void A_part_file_beside_its_file_in_the_temp_folder_is_no_orphan()
    {
        var m = new DownloadManager(StatePath, new SynchronizationContext());
        m.State.Settings.TempFolder = Temp;
        Directory.CreateDirectory(Temp);
        // Started before the temporary folder was set, in what is now that folder.
        var beside = m.Add(new DownloadRequest { Url = "http://127.0.0.1:9/a.bin", FileName = "a.bin", Directory = Temp }, startNow: false);
        beside.PartFolder = null;
        File.WriteAllBytes(beside.PartPath, new byte[16]);

        Assert.Empty(m.OrphanedParts());
    }

    [Fact]
    public void Deleting_orphaned_parts_leaves_everything_else()
    {
        var m = new DownloadManager(StatePath, new SynchronizationContext());
        m.State.Settings.TempFolder = Temp;
        Directory.CreateDirectory(Temp);
        Directory.CreateDirectory(Files);
        var used = m.Add(new DownloadRequest { Url = "http://127.0.0.1:9/a.bin", FileName = "a.bin", Directory = Files }, startNow: false);
        used.PartFolder = Path.GetFullPath(Temp);
        File.WriteAllBytes(used.PartPath, new byte[16]);
        var orphanFile = Path.Combine(Temp, $"{Guid.NewGuid():N}.admpart");
        var orphanRemux = Path.Combine(Temp, $"{Guid.NewGuid():N}.admremux");
        var orphanFolder = Path.Combine(Temp, $"{Guid.NewGuid():N}.admparts");
        File.WriteAllBytes(orphanFile, new byte[16]);
        File.WriteAllBytes(orphanRemux, new byte[16]);
        Directory.CreateDirectory(orphanFolder);
        File.WriteAllBytes(Path.Combine(orphanFolder, "0.ts"), new byte[16]);
        var notes = Path.Combine(Temp, "notes.txt");
        File.WriteAllText(notes, "the user's own");

        m.DeleteOrphanedParts();

        Assert.True(File.Exists(used.PartPath));
        Assert.True(File.Exists(notes));
        Assert.False(File.Exists(orphanFile));
        Assert.False(File.Exists(orphanRemux));
        Assert.False(Directory.Exists(orphanFolder));
        Assert.Empty(m.OrphanedParts());
    }

    [Fact]
    public async Task Ignored_modification_time_keeps_progress()
    {
        await using var server = await TestServer.StartAsync(4 * MB, TimeSpan.Zero);

        var strict = NewItem(server.TouchedUrl);
        await PauseAfterAsync(strict, new Settings(), MB);
        var before = server.Ranges("/touched.bin").Count;
        await RunAsync(strict, new Settings());
        Assert.True(StartedOver(server.Ranges("/touched.bin").Skip(before)));

        var lenient = NewItem(server.TouchedUrl);
        lenient.FileName = "lenient.bin";
        var ignoring = new Settings { IgnoreModificationTime = true };
        await PauseAfterAsync(lenient, ignoring, MB);
        before = server.Ranges("/touched.bin").Count;
        await RunAsync(lenient, ignoring);
        Assert.False(StartedOver(server.Ranges("/touched.bin").Skip(before)));
        Assert.Equal(server.Payload, File.ReadAllBytes(lenient.FullPath));
    }

    [Fact]
    public async Task Sync_check_ignores_modification_time_when_set()
    {
        await using var server = await TestServer.StartAsync(256 * 1024, TimeSpan.Zero);
        var item = NewItem(server.TouchedUrl);
        await RunAsync(item, new Settings());
        Assert.NotNull(item.LastModified);

        Assert.True(await HttpDownloader.HasChangedAsync(item, new Settings(), CancellationToken.None));
        Assert.False(await HttpDownloader.HasChangedAsync(item, new Settings { IgnoreModificationTime = true }, CancellationToken.None));
    }
}
