using ADM.Core;

namespace ADM.Core.Tests;

public sealed class QueueTests : IDisposable
{
    // Port 9 (discard) refuses connections: a file started there fails, after the downloader's own retries.
    const string Unreachable = "http://127.0.0.1:9/";

    readonly string _dir = Path.Combine(Path.GetTempPath(), "adm-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    string StatePath => Path.Combine(_dir, "state.json");

    DownloadManager NewManager()
    {
        var m = new DownloadManager(StatePath, new SynchronizationContext());
        m.State.Settings.DownloadRoot = _dir;
        return m;
    }

    static DownloadItem Queue(DownloadManager m, string name, DownloadQueue? queue = null) =>
        m.Add(new DownloadRequest { Url = Unreachable + name, FileName = name }, startNow: false, queue);

    static DownloadQueue NewQueue(DownloadManager m, string name)
    {
        var queue = new DownloadQueue { Name = name };
        m.Queues.Add(queue);
        return queue;
    }

    static string Names(IEnumerable<DownloadItem> items) => string.Join(" ", items.Select(i => i.FileName));

    void WriteState(string downloads)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(StatePath, "{ \"Downloads\": [" + downloads + "] }");
    }

    sealed class StandInFeed : IQueueFeed
    {
        public required DownloadManager Manager;
        public required DownloadQueue Queue;
        public required Func<string> NextUrl;
        public int Files;
        public bool IsBusy = true;
        public int Starts, Stops, Shutdowns;
        public readonly List<int> Rooms = new();

        public void Starting() => Starts++;
        public void Stopping() => Stops++;
        public bool Busy => IsBusy;

        public void Fill(int room)
        {
            Rooms.Add(room);
            for (; room > 0 && Files > 0; room--, Files--)
                Manager.AddFromFeed(new DownloadRequest { Url = NextUrl(), FileName = $"{Files}.bin" }, Queue);
        }

