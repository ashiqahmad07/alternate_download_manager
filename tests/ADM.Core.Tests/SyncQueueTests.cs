using ADM.Core;

namespace ADM.Core.Tests;

public sealed class SyncQueueTests : IDisposable
{
    // A Monday, so the default schedule (every day) applies.
    static readonly DateTime Day = new(2026, 11, 2);

    readonly string _dir = Path.Combine(Path.GetTempPath(), "adm-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    // State is kept apart from the downloads, so a folder holds only downloaded files.
    string StatePath => Path.Combine(_dir, "state", "state.json");

    DownloadManager NewManager()
    {
        var m = new DownloadManager(StatePath, new SynchronizationContext());
        m.State.Settings.DownloadRoot = Path.Combine(_dir, "files");
        return m;
    }

    static async Task RunQueue(DownloadManager m, DownloadQueue queue)
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnFinished(DownloadQueue q) { if (q == queue) finished.TrySetResult(); }
        void OnFailed(DownloadItem i) => finished.TrySetException(new Exception(i.Error));
        m.QueueFinished += OnFinished;
        m.DownloadFailed += OnFailed;
        try
        {
            m.StartQueue(queue);
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            m.QueueFinished -= OnFinished;
            m.DownloadFailed -= OnFailed;
        }
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

    [Fact]
    public void Old_state_gains_the_main_sync_queue()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        File.WriteAllText(StatePath, """{ "Queues": [ { "Id": "main", "Name": "Main queue" } ] }""");

        var queues = Store.Load(StatePath).Queues;

        Assert.Equal(new[] { DownloadQueue.MainId, DownloadQueue.SyncId }, queues.Select(q => q.Id));
        Assert.Equal(QueueKind.Sync, queues[1].Kind);
    }

    [Fact]
    public async Task Changed_file_is_downloaded_again_in_place()
    {
        await using var server = await TestServer.StartAsync(64 * 1024, TimeSpan.Zero);
        var m = NewManager();
        var item = m.Add(new DownloadRequest { Url = server.SyncUrl }, startNow: false, m.SyncQueue);
        await RunQueue(m, m.SyncQueue);
        Assert.Equal(server.SyncPayload(0), File.ReadAllBytes(item.FullPath));
        Assert.Equal(DownloadQueue.SyncId, item.QueueId);

        server.SyncVersion = 1;
        await RunQueue(m, m.SyncQueue);

        Assert.Equal(DownloadStatus.Completed, item.Status);
        Assert.Equal(DownloadQueue.SyncId, item.QueueId);
        Assert.Equal(server.SyncPayload(1), File.ReadAllBytes(item.FullPath));
        Assert.Single(Directory.GetFiles(item.Directory));
    }

    [Fact]
    public async Task Unchanged_file_is_left_alone()
    {
        await using var server = await TestServer.StartAsync(64 * 1024, TimeSpan.Zero);
        var m = NewManager();
        var item = m.Add(new DownloadRequest { Url = server.SyncUrl }, startNow: false, m.SyncQueue);
        await RunQueue(m, m.SyncQueue);
        var completedAt = item.CompletedAt;
        var requests = server.Hits("/sync.bin");

        await RunQueue(m, m.SyncQueue);

        Assert.Equal(requests + 1, server.Hits("/sync.bin")); // the check, and nothing more
        Assert.Equal(completedAt, item.CompletedAt);
        Assert.NotNull(item.LastCheckedAt);
        Assert.Equal(server.SyncPayload(0), File.ReadAllBytes(item.FullPath));
    }

    [Fact]
    public async Task Checking_keeps_the_file_complete()
    {
        // Every request is held, so the check can be seen while it waits for the server.
        await using var server = await TestServer.StartAsync(16 * 1024, TimeSpan.FromSeconds(1));
        var m = NewManager();
        var item = m.Add(new DownloadRequest { Url = server.SyncUrl }, startNow: false, m.SyncQueue);
        await RunQueue(m, m.SyncQueue);

        m.StartQueue(m.SyncQueue);

        Assert.True(m.IsRunning(item));
        Assert.True(item.IsChecking);
        Assert.Equal(DownloadStatus.Completed, item.Status);
        Assert.Equal("Checking for changes", item.StatusText);
        // Saved mid-check, as an exit or a crash would leave it.
        m.Save();
        Assert.Equal(DownloadStatus.Completed, new DownloadManager(StatePath, new SynchronizationContext()).Items.Single().Status);

        m.Pause(item);
        // The manager's context here is the thread pool, so the check is seen leaving the running list before
        // it is marked done; in the app both happen on the UI thread.
        await Until(() => !m.IsRunning(item) && !item.IsChecking);
        Assert.Equal(DownloadStatus.Completed, item.Status);
    }

