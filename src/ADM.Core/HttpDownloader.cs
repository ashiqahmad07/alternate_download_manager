using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;

namespace ADM.Core;

/// <param name="FinalUrl">The address the redirects ended on; a run's segments ask it directly.</param>
public sealed record ProbeResult(
    long Total, bool RangeSupported, string FileName, string? ETag, string? LastModified, string? MediaType, string? FinalUrl = null);

/// <summary>
/// Multi-connection HTTP downloader with dynamic segmentation: every free connection either resumes an
/// idle segment or splits off part of the one expected to finish last, sized by the connections' speeds
/// so both parts end together. Connections stay busy until the end instead of idling behind a slow one.
/// </summary>
public sealed class HttpDownloader
{
    const long MinSplitBytes = 512 * 1024;
    /// <summary>The smallest split taken from a connection running at less than half the median speed.</summary>
    const long MinSlowSplitBytes = 64 * 1024;
    const int BufferSize = 64 * 1024;
    const int MaxRetries = 8;
    /// <summary>
    /// A request continuing a partly downloaded segment starts this much early, and those bytes are checked
    /// against the part file: a mismatch means the server's copy changed without saying so.
    /// </summary>
    internal const int OverlapBytes = 4096;
    /// <summary>How often a connection with nothing to take looks again while others still work.</summary>
    static readonly TimeSpan IdlePoll = TimeSpan.FromMilliseconds(50);
    /// <summary>How long a connection receives before it can be judged too slow to wait for, so a new one's slow start doesn't count against it.</summary>
    static readonly TimeSpan TakeoverAfter = TimeSpan.FromSeconds(1);

    /// <summary>How much of a file a sync check reads, from a server with nothing to compare.</summary>
    const int ComparedBytes = 64 * 1024;

    /// <summary>How many times a piece passes on from a connection that isn't receiving.</summary>
    const int MaxHandOvers = 3;

    readonly RateLimiter _limiter;
    readonly Settings _settings;
    readonly Action<DownloadItem, ProbeResult> _onProbed;
    HttpClient _client;
    ProxySettings _proxy;

    public HttpDownloader(RateLimiter limiter, Settings settings, Action<DownloadItem, ProbeResult> onProbed)
    {
        _limiter = limiter;
        _settings = settings;
        // The proxy in effect when the download starts serves it to the end.
        _client = Net.ClientFor(settings);
        _proxy = settings.Proxy;
        _onProbed = onProbed;
        Retry = RetryPolicy.For(settings);
    }

    /// <summary>The waits between tries, and how long to keep reconnecting through an outage.</summary>
    internal RetryPolicy Retry { get; init; }

    /// <summary>How often progress is made durable while the transfer runs; tests shorten it.</summary>
    internal TimeSpan CheckpointInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Opens the part file; tests substitute one that loses whatever was not flushed.</summary>
    internal Func<string, PartFile> OpenPartFile { get; init; } = path => new PartFile(path);

    /// <summary>The bytes this user may still write in a folder; tests substitute their own.</summary>
    internal Func<string, long> FreeSpace { get; init; } = PartFile.FreeSpaceIn;

    /// <summary>Moves the complete part file to the download's name (source, target, overwrite); tests make it fail.</summary>
    internal Action<string, string, bool> MoveIntoPlace { get; init; } = File.Move;

    public static async Task<ProbeResult> ProbeAsync(DownloadItem item, Settings settings, CancellationToken ct)
    {
        using var source = SegmentSources.For(item, settings, Net.ClientFor(settings), settings.Proxy);
        return await RetryPolicy.For(settings).AnswerAsync(source.ProbeAsync, ct);
    }

    /// <summary>
    /// For a sync queue: whether the server's copy differs from the finished download. A different size,
    /// or a different ETag or Last-Modified where both sides have one, counts; so does a missing local file.
    /// A server that gives nothing to compare has the start of its copy read instead.
    /// </summary>
    public static Task<bool> HasChangedAsync(DownloadItem item, Settings settings, CancellationToken ct) =>
        HasChangedAsync(item, settings, ct, RetryPolicy.For(settings));

    internal static async Task<bool> HasChangedAsync(DownloadItem item, Settings settings, CancellationToken ct, RetryPolicy retry)
    {
        if (!File.Exists(item.FullPath)) return true;
        using var source = SegmentSources.For(item, settings, Net.ClientFor(settings), settings.Proxy);
        var probe = await retry.AnswerAsync(source.ProbeAsync, ct);
        if (probe.Total <= 0 && probe.ETag == null && probe.LastModified == null) return !await SameStartAsync(item, source, retry, ct);
        return ServerCopyDiffers(probe.Total, probe.ETag, probe.LastModified, item.TotalBytes, item.ETag, item.LastModified, settings);
    }

