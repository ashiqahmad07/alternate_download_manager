using System.Security.Cryptography;
using ADM.Core;

namespace ADM.Core.Tests;

/// <summary>Checking a finished download against a SHA-256 the user gave.</summary>
public sealed class FileHashTests : IDisposable
{
    const string SomeHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    readonly string _dir = Path.Combine(Path.GetTempPath(), "adm-tests", Guid.NewGuid().ToString("N"));

    public FileHashTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    string StatePath => Path.Combine(_dir, "state", "state.json");

    string Files => Path.Combine(_dir, "files");

    DownloadManager NewManager()
    {
        var m = new DownloadManager(StatePath, new LockedContext());
        m.State.Settings.DownloadRoot = Files;
        return m;
    }

    static string HashOf(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    DownloadRequest Request(TestServer server, string? sha256) =>
        new() { Url = server.FileUrl, FileName = "file.bin", Directory = Files, Sha256 = sha256 };

    /// <summary>Starts the download and waits until it completes or fails.</summary>
    static async Task<DownloadItem> DownloadAsync(DownloadManager m, DownloadRequest request)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Finished(DownloadItem _) => done.TrySetResult();
        m.DownloadCompleted += Finished;
        m.DownloadFailed += Finished;
        var item = m.Add(request, startNow: true);
        await done.Task.WaitAsync(TimeSpan.FromSeconds(30));
        m.DownloadCompleted -= Finished;
        m.DownloadFailed -= Finished;
        return item;
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

    // ---- What people paste

    [Theory]
    [InlineData(SomeHash, SomeHash)]
    [InlineData("E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855", SomeHash)]
    [InlineData("  " + SomeHash + "\r\n", SomeHash)]
    [InlineData(SomeHash + "  file.zip", SomeHash)]
    [InlineData(SomeHash + " *file.zip", SomeHash)]
    public void Normalize_takes_what_people_paste(string text, string hash) =>
        Assert.Equal(hash, FileHash.Normalize(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a hash")]
    [InlineData("d41d8cd98f00b204e9800998ecf8427e")] // MD5
    [InlineData("da39a3ee5e6b4b0d3255bfef95601890afd80709")] // SHA-1
    [InlineData("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b85")] // one short
    [InlineData("g3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")] // not hex
    [InlineData(SomeHash + "a")] // one too many
    [InlineData("hash " + SomeHash)]
    public void Normalize_refuses_what_is_not_a_sha256(string? text) =>
        Assert.Null(FileHash.Normalize(text));

    [Fact]
    public async Task A_file_is_hashed()
    {
        var path = Path.Combine(_dir, "some.bin");
        var data = new byte[3 * 1024 * 1024];
        new Random(1).NextBytes(data);
        File.WriteAllBytes(path, data);

        Assert.Equal(HashOf(data), await FileHash.Sha256Async(path, CancellationToken.None));
    }

    // ---- The outcome

    [Fact]
    public async Task Matching_hash_completes_and_says_so()
    {
        await using var server = await TestServer.StartAsync(64 * 1024, TimeSpan.Zero);
        var m = NewManager();

        var item = await DownloadAsync(m, Request(server, HashOf(server.Payload).ToUpperInvariant()));

        Assert.Equal(DownloadStatus.Completed, item.Status);
        Assert.Equal(StatusWords.Current.HashMatches, item.HashResult);
        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
        Assert.False(item.Landed);
    }

    [Fact]
    public async Task No_hash_is_no_check()
    {
        await using var server = await TestServer.StartAsync(64 * 1024, TimeSpan.Zero);
        var m = NewManager();
        m.Hasher = (_, _) => throw new InvalidOperationException("nothing to check");

        var item = await DownloadAsync(m, Request(server, null));

        Assert.Equal(DownloadStatus.Completed, item.Status);
        Assert.Null(item.HashResult);
    }

    [Fact]
    public async Task Wrong_hash_fails_and_keeps_the_file_under_a_safe_name()
    {
        await using var server = await TestServer.StartAsync(64 * 1024, TimeSpan.Zero);
        var m = NewManager();

        var item = await DownloadAsync(m, Request(server, SomeHash));

        Assert.Equal(DownloadStatus.Failed, item.Status);
        Assert.False(File.Exists(item.FullPath));
        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath + ".badhash"));
        Assert.Contains("file.bin.badhash", item.Error);
        Assert.False(item.Landed);
    }

    [Fact]
    public async Task Mismatch_on_a_file_held_open_still_fails()
    {
        await using var server = await TestServer.StartAsync(64 * 1024, TimeSpan.Zero);
        var m = NewManager();
        FileStream? held = null;
        m.Hasher = (path, _) =>
        {
            held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return Task.FromResult(SomeHash);
        };

        var item = await DownloadAsync(m, Request(server, new string('a', 64)));
        using (held)
        {
            Assert.Equal(DownloadStatus.Failed, item.Status);
            Assert.Contains("file.bin", item.Error);
            Assert.False(item.Landed);
            Assert.True(File.Exists(item.FullPath));
        }
    }

    [Fact]
    public async Task Second_mismatch_gets_a_numbered_name()
    {
        await using var server = await TestServer.StartAsync(64 * 1024, TimeSpan.Zero);
        var m = NewManager();
        var first = await DownloadAsync(m, Request(server, SomeHash));

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        m.DownloadFailed += _ => done.TrySetResult();
        m.Restart(first);
        await done.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(new[] { "file.bin (1).badhash", "file.bin.badhash" }, Directory.GetFiles(Files).Select(Path.GetFileName).Order());
    }

    [Fact]
    public async Task Mismatch_keeps_the_mark_of_the_web()
    {
        await using var server = await TestServer.StartAsync(64 * 1024, TimeSpan.Zero);
        var m = NewManager();

        var item = await DownloadAsync(m, Request(server, SomeHash));

        Assert.Contains("ZoneId=3", File.ReadAllText(item.FullPath + ".badhash:Zone.Identifier"));
    }

    [Fact]
    public async Task Wrong_hash_is_not_retried_by_a_queue()
    {
        await using var server = await TestServer.StartAsync(64 * 1024, TimeSpan.Zero);
        var m = NewManager();
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        m.QueueFinished += _ => finished.TrySetResult();
        var item = m.Add(Request(server, SomeHash), startNow: false, m.MainQueue);

        m.StartQueue(m.MainQueue);
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(DownloadStatus.Failed, item.Status);
        Assert.Single(Directory.GetFiles(Files));
    }

    // ---- While it runs

    [Fact]
    public async Task Completion_waits_for_the_check()
    {
        await using var server = await TestServer.StartAsync(64 * 1024, TimeSpan.Zero);
        var m = NewManager();
        var held = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        m.Hasher = (_, _) => held.Task;
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        m.DownloadCompleted += _ => completed.TrySetResult();

        var item = m.Add(Request(server, HashOf(server.Payload)), startNow: true);
        await Until(() => item.IsHashing);

        Assert.Equal(DownloadStatus.Downloading, item.Status);
        Assert.Equal(StatusWords.Current.Hashing, item.StatusText);
        Assert.True(m.IsRunning(item));
        Assert.False(completed.Task.IsCompleted);
        // What a crash now would leave.
        Assert.True(Assert.Single(Store.Load(StatePath).Downloads).Landed);

        held.SetResult(HashOf(server.Payload));
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.False(item.IsHashing);
    }

    [Fact]
    public async Task Pause_stops_the_check_and_says_so()
    {
        await using var server = await TestServer.StartAsync(64 * 1024, TimeSpan.Zero);
        var m = NewManager();
        m.Hasher = async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return "";
        };
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        m.DownloadCompleted += _ => completed.TrySetResult();

        var item = m.Add(Request(server, HashOf(server.Payload)), startNow: true);
        await Until(() => item.IsHashing);
        m.Pause(item);
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(DownloadStatus.Completed, item.Status);
        Assert.Equal(StatusWords.Current.HashNotChecked, item.HashResult);
    }

