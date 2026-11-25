using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace ADM.Core;

public enum DownloadStatus { Queued, Downloading, Paused, Completed, Failed }

/// <summary>A download's state as the list shows it, by colour and mark: <see cref="DownloadItem.Look"/>.</summary>
public enum StatusLook { Waiting, Active, Paused, Warning, Failed, Done }

/// <summary>
/// How a download is fetched. <see cref="Site"/>: the address is a page, read by yt-dlp each time it starts.
/// <see cref="Joined"/>: the address is a video file, and <see cref="DownloadItem.AudioUrl"/> its sound, joined by ffmpeg.
/// </summary>
public enum DownloadKind { Http, Hls, Dash, Site, Joined }

public enum CompletionAction { None, ExitApp, Sleep, Shutdown, Hibernate }

/// <summary>What to do when a link that is already in the list is added again.</summary>
public enum DuplicateAction { Ask, AddNumbered, Overwrite, Resume }

/// <summary>What double-clicking a download does.</summary>
public enum DoubleClickAction { Open, Properties }

/// <summary>How a download's progress window starts; queues never open one.</summary>
public enum ProgressWindowView { Normal, Minimized, Hidden }

/// <summary>Where <see cref="DownloadManager.MoveInQueue"/> moves the selected files.</summary>
public enum QueueMove { Top, Up, Down, Bottom }

/// <summary>
/// A download queue lets go of each file once it is complete; a sync queue keeps its files and,
/// on each run, downloads again the ones that changed on the server.
/// </summary>
public enum QueueKind { Download, Sync }

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
}

/// <summary>What a connection is doing, as the progress window lists it.</summary>
public enum ConnectionStage { Idle, Connecting, Asking, Receiving, Waiting, Complete }

/// <summary>A row of the progress window's list: what one connection has brought, and what it is doing.</summary>
public readonly record struct ConnectionInfo(long Downloaded, ConnectionStage Stage);

/// <summary>
/// A stretch of the progress window's strip, in the strip's own units: filled from its start for
/// <paramref name="Filled"/>, with a connection at work on it when <paramref name="Live"/>, and a line where
/// <paramref name="Marked"/>, at the place a connection started.
/// </summary>
public readonly record struct StripPiece(long Start, long Length, long Filled, bool Live, bool Marked);

/// <summary>The progress window's strip: its stretches, on a scale of <paramref name="Total"/> units.</summary>
public sealed record Strip(long Total, IReadOnlyList<StripPiece> Pieces);

/// <summary>A byte range of the target file served by one connection.</summary>
public sealed class Segment
{
    public long Start { get; set; }

    /// <summary>Inclusive end offset; <see cref="long.MaxValue"/> while the total size is unknown.</summary>
    public long End { get; set; }

    public long Downloaded { get; set; }

    [JsonIgnore] public bool Active { get; set; }
    [JsonIgnore] public long Position => Start + Downloaded;
    [JsonIgnore] public long Remaining => End == long.MaxValue ? long.MaxValue : End - Position + 1;
    [JsonIgnore] public bool IsComplete => Remaining <= 0;

    /// <summary>What the connection serving this segment is doing; read under the item's SyncRoot.</summary>
    [JsonIgnore]
    public ConnectionStage Stage =>
        IsComplete ? ConnectionStage.Complete
        : !Active ? ConnectionStage.Idle
        : IsWaiting ? ConnectionStage.Waiting
        : _askedAt != 0 ? ConnectionStage.Asking
        : Request != null ? ConnectionStage.Receiving
        : ConnectionStage.Connecting;

    internal Segment Clone() => new() { Start = Start, End = End, Downloaded = Downloaded };

    // The connection serving this segment: its speed over the last two seconds, and its open request; see
    // HttpDownloader. Guarded by the item's SyncRoot, and not saved. Times are Stopwatch timestamps.
    readonly Queue<(long At, long Bytes)> _samples = new();
    long _received;
    long _connectedAt;
    long _disconnectedAt;
    long _askedAt;

    /// <summary>The open request of the connection serving this segment, so another connection can end it; null between requests.</summary>
    internal CancellationTokenSource? Request { get; private set; }

    /// <summary>Its connection is waiting to try again, and <see cref="Request"/> ends the wait.</summary>
    internal bool IsWaiting { get; private set; }

    /// <summary>How many connections have passed this piece on; a connection that isn't receiving does so only a few times.</summary>
    internal int HandOvers { get; init; }

    /// <summary>The bytes before its position that its connection is checking against the part file, until they pass.</summary>
    internal int Checking { get; set; }

    /// <summary>The bytes before its position still to check, left by the connection this piece was taken from.</summary>
    internal int CheckOwed { get; set; }

    /// <summary>A connection has asked for this segment and has no answer yet.</summary>
    internal void Asked(long now, CancellationTokenSource request)
    {
        _askedAt = now;
        Request = request;
    }

    /// <summary>How long the request has gone unanswered; 0 unless it is waiting for its answer.</summary>
    internal double SecondsAsking(long now) => _askedAt == 0 ? 0 : (double)(now - _askedAt) / Stopwatch.Frequency;

    internal void Waiting(CancellationTokenSource wake)
    {
        IsWaiting = true;
        Request = wake;
    }

    internal void StopWaiting()
    {
        IsWaiting = false;
        Request = null;
    }

    /// <summary>A connection starts receiving this segment, so its speed is measured afresh.</summary>
    internal void Connected(long now, CancellationTokenSource request)
    {
        _askedAt = 0;
        _samples.Clear();
        _received = 0;
        _connectedAt = now;
        _disconnectedAt = 0;
        _samples.Enqueue((now, 0));
        Request = request;
    }

