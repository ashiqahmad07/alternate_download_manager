using System.Collections.ObjectModel;

namespace ADM.Core;

/// <summary>
/// Owns downloads, queues and the scheduler. Every public member must be called on the
/// UI thread captured at construction; transfers run on the thread pool.
/// </summary>
public sealed class DownloadManager
{
    /// <param name="Stop">Ends a live recording and saves it; see <see cref="StopAndSave"/>.</param>
    /// <param name="Check">A sync queue's check of a complete file, whose task returns whether it changed.</param>
    /// <param name="Scan">The virus scan of a finished transfer's file, whose task returns a <see cref="ScanOutcome"/>.</param>
    /// <param name="Hash">The check of a finished transfer's file against its SHA-256, whose task returns the hash.</param>
    sealed record Running(CancellationTokenSource Cts, CancellationTokenSource Stop, Task Task, bool Check = false, bool Scan = false, bool Hash = false);

    static readonly TimeSpan RefreshWait = TimeSpan.FromMinutes(10);

    const string ScanStopped = "The virus scan was stopped before it finished";

    readonly SynchronizationContext _ui;
    readonly string _statePath;
    readonly StateWriter _writer;
    readonly Dictionary<Guid, Running> _running = new();
    readonly HashSet<Guid> _requeueOnCancel = new();
    readonly HashSet<string> _queuesThatRan = new();
    // Queue retries: failed tries per file since its queue started, and when each may run again.
    readonly Dictionary<Guid, int> _failures = new();
    readonly Dictionary<Guid, DateTime> _retryAt = new();
    // Complete files a running sync queue has yet to check.
    readonly HashSet<Guid> _toCheck = new();
    // Download limit: each transfer's bytes already counted, and the files outside a queue to restart when it lifts.
    readonly Dictionary<Guid, long> _counted = new();
    readonly HashSet<Guid> _resumeAfterLimit = new();
    // Downloads whose files MoveAsync is moving.
    readonly HashSet<Guid> _moving = new();
    // Queues whose files come while they run, by queue.
    readonly Dictionary<string, IQueueFeed> _feeds = new();
    // Downloads started while their dialog is open, by id; see StartEarly.
    readonly Dictionary<Guid, Early> _early = new();
    bool _limitHolds;
    // Archives being read, for the download limit and exit to stop. Locked, as they end off the UI thread.
    readonly List<ZipRead> _zipReads = new();
    // Bytes read from a server outside a transfer since the last tick: archives previewed, and sites explored.
    long _otherBytes;
    // Completes when the download limit lets go; a new one each time it takes hold. Read off the UI thread.
    TaskCompletionSource _limitLifted = Lifted();
    DateTime _lastSave = DateTime.MinValue;
    // When a save the writer made in the background failed; another is tried a while after.
    DateTime? _saveFailedAt;
    DateTime _refreshSince;
    bool _dirty;
    bool _shuttingDown;

    public DownloadManager(string statePath, SynchronizationContext ui)
    {
        _statePath = statePath;
        _writer = new StateWriter(statePath);
        _ui = ui;
        State = Store.Load(statePath);
        Items = new ObservableCollection<DownloadItem>(State.Downloads);
        Queues = new ObservableCollection<DownloadQueue>(State.Queues);
        foreach (var item in Items)
        {
            OldName.RenameParts(item);
            if (item.QueueId != null && QueueFor(item) == null) item.QueueId = null;
            CompleteIfLanded(item);
            if (item.Status == DownloadStatus.Completed && QueueFor(item)?.Kind != QueueKind.Sync) item.QueueId = null;
            // A transfer cut off by the last exit waits for its queue again; outside a queue nothing would restart it.
            if (item.Status is DownloadStatus.Downloading or DownloadStatus.Queued)
                item.Status = item.QueueId != null ? DownloadStatus.Queued : DownloadStatus.Paused;
        }
        OldName.RenameOrphans(State.Settings.NewPartFolder());
        DeleteEarlyOrphans();
        foreach (var queue in Queues) Renumber(queue);
        if (State.Settings.SpeedLimiterOnAtStartup) State.Settings.SpeedLimiterOn = true;
        ApplySettings();
    }

    /// <summary>
    /// The last exit cut off a download whose file had landed, as a crash during its virus scan does. It only
    /// has to complete: starting it would download the file again under a new name. If the file has gone, it does start again.
    /// </summary>
    void CompleteIfLanded(DownloadItem item)
    {
        if (!item.Landed) return;
        item.Landed = false;
        if (item.Status == DownloadStatus.Completed || !File.Exists(item.FullPath)) return;
        if (!string.IsNullOrWhiteSpace(State.Settings.VirusScanner)) item.ScanResult = ScanStopped;
        // Cut off during its check: a match already found stays, but this is never one.
        if (item.ExpectedSha256 != null && item.HashResult == null) item.HashResult = StatusWords.Current.HashNotChecked;
        Complete(item);
    }

    /// <summary>Works out a file's SHA-256; tests hold it open, or make it fail.</summary>
    internal Func<string, CancellationToken, Task<string>> Hasher { get; set; } = FileHash.Sha256Async;

