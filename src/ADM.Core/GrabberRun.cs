using System.Collections.Concurrent;

namespace ADM.Core;

/// <summary>What a run of a project does.</summary>
public enum GrabberRunMode { ExploreOnly, ExploreAndDownload, DownloadChecked, Sync }

/// <summary>
/// A queue whose files come while it runs, rather than all sitting in the list. The manager asks it to fill any room
/// the queue has, and a queue doesn't finish while its feed is busy.
/// </summary>
public interface IQueueFeed
{
    void Starting();
    /// <summary>The queue was stopped, or ADM is closing; it may be started again.</summary>
    void Stopping();
    /// <summary>More files may come, or work the queue has to wait for is under way.</summary>
    bool Busy { get; }
    /// <summary>Adds up to this many files through <see cref="DownloadManager.AddFromFeed"/>.</summary>
    void Fill(int room);
    /// <summary>Everything is saved, for ADM to close.</summary>
    Task ShutdownAsync();
}

/// <summary>
/// One project's work in its queue: exploring, then its checked files as ordinary downloads, a few at a time, and its
/// pages from the copies kept while exploring. A file leaves the manager's list as soon as it ends, and the project keeps
/// the result, so a big site's list stays out of the state. Links are converted when it ends or is stopped.
/// </summary>
public sealed class GrabberRun : IQueueFeed
{
    readonly DownloadManager _manager;
    readonly GrabberProject _project;
    readonly DownloadQueue _queue;
    readonly string _folder;
    readonly string _savePath;
    readonly object _gate;
    readonly ConcurrentQueue<GrabberEntry> _ready = new();
    readonly ConcurrentQueue<(GrabberEntry Entry, string Path)> _pages = new();
    readonly Dictionary<Guid, GrabberEntry> _downloads = new();
    readonly Dictionary<string, GrabberEntry> _byAddress = new(StringComparer.Ordinal);
    GrabberPaths _paths;
    CancellationTokenSource? _stop;
    GrabberCrawler? _crawler;
    bool _exploring;
    bool _running;
    int _pageWorkers;
    int _nudging;
    Task _exploration = Task.CompletedTask;
    Task _conversion = Task.CompletedTask;
    Task _pageWork = Task.CompletedTask;
    GrabberRunMode _mode;
    GrabberRunMode? _once;
    int _conversions;
    bool _dirty;
    DateTime _lastSave = DateTime.UtcNow;

    public GrabberRun(DownloadManager manager, GrabberProject project, DownloadQueue queue, string folder)
    {
        _manager = manager;
        _project = project;
        _queue = queue;
        _folder = folder;
        _savePath = Path.Combine(folder, "project.json");
        _gate = project.SyncRoot;
        _paths = NewPaths();
        lock (_gate)
            foreach (var entry in project.Entries) _byAddress[entry.Url] = entry;
        manager.DownloadCompleted += OnCompleted;
        manager.DownloadFailed += OnFailed;
        queue.GrabberProjectId = project.Id;
        manager.SetFeed(queue, this);
        Reconcile();
    }

    /// <summary>What starting the queue does, as its schedule does, unless <see cref="Start"/> says otherwise.</summary>
    public GrabberRunMode Mode { get; set; } = GrabberRunMode.ExploreAndDownload;

    /// <summary>What the run under way, or the last one, does.</summary>
    public GrabberRunMode Running => _mode;

    /// <summary>A page downloaded rather than explored is converted only up to this size, as a crawl reads one.</summary>
    internal long ConvertLimit { get; set; } = GrabberCrawler.MaxPageBytes;
    internal Func<string, long> FreeSpace { get; set; } = PartFile.FreeSpaceIn;

    /// <summary>The crawl of the run under way; null when it isn't exploring.</summary>
    public GrabberCrawler? Crawler => _crawler;

    /// <summary>Why the last run stopped by itself, such as a full disk; null when it didn't. On the UI thread.</summary>
    public string? Error { get; private set; }

    /// <summary>Something changed in the project; on the UI thread.</summary>
    public event Action? Changed;