    internal void Received(int bytes, long now)
    {
        _received += bytes;
        _samples.Enqueue((now, _received));
        Trim(now);
    }

    internal void Disconnected(long now)
    {
        _askedAt = 0;
        _disconnectedAt = now;
        Request = null;
    }

    /// <summary>How long the connection serving this segment has been receiving it; 0 between requests.</summary>
    internal double SecondsConnected(long now) =>
        Request == null || IsWaiting || _askedAt != 0 ? 0 : (double)(now - _connectedAt) / Stopwatch.Frequency;

    /// <summary>Bytes per second over the last two seconds, up to when the connection ended; 0 while it is too soon to tell.</summary>
    internal double SpeedAt(long now)
    {
        if (_samples.Count == 0) return 0;
        var end = _disconnectedAt != 0 ? _disconnectedAt : now;
        Trim(end);
        var (at, bytes) = _samples.Peek();
        var seconds = (double)(end - at) / Stopwatch.Frequency;
        if (_disconnectedAt == 0 && seconds < 0.1) return 0;
        return (_received - bytes) / Math.Max(seconds, 0.001);
    }

    // The newest sample at least two seconds old stays, as the start of the window.
    void Trim(long now)
    {
        var start = now - 2 * Stopwatch.Frequency;
        while (_samples.Count > 1 && _samples.ElementAt(1).At <= start) _samples.Dequeue();
    }
}

public sealed class DownloadItem : Observable
{
    string _fileName = "";
    string _directory = "";
    string _category = "General";
    string? _queueId;
    int _queuePosition;
    long _totalBytes = -1;
    DownloadStatus _status;
    string? _error;
    DateTime? _completedAt;
    long _downloaded;
    long _speedLimitKBps;
    int _hlsDone;
    bool _isLive;
    bool _isChecking;
    double _speed;
    // The bytes downloaded at each recent tick, oldest first: the speed is what arrived over the last SpeedWindow.
    readonly List<(DateTime At, long Bytes)> _samples = new();
    static readonly TimeSpan SpeedWindow = TimeSpan.FromSeconds(1);

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Url { get => _url; set => Set(ref _url, value); }
    string _url = "";
    public string? Referrer { get => _referrer; set => Set(ref _referrer, value); }
    string? _referrer;
    /// <summary>The page the download came from; set once when it is added, and kept when the referer is edited.</summary>
    public string? PageUrl { get => _pageUrl; set => Set(ref _pageUrl, value); }
    string? _pageUrl;
    public string? Description { get => _description; set => Set(ref _description, value); }
    string? _description;
    /// <summary>When a transfer last started; a sync queue's check doesn't count.</summary>
    public DateTime? LastTryAt { get => _lastTryAt; set => Set(ref _lastTryAt, value); }
    DateTime? _lastTryAt;
    /// <summary>A running queue started the current or last transfer; such downloads open no windows.</summary>
    [JsonIgnore] public bool StartedByQueue { get; internal set; }
    public string? Cookies { get; set; }
    public string? UserAgent { get; set; }
    /// <summary>The proxy the browser uses for this address, as in <see cref="ADM.Core.BrowserProxy.Parse"/>; null if it uses none, or isn't known.</summary>
    public string? BrowserProxy { get; set; }
    /// <summary>Login offered when this download's own server asks for one.</summary>
    public string? UserName { get; set; }
    /// <summary>Encrypted with <see cref="Secrets"/>; never plain text.</summary>
    public string? ProtectedPassword { get; set; }
    public DownloadKind Kind { get; set; }
    public bool ResumeSupported { get; set; }
    /// <summary>The server has answered, so <see cref="ResumeSupported"/> says what it can do.</summary>
    public bool ResumeKnown { get; set; }

    /// <summary>HLS or DASH: downloaded as parts by <see cref="HlsDownloader"/>, and joined here.</summary>
    [JsonIgnore]
    public bool IsStream => Kind != DownloadKind.Http;

    /// <summary>Whether a pause keeps the progress: null until the server has answered. A stream keeps its parts.</summary>
    [JsonIgnore]
    public bool? Resumable => IsStream || ResumeSupported ? true : ResumeKnown ? false : null;

    /// <summary>The Resume column: Yes, No, or blank until the server has answered.</summary>
    [JsonIgnore]
    public string ResumeText => StatusWords.Current.ResumeOf(this);
    public string? ETag { get; set; }

    /// <summary>The SHA-256 the finished file should have, in lower-case hex; null checks nothing.</summary>
    public string? ExpectedSha256 { get; set; }

    /// <summary>What the check of the last try found; null when none was made.</summary>
    public string? HashResult { get => _hashResult; set => Set(ref _hashResult, value); }
    string? _hashResult;

    /// <summary>The finished file is being checked against <see cref="ExpectedSha256"/>.</summary>
    [JsonIgnore] public bool IsHashing { get => _isHashing; internal set { if (Set(ref _isHashing, value)) RaiseStatus(); } }
    bool _isHashing;

    /// <summary>The transfer is over, and the file is being checked: by its hash, or by a virus scanner.</summary>
    [JsonIgnore] public bool IsCheckingFile => IsScanning || IsHashing;

    /// <summary>The file is in its place, and the download isn't complete yet; saved, for a crash during a virus scan.</summary>
    public bool Landed { get; set; }

    /// <summary>What a sync check compares a stream by; see <see cref="HlsDownloader"/>.</summary>
    public string? HlsFingerprint { get; set; }