        public Task ShutdownAsync()
        {
            Shutdowns++;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Feed_is_asked_while_there_is_room()
    {
        await using var server = await TestServer.StartAsync(16 * 1024);
        var ui = new LockedContext();
        var m = new DownloadManager(StatePath, ui);
        m.State.Settings.DownloadRoot = _dir;
        var queue = NewQueue(m, "fed");
        queue.MaxConcurrent = 2;
        var feed = new StandInFeed { Manager = m, Queue = queue, NextUrl = () => server.FileUrl, Files = 5 };
        m.SetFeed(queue, feed);
        int most = 0;
        m.DownloadCompleted += item => m.Items.Remove(item);
        ui.Run(() => m.StartQueue(queue));
        Assert.Equal(1, feed.Starts);
        Assert.Equal(new[] { 2 }, feed.Rooms.Take(1));
        Assert.Equal(2, m.Items.Count);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (true)
        {
            bool done = false;
            ui.Run(() =>
            {
                most = Math.Max(most, m.Items.Count);
                done = feed.Files == 0 && m.Items.Count == 0;
            });
            if (done) break;
            Assert.True(DateTime.UtcNow < deadline);
            await Task.Delay(10);
        }
        Assert.Equal(2, most);
        Assert.True(queue.IsRunning, "a busy feed keeps its queue running");
        await m.ShutdownAsync();
    }

    [Fact]
    public void Busy_feed_keeps_the_queue_running()
    {
        var m = NewManager();
        var queue = NewQueue(m, "fed");
        var feed = new StandInFeed { Manager = m, Queue = queue, NextUrl = () => Unreachable };
        m.SetFeed(queue, feed);
        m.StartQueue(queue);
        Assert.True(queue.IsRunning);
        m.Nudge();
        Assert.True(queue.IsRunning);
        feed.IsBusy = false;
        m.Nudge();
        Assert.False(queue.IsRunning);
    }

    [Fact]
    public void Feed_finishes_its_queue_once()
    {
        var m = NewManager();
        var queue = NewQueue(m, "fed");
        var feed = new StandInFeed { Manager = m, Queue = queue, NextUrl = () => Unreachable };
        m.SetFeed(queue, feed);
        int finished = 0;
        m.QueueFinished += _ => finished++;
        m.StartQueue(queue);
        Assert.Equal(0, finished);
        feed.IsBusy = false;
        m.Nudge();
        m.Nudge();
        Assert.Equal(1, finished);
    }

    [Fact]
    public void Feed_with_nothing_to_do_reports_no_finish()
    {
        var m = NewManager();
        var queue = NewQueue(m, "fed");
        var feed = new StandInFeed { Manager = m, Queue = queue, NextUrl = () => Unreachable, IsBusy = false };
        m.SetFeed(queue, feed);
        int finished = 0;
        m.QueueFinished += _ => finished++;
        m.StartQueue(queue);
        Assert.False(queue.IsRunning);
        Assert.Equal(0, finished);
    }

    [Fact]
    public void Stopping_stops_the_feed()
    {
        var m = NewManager();
        var queue = NewQueue(m, "fed");
        var feed = new StandInFeed { Manager = m, Queue = queue, NextUrl = () => Unreachable };
        m.SetFeed(queue, feed);
        m.StopQueue(queue);
        Assert.Equal(0, feed.Stops);
        m.StartQueue(queue);
        m.StopQueue(queue);
        Assert.Equal(1, feed.Stops);
        Assert.False(queue.IsRunning);
    }

    [Fact]
    public void A_running_queue_started_again_does_not_start_its_feed_again()
    {
        var m = NewManager();
        var queue = NewQueue(m, "fed");
        var feed = new StandInFeed { Manager = m, Queue = queue, NextUrl = () => Unreachable };
        m.SetFeed(queue, feed);
        m.StartQueue(queue);
        m.StartQueue(queue);
        Assert.Equal(1, feed.Starts);
    }

    [Fact]
    public async Task Closing_shuts_the_feed_down()
    {
        var m = NewManager();
        var queue = NewQueue(m, "fed");
        var feed = new StandInFeed { Manager = m, Queue = queue, NextUrl = () => Unreachable };
        m.SetFeed(queue, feed);
        await m.ShutdownAsync();
        Assert.Equal(1, feed.Shutdowns);
    }

    [Fact]
    public void Queue_without_a_feed_is_unchanged()
    {
        var m = NewManager();
        var queue = NewQueue(m, "plain");
        var other = NewQueue(m, "fed");
        var feed = new StandInFeed { Manager = m, Queue = other, NextUrl = () => Unreachable };
        m.SetFeed(other, feed);
        m.StartQueue(queue);
        Assert.False(queue.IsRunning);
        Assert.Equal(0, feed.Starts);
        Assert.Empty(feed.Rooms);
    }

    [Fact]
    public async Task Feed_file_is_its_own_while_the_same_address_runs_in_the_list()
    {
        await using var server = await TestServer.StartAsync(16 * 1024, TimeSpan.FromSeconds(5));
        var ui = new LockedContext();
        var m = new DownloadManager(StatePath, ui);
        m.State.Settings.DownloadRoot = _dir;
        var queue = NewQueue(m, "fed");
        ui.Run(() =>
        {
            var mine = m.Add(new DownloadRequest { Url = server.FileUrl, FileName = "mine.bin" }, startNow: true);
            var fed = m.AddFromFeed(new DownloadRequest { Url = server.FileUrl, FileName = "fed.bin", GrabberProjectId = "p" }, queue);
            Assert.NotSame(mine, fed);
            Assert.Equal(("fed.bin", "p"), (fed.FileName, fed.GrabberProjectId));
        });
        await m.ShutdownAsync();
    }

    [Fact]
    public void A_project_file_is_no_duplicate_for_the_user()
    {
        var m = NewManager();
        var queue = NewQueue(m, "fed");
        m.AddFromFeed(new DownloadRequest { Url = Unreachable + "same", FileName = "fed.bin", GrabberProjectId = "p" }, queue);
        Assert.Null(m.FindDuplicate(Unreachable + "same"));
        var mine = m.Add(new DownloadRequest { Url = Unreachable + "same", FileName = "mine.bin" }, startNow: false, queue: m.MainQueue, whenDuplicate: DuplicateAction.Overwrite);
        Assert.Equal(2, m.Items.Count);
        Assert.Same(mine, m.FindDuplicate(Unreachable + "same"));
    }

    [Fact]
    public void Feed_file_keeps_its_name_and_never_replaces_another()
    {
        var m = NewManager();
        var queue = NewQueue(m, "fed");
        m.State.Settings.OnDuplicate = DuplicateAction.Overwrite;
        var existing = m.Add(new DownloadRequest { Url = Unreachable + "same", FileName = "first.bin" }, startNow: false, queue: m.MainQueue);
        var item = m.AddFromFeed(new DownloadRequest { Url = Unreachable + "same", FileName = "second.bin", GrabberProjectId = "p", OverwritePath = "x" }, queue);
        Assert.Contains(existing, m.Items);
        Assert.Equal(("second.bin", "p", "x", queue.Id), (item.FileName, item.GrabberProjectId, item.OverwritePath, item.QueueId));
    }

    [Fact]
    public async Task A_queue_reports_its_start_once_per_run()
    {
        // Each request is held, so the queue is still running when it is started again and stopped.
        await using var server = await TestServer.StartAsync(16 * 1024, TimeSpan.FromSeconds(5));
        var ui = new LockedContext();
        var m = new DownloadManager(StatePath, ui);
        m.State.Settings.DownloadRoot = _dir;
        var started = new List<string>();
        m.QueueStarted += queue => started.Add(queue.Id);
        m.Add(new DownloadRequest { Url = server.FileUrl, FileName = "a.bin" }, startNow: false);

        ui.Run(() =>
        {
            m.StartQueue(m.MainQueue);
            m.StartQueue(m.MainQueue);
        });
        Assert.Equal(new[] { DownloadQueue.MainId }, started);

        ui.Run(() =>
        {
            m.StopQueue(m.MainQueue);
            m.StartQueue(m.MainQueue);
        });
        Assert.Equal(new[] { DownloadQueue.MainId, DownloadQueue.MainId }, started);

        await m.ShutdownAsync();
    }

    [Fact]
    public void An_empty_queue_reports_no_start()
    {
        var m = NewManager();
        var started = 0;
        m.QueueStarted += _ => started++;

        m.StartQueue(m.MainQueue);

        Assert.Equal(0, started);
        Assert.False(m.MainQueue.IsRunning);
    }

    [Fact]
    public void Queue_serves_files_oldest_first_and_numbers_them()
    {
        var m = NewManager();
        var a = Queue(m, "a");
        var b = Queue(m, "b");
        var c = Queue(m, "c");

        Assert.Equal("a b c", Names(m.QueueItems(m.MainQueue)));
        Assert.Equal(new[] { 1, 2, 3 }, new[] { a, b, c }.Select(i => i.QueuePosition));
    }

    [Theory]
    [InlineData(QueueMove.Top, "c", "c a b d e")]
    [InlineData(QueueMove.Up, "c", "a c b d e")]
    [InlineData(QueueMove.Down, "c", "a b d c e")]
    [InlineData(QueueMove.Bottom, "c", "a b d e c")]
    [InlineData(QueueMove.Up, "a", "a b c d e")]
    [InlineData(QueueMove.Down, "e", "a b c d e")]
    [InlineData(QueueMove.Up, "b d", "b a d c e")]
    [InlineData(QueueMove.Down, "b d", "a c b e d")]
    [InlineData(QueueMove.Top, "d b", "b d a c e")]
    [InlineData(QueueMove.Bottom, "b d", "a c e b d")]
    [InlineData(QueueMove.Up, "a c", "a c b d e")]
    [InlineData(QueueMove.Down, "d e", "a b c d e")]
    public void MoveInQueue_keeps_the_selection_in_its_own_order(QueueMove move, string selected, string expected)
    {
        var m = NewManager();
        var items = "a b c d e".Split(' ').ToDictionary(n => n, n => Queue(m, n));

        m.MoveInQueue(selected.Split(' ').Select(n => items[n]).ToList(), move);

        var order = m.QueueItems(m.MainQueue);
        Assert.Equal(expected, Names(order));
        Assert.Equal(Enumerable.Range(1, 5), order.Select(i => i.QueuePosition));
    }

    [Fact]
    public void Old_state_is_numbered_oldest_first()
    {
        // Saved newest first and without QueueOrder, as before queues had an order.
        WriteState("""
            { "Url": "http://127.0.0.1:9/c", "FileName": "c", "QueueId": "main", "Status": "Queued" },
            { "Url": "http://127.0.0.1:9/b", "FileName": "b", "QueueId": "main", "Status": "Queued" },
            { "Url": "http://127.0.0.1:9/a", "FileName": "a", "QueueId": "main", "Status": "Queued" }
            """);
        var m = NewManager();

        Assert.Equal("a b c", Names(m.QueueItems(m.MainQueue)));
        Assert.Equal(3, m.Items.Single(i => i.FileName == "c").QueuePosition);
    }

    [Fact]
    public void Running_files_come_back_queued_in_a_queue_and_paused_outside_one()
    {
        WriteState("""
            { "Url": "http://127.0.0.1:9/q", "FileName": "q", "QueueId": "main", "Status": "Downloading" },
            { "Url": "http://127.0.0.1:9/n", "FileName": "n", "Status": "Downloading" },
            { "Url": "http://127.0.0.1:9/g", "FileName": "g", "QueueId": "gone", "Status": "Queued" }
            """);
        var m = NewManager();

        Assert.Equal(DownloadStatus.Queued, m.Items.Single(i => i.FileName == "q").Status);
        Assert.Equal(DownloadStatus.Paused, m.Items.Single(i => i.FileName == "n").Status);
        // Its queue was deleted: it leaves the queue and stops waiting for it.
        var orphan = m.Items.Single(i => i.FileName == "g");
        Assert.Null(orphan.QueueId);
        Assert.Equal(DownloadStatus.Paused, orphan.Status);
    }

    [Fact]
    public async Task Exit_leaves_queue_files_queued_and_others_paused()
    {
        await using var server = await TestServer.StartAsync(16 * 1024, TimeSpan.FromSeconds(20));
        var m = NewManager();
        var queued = m.Add(new DownloadRequest { Url = server.FileUrl, FileName = "q.bin" }, startNow: false);
        var manual = m.Add(new DownloadRequest { Url = server.FileUrl + "?n", FileName = "n.bin" }, startNow: true);
        m.StartQueue(m.MainQueue);
        Assert.True(m.IsRunning(queued));

        await m.ShutdownAsync();

        Assert.Equal(DownloadStatus.Queued, queued.Status);
        Assert.Equal(DownloadStatus.Paused, manual.Status);
    }

    [Fact]
    public async Task Starting_a_queue_leaves_paused_files_paused()
    {
        var m = NewManager();
        var a = Queue(m, "a");
        var b = Queue(m, "b");
        m.Pause(b);

        m.StartQueue(m.MainQueue);

        Assert.True(m.IsRunning(a));
        Assert.False(m.IsRunning(b));
        Assert.Equal(DownloadStatus.Paused, b.Status);
        await m.ShutdownAsync();
    }

    [Fact]
    public async Task Starting_a_queue_tries_its_failed_files_again()
    {
        var m = NewManager();
        var a = Queue(m, "a");
        a.Status = DownloadStatus.Failed;

        m.StartQueue(m.MainQueue);

        Assert.True(m.IsRunning(a));
        await m.ShutdownAsync();
    }

    [Fact]
    public async Task Completed_file_leaves_its_download_queue()
    {
        await using var server = await TestServer.StartAsync(16 * 1024, TimeSpan.Zero);
        var m = NewManager();
        var finished = new TaskCompletionSource<DownloadQueue>(TaskCreationOptions.RunContinuationsAsynchronously);
        m.QueueFinished += q => finished.TrySetResult(q);
        var item = m.Add(new DownloadRequest { Url = server.FileUrl }, startNow: false);

        m.StartQueue(m.MainQueue);

        Assert.Same(m.MainQueue, await finished.Task.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal(DownloadStatus.Completed, item.Status);
        Assert.Null(item.QueueId);
        Assert.Equal(0, item.QueuePosition);
    }

    [Fact]
    public void MoveToQueue_puts_the_file_last_and_queues_it_again()
    {
        var m = NewManager();
        var other = NewQueue(m, "Other");
        Queue(m, "a", other);
        var b = Queue(m, "b");
        m.Pause(b);

        Assert.True(m.MoveToQueue(b, other));

        Assert.Equal(other.Id, b.QueueId);
        Assert.Equal(DownloadStatus.Queued, b.Status);
        Assert.Equal("a b", Names(m.QueueItems(other)));
        Assert.Equal(2, b.QueuePosition);
        Assert.Empty(m.QueueItems(m.MainQueue));
    }

    [Fact]
    public void MoveToQueue_takes_a_failed_file_outside_any_queue()
    {
        var m = NewManager();
        var item = m.Add(new DownloadRequest { Url = Unreachable + "f", FileName = "f" }, startNow: false);
        m.RemoveFromQueue(item);
        item.Status = DownloadStatus.Failed;

        Assert.True(m.MoveToQueue(item, m.MainQueue));

        Assert.Equal(DownloadStatus.Queued, item.Status);
        Assert.Equal(1, item.QueuePosition);
    }

    [Fact]
    public void RemoveFromQueue_pauses_a_waiting_file_and_renumbers_the_rest()
    {
        var m = NewManager();
        var a = Queue(m, "a");
        var b = Queue(m, "b");

        m.RemoveFromQueue(a);

        Assert.Null(a.QueueId);
        Assert.Equal(DownloadStatus.Paused, a.Status);
        Assert.Equal(0, a.QueuePosition);
        Assert.Equal(1, b.QueuePosition);
    }

    [Fact]
    public void DeleteQueue_moves_its_files_to_the_end_of_the_main_queue()
    {
        var m = NewManager();
        var other = NewQueue(m, "Other");
        Queue(m, "a");
        Queue(m, "b", other);
        Queue(m, "c", other);

        Assert.True(m.DeleteQueue(other));

        Assert.DoesNotContain(other, m.Queues);
        Assert.Equal("a b c", Names(m.QueueItems(m.MainQueue)));
        Assert.False(m.DeleteQueue(m.MainQueue));
        Assert.Contains(m.MainQueue, m.Queues);
    }

    [Fact]
    public async Task StartQueuesOnLaunch_starts_only_the_marked_queues()
    {
        var m = NewManager();
        var other = NewQueue(m, "Other");
        other.StartOnLaunch = true;
        var a = Queue(m, "a");
        var b = Queue(m, "b", other);

        m.StartQueuesOnLaunch();

        Assert.True(other.IsRunning);
        Assert.False(m.MainQueue.IsRunning);
        Assert.True(m.IsRunning(b));
        Assert.False(m.IsRunning(a));
        await m.ShutdownAsync();
    }
}
