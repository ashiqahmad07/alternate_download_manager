using ADM.Core;

namespace ADM.Core.Tests;

/// <summary>Moving to the new name loses nothing: the state and logs under the old one, and the progress in its part files.</summary>
public sealed class OldNameTests : IDisposable
{
    const int MB = 1024 * 1024;

    readonly string _dir = Path.Combine(Path.GetTempPath(), "adm-tests", Guid.NewGuid().ToString("N"));

    // Named for this test alone, so a real copy of the old app running on the machine doesn't matter here.
    readonly string _oldCopy = @"Local\ADM.Tests." + Guid.NewGuid().ToString("N");

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    string Old => Path.Combine(_dir, "FluxDM");
    string New => Path.Combine(_dir, "ADM");
    string Files => Path.Combine(_dir, "files");
    string Temp => Path.Combine(_dir, "temp");
    string StatePath => Path.Combine(New, "state.json");

    void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    void MoveState() => OldName.MoveState(Old, New, _oldCopy);

    DownloadItem NewItem(string url = "http://a.example/file.bin") =>
        new() { Url = url, Directory = Files, FileName = "file.bin", Status = DownloadStatus.Paused };

    /// <summary>Starts the app's manager on a saved list, as at the first start under the new name.</summary>
    DownloadManager Start(params DownloadItem[] items)
    {
        var state = new AppState();
        state.Downloads.AddRange(items);
        Store.Save(StatePath, state);
        return new DownloadManager(StatePath, new SynchronizationContext());
    }

    // ---- The state folder

    [Fact]
    public void The_state_and_logs_move_and_the_old_folder_goes()
    {
        Store.Save(Path.Combine(Old, "state.json"), new AppState { Downloads = { NewItem() } });
        Write(Path.Combine(Old, "state.json.bak"), "the copy before");
        Write(Path.Combine(Old, "logs", "fluxdm.log"), "a log");

        MoveState();

        Assert.Equal("http://a.example/file.bin", Assert.Single(Store.Load(StatePath).Downloads).Url);
        Assert.Equal("the copy before", File.ReadAllText(StatePath + ".bak"));
        Assert.Equal("a log", File.ReadAllText(Path.Combine(New, "logs", "fluxdm.log")));
        Assert.False(Directory.Exists(Old));
    }

    [Fact]
    public void A_backup_alone_moves_so_the_list_can_still_be_loaded_from_it()
    {
        Store.Save(Path.Combine(Old, "state.json.bak"), new AppState { Downloads = { NewItem() } });

        MoveState();

        Assert.Single(Store.Load(StatePath).Downloads);
    }

    [Fact]
    public void State_under_the_new_name_wins_and_the_old_stays_untouched()
    {
        Write(Path.Combine(Old, "state.json"), "old");
        Write(Path.Combine(Old, "state.json.bak"), "old backup");
        Write(StatePath, "new");

        MoveState();

        Assert.Equal("new", File.ReadAllText(StatePath));
        Assert.Equal("old", File.ReadAllText(Path.Combine(Old, "state.json")));
        // An old backup here would become what newer state falls back on.
        Assert.False(File.Exists(StatePath + ".bak"));
    }

    [Fact]
    public void A_log_the_native_host_started_first_does_not_stop_the_move()
    {
        // The browser can start the host before the user first starts the app.
        Write(Path.Combine(Old, "state.json"), "old state");
        Write(Path.Combine(Old, "logs", "host.log"), "old log");
        Write(Path.Combine(New, "logs", "host.log"), "new log");

        MoveState();

        Assert.Equal("old state", File.ReadAllText(StatePath));
        Assert.Equal("new log", File.ReadAllText(Path.Combine(New, "logs", "host.log")));
        Assert.Contains("old log", Directory.GetFiles(Path.Combine(New, "logs")).Select(File.ReadAllText));
        Assert.False(Directory.Exists(Old));
    }

    [Fact]
    public void A_move_cut_short_carries_on_at_the_next_start()
    {
        Write(Path.Combine(Old, "state.json"), "state");
        Write(Path.Combine(Old, "logs", "a.log"), "a");
        // As if the last start got this far, with the state still to go.
        Write(Path.Combine(New, "state.json.bak"), "moved already");

        MoveState();

        Assert.Equal("state", File.ReadAllText(StatePath));
        Assert.Equal("moved already", File.ReadAllText(StatePath + ".bak"));
        Assert.Equal("a", File.ReadAllText(Path.Combine(New, "logs", "a.log")));
    }