    public string? LastModified { get; set; }
    public int MaxConnections { get; set; } = 8;
    public bool DirectoryChosenByUser { get; set; }
    /// <summary>The one existing file this download may replace when it finishes; null means never overwrite.</summary>
    public string? OverwritePath { get; set; }

    /// <summary>True only while the download still targets exactly <see cref="OverwritePath"/>.</summary>
    [JsonIgnore]
    public bool MayOverwrite => MayReplace(FullPath);

    /// <summary>True only when <paramref name="path"/> is exactly <see cref="OverwritePath"/>.</summary>
    public bool MayReplace(string path) => OverwritePath != null
        && string.Equals(Path.GetFullPath(OverwritePath), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);

    /// <summary>Per-download cap on top of the global limit; zero means none.</summary>
    public long SpeedLimitKBps
    {
        get => _speedLimitKBps;
        set { if (Set(ref _speedLimitKBps, value)) Limiter.BytesPerSecond = value * 1024; }
    }

    /// <summary>When false, <see cref="SpeedLimitKBps"/> goes back to none once a transfer ends.</summary>
    public bool KeepSpeedLimit { get; set; } = true;

    [JsonIgnore] public RateLimiter Limiter { get; } = new();
    public DateTime AddedAt { get; set; } = DateTime.Now;
    public int HlsSegmentCount { get; set; }

    // A stream's choices, as in HlsMaster and DashManifest: no bandwidth means the best quality.
    public long? HlsBandwidth { get; set; }
    public bool HlsAudioOnly { get; set; }
    public bool HlsSubtitles { get; set; } = true;
    /// <summary>A site's video: the choice made in the panel, as <see cref="SiteChoices"/> keys it; null is the original.</summary>
    public string? SiteChoice { get; set; }
    /// <summary>Two files joined: the sound's address, beside the video's in <see cref="Url"/>.</summary>
    public string? AudioUrl { get; set; }
    /// <summary>A site's video: the formats and sizes its parts came from, so parts of another file are never joined to them.</summary>
    public string? SiteFormats { get; set; }
    /// <summary>A live stream being recorded; see <see cref="DownloadManager.StopAndSave"/>.</summary>
    public bool IsLive { get => _isLive; set { if (Set(ref _isLive, value)) RaiseStatus(); } }

    /// <summary>A sync queue is asking the server whether this complete file changed; it stays Completed meanwhile.</summary>
    [JsonIgnore] public bool IsChecking { get => _isChecking; internal set { if (Set(ref _isChecking, value)) RaiseStatus(); } }
    /// <summary>When a sync queue last found this file unchanged on the server.</summary>
    public DateTime? LastCheckedAt { get; set; }

    /// <summary>The virus scanner is checking the downloaded file; the download counts as complete once it is done.</summary>
    [JsonIgnore] public bool IsScanning { get => _isScanning; internal set { if (Set(ref _isScanning, value)) RaiseStatus(); } }
    bool _isScanning;
    /// <summary>What the virus scan of the last try reported; null when it found nothing, or there was none.</summary>
    public string? ScanResult { get => _scanResult; set { if (Set(ref _scanResult, value)) RaiseStatus(); } }
    string? _scanResult;

    /// <summary>Shown instead of "Downloading" while a transfer reconnects or starts over.</summary>
    [JsonIgnore] public string? StatusNote { get => _statusNote; internal set { if (Set(ref _statusNote, value)) RaiseStatus(); } }
    string? _statusNote;

    /// <summary>Cancelled by <see cref="Reconnect"/>; transfers link it into each try, so their reads in flight end at once.</summary>
    [JsonIgnore] internal CancellationToken ReconnectToken => Volatile.Read(ref _reconnect).Token;
    CancellationTokenSource _reconnect = new();

    /// <summary>Ends the reads in flight, so the connections start again at once, as after sleep or a network change.</summary>
    internal void Reconnect() => Interlocked.Exchange(ref _reconnect, new CancellationTokenSource()).Cancel();

    public string FileName { get => _fileName; set { if (Set(ref _fileName, value)) Raise(nameof(FullPath)); } }
    public string Directory { get => _directory; set { if (Set(ref _directory, value)) Raise(nameof(FullPath)); } }
    public string Category { get => _category; set => Set(ref _category, value); }
    public string? QueueId { get => _queueId; set => Set(ref _queueId, value); }
    /// <summary>The site grabber project it's being downloaded for; it leaves the list once it ends, and the project keeps the result.</summary>
    public string? GrabberProjectId { get; set; }
    /// <summary>Sort key within the queue; <see cref="DownloadManager"/> keeps it numbered 1, 2, 3...</summary>
    public long QueueOrder { get; set; }
    /// <summary>The 1-based place in the queue, or 0 outside one.</summary>
    [JsonIgnore] public int QueuePosition { get => _queuePosition; internal set => Set(ref _queuePosition, value); }
    public string? Error { get => _error; set { if (Set(ref _error, value)) RaiseStatus(); } }
    public DateTime? CompletedAt { get => _completedAt; set => Set(ref _completedAt, value); }

    public long TotalBytes
    {
        get => Interlocked.Read(ref _totalBytes);
        set { Interlocked.Exchange(ref _totalBytes, value); Raise(); Raise(nameof(SizeText)); }
    }

    public DownloadStatus Status
    {
        get => _status;
        set { if (Set(ref _status, value)) { RaiseStatus(); Raise(nameof(ProgressText)); } }
    }

