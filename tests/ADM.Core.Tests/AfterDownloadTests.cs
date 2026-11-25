using System.Diagnostics;
using System.Text.Json;
using ADM.Core;

namespace ADM.Core.Tests;

/// <summary>
/// What happens to a file once it has downloaded and before it counts as complete: the mark of the web, and the
/// virus scan, run with ADM.TestTool standing in for the scanner.
/// </summary>
/// <remarks>The stand-in takes its orders from environment variables, which is safe because a class's tests run one at a time.</remarks>
[Collection(nameof(SpeedTests))]
public sealed class AfterDownloadTests : IDisposable
{
    const string Referrer = "https://example.com/page";

    static readonly string[] ToolVariables = { "ADM_TOOL_LOG", "ADM_TOOL_WAIT", "ADM_TOOL_REMOVE", "ADM_TOOL_EXIT" };

    readonly string _dir = Path.Combine(Path.GetTempPath(), "adm-tests", Guid.NewGuid().ToString("N"));

    public AfterDownloadTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        foreach (var name in ToolVariables) Environment.SetEnvironmentVariable(name, null);
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    string Files => Path.Combine(_dir, "files");
    string StatePath => Path.Combine(_dir, "state.json");
    string Log => Path.Combine(_dir, "tool.json");

    static string ToolPath => Path.Combine(AppContext.BaseDirectory, "ADM.TestTool.exe");

    /// <summary>What the stand-in scanner was given and found.</summary>
    sealed record ToolRecord(string[] Args, int Pid, string[] Existing, string[] Marked);

    /// <summary>Tells the stand-in how to behave on its next run.</summary>
    void Tool(int exitCode = 0, int waitMs = 0, bool remove = false)
    {
        Environment.SetEnvironmentVariable("ADM_TOOL_LOG", Log);
        Environment.SetEnvironmentVariable("ADM_TOOL_WAIT", waitMs.ToString());
        Environment.SetEnvironmentVariable("ADM_TOOL_REMOVE", remove ? "1" : null);
        Environment.SetEnvironmentVariable("ADM_TOOL_EXIT", exitCode.ToString());
    }

    /// <summary>What the stand-in recorded; null until it has written it all.</summary>
    ToolRecord? Seen()
    {
        try
        {
            return JsonSerializer.Deserialize<ToolRecord>(File.ReadAllText(Log));
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            return null;
        }
    }

    bool ToolExited => File.Exists(Log + ".exited");

    DownloadManager NewManager(LockedContext ui, string? scanner = null, string arguments = "")
    {
        var m = new DownloadManager(StatePath, ui);
        m.State.Settings.DownloadRoot = Files;
        m.State.Settings.VirusScanner = scanner ?? "";
        m.State.Settings.VirusScannerArguments = arguments;
        return m;
    }

    DownloadRequest Request(TestServer server, string name = "file.bin") =>
        new() { Url = server.FileUrl, Referrer = Referrer, FileName = name, Directory = Files };

    /// <summary>Starts the download and waits until it completes or fails.</summary>
    static async Task<DownloadItem> DownloadAsync(DownloadManager m, LockedContext ui, DownloadRequest request)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        m.DownloadCompleted += _ => done.TrySetResult();
        m.DownloadFailed += _ => done.TrySetResult();
        DownloadItem item = null!;
        ui.Run(() => item = m.Add(request, startNow: true));
        await done.Task.WaitAsync(TimeSpan.FromSeconds(30));
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

