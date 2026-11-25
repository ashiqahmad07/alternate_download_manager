using ADM.Core;

namespace ADM.Core.Tests;

public sealed class RefreshAddressTests : IDisposable
{
    // The expired address; port 9 is never contacted.
    const string OldUrl = "http://127.0.0.1:9/file.bin";

    readonly string _dir = Path.Combine(Path.GetTempPath(), "adm-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    DownloadManager NewManager()
    {
        var m = new DownloadManager(Path.Combine(_dir, "state.json"), new SynchronizationContext());
        m.State.Settings.DownloadRoot = _dir;
        return m;
    }

    static DownloadItem AddWaiting(DownloadManager m)
    {
        var item = m.Add(new DownloadRequest { Url = OldUrl, FileName = "file.bin" }, startNow: false);
        m.BeginRefresh(item);
        return item;
    }

    [Fact]
    public void Matching_capture_is_the_refresh_target()
    {
        var m = NewManager();
        var item = AddWaiting(m);

        Assert.Same(item, m.AwaitingNewAddress);
        Assert.Same(item, m.FindRefreshTarget(new DownloadRequest { Url = "https://mirror.example/dl/FILE.BIN?token=1" }, DateTime.Now));
        Assert.Same(item, m.FindRefreshTarget(new DownloadRequest { Url = "https://mirror.example/get?id=7", FileName = "file.bin" }, DateTime.Now));
    }

    [Fact]
    public void Unrelated_capture_is_not_a_refresh_candidate()
    {
        var m = NewManager();
        AddWaiting(m);

        Assert.Null(m.FindRefreshTarget(new DownloadRequest { Url = "https://mirror.example/other.zip" }, DateTime.Now));
        Assert.Null(m.FindRefreshTarget(new DownloadRequest { Url = "https://mirror.example/file.bin", FileName = "other.bin" }, DateTime.Now));
    }

    [Fact]
    public void Refresh_wait_expires_after_ten_minutes()
    {
        var m = NewManager();
        AddWaiting(m);

        Assert.Null(m.FindRefreshTarget(new DownloadRequest { Url = "https://mirror.example/file.bin" }, DateTime.Now.AddMinutes(11)));
    }

    [Fact]
    public async Task Removed_download_is_not_a_refresh_target()
    {
        // Otherwise a later capture of the same name would restart a download the user deleted.
        var m = NewManager();
        var item = AddWaiting(m);

        await m.RemoveAsync(item, deleteFile: false);

        Assert.Null(m.FindRefreshTarget(new DownloadRequest { Url = "https://mirror.example/file.bin" }, DateTime.Now));
    }

    [Fact]
    public void Running_or_finished_download_is_not_a_refresh_target()
    {
        // Swapping the address under a running transfer could mix bytes from two servers into one
        // file, and a finished download would swallow the capture without downloading anything.
        var m = NewManager();
        var item = AddWaiting(m);
        var capture = new DownloadRequest { Url = "https://mirror.example/file.bin" };

        item.Status = DownloadStatus.Completed;
        Assert.Null(m.FindRefreshTarget(capture, DateTime.Now));

        item.Status = DownloadStatus.Paused;
        m.Start(item); // the old address refuses connections, so the download keeps retrying
        try
        {
            Assert.Null(m.FindRefreshTarget(capture, DateTime.Now));
        }
        finally
        {
            m.Pause(item);
        }
    }

    [Fact]
    public async Task Refreshed_address_resumes_from_saved_progress()
    {
        await using var server = await TestServer.StartAsync(2 * 1024 * 1024, TimeSpan.Zero);
        int total = server.Payload.Length, saved = 768 * 1024;
        var m = NewManager();
        var item = m.Add(new DownloadRequest { Url = OldUrl, FileName = "file.bin", Directory = _dir }, startNow: false);
        item.ResumeSupported = true;
        item.TotalBytes = total;
        item.SegmentsSnapshot = new() { new Segment { Start = 0, End = total - 1, Downloaded = saved } };
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(item.PartPath, server.Payload[..saved]);

        m.BeginRefresh(item);
        var capture = new DownloadRequest { Url = server.BaseUrl + "/mirror/file.bin" };
        Assert.Same(item, m.FindRefreshTarget(capture, DateTime.Now));

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        m.DownloadCompleted += _ => done.TrySetResult();
        m.DownloadFailed += i => done.TrySetException(new Exception(i.Error));
        m.ApplyRefresh(item, capture);
        await done.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
        Assert.Equal(capture.Url, item.Url);
        Assert.Null(m.AwaitingNewAddress);
        // Apart from the bytes=0-0 probe, no request asks for bytes the part file already had, other
        // than the overlap a continuation asks for again to check them.
        var starts = server.MirrorRanges.Where(r => r != "bytes=0-0").Select(r => long.Parse(r["bytes=".Length..r.IndexOf('-')])).ToList();
        Assert.Equal(saved - HttpDownloader.OverlapBytes, starts.Min());
    }
}
