using ADM.Core;

namespace ADM.Core.Tests;

/// <summary>What the Properties dialog shows and edits: description, parent page, last try, address, login and speed limits.</summary>
public sealed class DownloadDetailsTests : IDisposable
{
    // Port 9 (discard) refuses connections: a file started there keeps retrying, then fails.
    const string Unreachable = "http://127.0.0.1:9/";

    readonly string _dir = Path.Combine(Path.GetTempPath(), "adm-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    string StatePath => Path.Combine(_dir, "state", "state.json");

    DownloadManager NewManager(SynchronizationContext? ui = null)
    {
        var m = new DownloadManager(StatePath, ui ?? new SynchronizationContext());
        m.State.Settings.DownloadRoot = Path.Combine(_dir, "files");
        return m;
    }

    DownloadItem Reloaded(DownloadManager m, DownloadItem item)
    {
        m.Save();
        return new DownloadManager(StatePath, new SynchronizationContext()).Items.Single(i => i.Id == item.Id);
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
    public void Added_download_keeps_its_page_and_description()
    {
        var m = NewManager();
        var item = m.Add(new DownloadRequest { Url = Unreachable + "a.bin", Referrer = "https://site.test/page", Description = "notes" }, startNow: false);
        // A page the browser names wins over the referer, which can be a player or a CDN.
        var named = m.Add(new DownloadRequest { Url = Unreachable + "b.bin", Referrer = "https://cdn.test/player", PageUrl = "https://site.test/watch" }, startNow: false);

        var again = Reloaded(m, item);

        Assert.Equal("https://site.test/page", again.PageUrl);
        Assert.Equal("notes", again.Description);
        Assert.Equal("https://site.test/watch", named.PageUrl);
    }

    [Fact]
    public void Page_url_stays_when_the_referer_is_edited()
    {
        var m = NewManager();
        var item = m.Add(new DownloadRequest { Url = Unreachable + "a.bin", Referrer = "https://site.test/page" }, startNow: false);

        item.Referrer = "https://other.test/";

        Assert.Equal("https://site.test/page", Reloaded(m, item).PageUrl);
    }

    [Fact]
    public void Download_without_a_page_gets_none_from_a_later_referer()
    {
        // Only downloads saved before Phase 8 take their page from the referer.
        var m = NewManager();
        var item = m.Add(new DownloadRequest { Url = Unreachable + "a.bin" }, startNow: false);

        item.Referrer = "https://other.test/";

        Assert.True(string.IsNullOrEmpty(Reloaded(m, item).PageUrl));
    }

    [Fact]
    public void Older_downloads_take_their_page_from_the_referer()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        File.WriteAllText(StatePath, """
            { "Settings": { "SpeedLimitKBps": 100 },
              "Downloads": [ { "Url": "https://site.test/a.zip", "Referrer": "https://site.test/page", "FileName": "a.zip", "Status": "Paused" } ] }
            """);

        var state = Store.Load(StatePath);

        var item = Assert.Single(state.Downloads);
        Assert.Equal("https://site.test/page", item.PageUrl);
        Assert.True(item.KeepSpeedLimit);
        Assert.True(state.Settings.SpeedLimiterOn);
    }

    [Fact]
    public async Task Start_records_the_last_try()
    {
        await using var server = await TestServer.StartAsync(16 * 1024, TimeSpan.FromSeconds(20));
        var m = NewManager();
        var before = DateTime.Now;

        var item = m.Add(new DownloadRequest { Url = server.FileUrl, FileName = "a.bin" }, startNow: true);

        Assert.InRange(item.LastTryAt ?? default, before, DateTime.Now);
        await m.ShutdownAsync();
    }

    [Fact]
    public async Task Queue_started_download_is_marked()
    {
        var m = NewManager();
        var queued = m.Add(new DownloadRequest { Url = Unreachable + "q.bin", FileName = "q.bin" }, startNow: false);
        var manual = m.Add(new DownloadRequest { Url = Unreachable + "n.bin", FileName = "n.bin" }, startNow: true);

        m.StartQueue(m.MainQueue);

        Assert.True(m.IsRunning(queued));
        Assert.True(queued.StartedByQueue);
        Assert.False(manual.StartedByQueue);
        await m.ShutdownAsync();
    }

    [Fact]
    public async Task Removed_download_is_not_started_again()
    {
        // A progress window left open on a deleted download must not bring back a transfer nobody can see.
        var m = NewManager();
        var item = m.Add(new DownloadRequest { Url = Unreachable + "gone.bin", FileName = "gone.bin" }, startNow: false);
        await m.RemoveAsync(item, deleteFile: false);

        m.Start(item);
        m.Restart(item);

        Assert.False(m.IsRunning(item));
        Assert.Equal(0, m.ActiveCount);
    }

    [Fact]
    public async Task Sync_redownload_counts_as_started_by_its_queue()
    {
        // A sync queue that finds a changed file downloads it again; that is queue work, and opens no window.
        await using var server = await TestServer.StartAsync(64 * 1024, TimeSpan.Zero);
        var m = NewManager();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        m.DownloadCompleted += _ => done.TrySetResult();
        var item = m.Add(new DownloadRequest { Url = server.SyncUrl }, startNow: true);
        await done.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.False(item.StartedByQueue);
        Assert.True(m.MoveToQueue(item, m.SyncQueue));

        server.SyncVersion = 1;
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        m.QueueFinished += _ => finished.TrySetResult();
        m.StartQueue(m.SyncQueue);
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(server.SyncPayload(1), File.ReadAllBytes(item.FullPath));
        Assert.True(item.StartedByQueue);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 100)]
    public async Task Speed_limit_is_dropped_on_stop_unless_kept(bool keep, long expected)
    {
        await using var server = await TestServer.StartAsync(4 * 1024 * 1024, TimeSpan.Zero);
        var ui = new LockedContext();
        var m = NewManager(ui);
        DownloadItem item = null!;
        ui.Run(() =>
        {
            item = m.Add(new DownloadRequest { Url = server.ThrottledUrl, FileName = "a.bin" }, startNow: false);
            item.KeepSpeedLimit = keep;
            item.SpeedLimitKBps = 100;
            m.Start(item);
        });
        await Until(() => item.DownloadedBytes > 0);

        ui.Run(() => m.Pause(item));
        await Until(() => { var running = true; ui.Run(() => running = m.IsRunning(item)); return !running; });

        Assert.Equal(expected, item.SpeedLimitKBps);
    }

