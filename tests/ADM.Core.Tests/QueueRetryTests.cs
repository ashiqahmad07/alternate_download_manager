using ADM.Core;

namespace ADM.Core.Tests;

[Collection(nameof(SpeedTests))]
public sealed class QueueRetryTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "adm-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    readonly LockedContext _ui = new();

    DownloadManager NewManager(TimeSpan retryDelay)
    {
        var m = new DownloadManager(Path.Combine(_dir, "state.json"), _ui) { RetryDelay = retryDelay };
        m.State.Settings.DownloadRoot = _dir;
        return m;
    }

    static DownloadItem Queue(DownloadManager m, string url, string name) =>
        m.Add(new DownloadRequest { Url = url, FileName = name }, startNow: false);

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
    public async Task Failed_file_goes_last_and_the_next_file_starts()
    {
        // A missing file fails at once (404); the good one is held long enough to be seen running.
        await using var server = await TestServer.StartAsync(16 * 1024, TimeSpan.FromSeconds(1));
        var m = NewManager(TimeSpan.FromMinutes(10));
        m.MainQueue.MaxConcurrent = 1;
        var bad = Queue(m, server.MissingUrl, "bad.bin");
        var good = Queue(m, server.FileUrl, "good.bin");

        m.StartQueue(m.MainQueue);
        await Until(() => bad.Status == DownloadStatus.Queued && m.IsRunning(good));

        Assert.Equal(1, good.QueuePosition);
        Assert.Equal(2, bad.QueuePosition);
        Assert.NotNull(bad.Error);
        Assert.StartsWith("Retrying: ", bad.StatusText);
        await m.ShutdownAsync();
    }

    [Fact]
    public async Task Lone_failed_file_waits_before_retrying()
    {
        await using var server = await TestServer.StartAsync(16 * 1024, TimeSpan.Zero);
        var m = NewManager(TimeSpan.FromSeconds(30));
        var bad = Queue(m, server.MissingUrl, "bad.bin");

        m.StartQueue(m.MainQueue);
        await Until(() => bad.Status == DownloadStatus.Queued && bad.Error != null && !m.IsRunning(bad));

        _ui.Run(() => m.Tick(DateTime.Now));
        await Task.Delay(200);
        Assert.Equal(1, server.Hits("/missing.bin"));
        Assert.True(m.MainQueue.IsRunning);

        // The retry fails again within milliseconds, so it is seen at the server rather than as running.
        _ui.Run(() => m.Tick(DateTime.Now.AddSeconds(31)));
        await Until(() => server.Hits("/missing.bin") == 2);
        await m.ShutdownAsync();
    }

    [Fact]
    public async Task Retry_limit_lets_the_queue_finish()
    {
        await using var server = await TestServer.StartAsync(16 * 1024, TimeSpan.Zero);
        var m = NewManager(TimeSpan.Zero);
        m.MainQueue.RetriesPerFile = 2;
        var failures = 0;
        m.DownloadFailed += _ => Interlocked.Increment(ref failures);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        m.QueueFinished += _ => finished.TrySetResult();
        var bad = Queue(m, server.MissingUrl, "bad.bin");
        var good = Queue(m, server.FileUrl, "good.bin");

        m.StartQueue(m.MainQueue);
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(DownloadStatus.Failed, bad.Status);
        Assert.Equal(DownloadStatus.Completed, good.Status);
        Assert.Equal(1, failures);
        Assert.Equal(3, server.Hits("/missing.bin")); // the first try and two retries
        Assert.False(m.MainQueue.IsRunning);
    }

    [Fact]
    public async Task Unlimited_retries_keep_the_queue_running()
    {
        await using var server = await TestServer.StartAsync(16 * 1024, TimeSpan.Zero);
        var m = NewManager(TimeSpan.Zero);
        m.MainQueue.LimitRetries = false;
        m.MainQueue.RetriesPerFile = 1;
        var failures = 0;
        m.DownloadFailed += _ => Interlocked.Increment(ref failures);
        var bad = Queue(m, server.MissingUrl, "bad.bin");

        m.StartQueue(m.MainQueue);
        await Until(() => server.Hits("/missing.bin") >= 4);

        Assert.True(m.MainQueue.IsRunning);
        Assert.NotEqual(DownloadStatus.Failed, bad.Status);
        Assert.Equal(0, failures);
        await m.ShutdownAsync();
    }

    [Fact]
    public async Task Queue_stopped_by_hand_does_not_report_finished()
    {
        await using var server = await TestServer.StartAsync(16 * 1024, TimeSpan.FromSeconds(5));
        var m = NewManager(TimeSpan.Zero);
        var finished = 0;
        m.QueueFinished += _ => Interlocked.Increment(ref finished);
        var item = Queue(m, server.FileUrl, "f.bin");

        m.StartQueue(m.MainQueue);
        m.StopQueue(m.MainQueue);
        await Until(() => !m.IsRunning(item));

        Assert.Equal(DownloadStatus.Queued, item.Status);
        Assert.Equal(0, finished);
    }
}