    public long DownloadedBytes
    {
        get => Interlocked.Read(ref _downloaded);
        set => Interlocked.Exchange(ref _downloaded, value);
    }

    public int HlsSegmentsDone
    {
        get => Volatile.Read(ref _hlsDone);
        set => Volatile.Write(ref _hlsDone, value);
    }

    /// <summary>Live segment list; guarded by <see cref="SyncRoot"/>.</summary>
    [JsonIgnore] public List<Segment> Segments { get; internal set; } = new();

    /// <summary>
    /// While a transfer runs, the progress known to be on disk; null otherwise. Guarded by
    /// <see cref="SyncRoot"/>; see <see cref="HttpDownloader"/>.
    /// </summary>
    [JsonIgnore] internal List<Segment>? Checkpoint { get; set; }

    /// <summary>What is saved: while a transfer runs, its last checkpoint, so saved progress never claims a byte not yet on disk.</summary>
    [JsonPropertyName("Segments")]
    public List<Segment> SegmentsSnapshot
    {
        get
        {
            lock (SyncRoot)
                return (Checkpoint ?? Segments).Select(s => s.Clone()).ToList();
        }
        set => Segments = value ?? new();
    }

    [JsonIgnore] public object SyncRoot { get; } = new();
    [JsonIgnore] public string FullPath => Path.Combine(Directory, FileName);

    /// <summary>
    /// The temporary folder the part file was made in; null keeps it beside the file. It stays the same until
    /// the download starts over, whatever the settings say meanwhile.
    /// </summary>
    public string? PartFolder { get; set; }

    /// <summary>
    /// Where a fresh run makes its part files when the settings name no temporary folder. Set for a download started
    /// before its name and folder were settled (see <see cref="DownloadManager.StartEarly"/>), so they can still change.
    /// </summary>
    [JsonIgnore] internal string? OwnPartFolder { get; set; }

    /// <summary>
    /// Completes once the user has settled the download's name and folder; until then a transfer that has every byte
    /// waits before putting its file in place. See <see cref="DownloadManager.StartEarly"/>.
    /// </summary>
    [JsonIgnore] internal Task Settled { get; set; } = Task.CompletedTask;

    /// <summary>Waits for <see cref="Settled"/>; a run stopped meanwhile, or just before, puts nothing in place.</summary>
    internal async Task UntilSettledAsync(CancellationToken ct)
    {
        await Settled.WaitAsync(ct);
        ct.ThrowIfCancellationRequested();
    }

    // In a temporary folder, part files are named after the download, so two files of one name never share
    // one, and a rename leaves them where they are.
    [JsonIgnore] public string PartPath => PartBase + ".admpart";
    /// <summary>A stream's folder of segments.</summary>
    [JsonIgnore] public string PartsFolderPath => PartBase + ".admparts";
    /// <summary>Where ffmpeg writes a repackaged stream.</summary>
    [JsonIgnore] internal string RemuxPath => PartBase + ".admremux";
    string PartBase => PartFolder == null ? FullPath : Path.Combine(PartFolder, Id.ToString("N"));
    [JsonIgnore] public double Speed => _speed;

    [JsonIgnore]
    public int ActiveConnections
    {
        get
        {
            if (IsStream) return StreamRun?.AtWork ?? 0;
            lock (SyncRoot) return Segments.Count(s => s.Active);
        }
    }

    /// <summary>A stream's latest run in this session, for the progress window; null before one. Not saved.</summary>
    [JsonIgnore] internal StreamRun? StreamRun { get; set; }

    /// <summary>
    /// The progress window's list of connections: a file's, one for each segment; a stream's, as its latest run in
    /// this session has them.
    /// </summary>
    public IReadOnlyList<ConnectionInfo> ConnectionsNow()
    {
        if (IsStream) return StreamRun?.Connections() ?? [];
        lock (SyncRoot) return Segments.Select(s => new ConnectionInfo(s.Downloaded, s.Stage)).ToList();
    }

    /// <summary>
    /// The progress window's strip: a file's by its bytes, marked where each segment starts; a stream's by its parts.
    /// Null when there is nothing to draw it from, and the progress so far is all it can show.
    /// </summary>
    public Strip? StripNow()
    {
        if (Status == DownloadStatus.Completed) return null;
        if (IsStream) return StreamRun?.StripNow();
        var total = TotalBytes;
        if (total <= 0) return null;
        lock (SyncRoot)
            return new Strip(total, Segments.Select(s => new StripPiece(
                s.Start, Math.Min(s.End, total - 1) - s.Start + 1, s.Downloaded, s.Active && !s.IsComplete, s.Start > 0)).ToList());
    }

    [JsonIgnore]
    public double Progress
    {
        get
        {
            if (Status == DownloadStatus.Completed) return 100;
            var total = TotalBytes;
            // A stream of unknown size goes by its parts; one whose parts' sizes are known, by its bytes.
            if (IsStream && total <= 0)
                return HlsSegmentCount > 0 ? 100.0 * HlsSegmentsDone / HlsSegmentCount : 0;
            return total > 0 ? Math.Min(100, 100.0 * DownloadedBytes / total) : 0;
        }
    }

    /// <summary>
    /// Whether <see cref="Progress"/> stands for anything: the download finished, or its size, or a stream's count of
    /// parts, is known. A file whose server sends no size reads 0% until it ends, however much has come.
    /// </summary>
    [JsonIgnore]
    public bool ProgressKnown => Status == DownloadStatus.Completed || TotalBytes > 0 || (IsStream && HlsSegmentCount > 0);