    /// <summary>Starts one run of this kind in the project's queue; false while one is under way, as a project has one at a time.</summary>
    public bool Start(GrabberRunMode mode)
    {
        if (_running || _queue.IsRunning) return false;
        _once = mode;
        try
        {
            _manager.StartQueue(_queue);
        }
        finally
        {
            _once = null;
        }
        return true;
    }

    GrabberSettings Options => _project.Settings;

    GrabberPaths NewPaths()
    {
        var paths = new GrabberPaths(Options.KeepSubfolders, Options.Overwrite);
        lock (_gate)
            foreach (var entry in _project.Entries)
                if (entry.LocalPath != null) paths.Taken(entry.LocalPath);
        return paths;
    }

    /// <summary>After a restart: what the list holds for this project says how its files really stand.</summary>
    void Reconcile()
    {
        var mine = _manager.Items.Where(i => i.GrabberProjectId == _project.Id).ToList();
        foreach (var item in mine)
        {
            if (!_byAddress.TryGetValue(item.Url, out var entry))
            {
                _ = _manager.RemoveAsync(item, deleteFile: false);
                continue;
            }
            lock (_gate)
            {
                entry.FileState = GrabberFileState.Queued;
                entry.LocalPath ??= item.FullPath;
            }
            _downloads[item.Id] = entry;
            if (item.Status == DownloadStatus.Completed) OnCompleted(item);
        }
        lock (_gate)
            foreach (var entry in _project.Entries)
                // Saved as queued, and no longer in the list: it never got to start, or the list is older.
                if (entry.FileState == GrabberFileState.Queued && !_downloads.ContainsValue(entry)) entry.FileState = GrabberFileState.None;
    }

    public void Starting()
    {
        if (_running) return;
        _running = true;
        _mode = _once ?? Mode;
        Error = null;
        _queue.MaxConcurrent = Math.Clamp(Options.DownloadAtOnce, 1, 10);
        _stop = new CancellationTokenSource();
        _paths = NewPaths();
        while (_ready.TryDequeue(out _)) { }
        bool explored;
        lock (_gate)
        {
            // The same kind of run as one cut off carries it on: a crawl that went all the way through isn't done again.
            explored = _project.Unfinished == _mode && _project.Explored;
            _project.Unfinished = _mode;
            if (_mode != GrabberRunMode.ExploreOnly)
                foreach (var entry in _project.Entries)
                    if (entry.Matched && entry.Checked && entry.FileState is GrabberFileState.None or GrabberFileState.Failed) _ready.Enqueue(entry);
        }
        _dirty = true;
        if (_mode != GrabberRunMode.DownloadChecked && !explored) StartExploring(_stop.Token);
    }

    void StartExploring(CancellationToken ct)
    {
        var crawler = new GrabberCrawler(_project, _folder, _manager.State.Settings, _manager.Limiter)
        {
            SavePath = _savePath,
            // A crawl's pages count toward the download limit as a download's bytes do, and wait while it holds.
            Counted = _manager.CountBytes,
            Held = _manager.WaitWhileLimitHoldsAsync,
            FreeSpace = FreeSpace,
        };
        crawler.Found += OnFound;
        crawler.Updated += OnUpdated;
        crawler.Changed += Nudge;
        _crawler = crawler;
        _exploring = true;
        // One at a time: a run stopped and started again waits for the last crawl to let go of the project.
        var before = _exploration;
        _exploration = Task.Run(async () =>
        {
            await before;
            await crawler.RunAsync(ct);
        }).ContinueWith(task =>
        {
            if (task.IsFaulted) Log.Error("A grabber project stopped exploring.", task.Exception!.GetBaseException());
            _manager.Post(() =>
            {
                if (_crawler == crawler)
                {
                    _crawler = null;
                    _exploring = false;
                    // A disk too full to keep pages has no room for files either: the project stops, and says why.
                    if (crawler.Error != null && _running)
                    {
                        Error = crawler.Error;
                        _manager.StopQueue(_queue);
                    }
                }
                ChangedNow();
                _manager.Nudge();
            });
        }, TaskScheduler.Default);
    }