    static string? ZoneText(string path)
    {
        try
        {
            return File.ReadAllText(path + ":Zone.Identifier");
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    static bool Exited(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    [Theory]
    [InlineData(DownloadKind.Http)]
    [InlineData(DownloadKind.Hls)]
    public async Task Finished_file_carries_the_mark_of_the_web(DownloadKind kind)
    {
        await using var server = await TestServer.StartAsync(64 * 1024, TimeSpan.Zero);
        var ui = new LockedContext();
        var m = NewManager(ui);
        string? atCompletion = null;
        m.DownloadCompleted += item => atCompletion = ZoneText(item.FullPath);
        var request = kind == DownloadKind.Hls
            ? new DownloadRequest { Url = server.HlsTsUrl, Referrer = Referrer, FileName = "video.ts", Directory = Files, Kind = DownloadKind.Hls }
            : Request(server);

        var item = await DownloadAsync(m, ui, request);

        Assert.Equal(DownloadStatus.Completed, item.Status);
        Assert.NotNull(atCompletion);
        var lines = atCompletion!.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("[ZoneTransfer]", lines[0]);
        Assert.Contains("ZoneId=3", lines);
        Assert.Contains("HostUrl=" + request.Url, lines);
        Assert.Contains("ReferrerUrl=" + Referrer, lines);
    }

    [Fact]
    public void Mark_leaves_out_the_login_in_the_address()
    {
        var path = Path.Combine(_dir, "a.bin");
        File.WriteAllBytes(path, new byte[16]);

        ZoneMark.Write(path, "http://user:secret@example.com:8080/a.bin?x=1", "https://me:hidden@example.com/page\r\nZoneId=0");

        var text = ZoneText(path)!;
        Assert.Contains("HostUrl=http://example.com:8080/a.bin?x=1\r\n", text);
        Assert.DoesNotContain("secret", text);
        Assert.DoesNotContain("hidden", text);
        Assert.DoesNotContain("user", text);
        // A line break in an address can't add a line of its own.
        Assert.Equal(1, text.Split("\r\n").Count(line => line.StartsWith("ZoneId=")));
        Assert.Contains("ZoneId=3", text);
    }

    [Fact]
    public async Task No_mark_when_turned_off()
    {
        await using var server = await TestServer.StartAsync(64 * 1024, TimeSpan.Zero);
        var item = new DownloadItem { Url = server.FileUrl, Referrer = Referrer, Directory = Files, FileName = "file.bin" };

        await new HttpDownloader(new RateLimiter(), new Settings { MarkDownloads = false }, (_, _) => { }).RunAsync(item, CancellationToken.None);

        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
        Assert.Null(ZoneText(item.FullPath));
    }

    [Fact]
    public async Task Scanner_runs_before_completion()
    {
        await using var server = await TestServer.StartAsync(64 * 1024, TimeSpan.Zero);
        Tool(waitMs: 700);
        var ui = new LockedContext();
        var m = NewManager(ui, ToolPath);
        bool? exitedFirst = null;
        m.DownloadCompleted += _ => exitedFirst = ToolExited;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        m.DownloadCompleted += _ => done.TrySetResult();
        m.DownloadFailed += i => done.TrySetException(new Exception(i.Error));
        DownloadItem item = null!;
        ui.Run(() => item = m.Add(Request(server), startNow: true));

        await Until(() => item.IsScanning);
        ui.Run(() =>
        {
            Assert.Equal(DownloadStatus.Downloading, item.Status);
            Assert.Equal("Scanning for viruses", item.StatusText);
            Assert.True(m.IsRunning(item));
        });
        await done.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(exitedFirst);
        var seen = Seen()!;
        Assert.Contains(item.FullPath, seen.Existing);
        // Marked before anything, the scanner included, could open it.
        Assert.Contains(item.FullPath, seen.Marked);
        Assert.False(item.IsScanning);
        Assert.Null(item.ScanResult);
        Assert.Equal("Complete", item.LastResultText);
    }

    [Fact]
    public async Task Landed_is_saved_before_the_scan_starts()
    {
        await using var server = await TestServer.StartAsync(64 * 1024, TimeSpan.Zero);
        Tool(waitMs: 700);
        var ui = new LockedContext();
        var m = NewManager(ui, ToolPath);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        m.DownloadCompleted += _ => done.TrySetResult();
        m.DownloadFailed += i => done.TrySetException(new Exception(i.Error));
        DownloadItem item = null!;
        ui.Run(() => item = m.Add(Request(server), startNow: true));

        await Until(() => item.IsScanning);
        // What a crash during the scan would leave.
        var saved = Assert.Single(Store.Load(StatePath).Downloads);
        Assert.True(saved.Landed);
        await done.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.False(item.Landed);
    }

    [Fact]
    public async Task Hash_comes_before_the_scan()
    {
        await using var server = await TestServer.StartAsync(64 * 1024, TimeSpan.Zero);
        Tool(waitMs: 300);
        var ui = new LockedContext();
        var m = NewManager(ui, ToolPath);
        var request = Request(server);
        request.Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(server.Payload));
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        m.DownloadCompleted += _ => done.TrySetResult();
        DownloadItem item = null!;
        ui.Run(() => item = m.Add(request, startNow: true));

        await Until(() => item.IsScanning);
        // The check is already over by the time the scan starts.
        Assert.False(item.IsHashing);
        Assert.Equal(StatusWords.Current.HashMatches, item.HashResult);
        await done.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(DownloadStatus.Completed, item.Status);
        Assert.Contains(item.FullPath, Seen()!.Existing);
    }

    [Fact]
    public async Task Pausing_the_check_completes_without_a_scan()
    {
        await using var server = await TestServer.StartAsync(64 * 1024, TimeSpan.Zero);
        Tool();
        var ui = new LockedContext();
        var m = NewManager(ui, ToolPath);
        m.Hasher = async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return "";
        };
        var request = Request(server);
        request.Sha256 = new string('a', 64);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        m.DownloadCompleted += _ => done.TrySetResult();
        DownloadItem item = null!;
        ui.Run(() => item = m.Add(request, startNow: true));

        await Until(() => item.IsHashing);
        ui.Run(() => m.Pause(item));
        await done.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(DownloadStatus.Completed, item.Status);
        Assert.Null(Seen());
    }

    [Fact]
    public async Task A_file_that_cannot_be_hashed_is_still_scanned()
    {
        await using var server = await TestServer.StartAsync(64 * 1024, TimeSpan.Zero);
        Tool();
        var ui = new LockedContext();
        var m = NewManager(ui, ToolPath);
        m.Hasher = (_, _) => throw new IOException("the disk went away");
        var request = Request(server);
        request.Sha256 = new string('a', 64);

        var item = await DownloadAsync(m, ui, request);

        Assert.Equal(DownloadStatus.Completed, item.Status);
        Assert.Equal(StatusWords.Current.HashNotChecked, item.HashResult);
        Assert.NotNull(Seen());
    }

    [Fact]
    public async Task A_file_that_fails_its_hash_is_not_scanned()
    {
        await using var server = await TestServer.StartAsync(64 * 1024, TimeSpan.Zero);
        Tool();
        var ui = new LockedContext();
        var m = NewManager(ui, ToolPath);
        var request = Request(server);
        request.Sha256 = new string('a', 64);

        var item = await DownloadAsync(m, ui, request);

        Assert.Equal(DownloadStatus.Failed, item.Status);
        Assert.Null(Seen());
    }

    [Fact]
    public async Task Scanner_gets_the_path_as_one_argument()
    {
        await using var server = await TestServer.StartAsync(16 * 1024, TimeSpan.Zero);
        Tool();
        var ui = new LockedContext();
        var m = NewManager(ui, ToolPath);
        // Names come from the server: a space, a shell's & and ^, a quote mark and a variable must all arrive as they are.
        const string name = "a b&c'd ^x %PATH%.bin";
        var request = Request(server, name);
        request.Directory = Path.Combine(_dir, "my files");

        var item = await DownloadAsync(m, ui, request);

        Assert.Equal(DownloadStatus.Completed, item.Status);
        Assert.Equal(name, item.FileName);
        Assert.Equal(new[] { item.FullPath }, Seen()!.Args);
    }

    [Fact]
    public async Task Arguments_template_places_the_file()
    {
        var file = Path.Combine(_dir, "my files", "a b.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file, new byte[16]);
        Tool();

        var outcome = await VirusScan.RunAsync(ToolPath, "--scan {file} --quiet", file, CancellationToken.None);
        Assert.Equal(new ScanOutcome(0, false, null), outcome);
        Assert.Equal(new[] { "--scan", file, "--quiet" }, Seen()!.Args);

        // A {file} the user put in quotes is still one argument.
        await VirusScan.RunAsync(ToolPath, "--scan \"{file}\"", file, CancellationToken.None);
        Assert.Equal(new[] { "--scan", file }, Seen()!.Args);

        // Without {file}, the file goes last.
        await VirusScan.RunAsync(ToolPath, "--scan", file, CancellationToken.None);
        Assert.Equal(new[] { "--scan", file }, Seen()!.Args);
    }

    [Fact]
    public async Task Scanner_finding_marks_the_download()
    {
        await using var server = await TestServer.StartAsync(16 * 1024, TimeSpan.Zero);
        Tool(exitCode: 2);
        var ui = new LockedContext();
        var m = NewManager(ui, ToolPath);

        var item = await DownloadAsync(m, ui, Request(server));

        Assert.Equal(DownloadStatus.Completed, item.Status);
        Assert.Equal("The virus scanner reported a problem (exit code 2)", item.ScanResult);
        Assert.Equal(item.ScanResult, item.LastResultText);
        Assert.Equal(item.ScanResult, item.StatusText);
        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));

        ui.Run(m.Save);
        var reloaded = Assert.Single(new DownloadManager(StatePath, new LockedContext()).Items);
        Assert.Equal(item.ScanResult, reloaded.ScanResult);
    }