    /// <summary>The progress as a percentage; blank while it isn't known.</summary>
    [JsonIgnore] public string ProgressText => Status == DownloadStatus.Completed ? "100%" : ProgressKnown ? $"{Progress:0.0}%" : "";
    [JsonIgnore] public string SizeText => TotalBytes > 0 ? Format.Bytes(TotalBytes) : (DownloadedBytes > 0 ? Format.Bytes(DownloadedBytes) : StatusWords.Current.UnknownSize);
    [JsonIgnore] public string SpeedText => Status == DownloadStatus.Downloading ? Format.Speed(_speed) : "";

    /// <summary>Seconds until the download finishes at its current speed; null when it isn't running or that can't be told yet.</summary>
    [JsonIgnore]
    public double? SecondsLeft
    {
        get
        {
            if (Status != DownloadStatus.Downloading || IsCheckingFile || _speed < 1) return null;
            var total = TotalBytes;
            if (IsStream && total <= 0)
            {
                var done = HlsSegmentsDone;
                if (done == 0 || HlsSegmentCount == 0) return null;
                var perSegment = (double)DownloadedBytes / done;
                return perSegment * (HlsSegmentCount - done) / _speed;
            }
            return total > 0 ? Math.Max(0, total - DownloadedBytes) / _speed : null;
        }
    }

    [JsonIgnore] public string EtaText => SecondsLeft is double seconds ? Format.Eta(TimeSpan.FromSeconds(seconds)) : "";

    [JsonIgnore]
    public string StatusText => StatusWords.Current.StatusOf(this);

    /// <summary>How the list colours and marks the download's state; it follows <see cref="StatusText"/>.</summary>
    [JsonIgnore]
    public StatusLook Look => Status switch
    {
        DownloadStatus.Completed when IsChecking => StatusLook.Active,
        // The scanner's report stays on a finished file, and is worth a look.
        DownloadStatus.Completed when ScanResult != null => StatusLook.Warning,
        DownloadStatus.Completed => StatusLook.Done,
        DownloadStatus.Failed => StatusLook.Failed,
        DownloadStatus.Downloading => StatusLook.Active,
        // Waiting to try again, or paused after a try that failed.
        _ when Error != null => StatusLook.Warning,
        DownloadStatus.Queued => StatusLook.Waiting,
        _ => StatusLook.Paused,
    };

    /// <summary>How the last try ended, for the Properties dialog.</summary>
    [JsonIgnore]
    public string LastResultText => StatusWords.Current.LastResultOf(this);

    void RaiseStatus()
    {
        Raise(nameof(StatusText));
        Raise(nameof(LastResultText));
        Raise(nameof(Look));
    }

    public void AddDownloaded(long bytes) => Interlocked.Add(ref _downloaded, bytes);

    public void AddHlsSegmentDone() => Interlocked.Increment(ref _hlsDone);

    /// <summary>
    /// Samples throughput: the speed is what arrived over the last second, so it reads the same however often this
    /// is called, and follows the transfer within a second. Call from one thread.
    /// </summary>
    public void Tick(DateTime now)
    {
        var bytes = DownloadedBytes;
        // Progress taken back, as a failed part's is, starts the second again; the speed stands until it can be told.
        if (_samples.Count > 0 && bytes < _samples[^1].Bytes) _samples.Clear();
        _samples.Add((now, bytes));
        // The second starts at the newest sample that is a second old or more.
        while (_samples.Count > 2 && now - _samples[1].At >= SpeedWindow) _samples.RemoveAt(0);
        var (since, from) = _samples[0];
        var seconds = (now - since).TotalSeconds;
        if (seconds > 0) _speed = (bytes - from) / seconds;
        RaiseProgress();
    }

    public void ResetSpeed()
    {
        _speed = 0;
        _samples.Clear();
        RaiseProgress();
    }

    public void RaiseProgress()
    {
        Raise(nameof(Progress));
        Raise(nameof(ProgressText));
        Raise(nameof(SpeedText));
        Raise(nameof(EtaText));
        Raise(nameof(SecondsLeft));
        Raise(nameof(SizeText));
        Raise(nameof(ActiveConnections));
        Raise(nameof(ResumeText));
    }

    // Grid rows use this as their UI Automation name, which screen readers announce.
    public override string ToString() => FileName.Length > 0 ? FileName : Url;
}

public sealed class Category
{
    public string Name { get; set; } = "";
    public List<string> Extensions { get; set; } = new();

    /// <summary>Folder name under the download root, or an absolute path.</summary>
    public string Folder { get; set; } = "";
}

public sealed class QueueSchedule
{
    public bool Enabled { get; set; }
    public TimeSpan? StartTime { get; set; }
    public TimeSpan? StopTime { get; set; }
    public List<DayOfWeek> Days { get; set; } = Enum.GetValues<DayOfWeek>().ToList();
    public CompletionAction OnComplete { get; set; }
    /// <summary>With <see cref="CompletionAction.Shutdown"/>: close programs even if they have unsaved work.</summary>
    public bool ForceCloseApps { get; set; }
    /// <summary>A file to open, or a program to run, when the queue finishes, before <see cref="OnComplete"/>.</summary>
    public string? RunWhenDone { get; set; }
    /// <summary>
    /// Sync queues only: after the scheduled start, start again this many minutes after the last start,
    /// until the stop time (or the end of the day). Zero runs once.
    /// </summary>
    public int RepeatMinutes { get; set; }
    public DateTime? LastStartedOn { get; set; }
    public DateTime? LastStartedAt { get; set; }
    public DateTime? LastStoppedOn { get; set; }
}

