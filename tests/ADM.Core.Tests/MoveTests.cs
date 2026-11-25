using ADM.Core;

namespace ADM.Core.Tests;

/// <summary>Move/Rename: a download's file, finished or not, goes to a new folder or name and keeps its progress.</summary>
public sealed class MoveTests : IDisposable
{
    const int MB = 1024 * 1024;
    // Port 9 (discard) refuses connections; these downloads are never started.
    const string Unreachable = "http://127.0.0.1:9/";

    readonly string _dir = Path.Combine(Path.GetTempPath(), "adm-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    string StatePath => Path.Combine(_dir, "state", "state.json");
    string Files => Path.Combine(_dir, "files");

    DownloadManager NewManager(SynchronizationContext? ui = null)
    {
        var m = new DownloadManager(StatePath, ui ?? new SynchronizationContext());
        m.State.Settings.DownloadRoot = Files;
        return m;
    }

    static byte[] Bytes(int seed) => Enumerable.Range(0, 64 * 1024).Select(i => (byte)(i * seed + seed)).ToArray();

    /// <summary>A finished download whose file holds <see cref="Bytes"/> of <paramref name="seed"/>.</summary>
    DownloadItem Finished(DownloadManager m, string name = "a.bin", int seed = 1)
    {
        var item = m.Add(new DownloadRequest { Url = Unreachable + name, FileName = name, Directory = Files }, startNow: false);
        Directory.CreateDirectory(Files);
        File.WriteAllBytes(item.FullPath, Bytes(seed));
        item.Status = DownloadStatus.Completed;
        return item;
    }

    /// <summary>A paused download whose part file holds <see cref="Bytes"/> of <paramref name="seed"/>.</summary>
    DownloadItem Unfinished(DownloadManager m, string name = "a.bin", int seed = 1)
    {
        var item = m.Add(new DownloadRequest { Url = Unreachable + name, FileName = name, Directory = Files }, startNow: false);
        m.Pause(item);
        Directory.CreateDirectory(Files);
        File.WriteAllBytes(item.PartPath, Bytes(seed));
        return item;
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
    public async Task Completed_file_moves_to_the_new_folder_and_name()
    {
        var m = NewManager();
        var item = Finished(m);
        var old = item.FullPath;
        var category = item.Category;
        var target = Path.Combine(_dir, "elsewhere", "b.bin");

        await m.MoveAsync(item, target, replace: false);

        Assert.False(File.Exists(old));
        Assert.Equal(Bytes(1), File.ReadAllBytes(target));
        Assert.Equal(target, item.FullPath);
        Assert.True(item.DirectoryChosenByUser);
        Assert.Equal(category, item.Category);
    }

    [Fact]
    public async Task Rename_in_place_keeps_the_folder()
    {
        var m = NewManager();
        var item = Finished(m);

        await m.MoveAsync(item, Path.Combine(Files, "renamed.bin"), replace: false);

        Assert.Equal(Files, item.Directory);
        Assert.Equal("renamed.bin", item.FileName);
        Assert.Equal(Bytes(1), File.ReadAllBytes(Path.Combine(Files, "renamed.bin")));
        Assert.False(File.Exists(Path.Combine(Files, "a.bin")));
    }

    [Fact]
    public async Task Same_path_in_another_case_does_nothing()
    {
        var m = NewManager();
        var item = Finished(m);

        await m.MoveAsync(item, Path.Combine(Files, "A.BIN"), replace: false);

        Assert.Equal("a.bin", item.FileName);
        Assert.Equal(Bytes(1), File.ReadAllBytes(item.FullPath));
    }

    [Fact]
    public async Task Moved_unfinished_download_resumes_to_identical_bytes()
    {
        await using var server = await TestServer.StartAsync(4 * MB, TimeSpan.Zero);
        var ui = new LockedContext();
        var m = NewManager(ui);
        DownloadItem item = null!;
        ui.Run(() => item = m.Add(new DownloadRequest { Url = server.ThrottledUrl, FileName = "file.bin", Directory = Files }, startNow: true));
        await Until(() => item.DownloadedBytes >= MB);
        ui.Run(() => m.Pause(item));
        await Until(() => { var running = true; ui.Run(() => running = m.IsRunning(item)); return !running; });
        var oldFull = item.FullPath;
        var oldPart = item.PartPath;
        Assert.True(File.Exists(oldPart));

        await m.MoveAsync(item, Path.Combine(Files, "sub", "renamed.bin"), replace: false);

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        m.DownloadCompleted += _ => done.TrySetResult();
        m.DownloadFailed += i => done.TrySetException(new Exception(i.Error));
        ui.Run(() => m.Start(item));
        await done.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(Path.Combine(Files, "sub", "renamed.bin"), item.FullPath);
        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
        Assert.False(File.Exists(oldFull));
        Assert.False(File.Exists(oldPart));
    }

    [Fact]
    public async Task Unfinished_hls_moves_its_parts_folder()
    {
        var m = NewManager();
        var item = m.Add(new DownloadRequest { Url = Unreachable + "live.m3u8", Kind = DownloadKind.Hls, Directory = Files }, startNow: false);
        m.Pause(item);
        var parts = Directory.CreateDirectory(item.FullPath + ".admparts").FullName;
        File.WriteAllBytes(Path.Combine(parts, "00001.ts"), Bytes(2));

        await m.MoveAsync(item, Path.Combine(Files, "clip.ts"), replace: false);

        Assert.False(Directory.Exists(parts));
        Assert.Equal(Bytes(2), File.ReadAllBytes(Path.Combine(Files, "clip.ts.admparts", "00001.ts")));
    }

    [Fact]
    public async Task Download_without_a_name_takes_the_new_one()
    {
        var m = NewManager();
        var item = m.Add(new DownloadRequest { Url = Unreachable }, startNow: false);
        Assert.Equal("", item.FileName);
        var target = Path.Combine(_dir, "chosen", "named.bin");

        await m.MoveAsync(item, target, replace: false);

        Assert.Equal(target, item.FullPath);
        Assert.True(item.DirectoryChosenByUser);
    }

    [Fact]
    public async Task Running_download_can_not_be_moved()
    {
        await using var server = await TestServer.StartAsync(16 * 1024, TimeSpan.FromSeconds(20));
        var m = NewManager();
        var item = m.Add(new DownloadRequest { Url = server.FileUrl, FileName = "a.bin", Directory = Files }, startNow: true);
        var before = item.FullPath;

        await Assert.ThrowsAsync<InvalidOperationException>(() => m.MoveAsync(item, Path.Combine(Files, "b.bin"), replace: false));

        Assert.Equal(before, item.FullPath);
        await m.ShutdownAsync();
    }

    [Fact]
    public async Task Existing_target_is_not_replaced_without_permission()
    {
        var m = NewManager();
        var item = Finished(m, "a.bin", seed: 1);
        var target = Path.Combine(Files, "b.bin");
        File.WriteAllBytes(target, Bytes(2));

        var error = await Assert.ThrowsAsync<IOException>(() => m.MoveAsync(item, target, replace: false));

        Assert.Equal($"A file named b.bin already exists in {Files}.", error.Message);
        Assert.Equal(Bytes(1), File.ReadAllBytes(Path.Combine(Files, "a.bin")));
        Assert.Equal(Bytes(2), File.ReadAllBytes(target));
        Assert.Equal("a.bin", item.FileName);

        await m.MoveAsync(item, target, replace: true);

        Assert.Equal(Bytes(1), File.ReadAllBytes(target));
        Assert.False(File.Exists(Path.Combine(Files, "a.bin")));
    }

    [Fact]
    public async Task Unfinished_move_onto_an_existing_file_replaces_it_only_when_done()
    {
        // Until the download finishes, the file it will replace stays as it is.
        var m = NewManager();
        var item = Unfinished(m, "a.bin", seed: 1);
        var target = Path.Combine(Files, "b.bin");
        File.WriteAllBytes(target, Bytes(2));

        await Assert.ThrowsAsync<IOException>(() => m.MoveAsync(item, target, replace: false));
        await m.MoveAsync(item, target, replace: true);

        Assert.Equal(Bytes(2), File.ReadAllBytes(target));
        Assert.Equal(Bytes(1), File.ReadAllBytes(target + ".admpart"));
        Assert.True(item.MayOverwrite);
    }

    [Fact]
    public async Task Failed_move_changes_nothing()
    {
        var m = NewManager();
        var item = Finished(m);
        var blocker = Path.Combine(_dir, "blocker");
        File.WriteAllText(blocker, "a file where the folder would go");

        await Assert.ThrowsAnyAsync<IOException>(() => m.MoveAsync(item, Path.Combine(blocker, "x.bin"), replace: false));

        Assert.Equal(Path.Combine(Files, "a.bin"), item.FullPath);
        Assert.Equal(Bytes(1), File.ReadAllBytes(item.FullPath));
    }

    [Fact]
    public async Task Failed_unfinished_move_puts_back_what_it_moved()
    {
        // An HLS download keeps a part file and a parts folder; if the second can't move, the first goes back.
        var m = NewManager();
        var item = m.Add(new DownloadRequest { Url = Unreachable + "v.m3u8", FileName = "v.ts", Kind = DownloadKind.Hls, Directory = Files }, startNow: false);
        m.Pause(item);
        Directory.CreateDirectory(Files);
        File.WriteAllBytes(item.PartPath, Bytes(1));
        Directory.CreateDirectory(item.FullPath + ".admparts");
        var target = Path.Combine(Files, "w.ts");
        File.WriteAllText(target + ".admparts", "a file where the parts folder would go");

        await Assert.ThrowsAnyAsync<IOException>(() => m.MoveAsync(item, target, replace: false));

        Assert.Equal(Bytes(1), File.ReadAllBytes(Path.Combine(Files, "v.ts.admpart")));
        Assert.True(Directory.Exists(Path.Combine(Files, "v.ts.admparts")));
        Assert.False(File.Exists(target + ".admpart"));
        Assert.Equal("v.ts", item.FileName);
    }
}
