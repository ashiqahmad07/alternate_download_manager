using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Text;

namespace ADM.Core;

/// <summary>The part of an address's file that a #EXT-X-BYTERANGE or a DASH manifest names. A sync check's fingerprint holds what this prints, so it stays as it is.</summary>
public sealed record ByteRange(long Offset, long Length);

/// <summary>
/// Downloads an HLS stream (RFC 8216): picks a variant from the master playlist, fetches
/// media segments in parallel, decrypts AES-128 segments and joins them in order. ffmpeg joins a
/// separate audio track to the video; without it the audio is saved next to the video. Subtitles
/// are saved next to the video as WebVTT. A live stream is recorded until it ends or is stopped.
/// SAMPLE-AES and other DRM schemes are refused. A static DASH stream goes the same way once its manifest has
/// been read: only <see cref="LoadDashAsync"/> knows it isn't HLS.
/// </summary>
public sealed class HlsDownloader
{
    sealed record HlsKey(Uri Uri, byte[]? Iv);
    sealed record HlsPart(int Index, Uri Uri, long Sequence, HlsKey? Key, bool IsInit = false, ByteRange? Range = null);

    /// <summary>
    /// One media playlist. The main track's parts sit in the work folder itself, the audio track's in a
    /// subfolder. Without #EXT-X-ENDLIST it is live and reloaded every target duration. FragmentedMp4 says it
    /// needs no repackaging, which is also true of WebM; DASH gives the Extension its container calls for.
    /// </summary>
    sealed record HlsTrack(string Folder, List<HlsPart> Parts, bool FragmentedMp4, Uri Playlist, bool Ended, double TargetDuration, string? Extension = null);

    /// <param name="Output">The container the result should be, which names the file and decides ffmpeg's; null is MP4.</param>
    sealed record HlsStream(HlsTrack Main, HlsTrack? Audio, HlsRendition? Subtitles, string? Output = null);

    const int BufferSize = 64 * 1024;

    readonly RateLimiter _limiter;
    readonly Settings _settings;
    readonly string? _ffmpeg;
    readonly YtDlp? _reader;
    readonly ProxySettings _proxy;
    HttpClient _client;
    // The headers yt-dlp named for a site's files, sent with every request for them.
    IReadOnlyDictionary<string, string>? _headers;

    public HlsDownloader(RateLimiter limiter, Settings settings) : this(limiter, settings, FindFfmpeg(), VideoTools.Current.Reader()) { }

    /// <param name="ffmpeg">The ffmpeg that remuxes to MP4 and joins audio to video; null leaves the parts as they are.</param>
    /// <param name="reader">The yt-dlp that reads a site's page; null can't download one.</param>
    internal HlsDownloader(RateLimiter limiter, Settings settings, string? ffmpeg, YtDlp? reader = null)
    {
        _limiter = limiter;
        _settings = settings;
        // The proxy in effect when the download starts serves it to the end.
        _client = Net.ClientFor(settings);
        _proxy = settings.Proxy;
        _ffmpeg = ffmpeg;
        _reader = reader;
        Retry = RetryPolicy.For(settings);
    }

    /// <summary>The waits between tries, and how long to keep reconnecting through an outage.</summary>
    internal RetryPolicy Retry { get; init; }

    /// <summary>The ffmpeg ADM fetched into its tools folder, else one on <c>PATH</c>.</summary>
    public static string? FindFfmpeg() => VideoTools.Current.FfmpegPath;

    // The note to go back to once an outage is over.
    string? _note;
    int _inOutage;
    CancellationToken _stopLive;

    /// <summary>How often a live playlist is reloaded; null follows its target duration. Tests shorten it.</summary>
    internal TimeSpan? LivePoll { get; init; }