public sealed class DownloadQueue : Observable
{
    public const string MainId = "main";
    public const string SyncId = "sync";

    string _name = "";
    bool _isRunning;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get => _name; set => Set(ref _name, value); }
    /// <summary>Fixed for the two main queues; added queues may change it.</summary>
    public QueueKind Kind { get; set; }
    [JsonIgnore] public bool IsMain => Id is MainId or SyncId;
    public int MaxConcurrent { get; set; } = 2;
    /// <summary>The site grabber project whose queue this is, which gets its files through <see cref="IQueueFeed"/>.</summary>
    public string? GrabberProjectId { get; set; }
    /// <summary>Starts whenever ADM starts; see <see cref="DownloadManager.StartQueuesOnLaunch"/>.</summary>
    public bool StartOnLaunch { get; set; }
    /// <summary>
    /// A failed file goes to the end of the queue and is tried again. Without a limit, a file that can
    /// never download keeps the queue from finishing.
    /// </summary>
    public bool LimitRetries { get; set; } = true;
    public int RetriesPerFile { get; set; } = 5;
    public QueueSchedule Schedule { get; set; } = new();

    [JsonIgnore] public bool IsRunning { get => _isRunning; set => Set(ref _isRunning, value); }

    // Lists and combo boxes of queues use this as their UI Automation name, which screen readers announce.
    public override string ToString() => Name;
}

public sealed class Settings
{
    public const string DefaultUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36";

    public int DefaultConnections { get; set; } = 8;
    /// <summary>What set <see cref="DefaultConnections"/>; see <see cref="ConnectionPresets"/>. Older settings stay Custom and keep their number.</summary>
    public ConnectionType ConnectionType { get; set; } = ConnectionType.Custom;
    public long SpeedLimitKBps { get; set; }
    /// <summary>Whether <see cref="SpeedLimitKBps"/> applies; the Speed Limiter menu turns it on and off.</summary>
    public bool SpeedLimiterOn { get; set; } = true;
    public bool SpeedLimiterOnAtStartup { get; set; }
    public string DownloadRoot { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "ADM");
    /// <summary>Where new part files go until they are complete; empty keeps them beside the file.</summary>
    public string? TempFolder { get; set; }
    /// <summary>A resume or a sync check doesn't count a changed Last-Modified; size and ETag still do.</summary>
    public bool IgnoreModificationTime { get; set; }
    /// <summary>Finished files get the mark of the web, as a browser's downloads do; see <see cref="ZoneMark"/>.</summary>
    public bool MarkDownloads { get; set; } = true;
    /// <summary>The program that scans each finished file before it counts as complete; empty for none.</summary>
    public string VirusScanner { get; set; } = "";
    /// <summary>The scanner's arguments, where {file} stands for the file; without it, the file goes last.</summary>
    public string VirusScannerArguments { get; set; } = "";
    public string UserAgent { get; set; } = DefaultUserAgent;
    public bool MonitorClipboard { get; set; } = true;
    public DuplicateAction OnDuplicate { get; set; }
    public List<ServerLimit> ServerLimits { get; set; } = new();

    // Browser capture; see CaptureRules. Lists are space-separated and accept * wildcards.
    public string CaptureExtensions { get; set; } = CaptureRules.DefaultExtensions;
    public string CaptureExceptionSites { get; set; } = CaptureRules.DefaultExceptionSites;
    public string CaptureExceptionAddresses { get; set; } = "";
    public long CaptureMinSizeKB { get; set; }

    // Keys held while clicking a link, as in SpecialKeys: one combination leaves the download to the
    // browser, the other hands any link to ADM.
    public string PreventCaptureKeys { get; set; } = "Alt";
    public string ForceCaptureKeys { get; set; } = "Insert";
    /// <summary>A left click on a link with the force keys held sends the link itself, not only a download it starts.</summary>
    public bool ForceOnLinkClick { get; set; } = true;
    /// <summary>The second cancel of a download taken from the browser offers to stop capturing it.</summary>
    public bool PromptAfterTwoCancels { get; set; } = true;

    // Items in the browser's context menu.
    public bool MenuDownloadLink { get; set; } = true;
    public bool MenuDownloadAllLinks { get; set; } = true;

    /// <summary>Saves an HLS stream's subtitles next to the video.</summary>
    public bool DownloadSubtitles { get; set; } = true;

    // The download panel the extension shows over web players.
    public VideoPanelMode VideoPanel { get; set; } = VideoPanelMode.Full;
    /// <summary>Media files known to be smaller than this get no panel; HLS streams always do.</summary>
    public long VideoPanelMinSizeKB { get; set; } = 300;
    /// <summary>Space-separated site patterns, as in <see cref="SiteList"/>, where the panel never appears.</summary>
    public string VideoPanelExceptionSites { get; set; } = "";

    public ProxySettings Proxy { get; set; } = new();
    /// <summary>A download taken from the browser that gets no answer, or a 403, tries the browser's own proxy; see <see cref="BrowserProxy"/>.</summary>
    public bool UseBrowserProxyOnErrors { get; set; } = true;
    public List<SiteLogin> SiteLogins { get; set; } = new();

    /// <summary>Sound files for download and queue events; an event with no entry plays nothing.</summary>
    public List<EventSound> Sounds { get; set; } = new();

    // Download limits, for connections with a data allowance: no more than QuotaMB every QuotaHours (1 to 23).
    public bool QuotaEnabled { get; set; }
    public long QuotaMB { get; set; } = 1000;
    public int QuotaHours { get; set; } = 4;

