namespace ADM.Core;

/// <summary>
/// The site grabber's saved projects. The state lists each one and its queue; the rest is in the project's own file,
/// loaded when it is opened or its queue starts, so ADM starts as quickly however many big projects there are. Each
/// project has a queue of its own, so its schedule, retries and "when done" work as any queue's do, and its runs are
/// that queue's feed. Every member is called on the UI thread.
/// </summary>
public sealed class GrabberProjects
{
    readonly DownloadManager _manager;
    readonly Dictionary<string, (GrabberProject Project, GrabberRun Run)> _open = new(StringComparer.Ordinal);

    public GrabberProjects(DownloadManager manager)
    {
        _manager = manager;
        foreach (var info in manager.State.GrabberProjects) manager.SetFeed(QueueOf(info), new Unopened(this, info.Id));
    }

    public IReadOnlyList<GrabberProjectInfo> All => _manager.State.GrabberProjects;

    /// <summary>Where a project keeps its file and the copies of pages it explored.</summary>
    public string FolderOf(string id) => Path.Combine(_manager.GrabberFolder, id);

    string FileOf(string id) => Path.Combine(FolderOf(id), "project.json");

    GrabberProjectInfo? InfoOf(string id) => _manager.State.GrabberProjects.FirstOrDefault(p => p.Id == id);

    /// <summary>The project's queue, made again if it has gone missing from the state.</summary>
    DownloadQueue QueueOf(GrabberProjectInfo info)
    {
        if (_manager.Queues.FirstOrDefault(q => q.Id == info.QueueId) is { } queue) return queue;
        queue = new DownloadQueue { Name = info.Name, GrabberProjectId = info.Id };
        _manager.Queues.Add(queue);
        info.QueueId = queue.Id;
        _manager.MarkDirty();
        return queue;
    }

    public DownloadQueue? QueueOf(GrabberProject project) => InfoOf(project.Id) is { } info ? QueueOf(info) : null;

    /// <summary>The run of an open project, which is its queue's feed.</summary>
    public GrabberRun? RunOf(GrabberProject project) => _open.TryGetValue(project.Id, out var open) ? open.Run : null;

    /// <summary>The name, or a numbered one if another project has it, so two are never confused.</summary>
    public string UniqueName(string wanted, string? exceptId = null)
    {
        var name = wanted.Trim();
        if (name.Length == 0) name = "Grabber project";
        bool Taken(string candidate) => _manager.State.GrabberProjects.Any(p => p.Id != exceptId && string.Equals(p.Name, candidate, StringComparison.OrdinalIgnoreCase));
        if (!Taken(name)) return name;
        for (int n = 2; ; n++)
            if (!Taken($"{name} ({n})")) return $"{name} ({n})";
    }

    /// <summary>A new project, saved at once with a queue of its own, and open.</summary>
    public GrabberProject Create(string name, GrabberSettings settings)
    {
        var project = new GrabberProject { Name = UniqueName(name), Settings = settings };
        var info = new GrabberProjectInfo { Id = project.Id, Name = project.Name };
        _manager.State.GrabberProjects.Add(info);
        var queue = QueueOf(info);
        GrabberStore.Save(FileOf(project.Id), project);
        _manager.MarkDirty();
        Attach(project, queue);
        return project;
    }

    /// <summary>A saved project, loaded if it isn't open yet; null if there is no such project, or its file can't be read.</summary>
    public GrabberProject? Open(string id)
    {
        if (_open.TryGetValue(id, out var open)) return open.Project;
        if (InfoOf(id) is not { } info) return null;
        var project = GrabberStore.Load(FileOf(id));
        if (project == null)
        {
            Log.Error($"Grabber project {id} could not be read.");
            return null;
        }
        project.Id = id;
        project.Name = info.Name;
        Attach(project, QueueOf(info));
        return project;
    }

    void Attach(GrabberProject project, DownloadQueue queue)
    {
        var run = new GrabberRun(_manager, project, queue, FolderOf(project.Id)) { Mode = project.ScheduledRun };
        _open[project.Id] = (project, run);
    }

    /// <summary>Saves the project's file, and its name in the state; a name another project has gets a number.</summary>
    public void Save(GrabberProject project)
    {
        if (InfoOf(project.Id) is not { } info) return;
        project.Name = UniqueName(project.Name, project.Id);
        info.Name = project.Name;
        QueueOf(info).Name = project.Name;
        GrabberStore.Save(FileOf(project.Id), project);
        _manager.MarkDirty();
    }

