using System.Text;
using ADM.Core;

namespace ADM.Core.Tests;

/// <summary>The state saved off the UI thread: the newest save lands, none older after it, and a failure is tried again.</summary>
[Collection(nameof(SpeedTests))]
public sealed class StateWriterTests : IDisposable
{
    static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    readonly string _dir = Path.Combine(Path.GetTempPath(), "adm-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    string StatePath => Path.Combine(_dir, "state.json");

    static byte[] B(string text) => Encoding.UTF8.GetBytes(text);

    /// <summary>A disk that holds up its first write until let go, and keeps what it was given.</summary>
    sealed class SlowDisk
    {
        public readonly List<string> Written = new();
        public readonly ManualResetEventSlim Writing = new();
        public readonly ManualResetEventSlim Release = new();
        int _calls;

        public void Write(byte[] bytes)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                Writing.Set();
                Assert.True(Release.Wait(Wait));
            }
            lock (Written) Written.Add(Encoding.UTF8.GetString(bytes));
        }
    }

    [Fact]
    public async Task Post_returns_at_once_and_only_the_newest_waiting_save_lands()
    {
        var disk = new SlowDisk();
        var writer = new StateWriter(disk.Write);

        writer.Post(B("1"));
        Assert.True(disk.Writing.Wait(Wait));
        // The disk is busy: these wait, and each newer one takes the place of the one before.
        writer.Post(B("2"));
        writer.Post(B("3"));
        disk.Release.Set();
        await writer.Idle.WaitAsync(Wait);

        Assert.Equal(new[] { "1", "3" }, disk.Written);
    }

    [Fact]
    public async Task A_save_that_waits_for_the_disk_is_not_overtaken_by_an_older_one()
    {
        var disk = new SlowDisk();
        var writer = new StateWriter(disk.Write);

        writer.Post(B("1"));
        Assert.True(disk.Writing.Wait(Wait));
        writer.Post(B("2"));
        // As at exit: the final save waits for the write under way, and what was waiting is older than it.
        var exit = Task.Run(() => writer.Write(B("final")));
        await Task.Delay(50);
        Assert.False(exit.IsCompleted);
        disk.Release.Set();
        await exit.WaitAsync(Wait);
        await writer.Idle.WaitAsync(Wait);

        Assert.Equal(new[] { "1", "final" }, disk.Written);
    }

    [Fact]
    public async Task A_failed_background_write_is_reported_once_and_the_next_save_lands()
    {
        var calls = 0;
        var written = new List<string>();
        var writer = new StateWriter(bytes =>
        {
            if (++calls == 1) throw new IOException("The disk is full.");
            written.Add(Encoding.UTF8.GetString(bytes));
        });

        writer.Post(B("1"));
        await writer.Idle.WaitAsync(Wait);
        Assert.True(writer.TakeFailure());
        Assert.False(writer.TakeFailure());

        writer.Post(B("2"));
        await writer.Idle.WaitAsync(Wait);
        Assert.False(writer.TakeFailure());
        Assert.Equal(new[] { "2" }, written);
    }

    [Fact]
    public void A_save_that_waits_throws_when_it_fails()
    {
        var writer = new StateWriter(_ => throw new IOException("The disk is full."));

        Assert.Throws<IOException>(() => writer.Write(B("1")));
    }

    [Fact]
    public async Task The_tick_saves_without_waiting_and_the_file_follows()
    {
        var m = new DownloadManager(StatePath, new SynchronizationContext());
        m.State.Settings.DownloadRoot = _dir;
        m.Add(new DownloadRequest { Url = "http://127.0.0.1:9/f.zip" }, startNow: false);
        m.MarkDirty();

        m.Tick(DateTime.Now);
        await m.SavesWritten.WaitAsync(Wait);

        Assert.Equal("http://127.0.0.1:9/f.zip", Assert.Single(Store.Load(StatePath).Downloads).Url);
    }

    [Fact]
    public async Task A_failed_tick_save_is_tried_again_a_while_later()
    {
        var m = new DownloadManager(StatePath, new SynchronizationContext());
        m.State.Settings.DownloadRoot = _dir;
        m.Add(new DownloadRequest { Url = "http://127.0.0.1:9/f.zip" }, startNow: false);
        m.MarkDirty();
        // Something else has the temporary file open, as a virus scanner can.
        Directory.CreateDirectory(_dir);
        using (new FileStream(StatePath + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
        {
            m.Tick(DateTime.Now);
            await m.SavesWritten.WaitAsync(Wait);
        }
        Assert.False(File.Exists(StatePath));

        // The next tick notes the failure; nothing is running, so only the retry saves.
        var at = DateTime.Now;
        m.Tick(at);
        await m.SavesWritten.WaitAsync(Wait);
        Assert.False(File.Exists(StatePath));
        m.Tick(at.AddSeconds(6));
        await m.SavesWritten.WaitAsync(Wait);

        Assert.Single(Store.Load(StatePath).Downloads);
    }

    [Fact]
    public void The_state_file_is_not_indented()
    {
        var state = new AppState();
        state.Downloads.Add(new DownloadItem { Url = "http://a.example/1.zip" });

        Store.Save(StatePath, state);

        Assert.DoesNotContain("\n", File.ReadAllText(StatePath));
        Assert.Equal("http://a.example/1.zip", Assert.Single(Store.Load(StatePath).Downloads).Url);
    }
}