    // Off the UI thread.
    void OnFound(GrabberEntry entry)
    {
        // A sync keeps the saved site up to date, so what's new on it is wanted too.
        bool wanted = _mode == GrabberRunMode.Sync || (_mode != GrabberRunMode.ExploreOnly && Options.StartAtOnce);
        lock (_gate)
        {
            _byAddress[entry.Url] = entry;
            if (wanted) entry.Checked = true;
            if (!entry.Checked || entry.FileState != GrabberFileState.None) return;
        }
        _ready.Enqueue(entry);
        Nudge();
    }

    // Off the UI thread: the server's copy changed since it was saved.
    void OnUpdated(GrabberEntry entry)
    {
        if (_mode == GrabberRunMode.ExploreOnly) return;
        lock (_gate)
            if (!entry.Checked) return;
        _ready.Enqueue(entry);
        Nudge();
    }

    // At most one wake-up waits at a time, however fast things happen.
    void Nudge()
    {
        if (Interlocked.Exchange(ref _nudging, 1) == 1) return;
        _manager.Post(() =>
        {
            Volatile.Write(ref _nudging, 0);
            ChangedNow();
            _manager.Nudge();
        });
    }

    void ChangedNow() => Changed?.Invoke();

    public bool Busy => _running;

    public void Fill(int room)
    {
        if (!_running) return;
        while (room > 0 && _ready.TryDequeue(out var entry))
        {
            GrabberFileState state;
            bool wanted;
            lock (_gate) (state, wanted) = (entry.FileState, entry.Checked);
            if (!wanted || state is not (GrabberFileState.None or GrabberFileState.Failed)) continue;
            if (entry.Kept)
            {
                QueuePage(entry);
                continue;
            }
            AddDownload(entry);
            room--;
        }
        StartPageWorker();
        FinishIfDone();
        SaveIfDue();
    }

    string RootFor(GrabberEntry entry) => Options.SaveMode switch
    {
        GrabberSaveMode.ByCategory => Categories.FolderFor(Categories.Resolve(entry.FileName ?? entry.Url, _manager.State.Categories), _manager.State),
        GrabberSaveMode.CategoryFolder => Categories.FolderFor(Options.Category, _manager.State),
        _ => Options.Folder.Length > 0 ? Options.Folder : Path.Combine(_manager.State.Settings.DownloadRoot, FileNames.Sanitize(_project.Name)),
    };

    string PathFor(GrabberEntry entry)
    {
        if (entry.LocalPath != null) return entry.LocalPath;
        var name = entry.FileName ?? FileNames.FromResponse(null, entry.Url, null);
        var path = _paths.Place(RootFor(entry), new Uri(entry.Url), name, entry.Kind == GrabberKind.Page);
        lock (_gate) entry.LocalPath = path;
        return path;
    }

    void AddDownload(GrabberEntry entry)
    {
        var path = PathFor(entry);
        var address = new Uri(entry.Url);
        bool startHost = string.Equals(address.Host, new Uri(StartUrl).Host, StringComparison.OrdinalIgnoreCase);
        var item = _manager.AddFromFeed(new DownloadRequest
        {
            Url = entry.Url,
            Referrer = entry.Referrer,
            PageUrl = entry.Referrer,
            Description = Options.LinkTextAsDescription ? entry.LinkText : null,
            Directory = Path.GetDirectoryName(path),
            FileName = Path.GetFileName(path),
            GrabberProjectId = _project.Id,
            UserName = startHost ? Options.UserName : null,
            Password = startHost ? Secrets.Unprotect(Options.ProtectedPassword) : null,
            Cookies = Net.SameOrigin(address, StartUrl) ? Secrets.Unprotect(Options.ProtectedCookies) : null,
            // A file the project saved, and the server has since changed, is the project's own to replace.
            OverwritePath = Options.Overwrite || entry.Changed ? path : null,
        }, _queue);
        _downloads[item.Id] = entry;
        lock (_gate) entry.FileState = GrabberFileState.Queued;
        _dirty = true;
    }

