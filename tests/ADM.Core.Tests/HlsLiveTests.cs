using ADM.Core;

namespace ADM.Core.Tests;

[Collection(nameof(SpeedTests))]
public sealed class HlsLiveTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "adm-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    DownloadItem NewItem(string url) =>
        new() { Url = url, Directory = _dir, FileName = "live.ts", Kind = DownloadKind.Hls, MaxConnections = 4 };

    // No ffmpeg, and a quick reload instead of the playlist's one-second target duration.
    static HlsDownloader Downloader() => new(new RateLimiter(), new Settings(), null) { LivePoll = TimeSpan.FromMilliseconds(50) };

    static byte[] Recording(long from, long to) =>
        Enumerable.Range((int)from, (int)(to - from + 1)).SelectMany(s => TestServer.LivePart(s)).ToArray();

    static async Task WaitUntil(Func<bool> condition)
    {
        for (var deadline = DateTime.UtcNow.AddSeconds(15); !condition(); await Task.Delay(20))
            if (DateTime.UtcNow > deadline) throw new TimeoutException();
    }

    /// <summary>The recording on disk must be segments 0, 1, 2... each once; returns how many.</summary>
    static int AssertContiguousFromZero(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var count = bytes.Length / 4096;
        Assert.Equal(Recording(0, count - 1), bytes);
        return count;
    }

    [Fact]
    public async Task Live_recording_keeps_each_segment_once_in_order()
    {
        await using var server = await TestServer.StartAsync(1024, TimeSpan.Zero);
        var item = NewItem(server.HlsLiveUrl(end: 8));

        await Downloader().RunAsync(item, CancellationToken.None);

        Assert.True(item.IsLive);
        Assert.Equal("live.ts", item.FileName);
        Assert.Equal(Recording(0, 7), File.ReadAllBytes(item.FullPath));
        Assert.False(Directory.Exists(item.FullPath + ".admparts"));
    }

    // ---- Separate audio

    static byte[] AudioRecording(long from, long to) =>
        Enumerable.Range((int)from, (int)(to - from + 1)).SelectMany(s => TestServer.LiveAudioPart(s)).ToArray();

    /// <summary>A stand-in for ffmpeg that copies its first -i input to its last argument, and records its arguments.</summary>
    string FakeFfmpeg()
    {
        var path = Path.Combine(_dir, "tools", "ffmpeg.cmd");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """
            @echo off
            setlocal
            echo %* > "%~dp0args.txt"
            :next
            if "%~2"=="" goto last
            if "%~1"=="-i" if not defined source set "source=%~2"
            shift
            goto next
            :last
            copy /y "%source%" "%~1" >nul
            """);
        return path;
    }

    [Fact]
    public async Task Live_stream_with_separate_audio_records_both()
    {
        await using var server = await TestServer.StartAsync(1024, TimeSpan.Zero);
        var item = NewItem(server.HlsLiveAvUrl(end: 8));

        await Downloader().RunAsync(item, CancellationToken.None);

        Assert.True(item.IsLive);
        Assert.Equal(Recording(0, 7), File.ReadAllBytes(item.FullPath));
        Assert.Equal(AudioRecording(0, 7), File.ReadAllBytes(Path.Combine(_dir, "live (audio).ts")));
    }

    [Fact]
    public async Task Live_stream_with_separate_audio_is_joined_by_ffmpeg()
    {
        await using var server = await TestServer.StartAsync(1024, TimeSpan.Zero);
        var item = NewItem(server.HlsLiveAvUrl(end: 8));
        var ffmpeg = FakeFfmpeg();

        await new HlsDownloader(new RateLimiter(), new Settings(), ffmpeg) { LivePoll = TimeSpan.FromMilliseconds(20) }.RunAsync(item, CancellationToken.None);

        Assert.Equal("live.mp4", item.FileName);
        var args = File.ReadAllText(Path.Combine(_dir, "tools", "args.txt"));
        Assert.Contains("audio.joined", args);
        Assert.False(File.Exists(Path.Combine(_dir, "live (audio).ts")));
    }

    [Fact]
    public async Task Stop_and_save_keeps_both_tracks()
    {
        await using var server = await TestServer.StartAsync(1024, TimeSpan.Zero);
        var item = NewItem(server.HlsLiveAvUrl());
        using var stop = new CancellationTokenSource();

        var run = Downloader().RunAsync(item, CancellationToken.None, stop.Token);
        await WaitUntil(() => item.HlsSegmentsDone >= 8);
        stop.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(15));

        AssertContiguousFromZero(item.FullPath);
        var audio = File.ReadAllBytes(Path.Combine(_dir, "live (audio).ts"));
        Assert.Equal(AudioRecording(0, audio.Length / 2048 - 1), audio);
    }

    [Fact]
    public async Task Stopped_while_paused_keeps_both_tracks()
    {
        await using var server = await TestServer.StartAsync(1024, TimeSpan.Zero);
        var item = NewItem(server.HlsLiveAvUrl());
        using (var pause = new CancellationTokenSource())
        {
            var running = Downloader().RunAsync(item, pause.Token);
            await WaitUntil(() => item.HlsSegmentsDone >= 8);
            pause.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(TimeSpan.FromSeconds(15)));
        }

        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Downloader().RunAsync(item, CancellationToken.None, stop.Token);

        AssertContiguousFromZero(item.FullPath);
        Assert.Single(Directory.GetFiles(_dir, "live (audio).*"));
    }

    [Fact]
    public async Task Live_audio_part_that_keeps_failing_ends_the_recording_with_its_error()
    {
        // As a video part's failure does, and the parts stay for the download to carry on from.
        await using var server = await TestServer.StartAsync(1024, TimeSpan.Zero);
        server.LiveAudioFailFrom = 4;
        var item = NewItem(server.HlsLiveAvUrl());
        var hls = new HlsDownloader(new RateLimiter(), new Settings(), null)
        {
            LivePoll = TimeSpan.FromMilliseconds(20),
            Retry = new RetryPolicy { FirstBackoff = TimeSpan.FromMilliseconds(20), MaxBackoff = TimeSpan.FromMilliseconds(20) },
        };

        var e = await Assert.ThrowsAsync<HttpRequestException>(() => hls.RunAsync(item, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20)));

        Assert.Equal(System.Net.HttpStatusCode.InternalServerError, e.StatusCode);
        Assert.True(Directory.Exists(item.PartsFolderPath));
    }

    [Fact]
    public async Task Stop_and_save_ends_a_running_recording()
    {
        await using var server = await TestServer.StartAsync(1024, TimeSpan.Zero);
        var item = NewItem(server.HlsLiveUrl());
        using var stop = new CancellationTokenSource();

        var run = Downloader().RunAsync(item, CancellationToken.None, stop.Token);
        await WaitUntil(() => item.HlsSegmentsDone >= 5);
        stop.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.True(AssertContiguousFromZero(item.FullPath) >= 5);
    }

    [Fact]
    public async Task Stopping_a_paused_recording_saves_it_without_recording_more()
    {
        await using var server = await TestServer.StartAsync(1024, TimeSpan.Zero);
        var item = NewItem(server.HlsLiveUrl());
        using var pause = new CancellationTokenSource();
        var run = Downloader().RunAsync(item, pause.Token);
        await WaitUntil(() => item.HlsSegmentsDone >= 4);
        pause.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        var requests = server.HlsLiveRequests;

        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Downloader().RunAsync(item, CancellationToken.None, stop.Token);

        Assert.Equal(requests, server.HlsLiveRequests);
        Assert.True(AssertContiguousFromZero(item.FullPath) >= 4);
    }

    [Fact]
    public async Task Manager_stop_and_save_completes_a_recording()
    {
        await using var server = await TestServer.StartAsync(1024, TimeSpan.Zero);
        var m = new DownloadManager(Path.Combine(_dir, "state.json"), new SynchronizationContext());
        m.State.Settings.DownloadRoot = _dir;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        m.DownloadCompleted += _ => done.TrySetResult();
        m.DownloadFailed += i => done.TrySetException(new Exception(i.Error));

        var item = m.Add(new DownloadRequest { Url = server.HlsLiveUrl(), Kind = DownloadKind.Hls, FileName = "live.ts" }, startNow: true);
        await WaitUntil(() => item.HlsSegmentsDone >= 3);
        Assert.Equal("Recording", item.StatusText);
        m.StopAndSave(item);
        await done.Task.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(DownloadStatus.Completed, item.Status);
        Assert.True(AssertContiguousFromZero(item.FullPath) >= 3);
    }
}