    [Fact]
    public void A_file_that_wont_move_leaves_the_state_behind_to_try_again()
    {
        Write(Path.Combine(Old, "state.json"), "state");
        var log = Path.Combine(Old, "logs", "a.log");
        Write(log, "a");

        using (new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Throws<IOException>(MoveState);

        Assert.False(File.Exists(StatePath));
        MoveState();
        Assert.Equal("state", File.ReadAllText(StatePath));
        Assert.Equal("a", File.ReadAllText(Path.Combine(New, "logs", "a.log")));
    }

    [Fact]
    public void Only_the_state_and_logs_move()
    {
        // The user's temporary folder could be in here, and part files are found by their folder.
        Write(Path.Combine(Old, "state.json"), "state");
        Write(Path.Combine(Old, "temp", "a.fluxpart"), "the user's");

        MoveState();

        Assert.Equal("the user's", File.ReadAllText(Path.Combine(Old, "temp", "a.fluxpart")));
        Assert.False(Directory.Exists(Path.Combine(New, "temp")));
    }

    [Fact]
    public void Without_an_old_folder_nothing_happens()
    {
        MoveState();

        Assert.False(Directory.Exists(New));
    }

    [Fact]
    public void A_running_old_copy_stops_the_move()
    {
        Write(Path.Combine(Old, "state.json"), "state");
        using var running = new Mutex(true, _oldCopy);

        Assert.Throws<IOException>(MoveState);

        Assert.True(File.Exists(Path.Combine(Old, "state.json")));
        Assert.False(File.Exists(StatePath));
    }

    [Fact]
    public void A_running_old_copy_does_not_matter_once_the_state_has_moved()
    {
        Write(Path.Combine(Old, "state.json"), "written since");
        Write(StatePath, "moved");
        using var running = new Mutex(true, _oldCopy);

        MoveState();

        Assert.Equal("moved", File.ReadAllText(StatePath));
    }

    // ---- Part files

    [Theory]
    [InlineData(".fluxpart", ".admpart")]
    [InlineData(".fluxremux", ".admremux")]
    public void A_part_file_beside_the_download_takes_the_new_name(string oldExtension, string newExtension)
    {
        var item = NewItem();
        Write(Path.Combine(Files, "file.bin" + oldExtension), "progress");

        Start(item);

        Assert.Equal("progress", File.ReadAllText(Path.Combine(Files, "file.bin" + newExtension)));
        Assert.False(File.Exists(Path.Combine(Files, "file.bin" + oldExtension)));
    }

    [Fact]
    public void A_part_file_in_a_temporary_folder_takes_the_new_name()
    {
        var item = NewItem();
        item.PartFolder = Temp;
        Write(Path.Combine(Temp, item.Id.ToString("N") + ".fluxpart"), "progress");

        Start(item);

        Assert.Equal("progress", File.ReadAllText(item.PartPath));
    }

    [Fact]
    public void An_HLS_parts_folder_takes_the_new_name()
    {
        var item = NewItem();
        Write(Path.Combine(Files, "file.bin.fluxparts", "0.ts"), "a segment");

        Start(item);

        Assert.Equal("a segment", File.ReadAllText(Path.Combine(item.PartsFolderPath, "0.ts")));
        Assert.False(Directory.Exists(Path.Combine(Files, "file.bin.fluxparts")));
    }

    [Fact]
    public void A_part_file_already_under_the_new_name_is_kept()
    {
        var item = NewItem();
        Write(Path.Combine(Files, "file.bin.fluxpart"), "old");
        Write(item.PartPath, "new");

        Start(item);

        Assert.Equal("new", File.ReadAllText(item.PartPath));
        Assert.Equal("old", File.ReadAllText(Path.Combine(Files, "file.bin.fluxpart")));
    }

    [Fact]
    public void A_finished_download_leaves_a_file_of_that_name_alone()
    {
        var item = NewItem();
        item.Status = DownloadStatus.Completed;
        Write(Path.Combine(Files, "file.bin.fluxpart"), "the user's");

        Start(item);

        Assert.True(File.Exists(Path.Combine(Files, "file.bin.fluxpart")));
    }

    [Fact]
    public void A_download_without_a_file_name_leaves_its_folder_alone()
    {
        var item = NewItem();
        item.FileName = "";
        // Without a file name the path is the folder's, and this is what a sibling of it would be called.
        Write(Files + ".fluxpart", "the user's");

        Start(item);

        Assert.True(File.Exists(Files + ".fluxpart"));
    }

    [Fact]
    public void Leftovers_in_the_temporary_folder_are_offered_for_deleting_under_their_new_names()
    {
        var part = Guid.NewGuid().ToString("N");
        var remux = Guid.NewGuid().ToString("N");
        var parts = Guid.NewGuid().ToString("N");
        Write(Path.Combine(Temp, part + ".fluxpart"), "");
        Write(Path.Combine(Temp, remux + ".fluxremux"), "");
        Write(Path.Combine(Temp, parts + ".fluxparts", "0.ts"), "");
        Write(Path.Combine(Temp, "notes.txt"), "the user's");
        var state = new AppState();
        state.Settings.TempFolder = Temp;
        Store.Save(StatePath, state);

        var orphans = new DownloadManager(StatePath, new SynchronizationContext()).OrphanedParts();

        var expected = new[] { part + ".admpart", remux + ".admremux", parts + ".admparts" }.Select(name => Path.Combine(Temp, name));
        Assert.Equal(expected.Select(Path.GetFullPath).Order(), orphans.Order());
        Assert.True(File.Exists(Path.Combine(Temp, "notes.txt")));
    }

    [Fact]
    public async Task A_download_carries_on_from_a_part_file_saved_under_the_old_name()
    {
        await using var server = await TestServer.StartAsync(4 * MB, TimeSpan.Zero);
        var saved = 2 * MB;
        var item = NewItem(server.ThrottledUrl);
        item.ResumeSupported = true;
        item.TotalBytes = server.Payload.Length;
        item.SegmentsSnapshot = new() { new Segment { Start = 0, End = server.Payload.Length - 1, Downloaded = saved } };
        Directory.CreateDirectory(Files);
        File.WriteAllBytes(Path.Combine(Files, "file.bin.fluxpart"), server.Payload[..saved]);

        var restored = Assert.Single(Start(item).Items);
        await new HttpDownloader(new RateLimiter(), new Settings(), (_, _) => { }).RunAsync(restored, CancellationToken.None);

        Assert.Equal(server.Payload, File.ReadAllBytes(restored.FullPath));
        // Starting over would ask from the first byte; carrying on asks from around where it stopped.
        var starts = server.Ranges("/throttled.bin")
            .Where(range => range.StartsWith("bytes=") && range != "bytes=0-0")
            .Select(range => long.Parse(range["bytes=".Length..range.IndexOf('-')]));
        Assert.NotEmpty(starts);
        Assert.All(starts, start => Assert.True(start >= saved - HttpDownloader.OverlapBytes, $"asked again from {start}"));
    }
}