    [Fact]
    public async Task Stopping_a_queue_leaves_a_check_to_finish()
    {
        await using var server = await TestServer.StartAsync(64 * 1024, TimeSpan.Zero);
        var m = NewManager();
        var held = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        m.Hasher = async (_, ct) => await held.Task.WaitAsync(ct);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        m.DownloadCompleted += _ => completed.TrySetResult();
        var item = m.Add(Request(server, HashOf(server.Payload)), startNow: false, m.MainQueue);

        m.StartQueue(m.MainQueue);
        await Until(() => item.IsHashing);
        m.StopQueue(m.MainQueue);
        held.SetResult(HashOf(server.Payload));
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(StatusWords.Current.HashMatches, item.HashResult);
    }

    [Fact]
    public async Task Exiting_stops_the_check_and_says_so()
    {
        await using var server = await TestServer.StartAsync(64 * 1024, TimeSpan.Zero);
        var m = NewManager();
        m.Hasher = async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return "";
        };

        var item = m.Add(Request(server, HashOf(server.Payload)), startNow: true);
        await Until(() => item.IsHashing);
        await m.ShutdownAsync();

        var saved = Assert.Single(Store.Load(StatePath).Downloads);
        Assert.Equal(DownloadStatus.Completed, saved.Status);
        Assert.Equal(StatusWords.Current.HashNotChecked, saved.HashResult);
    }

    [Fact]
    public async Task Exiting_during_a_check_that_will_not_stop_says_so()
    {
        await using var server = await TestServer.StartAsync(64 * 1024, TimeSpan.Zero);
        var m = NewManager();
        var never = new TaskCompletionSource<string>();
        m.Hasher = (_, _) => never.Task;

        var item = m.Add(Request(server, HashOf(server.Payload)), startNow: true);
        await Until(() => item.IsHashing);
        await m.ShutdownAsync();

        var saved = Assert.Single(Store.Load(StatePath).Downloads);
        Assert.Equal(DownloadStatus.Completed, saved.Status);
        Assert.Equal(StatusWords.Current.HashNotChecked, saved.HashResult);
        Assert.Null(saved.ScanResult);
    }

    [Fact]
    public async Task A_file_that_cannot_be_read_goes_unchecked()
    {
        await using var server = await TestServer.StartAsync(64 * 1024, TimeSpan.Zero);
        var m = NewManager();
        m.Hasher = (_, _) => throw new IOException("the disk went away");

        var item = await DownloadAsync(m, Request(server, HashOf(server.Payload)));

        Assert.Equal(DownloadStatus.Completed, item.Status);
        Assert.Equal(StatusWords.Current.HashNotChecked, item.HashResult);
    }

    [Fact]
    public void Crash_during_the_check_completes_unchecked()
    {
        var saved = new DownloadItem { Url = "http://127.0.0.1:9/file.bin", Directory = Files, FileName = "file.bin", Status = DownloadStatus.Downloading, Landed = true, ExpectedSha256 = SomeHash };
        var state = new AppState();
        state.Downloads.Add(saved);
        Directory.CreateDirectory(Files);
        Store.Save(StatePath, state);
        File.WriteAllBytes(saved.FullPath, new byte[] { 1, 2, 3 });

        var item = Assert.Single(NewManager().Items);

        Assert.Equal(DownloadStatus.Completed, item.Status);
        Assert.Equal(StatusWords.Current.HashNotChecked, item.HashResult);
    }

    [Fact]
    public void Crash_during_the_scan_keeps_a_match_already_found()
    {
        var saved = new DownloadItem { Url = "http://127.0.0.1:9/file.bin", Directory = Files, FileName = "file.bin", Status = DownloadStatus.Downloading, Landed = true, ExpectedSha256 = SomeHash, HashResult = StatusWords.Current.HashMatches };
        var state = new AppState();
        state.Downloads.Add(saved);
        Directory.CreateDirectory(Files);
        Store.Save(StatePath, state);
        File.WriteAllBytes(saved.FullPath, new byte[] { 1, 2, 3 });

        var item = Assert.Single(NewManager().Items);

        Assert.Equal(StatusWords.Current.HashMatches, item.HashResult);
    }

    // ---- What keeps it, and what drops it

    [Fact]
    public async Task Hash_is_saved_and_older_state_still_loads()
    {
        var path = Path.Combine(_dir, "older.json");
        File.WriteAllText(path, """{ "Downloads": [ { "Url": "http://example.com/a.zip" } ] }""");
        Assert.Null(Assert.Single(Store.Load(path).Downloads).ExpectedSha256);

        await using var server = await TestServer.StartAsync(1024, TimeSpan.Zero);
        var m = NewManager();
        var item = await DownloadAsync(m, Request(server, HashOf(server.Payload)));
        m.Save();

        var saved = Assert.Single(Store.Load(StatePath).Downloads);
        Assert.Equal(item.ExpectedSha256, saved.ExpectedSha256);
        Assert.Equal(StatusWords.Current.HashMatches, saved.HashResult);
    }

    [Fact]
    public void Text_that_is_not_a_hash_is_not_kept()
    {
        var m = NewManager();

        var item = m.Add(new DownloadRequest { Url = "http://example.com/a.zip", Sha256 = "not a hash" }, startNow: false);

        Assert.Null(item.ExpectedSha256);
    }

    [Fact]
    public async Task Redownload_keeps_the_hash()
    {
        await using var server = await TestServer.StartAsync(64 * 1024, TimeSpan.Zero);
        var m = NewManager();
        var item = await DownloadAsync(m, Request(server, SomeHash));

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        m.DownloadFailed += _ => done.TrySetResult();
        m.Restart(item);
        await done.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(SomeHash, item.ExpectedSha256);
    }

    [Fact]
    public async Task A_new_try_forgets_the_last_result()
    {
        await using var server = await TestServer.StartAsync(64 * 1024, TimeSpan.Zero);
        var m = NewManager();
        var item = await DownloadAsync(m, Request(server, HashOf(server.Payload)));
        var held = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        m.Hasher = (_, _) => held.Task;

        m.Restart(item);
        Assert.Null(item.HashResult);

        held.SetResult(HashOf(server.Payload));
        await Until(() => item.Status == DownloadStatus.Completed);
    }

    [Fact]
    public async Task Sync_download_drops_the_hash()
    {
        await using var server = await TestServer.StartAsync(64 * 1024, TimeSpan.Zero);
        var m = NewManager();
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        m.QueueFinished += _ => finished.TrySetResult();
        var item = m.Add(Request(server, SomeHash), startNow: false, m.SyncQueue);

        m.StartQueue(m.SyncQueue);
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(DownloadStatus.Completed, item.Status);
        Assert.Null(item.ExpectedSha256);
        Assert.Null(item.HashResult);
    }

    [Fact]
    public async Task Hls_ignores_a_hash()
    {
        await using var server = await TestServer.StartAsync(64 * 1024, TimeSpan.Zero);
        var m = NewManager();

        var item = await DownloadAsync(m, new DownloadRequest { Url = server.HlsTsUrl, Kind = DownloadKind.Hls, FileName = "video.ts", Directory = Files, Sha256 = SomeHash });

        Assert.Equal(DownloadStatus.Completed, item.Status);
        Assert.Null(item.ExpectedSha256);
    }
}