    [Fact]
    public async Task Scanner_removing_the_file_fails_the_download()
    {
        await using var server = await TestServer.StartAsync(16 * 1024, TimeSpan.Zero);
        Tool(exitCode: 2, remove: true);
        var ui = new LockedContext();
        var m = NewManager(ui, ToolPath);
        var completed = false;
        m.DownloadCompleted += _ => completed = true;

        var item = await DownloadAsync(m, ui, Request(server));

        Assert.False(item.Landed);
        Assert.Equal(DownloadStatus.Failed, item.Status);
        Assert.Equal("The virus scanner removed the file", item.Error);
        Assert.False(completed);
        Assert.False(File.Exists(item.FullPath));
    }

    [Fact]
    public async Task Missing_scanner_still_completes_with_a_note()
    {
        await using var server = await TestServer.StartAsync(16 * 1024, TimeSpan.Zero);
        var ui = new LockedContext();
        var m = NewManager(ui, Path.Combine(_dir, "no such scanner.exe"));

        var item = await DownloadAsync(m, ui, Request(server));

        Assert.Equal(DownloadStatus.Completed, item.Status);
        Assert.StartsWith("The virus scanner could not start: ", item.ScanResult);
        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
    }

    [Fact]
    public void Known_scanner_gets_its_arguments()
    {
        const string defender = "-Scan -ScanType 3 -File {file}";
        Assert.Equal(defender, VirusScan.KnownArguments(@"C:\Program Files\Windows Defender\MpCmdRun.exe"));
        Assert.Equal(defender, VirusScan.KnownArguments(@"C:\ProgramData\Microsoft\Windows Defender\Platform\4.18.24090.11-0\MPCMDRUN.EXE"));
        Assert.Null(VirusScan.KnownArguments(@"C:\Tools\other.exe"));
        Assert.Null(VirusScan.KnownArguments(""));
    }