    /// <summary>How long a download keeps reconnecting through an outage without progress; 0 keeps on until stopped.</summary>
    public int ReconnectMinutes { get; set; } = 60;

    // The main window. Columns and Toolbar are saved as the user left them; ListColumns.Normalize and
    // ToolbarButtons.Normalize bring a layout from another version up to date.
    public List<ListColumn> Columns { get; set; } = ListColumns.Defaults();
    public DownloadSortKey SortKey { get; set; } = DownloadSortKey.OrderAdded;
    public bool SortDescending { get; set; } = true;
    public List<string> Toolbar { get; set; } = ToolbarButtons.Defaults();
    public bool LargeToolbarButtons { get; set; } = true;
    /// <summary>The site grabber's named file masks, which the user may change; see <see cref="GrabberFilter.Defaults"/>.</summary>
    public List<GrabberFilter> GrabberFilters { get; set; } = GrabberFilter.Defaults();
    /// <summary>The user's own grabber templates; ADM's are <see cref="ADM.Core.GrabberTemplates.BuiltIn"/>.</summary>
    public List<GrabberTemplate> GrabberTemplates { get; set; } = new();
    public bool ShowCategories { get; set; } = true;
    public SearchTargets SearchIn { get; set; } = SearchTargets.Names | SearchTargets.Descriptions;
    public bool SearchMatchCase { get; set; }
    public bool SearchWholeString { get; set; }
    public DoubleClickAction OnDoubleClick { get; set; } = DoubleClickAction.Open;
    public bool ConfirmDeleteCompleted { get; set; } = true;
    public bool ConfirmDeleteUnfinished { get; set; } = true;
    public bool ShowCompleteDialog { get; set; } = true;

    // A download's own progress window.
    public ProgressWindowView ProgressView { get; set; } = ProgressWindowView.Normal;
    public bool ProgressSpeedTab { get; set; } = true;
    public bool ProgressWhenDoneTab { get; set; } = true;
    public bool ProgressHideTabButtons { get; set; } = true;

    // How the windows look; see Appearance.
    public ThemeChoice Theme { get; set; } = ThemeChoice.FollowWindows;
    public string FontFamily { get; set; } = Appearance.DefaultFontFamily;
    public double FontSize { get; set; } = Appearance.DefaultFontSize;

    /// <summary>The code of the language to use, from its file in the Languages folder; takes effect at the next start.</summary>
    public string Language { get; set; } = "en";

    /// <summary>Start with Windows has been turned on once, as IDM's is out of the box; from then on only Options turns it on or off.</summary>
    public bool StartWithWindowsDefaultApplied { get; set; }

    /// <summary>Turns Start with Windows on the first time ADM starts, an upgraded copy included.</summary>
    /// <param name="turnOn">Writes the startup entry. If it throws, nothing is marked, and the next start tries again.</param>
    /// <returns>Whether the settings changed and need saving.</returns>
    public bool ApplyStartWithWindowsDefault(Action turnOn)
    {
        if (StartWithWindowsDefaultApplied) return false;
        turnOn();
        StartWithWindowsDefaultApplied = true;
        return true;
    }

    /// <summary>The folder for a new part file: the temporary folder, or null for beside the file.</summary>
    internal string? NewPartFolder() => string.IsNullOrWhiteSpace(TempFolder) ? null : Path.GetFullPath(TempFolder);

    public int ConnectionsFor(string url, int requested)
    {
        foreach (var limit in ServerLimits)
            if (SiteList.Matches(limit.Sites, url))
                return Math.Min(requested, Math.Max(1, limit.Connections));
        return requested;
    }
}

public enum VideoPanelMode { Off, Full, Mini }

public enum SoundEvent { DownloadComplete, DownloadFailed, QueueStarted, QueueFinished }

/// <summary>The sound an event plays; a file that is missing plays nothing.</summary>
public sealed class EventSound
{
    public SoundEvent Event { get; set; }
    public bool On { get; set; }
    public string File { get; set; } = "";
}

public enum ThemeChoice { FollowWindows, Light, Dark }

/// <summary>What the look's settings mean. A size or a family edited by hand can't stop the windows from opening.</summary>
public static class Appearance
{
    public const string DefaultFontFamily = "Segoe UI";
    public const double DefaultFontSize = 13;
    public const double MinFontSize = 8;
    public const double MaxFontSize = 24;

    public static bool IsDark(ThemeChoice choice, bool windowsUsesLight) => choice switch
    {
        ThemeChoice.Light => false,
        ThemeChoice.Dark => true,
        _ => !windowsUsesLight,
    };

    public static double FontSizeOf(double saved) =>
        double.IsFinite(saved) ? Math.Clamp(saved, MinFontSize, MaxFontSize) : DefaultFontSize;

    public static string FontFamilyOf(string? saved) =>
        string.IsNullOrWhiteSpace(saved) ? DefaultFontFamily : saved.Trim();
}

public enum ConnectionType { Low, Medium, High, Custom }

public static class ConnectionPresets
{
    /// <summary>The number of connections a type sets; null for Custom, which keeps <see cref="Settings.DefaultConnections"/>.</summary>
    public static int? Connections(ConnectionType type) => type switch
    {
        ConnectionType.Low => 2,
        ConnectionType.Medium => 8,
        ConnectionType.High => 16,
        _ => null,
    };
}

public enum ProxyMode { System, None, Manual, Script, AutoDetect }

public enum ProxyKind { Http, Socks5, Https, Socks4 }