    [Theory]
    [InlineData("http://site.test/other/b.zip", true)]
    [InlineData("http://other.test/f.zip", false)]
    [InlineData("https://site.test/a.zip", false)]
    [InlineData("http://site.test:8080/a.zip", false)]
    public void Changing_the_address_to_another_host_drops_browser_cookies(string url, bool kept)
    {
        var m = NewManager();
        var item = m.Add(new DownloadRequest { Url = "http://site.test/a.zip", Cookies = "a=b" }, startNow: false);

        Assert.True(m.SetAddress(item, url));

        Assert.Equal(url, item.Url);
        Assert.Equal(kept ? "a=b" : null, item.Cookies);
    }

    [Theory]
    [InlineData("http://site.test/other/b.zip", true)]
    [InlineData("http://other.test/f.zip", false)]
    [InlineData("https://site.test/a.zip", false)]
    [InlineData("http://site.test:8080/a.zip", false)]
    public void Changing_the_address_to_another_host_drops_the_login(string url, bool kept)
    {
        // The login is offered to whatever server the download points at; another site must not get the old one's password.
        var m = NewManager();
        var item = m.Add(new DownloadRequest { Url = "http://site.test/a.zip", UserName = "u", Password = "p" }, startNow: false);

        Assert.True(m.SetAddress(item, url));

        Assert.Equal(kept ? "u" : null, item.UserName);
        Assert.Equal(kept ? "p" : null, Secrets.Unprotect(item.ProtectedPassword));
    }

    [Fact]
    public async Task Address_can_not_change_while_downloading()
    {
        await using var server = await TestServer.StartAsync(16 * 1024, TimeSpan.FromSeconds(20));
        var m = NewManager();
        var item = m.Add(new DownloadRequest { Url = server.FileUrl, FileName = "a.bin" }, startNow: true);

        Assert.False(m.SetAddress(item, server.FileUrl + "?other"));

        Assert.Equal(server.FileUrl, item.Url);
        await m.ShutdownAsync();
    }