    [Fact]
    public async Task Batch_scanner_is_never_given_a_name_cmd_would_expand()
    {
        // cmd.exe runs a batch file, and expands %...% even inside quotes, which could close them.
        var ran = Path.Combine(_dir, "ran.txt");
        var batch = Path.Combine(_dir, "scan.cmd");
        File.WriteAllText(batch, $"@echo ran> \"{ran}\"\r\n");
        var plain = Path.Combine(_dir, "plain.bin");
        var risky = Path.Combine(_dir, "a%CMDCMDLINE:~-1%&echo x.bin");
        File.WriteAllBytes(plain, new byte[16]);
        File.WriteAllBytes(risky, new byte[16]);

        var refused = await VirusScan.RunAsync(batch, "", risky, CancellationToken.None);
        Assert.Null(refused.ExitCode);
        Assert.NotNull(refused.StartError);
        Assert.False(File.Exists(ran));

        var allowed = await VirusScan.RunAsync(batch, "", plain, CancellationToken.None);
        Assert.Equal(0, allowed.ExitCode);
        Assert.True(File.Exists(ran));
    }

    [Fact]
    public async Task Deleting_the_download_stops_the_scanner()
    {
        await using var server = await TestServer.StartAsync(16 * 1024, TimeSpan.Zero);
        Tool(waitMs: 60_000);
        var ui = new LockedContext();
        var m = NewManager(ui, ToolPath);
        var events = 0;
        m.DownloadCompleted += _ => Interlocked.Increment(ref events);
        m.DownloadFailed += _ => Interlocked.Increment(ref events);
        DownloadItem item = null!;
        ui.Run(() => item = m.Add(Request(server), startNow: true));
        await Until(() => Seen() != null);

        Task remove = null!;
        ui.Run(() => remove = m.RemoveAsync(item, deleteFile: false));
        await remove.WaitAsync(TimeSpan.FromSeconds(10));

        await Until(() => Exited(Seen()!.Pid));
        Assert.Empty(m.Items);
        await Task.Delay(200);
        Assert.Equal(0, events);
    }