/// <summary>How downloads reach the internet. Immutable, because each value keys a cached HTTP client.</summary>
public sealed record ProxySettings
{
    public ProxyMode Mode { get; init; }
    public ProxyKind Kind { get; init; }
    public string Host { get; init; } = "";
    public int Port { get; init; } = 8080;
    public string? UserName { get; init; }
    /// <summary>Encrypted with <see cref="Secrets"/>; never plain text.</summary>
    public string? ProtectedPassword { get; init; }
    /// <summary>Space-separated site patterns, as in <see cref="SiteList"/>, that are reached directly.</summary>
    public string Bypass { get; init; } = "localhost 127.0.0.1";
    /// <summary>The address of the proxy script <see cref="ProxyMode.Script"/> asks; see <see cref="PacProxy"/>.</summary>
    public string ScriptUrl { get; init; } = "";
}

/// <summary>A login offered to every site matching <see cref="Sites"/> when the server asks for one.</summary>
public sealed class SiteLogin
{
    /// <summary>Space-separated site patterns, as in <see cref="SiteList"/>.</summary>
    public string Sites { get; set; } = "";
    public string UserName { get; set; } = "";
    /// <summary>Encrypted with <see cref="Secrets"/>; never plain text.</summary>
    public string? ProtectedPassword { get; set; }
}

/// <summary>Caps connections for servers that refuse too many from one client.</summary>
public sealed class ServerLimit
{
    /// <summary>Space-separated site patterns, as in <see cref="SiteList"/>.</summary>
    public string Sites { get; set; } = "";
    public int Connections { get; set; } = 1;
}

public sealed class AppState
{
    public Settings Settings { get; set; } = new();
    public List<Category> Categories { get; set; } = new();
    public List<DownloadQueue> Queues { get; set; } = new();
    public List<DownloadItem> Downloads { get; set; } = new();
    /// <summary>Saved, so restarting the app doesn't reset the download limit.</summary>
    public QuotaUsage Quota { get; set; } = new();
    /// <summary>The site grabber's projects; each keeps the rest in its own file, as a big site's list is long.</summary>
    public List<GrabberProjectInfo> GrabberProjects { get; set; } = new();
}

/// <summary>A site grabber project as the state knows it: enough to find its file and its queue.</summary>
public sealed class GrabberProjectInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string QueueId { get; set; } = "";
}

/// <summary>What has been downloaded in the current download-limit period; see <see cref="Settings.QuotaMB"/>.</summary>
public sealed class QuotaUsage
{
    public DateTime? PeriodStart { get; set; }
    public long Bytes { get; set; }

    /// <summary>Counts bytes; the first bytes after a period has ended start the next one.</summary>
    public void Add(long bytes, DateTime now, int hours)
    {
        Roll(now, hours);
        if (bytes <= 0) return;
        PeriodStart ??= now;
        Bytes += bytes;
    }

    /// <summary>Forgets a period whose hours are up.</summary>
    public void Roll(DateTime now, int hours)
    {
        if (PeriodStart is DateTime start && now - start >= TimeSpan.FromHours(hours))
        {
            PeriodStart = null;
            Bytes = 0;
        }
    }

    public bool Exceeded(Settings settings) => settings.QuotaEnabled && Bytes >= settings.QuotaMB * 1024 * 1024;

    public DateTime? ResumesAt(int hours) => PeriodStart?.AddHours(hours);
}

public sealed class DownloadRequest
{
    public string Url { get; set; } = "";

    /// <summary>What the finished file should hash to, as the user gave it.</summary>
    public string? Sha256 { get; set; }
    public string? Referrer { get; set; }
    /// <summary>The page the link was found on; null takes the referer.</summary>
    public string? PageUrl { get; set; }
    public string? Description { get; set; }
    public string? Cookies { get; set; }
    public string? UserAgent { get; set; }
    /// <summary>The proxy the browser uses for this address, as in <see cref="ADM.Core.BrowserProxy.Parse"/>.</summary>
    public string? BrowserProxy { get; set; }
    public string? UserName { get; set; }
    /// <summary>Plain text in memory only; <see cref="DownloadManager.Add"/> stores it encrypted.</summary>
    public string? Password { get; set; }
    public string? FileName { get; set; }
    public string? Directory { get; set; }
    public string? Category { get; set; }
    public int? Connections { get; set; }
    public DownloadKind Kind { get; set; }
    /// <summary>Taken over from the browser automatically, rather than sent by the user.</summary>
    public bool Captured { get; set; }
    /// <summary>The site grabber project it's being downloaded for.</summary>
    public string? GrabberProjectId { get; set; }
    /// <summary>The one existing file it may replace when it finishes.</summary>
    public string? OverwritePath { get; set; }
    public long? HlsBandwidth { get; set; }
    public bool HlsAudioOnly { get; set; }
    /// <summary>Null follows <see cref="Settings.DownloadSubtitles"/>.</summary>
    public bool? HlsSubtitles { get; set; }
    /// <summary>See <see cref="DownloadItem.SiteChoice"/>.</summary>
    public string? SiteChoice { get; set; }
    /// <summary>See <see cref="DownloadItem.AudioUrl"/>.</summary>
    public string? AudioUrl { get; set; }

    /// <summary>This request for another address. Cookies are for their own site, so they go only if the address is on it; a login stays, since the dialog shows it.</summary>
    public DownloadRequest ForAddress(string url)
    {
        var copy = (DownloadRequest)MemberwiseClone();
        copy.Url = url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var target) || !Net.SameOrigin(target, Url)) copy.Cookies = null;
        return copy;
    }
}