    [Fact]
    public void Complete_files_only_go_into_sync_queues()
    {
        var m = NewManager();
        var item = m.Add(new DownloadRequest { Url = "http://127.0.0.1:9/f", FileName = "f" }, startNow: false);
        m.RemoveFromQueue(item);
        item.Status = DownloadStatus.Completed;

        Assert.False(m.MoveToQueue(item, m.MainQueue));
        Assert.Null(item.QueueId);

        Assert.True(m.MoveToQueue(item, m.SyncQueue));
        Assert.Equal(DownloadQueue.SyncId, item.QueueId);
        Assert.Equal(DownloadStatus.Completed, item.Status);
    }

    [Fact]
    public void A_sync_queue_turned_into_a_download_queue_lets_go_of_complete_files()
    {
        var m = NewManager();
        var extra = new DownloadQueue { Name = "Mirrors", Kind = QueueKind.Sync };
        m.Queues.Add(extra);
        var done = m.Add(new DownloadRequest { Url = "http://127.0.0.1:9/d", FileName = "d" }, startNow: false, extra);
        var waiting = m.Add(new DownloadRequest { Url = "http://127.0.0.1:9/w", FileName = "w" }, startNow: false, extra);
        done.Status = DownloadStatus.Completed;

        m.SetKind(extra, QueueKind.Download);
        m.SetKind(m.SyncQueue, QueueKind.Download);

        Assert.Null(done.QueueId);
        Assert.Equal(1, waiting.QueuePosition);
        Assert.Equal(QueueKind.Sync, m.SyncQueue.Kind); // the main queues keep their kind
    }

    [Fact]
    public void Deleting_a_sync_queue_moves_its_files_to_the_main_sync_queue()
    {
        var m = NewManager();
        var extra = new DownloadQueue { Name = "Mirrors", Kind = QueueKind.Sync };
        m.Queues.Add(extra);
        var item = m.Add(new DownloadRequest { Url = "http://127.0.0.1:9/f", FileName = "f" }, startNow: false, extra);

        Assert.True(m.DeleteQueue(extra));
        Assert.False(m.DeleteQueue(m.SyncQueue));

        Assert.Equal(DownloadQueue.SyncId, item.QueueId);
    }

    [Fact]
    public void Sync_queue_repeats_until_the_stop_time()
    {
        var m = NewManager();
        foreach (var queue in new[] { m.SyncQueue, m.MainQueue })
        {
            var s = queue.Schedule;
            s.Enabled = true;
            s.StartTime = TimeSpan.FromHours(10);
            s.StopTime = TimeSpan.FromHours(12);
            s.RepeatMinutes = 30;
        }
        DateTime? LastStart(DownloadQueue q) => q.Schedule.LastStartedAt;

        m.Tick(Day.AddHours(10).AddSeconds(30));
        Assert.Equal(Day.AddHours(10).AddSeconds(30), LastStart(m.SyncQueue));
        Assert.Equal(Day.AddHours(10).AddSeconds(30), LastStart(m.MainQueue));

        m.Tick(Day.AddHours(10).AddMinutes(20));
        Assert.Equal(Day.AddHours(10).AddSeconds(30), LastStart(m.SyncQueue));

        m.Tick(Day.AddHours(10).AddMinutes(31));
        Assert.Equal(Day.AddHours(10).AddMinutes(31), LastStart(m.SyncQueue));
        Assert.Equal(Day.AddHours(10).AddSeconds(30), LastStart(m.MainQueue)); // download queues run once

        m.Tick(Day.AddHours(11).AddMinutes(59));
        Assert.Equal(Day.AddHours(11).AddMinutes(59), LastStart(m.SyncQueue));

        m.Tick(Day.AddHours(12).AddMinutes(40));
        Assert.Equal(Day.AddHours(11).AddMinutes(59), LastStart(m.SyncQueue));
    }

    [Fact]
    public async Task Stop_time_applies_without_a_scheduled_start()
    {
        await using var server = await TestServer.StartAsync(16 * 1024, TimeSpan.FromSeconds(5));
        var m = NewManager();
        m.MainQueue.Schedule.StopTime = TimeSpan.FromHours(10);
        var item = m.Add(new DownloadRequest { Url = server.FileUrl }, startNow: false);
        m.StartQueue(m.MainQueue);
        Assert.True(m.IsRunning(item));

        m.Tick(Day.AddHours(10).AddSeconds(30));

        Assert.False(m.MainQueue.IsRunning);
        await Until(() => !m.IsRunning(item));
        Assert.Equal(DownloadStatus.Queued, item.Status);
    }
}