    [Fact]
    public async Task Exit_stops_the_scanner_and_keeps_the_download_complete()
    {
        await using var server = await TestServer.StartAsync(16 * 1024, TimeSpan.Zero);
        Tool(waitMs: 60_000);
        var ui = new LockedContext();
        var m = NewManager(ui, ToolPath);
        var completed = false;
        m.DownloadCompleted += _ => completed = true;
        DownloadItem item = null!;
        ui.Run(() => item = m.Add(Request(server), startNow: true));
        await Until(() => Seen() != null);

        // The UI thread stays busy until the exit has saved, so the stopped scan's own callback can't run first;
        // the exit alone has to leave the download complete.
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var busy = new Thread(() =>
        {
            lock (ui.Gate)
            {
                held.Set();
                release.Wait();
            }
        });
        busy.Start();
        held.Wait();
        try
        {
            await m.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            release.Set();
        }

        await Until(() => Exited(Seen()!.Pid));
        await Task.Delay(200);
        Assert.False(completed);
        var reloaded = Assert.Single(new DownloadManager(StatePath, new LockedContext()).Items);
        Assert.Equal(DownloadStatus.Completed, reloaded.Status);
        Assert.Equal("The virus scan was stopped before it finished", reloaded.ScanResult);
        Assert.Equal(server.Payload, File.ReadAllBytes(reloaded.FullPath));
    }

    [Fact]
    public async Task Pausing_a_scan_stops_it_and_completes_the_download()
    {
        await using var server = await TestServer.StartAsync(16 * 1024, TimeSpan.Zero);
        Tool(waitMs: 60_000);
        var ui = new LockedContext();
        var m = NewManager(ui, ToolPath);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        m.DownloadCompleted += _ => done.TrySetResult();
        DownloadItem item = null!;
        ui.Run(() => item = m.Add(Request(server), startNow: true));
        await Until(() => Seen() != null);

        ui.Run(() => m.Pause(item));
        await done.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await Until(() => Exited(Seen()!.Pid));
        Assert.Equal(DownloadStatus.Completed, item.Status);
        Assert.Equal("The virus scan was stopped before it finished", item.ScanResult);
        Assert.Equal(server.Payload, File.ReadAllBytes(item.FullPath));
    }

    [Fact]
    public async Task Queue_finishes_only_after_the_scan()
    {
        await using var server = await TestServer.StartAsync(16 * 1024, TimeSpan.Zero);
        Tool(waitMs: 700);
        var ui = new LockedContext();
        var m = NewManager(ui, ToolPath);
        bool? exitedFirst = null;
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        m.QueueFinished += _ =>
        {
            exitedFirst = ToolExited;
            finished.TrySetResult();
        };
        DownloadItem item = null!;
        ui.Run(() =>
        {
            item = m.Add(Request(server), startNow: false);
            m.StartQueue(m.MainQueue);
        });

        await finished.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(exitedFirst);
        Assert.Equal(DownloadStatus.Completed, item.Status);
    }

    // A file taken out of an archive meets the same scanner as a download, under its own name.

    static readonly byte[] ZipContent = "inside the archive"u8.ToArray();

    Settings ScanningSettings(string? scanner = null) => new() { VirusScanner = scanner ?? ToolPath };

    string Extracted => Path.Combine(_dir, "out");

    static DownloadItem Remote(string url) => new() { Url = url, FileName = "archive.zip" };

    [Fact]
    public async Task A_file_taken_out_of_an_archive_is_scanned_marked_and_named()
    {
        await using var server = await TestServer.StartAsync(new ZipBuilder().Add("a.txt", ZipContent).Build(), TimeSpan.Zero);
        Tool();
        var entries = await ZipPreview.ListAsync(Remote(server.FileUrl), new Settings(), CancellationToken.None);

        var result = Assert.Single(await ZipPreview.ExtractAsync(Remote(server.FileUrl), ScanningSettings(), entries, Extracted, null, CancellationToken.None));

        var landed = Path.Combine(Extracted, "a.txt");
        Assert.Equal(landed, result.Path);
        Assert.Null(result.Problem);
        Assert.Null(result.ScanResult);
        var seen = Seen();
        Assert.NotNull(seen);
        Assert.Equal(new[] { landed }, seen.Existing);
        Assert.Equal(new[] { landed }, seen.Marked);
    }

