using ADM.Core;

namespace ADM.Core.Tests;

[Collection(nameof(SpeedTests))]
public sealed class QuotaTests : IDisposable
{
    const long MB = 1024 * 1024;
    static readonly DateTime Nine = new(2026, 11, 2, 9, 0, 0);

    readonly string _dir = Path.Combine(Path.GetTempPath(), "adm-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    string StatePath => Path.Combine(_dir, "state", "state.json");

    readonly LockedContext _ui = new();

    DownloadManager NewManager()
    {
        var m = new DownloadManager(StatePath, _ui);
        var s = m.State.Settings;
        s.DownloadRoot = Path.Combine(_dir, "files");
        s.QuotaEnabled = true;
        s.QuotaMB = 1;
        s.QuotaHours = 1;
        return m;
    }

    static async Task Until(Func<bool> condition, Action? meanwhile = null)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException();
            meanwhile?.Invoke();
            await Task.Delay(50);
        }
    }

    [Fact]
    public void Period_starts_with_the_first_bytes_and_ends_after_its_hours()
    {
        var usage = new QuotaUsage();

        usage.Add(0, Nine, 4);
        Assert.Null(usage.PeriodStart);

        usage.Add(100, Nine.AddMinutes(5), 4);
        usage.Add(50, Nine.AddHours(4), 4);
        Assert.Equal(Nine.AddMinutes(5), usage.PeriodStart);
        Assert.Equal(150, usage.Bytes);
        Assert.Equal(Nine.AddHours(4).AddMinutes(5), usage.ResumesAt(4));

        usage.Add(10, Nine.AddHours(4).AddMinutes(5), 4);
        Assert.Equal(Nine.AddHours(4).AddMinutes(5), usage.PeriodStart);
        Assert.Equal(10, usage.Bytes);
    }

    [Fact]
    public void Limit_is_exceeded_only_when_on_and_reached()
    {
        var settings = new Settings { QuotaMB = 1 };
        var usage = new QuotaUsage { PeriodStart = Nine, Bytes = MB };

        Assert.False(usage.Exceeded(settings));
        settings.QuotaEnabled = true;
        Assert.True(usage.Exceeded(settings));
        usage.Bytes = MB - 1;
        Assert.False(usage.Exceeded(settings));
    }

    [Fact]
    public async Task Limit_holds_downloads_until_the_period_ends()
    {
        await using var server = await TestServer.StartAsync((int)(2 * MB), TimeSpan.Zero);
        var m = NewManager();
        DateTime? resumesAt = null;
        m.QuotaReached += at => resumesAt = at;
        var completed = 0;
        m.DownloadCompleted += _ => Interlocked.Increment(ref completed);

        var manual = m.Add(new DownloadRequest { Url = server.FileUrl }, startNow: false);
        var queued = m.Add(new DownloadRequest { Url = server.FileUrl + "?q" }, startNow: false);
        m.RemoveFromQueue(manual);
        // Together 512 KB/s: the 1 MB limit is reached after about two seconds, well before either file ends.
        manual.SpeedLimitKBps = 256;
        queued.SpeedLimitKBps = 256;
        m.Start(manual);
        m.StartQueue(m.MainQueue);

        await Until(() => resumesAt != null, () => _ui.Run(() => m.Tick(DateTime.Now)));
        await Until(() => !m.IsRunning(manual) && !m.IsRunning(queued));

        Assert.Equal(DownloadStatus.Paused, manual.Status);
        Assert.Contains("download limit", manual.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(DownloadStatus.Queued, queued.Status);
        Assert.True(m.MainQueue.IsRunning);
        Assert.Equal(0, completed);

        // Neither a resume by hand nor the queue starts anything while the limit holds.
        _ui.Run(() =>
        {
            m.Start(manual);
            m.Tick(DateTime.Now);
        });
        Assert.False(m.IsRunning(manual));
        Assert.False(m.IsRunning(queued));

        // Once the period is over, both carry on from where they stopped. No more ticks after this,
        // so the rest is not counted against the test's real-time clock.
        _ui.Run(() =>
        {
            m.Tick(resumesAt!.Value.AddSeconds(1));
            Assert.True(m.IsRunning(manual));
            Assert.True(m.IsRunning(queued));
            manual.SpeedLimitKBps = 0;
            queued.SpeedLimitKBps = 0;
        });
        await Until(() => Volatile.Read(ref completed) == 2);
        Assert.Equal(server.Payload, File.ReadAllBytes(manual.FullPath));
        Assert.Equal(server.Payload, File.ReadAllBytes(queued.FullPath));
    }

    [Fact]
    public async Task Held_files_wait_but_a_paused_recording_still_saves()
    {
        var m = NewManager();
        m.State.Quota.Add(2 * MB, DateTime.Now, 1);
        DateTime? resumesAt = null;
        m.QuotaReached += at => resumesAt = at;
        m.Tick(DateTime.Now);
        Assert.NotNull(resumesAt);

        var file = m.Add(new DownloadRequest { Url = "http://127.0.0.1:9/f.zip" }, startNow: false);
        m.RemoveFromQueue(file);
        m.Start(file);
        Assert.False(m.IsRunning(file));

        // Saving joins parts already on disk and needs no network, so the limit doesn't apply.
        var recording = m.Add(new DownloadRequest { Url = "http://127.0.0.1:9/live.m3u8", Kind = DownloadKind.Hls }, startNow: false);
        m.RemoveFromQueue(recording);
        recording.IsLive = true;
        m.StopAndSave(recording);
        Assert.True(m.IsRunning(recording));
        await m.ShutdownAsync();
    }

    [Fact]
    public void Usage_is_kept_across_a_restart()
    {
        var m = NewManager();
        m.State.Quota.Add(123, Nine, 1);
        m.Save();

        var quota = new DownloadManager(StatePath, new SynchronizationContext()).State.Quota;

        Assert.Equal(Nine, quota.PeriodStart);
        Assert.Equal(123, quota.Bytes);
    }
}
