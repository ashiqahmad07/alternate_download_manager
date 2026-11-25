using ADM.Core;

namespace ADM.Core.Tests;

/// <summary>
/// A download the browser hands over starts while its dialog is still open, as in IDM: a yes finds it under way, under
/// whatever name and folder the user settled on, and a no leaves nothing behind.
/// </summary>
public sealed class EarlyStartTests : IDisposable
{
    const int MB = 1024 * 1024;

    readonly string _dir = Path.Combine(Path.GetTempPath(), "adm-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    string Files => Path.Combine(_dir, "files");
    string StatePath => Path.Combine(_dir, "state", "state.json");
    /// <summary>Where an early download keeps its parts when the settings name no temporary folder.</summary>
    string EarlyParts => Path.Combine(_dir, "state", "parts");

    (DownloadManager M, LockedContext Ui) NewManager()
    {
        var ui = new LockedContext();
        return (new DownloadManager(StatePath, ui), ui);
    }

    DownloadRequest Request(string url, string name = "file.bin", string? folder = null) =>
        new() { Url = url, FileName = name, Directory = folder ?? Files, Captured = true };

    static DownloadItem StartEarly(DownloadManager m, LockedContext ui, DownloadRequest request)
    {
        DownloadItem? early = null;
        ui.Run(() => early = m.StartEarly(request));
        return early ?? throw new InvalidOperationException("it did not start early");
    }

    static bool Adopt(DownloadManager m, LockedContext ui, DownloadItem early, DownloadRequest settled, bool startNow, DownloadQueue? queue = null)
    {
        var adopted = false;
        ui.Run(() => adopted = m.Adopt(early, settled, startNow, queue));
        return adopted;
    }

    static bool IsRunning(DownloadManager m, LockedContext ui, DownloadItem item)
    {
        var running = false;
        ui.Run(() => running = m.IsRunning(item));
        return running;
    }

    static TaskCompletionSource Completion(DownloadManager m)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        m.DownloadCompleted += _ => done.TrySetResult();
        m.DownloadFailed += i => done.TrySetException(new Exception(i.Error));
        return done;
    }