    [Fact]
    public async Task A_finding_on_a_file_taken_out_leaves_it_and_says_so()
    {
        await using var server = await TestServer.StartAsync(new ZipBuilder().Add("a.txt", ZipContent).Build(), TimeSpan.Zero);
        Tool(exitCode: 2);
        var entries = await ZipPreview.ListAsync(Remote(server.FileUrl), new Settings(), CancellationToken.None);

        var result = Assert.Single(await ZipPreview.ExtractAsync(Remote(server.FileUrl), ScanningSettings(), entries, Extracted, null, CancellationToken.None));

        Assert.Equal("The virus scanner reported a problem (exit code 2)", result.ScanResult);
        Assert.Null(result.Problem);
        Assert.Equal(ZipContent, File.ReadAllBytes(result.Path!));
    }

    [Fact]
    public async Task A_file_the_scanner_removes_is_left_out()
    {
        await using var server = await TestServer.StartAsync(new ZipBuilder().Add("a.txt", ZipContent).Add("b.txt", ZipContent).Build(), TimeSpan.Zero);
        Tool(remove: true);
        var entries = await ZipPreview.ListAsync(Remote(server.FileUrl), new Settings(), CancellationToken.None);

        var results = await ZipPreview.ExtractAsync(Remote(server.FileUrl), ScanningSettings(), entries, Extracted, null, CancellationToken.None);

        Assert.All(results, result =>
        {
            Assert.Null(result.Path);
            Assert.Equal(VirusScan.RemovedFile, result.Problem);
        });
    }

    [Fact]
    public async Task A_scanner_that_will_not_start_leaves_the_file_and_says_so()
    {
        await using var server = await TestServer.StartAsync(new ZipBuilder().Add("a.txt", ZipContent).Build(), TimeSpan.Zero);
        var entries = await ZipPreview.ListAsync(Remote(server.FileUrl), new Settings(), CancellationToken.None);

        var result = Assert.Single(await ZipPreview.ExtractAsync(Remote(server.FileUrl), ScanningSettings(Path.Combine(_dir, "no-such-scanner.exe")), entries, Extracted, null, CancellationToken.None));

        Assert.StartsWith("The virus scanner could not start: ", result.ScanResult);
        Assert.True(File.Exists(result.Path));
    }

    [Fact]
    public async Task A_folder_taken_out_of_an_archive_is_not_scanned()
    {
        await using var server = await TestServer.StartAsync(new ZipBuilder().Add("f/", Array.Empty<byte>()).Build(), TimeSpan.Zero);
        Tool();
        var entries = await ZipPreview.ListAsync(Remote(server.FileUrl), new Settings(), CancellationToken.None);

        var result = Assert.Single(await ZipPreview.ExtractAsync(Remote(server.FileUrl), ScanningSettings(), entries, Extracted, null, CancellationToken.None));

        Assert.True(Directory.Exists(result.Path));
        Assert.Null(Seen());
    }

    [Fact]
    public async Task No_scanner_chosen_means_no_scan_of_a_file_taken_out()
    {
        await using var server = await TestServer.StartAsync(new ZipBuilder().Add("a.txt", ZipContent).Build(), TimeSpan.Zero);
        Tool();
        var entries = await ZipPreview.ListAsync(Remote(server.FileUrl), new Settings(), CancellationToken.None);

        var result = Assert.Single(await ZipPreview.ExtractAsync(Remote(server.FileUrl), new Settings(), entries, Extracted, null, CancellationToken.None));

        Assert.True(File.Exists(result.Path));
        Assert.Null(Seen());
    }

    [Fact]
    public async Task Stopping_a_scan_takes_the_unchecked_file_with_it()
    {
        await using var server = await TestServer.StartAsync(new ZipBuilder().Add("a.txt", ZipContent).Build(), TimeSpan.Zero);
        Tool(waitMs: 30000);
        var entries = await ZipPreview.ListAsync(Remote(server.FileUrl), new Settings(), CancellationToken.None);
        using var stop = new CancellationTokenSource();

        var taking = ZipPreview.ExtractAsync(Remote(server.FileUrl), ScanningSettings(), entries, Extracted, null, stop.Token);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (Seen() == null)
        {
            Assert.True(DateTime.UtcNow < deadline, "the scanner didn't start");
            await Task.Delay(20);
        }
        stop.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => taking);
        Assert.Empty(Directory.GetFiles(Extracted));
    }
}