    string StartUrl => GrabberCrawler.StartAddress(Options.StartUrl)?.AbsoluteUri ?? Options.StartUrl;

    void QueuePage(GrabberEntry entry)
    {
        var path = PathFor(entry);
        lock (_gate) entry.FileState = GrabberFileState.Queued;
        _pages.Enqueue((entry, path));
    }

    void StartPageWorker()
    {
        if (_pages.IsEmpty || Interlocked.CompareExchange(ref _pageWorkers, 1, 0) != 0) return;
        _pageWork = Task.Run(() =>
        {
            try
            {
                while (_pages.TryDequeue(out var page)) WritePage(page.Entry, page.Path);
            }
            finally
            {
                Volatile.Write(ref _pageWorkers, 0);
                Nudge();
            }
        });
    }

    /// <summary>A page explored is saved as it came, without asking the server again; its links are converted at the end.</summary>
    void WritePage(GrabberEntry entry, string path)
    {
        try
        {
            var body = GrabberStore.ReadBody(_folder, entry) ?? throw new IOException("The kept copy of this page can't be read.");
            WriteFile(entry, path, body);
            lock (_gate)
            {
                entry.FileState = GrabberFileState.Complete;
                entry.Changed = false;
                entry.Error = null;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Error("A grabber project could not save a page.", e);
            lock (_gate)
            {
                entry.FileState = GrabberFileState.Failed;
                entry.Error = e.Message;
            }
        }
        _dirty = true;
    }

    void WriteFile(GrabberEntry entry, string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".admtmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        // Before the swap, so nothing ever sees the file without it. The mark goes along with the file.
        if (_manager.State.Settings.MarkDownloads) ZoneMark.Write(temp, entry.Url, entry.Referrer);
        try
        {
            Swap(temp, path);
        }
        catch
        {
            // The page stays as it was, and nothing half done is left beside it.
            try { File.Delete(temp); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    /// <summary>How long a swap waits, in all, for a page something else has open; a virus scanner holds a new file for a moment.</summary>
    internal static readonly TimeSpan[] SwapWaits = [TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(800)];

    // Off the UI thread, so it may wait.
    static void Swap(string temp, string path)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(temp, path, overwrite: true);
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException && attempt < SwapWaits.Length && File.Exists(temp))
            {
                Thread.Sleep(SwapWaits[attempt]);
            }
        }
    }

    void OnCompleted(DownloadItem item)
    {
        if (item.GrabberProjectId != _project.Id || !_downloads.Remove(item.Id, out var entry)) return;
        lock (_gate)
        {
            entry.FileState = GrabberFileState.Complete;
            entry.Changed = false;
            entry.LocalPath = item.FullPath;
            entry.Size = item.TotalBytes;
            entry.ETag = item.ETag ?? entry.ETag;
            entry.LastModified = item.LastModified ?? entry.LastModified;
            entry.Error = null;
        }
        LeaveList(item);
    }

    // A page or stylesheet downloaded rather than explored has no copy kept yet, and the file is still as it came.
    void KeepToConvert(GrabberEntry entry)
    {
        var file = new FileInfo(entry.LocalPath!);
        if (!file.Exists || file.Length > ConvertLimit) return;
        var body = File.ReadAllBytes(file.FullName);
        GrabberStore.WriteBody(_folder, entry, body);
        lock (_gate)
        {
            entry.BodyHash = GrabberStore.HashOf(body);
            entry.Kept = true;
        }
    }

    void OnFailed(DownloadItem item)
    {
        if (item.GrabberProjectId != _project.Id || !_downloads.Remove(item.Id, out var entry)) return;
        lock (_gate)
        {
            entry.FileState = GrabberFileState.Failed;
            entry.Error = item.Error;
        }
        LeaveList(item);
    }

    void LeaveList(DownloadItem item)
    {
        _dirty = true;
        _ = _manager.RemoveAsync(item, deleteFile: false);
        ChangedNow();
    }

    void FinishIfDone()
    {
        if (_exploring || _conversions > 0 || !_ready.IsEmpty || !_pages.IsEmpty || Volatile.Read(ref _pageWorkers) > 0 || _downloads.Count > 0) return;
        // Saved with the conversion; a run started meanwhile says it's under way again.
        lock (_gate) _project.Unfinished = null;
        var run = _stop;
        Convert(() =>
        {
            // Stopped and started again meanwhile, this isn't the run that ended.
            if (_stop == run) _running = false;
        });
    }

    /// <summary>Converts the links of what's saved so far, from the bodies kept, so that each time it starts from what came.</summary>
    void Convert(Action? then = null)
    {
        _conversions++;
        // One at a time: a run stopped and started again can't have two writing one file.
        _conversion = _conversion.ContinueWith(_ =>
        {
            try
            {
                if (Options.ConvertLinks && _mode != GrabberRunMode.ExploreOnly) ConvertAll();
            }
            catch (Exception e)
            {
                Log.Error("A grabber project could not convert its links.", e);
            }
            SaveNow();
            _manager.Post(() =>
            {
                _conversions--;
                then?.Invoke();
                ChangedNow();
                _manager.Nudge();
            });
        }, TaskScheduler.Default);
    }

    void ConvertAll()
    {
        var saved = new Dictionary<string, string>(StringComparer.Ordinal);
        List<GrabberEntry> documents;
        lock (_gate)
        {
            foreach (var entry in _project.Entries)
            {
                if (entry.FileState != GrabberFileState.Complete || entry.LocalPath == null) continue;
                saved[entry.Url] = entry.LocalPath;
            }
            documents = _project.Entries.Where(e => e.FileState == GrabberFileState.Complete && e.LocalPath != null && e.Kind is (GrabberKind.Page or GrabberKind.Style)).ToList();
        }
        foreach (var entry in documents)
        {
            try
            {
                if (!entry.Kept) KeepToConvert(entry);
                if (GrabberStore.ReadBody(_folder, entry) is not { } body) continue;
                var address = new Uri(entry.FinalUrl ?? entry.Url);
                var converted = GrabberRewriter.Rewrite(body, entry.Charset, entry.Kind, address, entry.LocalPath!, uri => saved.GetValueOrDefault(uri.AbsoluteUri));
                WriteFile(entry, entry.LocalPath!, converted);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // This page keeps its links as they were, and the others still get theirs.
                Log.Error("A grabber project could not convert a page.", e);
            }
        }
    }

    public void Stopping()
    {
        if (!_running) return;
        _running = false;
        _stop?.Cancel();
        // What was waiting goes back to waiting, and what is saved so far works offline.
        while (_pages.TryDequeue(out _)) { }
        lock (_gate)
            foreach (var entry in _project.Entries)
                if (entry.FileState == GrabberFileState.Queued && !_downloads.ContainsValue(entry)) entry.FileState = GrabberFileState.None;
        Convert();
    }

    public async Task ShutdownAsync()
    {
        Stopping();
        // ADM won't wait long for a big site's links: what the project knows is safe first.
        await Task.Run(SaveNow);
        await _conversion;
    }

    /// <summary>
    /// Stops the run, waits for what it has under way, saves the project, and lets go of the manager: the queue loses
    /// its feed, and the run hears nothing more of its files. A file left in the queue is matched again by the next run.
    /// </summary>
    public async Task CloseAsync()
    {
        if (_queue.IsRunning) _manager.StopQueue(_queue);
        else Stopping();
        _manager.DownloadCompleted -= OnCompleted;
        _manager.DownloadFailed -= OnFailed;
        _manager.SetFeed(_queue, null);
        await _exploration;
        await _pageWork;
        await _conversion;
        await Task.Run(SaveNow);
    }

    void SaveIfDue()
    {
        if (!_dirty || DateTime.UtcNow - _lastSave < TimeSpan.FromSeconds(5)) return;
        _lastSave = DateTime.UtcNow;
        _dirty = false;
        Task.Run(SaveNow);
    }

    void SaveNow()
    {
        try
        {
            GrabberStore.Save(_savePath, _project);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Error("A grabber project could not be saved.", e);
        }
    }
}