    /// <summary>
    /// Whether what a server says of its copy shows it differs from the one saved: its size, its ETag, or its
    /// Last-Modified unless <see cref="Settings.IgnoreModificationTime"/>. What either side doesn't know isn't compared.
    /// </summary>
    public static bool ServerCopyDiffers(long serverSize, string? serverETag, string? serverModified, long savedSize, string? savedETag, string? savedModified, Settings settings)
    {
        return (serverSize > 0 && savedSize >= 0 && serverSize != savedSize)
            || Differs(serverETag, savedETag)
            || (!settings.IgnoreModificationTime && Differs(serverModified, savedModified));

        static bool Differs(string? server, string? saved) => server != null && saved != null && server != saved;
    }

    /// <summary>
    /// Whether the server's copy starts as the file on disk does. It is all a server with nothing to compare
    /// leaves to go on short of downloading the file, so a change only later in the file goes unnoticed.
    /// </summary>
    static async Task<bool> SameStartAsync(DownloadItem item, ISegmentSource source, RetryPolicy retry, CancellationToken ct)
    {
        var (_, first) = await retry.AnswerAsync(source.OpenAsync, ct);
        using (first)
        {
            await using var file = File.OpenRead(item.FullPath);
            var theirs = await ReadStartAsync(first.Stream, ct);
            var ours = await ReadStartAsync(file, ct);
            return theirs.AsSpan().SequenceEqual(ours);
        }
    }

    static async Task<byte[]> ReadStartAsync(Stream stream, CancellationToken ct)
    {
        var start = new byte[ComparedBytes];
        using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        int have = 0;
        while (have < start.Length)
        {
            readTimeout.CancelAfter(Net.ReadTimeout);
            int read = await stream.ReadAsync(start.AsMemory(have), readTimeout.Token);
            if (read == 0) break;
            have += read;
        }
        return start[..have];
    }

    public async Task RunAsync(DownloadItem item, CancellationToken ct)
    {
        try
        {
            try
            {
                await RunOnceAsync(item, ct);
            }
            catch (FileChangedException)
            {
                // The server's copy changed without its size, ETag or Last-Modified showing it: start over, once.
                lock (item.SyncRoot) item.Segments.Clear();
                item.DownloadedBytes = 0;
                TryDelete(item.PartPath);
                item.StatusNote = StatusWords.Current.FileChanged;
                try
                {
                    await RunOnceAsync(item, ct);
                }
                catch (FileChangedException)
                {
                    throw new IOException("The file keeps changing on the server.");
                }
            }
        }
        finally
        {
            item.StatusNote = null;
        }
    }

    async Task RunOnceAsync(DownloadItem item, CancellationToken ct)
    {
        bool resuming = item.Segments.Count > 0;
        if (resuming && HasEveryByte(item))
        {
            // Everything arrived before, but the file couldn't be moved into place: only the move is left.
            await item.UntilSettledAsync(ct);
            Directory.CreateDirectory(item.Directory);
            Finish(item);
            return;
        }

        var asked = Stopwatch.GetTimestamp();
        var (source, probe, first) = await FirstRequestThroughOutageAsync(item, resuming, ct);
        using var ownedSource = source;
        using var run = new Run(item, source, first) { FirstAnswer = Stopwatch.GetElapsedTime(asked) };

        if (resuming && !CanResume(item, probe, _settings))
        {
            // The server copy changed or cannot serve ranges: start over.
            lock (item.SyncRoot) item.Segments.Clear();
            item.DownloadedBytes = 0;
            TryDelete(item.PartPath);
            resuming = false;
        }

        if (!resuming)
        {
            item.ResumeSupported = probe.RangeSupported && probe.Total > 0;
            item.ResumeKnown = true;
            item.TotalBytes = probe.Total;
            item.ETag = probe.ETag;
            item.LastModified = probe.LastModified;
            // A new part file goes where the settings say now; one already made stays where it is.
            item.PartFolder = _settings.NewPartFolder() ?? item.OwnPartFolder;
            _onProbed(item, probe);
            lock (item.SyncRoot)
                item.Segments.Add(new Segment { Start = 0, End = probe.Total > 0 ? probe.Total - 1 : long.MaxValue });
        }

        Directory.CreateDirectory(item.Directory);
        if (item.PartFolder != null) Directory.CreateDirectory(item.PartFolder);
        item.DownloadedBytes = item.SegmentsSnapshot.Sum(s => s.Downloaded);
        // Before any part file is made or any byte transferred.
        // What the part file already holds, not what has been downloaded: one from before part files were sparse holds it all.
        EnsureSpace(item.PartFolder ?? item.Directory, item.TotalBytes - PartFile.AllocatedBytes(item.PartPath));
        if (!resuming) ReservePartFile(item);

        using (var file = OpenPartFile(item.PartPath))
        {
            if (item.TotalBytes > 0 && file.Length != item.TotalBytes)
                file.SetLength(item.TotalBytes);

            int connections = item.ResumeSupported ? Math.Clamp(ConnectionsFor(item, run.Source.Address), 1, 32) : 1;
            await RunWithCheckpointsAsync(run, file, connections, ct);

            lock (item.SyncRoot)
            {
                if (item.Segments.Any(s => !s.IsComplete))
                    throw new IOException("Download ended with missing byte ranges.");
                // An unknown size is only learned at end of stream.
                if (item.TotalBytes <= 0) item.TotalBytes = item.Segments.Sum(s => s.Downloaded);
            }
            // Drops any stale tail left by an earlier, longer attempt.
            if (file.Length != item.TotalBytes) file.SetLength(item.TotalBytes);
        }
        await item.UntilSettledAsync(ct);
        // Started early, it may have been given another folder while it ran.
        Directory.CreateDirectory(item.Directory);
        Finish(item);
    }