    /// <summary>The longest a site's video waits for its files to be served; tests shorten it.</summary>
    internal TimeSpan LongestSiteWait { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Opens a part being written; tests substitute one that loses whatever was not flushed.</summary>
    internal Func<string, PartFile> OpenPartFile { get; init; } = path => new PartFile(path);

    /// <summary>
    /// For a sync queue: whether the playlists now list other parts than the download was made from. A
    /// download from before the fingerprint counts as changed, once.
    /// </summary>
    public static async Task<bool> HasChangedAsync(DownloadItem item, Settings settings, CancellationToken ct)
    {
        if (!File.Exists(item.FullPath) || item.HlsFingerprint == null) return true;
        var hls = new HlsDownloader(new RateLimiter(), settings, null);
        return Fingerprint(await hls.LoadStreamAsync(item, ct)) != item.HlsFingerprint;
    }

    /// <summary>
    /// The parts' and keys' addresses in order, without their queries, which often hold a token that changes
    /// on every load, and the parts' byte ranges, as several can share an address.
    /// </summary>
    static string Fingerprint(HlsStream stream)
    {
        var addresses = new StringBuilder();
        foreach (var track in new[] { stream.Main, stream.Audio }.OfType<HlsTrack>())
            foreach (var part in track.Parts)
                addresses.Append(part.Uri.GetLeftPart(UriPartial.Path)).Append('\n').Append(part.Range).Append('\n').Append(part.Key?.Uri.GetLeftPart(UriPartial.Path)).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(addresses.ToString())));
    }

    /// <param name="stopLive">
    /// Ends a live recording and saves what it has; a paused recording is saved without recording more.
    /// Complete playlists ignore it.
    /// </param>
    public async Task RunAsync(DownloadItem item, CancellationToken ct, CancellationToken stopLive = default)
    {
        try
        {
            await RunOnceAsync(item, ct, stopLive);
        }
        finally
        {
            item.StatusNote = null;
            item.StreamRun?.Rest();
        }
    }

    async Task RunOnceAsync(DownloadItem item, CancellationToken ct, CancellationToken stopLive)
    {
        var workDir = item.PartsFolderPath;
        if (item.IsLive && item.HlsSegmentCount > 0 && stopLive.IsCancellationRequested && Directory.Exists(workDir))
        {
            // Stopped while paused: the stream may be gone, so the parts are saved without the network.
            var parts = RecordedParts(workDir, new Uri(item.Url));
            if (parts.Count == 0) throw new InvalidDataException("The live stream had no segments to save.");
            var fragmented = !LooksLikeMpegTs(PartFile(workDir, parts[0]));
            await FinishAsync(item, workDir, new HlsTrack("", parts, fragmented, new Uri(item.Url), true, 0), RecordedAudio(workDir, new Uri(item.Url)), null, ct);
            return;
        }

        _stopLive = stopLive;
        var stream = await ThroughOutageAsync(item, () => FirstLoadAsync(item, ct), ct);
        var main = stream.Main;
        item.IsLive = !main.Ended;
        // A recording says "Recording" throughout, outages aside.
        if (item.IsLive) item.StatusNote = _note = null;
        if (main.FragmentedMp4 && item.FileName.EndsWith(".ts", StringComparison.OrdinalIgnoreCase))
            item.FileName = Path.ChangeExtension(item.FileName, item.HlsAudioOnly ? ".m4a" : ".mp4");
        // On a first run only: the parts of a paused download are kept under whatever it has been renamed to.
        if (item.HlsSegmentCount == 0 && (stream.Output ?? main.Extension) is { } extension && !item.FileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            item.FileName = Path.ChangeExtension(item.FileName, extension);

        Directory.CreateDirectory(item.Directory);
        // Finished parts are reused on resume, so a first run must not adopt a folder
        // that belongs to another download of the same name.
        if (item.HlsSegmentCount == 0) ReserveWorkDir(item, _settings.NewPartFolder() ?? item.OwnPartFolder);
        workDir = item.PartsFolderPath;
        if (item.IsLive)
        {
            Directory.CreateDirectory(workDir);
            var (video, audio) = await RecordLiveAsync(item, main, stream.Audio, workDir, ct, stopLive);
            await FinishAsync(item, workDir, video, audio, null, ct);
            return;
        }

        var tracks = new[] { main, stream.Audio }.OfType<HlsTrack>().ToList();
        foreach (var track in tracks) Directory.CreateDirectory(Path.Combine(workDir, track.Folder));

        var all = tracks.SelectMany(t => t.Parts.Select(p => (Part: p, File: PartFile(workDir, t, p)))).ToList();
        item.HlsSegmentCount = all.Count;
        // Parts fetched side by side finish together, so counting them, the bar stands still and then leaps. Where
        // their sizes are known, as for a site's video, progress counts bytes as they arrive instead.
        item.TotalBytes = KnownBytes(all.Select(x => x.Part)) ?? -1;
        var pending = all.Where(x => !File.Exists(x.File)).ToList();
        item.HlsSegmentsDone = all.Count - pending.Count;
        item.DownloadedBytes = all.Except(pending).Sum(x => new FileInfo(x.File).Length);
        var waiting = pending.Select(x => x.File).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // What is kept is decrypted, so an encrypted part's size is only known once it is.
        var run = item.StreamRun = new StreamRun(all.Select(x => new StreamRun.Part(
            x.File, x.Part.Key == null ? x.Part.Range?.Length : null, !waiting.Contains(x.File))).ToList());
        item.RaiseProgress();

        await DownloadPartsAsync(item, run, pending, new ConcurrentDictionary<Uri, Lazy<Task<byte[]>>>(), ct);
        var subtitles = stream.Subtitles == null ? null : await TryLoadSubtitlesAsync(item, stream.Subtitles.Uri!, VideoStart(workDir, main), ct);
        item.HlsFingerprint = Fingerprint(stream);
        await FinishAsync(item, workDir, main, stream.Audio,
            subtitles == null ? null : (FileNames.Sanitize(stream.Subtitles!.Language ?? "subtitles"), subtitles), ct, stream.Output);
    }

    /// <summary>A playlist's request, tried again through an outage for the reconnect window.</summary>
    async Task<T> ThroughOutageAsync<T>(DownloadItem item, Func<Task<T>> ask, CancellationToken ct)
    {
        int reconnects = 0;
        DateTime? giveUpAt = null;
        while (true)
        {
            var reconnect = item.ReconnectToken;
            try
            {
                var answer = await ask();
                if (reconnects > 0) BackFromOutage(item);
                return answer;
            }
            catch (Exception e) when (Net.IsConnectionFailure(e, ct) && !Retry.NoSuchName(e))
            {
                if (reconnects == 0) giveUpAt = DateTime.UtcNow + Retry.ReconnectWindow;
                if (DateTime.UtcNow >= giveUpAt || _stopLive.IsCancellationRequested) throw;
                giveUpAt = await WaitToReconnectAsync(item, reconnects++, giveUpAt, reconnect, ct);
                if (_stopLive.IsCancellationRequested) throw;
            }
        }
    }

    void BackFromOutage(DownloadItem item)
    {
        item.StatusNote = _note;
        // Once for the outage, however many parts it reaches.
        if (Interlocked.Exchange(ref _inOutage, 0) == 1) Log.Info($"Download {item.Id} reconnected");
    }

    /// <summary>Stopping a recording ends the wait too: there is no sense in waiting to record more.</summary>
    async Task<DateTime?> WaitToReconnectAsync(DownloadItem item, int reconnects, DateTime? giveUpAt, CancellationToken reconnect, CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _inOutage, 1) == 0) Log.Info($"Download {item.Id} waiting to reconnect to {Log.Host(item.Url)}");
        using var wake = CancellationTokenSource.CreateLinkedTokenSource(reconnect, _stopLive);
        return await Retry.WaitToReconnectAsync(reconnects, giveUpAt, note => item.StatusNote = note,
            wait => RetryPolicy.WaitOrWakeAsync(wait, wake.Token, ct));
    }

    /// <summary>
    /// The playlists, through the browser's proxy if the download's own route gets no answer or a 403; that
    /// proxy then serves the rest of the run. If it fails too, the download's own error stands, and its own
    /// route is what any next try starts from.
    /// </summary>
    async Task<HlsStream> FirstLoadAsync(DownloadItem item, CancellationToken ct)
    {
        try
        {
            return await LoadStreamAsync(item, ct);
        }
        catch (Exception e) when (BrowserProxy.RouteAfter(e, item, _settings, _proxy, ct) is { } route)
        {
            var client = _client;
            _client = Net.ClientFor(route);
            item.StatusNote = _note = StatusWords.Current.UsingBrowserProxy;
            try
            {
                return await LoadStreamAsync(item, ct);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                _client = client;
                item.StatusNote = _note = null;
                ExceptionDispatchInfo.Throw(e);
                throw;
            }
        }
    }

    /// <summary>The most connections a stream's parts are fetched over, as for a file.</summary>
    const int MaxConnections = 32;

    /// <summary>
    /// Fetches the parts over up to the connection limit; the first failure stops the rest. Once no part is left
    /// to start, a free connection takes on half of what a part still arriving has left, where that is a byte range,
    /// rather than wait while the last parts arrive one connection each.
    /// </summary>
    async Task DownloadPartsAsync(DownloadItem item, StreamRun run, IEnumerable<(HlsPart Part, string File)> parts,
        ConcurrentDictionary<Uri, Lazy<Task<byte[]>>> keys, CancellationToken ct)
    {
        using var failure = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var board = new PartBoard(parts);
        Exception? fatal = null;

        int connections = Math.Clamp(_settings.ConnectionsFor(item.Url, item.MaxConnections), 1, MaxConnections);
        await Task.WhenAll(Enumerable.Range(0, connections).Select(async worker =>
        {
            // Listed once it has work, so a connection with nothing to do isn't.
            StreamRun.Line? line = null;
            try
            {
                while (!failure.IsCancellationRequested && board.Next() is { } work)
                {
                    line ??= run.LineFor(worker);
                    line.On(work.File);
                    if (work.Span == null) await DownloadPartAsync(item, line, work.Part, work.File, work.Piece, board, keys, failure.Token);
                    // A helper that fails leaves its span to the part's own connection, which tries again as it would its own.
                    else if (!await HelpAsync(item, line, work.Part, work.Piece!, work.Span, failure.Token)) return;
                }
                line?.Done();
            }
            catch (Exception e)
            {
                Interlocked.CompareExchange(ref fatal, e, null);
                failure.Cancel();
            }
        }));
        ct.ThrowIfCancellationRequested();
        if (fatal != null) ExceptionDispatchInfo.Throw(fatal);
    }

    /// <summary>What a free connection does next: start a part, or fill a span of one arriving. <paramref name="File"/> is where the part is kept.</summary>
    sealed record Work(HlsPart Part, string File, SharedPiece? Piece, SharedPiece.Span? Span);

    /// <summary>A batch's parts still to start, and those arriving as shared pieces, for free connections to take.</summary>
    sealed class PartBoard(IEnumerable<(HlsPart Part, string File)> parts)
    {
        readonly Queue<(HlsPart Part, string File)> _waiting = new(parts);
        readonly List<(HlsPart Part, string File, SharedPiece Piece)> _arriving = new();

        /// <summary>The next part to start; else a span of the arriving piece with the most left; null when there is neither.</summary>
        public Work? Next()
        {
            lock (_waiting)
            {
                if (_waiting.TryDequeue(out var next))
                {
                    var piece = Shareable(next.Part) ? new SharedPiece(next.Part.Range!.Offset, (int)next.Part.Range.Length) : null;
                    if (piece != null) _arriving.Add((next.Part, next.File, piece));
                    return new Work(next.Part, next.File, piece, null);
                }
                foreach (var (part, file, piece) in _arriving.OrderByDescending(a => a.Piece.MostLeft))
                    if (piece.Take(out _) is { } span) return new Work(part, file, piece, span);
                return null;
            }
        }

        /// <summary>The piece is kept, or never will be: no connection may take any of it now.</summary>
        public void Done(SharedPiece piece)
        {
            lock (_waiting) _arriving.RemoveAll(a => a.Piece == piece);
        }

        /// <summary>
        /// A byte range in clear can be fetched by several connections at once and put together. One that is
        /// encrypted is decrypted whole, and one far larger than a part should be is left to arrive as it can.
        /// </summary>
        static bool Shareable(HlsPart part) => part.Key == null && part.Range is { Length: > 0 and <= MaxSharedBytes };

        const long MaxSharedBytes = 64 * 1024 * 1024;
    }

    /// <summary>One track of a recording: where its parts go, and how far it has got. Each has its own sequence numbers.</summary>
    sealed class LiveTrack(HlsTrack playlist, string folder, List<HlsPart> recorded)
    {
        public HlsTrack Playlist { get; set; } = playlist;
        public string Folder { get; } = folder;
        public List<HlsPart> Recorded { get; } = recorded;
        public int Next { get; set; } = recorded.Count == 0 ? 0 : recorded[^1].Index + 1;
        public long Last { get; set; } = -1;
    }

    /// <summary>
    /// Records a live stream: reloads its playlists every target duration and fetches each segment newer
    /// than the last one recorded, until the main playlist ends or <paramref name="stop"/> is set. A separate
    /// audio track is recorded alongside. Parts are numbered in recording order; a resumed recording continues
    /// after the parts it already has.
    /// </summary>
    async Task<(HlsTrack Main, HlsTrack? Audio)> RecordLiveAsync(DownloadItem item, HlsTrack main, HlsTrack? audio, string workDir, CancellationToken ct, CancellationToken stop)
    {
        var tracks = new List<LiveTrack> { new(main, workDir, RecordedParts(workDir, main.Playlist)) };
        if (audio != null)
        {
            var folder = Path.Combine(workDir, audio.Folder);
            Directory.CreateDirectory(folder);
            tracks.Add(new LiveTrack(audio, folder, RecordedParts(folder, audio.Playlist)));
        }
        item.HlsSegmentCount = item.HlsSegmentsDone = tracks.Sum(t => t.Recorded.Count);
        item.DownloadedBytes = tracks.Sum(t => t.Recorded.Sum(p => new FileInfo(PartFile(t.Folder, p)).Length));
        item.RaiseProgress();

        var keys = new ConcurrentDictionary<Uri, Lazy<Task<byte[]>>>();
        var run = item.StreamRun = StreamRun.Recording();
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct, stop);
        while (true)
        {
            var batches = tracks.Select(track =>
            {
                // The initialisation section is needed once, before the first media segment.
                var fresh = track.Playlist.Parts.Where(p => p.IsInit ? track.Next == 0 : p.Sequence > track.Last).ToList();
                var batch = fresh.Select(p => (Part: p, File: Path.Combine(track.Folder, $"{track.Next++:D6}.seg"))).ToList();
                track.Last = fresh.Where(p => !p.IsInit).Select(p => p.Sequence).DefaultIfEmpty(track.Last).Max();
                return (Track: track, Batch: batch);
            }).ToList();
            item.HlsSegmentCount += batches.Sum(b => b.Batch.Count);
            try
            {
                await DownloadPartsAsync(item, run, batches.SelectMany(b => b.Batch), keys, ct);
            }
            catch (Exception e) when (stop.IsCancellationRequested && Net.IsConnectionFailure(e, ct))
            {
                // Stopped during an outage: what arrived is saved.
                item.HlsSegmentCount = item.HlsSegmentsDone;
            }
            // Until the playlist has more, the connections wait.
            run.Rest();
            foreach (var (track, batch) in batches)
                track.Recorded.AddRange(batch.Where(x => File.Exists(x.File))
                    .Select(x => x.Part with { Index = int.Parse(Path.GetFileNameWithoutExtension(x.File), CultureInfo.InvariantCulture) }));

            if (tracks[0].Playlist.Ended || stop.IsCancellationRequested) break;
            try
            {
                await Task.Delay(LivePoll ?? TimeSpan.FromSeconds(Math.Clamp(tracks[0].Playlist.TargetDuration, 1, 10)), wait.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                break;
            }
            try
            {
                foreach (var track in tracks)
                {
                    var (address, folder) = (track.Playlist.Playlist, track.Playlist.Folder);
                    track.Playlist = await ThroughOutageAsync(item, () => LoadMediaAsync(item, address, folder, ct), ct);
                }
            }
            catch (Exception e) when (stop.IsCancellationRequested && Net.IsConnectionFailure(e, ct))
            {
                break;
            }
        }
        if (tracks[0].Recorded.Count == 0) throw new InvalidDataException("The live stream had no segments to save.");
        var saved = tracks.Select(t => t.Playlist with { Parts = t.Recorded }).ToList();
        return (saved[0], saved.Count > 1 && saved[1].Parts.Count > 0 ? saved[1] : null);
    }

    /// <summary>The audio a paused recording has, which goes with its video when it is stopped without the network.</summary>
    static HlsTrack? RecordedAudio(string workDir, Uri playlist)
    {
        var folder = Path.Combine(workDir, "audio");
        if (!Directory.Exists(folder)) return null;
        var parts = RecordedParts(folder, playlist);
        return parts.Count == 0 ? null : new HlsTrack("audio", parts, !LooksLikeMpegTs(PartFile(folder, parts[0])), playlist, true, 0);
    }

    /// <summary>
    /// The parts a recording has so far, in order. A pause can cut a batch short and leave a gap, which
    /// stays a gap: the stream has moved on by the time the recording resumes.
    /// </summary>
    static List<HlsPart> RecordedParts(string workDir, Uri playlist) => Directory.GetFiles(workDir, "*.seg")
        .Select(f => int.Parse(Path.GetFileNameWithoutExtension(f), CultureInfo.InvariantCulture))
        .Order()
        .Select(i => new HlsPart(i, playlist, i, null))
        .ToList();

    static string PartFile(string workDir, HlsPart part) => Path.Combine(workDir, $"{part.Index:D6}.seg");

    static bool LooksLikeMpegTs(string file)
    {
        var head = new byte[189];
        using var stream = File.OpenRead(file);
        int read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        return read > 0 && head[0] == 0x47 && (read < 189 || head[188] == 0x47);
    }

    /// <summary>
    /// Joins the tracks into the final file: ffmpeg repackages TS as MP4 and joins a separate audio
    /// track to the video; audio it cannot join is kept next to the video, and subtitles go beside it.
    /// </summary>
    /// <param name="output">The container asked for, as a site's choice gives it; null is MP4.</param>
    async Task FinishAsync(DownloadItem item, string workDir, HlsTrack main, HlsTrack? audioTrack,
        (string Language, string Text)? subtitles, CancellationToken ct, string? output = null)
    {
        var joined = item.PartPath;
        await JoinAsync(workDir, main, joined, ct);
        string? audio = null;
        if (audioTrack != null)
        {
            audio = Path.Combine(workDir, "audio.joined");
            await JoinAsync(workDir, audioTrack, audio, ct);
        }

        // What ffmpeg writes is the container asked for: TS repackaged, a file in another container, or video and audio joined.
        var produced = joined;
        string? extension = null;
        var container = output ?? (item.HlsAudioOnly ? ".m4a" : ".mp4");
        var own = main.Extension ?? (main.FragmentedMp4 ? null : ".ts");
        var otherContainer = output != null && own != null && !SameContainer(own, output);
        var remuxed = false;
        if (_ffmpeg != null && (audio != null || !main.FragmentedMp4 || otherContainer))
        {
            if (await TryRemuxAsync(_ffmpeg, audio == null ? new[] { joined } : new[] { joined, audio }, item.RemuxPath, FormatOf(container), ct))
            {
                File.Delete(joined);
                produced = item.RemuxPath;
                extension = container;
                audio = null;
                remuxed = true;
            }
        }
        // Not repackaged, a file keeps its own container, and the name says so.
        if (!remuxed && otherContainer) extension = own;

        // Joined while its dialog is still open, a download started early takes the name settled on there.
        await item.UntilSettledAsync(ct);
        Directory.CreateDirectory(item.Directory);
        var target = extension == null ? item.FullPath : Path.ChangeExtension(item.FullPath, extension);

        // The work folder and the name stay until the file is in place, so a failure here resumes.
        var replace = item.MayReplace(target);
        var final = replace ? target : FileNames.Unique(target);
        File.Move(produced, final, replace);
        Mark(item, final);
        // Audio that could not be joined is kept next to the video rather than dropped.
        if (audio != null)
        {
            var kept = Beside(final, $" (audio){AudioExtension(audioTrack!)}", replace);
            File.Move(audio, kept, replace);
            Mark(item, kept);
        }
        if (subtitles is var (language, text))
        {
            var vtt = Beside(final, $".{language}.vtt", replace);
            File.WriteAllText(vtt, text);
            Mark(item, vtt);
        }
        Directory.Delete(workDir, true);
        item.FileName = Path.GetFileName(final);
        item.TotalBytes = new FileInfo(final).Length;
        item.Landed = true;
    }

    /// <summary>While the download still runs, so whatever waits for it to complete only ever sees a marked file.</summary>
    void Mark(DownloadItem item, string path)
    {
        if (_settings.MarkDownloads) ZoneMark.Write(path, item.Url, item.Referrer);
    }

    static readonly object ReserveLock = new();

    /// <summary>
    /// Renames the download to "name (n).ext" until neither its file nor its work folder exists, then
    /// creates the folder. A download allowed to replace exactly this file keeps its name unless
    /// another download holds the work folder. In a temporary folder the work folder is named after
    /// this download, so no other download can hold it.
    /// </summary>
    static void ReserveWorkDir(DownloadItem item, string? partFolder)
    {
        item.PartFolder = partFolder;
        var stem = Path.GetFileNameWithoutExtension(item.FileName);
        var extension = Path.GetExtension(item.FileName);
        lock (ReserveLock)
        {
            for (int n = 1; (!item.MayOverwrite && File.Exists(item.FullPath)) || (partFolder == null && Directory.Exists(item.PartsFolderPath)); n++)
                item.FileName = $"{stem} ({n}){extension}";
            // A first run starts empty: parts left over from an earlier attempt may be of another quality.
            if (partFolder != null && Directory.Exists(item.PartsFolderPath)) Directory.Delete(item.PartsFolderPath, true);
            Directory.CreateDirectory(item.PartsFolderPath);
        }
    }

    static string PartFile(string workDir, HlsTrack track, HlsPart part) => Path.Combine(workDir, track.Folder, $"{part.Index:D6}.seg");

    /// <summary>A file next to <paramref name="final"/> named after it; replaced along with it, otherwise never.</summary>
    static string Beside(string final, string suffix, bool replace)
    {
        var path = Path.Combine(Path.GetDirectoryName(final)!, Path.GetFileNameWithoutExtension(final) + suffix);
        return replace ? path : FileNames.Unique(path);
    }

    static bool SameContainer(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase) || (IsMp4(a) && IsMp4(b));

    static bool IsMp4(string extension) => extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase) || extension.Equals(".m4a", StringComparison.OrdinalIgnoreCase);

    /// <summary>ffmpeg's name for the container a file name's extension asks for.</summary>
    static string FormatOf(string extension) => extension.ToLowerInvariant() switch
    {
        ".webm" => "webm",
        ".mkv" => "matroska",
        _ => "mp4",
    };

    static string AudioExtension(HlsTrack audio)
    {
        if (audio.Extension != null) return audio.Extension;
        if (audio.FragmentedMp4) return ".m4a";
        var extension = Path.GetExtension(audio.Parts[0].Uri.AbsolutePath).ToLowerInvariant();
        return extension is ".aac" or ".mp3" or ".ac3" or ".ec3" ? extension : ".ts";
    }

    static async Task JoinAsync(string workDir, HlsTrack track, string output, CancellationToken ct)
    {
        await using var stream = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, true);
        foreach (var part in track.Parts)
        {
            await using var input = File.OpenRead(PartFile(workDir, track, part));
            await input.CopyToAsync(stream, ct);
        }
    }

    /// <summary>The variant to download, its separate audio track and its subtitles, as the item asks.</summary>
    async Task<HlsStream> LoadStreamAsync(DownloadItem item, CancellationToken ct)
    {
        if (item.Kind == DownloadKind.Dash) return await LoadDashAsync(item, ct);
        if (item.Kind == DownloadKind.Site) return await LoadSiteAsync(item, ct);
        if (item.Kind == DownloadKind.Joined) return await LoadJoinedAsync(item, ct);
        var asked = new Uri(item.Url);
        var (text, uri) = await GetDocumentAsync(item, asked, ct);
        if (!text.TrimStart().StartsWith("#EXTM3U"))
            throw new InvalidDataException("The URL did not return an HLS playlist.");

        const string noAudioTrack = "This stream has no separate audio track to download on its own.";
        if (!HlsMaster.IsMaster(text))
        {
            if (item.HlsAudioOnly) throw new InvalidDataException(noAudioTrack);
            return new HlsStream(ParseMedia(text, uri, "", asked), null, null);
        }

        var master = HlsMaster.Parse(text, uri);
        var variant = master.Pick(item.HlsBandwidth);
        var audio = master.AudioFor(variant);
        if (item.HlsAudioOnly)
        {
            var source = audio?.Uri ?? master.Variants.Where(v => v.AudioOnly).MaxBy(v => v.Bandwidth)?.Uri
                ?? throw new InvalidDataException(noAudioTrack);
            return new HlsStream(await LoadMediaAsync(item, source, "", ct), null, null);
        }
        var (main, sound) = await SideBySideAsync(LoadMediaAsync(item, variant.Uri, "", ct),
            audio == null ? null : LoadMediaAsync(item, audio.Uri!, "audio", ct));
        return new HlsStream(main, sound, item.HlsSubtitles ? master.SubtitlesFor(variant) : null);
    }

    /// <summary>The tracks a DASH manifest has for the quality the item asks for, as HLS tracks that are complete.</summary>
    async Task<HlsStream> LoadDashAsync(DownloadItem item, CancellationToken ct)
    {
        var (text, address) = await GetDocumentAsync(item, new Uri(item.Url), ct);
        var choice = DashManifest.Parse(text, address).Pick(item.HlsBandwidth, item.HlsAudioOnly);
        var (main, audio) = await SideBySideAsync(LoadDashTrackAsync(item, choice.Main, "", ct),
            choice.Audio.Count == 0 ? null : LoadDashTrackAsync(item, choice.Audio, "audio", ct));
        return new HlsStream(main, audio, null);
    }

    /// <summary>Each period's parts after the last's. A period's initialisation section is left out if it is the one already in the track.</summary>
    async Task<HlsTrack> LoadDashTrackAsync(DownloadItem item, IReadOnlyList<DashRepresentation> periods, string folder, CancellationToken ct)
    {
        var parts = new List<HlsPart>();
        DashPart? init = null;
        foreach (var representation in periods)
        {
            var found = representation.Parts();
            if (found.Init is { } section && section != init)
            {
                init = section;
                parts.Add(new HlsPart(parts.Count, section.Uri, parts.Count, null, IsInit: true, Range: section.Range));
            }
            var media = found.WholeFile is { } file ? new[] { await WholeFileAsync(item, file, ct) } : found.Media;
            foreach (var part in media.SelectMany(DashManifest.Pieces))
            {
                if (parts.Count >= DashManifest.MaxParts) throw DashManifest.TooManyParts();
                parts.Add(new HlsPart(parts.Count, part.Uri, parts.Count, null, Range: part.Range));
            }
        }
        var extension = periods[0].Extension;
        return new HlsTrack(folder, parts, extension != ".ts", new Uri(item.Url), true, 0, extension);
    }

    /// <summary>
    /// A site's video. Its first start takes the page's kept read, the panel's menu's or another download's, so it is
    /// under way at once rather than after yt-dlp has read the page again, and any wait for the files has been running
    /// since. Any later start reads the page again, as the addresses it had may be gone; so does a first start whose
    /// kept read's files are. A read made here is kept for the next download of the page.
    /// </summary>
    async Task<HlsStream> LoadSiteAsync(DownloadItem item, CancellationToken ct)
    {
        var reader = _reader ?? throw new InvalidDataException("yt-dlp is needed for this download. Set it up in ADM's Options > Video, then resume it.");
        if (item.SiteFormats == null && reader.Kept(item.Url) is { } kept)
        {
            try
            {
                return await LoadSiteAsync(item, kept, ct);
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                Log.Info($"Download {item.Id} reads its page again, as the kept read failed: {e.Message}");
            }
        }
        SiteVideoInfo info;
        item.StatusNote = StatusWords.Current.ReadingVideo;
        try
        {
            info = await reader.ReadAsync(item.Url, keep: true, ct);
        }
        finally
        {
            item.StatusNote = _note;
        }
        return await LoadSiteAsync(item, info, ct);
    }

    /// <summary>
    /// The files chosen from a read: those the parts came from while the read still has them. If they differ from
    /// those, by format or size, the parts are thrown away.
    /// </summary>
    async Task<HlsStream> LoadSiteAsync(DownloadItem item, SiteVideoInfo info, CancellationToken ct)
    {
        var pick = SiteChoices.Pick(info, item.SiteChoice ?? "original", item.SiteFormats);
        // The size yt-dlp gives, so it is known through the wait for the files; their own sizes replace it once asked for.
        if (item.TotalBytes <= 0 && SiteChoices.SizeOf(pick) is long size) item.TotalBytes = size;
        await UntilServedAsync(item, pick, ct);
        _headers = pick.Main.Headers;
        var (main, audio) = await SideBySideAsync(SiteTrackAsync(item, pick.Main, "", ct),
            pick.Audio == null ? null : SiteTrackAsync(item, pick.Audio, "audio", ct));
        var formats = $"{pick.Main.Id}:{TrackBytes(main)}|{pick.Audio?.Id}:{(audio == null ? 0 : TrackBytes(audio))}";
        if (item.SiteFormats != formats)
        {
            if (item.HlsSegmentCount > 0) StartOver(item);
            item.SiteFormats = formats;
        }
        return new HlsStream(main, audio, null, pick.Extension);
    }

    /// <summary>
    /// Waits until the site serves the files picked, as yt-dlp says when: YouTube refuses them with a 403 until the
    /// page's preroll ad would have played. A time further off than <see cref="LongestSiteWait"/> is waited for that
    /// long, and the site's answer then stands.
    /// </summary>
    async Task UntilServedAsync(DownloadItem item, SitePick pick, CancellationToken ct)
    {
        if (new[] { pick.Main.AvailableAt, pick.Audio?.AvailableAt }.Max() is not { } at) return;
        var longest = DateTimeOffset.UtcNow + LongestSiteWait;
        var until = at < longest ? at : longest;
        if (until <= DateTimeOffset.UtcNow) return;
        item.StatusNote = StatusWords.Current.WaitingForVideo;
        // A timer can end a little before the clock says it should, so the wait goes on until the clock agrees.
        while (until - DateTimeOffset.UtcNow is var left && left > TimeSpan.Zero) await Task.Delay(left, ct);
        item.StatusNote = _note;
    }

    /// <summary>A file, or an HLS playlist, as a track of the site's video.</summary>
    async Task<HlsTrack> SiteTrackAsync(DownloadItem item, SiteFormat format, string folder, CancellationToken ct) =>
        format.IsStream ? await LoadMediaAsync(item, format.Url, folder, ct) : await WholeFileTrackAsync(item, format.Url, folder, "." + format.Extension, ct);

    /// <summary>A video file and its sound, joined as MP4.</summary>
    async Task<HlsStream> LoadJoinedAsync(DownloadItem item, CancellationToken ct)
    {
        if (!Uri.TryCreate(item.AudioUrl, UriKind.Absolute, out var audio)) throw new InvalidDataException("This download has no sound file to join to its video.");
        var (main, sound) = await SideBySideAsync(WholeFileTrackAsync(item, new Uri(item.Url), "", ".mp4", ct), WholeFileTrackAsync(item, audio, "audio", ".m4a", ct));
        return new HlsStream(main, sound, null, ".mp4");
    }

    /// <summary>
    /// A track and its sound, asked for side by side rather than one after the other, so the first bytes come a round
    /// trip sooner. Both are waited for, and if both fail, the track's failure is the one that stands.
    /// </summary>
    static async Task<(HlsTrack Main, HlsTrack? Audio)> SideBySideAsync(Task<HlsTrack> main, Task<HlsTrack>? audio)
    {
        if (audio == null) return (await main, null);
        await Task.WhenAll(main, audio);
        return (main.Result, audio.Result);
    }

    /// <summary>A whole file as a track of pieces of <see cref="DashManifest.MaxPartBytes"/>, so memory stays bounded and pieces resume.</summary>
    async Task<HlsTrack> WholeFileTrackAsync(DownloadItem item, Uri file, string folder, string extension, CancellationToken ct)
    {
        var parts = DashManifest.Pieces(await WholeFileAsync(item, file, ct)).Select((p, i) => new HlsPart(i, p.Uri, i, null, Range: p.Range)).ToList();
        return new HlsTrack(folder, parts, extension != ".ts", file, true, 0, extension);
    }

    async Task<DashPart> WholeFileAsync(DownloadItem item, Uri file, CancellationToken ct) =>
        new(file, await SizeOfAsync(item, file, ct) is long size ? new ByteRange(0, size) : null);

    /// <summary>
    /// What the parts come to when each is a byte range kept as it arrives; null when that can't be told, or when
    /// decryption makes what is kept smaller than what arrives.
    /// </summary>
    static long? KnownBytes(IEnumerable<HlsPart> parts)
    {
        long total = 0;
        foreach (var part in parts)
        {
            if (part.Range == null || part.Key != null) return null;
            total += part.Range.Length;
        }
        return total > 0 ? total : null;
    }

    /// <summary>What a track comes to: its bytes where its parts say, else its number of parts.</summary>
    static long TrackBytes(HlsTrack track) =>
        track.Parts.All(p => p.Range != null) ? track.Parts.Sum(p => p.Range!.Length) : track.Parts.Count;

    /// <summary>Throws away the parts so far, so a run starts afresh with the files now chosen.</summary>
    static void StartOver(DownloadItem item)
    {
        if (Directory.Exists(item.PartsFolderPath)) Directory.Delete(item.PartsFolderPath, true);
        item.HlsSegmentCount = 0;
        item.HlsSegmentsDone = 0;
        item.DownloadedBytes = 0;
        item.StreamRun = null;
    }

    /// <summary>How long a file is, from the answer to asking for its first byte; null from a server that doesn't send ranges.</summary>
    async Task<long?> SizeOfAsync(DownloadItem item, Uri file, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Net.ReadTimeout);
        using var req = NewRequest(item, file);
        req.Headers.Range = new RangeHeaderValue(0, 0);
        using var resp = await Net.SendAsync(_client, _settings, item, req, timeout.Token);
        Net.EnsureSuccess(resp);
        return resp.StatusCode == HttpStatusCode.PartialContent ? resp.Content.Headers.ContentRange?.Length : null;
    }

    async Task<HlsTrack> LoadMediaAsync(DownloadItem item, Uri uri, string folder, CancellationToken ct)
    {
        var (text, address) = await GetDocumentAsync(item, uri, ct);
        return ParseMedia(text, address, folder, uri);
    }

    /// <param name="uri">Where the redirects ended, which relative addresses start from.</param>
    /// <param name="playlist">Where to reload it from: a redirect's target may be signed, and expire.</param>
    static HlsTrack ParseMedia(string text, Uri uri, string folder, Uri playlist)
    {
        var parts = new List<HlsPart>();
        (long Length, long? Offset)? nextRange = null;
        Uri? lastAddress = null;
        long lastEnd = 0;
        long sequence = 0;
        double targetDuration = 0;
        HlsKey? key = null;
        bool fragmented = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("#EXT-X-MEDIA-SEQUENCE:"))
            {
                sequence = long.Parse(line["#EXT-X-MEDIA-SEQUENCE:".Length..], CultureInfo.InvariantCulture);
            }
            else if (line.StartsWith("#EXT-X-TARGETDURATION:"))
            {
                double.TryParse(line["#EXT-X-TARGETDURATION:".Length..], NumberStyles.Float, CultureInfo.InvariantCulture, out targetDuration);
            }
            else if (line.StartsWith("#EXT-X-KEY:"))
            {
                var attrs = HlsMaster.Attributes(line);
                var method = attrs.GetValueOrDefault("METHOD", "NONE");
                key = method switch
                {
                    "NONE" => null,
                    "AES-128" => new HlsKey(new Uri(uri, attrs["URI"]), ParseIv(attrs.GetValueOrDefault("IV"))),
                    _ => throw new NotSupportedException($"Streams protected with {method} are not supported."),
                };
            }
            else if (line.StartsWith("#EXT-X-BYTERANGE:"))
            {
                nextRange = ParseRange(line["#EXT-X-BYTERANGE:".Length..]);
            }
            else if (line.StartsWith("#EXT-X-MAP:"))
            {
                // Fragmented MP4: the initialisation section must precede the media segments.
                var map = HlsMaster.Attributes(line);
                if (!fragmented)
                {
                    var range = map.TryGetValue("BYTERANGE", out var named) ? ParseRange(named) : ((long, long?)?)null;
                    parts.Add(new HlsPart(parts.Count, new Uri(uri, map["URI"]), sequence, key, IsInit: true,
                        Range: range is var (length, offset) ? new ByteRange(offset ?? 0, length) : null));
                }
                fragmented = true;
            }
            else if (!line.StartsWith('#'))
            {
                var address = new Uri(uri, line);
                ByteRange? range = null;
                if (nextRange is var (length, offset))
                {
                    // With no offset it goes on from the part before, which must be on the same address.
                    if (offset == null && address != lastAddress)
                        throw new InvalidDataException("The playlist gives a byte range with no part before it to follow on from.");
                    range = new ByteRange(offset ?? lastEnd, length);
                    lastEnd = range.Offset + length;
                }
                lastAddress = range == null ? null : address;
                nextRange = null;
                parts.Add(new HlsPart(parts.Count, address, sequence++, key, Range: range));
            }
        }
        var ended = text.Contains("#EXT-X-ENDLIST");
        // A live playlist may be empty for a moment; a complete one never is.
        if (parts.Count == 0 && ended) throw new InvalidDataException("The playlist contains no media segments.");
        return new HlsTrack(folder, parts, fragmented, playlist, ended, targetDuration);
    }

    /// <summary>A byte range as a playlist writes it: a length, then @ and an offset if it has one.</summary>
    static (long Length, long? Offset) ParseRange(string text)
    {
        var parts = text.Trim().Split('@');
        if (parts.Length <= 2 && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var length) && length > 0)
        {
            if (parts.Length == 1) return (length, null);
            if (long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var offset)) return (length, offset);
        }
        throw new InvalidDataException($"The playlist gives a byte range it can't be read from: {text}");
    }

    /// <summary>
    /// The subtitles as one WebVTT file, with their cues moved to line up with the video; null when they cannot
    /// be fetched, which leaves the video without them. Subtitles in byte ranges count as that too: nothing has
    /// been seen to use them, and one file's worth of cues would need cutting up rather than joining.
    /// </summary>
    /// <param name="videoStart">When the video starts, in 90 kHz ticks; null takes it from the subtitles' first map, so that their first cue stays where it is.</param>
    async Task<string?> TryLoadSubtitlesAsync(DownloadItem item, Uri playlist, long? videoStart, CancellationToken ct)
    {
        try
        {
            var track = await LoadMediaAsync(item, playlist, "", ct);
            if (track.Parts.Any(part => part.Range != null)) return null;
            var text = new StringBuilder("WEBVTT\n");
            foreach (var part in track.Parts)
            {
                var segment = await GetStringAsync(item, part.Uri, ct);
                videoStart ??= ReadMap(segment) is { } map ? map.Mpegts - (long)(map.Local * MpegTicks) : null;
                text.Append(ShiftCues(segment, videoStart ?? 0));
            }
            return text.ToString();
        }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidDataException or NotSupportedException
            || (e is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return null;
        }
    }

    const double MpegTicks = 90000;
    const long MpegClock = 1L << 33;

    /// <summary>The cues of one WebVTT segment, moved by its X-TIMESTAMP-MAP to where they belong in a video starting at <paramref name="videoStart"/>. With no map that can be read, they stay as they came.</summary>
    internal static string ShiftCues(string segment, long videoStart)
    {
        var cues = WebVttCues(segment);
        if (ReadMap(segment) is not { } map) return cues;
        // The nearer way round the clock, which wraps every 26 hours.
        var ticks = ((map.Mpegts - videoStart) % MpegClock + MpegClock) % MpegClock;
        if (ticks >= MpegClock / 2) ticks -= MpegClock;
        var shift = ticks / MpegTicks - map.Local;
        return Regex.Replace(cues, @"(?m)^(?<from>(?:\d+:)?\d{2}:\d{2}\.\d{3})(?<arrow>[ \t]+-->[ \t]+)(?<to>(?:\d+:)?\d{2}:\d{2}\.\d{3})",
            cue => Moved(cue.Groups["from"].Value, shift) + cue.Groups["arrow"].Value + Moved(cue.Groups["to"].Value, shift));
    }

    /// <summary>The MPEG time and the cue time a segment's X-TIMESTAMP-MAP says are the same moment; null if it has none that can be read.</summary>
    static (long Mpegts, double Local)? ReadMap(string segment)
    {
        var header = segment.Replace("\r\n", "\n").Split("\n\n", 2)[0];
        var map = Regex.Match(header, @"^X-TIMESTAMP-MAP=(.*)$", RegexOptions.Multiline);
        if (!map.Success) return null;
        var mpegts = Regex.Match(map.Groups[1].Value, @"MPEGTS:(\d+)");
        var local = Regex.Match(map.Groups[1].Value, @"LOCAL:((?:\d+:)?\d{2}:\d{2}\.\d{3})");
        return mpegts.Success && local.Success && long.TryParse(mpegts.Groups[1].Value, out var ticks)
            ? (ticks, Seconds(local.Groups[1].Value))
            : null;
    }

    static double Seconds(string time)
    {
        double seconds = 0;
        foreach (var part in time.Split(':')) seconds = seconds * 60 + double.Parse(part, CultureInfo.InvariantCulture);
        return seconds;
    }

    /// <summary>A cue time moved along, in the form it came in, unless it now needs hours. Nothing can come before the start.</summary>
    static string Moved(string time, double shift)
    {
        var moved = TimeSpan.FromMilliseconds(Math.Max(0, Math.Round((Seconds(time) + shift) * 1000)));
        return time.Count(c => c == ':') == 2 || moved.TotalHours >= 1
            ? $"{(int)moved.TotalHours:00}:{moved.Minutes:00}:{moved.Seconds:00}.{moved.Milliseconds:000}"
            : $"{moved.Minutes:00}:{moved.Seconds:00}.{moved.Milliseconds:000}";
    }

    /// <summary>When the video starts: the first presentation time in its first part; null if that part isn't TS or says nothing.</summary>
    static long? VideoStart(string workDir, HlsTrack main)
    {
        if (main.FragmentedMp4 || main.Parts.Count == 0) return null;
        var head = new byte[1024 * 1024];
        using var stream = File.OpenRead(PartFile(workDir, main, main.Parts[0]));
        int read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        return FirstPts(head.AsSpan(0, read));
    }

    /// <summary>The first presentation time in TS data, in 90 kHz ticks; null if it isn't TS or has none.</summary>
    internal static long? FirstPts(ReadOnlySpan<byte> ts)
    {
        const int PacketSize = 188;
        for (int at = 0; at + PacketSize <= ts.Length; at += PacketSize)
        {
            var packet = ts.Slice(at, PacketSize);
            if (packet[0] != 0x47) return null;
            // A PES packet starts here, and this TS packet has a payload.
            int adaptation = (packet[3] >> 4) & 3;
            if ((packet[1] & 0x40) == 0 || adaptation is 0 or 2) continue;
            int pes = adaptation == 3 ? 5 + packet[4] : 4;
            if (pes + 14 > PacketSize || packet[pes] != 0 || packet[pes + 1] != 0 || packet[pes + 2] != 1) continue;
            if ((packet[pes + 7] & 0x80) == 0) continue;
            var pts = packet.Slice(pes + 9, 5);
            return ((long)(pts[0] & 0x0E) << 29) | ((long)pts[1] << 22) | ((long)(pts[2] & 0xFE) << 14) | ((long)pts[3] << 7) | ((long)pts[4] >> 1);
        }
        return null;
    }

    /// <summary>The cues of one WebVTT segment: everything after its header block, which each segment repeats.</summary>
    internal static string WebVttCues(string segment)
    {
        var text = segment.TrimStart('﻿').Replace("\r\n", "\n");
        var start = text.IndexOf("\n\n", StringComparison.Ordinal);
        return start < 0 ? "" : "\n" + text[(start + 2)..].Trim('\n') + "\n";
    }

    static byte[]? ParseIv(string? hex)
    {
        if (string.IsNullOrEmpty(hex)) return null;
        var digits = hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? hex[2..] : hex;
        return Convert.FromHexString(digits.PadLeft(32, '0'));
    }

    static byte[] SequenceIv(long sequence)
    {
        var iv = new byte[16];
        for (int i = 15; i >= 8; i--, sequence >>= 8) iv[i] = (byte)sequence;
        return iv;
    }

    /// <param name="piece">Where a part other connections may help with arrives; null for one fetched by this connection alone.</param>
    async Task DownloadPartAsync(DownloadItem item, StreamRun.Line line, HlsPart part, string target, SharedPiece? piece, PartBoard board,
        ConcurrentDictionary<Uri, Lazy<Task<byte[]>>> keys, CancellationToken ct)
    {
        bool kept = false;
        try
        {
            await KeepPartAsync(item, line, part, target, piece, keys, ct);
            kept = true;
        }
        finally
        {
            if (piece != null)
            {
                board.Done(piece);
                // Never kept, its bytes come off the progress, and any still arriving don't count.
                if (!kept)
                {
                    var lost = piece.Abandon();
                    item.AddDownloaded(-lost);
                    line.Lost(lost);
                }
            }
        }
    }

    /// <summary>Fetches a part and puts it on disk under its own name, which says it is complete.</summary>
    /// <remarks>
    /// A try that lost its connection is tried again for the reconnect window, from the first time it failed;
    /// HTTP errors get a count of tries.
    /// </remarks>
    async Task KeepPartAsync(DownloadItem item, StreamRun.Line line, HlsPart part, string target, SharedPiece? piece,
        ConcurrentDictionary<Uri, Lazy<Task<byte[]>>> keys, CancellationToken ct)
    {
        int reconnects = 0;
        DateTime? giveUpAt = null;
        for (int attempt = 0; ; attempt++)
        {
            long arrived = 0;
            var reconnect = item.ReconnectToken;
            try
            {
                var data = piece != null
                    // What arrived stays in the piece through a failed try, and counts once, whichever connection brought it.
                    ? await FetchSharedAsync(item, line, part, piece, reconnect, ct)
                    : await GetSegmentAsync(item, line, part.Uri, part.Range, read => { item.AddDownloaded(read); line.Received(read); arrived += read; }, reconnect, ct);
                if (part.Key is { } key)
                {
                    var keyBytes = await keys.GetOrAdd(key.Uri, u => new Lazy<Task<byte[]>>(() => GetBytesAsync(item, u, ct))).Value;
                    using var aes = Aes.Create();
                    aes.Key = keyBytes;
                    data = aes.DecryptCbc(data, key.Iv ?? SequenceIv(part.Sequence), PaddingMode.PKCS7);
                }

                using (var file = OpenPartFile(target + ".tmp"))
                {
                    file.SetLength(data.Length);
                    await file.WriteAsync(0, data, ct);
                    // On disk before it is renamed into place: a part that exists is complete, even after a power cut.
                    file.Flush();
                }
                File.Move(target + ".tmp", target, true);
                // Progress counts what is kept, and decryption drops the padding. A shared piece counted just what it kept.
                if (piece == null) item.AddDownloaded(data.Length - arrived);
                item.AddHlsSegmentDone();
                line.Kept();
                if (reconnects > 0) BackFromOutage(item);
                return;
            }
            catch (Exception e)
            {
                // A failed attempt keeps nothing, so its bytes come off the progress.
                item.AddDownloaded(-arrived);
                line.Lost(arrived);
                // Reconnect now, as after sleep or a network change: straight back, without using up a try.
                if (e is OperationCanceledException && reconnect.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    attempt--;
                    continue;
                }
                if (Net.IsConnectionFailure(e, ct))
                {
                    if (reconnects == 0) giveUpAt = DateTime.UtcNow + Retry.ReconnectWindow;
                    if (DateTime.UtcNow >= giveUpAt || _stopLive.IsCancellationRequested) throw;
                    if (part.Key != null) keys.TryRemove(part.Key.Uri, out _);
                    line.Waiting();
                    giveUpAt = await WaitToReconnectAsync(item, reconnects++, giveUpAt, reconnect, ct);
                    if (_stopLive.IsCancellationRequested) throw;
                    attempt--;
                    continue;
                }
                if (!Net.IsTransient(e, ct) || attempt >= 6) throw;
                if (part.Key != null) keys.TryRemove(part.Key.Uri, out _);
                line.Waiting();
                await Retry.BackoffAsync(attempt, ct);
            }
        }
    }

    /// <summary>Fetches a media segment under both speed limits, reporting bytes as they arrive.</summary>
    /// <param name="reconnect">Cancelled by <see cref="DownloadManager.Reconnect"/>: ends the request and reads at once.</param>
    async Task<byte[]> GetSegmentAsync(DownloadItem item, StreamRun.Line line, Uri uri, ByteRange? range, Action<int> onRead, CancellationToken reconnect, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, reconnect);
        timeout.CancelAfter(Net.ReadTimeout);
        using var req = NewRequest(item, uri);
        if (range != null) req.Headers.Range = new RangeHeaderValue(range.Offset, range.Offset + range.Length - 1);
        line.Asking();
        using var resp = await Net.SendAsync(_client, _settings, item, req, timeout.Token);
        Net.EnsureSuccess(resp);
        if (range != null && !AnswersRange(resp, range))
            throw new NotSupportedException("This stream is byte ranges of one file, and the server doesn't send byte ranges.");
        line.Receiving();

        await using var body = await resp.Content.ReadAsStreamAsync(ct);
        using var data = new MemoryStream();
        var buffer = new byte[BufferSize];
        while (true)
        {
            // Each read has its own deadline, so time spent waiting on a speed limit never counts against it.
            timeout.CancelAfter(Net.ReadTimeout);
            int read = await body.ReadAsync(buffer, timeout.Token);
            if (read == 0)
            {
                if (range != null && data.Length < range.Length)
                    throw new HttpIOException(HttpRequestError.ResponseEnded, "Connection closed before the segment finished.");
                return data.ToArray();
            }
            await _limiter.WaitAsync(read, ct);
            await item.Limiter.WaitAsync(read, ct);
            data.Write(buffer, 0, read);
            onRead(read);
        }
    }

    static bool AnswersRange(HttpResponseMessage resp, ByteRange range) => AnswersRange(resp, range.Offset, range.Offset + range.Length - 1);

    static bool AnswersRange(HttpResponseMessage resp, long from, long to) =>
        resp.StatusCode == HttpStatusCode.PartialContent
        && resp.Content.Headers.ContentRange is { From: long first, To: long last }
        && first == from && last == to;

    /// <summary>
    /// Fills a part's shared piece: what is left of it after a failed try, any span a helper let go, and half of
    /// what a helper still has left. It waits only while helpers finish spans too small to split.
    /// </summary>
    async Task<byte[]> FetchSharedAsync(DownloadItem item, StreamRun.Line line, HlsPart part, SharedPiece piece, CancellationToken reconnect, CancellationToken ct)
    {
        while (!piece.IsComplete)
        {
            var span = piece.Take(out var released);
            if (span == null)
            {
                await released.WaitAsync(ct);
                continue;
            }
            try
            {
                await FetchSpanAsync(item, line, part.Uri, piece, span, reconnect, ct);
            }
            finally
            {
                piece.Release(span);
            }
        }
        return piece.Data;
    }

    /// <summary>A free connection fills a span of another part's piece. False when it failed, which leaves the rest to that part's own connection.</summary>
    async Task<bool> HelpAsync(DownloadItem item, StreamRun.Line line, HlsPart part, SharedPiece piece, SharedPiece.Span span, CancellationToken ct)
    {
        var reconnect = item.ReconnectToken;
        try
        {
            await FetchSpanAsync(item, line, part.Uri, piece, span, reconnect, ct);
            return true;
        }
        catch (OperationCanceledException) when (reconnect.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // Reconnecting, as after sleep or a network change: free again at once.
            return true;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return false;
        }
        finally
        {
            piece.Release(span);
        }
    }

    /// <summary>Fills <paramref name="span"/> from one request, under both speed limits, until it is full or a split leaves it full sooner.</summary>
    async Task FetchSpanAsync(DownloadItem item, StreamRun.Line line, Uri uri, SharedPiece piece, SharedPiece.Span span, CancellationToken reconnect, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, reconnect);
        timeout.CancelAfter(Net.ReadTimeout);
        var (from, to) = piece.RangeOf(span);
        using var req = NewRequest(item, uri);
        req.Headers.Range = new RangeHeaderValue(from, to);
        line.Asking();
        using var resp = await Net.SendAsync(_client, _settings, item, req, timeout.Token);
        Net.EnsureSuccess(resp);
        if (!AnswersRange(resp, from, to))
            throw new NotSupportedException("This stream is byte ranges of one file, and the server doesn't send byte ranges.");
        line.Receiving();

        await using var body = await resp.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[BufferSize];
        while (piece.LeftOf(span) is var left and > 0)
        {
            // Each read has its own deadline, so time spent waiting on a speed limit never counts against it.
            timeout.CancelAfter(Net.ReadTimeout);
            int read = await body.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, left)), timeout.Token);
            if (read == 0) throw new HttpIOException(HttpRequestError.ResponseEnded, "Connection closed before the segment finished.");
            await _limiter.WaitAsync(read, ct);
            await item.Limiter.WaitAsync(read, ct);
            var stored = piece.Store(span, buffer.AsSpan(0, read));
            item.AddDownloaded(stored);
            line.Received(stored);
        }
    }

    /// <summary>Fetches a key or subtitle segment, which are small and not held to the speed limits.</summary>
    async Task<byte[]> GetBytesAsync(DownloadItem item, Uri uri, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        using var req = NewRequest(item, uri);
        using var resp = await Net.SendAsync(_client, _settings, item, req, timeout.Token);
        Net.EnsureSuccess(resp);
        return await resp.Content.ReadAsByteArrayAsync(timeout.Token);
    }

    /// <summary>A playlist or manifest, which is not held to the speed limits but is held to a size, and where its redirects ended.</summary>
    async Task<(string Text, Uri Address)> GetDocumentAsync(DownloadItem item, Uri uri, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        using var req = NewRequest(item, uri);
        using var resp = await Net.SendAsync(_client, _settings, item, req, timeout.Token);
        Net.EnsureSuccess(resp);
        await using var body = await resp.Content.ReadAsStreamAsync(timeout.Token);
        using var data = new MemoryStream();
        var buffer = new byte[BufferSize];
        while (await body.ReadAsync(buffer, timeout.Token) is var read and > 0)
        {
            if (data.Length + read > DashManifest.MaxManifestChars) throw new InvalidDataException("The server sent a playlist or manifest too large to read.");
            data.Write(buffer, 0, read);
        }
        return (Encoding.UTF8.GetString(data.GetBuffer(), 0, (int)data.Length), resp.RequestMessage?.RequestUri ?? uri);
    }

    static readonly HashSet<string> OwnHeaders = new(StringComparer.OrdinalIgnoreCase) { "Cookie", "Host", "Range", "Accept-Encoding" };

    /// <summary>
    /// A playlist can name any site, and the browser's cookies are for the download's own. A site's files get the
    /// headers yt-dlp named for them, but cookies, ranges and encoding stay ADM's own.
    /// </summary>
    HttpRequestMessage NewRequest(DownloadItem item, Uri uri)
    {
        var req = Net.NewRequest(item, uri.ToString(), _settings.UserAgent, cookies: Net.SameOrigin(uri, item.Url));
        foreach (var (name, value) in _headers ?? new Dictionary<string, string>())
        {
            if (OwnHeaders.Contains(name)) continue;
            req.Headers.Remove(name);
            req.Headers.TryAddWithoutValidation(name, value);
        }
        return req;
    }

    async Task<string> GetStringAsync(DownloadItem item, Uri uri, CancellationToken ct) =>
        Encoding.UTF8.GetString(await GetBytesAsync(item, uri, ct));

    /// <summary>
    /// Writes <paramref name="format"/> (ffmpeg's name for it) without re-encoding: one input is repackaged, two are
    /// joined as video and audio. False when ffmpeg fails.
    /// </summary>
    static async Task<bool> TryRemuxAsync(string ffmpeg, string[] inputs, string target, string format, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(ffmpeg)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardError = true,
        };
        var args = new List<string> { "-nostdin", "-y" };
        foreach (var input in inputs) args.AddRange(new[] { "-i", input });
        if (inputs.Length > 1) args.AddRange(new[] { "-map", "0:v:0", "-map", "1:a:0" });
        // The target's temporary extension says nothing, so the output format is named.
        args.AddRange(new[] { "-c", "copy" });
        if (format == "mp4") args.AddRange(new[] { "-movflags", "+faststart" });
        args.AddRange(new[] { "-f", format, target });
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi);
        if (process == null) return false;
        _ = process.StandardError.ReadToEndAsync(ct);
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // Paused: stop ffmpeg so it lets go of the files.
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }
        if (process.ExitCode == 0 && File.Exists(target)) return true;
        if (File.Exists(target)) File.Delete(target);
        return false;
    }
}
