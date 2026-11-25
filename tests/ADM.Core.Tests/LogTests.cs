using System.Text.RegularExpressions;
using ADM.Core;

namespace ADM.Core.Tests;

/// <summary>On their own, since they start the one static log, and anything running beside them would write to it.</summary>
[CollectionDefinition(nameof(LogTests), DisableParallelization = true)]
public sealed class LogTestsCollection
{
}

[Collection(nameof(LogTests))]
public sealed class LogTests : IDisposable
{
    static readonly TimeSpan Short = TimeSpan.FromMilliseconds(20);

    readonly string _dir = Path.Combine(Path.GetTempPath(), "adm-tests", Guid.NewGuid().ToString("N"));

    public LogTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        Log.Stop();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    string LogPath => Path.Combine(_dir, "logs", "adm.log");

    string Written => File.Exists(LogPath) ? File.ReadAllText(LogPath) : "";

    DownloadManager NewManager()
    {
        var m = new DownloadManager(Path.Combine(_dir, "state", "state.json"), new LockedContext());
        m.State.Settings.DownloadRoot = Path.Combine(_dir, "files");
        return m;
    }

    static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException();
            await Task.Delay(10);
        }
    }

    // ---- A line

    [Fact]
    public void Line_has_its_time_level_and_text()
    {
        new LogFile(LogPath).Info("Something happened");

        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} INFO +Something happened\r?\n$", Written);
    }

    [Fact]
    public void Exception_is_written_with_its_stack()
    {
        Exception thrown;
        try
        {
            throw new InvalidOperationException("It went wrong");
        }
        catch (InvalidOperationException e)
        {
            thrown = e;
        }

        new LogFile(LogPath).Error("Something failed", thrown);

        var lines = Written.Split('\n');
        Assert.Matches(@" ERROR +Something failed\r?$", lines[0]);
        Assert.Contains("It went wrong", Written);
        Assert.Contains(nameof(Exception_is_written_with_its_stack), Written);
    }

    [Fact]
    public void A_line_break_in_a_text_stays_on_one_line()
    {
        new LogFile(LogPath).Info("One\r\nTwo");

        Assert.Single(Written.TrimEnd().Split('\n'));
    }

    // ---- What is left out

    [Fact]
    public void Clean_cuts_addresses_to_their_host()
    {
        Assert.Equal("https://host", Log.Clean("https://user:pass@host/s/token/file.zip?sig=abc"));
        Assert.Equal("Failed: ftp://server.example, then more", Log.Clean("Failed: ftp://me:secret@server.example:2121/a/b.iso, then more"));
    }

    [Theory]
    [InlineData("https://host/file;jsessionid=SECRET", "https://host")]
    [InlineData("https://host;jsessionid=SECRET", "https://host")]
    [InlineData("https://host/a,SECRET/file.zip", "https://host")]
    [InlineData("https://host/it's/SECRET", "https://host")]
    [InlineData("https://host?SECRET", "https://host")]
    [InlineData("https://host:8443/SECRET", "https://host")]
    public void Clean_takes_all_of_an_address(string address, string cleaned) =>
        Assert.Equal(cleaned, Log.Clean(address));

    [Theory]
    [InlineData("Asked https://host/SECRET, then more", "Asked https://host, then more")]
    [InlineData("Asked https://host/SECRET; then more", "Asked https://host; then more")]
    [InlineData("Asked 'https://host/SECRET' and more", "Asked 'https://host' and more")]
    [InlineData("Asked \"https://host/SECRET\" and more", "Asked \"https://host\" and more")]
    public void Clean_leaves_what_comes_after_an_address(string text, string cleaned) =>
        Assert.Equal(cleaned, Log.Clean(text));

    [Theory]
    [InlineData(@"Access to the path 'C:\Users\me\Downloads\SECRET.zip.part' is denied.", "Access to the path '(path)' is denied.")]
    [InlineData(@"Could not find ""D:\My Files\SECRET.zip"".", "Could not find \"(path)\".")]
    [InlineData(@"Access to the path 'C:\it's\SECRET.zip' is denied.", "Access to the path '(path)' is denied.")]
    [InlineData(@"Access to the path '\\server\share\SECRET.zip' is denied.", "Access to the path '(path)' is denied.")]
    [InlineData(@"Could not move C:\My Files\SECRET.zip for some reason", "Could not move (path)")]
    [InlineData(@"Could not move \\server\share\SECRET.zip", "Could not move (path)")]
    public void Clean_removes_file_paths(string text, string cleaned) =>
        Assert.Equal(cleaned, Log.Clean(text));

    [Fact]
    public void Clean_keeps_a_stack_frames_source_line()
    {
        const string frame = @"   at ADM.Core.Store.Save(String path) in D:\Project\IDM\src\ADM.Core\Store.cs:line 66";

        Assert.Equal(frame, Log.Clean(frame));
    }

    [Fact]
    public void Clean_leaves_ordinary_text_alone()
    {
        const string text = "Not enough disk space on C: (needs 1 MB, 2 KB free) at 10:30";

        Assert.Equal(text, Log.Clean(text));
    }

    [Fact]
    public async Task Failed_download_logs_no_path()
    {
        // A file where its folder should go, so the error from Windows names the path.
        await using var server = await TestServer.StartAsync(1024, TimeSpan.Zero);
        Log.Start(LogPath);
        File.WriteAllText(Path.Combine(_dir, "privateblocker"), "");
        var m = NewManager();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        m.DownloadFailed += _ => done.TrySetResult();
        var item = m.Add(new DownloadRequest { Url = server.FileUrl, FileName = "file.bin", Directory = Path.Combine(_dir, "privateblocker", "privatefolder") }, startNow: true);

        await done.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Contains("privateblocker", item.Error);
        Assert.Contains(item.Id.ToString(), Written);
        Assert.DoesNotContain("private", Written);
    }

    [Fact]
    public async Task Ftp_reply_text_stays_out_of_the_log()
    {
        await using var server = await FtpTestServer.StartAsync(1024);
        server.RetrReply = "553 Won't send /pathtoken/privatename.iso";
        Log.Start(LogPath);
        var m = NewManager();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        m.DownloadFailed += _ => done.TrySetResult();
        var item = m.Add(new DownloadRequest { Url = server.FileUrl, FileName = "file.bin" }, startNow: true);

        await done.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Contains("553", Written);
        Assert.DoesNotContain("pathtoken", Written);
        Assert.Contains("pathtoken", item.Error);
    }

    [Fact]
    public void Clean_removes_cookie_and_authorization_values()
    {
        var cleaned = Log.Clean("Sent\nCookie: sid=abc; token=def\nAuthorization: Basic dXNlcjpwYXNz\nAccept: */*");

        Assert.DoesNotContain("abc", cleaned);
        Assert.DoesNotContain("dXNlcjpwYXNz", cleaned);
        Assert.Contains("Accept: */*", cleaned);
    }

    [Fact]
    public void A_log_file_cleans_whatever_it_is_given()
    {
        var log = new LogFile(LogPath);

        log.Info("Asked https://host.example/texttoken");
        log.Error("Failed", new HttpRequestException("While asking https://host.example/messagetoken\nCookie: cookievalue"));

        Assert.Contains("https://host.example", Written);
        foreach (var secret in new[] { "texttoken", "messagetoken", "cookievalue" })
            Assert.DoesNotContain(secret, Written);
    }

    [Fact]
    public void Host_gives_only_the_host()
    {
        Assert.Equal("host.example", Log.Host("https://me:secret@host.example/token/file.zip?sig=abc"));
        Assert.DoesNotContain("not", Log.Host("not an address"));
    }

    [Fact]
    public async Task Failed_download_logs_no_secrets()
    {
        await using var server = await TestServer.StartAsync(1024, TimeSpan.Zero);
        Log.Start(LogPath);
        var m = NewManager();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        m.DownloadFailed += _ => done.TrySetResult();
        var item = m.Add(new DownloadRequest
        {
            Url = server.BaseUrl + "/secret/pathtoken/privatename.zip?sig=querytoken",
            FileName = "privatename.zip",
            Cookies = "sid=cookievalue",
            Referrer = "https://referrer.example/refererpath",
            UserName = "loginname",
            Password = "loginpassword",
        }, startNow: true);

        await done.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var written = Written;
        Assert.Contains(item.Id.ToString(), written);
        Assert.Contains("127.0.0.1", written);
        Assert.Contains("500", written);
        foreach (var secret in new[] { "pathtoken", "privatename", "querytoken", "cookievalue", "refererpath", "loginname", "loginpassword" })
            Assert.DoesNotContain(secret, written);
    }

    // ---- What is logged

    [Fact]
    public void Start_says_which_version_on_which_windows()
    {
        Log.Start(LogPath);

        Assert.Contains("Windows", Written);
        Assert.Matches(@"ADM \S+ starting", Written);
    }

    [Fact]
    public async Task Completed_download_is_logged()
    {
        await using var server = await TestServer.StartAsync(1024, TimeSpan.Zero);
        Log.Start(LogPath);
        var m = NewManager();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        m.DownloadCompleted += _ => done.TrySetResult();
        var item = m.Add(new DownloadRequest { Url = server.FileUrl, FileName = "file.bin" }, startNow: true);

        await done.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Matches($"{item.Id}.*starting", Written);
        Assert.Matches($"{item.Id}.*complete", Written);
    }

    [Fact]
    public async Task Queue_starting_and_stopping_is_logged()
    {
        await using var server = await TestServer.StartAsync(1024, TimeSpan.Zero);
        Log.Start(LogPath);
        var m = NewManager();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        m.QueueFinished += _ => done.TrySetResult();
        m.Add(new DownloadRequest { Url = server.FileUrl, FileName = "file.bin" }, startNow: false, m.MainQueue);

        m.StartQueue(m.MainQueue);
        await done.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Contains("Queue started", Written);
        Assert.Contains("Queue finished", Written);
    }

    [Fact]
    public async Task Reconnect_wait_is_logged()
    {
        await using var server = await TestServer.StartAsync(4 * 1024 * 1024, TimeSpan.Zero);
        Log.Start(LogPath);
        var item = new DownloadItem { Url = server.ThrottledUrl, Directory = _dir, FileName = "file.bin", MaxConnections = 2 };
        var retry = new RetryPolicy { FirstBackoff = Short, MaxBackoff = Short };
        var run = new HttpDownloader(new RateLimiter(), new Settings(), (_, _) => { }) { Retry = retry }.RunAsync(item, CancellationToken.None);

        await Until(() => item.DownloadedBytes >= 256 * 1024);
        server.Down = true;
        await Until(() => Written.Contains("waiting to reconnect"));
        // Long enough for many tries.
        await Task.Delay(300);
        server.Down = false;
        await run.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Contains("reconnected", Written);
        // Once for the outage, not once for every try.
        Assert.True(Regex.Matches(Written, "waiting to reconnect").Count <= 2, Written);
    }

    [Fact]
    public async Task Starting_during_an_outage_is_logged()
    {
        await using var server = await TestServer.StartAsync(1024, TimeSpan.Zero);
        Log.Start(LogPath);
        server.Down = true;
        var item = new DownloadItem { Url = server.FileUrl, Directory = _dir, FileName = "file.bin" };
        var retry = new RetryPolicy { FirstBackoff = Short, MaxBackoff = Short };
        var run = new HttpDownloader(new RateLimiter(), new Settings(), (_, _) => { }) { Retry = retry }.RunAsync(item, CancellationToken.None);

        await Until(() => Written.Contains("waiting to reconnect"));
        server.Down = false;
        await run.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Contains("reconnected", Written);
    }

    [Fact]
    public async Task Hls_outage_is_logged()
    {
        await using var server = await TestServer.StartAsync(1024 * 1024, TimeSpan.Zero);
        Log.Start(LogPath);
        server.HlsOutageAfterParts = 2;
        server.HlsOutage = TimeSpan.FromMilliseconds(500);
        var item = new DownloadItem { Url = server.HlsTsUrl, Directory = _dir, FileName = "video.ts", Kind = DownloadKind.Hls, MaxConnections = 1 };
        var hls = new HlsDownloader(new RateLimiter(), new Settings(), null) { Retry = new RetryPolicy { FirstBackoff = Short, MaxBackoff = Short } };

        await hls.RunAsync(item, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Contains("waiting to reconnect", Written);
        Assert.Contains("reconnected", Written);
    }

    [Fact]
    public void Failed_state_save_is_logged()
    {
        Log.Start(LogPath);
        var path = Path.Combine(_dir, "state.json");
        // Nothing can be written where a folder has the temporary file's name.
        Directory.CreateDirectory(path + ".tmp");

        Assert.ThrowsAny<Exception>(() => Store.Save(path, new AppState()));

        Assert.Contains("ERROR", Written);
        Assert.Contains("state", Written);
    }

    [Fact]
    public void Loading_from_the_backup_is_logged()
    {
        Log.Start(LogPath);
        var path = Path.Combine(_dir, "state.json");
        Store.Save(path, new AppState());
        File.Copy(path, path + ".bak");
        File.WriteAllText(path, "{ not json");

        Store.Load(path);

        Assert.Contains("backup", Written);
    }

    // ---- Its size

    [Fact]
    public void Log_rolls_at_its_limit_and_keeps_two_files()
    {
        var log = new LogFile(LogPath, limit: 1000);

        for (int i = 0; i < 200; i++) log.Info($"Line number {i} with enough words to fill it up");

        var older = Path.Combine(_dir, "logs", "adm.1.log");
        Assert.True(File.Exists(older));
        Assert.Equal(new[] { "adm.1.log", "adm.log" }, Directory.GetFiles(Path.GetDirectoryName(LogPath)!).Select(Path.GetFileName).Order());
        Assert.True(new FileInfo(LogPath).Length <= 1100, $"{new FileInfo(LogPath).Length} bytes");
        Assert.Contains("Line number 199", Written);
    }

    [Fact]
    public void Lines_from_many_threads_stay_whole()
    {
        var log = new LogFile(LogPath);

        Parallel.For(0, 8, thread =>
        {
            for (int i = 0; i < 100; i++) log.Info($"Thread {thread} line {i}");
        });

        var lines = Written.TrimEnd().Split('\n');
        Assert.Equal(800, lines.Length);
        Assert.All(lines, line => Assert.Matches(@"INFO +Thread \d line \d+\r?$", line));
    }

    // ---- It can't get in the way

    [Fact]
    public void Nothing_is_written_before_start()
    {
        Log.Info("Nobody is listening");
        Log.Error("Nor here", new InvalidOperationException());

        Assert.False(File.Exists(LogPath));
    }

    [Fact]
    public async Task Unwritable_log_does_not_stop_a_download()
    {
        await using var server = await TestServer.StartAsync(1024, TimeSpan.Zero);
        Directory.CreateDirectory(LogPath);
        Log.Start(LogPath);
        var m = NewManager();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        m.DownloadCompleted += _ => done.TrySetResult();
        var item = m.Add(new DownloadRequest { Url = server.FileUrl, FileName = "file.bin" }, startNow: true);

        await done.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
    }

    // ---- How the last run ended

    [Fact]
    public void Unclean_end_is_noted_at_the_next_start()
    {
        Log.Start(LogPath);
        Log.Info("Then the power went off");

        Log.Start(LogPath);

        Assert.Single(Regex.Matches(Written, "did not end cleanly"));
    }

    [Fact]
    public void Clean_exit_is_not_noted()
    {
        Log.Start(LogPath);
        Log.Stop();

        Log.Start(LogPath);

        Assert.DoesNotContain("did not end cleanly", Written);
    }

    [Fact]
    public void A_first_start_notes_nothing()
    {
        Log.Start(LogPath);

        Assert.DoesNotContain("did not end cleanly", Written);
    }
}