    /// <summary>
    /// A run's first request, which waits out an outage as the requests after it do. Its quick tries and the
    /// browser's proxy come first, so an answer that is there to be had doesn't wait for the window.
    /// </summary>
    async Task<(ISegmentSource Source, ProbeResult Probe, SegmentBody? First)> FirstRequestThroughOutageAsync(DownloadItem item, bool resuming, CancellationToken ct)
    {
        int reconnects = 0;
        DateTime? giveUpAt = null;
        string? trouble = null;
        while (true)
        {
            var reconnect = item.ReconnectToken;
            var tried = DateTime.UtcNow;
            try
            {
                var answer = await FirstRequestAsync(item, resuming, ct);
                // The browser's proxy leaves a note of its own.
                if (item.StatusNote == trouble) item.StatusNote = null;
                if (reconnects > 0) Log.Info($"Download {item.Id} reconnected");
                return answer;
            }
            catch (Exception e) when (IsOutage(item, e, ct))
            {
                if (reconnects == 0) giveUpAt = DateTime.UtcNow + Retry.ReconnectWindow;
                // With no network, the quick tries don't count against the window any more than the wait does.
                if (!Retry.NetworkAvailable()) giveUpAt += DateTime.UtcNow - tried;
                if (DateTime.UtcNow >= giveUpAt) throw;
                if (reconnects == 0) Log.Info($"Download {item.Id} waiting to reconnect to {Log.Host(item.Url)}: {Log.Reason(e)}");
                giveUpAt = await Retry.WaitToReconnectAsync(reconnects++, giveUpAt,
                    note => item.StatusNote = trouble = e.Message == PacProxy.LoadFailed ? StatusWords.Current.ScriptNotLoaded : note,
                    wait => RetryPolicy.WaitOrWakeAsync(wait, reconnect, ct));
            }
        }
    }

    bool IsOutage(DownloadItem item, Exception e, CancellationToken ct)
    {
        using var source = SegmentSources.For(item, _settings, _client, _proxy);
        return source.IsConnectionFailure(e, ct) && !Retry.NoSuchName(e);
    }

    /// <summary>
    /// A run's first request, with its retries. If it fails in a way the browser's proxy may get round, it is
    /// tried once more through that, which then serves the rest of the run. If that fails too, the download's
    /// own error stands, and its own route is what any next try starts from.
    /// </summary>
    async Task<(ISegmentSource Source, ProbeResult Probe, SegmentBody? First)> FirstRequestAsync(DownloadItem item, bool resuming, CancellationToken ct)
    {
        try
        {
            return await AskAsync(item, resuming, ct);
        }
        catch (Exception e) when (BrowserProxy.RouteAfter(e, item, _settings, _proxy, ct) is { } route)
        {
            var (proxy, client) = (_proxy, _client);
            // Once on the browser's proxy, this no longer matches: a run switches at most once.
            _proxy = route;
            _client = Net.ClientFor(route);
            item.StatusNote = StatusWords.Current.UsingBrowserProxy;
            try
            {
                return await AskAsync(item, resuming, ct);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                (_proxy, _client) = (proxy, client);
                item.StatusNote = null;
                ExceptionDispatchInfo.Throw(e);
                throw;
            }
        }
    }