    /// <summary>
    /// What the project's schedule runs. A sync makes its queue a sync queue, so the schedule can repeat it, and lets
    /// it replace files on disk.
    /// </summary>
    public void SetScheduledRun(GrabberProject project, GrabberRunMode mode)
    {
        project.ScheduledRun = mode;
        if (mode == GrabberRunMode.Sync) project.Settings.Overwrite = true;
        if (RunOf(project) is { } run) run.Mode = mode;
        if (QueueOf(project) is { } queue) _manager.SetKind(queue, mode == GrabberRunMode.Sync ? QueueKind.Sync : QueueKind.Download);
        Save(project);
    }

    /// <summary>Starts one run of an open project; false if one is under way, as a project has one at a time.</summary>
    public bool Start(GrabberProject project, GrabberRunMode mode) => RunOf(project)?.Start(mode) ?? false;

    public void Stop(GrabberProject project)
    {
        if (QueueOf(project) is { IsRunning: true } queue) _manager.StopQueue(queue);
    }

    /// <summary>The most files "add to the main list" takes at once: a bigger site belongs in its project, a few at a time.</summary>
    public const int MaxAddToMainList = 10_000;

    /// <summary>
    /// Adds files of the project to the main list as ordinary downloads, saved as the main list saves them, with the
    /// page each was found on and, on the start page's own site, the project's login and cookies. A file already
    /// listed is left as it is. Refuses more than <see cref="MaxAddToMainList"/>, adding none.
    /// </summary>
    /// <returns>How many were added, or -1 when there were too many.</returns>
    public int AddToMainList(GrabberProject project, IReadOnlyCollection<GrabberEntry> entries, DownloadQueue? queue)
    {
        if (entries.Count > MaxAddToMainList) return -1;
        var s = project.Settings;
        var start = GrabberCrawler.StartAddress(s.StartUrl);
        int added = 0;
        foreach (var entry in entries)
        {
            if (!Uri.TryCreate(entry.Url, UriKind.Absolute, out var address)) continue;
            bool startHost = start != null && string.Equals(address.Host, start.Host, StringComparison.OrdinalIgnoreCase);
            if (_manager.FindDuplicate(entry.Url) != null) continue;
            _manager.Add(new DownloadRequest
            {
                Url = entry.Url,
                Referrer = entry.Referrer,
                PageUrl = entry.Referrer,
                FileName = entry.FileName,
                Description = s.LinkTextAsDescription ? entry.LinkText : null,
                UserName = startHost ? s.UserName : null,
                Password = startHost ? Secrets.Unprotect(s.ProtectedPassword) : null,
                Cookies = start != null && Net.SameOrigin(address, start.AbsoluteUri) ? Secrets.Unprotect(s.ProtectedCookies) : null,
            }, startNow: false, queue ?? _manager.MainQueue, DuplicateAction.Resume);
            added++;
        }
        return added;
    }

    /// <summary>
    /// Saves a project that isn't running and lets it go, so a big one's list doesn't stay in memory; its schedule
    /// still starts it. One that is running stays open.
    /// </summary>
    public async Task CloseAsync(GrabberProject project)
    {
        if (!_open.TryGetValue(project.Id, out var open) || open.Run.Busy || QueueOf(project) is not { } queue) return;
        _open.Remove(project.Id);
        await open.Run.CloseAsync();
        _manager.SetFeed(queue, new Unopened(this, project.Id));
    }

    /// <summary>
    /// Deletes a project: its queue, the files waiting in it, and the copies of pages it kept. The files it saved stay
    /// where they are.
    /// </summary>
    public async Task DeleteAsync(string id)
    {
        if (InfoOf(id) is not { } info) return;
        if (_open.Remove(id, out var open)) await open.Run.CloseAsync();
        var queue = QueueOf(info);
        _manager.SetFeed(queue, null);
        _manager.StopQueue(queue);
        foreach (var item in _manager.Items.Where(i => i.GrabberProjectId == id).ToList()) await _manager.RemoveAsync(item, deleteFile: false);
        _manager.DeleteQueue(queue);
        _manager.State.GrabberProjects.Remove(info);
        _manager.MarkDirty();
        try
        {
            if (Directory.Exists(FolderOf(id))) Directory.Delete(FolderOf(id), recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Error("A deleted grabber project's folder could not be removed.", e);
        }
    }

    /// <summary>A project's queue while the project isn't loaded: starting it, as its schedule does, loads the project, whose run then takes over.</summary>
    sealed class Unopened(GrabberProjects owner, string id) : IQueueFeed
    {
        public void Starting()
        {
            if (owner.Open(id) is { } project) owner.RunOf(project)!.Starting();
        }

        public void Stopping() { }
        public bool Busy => false;
        public void Fill(int room) { }
        public Task ShutdownAsync() => Task.CompletedTask;
    }
}