    public AppState State { get; }
    /// <summary>Where site grabber projects keep their files, each in a folder of its own, beside the state.</summary>
    public string GrabberFolder => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_statePath))!, "grabber");
    public ObservableCollection<DownloadItem> Items { get; }
    public ObservableCollection<DownloadQueue> Queues { get; }
    public RateLimiter Limiter { get; } = new();
    public DownloadQueue MainQueue => Queues.First(q => q.Id == DownloadQueue.MainId);
    public DownloadQueue SyncQueue => Queues.First(q => q.Id == DownloadQueue.SyncId);
    public int ActiveCount => _running.Count;
    public double TotalSpeed => Items.Where(i => _running.ContainsKey(i.Id)).Sum(i => i.Speed);

    /// <summary>While the download limit holds every transfer, the time they carry on; otherwise null.</summary>
    public DateTime? LimitHoldsUntil => _limitHolds ? State.Quota.ResumesAt(LimitHours) : null;

    /// <summary>How long a failed queue file waits before its next try; tests shorten it.</summary>
    internal TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>The download waiting for the browser to capture a fresh address; see <see cref="BeginRefresh"/>.</summary>
    public DownloadItem? AwaitingNewAddress { get; private set; }

    public event Action<DownloadItem>? DownloadCompleted;
    public event Action<DownloadItem>? DownloadFailed;
    /// <summary>A stopped queue started, and has something to do.</summary>
    public event Action<DownloadQueue>? QueueStarted;
    public event Action<DownloadQueue>? QueueFinished;
    /// <summary>The download limit stopped every transfer; they resume at the given time.</summary>
    public event Action<DateTime>? QuotaReached;

    const string LimitReached = "download limit reached";

    public bool IsRunning(DownloadItem item) => _running.ContainsKey(item.Id);

    public void ApplySettings()
    {
        Limiter.BytesPerSecond = State.Settings.SpeedLimiterOn ? State.Settings.SpeedLimitKBps * 1024 : 0;
        MarkDirty();
    }

    /// <summary>
    /// Points a download that isn't running at another http, https or ftp address. Progress stays, and the
    /// resume checks decide whether it still fits. Browser cookies and the download's login go when the
    /// scheme, host or port changes, since they belong to the old site; a login in the new address is kept.
    /// </summary>
    public bool SetAddress(DownloadItem item, string url)
    {
        if (_running.ContainsKey(item.Id)) return false;
        var (clean, addressUser, addressPassword) = Links.SplitLogin(url.Trim());
        if (!Uri.TryCreate(clean, UriKind.Absolute, out var uri) || !Links.IsDownloadable(uri))
            return false;
        if (!Uri.TryCreate(item.Url, UriKind.Absolute, out var old)
            || old.Scheme != uri.Scheme || !string.Equals(old.Host, uri.Host, StringComparison.OrdinalIgnoreCase) || old.Port != uri.Port)
        {
            item.Cookies = null;
            item.UserName = null;
            item.ProtectedPassword = null;
        }
        if (addressUser != null)
        {
            item.UserName = addressUser;
            item.ProtectedPassword = Secrets.Protect(addressPassword);
        }
        item.Url = clean;
        if (AwaitingNewAddress == item) AwaitingNewAddress = null;
        MarkDirty();
        return true;
    }

    /// <summary>A move is under way; <see cref="Start"/> and the queues leave the download alone meanwhile.</summary>
    public bool IsMoving(DownloadItem item) => _moving.Contains(item.Id);

    /// <summary>
    /// Moves or renames a download that isn't running: a finished one's file, or an unfinished one's part
    /// file and HLS parts folder, so it keeps its progress. A missing folder is created. Any failure throws
    /// and leaves the download and its files as they were.
    /// </summary>
    /// <param name="replace">The user agreed to replace an existing file at <paramref name="newPath"/>.</param>
    public async Task MoveAsync(DownloadItem item, string newPath, bool replace)
    {
        if (_running.ContainsKey(item.Id)) throw new InvalidOperationException("Pause the download before moving it.");
        if (_moving.Contains(item.Id)) throw new InvalidOperationException("This download is already being moved.");
        var target = Path.GetFullPath(newPath);
        if (item.FileName.Length > 0 && string.Equals(target, Path.GetFullPath(item.FullPath), StringComparison.OrdinalIgnoreCase)) return;
        var folder = Path.GetDirectoryName(target)!;
        var name = Path.GetFileName(target);
        if (File.Exists(target) && !replace) throw new IOException($"A file named {name} already exists in {folder}.");

        var finished = item.Status == DownloadStatus.Completed;
        var from = item.FileName.Length > 0 ? item.FullPath : null;
        // Part files in a temporary folder are named after the download, not its file, so they stay put.
        var partsBeside = item.PartFolder == null;
        _moving.Add(item.Id);
        try
        {
            await Task.Run(() => MoveFiles(from, target, finished, replace, partsBeside));
        }
        finally
        {
            _moving.Remove(item.Id);
        }

        item.Directory = folder;
        item.FileName = name;
        item.DirectoryChosenByUser = true;
        // A finished file has replaced the target already; an unfinished one replaces it when it finishes.
        item.OverwritePath = !finished && replace && File.Exists(target) ? target : null;
        MarkDirty();
    }

    static void MoveFiles(string? from, string target, bool finished, bool replace, bool partsBeside)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (from == null) return;
        if (finished)
        {
            File.Move(from, target, replace);
            return;
        }
        if (!partsBeside) return;

        // An unfinished download's progress is in its part file and, for HLS, its parts folder. If one
        // can't move, whatever already moved goes back, so the download is left as it was.
        var moved = new List<(string From, string To, bool Folder)>();
        try
        {
            if (File.Exists(from + ".admpart"))
            {
                File.Move(from + ".admpart", target + ".admpart");
                moved.Add((from + ".admpart", target + ".admpart", false));
            }
            if (Directory.Exists(from + ".admparts"))
            {
                Directory.Move(from + ".admparts", target + ".admparts");
                moved.Add((from + ".admparts", target + ".admparts", true));
            }
        }
        catch
        {
            foreach (var (back, at, isFolder) in Enumerable.Reverse(moved))
            {
                try
                {
                    if (isFolder) Directory.Move(at, back);
                    else File.Move(at, back);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            throw;
        }
    }

    /// <summary>The login offered to this download's server. A null password keeps the saved one; an empty user name clears both.</summary>
    public void SetLogin(DownloadItem item, string? userName, string? password)
    {
        if (string.IsNullOrWhiteSpace(userName))
        {
            item.UserName = null;
            item.ProtectedPassword = null;
        }
        else
        {
            item.UserName = userName;
            if (password != null) item.ProtectedPassword = Secrets.Protect(password);
        }
        MarkDirty();
    }

    public DownloadItem? FindDuplicate(string url) => Items.FirstOrDefault(i => i.Url == url && i.GrabberProjectId == null);

    /// <summary>
    /// The download already in the list for this link and this choice of it: a site's video in another quality, or
    /// a stream's other quality or its sound alone, shares the address but is another file.
    /// </summary>
    public DownloadItem? FindDuplicate(DownloadRequest request)
    {
        var url = Links.SplitLogin(request.Url).Url;
        return Items.FirstOrDefault(i => i.Url == url && i.GrabberProjectId == null
            && i.SiteChoice == request.SiteChoice && i.HlsBandwidth == request.HlsBandwidth
            && i.HlsAudioOnly == request.HlsAudioOnly && i.AudioUrl == request.AudioUrl);
    }

    /// <param name="whenDuplicate">
    /// Overrides <see cref="Settings.OnDuplicate"/> for a link already in the list. "Ask" is the
    /// UI's job; when it reaches the engine, the link is added again with a numbered name.
    /// </param>
    public DownloadItem Add(DownloadRequest request, bool startNow, DownloadQueue? queue = null, DuplicateAction? whenDuplicate = null)
    {
        var item = AddQuietly(request, startNow, queue, whenDuplicate, FindDuplicate(request));
        if (!startNow) Pump();
        return item;
    }

    /// <summary>For a feed, whose queue the manager is already serving: adds a file under exactly the name it gives, whatever else is in the list.</summary>
    public DownloadItem AddFromFeed(DownloadRequest request, DownloadQueue queue) =>
        AddQuietly(request, startNow: false, queue, whenDuplicate: null, existing: null);

    /// <summary>Gives <paramref name="queue"/> a feed, or takes it away with null.</summary>
    public void SetFeed(DownloadQueue queue, IQueueFeed? feed)
    {
        if (feed == null) _feeds.Remove(queue.Id);
        else _feeds[queue.Id] = feed;
    }

    /// <summary>A feed has more for its queue, or has finished what it was waiting for.</summary>
    public void Nudge() => Pump();

    /// <summary>Runs on the UI thread, where every member of this class is to be called from.</summary>
    internal void Post(Action action) => _ui.Post(_ => action(), null);

    DownloadItem AddQuietly(DownloadRequest request, bool startNow, DownloadQueue? queue, DuplicateAction? whenDuplicate, DownloadItem? existing)
    {
        var onDuplicate = whenDuplicate ?? State.Settings.OnDuplicate;
        if (existing != null)
        {
            // A transfer in progress is never duplicated or replaced.
            if (_running.ContainsKey(existing.Id)) return existing;
            if (onDuplicate == DuplicateAction.Resume)
            {
                if (startNow) Start(existing);
                return existing;
            }
        }

        var item = NewItem(request);
        queue = startNow ? null : queue ?? MainQueue;

        // The new entry takes over the old one's file, and the old entry and its partial data go.
        // Permission covers that one known path only: an old entry that never learned its name
        // has no file to replace, so the link is simply added again as a numbered copy.
        if (existing != null && onDuplicate == DuplicateAction.Overwrite && existing.FileName.Length > 0)
        {
            item.FileName = existing.FileName;
            item.Directory = existing.Directory;
            item.DirectoryChosenByUser = true;
            item.OverwritePath = existing.FullPath;
            Items.Remove(existing);
            if (QueueFor(existing) is { } oldQueue) Renumber(oldQueue);
            DeletePartialData(existing);
        }

        Items.Insert(0, item);
        if (queue != null) Append(item, queue);
        MarkDirty();
        if (startNow) Start(item);
        return item;
    }

    /// <summary>The download a request asks for, not yet listed.</summary>
    DownloadItem NewItem(DownloadRequest request)
    {
        // A login in the address becomes the download's own, encrypted, and is gone from every address that is kept.
        var (url, addressUser, addressPassword) = Links.SplitLogin(request.Url);
        var fileName = string.IsNullOrWhiteSpace(request.FileName) ? "" : FileNames.Sanitize(request.FileName);
        var category = request.Category ?? Categories.Resolve(fileName.Length > 0 ? fileName : url, State.Categories);
        // A login given with the request wins over one in the address.
        bool ownLogin = !string.IsNullOrEmpty(request.UserName);
        var item = new DownloadItem
        {
            Url = url,
            Referrer = request.Referrer,
            // Empty rather than null when unknown, so loading never mistakes it for a download saved before pages were kept.
            PageUrl = request.PageUrl ?? request.Referrer ?? "",
            Description = request.Description,
            Cookies = request.Cookies,
            UserAgent = request.UserAgent,
            BrowserProxy = BrowserProxy.Usable(request.BrowserProxy),
            UserName = ownLogin ? request.UserName : addressUser,
            ProtectedPassword = Secrets.Protect(ownLogin ? request.Password : addressPassword),
            Kind = request.Kind,
            ExpectedSha256 = request.Kind != DownloadKind.Http ? null : FileHash.Normalize(request.Sha256),
            HlsBandwidth = request.HlsBandwidth,
            HlsAudioOnly = request.HlsAudioOnly,
            HlsSubtitles = request.HlsSubtitles ?? State.Settings.DownloadSubtitles,
            SiteChoice = request.SiteChoice,
            AudioUrl = request.AudioUrl,
            FileName = fileName,
            Category = category,
            Directory = request.Directory ?? Categories.FolderFor(category, State),
            DirectoryChosenByUser = request.Directory != null,
            MaxConnections = request.Connections ?? State.Settings.DefaultConnections,
            Status = DownloadStatus.Queued,
            GrabberProjectId = request.GrabberProjectId,
            OverwritePath = request.OverwritePath,
        };
        if (item.IsStream && item.FileName.Length == 0) item.FileName = item.Kind == DownloadKind.Hls ? "video.ts" : "video.mp4";
        return item;
    }

    /// <param name="Request">What the browser asked for, which the dialog's answer must still match.</param>
    /// <param name="Settled">Completes when the user says yes, so the transfer may put its file in place.</param>
    sealed record Early(DownloadItem Item, DownloadRequest Request, TaskCompletionSource Settled);

    /// <summary>Where a download started early keeps its parts when the settings name no temporary folder: beside the state.</summary>
    string EarlyPartFolder => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_statePath))!, "parts");

    /// <summary>
    /// Starts a download the browser handed over while its dialog is still open, as IDM does, so it is under way by the
    /// time the user says yes. It stays off the list and out of the saved state, keeps its parts where its name and
    /// folder don't matter, and never puts its file in place before <see cref="Adopt"/>. Null when it can't start
    /// early: an address that isn't a download, a link already listed, or the download limit holding.
    /// </summary>
    public DownloadItem? StartEarly(DownloadRequest request)
    {
        var url = Links.SplitLogin(request.Url).Url;
        if (_limitHolds || _shuttingDown || !Uri.TryCreate(url, UriKind.Absolute, out var address) || !Links.IsDownloadable(address) || FindDuplicate(request) != null)
            return null;
        var item = NewItem(request);
        // Set before the run starts, so a no straight away deletes only this download's parts.
        item.OwnPartFolder = EarlyPartFolder;
        item.PartFolder = State.Settings.NewPartFolder() ?? item.OwnPartFolder;
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        item.Settled = settled.Task;
        _early[item.Id] = new Early(item, request, settled);
        Start(item);
        return item;
    }

    /// <summary>
    /// The user said yes to a download <see cref="StartEarly"/> began: it takes the name, folder and choices they
    /// settled on, and is listed. Started now, it carries on, or starts again if it stopped meanwhile; for later, it
    /// stops, keeping what arrived, and waits in its queue. False, and it is discarded, when the dialog's download
    /// is no longer the one started (another address, kind or login) or the link was listed meanwhile; the caller
    /// then adds it afresh.
    /// </summary>
    public bool Adopt(DownloadItem early, DownloadRequest settled, bool startNow, DownloadQueue? queue = null)
    {
        if (!_early.Remove(early.Id, out var started)) return false;
        if (!SameDownload(started.Request, settled) || FindDuplicate(settled) != null)
        {
            _ = ForgetAsync(early);
            return false;
        }

        var wanted = NewItem(settled);
        var name = wanted.FileName.Length > 0 ? wanted.FileName : early.FileName;
        // A stream's container is the run's to name, as it does when it starts: the name keeps the extension it gave.
        var extension = Path.GetExtension(early.FileName);
        if (early.IsStream && extension.Length > 0) name = Path.ChangeExtension(name, extension);
        early.FileName = name;
        early.Description = wanted.Description;
        early.ExpectedSha256 = wanted.ExpectedSha256;
        early.Category = wanted.Category;
        early.Directory = wanted.Directory;
        early.DirectoryChosenByUser = wanted.DirectoryChosenByUser;
        early.MaxConnections = wanted.MaxConnections;
        Items.Insert(0, early);

        queue = startNow ? null : queue ?? MainQueue;
        var running = _running.TryGetValue(early.Id, out var run);
        if (queue != null)
        {
            // Stopped before the way is opened, so this run never puts the file in place.
            if (running)
            {
                _requeueOnCancel.Add(early.Id);
                run!.Cts.Cancel();
            }
            Append(early, queue);
            early.Status = DownloadStatus.Queued;
        }
        else if (running)
        {
            started.Settled.TrySetResult();
        }
        // Later runs put their file in place as soon as they have it.
        early.Settled = Task.CompletedTask;
        // Failed, or stopped by the download limit, while the dialog was open.
        if (queue == null && !running) Start(early);
        MarkDirty();
        if (queue != null) Pump();
        return true;
    }

    /// <summary>The user said no to a download <see cref="StartEarly"/> began: it stops, and what it downloaded is deleted.</summary>
    public Task DiscardAsync(DownloadItem early) => _early.Remove(early.Id) ? ForgetAsync(early) : Task.CompletedTask;

    async Task ForgetAsync(DownloadItem early)
    {
        if (_running.TryGetValue(early.Id, out var running))
        {
            running.Cts.Cancel();
            try { await running.Task; } catch { /* cancelled or failed; either way it is gone */ }
        }
        DeletePartialData(early);
    }

    /// <summary>Whether the dialog's download is still the one the browser asked for: the same address, kind, choices and login.</summary>
    static bool SameDownload(DownloadRequest started, DownloadRequest settled) =>
        started.Url == settled.Url && started.Kind == settled.Kind && Login(started) == Login(settled)
        && started.SiteChoice == settled.SiteChoice && started.HlsBandwidth == settled.HlsBandwidth
        && started.HlsAudioOnly == settled.HlsAudioOnly && started.AudioUrl == settled.AudioUrl;

    static (string User, string Password) Login(DownloadRequest request) =>
        string.IsNullOrEmpty(request.UserName) ? ("", "") : (request.UserName, request.Password ?? "");

    /// <summary>
    /// Deletes what downloads started early left in their folder that no listed download uses, as when ADM exited with
    /// their dialog open. Only ADM keeps files there, so nobody needs asking.
    /// </summary>
    void DeleteEarlyOrphans()
    {
        var folder = EarlyPartFolder;
        if (!Directory.Exists(folder)) return;
        var used = Items.SelectMany(i => new[] { i.PartPath, i.PartsFolderPath, i.RemuxPath })
            .Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFileSystemEntries(folder).Select(Path.GetFullPath).Where(p => !used.Contains(p)).ToList())
            TryDelete(() =>
            {
                if (Directory.Exists(path)) Directory.Delete(path, true);
                else File.Delete(path);
            });
    }

    public void Start(DownloadItem item) => Start(item, stopLive: false);

    /// <param name="byQueue">A running queue starts it, so it opens no progress or complete window.</param>
    void Start(DownloadItem item, bool stopLive, bool byQueue = false)
    {
        // A download taken off the list stays stopped, whatever window still shows it.
        if (_running.ContainsKey(item.Id) || _moving.Contains(item.Id) || item.Status == DownloadStatus.Completed
            || (!Items.Contains(item) && !_early.ContainsKey(item.Id))) return;
        // Saving a recording joins parts already on disk, so only that may start while the limit holds.
        if (_limitHolds && !stopLive)
        {
            HoldForLimit(item);
            return;
        }

        item.StartedByQueue = byQueue;
        item.LastTryAt = DateTime.Now;
        _retryAt.Remove(item.Id);
        item.Error = null;
        item.ScanResult = null;
        item.HashResult = null;
        // Pausing just as a file landed leaves this set, and this run's file isn't there yet.
        item.Landed = false;
        item.Status = DownloadStatus.Downloading;
        Log.Info($"Download {item.Id} starting, from {Log.Host(item.Url)}");
        var cts = new CancellationTokenSource();
        var stop = new CancellationTokenSource();
        if (stopLive) stop.Cancel();
        var task = Task.Run(() => item.IsStream
            ? new HlsDownloader(Limiter, State.Settings).RunAsync(item, cts.Token, stop.Token)
            : new HttpDownloader(Limiter, State.Settings, OnProbed).RunAsync(item, cts.Token));
        _running[item.Id] = new Running(cts, stop, task);
        _counted[item.Id] = item.DownloadedBytes;
        task.ContinueWith(t => _ui.Post(_ => OnFinished(item, t), null), TaskScheduler.Default);
        MarkDirty();
    }

    /// <summary>A file kept from starting by the download limit: its queue starts it later, or else the limit's end does.</summary>
    void HoldForLimit(DownloadItem item)
    {
        if (item.QueueId != null)
        {
            item.Status = DownloadStatus.Queued;
        }
        else
        {
            _resumeAfterLimit.Add(item.Id);
            item.Status = DownloadStatus.Paused;
            item.Error = LimitReached;
        }
        MarkDirty();
    }

    /// <summary>Adds what each transfer downloaded since it was last counted to the download limit's period.</summary>
    void CountTowardLimit(DownloadItem item, DateTime now)
    {
        if (!_counted.TryGetValue(item.Id, out var before)) return;
        var bytes = item.DownloadedBytes;
        // Progress can go back (a failed try is taken off), so only growth counts.
        if (bytes > before && State.Settings.QuotaEnabled)
        {
            State.Quota.Add(bytes - before, now, LimitHours);
            MarkDirty();
        }
        _counted[item.Id] = bytes;
    }

    int LimitHours => Math.Clamp(State.Settings.QuotaHours, 1, 23);

    /// <summary>Bytes read from a server by something other than a transfer, such as a site being explored; they count toward the download limit at the next tick. Any thread.</summary>
    public void CountBytes(long bytes) => Interlocked.Add(ref _otherBytes, bytes);

    /// <summary>Waits while the download limit holds every transfer. Any thread.</summary>
    public Task WaitWhileLimitHoldsAsync(CancellationToken ct) => Volatile.Read(ref _limitLifted).Task.WaitAsync(ct);

    static TaskCompletionSource Lifted()
    {
        var lifted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lifted.SetResult();
        return lifted;
    }

    void CountOtherBytes(DateTime now)
    {
        var bytes = Interlocked.Exchange(ref _otherBytes, 0);
        if (bytes == 0 || !State.Settings.QuotaEnabled) return;
        State.Quota.Add(bytes, now, LimitHours);
        MarkDirty();
    }

    /// <summary>The archive's entries, as <see cref="ZipPreview"/> reads them, under the same limits as a download.</summary>
    /// <exception cref="ZipPreviewException">It can't be previewed, or the download limit holds; the message says why.</exception>
    public Task<IReadOnlyList<ZipEntryInfo>> ListZipAsync(DownloadItem item, CancellationToken ct) =>
        ReadZipAsync(item, (limits, stop) => ZipPreview.ListAsync(item, State.Settings, RetryPolicy.For(State.Settings), stop, limits), ct);

    /// <summary>Takes these entries out into <paramref name="folder"/>, as <see cref="ZipPreview"/> does, under the same limits as a download.</summary>
    /// <exception cref="ZipPreviewException">The archive can't be read, or the download limit holds or was reached; the message says why.</exception>
    public Task<IReadOnlyList<ZipExtractResult>> ExtractZipAsync(
        DownloadItem item, IReadOnlyList<ZipEntryInfo> entries, string folder, IProgress<long>? progress, CancellationToken ct) =>
        ReadZipAsync(item, (limits, stop) => ZipPreview.ExtractAsync(item, State.Settings, entries, folder, progress, RetryPolicy.For(State.Settings), stop, limits), ct);

    /// <summary>
    /// Reads off the UI thread, since unpacking a large entry is real work. Bytes from a server pass the speed
    /// limiters and count toward the download limit; a finished file on disk uses no network, and answers to neither.
    /// </summary>
    async Task<T> ReadZipAsync<T>(DownloadItem item, Func<ReadLimits, CancellationToken, Task<T>> read, CancellationToken ct)
    {
        var fromServer = !ZipPreview.ReadsFromDisk(item);
        if (fromServer && _limitHolds) throw new ZipPreviewException(LimitHoldsMessage);

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var limits = new ReadLimits(Limiter, item.Limiter, bytes => CountBytes(bytes));
        var work = Task.Run(() => read(limits, stop.Token));
        var entry = new ZipRead(stop, work, fromServer);
        lock (_zipReads) _zipReads.Add(entry);
        try
        {
            return await work;
        }
        catch (OperationCanceledException) when (_limitHolds && !ct.IsCancellationRequested)
        {
            throw new ZipPreviewException(LimitHoldsMessage);
        }
        finally
        {
            lock (_zipReads) _zipReads.Remove(entry);
        }
    }

    string LimitHoldsMessage => $"The download limit has been reached. Downloads carry on at {LimitHoldsUntil:t}.";

    /// <summary>Stops the archive reads, and gives what they are doing to wait for.</summary>
    List<Task> StopZipReads(bool fromServerOnly)
    {
        lock (_zipReads)
        {
            var stopping = _zipReads.Where(r => r.FromServer || !fromServerOnly).ToList();
            foreach (var read in stopping) read.Stop.Cancel();
            return stopping.Select(r => r.Work).ToList();
        }
    }

    sealed record ZipRead(CancellationTokenSource Stop, Task Work, bool FromServer);

    /// <summary>Stops every transfer when the limit is reached, and restarts them when its period is over.</summary>
    void EnforceLimit(DateTime now)
    {
        State.Quota.Roll(now, LimitHours);
        var exceeded = State.Quota.Exceeded(State.Settings);
        if (exceeded && !_limitHolds)
        {
            _limitHolds = true;
            Volatile.Write(ref _limitLifted, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            // A scan downloads nothing, nor does a hash check, so they carry on.
            foreach (var item in Items.Where(i => _running.TryGetValue(i.Id, out var r) && !r.Check && !r.Scan && !r.Hash))
            {
                if (item.QueueId != null)
                {
                    _requeueOnCancel.Add(item.Id);
                }
                else
                {
                    _resumeAfterLimit.Add(item.Id);
                    item.Error = LimitReached;
                }
                _running[item.Id].Cts.Cancel();
            }
            // Those started early stop too; a yes starts them again once the limit lets go.
            foreach (var early in _early.Values)
                if (_running.TryGetValue(early.Item.Id, out var running)) running.Cts.Cancel();
            StopZipReads(fromServerOnly: true);
            QuotaReached?.Invoke(State.Quota.ResumesAt(LimitHours) ?? now);
        }
        else if (!exceeded && _limitHolds)
        {
            _limitHolds = false;
            _limitLifted.TrySetResult();
            foreach (var item in Items.Where(i => _resumeAfterLimit.Contains(i.Id) && i.Status == DownloadStatus.Paused).ToList())
                Start(item);
            _resumeAfterLimit.Clear();
        }
    }

    public void Pause(DownloadItem item)
    {
        if (_running.TryGetValue(item.Id, out var running))
        {
            // A check's file is already complete, and stays so; so is a scan's or a hash check's, which stopping completes.
            if (!running.Check && !running.Scan && !running.Hash) item.Status = DownloadStatus.Paused;
            running.Cts.Cancel();
        }
        else if (item.Status == DownloadStatus.Queued)
        {
            item.Status = DownloadStatus.Paused;
        }
        MarkDirty();
    }

    /// <summary>
    /// Ends a live recording and saves what it has as a complete file. A paused or failed recording is
    /// saved from its parts without recording more. Pause, by contrast, keeps the parts, and resuming
    /// records on after a gap.
    /// </summary>
    public void StopAndSave(DownloadItem item)
    {
        if (!item.IsLive) return;
        if (_running.TryGetValue(item.Id, out var running)) running.Stop.Cancel();
        else if (item.Status is DownloadStatus.Paused or DownloadStatus.Failed or DownloadStatus.Queued) Start(item, stopLive: true);
    }

    public DownloadQueue? QueueFor(DownloadItem item) =>
        item.QueueId == null ? null : Queues.FirstOrDefault(q => q.Id == item.QueueId);

    /// <summary>The queue's files in the order it serves them.</summary>
    public List<DownloadItem> QueueItems(DownloadQueue queue) =>
        // Items are newest-first, so equal orders (state saved before queues had one) go oldest first.
        Items.Reverse().Where(i => i.QueueId == queue.Id).OrderBy(i => i.QueueOrder).ToList();

    /// <summary>
    /// Puts the file at the end of <paramref name="queue"/>; a paused or failed file is queued again so
    /// the queue picks it up. A file already in that queue keeps its place. A complete file can only
    /// join a sync queue, since a download queue has nothing left to do with it.
    /// </summary>
    public bool MoveToQueue(DownloadItem item, DownloadQueue queue)
    {
        if (item.Status == DownloadStatus.Completed && queue.Kind != QueueKind.Sync) return false;
        var old = QueueFor(item);
        if (old != queue)
        {
            Append(item, queue);
            if (old != null) Renumber(old);
        }
        if (!_running.ContainsKey(item.Id) && item.Status is DownloadStatus.Paused or DownloadStatus.Failed)
            item.Status = DownloadStatus.Queued;
        MarkDirty();
        Pump();
        return true;
    }

    /// <summary>Takes the file out of its queue; a file that was waiting for the queue is paused.</summary>
    public void RemoveFromQueue(DownloadItem item)
    {
        if (QueueFor(item) is not { } queue) return;
        Leave(item);
        if (item.Status == DownloadStatus.Queued) item.Status = DownloadStatus.Paused;
        Renumber(queue);
        MarkDirty();
    }

    /// <summary>Moves the selected files within their queues; selected files never pass one another.</summary>
    public void MoveInQueue(IReadOnlyCollection<DownloadItem> items, QueueMove move)
    {
        foreach (var queue in items.Select(QueueFor).OfType<DownloadQueue>().Distinct().ToList())
        {
            var order = QueueItems(queue);
            var selected = items.ToHashSet();
            switch (move)
            {
                case QueueMove.Top:
                    order = order.Where(selected.Contains).Concat(order.Where(i => !selected.Contains(i))).ToList();
                    break;
                case QueueMove.Bottom:
                    order = order.Where(i => !selected.Contains(i)).Concat(order.Where(selected.Contains)).ToList();
                    break;
                case QueueMove.Up:
                    for (int i = 1; i < order.Count; i++)
                        if (selected.Contains(order[i]) && !selected.Contains(order[i - 1])) (order[i], order[i - 1]) = (order[i - 1], order[i]);
                    break;
                case QueueMove.Down:
                    for (int i = order.Count - 2; i >= 0; i--)
                        if (selected.Contains(order[i]) && !selected.Contains(order[i + 1])) (order[i], order[i + 1]) = (order[i + 1], order[i]);
                    break;
            }
            Number(order);
        }
        MarkDirty();
    }

    /// <summary>Deletes an added queue; its files go to the end of the main queue of its kind. The main queues stay.</summary>
    public bool DeleteQueue(DownloadQueue queue)
    {
        if (queue.IsMain) return false;
        StopQueue(queue);
        var main = queue.Kind == QueueKind.Sync ? SyncQueue : MainQueue;
        foreach (var item in QueueItems(queue)) Append(item, main);
        Queues.Remove(queue);
        MarkDirty();
        return true;
    }

    void Append(DownloadItem item, DownloadQueue queue)
    {
        var last = Items.Where(i => i.QueueId == queue.Id && i != item).Select(i => i.QueueOrder).DefaultIfEmpty(0).Max();
        item.QueueId = queue.Id;
        item.QueueOrder = last + 1;
        Renumber(queue);
    }

    static void Leave(DownloadItem item)
    {
        item.QueueId = null;
        item.QueueOrder = 0;
        item.QueuePosition = 0;
    }

    void Renumber(DownloadQueue queue) => Number(QueueItems(queue));

    static void Number(List<DownloadItem> order)
    {
        for (int i = 0; i < order.Count; i++)
        {
            order[i].QueueOrder = i + 1;
            order[i].QueuePosition = i + 1;
        }
    }

    public async Task RemoveAsync(DownloadItem item, bool deleteFile)
    {
        // Off the list first, so a transfer or scan that ends meanwhile reports nothing about it.
        Items.Remove(item);
        if (QueueFor(item) is { } queue) Renumber(queue);
        if (_running.TryGetValue(item.Id, out var running))
        {
            running.Cts.Cancel();
            try { await running.Task; } catch { /* cancelled or failed; either way it is gone */ }
        }

        DeletePartialData(item);
        if (deleteFile && item.Status == DownloadStatus.Completed) TryDelete(() => File.Delete(item.FullPath));
        MarkDirty();
    }

    /// <summary>Removes every finished download from the list; the files stay on disk.</summary>
    public int RemoveCompleted()
    {
        var done = Items.Where(i => i.Status == DownloadStatus.Completed).ToList();
        foreach (var item in done) Items.Remove(item);
        if (done.Count > 0) MarkDirty();
        return done.Count;
    }

    /// <summary>Downloads the file again from the start, replacing a finished copy in place.</summary>
    public void Restart(DownloadItem item)
    {
        if (_running.ContainsKey(item.Id) || !Items.Contains(item)) return;
        ResetForRedownload(item);
        Start(item);
    }

    /// <summary>Forgets progress; a finished file may be replaced by the new copy, and stays until it is.</summary>
    static void ResetForRedownload(DownloadItem item)
    {
        DeletePartialData(item);
        lock (item.SyncRoot) item.Segments.Clear();
        if (item.Status == DownloadStatus.Completed) item.OverwritePath = item.FullPath;
        item.DownloadedBytes = 0;
        item.HlsSegmentsDone = 0;
        item.HlsSegmentCount = 0;
        item.TotalBytes = -1;
        item.ResumeSupported = false;
        item.ETag = null;
        item.LastModified = null;
        item.HlsFingerprint = null;
        item.Status = DownloadStatus.Paused;
    }

    /// <summary>
    /// Waits up to ten minutes for the browser to capture a new address for an expired link.
    /// One download waits at a time; a later call replaces it.
    /// </summary>
    public void BeginRefresh(DownloadItem item)
    {
        if (_running.ContainsKey(item.Id)) Pause(item);
        AwaitingNewAddress = item;
        _refreshSince = DateTime.Now;
    }

    /// <summary>The waiting download, when this capture is for a file of the same name; otherwise null.</summary>
    public DownloadItem? FindRefreshTarget(DownloadRequest request, DateTime now)
    {
        var item = AwaitingNewAddress;
        if (item == null || now - _refreshSince > RefreshWait || !Items.Contains(item)) return null;
        // Resumed or finished since the wait began: a new address now would either change a
        // running transfer's source mid-file or swallow the capture.
        if (_running.ContainsKey(item.Id) || item.Status == DownloadStatus.Completed) return null;
        var name = string.IsNullOrWhiteSpace(request.FileName)
            ? FileNames.FromResponse(null, request.Url, null)
            : FileNames.Sanitize(request.FileName);
        // A download renamed because its name was taken still answers to the name the browser knows it by.
        return string.Equals(name, item.FileName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, FileNames.WithoutCopyNumber(item.FileName), StringComparison.OrdinalIgnoreCase) ? item : null;
    }

    /// <summary>
    /// Points the download at the captured address and starts it. Saved progress is kept,
    /// and the downloader resumes from it if the new server's copy is the same file.
    /// </summary>
    public void ApplyRefresh(DownloadItem item, DownloadRequest request)
    {
        item.Url = request.Url;
        item.Referrer = request.Referrer;
        item.Cookies = request.Cookies;
        item.UserAgent = request.UserAgent;
        item.BrowserProxy = BrowserProxy.Usable(request.BrowserProxy);
        AwaitingNewAddress = null;
        MarkDirty();
        Start(item);
    }

    static void DeletePartialData(DownloadItem item)
    {
        // Beside the file, part files are named after it, so a download not yet named has none.
        if (item.PartFolder == null && item.FileName.Length == 0) return;
        TryDelete(() => File.Delete(item.PartPath));
        TryDelete(() => File.Delete(item.RemuxPath));
        TryDelete(() => Directory.Delete(item.PartsFolderPath, true));
    }

    /// <summary>
    /// Part files and HLS part folders in the temporary folder that no download in the list uses, such as
    /// those of downloads removed while ADM wasn't running. Download folders are never searched: they
    /// hold the user's own files.
    /// </summary>
    public IReadOnlyList<string> OrphanedParts()
    {
        var folder = State.Settings.NewPartFolder();
        if (folder == null || !Directory.Exists(folder)) return Array.Empty<string>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Every download counts: the temporary folder can also be where one saves its file, beside its part file.
        foreach (var item in Items)
        {
            used.Add(Path.GetFullPath(item.PartPath));
            used.Add(Path.GetFullPath(item.PartsFolderPath));
            used.Add(Path.GetFullPath(item.RemuxPath));
        }
        return Directory.EnumerateFiles(folder, "*.admpart")
            .Concat(Directory.EnumerateFiles(folder, "*.admremux"))
            .Concat(Directory.EnumerateDirectories(folder, "*.admparts"))
            .Select(Path.GetFullPath)
            .Where(path => !used.Contains(path))
            .ToList();
    }

    /// <summary>Deletes what <see cref="OrphanedParts"/> finds now, so a download added since it was last asked keeps its parts. What can't be deleted stays.</summary>
    public void DeleteOrphanedParts()
    {
        foreach (var path in OrphanedParts())
        {
            TryDelete(() =>
            {
                if (Directory.Exists(path)) Directory.Delete(path, true);
                else File.Delete(path);
            });
        }
    }

    /// <summary>Starts the queue's waiting files, and tries its failed ones again. Files the user paused stay paused.</summary>
    public void StartQueue(DownloadQueue queue)
    {
        var wasRunning = queue.IsRunning;
        queue.IsRunning = true;
        if (!wasRunning && _feeds.TryGetValue(queue.Id, out var feed)) feed.Starting();
        foreach (var item in Items.Where(i => i.QueueId == queue.Id))
        {
            _failures.Remove(item.Id);
            _retryAt.Remove(item.Id);
            if (item.Status == DownloadStatus.Failed && !_running.ContainsKey(item.Id)) item.Status = DownloadStatus.Queued;
            // A live recording is not a file a server can say changed.
            if (queue.Kind == QueueKind.Sync && item.Status == DownloadStatus.Completed && !item.IsLive && !_running.ContainsKey(item.Id))
                _toCheck.Add(item.Id);
        }
        MarkDirty();
        Pump();
        // A queue with nothing to do has already stopped, and reports neither start nor finish.
        if (!wasRunning && queue.IsRunning)
        {
            Log.Info($"Queue started: {queue.Id}");
            QueueStarted?.Invoke(queue);
        }
    }

    /// <summary>Changes an added queue's kind. A download queue lets go of its complete files.</summary>
    public void SetKind(DownloadQueue queue, QueueKind kind)
    {
        if (queue.IsMain || queue.Kind == kind) return;
        queue.Kind = kind;
        if (kind == QueueKind.Download)
        {
            foreach (var item in QueueItems(queue).Where(i => i.Status == DownloadStatus.Completed && !_running.ContainsKey(i.Id)))
            {
                Leave(item);
                _toCheck.Remove(item.Id);
            }
            Renumber(queue);
        }
        MarkDirty();
    }

    public void StartQueuesOnLaunch()
    {
        foreach (var queue in Queues.Where(q => q.StartOnLaunch).ToList()) StartQueue(queue);
    }

    public void StopQueue(DownloadQueue queue)
    {
        Log.Info($"Queue stopped: {queue.Id}");
        if (queue.IsRunning && _feeds.TryGetValue(queue.Id, out var feed)) feed.Stopping();
        queue.IsRunning = false;
        _queuesThatRan.Remove(queue.Id);
        foreach (var item in Items.Where(i => i.QueueId == queue.Id))
        {
            _toCheck.Remove(item.Id);
            // A scan's transfer is done: the scan finishes, and the file completes. So with a hash check.
            if (!_running.TryGetValue(item.Id, out var running) || running.Scan || running.Hash) continue;
            _requeueOnCancel.Add(item.Id);
            running.Cts.Cancel();
        }
    }

    /// <summary>
    /// After sleep or a network change: every running transfer, HLS included, drops the reads it has in
    /// flight and its connections start again at once, without using up a try.
    /// </summary>
    public void Reconnect()
    {
        foreach (var item in Items.Where(i => _running.ContainsKey(i.Id))) item.Reconnect();
    }

    /// <summary>Call about twice a second: refreshes speeds, runs the scheduler and persists state.</summary>
    public void Tick(DateTime now)
    {
        foreach (var item in Items)
        {
            if (!_running.ContainsKey(item.Id)) continue;
            item.Tick(now);
            CountTowardLimit(item, now);
        }
        CountOtherBytes(now);

        EnforceLimit(now);
        RunScheduler(now);
        // Files waiting to be retried start once their wait is over.
        Pump(now);

        if (_writer.TakeFailure()) _saveFailedAt = now;
        if (_dirty || (_running.Count > 0 && now - _lastSave > SaveEvery) || (_saveFailedAt is DateTime failed && now - failed > SaveEvery))
            SaveInBackground();
    }

    static readonly TimeSpan SaveEvery = TimeSpan.FromSeconds(5);

    public DateTime? NextScheduledStart(DateTime now)
    {
        DateTime? next = null;
        foreach (var q in Queues)
        {
            var s = q.Schedule;
            if (!s.Enabled || s.StartTime is not TimeSpan start || s.Days.Count == 0) continue;
            if (NextRepeat(q, now) is DateTime again && again > now && (next == null || again < next)) next = again;
            for (int day = 0; day <= 7; day++)
            {
                var at = now.Date.AddDays(day) + start;
                if (at > now && s.Days.Contains(at.DayOfWeek))
                {
                    if (next == null || at < next) next = at;
                    break;
                }
            }
        }
        return next;
    }

    /// <summary>Saves the state and waits for the disk: for exit, the session ending, and before anything that may end the process.</summary>
    public void Save()
    {
        var bytes = Snapshot();
        try
        {
            _writer.Write(bytes);
        }
        catch (Exception e)
        {
            Log.Error("The state could not be saved.", e);
            throw;
        }
        Saved();
    }

    /// <summary>
    /// Saves the state without waiting for the disk, as the tick does while downloads run: the state is read here,
    /// on the UI thread, and the file written on another. A failure is logged, and tried again on a later tick.
    /// </summary>
    public void SaveInBackground()
    {
        _writer.Post(Snapshot());
        Saved();
    }

    /// <summary>Completes once saves handed to the background are on disk.</summary>
    public Task SavesWritten => _writer.Idle;

    byte[] Snapshot()
    {
        State.Downloads = Items.ToList();
        State.Queues = Queues.ToList();
        return Store.Serialize(State);
    }

    void Saved()
    {
        _dirty = false;
        _saveFailedAt = null;
        _lastSave = DateTime.Now;
    }

    /// <summary>Stops every transfer for exit. Files in a queue wait for it again next time; the others are paused.</summary>
    public async Task ShutdownAsync()
    {
        // Queues stay marked as running, so without this a file put back in its queue would start again.
        _shuttingDown = true;
        foreach (var item in Items.Where(i => i.QueueId != null && _running.ContainsKey(i.Id)))
            _requeueOnCancel.Add(item.Id);
        var feeds = _feeds.Values.Select(f => f.ShutdownAsync()).ToArray();
        var scanning = Items.Where(i => _running.TryGetValue(i.Id, out var r) && (r.Scan || r.Hash)).ToList();
        var tasks = _running.Values.Select(r => { r.Cts.Cancel(); return r.Task; }).Concat(StopZipReads(fromServerOnly: false)).Concat(feeds).ToArray();
        try { await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
        // A scan or a hash check the exit stopped leaves its file in place, so the download is complete, though not checked.
        foreach (var item in scanning.Where(i => i.Status == DownloadStatus.Downloading))
        {
            if (item.IsHashing) item.HashResult = StatusWords.Current.HashNotChecked;
            else item.ScanResult = ScanStopped;
            item.IsScanning = item.IsHashing = false;
            Complete(item);
        }
        foreach (var item in Items.Where(i => i.Status == DownloadStatus.Downloading))
            item.Status = item.QueueId != null ? DownloadStatus.Queued : DownloadStatus.Paused;
        Save();
    }

    public void MarkDirty() => _dirty = true;

    void OnProbed(DownloadItem item, ProbeResult probe)
    {
        if (string.IsNullOrWhiteSpace(item.FileName)) item.FileName = probe.FileName;
        if (item.DirectoryChosenByUser) return;
        item.Category = Categories.Resolve(item.FileName, State.Categories);
        item.Directory = Categories.FolderFor(item.Category, State);
    }

    void OnFinished(DownloadItem item, Task task)
    {
        CountTowardLimit(item, DateTime.Now);
        _counted.Remove(item.Id);
        if (_running.Remove(item.Id, out var running))
        {
            var cancelled = running.Cts.IsCancellationRequested;
            running.Cts.Dispose();
            running.Stop.Dispose();
            if (!running.Check && !item.KeepSpeedLimit) item.SpeedLimitKBps = 0;
            if (running.Check)
            {
                // A cancelled or failed check leaves the file as it was, complete.
                item.IsChecking = false;
                _requeueOnCancel.Remove(item.Id);
                if (!cancelled && task is Task<bool> { IsCompletedSuccessfully: true } check)
                {
                    item.LastCheckedAt = DateTime.Now;
                    if (check.Result)
                    {
                        ResetForRedownload(item);
                        Start(item, stopLive: false, byQueue: true);
                    }
                }
            }
            else if (running.Hash)
            {
                FinishHash(item, task);
            }
            else if (running.Scan)
            {
                FinishScan(item, task);
            }
            else if (cancelled)
            {
                item.Status = _requeueOnCancel.Remove(item.Id) ? DownloadStatus.Queued : DownloadStatus.Paused;
            }
            else if (task.IsFaulted)
            {
                item.Error = task.Exception!.GetBaseException().Message;
                Log.Error($"Download {item.Id} failed, from {Log.Host(item.Url)}: {Log.Reason(task.Exception!.GetBaseException())}");
                if (!RetryLater(item))
                {
                    item.Status = DownloadStatus.Failed;
                    // Not for a download whose dialog is still open: a yes tries again, and a no forgets it.
                    if (Reporting(item)) DownloadFailed?.Invoke(item);
                }
            }
            else
            {
                CheckFinishedFile(item);
            }
        }
        item.ResetSpeed();
        MarkDirty();
        Pump();
    }

    /// <summary>A transfer has ended well: its hash is checked if it has one, then it is scanned if there is a scanner, then it completes.</summary>
    void CheckFinishedFile(DownloadItem item)
    {
        // A sync queue's file is expected to change, so what it should hash to now is anyone's guess.
        if (QueueFor(item)?.Kind == QueueKind.Sync) item.ExpectedSha256 = null;
        // A stream's result is joined here, so no page's hash could match it.
        if (item.ExpectedSha256 != null && !item.IsStream) StartHash(item);
        else ScanOrComplete(item);
    }

    void ScanOrComplete(DownloadItem item)
    {
        if (!string.IsNullOrWhiteSpace(State.Settings.VirusScanner)) StartScan(item);
        else Complete(item);
    }

    /// <summary>Checks a finished transfer's file before anything else can open it. It stays running meanwhile, so queues, the complete dialog and "when done" all wait, as for a scan.</summary>
    void StartHash(DownloadItem item)
    {
        // A big file can take minutes, and a crash meanwhile must find the file landed.
        Save();
        var file = item.FullPath;
        var cts = new CancellationTokenSource();
        var task = Task.Run(() => Hasher(file, cts.Token));
        _running[item.Id] = new Running(cts, new CancellationTokenSource(), task, Hash: true);
        item.IsHashing = true;
        task.ContinueWith(t => _ui.Post(_ => OnFinished(item, t), null), TaskScheduler.Default);
    }

    void FinishHash(DownloadItem item, Task task)
    {
        item.IsHashing = false;
        _requeueOnCancel.Remove(item.Id);
        if (task is not Task<string> { IsCompletedSuccessfully: true } found)
        {
            // Stopped, it completes as a stopped scan does; one that can't be read still goes on to be scanned.
            item.HashResult = StatusWords.Current.HashNotChecked;
            Log.Info($"Download {item.Id}: its SHA-256 was not checked");
            if (task.IsCanceled) Complete(item);
            else ScanOrComplete(item);
            return;
        }
        if (found.Result == item.ExpectedSha256)
        {
            item.HashResult = StatusWords.Current.HashMatches;
            Log.Info($"Download {item.Id}: its SHA-256 matches");
            ScanOrComplete(item);
            return;
        }

        // The bytes are kept, because a page's hash can be the stale one, under a name a double click won't run.
        // Moving it keeps its mark of the web. Not tried again: the server would send the same file.
        var keptAs = FileNames.Unique(item.FullPath + ".badhash");
        try
        {
            File.Move(item.FullPath, keptAs);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Something has it open. It stays where it is, and the download still fails.
            keptAs = item.FullPath;
            Log.Error($"Download {item.Id}: a file failing its SHA-256 could not be renamed", e);
        }
        Log.Error($"Download {item.Id}: its SHA-256 does not match");
        item.Landed = false;
        item.Error = item.HashResult = StatusWords.Current.HashMismatch(Path.GetFileName(keptAs));
        item.Status = DownloadStatus.Failed;
        if (Reporting(item)) DownloadFailed?.Invoke(item);
    }

    void Complete(DownloadItem item)
    {
        Log.Info($"Download {item.Id} complete");
        item.Landed = false;
        item.Status = DownloadStatus.Completed;
        item.CompletedAt = DateTime.Now;
        // Browser session cookies were only needed to fetch the file; don't keep them on disk.
        item.Cookies = null;
        // A download queue's work on it is done; a sync queue keeps it to check later.
        if (QueueFor(item) is { Kind: QueueKind.Download } queue)
        {
            Leave(item);
            Renumber(queue);
        }
        if (Reporting(item)) DownloadCompleted?.Invoke(item);
    }

    /// <summary>Whether to tell the app how a download ended: not for one taken off the list, nor while ADM exits.</summary>
    bool Reporting(DownloadItem item) => !_shuttingDown && Items.Contains(item);

    /// <summary>
    /// Scans a finished transfer's file before the download counts as complete. It stays running meanwhile, so
    /// queues, the complete dialog and "when done" all wait for the scan.
    /// </summary>
    void StartScan(DownloadItem item)
    {
        // A scan can take minutes, and a crash meanwhile must find the file landed.
        Save();
        var (scanner, arguments, file) = (State.Settings.VirusScanner, State.Settings.VirusScannerArguments ?? "", item.FullPath);
        var cts = new CancellationTokenSource();
        var task = Task.Run(() => VirusScan.RunAsync(scanner, arguments, file, cts.Token));
        _running[item.Id] = new Running(cts, new CancellationTokenSource(), task, Scan: true);
        item.IsScanning = true;
        task.ContinueWith(t => _ui.Post(_ => OnFinished(item, t), null), TaskScheduler.Default);
    }

    /// <summary>A scan that removed the file fails the download; anything else completes it, with a note when there is one.</summary>
    void FinishScan(DownloadItem item, Task task)
    {
        item.IsScanning = false;
        _requeueOnCancel.Remove(item.Id);
        if (task is Task<ScanOutcome> { IsCompletedSuccessfully: true, Result.FileRemoved: true })
        {
            // Not tried again: the server would send the same file.
            Log.Info($"Download {item.Id}: the virus scanner removed its file");
            item.Landed = false;
            item.Error = VirusScan.RemovedFile;
            item.Status = DownloadStatus.Failed;
            if (Reporting(item)) DownloadFailed?.Invoke(item);
            return;
        }
        item.ScanResult = task switch
        {
            Task<ScanOutcome> { IsCompletedSuccessfully: true } done => VirusScan.Finding(done.Result),
            { IsCanceled: true } => ScanStopped,
            _ => "The virus scanner could not start: " + task.Exception!.GetBaseException().Message,
        };
        Log.Info($"Download {item.Id} scanned: {item.ScanResult ?? "nothing found"}");
        Complete(item);
    }

    /// <summary>
    /// A file that fails while its queue runs goes to the end of the queue and waits <see cref="RetryDelay"/>
    /// before its next try, unless it has used up the queue's retries.
    /// </summary>
    bool RetryLater(DownloadItem item)
    {
        if (QueueFor(item) is not { IsRunning: true } queue) return false;
        var failures = _failures[item.Id] = _failures.GetValueOrDefault(item.Id) + 1;
        if (queue.LimitRetries && failures > queue.RetriesPerFile)
        {
            _failures.Remove(item.Id);
            return false;
        }
        _retryAt[item.Id] = DateTime.Now + RetryDelay;
        Append(item, queue);
        item.Status = DownloadStatus.Queued;
        return true;
    }

    void Pump(DateTime? now = null)
    {
        // While the download limit holds, queues wait without finishing.
        if (_shuttingDown || _limitHolds) return;
        var at = now ?? DateTime.Now;
        foreach (var queue in Queues.Where(q => q.IsRunning).ToList())
        {
            _feeds.TryGetValue(queue.Id, out var feed);
            if (feed != null)
            {
                // Everything in the queue counts, so what it holds stays small however much its feed has.
                int held = Items.Count(i => i.QueueId == queue.Id && (_running.ContainsKey(i.Id) || i.Status == DownloadStatus.Queued));
                feed.Fill(Math.Max(0, queue.MaxConcurrent - held));
                if (feed.Busy) _queuesThatRan.Add(queue.Id);
            }
            int active = Items.Count(i => i.QueueId == queue.Id && _running.ContainsKey(i.Id));
            var waiting = QueueItems(queue)
                .Where(i => i.Status == DownloadStatus.Queued || (i.Status == DownloadStatus.Completed && _toCheck.Contains(i.Id)))
                .ToList();
            foreach (var next in waiting)
            {
                if (active >= queue.MaxConcurrent) break;
                if (_retryAt.TryGetValue(next.Id, out var retryAt) && retryAt > at) continue;
                if (_moving.Contains(next.Id)) continue;
                if (next.Status == DownloadStatus.Completed) StartCheck(next);
                else Start(next, stopLive: false, byQueue: true);
                _queuesThatRan.Add(queue.Id);
                active++;
            }

            if (active == 0 && feed?.Busy != true && !waiting.Any(i => i.Status == DownloadStatus.Queued || _toCheck.Contains(i.Id)))
            {
                queue.IsRunning = false;
                // Only report completion for a queue that did work, so an empty scheduled
                // queue never triggers its shutdown action. Save first: that action may end the session.
                if (_queuesThatRan.Remove(queue.Id))
                {
                    Save();
                    Log.Info($"Queue finished: {queue.Id}");
                    QueueFinished?.Invoke(queue);
                }
            }
        }
    }

    /// <summary>Asks the server whether a sync queue's complete file changed; the file stays Completed meanwhile.</summary>
    void StartCheck(DownloadItem item)
    {
        _toCheck.Remove(item.Id);
        item.IsChecking = true;
        var cts = new CancellationTokenSource();
        var task = Task.Run(() => item.IsStream
            ? HlsDownloader.HasChangedAsync(item, State.Settings, cts.Token)
            : HttpDownloader.HasChangedAsync(item, State.Settings, cts.Token));
        _running[item.Id] = new Running(cts, new CancellationTokenSource(), task, Check: true);
        task.ContinueWith(t => _ui.Post(_ => OnFinished(item, t), null), TaskScheduler.Default);
    }

    void RunScheduler(DateTime now)
    {
        var window = TimeSpan.FromMinutes(2);
        foreach (var queue in Queues)
        {
            var s = queue.Schedule;
            if (!s.Days.Contains(now.DayOfWeek)) continue;

            if (s.Enabled && s.StartTime is TimeSpan start && InWindow(now.TimeOfDay, start, window) && s.LastStartedOn != now.Date)
            {
                s.LastStartedOn = now.Date;
                s.LastStartedAt = now;
                MarkDirty();
                StartQueue(queue);
            }
            else if (NextRepeat(queue, now) is DateTime again && again <= now)
            {
                s.LastStartedAt = now;
                MarkDirty();
                StartQueue(queue);
            }
            // The stop time applies even to a queue that was started by hand.
            if (s.StopTime is TimeSpan stop && InWindow(now.TimeOfDay, stop, window) && s.LastStoppedOn != now.Date)
            {
                s.LastStoppedOn = now.Date;
                MarkDirty();
                if (queue.IsRunning) StopQueue(queue);
            }
        }
    }

    static bool InWindow(TimeSpan now, TimeSpan target, TimeSpan window) => now >= target && now - target < window;

    /// <summary>
    /// When a sync queue that its schedule started today runs again: <see cref="QueueSchedule.RepeatMinutes"/>
    /// after its last start, if that is before the stop time, or before midnight when there is no stop time or
    /// it comes before the start. Null while the queue runs, or when there is no repeat today.
    /// </summary>
    static DateTime? NextRepeat(DownloadQueue queue, DateTime now)
    {
        var s = queue.Schedule;
        if (queue.Kind != QueueKind.Sync || queue.IsRunning || !s.Enabled || s.RepeatMinutes <= 0) return null;
        if (s.StartTime is not TimeSpan start || s.LastStartedOn != now.Date || s.LastStartedAt is not DateTime last) return null;
        var again = last.AddMinutes(s.RepeatMinutes);
        if (again.Date != now.Date) return null;
        if (s.StopTime is TimeSpan stop && stop > start && again.TimeOfDay >= stop) return null;
        return again;
    }

    static void TryDelete(Action delete)
    {
        try { delete(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