    [Theory]
    [InlineData("javascript:x")]
    [InlineData("file:///c:/x")]
    [InlineData("site.test/a.zip")]
    [InlineData("")]
    public void Address_must_be_a_web_address(string url)
    {
        var m = NewManager();
        var item = m.Add(new DownloadRequest { Url = "http://site.test/a.zip", Cookies = "a=b" }, startNow: false);

        Assert.False(m.SetAddress(item, url));

        Assert.Equal("http://site.test/a.zip", item.Url);
        Assert.Equal("a=b", item.Cookies);
    }

    [Fact]
    public void New_address_ends_a_wait_for_a_refreshed_one()
    {
        var m = NewManager();
        var item = m.Add(new DownloadRequest { Url = Unreachable + "a.bin", FileName = "a.bin" }, startNow: false);
        m.BeginRefresh(item);

        Assert.True(m.SetAddress(item, "https://mirror.test/a.bin"));

        Assert.Null(m.AwaitingNewAddress);
    }

    [Fact]
    public void Login_is_stored_encrypted()
    {
        var m = NewManager();
        var item = m.Add(new DownloadRequest { Url = Unreachable + "a.bin" }, startNow: false);

        m.SetLogin(item, "u", "s3cret-xyz");
        m.Save();

        Assert.DoesNotContain("s3cret-xyz", File.ReadAllText(StatePath));
        Assert.Equal("u", item.UserName);
        Assert.Equal("s3cret-xyz", Secrets.Unprotect(item.ProtectedPassword));
    }

    [Fact]
    public void Login_without_a_new_password_keeps_the_saved_one()
    {
        // The Properties dialog leaves the password box empty; changing only the user name keeps the password.
        var m = NewManager();
        var item = m.Add(new DownloadRequest { Url = Unreachable + "a.bin", UserName = "u", Password = "p" }, startNow: false);

        m.SetLogin(item, "v", null);

        Assert.Equal("v", item.UserName);
        Assert.Equal("p", Secrets.Unprotect(item.ProtectedPassword));
    }

    [Fact]
    public void Empty_user_name_clears_the_login()
    {
        var m = NewManager();
        var item = m.Add(new DownloadRequest { Url = Unreachable + "a.bin", UserName = "u", Password = "p" }, startNow: false);

        m.SetLogin(item, "", "ignored");

        Assert.Null(item.UserName);
        Assert.Null(item.ProtectedPassword);
    }

    [Fact]
    public void Speed_limiter_off_lifts_the_global_limit()
    {
        var m = NewManager();
        m.State.Settings.SpeedLimitKBps = 100;

        m.State.Settings.SpeedLimiterOn = false;
        m.ApplySettings();
        Assert.Equal(0, m.Limiter.BytesPerSecond);

        m.State.Settings.SpeedLimiterOn = true;
        m.ApplySettings();
        Assert.Equal(102400, m.Limiter.BytesPerSecond);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void Speed_limiter_turns_on_at_startup_only_when_asked(bool onAtStartup, bool expected)
    {
        var m = NewManager();
        m.State.Settings.SpeedLimitKBps = 100;
        m.State.Settings.SpeedLimiterOn = false;
        m.State.Settings.SpeedLimiterOnAtStartup = onAtStartup;
        m.Save();

        var again = NewManager();

        Assert.Equal(expected, again.State.Settings.SpeedLimiterOn);
        Assert.Equal(expected ? 102400 : 0, again.Limiter.BytesPerSecond);
    }

    [Theory]
    [InlineData(DownloadStatus.Completed, null, "Complete")]
    [InlineData(DownloadStatus.Failed, "HTTP 404", "HTTP 404")]
    [InlineData(DownloadStatus.Paused, null, "Paused")]
    [InlineData(DownloadStatus.Paused, "download limit reached", "Paused: download limit reached")]
    [InlineData(DownloadStatus.Queued, null, "Waiting in the queue")]
    [InlineData(DownloadStatus.Downloading, null, "Downloading")]
    public void Last_result_describes_the_last_try(DownloadStatus status, string? error, string expected)
    {
        var item = new DownloadItem { Status = status, Error = error };

        Assert.Equal(expected, item.LastResultText);
    }

    [Fact]
    public void Last_result_follows_status_changes()
    {
        var item = new DownloadItem { Status = DownloadStatus.Paused };
        var raised = new List<string?>();
        item.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        item.Status = DownloadStatus.Completed;
        item.Error = "x";

        Assert.Equal(2, raised.Count(n => n == nameof(DownloadItem.LastResultText)));
    }
}