    /// <summary>A resume only asks about the file; a fresh start keeps the probe's body as its first bytes.</summary>
    async Task<(ISegmentSource Source, ProbeResult Probe, SegmentBody? First)> AskAsync(DownloadItem item, bool resuming, CancellationToken ct)
    {
        var source = SegmentSources.For(item, _settings, _client, _proxy);
        try
        {
            if (resuming) return (source, await Retry.WithRetryAsync(source, () => Retry.AnswerAsync(source.ProbeAsync, ct), ct), null);
            var (probe, first) = await Retry.WithRetryAsync(source, () => Retry.AnswerAsync(source.OpenAsync, ct), ct);
            return (source, probe, first);
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    /// <summary>Whether a previous run left every byte in the part file, so nothing is left to fetch.</summary>
    static bool HasEveryByte(DownloadItem item)
    {
        lock (item.SyncRoot)
            if (item.TotalBytes <= 0 || item.Segments.Any(s => !s.IsComplete)) return false;
        var part = new FileInfo(item.PartPath);
        return part.Exists && part.Length == item.TotalBytes;
    }

    /// <summary>
    /// Moves the complete part file to the download's name. The progress is cleared only once the file is
    /// there, so a move that fails is tried again without downloading anything.
    /// </summary>
    void Finish(DownloadItem item)
    {
        // Sparse only while downloading: the finished file is an ordinary one.
        using (var handle = File.OpenHandle(item.PartPath, FileMode.Open, FileAccess.ReadWrite))
            PartFile.TrySetSparse(handle, false);

        var overwrite = item.MayOverwrite;
        var final = overwrite ? item.FullPath : FileNames.Unique(item.FullPath);
        if (!SameDrive(item.PartPath, final))
        {
            // From a temporary folder on another drive, the move is a copy.
            EnsureSpace(item.Directory, item.TotalBytes, " to move the file into place");
            item.StatusNote = StatusWords.Current.MovingIntoPlace;
        }
        MoveIntoPlace(item.PartPath, final, overwrite);
        // While the download still runs, so whatever waits for it to complete only ever sees a marked file.
        if (_settings.MarkDownloads) ZoneMark.Write(final, item.Url, item.Referrer);
        lock (item.SyncRoot) item.Segments.Clear();
        item.FileName = Path.GetFileName(final);
        item.Landed = true;
    }

    static bool SameDrive(string a, string b) =>
        string.Equals(Path.GetPathRoot(Path.GetFullPath(a)), Path.GetPathRoot(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

    /// <summary>The connections set for this download, and no more than a server that refused extra ones lately took.</summary>
    int ConnectionsFor(DownloadItem item, string url)
    {
        var connections = _settings.ConnectionsFor(item.Url, item.MaxConnections);
        return Uri.TryCreate(url, UriKind.Absolute, out var server) && Net.LearnedLimit(server) is int learned
            ? Math.Min(connections, learned)
            : connections;
    }

    /// <summary>
    /// Creates a new part file, renaming the download to "name (n).ext" while the name is
    /// taken, so two downloads of the same file name never write into one part file.
    /// A download allowed to replace exactly this file keeps its name unless another download
    /// holds the part file. In a temporary folder the part file is this download's own, so
    /// one left over from an earlier attempt is replaced.
    /// </summary>
    static void ReservePartFile(DownloadItem item)
    {
        var stem = Path.GetFileNameWithoutExtension(item.FileName);
        var extension = Path.GetExtension(item.FileName);
        var mode = item.PartFolder == null ? FileMode.CreateNew : FileMode.Create;
        for (int n = 1; ; n++)
        {
            if (item.MayOverwrite || !File.Exists(item.FullPath))
            {
                try
                {
                    // Sparse before it has a length: Windows then doesn't write zeros ahead of a connection
                    // writing far into the file, and no byte goes to disk twice.
                    using (var created = File.Open(item.PartPath, mode))
                        PartFile.TrySetSparse(created.SafeFileHandle, true);
                    return;
                }
                catch (IOException) when (mode == FileMode.CreateNew && File.Exists(item.PartPath))
                {
                }
            }
            item.FileName = $"{stem} ({n}){extension}";
        }
    }

    /// <summary>Fails when <paramref name="needed"/> bytes don't fit in the free space on the folder's drive.</summary>
    void EnsureSpace(string folder, long needed, string purpose = "")
    {
        if (needed > 0) PartFile.EnsureSpace(folder, needed, FreeSpace(folder), purpose);
    }

    static bool CanResume(DownloadItem item, ProbeResult probe, Settings settings) =>
        item.ResumeSupported
        && probe.RangeSupported
        && probe.Total == item.TotalBytes
        && (item.ETag == null || probe.ETag == null || item.ETag == probe.ETag)
        && (settings.IgnoreModificationTime || item.LastModified == null || probe.LastModified == null || item.LastModified == probe.LastModified);

    /// <summary>
    /// Runs the workers and records checkpoints: every <see cref="CheckpointInterval"/>, and once more when
    /// they stop for any reason. Afterwards the item's progress is the last checkpoint, so it never claims
    /// a byte that is not on disk.
    /// </summary>
    async Task RunWithCheckpointsAsync(Run run, PartFile file, int connections, CancellationToken ct)
    {
        var item = run.Item;
        // What is already counted was on disk before this run, or this run starts from nothing.
        lock (item.SyncRoot) item.Checkpoint = item.Segments.Select(s => s.Clone()).ToList();
        using var stop = new CancellationTokenSource();
        var periodic = CheckpointEveryAsync(item, file, stop.Token);
        bool finished = false;
        try
        {
            await RunWorkersAsync(run, file, connections, ct);
            finished = true;
        }
        finally
        {
            stop.Cancel();
            await periodic;
            try
            {
                Checkpoint(item, file);
            }
            catch (Exception) when (!finished)
            {
                // The transfer failed or was paused anyway; its last checkpoint stands.
            }
            finally
            {
                lock (item.SyncRoot)
                {
                    item.Segments = item.Checkpoint!;
                    item.Checkpoint = null;
                    item.DownloadedBytes = item.Segments.Sum(s => s.Downloaded);
                }
            }
        }
    }

    async Task CheckpointEveryAsync(DownloadItem item, PartFile file, CancellationToken stop)
    {
        try
        {
            while (true)
            {
                await Task.Delay(CheckpointInterval, stop);
                try
                {
                    Checkpoint(item, file);
                }
                catch (IOException)
                {
                    // The last checkpoint stands; a failing disk fails the writes as well.
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Copies the progress, flushes the part file, and only then publishes the copy as the one saves write.</summary>
    static void Checkpoint(DownloadItem item, PartFile file)
    {
        List<Segment> progress;
        lock (item.SyncRoot) progress = item.Segments.Select(s => s.Clone()).ToList();
        file.Flush();
        lock (item.SyncRoot) item.Checkpoint = progress;
    }

    async Task RunWorkersAsync(Run run, PartFile file, int connections, CancellationToken ct)
    {
        using var failure = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Exception? fatal = null;

        var workers = Enumerable.Range(0, connections).Select(_ => Task.Run(async () =>
        {
            try
            {
                await WorkerAsync(run, file, failure.Token);
            }
            catch (Exception e)
            {
                Interlocked.CompareExchange(ref fatal, e, null);
                failure.Cancel();
            }
        }));
        await Task.WhenAll(workers);

        // A server that turned connections away gets, for a while, only as many as worked.
        if (run.Refused > 0 && Uri.TryCreate(run.Source.Address, UriKind.Absolute, out var server))
            Net.LearnLimit(server, run.PeakReceiving);

        ct.ThrowIfCancellationRequested();
        if (fatal != null) ExceptionDispatchInfo.Throw(fatal);
    }

    async Task WorkerAsync(Run run, PartFile file, CancellationToken ct)
    {
        var item = run.Item;
        var buffer = new byte[BufferSize];
        while (true)
        {
            var segment = Claim(item, Stopwatch.GetTimestamp(), run.SetupSeconds, run.UnansweredAfter(Retry), out var cut);
            // Its connection ends now rather than hold up the end until its read times out.
            if (cut != null) try { cut.Cancel(); } catch (ObjectDisposedException) { }
            if (segment == null)
            {
                // Nothing to take yet; while others work, one of them may slow down enough to be worth sharing.
                if (!StillWorking(item)) return;
                await Task.Delay(IdlePoll, ct);
                continue;
            }

            bool kept;
            try
            {
                kept = await DownloadSegmentWithRetryAsync(run, segment, file, buffer, ct);
            }
            finally
            {
                lock (item.SyncRoot) segment.Active = false;
            }
            if (!kept) return;
        }
    }

    static bool StillWorking(DownloadItem item)
    {
        lock (item.SyncRoot) return item.ResumeSupported && item.Segments.Any(s => s.Active && !s.IsComplete);
    }

    /// <summary>
    /// Hands the caller an idle segment, or splits off part of the one expected to finish last; null when
    /// nothing is worth sharing. The owner keeps what it should finish by the time the new connection, at the
    /// median speed, finishes the rest. A connection that isn't receiving, or has gone silent, hands over all it
    /// has left, and so, where nothing can be split, does one that would take more than twice as long as a fresh
    /// connection; <paramref name="cut"/> is then its request or its wait, for the caller to end.
    /// </summary>
    /// <param name="setupSeconds">How long a new request takes to be answered.</param>
    /// <param name="unansweredAfter">How long a request goes unanswered before it is no longer worth waiting for.</param>
    static Segment? Claim(DownloadItem item, long now, double setupSeconds, double unansweredAfter, out CancellationTokenSource? cut)
    {
        cut = null;
        lock (item.SyncRoot)
        {
            var idle = item.Segments.FirstOrDefault(s => !s.Active && !s.IsComplete);
            if (idle != null)
            {
                idle.Active = true;
                return idle;
            }
            if (!item.ResumeSupported) return null;

            // Waiting to try again, or asked and not answered, it receives nothing. Only so many times per piece,
            // as each connection starts its tries afresh, and a range that always fails must run out of them.
            var stuck = item.Segments.FirstOrDefault(s => s.Active && !s.IsComplete && s.HandOvers < MaxHandOvers
                && (s.IsWaiting || s.SecondsAsking(now) >= unansweredAfter));
            if (stuck != null) return TakeOver(item, stuck, out cut);

            var speeds = item.Segments.Select(s => s.SpeedAt(now)).Where(v => v > 0).Order().ToList();
            double median = speeds.Count == 0 ? 0 : speeds[speeds.Count / 2];
            // Expected to finish last: remaining bytes over the connection's speed. A segment with no speed
            // yet counts by its size, as if at the median; a silent one never finishes.
            var candidates = item.Segments
                .Where(s => s.Active && !s.IsComplete)
                .Select(s => (Owner: s, Speed: s.SpeedAt(now), Watched: s.SecondsConnected(now) >= TakeoverAfter.TotalSeconds))
                .OrderByDescending(c => c.Speed == 0 && c.Watched
                    ? double.PositiveInfinity
                    : c.Owner.Remaining / (c.Speed > 0 ? c.Speed : median > 0 ? median : 1))
                .ToList();
            foreach (var (owner, speed, watched) in candidates)
            {
                // Silent long enough that it's no slow start; nothing is gained by waiting for any of its bytes.
                if (speed == 0 && watched && median > 0) return TakeOver(item, owner, out cut);

                long remaining = owner.Remaining;
                long keep = speed > 0 && median > 0 ? (long)(remaining * speed / (speed + median)) : remaining / 2;
                // At least one buffer beyond the owner's position, so the read it has in flight is not wasted.
                keep = Math.Max(keep, BufferSize);
                long smallest = speed > 0 && speed < median / 2 ? MinSlowSplitBytes : MinSplitBytes;
                // Rather than a piece under the smallest size, the owner keeps less, but never under one buffer.
                keep = Math.Min(keep, remaining - smallest);
                if (keep < BufferSize) continue;

                long at = owner.Position + keep;
                var created = new Segment { Start = at, End = owner.End, Active = true };
                owner.End = at - 1;
                item.Segments.Add(created);
                return created;
            }

            // Nothing is big enough to split: a slow connection hands over all it has left if a fresh one would
            // take less than half as long. Not while it is checking: its speed so far says little, and a fresh
            // one would have to start the same check again.
            foreach (var (owner, speed, watched) in candidates)
                if (watched && speed > 0 && owner.Checking == 0 && owner.Remaining / speed > 2 * (setupSeconds + owner.Remaining / median))
                    return TakeOver(item, owner, out cut);
            return null;
        }
    }

    /// <summary>
    /// Moves all <paramref name="owner"/> has left to a new segment. Bytes it has read but not yet counted are
    /// the new segment's to count; <paramref name="cut"/> is its request, for the caller to end.
    /// </summary>
    static Segment TakeOver(DownloadItem item, Segment owner, out CancellationTokenSource? cut)
    {
        var created = new Segment
        {
            Start = owner.Position,
            End = owner.End,
            Active = true,
            HandOvers = owner.HandOvers + 1,
            // What its next try would have checked. Starting fresh, the new connection has nothing of its own to compare.
            CheckOwed = Math.Max(owner.Checking, owner.IsWaiting ? (int)Math.Min(OverlapBytes, owner.Downloaded) : 0),
        };
        owner.End = owner.Position - 1;
        cut = owner.Request;
        item.Segments.Add(created);
        return created;
    }

    /// <returns>
    /// False when the connection retired instead: the server refused it while another connection of this
    /// download was receiving, and its segment goes back to the others.
    /// </returns>
    /// <remarks>
    /// A try that got no HTTP answer at all is tried again for the reconnect window, which any progress
    /// starts afresh; HTTP errors get <see cref="MaxRetries"/> retries.
    /// </remarks>
    async Task<bool> DownloadSegmentWithRetryAsync(Run run, Segment segment, PartFile file, byte[] buffer, CancellationToken ct)
    {
        var item = run.Item;
        int attempt = 0, reconnects = 0;
        DateTime? giveUpAt = DateTime.UtcNow + Retry.ReconnectWindow;
        while (true)
        {
            long before;
            lock (item.SyncRoot) before = segment.Downloaded;
            var reconnect = item.ReconnectToken;
            try
            {
                await DownloadSegmentAsync(run, segment, file, buffer, reconnect, ct);
                return true;
            }
            catch (Exception e) when (run.Source.IsRefusal(e) && run.Receiving > 0)
            {
                run.CountRefusal();
                return false;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && Finished(item, segment))
            {
                // Another connection took over all this one had left, and ended its request.
                return true;
            }
            catch (OperationCanceledException) when (reconnect.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                // Reconnect now, as after sleep or a network change: straight back, without using up a try.
            }
            catch (Exception e) when (run.Source.IsConnectionFailure(e, ct))
            {
                if (Progressed(item, segment, before))
                {
                    giveUpAt = DateTime.UtcNow + Retry.ReconnectWindow;
                    reconnects = 0;
                }
                if (DateTime.UtcNow >= giveUpAt) throw;
                giveUpAt = await Retry.WaitToReconnectAsync(reconnects++, giveUpAt, run.ConnectionTrouble,
                    wait => WaitToRetryAsync(item, segment, wait, reconnect, ct));
            }
            catch (Exception e) when (run.Source.IsTransient(e, ct) && attempt < MaxRetries)
            {
                if (Progressed(item, segment, before)) attempt = 0;
                var wait = Retry.Backoff(attempt++);
                await WaitToRetryAsync(item, segment, token => Task.Delay(wait, token), default, ct);
            }
        }
    }

    static bool Progressed(DownloadItem item, Segment segment, long before)
    {
        lock (item.SyncRoot) return segment.Downloaded > before;
    }

    static bool Finished(DownloadItem item, Segment segment)
    {
        lock (item.SyncRoot) return segment.IsComplete;
    }

    /// <summary>
    /// A connection's wait before it tries again. A free connection can take over the segment meanwhile, and
    /// <paramref name="reconnect"/>, the failed try's signal from <see cref="DownloadManager.Reconnect"/>,
    /// ends the wait too; either way the caller goes straight round.
    /// </summary>
    static async Task WaitToRetryAsync(DownloadItem item, Segment segment, Func<CancellationToken, Task> wait, CancellationToken reconnect, CancellationToken ct)
    {
        using var wake = CancellationTokenSource.CreateLinkedTokenSource(ct, reconnect);
        lock (item.SyncRoot) segment.Waiting(wake);
        try
        {
            await wait(wake.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
        }
        finally
        {
            lock (item.SyncRoot) segment.StopWaiting();
        }
    }

    /// <param name="reconnect">Cancelled by <see cref="DownloadManager.Reconnect"/>: ends this try's request and reads at once.</param>
    async Task DownloadSegmentAsync(Run run, Segment segment, PartFile file, byte[] buffer, CancellationToken reconnect, CancellationToken ct)
    {
        var item = run.Item;
        long from, to;
        int overlap;
        lock (item.SyncRoot)
        {
            if (segment.IsComplete) return;
            if (!item.ResumeSupported && segment.Downloaded > 0)
            {
                // Without range support a retry restarts the body from byte zero.
                item.AddDownloaded(-segment.Downloaded);
                segment.Downloaded = 0;
            }
            from = segment.Position;
            to = segment.End;
            // A continuation asks again for the end of what it has, to check the server still has the same file.
            overlap = item.ResumeSupported ? Math.Max(segment.CheckOwed, (int)Math.Min(OverlapBytes, segment.Downloaded)) : 0;
            segment.Checking = overlap;
        }

        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct, reconnect);
        // The probe's connection, receiving since the run began, carries on as the first segment.
        var opened = run.TakeFirstBody(segment);
        bool receiving = opened != null;
        if (opened != null) to = item.TotalBytes > 0 ? item.TotalBytes - 1 : long.MaxValue;
        if (opened == null)
        {
            var asked = Stopwatch.GetTimestamp();
            lock (item.SyncRoot) segment.Asked(asked, attempt);
            try
            {
                var (start, end) = (from - overlap, to);
                opened = await Retry.AnswerAsync(token => run.Source.OpenRangeAsync(start, end, token), attempt.Token);
            }
            catch
            {
                lock (item.SyncRoot) segment.Disconnected(Stopwatch.GetTimestamp());
                throw;
            }
            run.Answered(Stopwatch.GetElapsedTime(asked));
        }
        try
        {
            if (!receiving) run.StartReceiving();
            receiving = true;
            run.ConnectionOk();
            lock (item.SyncRoot) segment.Connected(Stopwatch.GetTimestamp(), attempt);

            var body = opened.Stream;
            using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(attempt.Token);
            if (overlap > 0)
            {
                await CheckOverlapAsync(run.Item, segment, body, file, from - overlap, overlap, readTimeout, ct);
                lock (item.SyncRoot) (segment.Checking, segment.CheckOwed) = (0, 0);
            }
            while (true)
            {
                int want;
                lock (item.SyncRoot)
                {
                    var remaining = segment.Remaining;
                    if (remaining <= 0) break;
                    want = (int)Math.Min(buffer.Length, remaining);
                }

                readTimeout.CancelAfter(Net.ReadTimeout);
                int read = await body.ReadAsync(buffer.AsMemory(0, want), readTimeout.Token);
                if (read == 0)
                {
                    if (segment.End != long.MaxValue) throw EndedEarly();
                    lock (item.SyncRoot) segment.End = segment.Position - 1;
                    break;
                }

                await _limiter.WaitAsync(read, ct);
                await item.Limiter.WaitAsync(read, ct);

                long offset;
                lock (item.SyncRoot)
                {
                    // Another connection may have taken over the tail of this range meanwhile.
                    offset = segment.Position;
                    var remaining = segment.Remaining;
                    if (remaining <= 0) break;
                    if (read > remaining) read = (int)remaining;
                }
                await file.WriteAsync(offset, buffer.AsMemory(0, read), ct);
                lock (item.SyncRoot)
                {
                    // Taken over meanwhile, these bytes are the other connection's to count; they are the same bytes.
                    read = (int)Math.Clamp(segment.Remaining, 0, read);
                    segment.Downloaded += read;
                    segment.Received(read, Stopwatch.GetTimestamp());
                }
                item.AddDownloaded(read);
            }
        }
        finally
        {
            if (receiving) run.StopReceiving();
            lock (item.SyncRoot) segment.Disconnected(Stopwatch.GetTimestamp());
            opened.Dispose();
        }

        // Cut short by a split, the request asked for more than was read. The server may not yet have noticed
        // the connection closing, and asking again at once could count as one connection too many. An FTP
        // connection is closed after every piece, so there the source says how long to wait every time.
        bool cutShort;
        lock (item.SyncRoot) cutShort = to != long.MaxValue && segment.End < to;
        var settle = run.Source.CloseSettleTime;
        if (cutShort && settle < IdlePoll) settle = IdlePoll;
        if (settle > TimeSpan.Zero && StillWorking(item)) await Task.Delay(settle, ct);
    }

    static HttpIOException EndedEarly() => new(HttpRequestError.ResponseEnded, "Connection closed before the segment finished.");

    /// <summary>
    /// Reads the overlap a continuation asked for again and compares it with the part file. Bytes that
    /// differ mean the server's copy changed without its size, ETag or Last-Modified showing it. They count
    /// towards the connection's speed, so that a slow check isn't taken for a silent connection.
    /// </summary>
    static async Task CheckOverlapAsync(DownloadItem item, Segment segment, Stream body, PartFile file, long offset, int count, CancellationTokenSource readTimeout, CancellationToken ct)
    {
        var received = new byte[count];
        for (int have = 0; have < count;)
        {
            readTimeout.CancelAfter(Net.ReadTimeout);
            int arrived = await body.ReadAsync(received.AsMemory(have), readTimeout.Token);
            if (arrived == 0) throw EndedEarly();
            have += arrived;
            lock (item.SyncRoot) segment.Received(arrived, Stopwatch.GetTimestamp());
        }

        var saved = new byte[count];
        int read = 0;
        while (read < count)
        {
            int n = await file.ReadAsync(offset + read, saved.AsMemory(read), ct);
            if (n == 0) break;
            read += n;
        }
        if (read < count || !received.AsSpan().SequenceEqual(saved)) throw new FileChangedException();
    }

    /// <summary>The server's copy no longer matches the bytes already downloaded.</summary>
    sealed class FileChangedException() : Exception("The file changed on the server.");

    static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>What the connections of one run share.</summary>
    sealed class Run : IDisposable
    {
        SegmentBody? _first;
        int _receiving, _peak, _refused;
        long _setupTicks;

        public Run(DownloadItem item, ISegmentSource source, SegmentBody? first)
        {
            Item = item;
            Source = source;
            _first = first;
            // The probe's connection is receiving the first segment's bytes already.
            if (first != null) StartReceiving();
        }

        public DownloadItem Item { get; }

        /// <summary>Where this run's bytes come from. The run that made it disposes it.</summary>
        public ISegmentSource Source { get; }

        /// <summary>Connections receiving data now.</summary>
        public int Receiving => Volatile.Read(ref _receiving);

        /// <summary>The most connections that were receiving at once.</summary>
        public int PeakReceiving => Volatile.Read(ref _peak);

        public int Refused => Volatile.Read(ref _refused);

        /// <summary>The probe's body, for the connection that takes the first segment from its start.</summary>
        public SegmentBody? TakeFirstBody(Segment segment) =>
            segment.Start == 0 && segment.Downloaded == 0 ? Interlocked.Exchange(ref _first, null) : null;

        public void StartReceiving()
        {
            int now = Interlocked.Increment(ref _receiving), peak;
            while (now > (peak = Volatile.Read(ref _peak)) && Interlocked.CompareExchange(ref _peak, now, peak) != peak) { }
        }

        public void StopReceiving() => Interlocked.Decrement(ref _receiving);

        /// <summary>How long the run's first request took to be answered.</summary>
        public TimeSpan FirstAnswer { get; init; }

        /// <summary>When a request is no longer worth waiting for. A server that is merely slow to answer keeps its requests.</summary>
        public double UnansweredAfter(RetryPolicy retry) =>
            Math.Max(retry.UnansweredAfter.TotalSeconds, 3 * Math.Max(FirstAnswer.TotalSeconds, SetupSeconds));

        /// <summary>The longest any request of this run took to be answered: what a fresh connection costs before its first byte.</summary>
        public double SetupSeconds => TimeSpan.FromTicks(Volatile.Read(ref _setupTicks)).TotalSeconds;

        public void Answered(TimeSpan took)
        {
            long ticks = took.Ticks, seen;
            while (ticks > (seen = Volatile.Read(ref _setupTicks)) && Interlocked.CompareExchange(ref _setupTicks, ticks, seen) != seen) { }
        }

        public void CountRefusal() => Interlocked.Increment(ref _refused);

        /// <summary>Shows why a connection is waiting, until data flows again.</summary>
        public void ConnectionTrouble(string note)
        {
            // Once for the outage, however many connections it reaches and however many tries it takes.
            if (Interlocked.Exchange(ref _trouble, note) == null) Log.Info($"Download {Item.Id} waiting to reconnect to {Log.Host(Item.Url)}");
            Item.StatusNote = note;
        }

        /// <summary>Data flows again, so a note about connection trouble goes; others stay.</summary>
        public void ConnectionOk()
        {
            if (Interlocked.Exchange(ref _trouble, null) is not { } note) return;
            Log.Info($"Download {Item.Id} reconnected");
            if (Item.StatusNote == note) Item.StatusNote = null;
        }

        string? _trouble;

        public void Dispose()
        {
            // Never taken: the run ended before a connection started on the first segment.
            Interlocked.Exchange(ref _first, null)?.Dispose();
        }
    }
}