    static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException();
            await Task.Delay(20);
        }
    }

    static IEnumerable<string> Entries(string folder) =>
        Directory.Exists(folder) ? Directory.EnumerateFileSystemEntries(folder) : Enumerable.Empty<string>();

    /// <summary>Whether any request asked for the file from its first byte, leaving out bytes=0-0 probes.</summary>
    static bool StartedOver(IEnumerable<string> ranges) => ranges.Any(r => r.StartsWith("bytes=0-") && r != "bytes=0-0");

    [Fact]
    public async Task It_downloads_before_the_answer_off_the_list_and_out_of_the_saved_state()
    {
        await using var server = await TestServer.StartAsync(4 * MB, TimeSpan.Zero);
        var (m, ui) = NewManager();

        var early = StartEarly(m, ui, Request(server.ThrottledUrl));
        await Until(() => early.DownloadedBytes >= MB / 2);

        Assert.True(IsRunning(m, ui, early));
        ui.Run(() =>
        {
            Assert.Empty(m.Items);
            m.Save();
        });
        Assert.Empty(Store.Load(StatePath).Downloads);
        // Its parts are in a folder of its own, so the name and folder can still change.
        Assert.Empty(Entries(Files));
        Assert.True(File.Exists(Path.Combine(EarlyParts, $"{early.Id:N}.admpart")));
        await m.DiscardAsync(early);
    }

    [Fact]
    public async Task A_yes_carries_on_under_the_name_and_folder_settled_on()
    {
        await using var server = await TestServer.StartAsync(4 * MB, TimeSpan.Zero);
        var (m, ui) = NewManager();
        var done = Completion(m);
        var early = StartEarly(m, ui, Request(server.ThrottledUrl));
        await Until(() => early.DownloadedBytes >= MB);
        var before = server.Ranges("/throttled.bin").Count;
        var other = Path.Combine(_dir, "other");

        Assert.True(Adopt(m, ui, early, Request(server.ThrottledUrl, "renamed.bin", other), startNow: true));
        await done.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(server.Payload, File.ReadAllBytes(Path.Combine(other, "renamed.bin")));
        Assert.False(StartedOver(server.Ranges("/throttled.bin").Skip(before)));
        ui.Run(() => Assert.Same(early, Assert.Single(m.Items)));
        Assert.Empty(Entries(Files));
        Assert.Empty(Entries(EarlyParts));
    }

    [Fact]
    public async Task A_transfer_that_finishes_first_waits_for_the_answer_to_land()
    {
        await using var server = await TestServer.StartAsync(256 * 1024, TimeSpan.Zero);
        var (m, ui) = NewManager();
        var done = Completion(m);
        var early = StartEarly(m, ui, Request(server.FileUrl));
        await Until(() => early.DownloadedBytes == server.Payload.Length);
        await Task.Delay(300);

        Assert.False(done.Task.IsCompleted);
        Assert.True(IsRunning(m, ui, early));
        Assert.Empty(Entries(Files));

        Assert.True(Adopt(m, ui, early, Request(server.FileUrl), startNow: true));
        await done.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(server.Payload, File.ReadAllBytes(Path.Combine(Files, "file.bin")));
    }

    [Fact]
    public async Task A_no_stops_it_and_leaves_nothing_behind()
    {
        await using var server = await TestServer.StartAsync(4 * MB, TimeSpan.Zero);
        var (m, ui) = NewManager();
        var reported = 0;
        m.DownloadFailed += _ => reported++;
        m.DownloadCompleted += _ => reported++;
        var early = StartEarly(m, ui, Request(server.ThrottledUrl));
        await Until(() => early.DownloadedBytes >= MB / 2);

        await m.DiscardAsync(early);
        await Until(() => !IsRunning(m, ui, early));

        ui.Run(() => Assert.Empty(m.Items));
        Assert.Empty(Entries(EarlyParts));
        Assert.Empty(Entries(Files));
        Assert.Equal(0, reported);
    }

    [Fact]
    public async Task Download_later_keeps_what_arrived_and_waits_in_its_queue()
    {
        await using var server = await TestServer.StartAsync(4 * MB, TimeSpan.Zero);
        var (m, ui) = NewManager();
        var early = StartEarly(m, ui, Request(server.ThrottledUrl));
        await Until(() => early.DownloadedBytes >= MB);

        Assert.True(Adopt(m, ui, early, Request(server.ThrottledUrl), startNow: false, m.MainQueue));
        await Until(() => !IsRunning(m, ui, early));

        ui.Run(() =>
        {
            Assert.Same(early, Assert.Single(m.Items));
            Assert.Equal(DownloadStatus.Queued, early.Status);
            Assert.Same(m.MainQueue, m.QueueFor(early));
        });
        Assert.True(early.DownloadedBytes > 0);
        Assert.True(File.Exists(early.PartPath));
    }

    [Fact]
    public async Task Another_address_in_the_dialog_is_not_the_download_started()
    {
        await using var server = await TestServer.StartAsync(4 * MB, TimeSpan.Zero);
        var (m, ui) = NewManager();
        var early = StartEarly(m, ui, Request(server.ThrottledUrl));
        await Until(() => early.DownloadedBytes >= MB / 4);

        Assert.False(Adopt(m, ui, early, Request(server.FileUrl), startNow: true));

        await Until(() => !IsRunning(m, ui, early));
        await Until(() => !Entries(EarlyParts).Any());
        ui.Run(() => Assert.Empty(m.Items));
    }

    [Fact]
    public async Task A_failure_before_the_answer_is_not_reported_and_a_yes_tries_again()
    {
        await using var server = await TestServer.StartAsync(1024, TimeSpan.Zero);
        var (m, ui) = NewManager();
        var failed = 0;
        m.DownloadFailed += _ => Interlocked.Increment(ref failed);
        var early = StartEarly(m, ui, Request(server.MissingUrl));
        await Until(() => !IsRunning(m, ui, early));
        Assert.Equal(0, Volatile.Read(ref failed));

        Assert.True(Adopt(m, ui, early, Request(server.MissingUrl), startNow: true));

        await Until(() => Volatile.Read(ref failed) == 1);
        ui.Run(() => Assert.Same(early, Assert.Single(m.Items)));
    }

    [Fact]
    public void A_link_already_listed_does_not_start_early()
    {
        var (m, ui) = NewManager();
        ui.Run(() =>
        {
            m.Add(Request("http://127.0.0.1:9/a.bin"), startNow: false);
            Assert.Null(m.StartEarly(Request("http://127.0.0.1:9/a.bin")));
        });
    }

    [Fact]
    public void Parts_an_exit_left_are_deleted_at_the_next_start()
    {
        var m = new DownloadManager(StatePath, new SynchronizationContext());
        var kept = m.Add(Request("http://127.0.0.1:9/a.bin", "a.bin"), startNow: false);
        kept.PartFolder = Path.GetFullPath(EarlyParts);
        Directory.CreateDirectory(EarlyParts);
        File.WriteAllBytes(kept.PartPath, new byte[16]);
        m.Save();
        var orphan = Path.Combine(EarlyParts, $"{Guid.NewGuid():N}.admpart");
        var orphanFolder = Path.Combine(EarlyParts, $"{Guid.NewGuid():N}.admparts");
        File.WriteAllBytes(orphan, new byte[16]);
        Directory.CreateDirectory(orphanFolder);
        File.WriteAllBytes(Path.Combine(orphanFolder, "000000.seg"), new byte[16]);

        _ = new DownloadManager(StatePath, new SynchronizationContext());

        Assert.False(File.Exists(orphan));
        Assert.False(Directory.Exists(orphanFolder));
        Assert.True(File.Exists(kept.PartPath));
    }

    [Fact]
    public async Task A_stream_waits_for_the_answer_before_it_lands()
    {
        await using var server = await TestServer.StartAsync(256 * 1024, TimeSpan.Zero);
        var item = new DownloadItem
        {
            Url = server.HlsByteRangeUrl, Directory = Files, FileName = "video.ts", Kind = DownloadKind.Hls,
            MaxConnections = 4, OwnPartFolder = Path.GetFullPath(EarlyParts),
        };
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        item.Settled = settled.Task;

        var run = new HlsDownloader(new RateLimiter(), new Settings(), null).RunAsync(item, CancellationToken.None);
        await Until(() => item.HlsSegmentCount > 0 && item.HlsSegmentsDone == item.HlsSegmentCount);
        await Task.Delay(300);

        Assert.False(run.IsCompleted);
        Assert.Empty(Entries(Files));
        Assert.True(Directory.Exists(Path.Combine(EarlyParts, $"{item.Id:N}.admparts")));

        item.FileName = "renamed" + Path.GetExtension(item.FileName);
        settled.SetResult();
        await run.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(server.Payload, File.ReadAllBytes(Path.Combine(Files, item.FileName)));
    }
}
