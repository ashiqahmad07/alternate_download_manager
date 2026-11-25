using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using ADM.Core;
using Microsoft.Win32;

namespace ADM.App;

public partial class MainWindow : ThemedWindow
{
    const int MaxTrayText = 127;

    public static readonly RoutedUICommand AddUrlCommand = new("Add URL", nameof(AddUrlCommand), typeof(MainWindow));
    public static readonly RoutedUICommand DeleteCommand = new("Delete", nameof(DeleteCommand), typeof(MainWindow));
    public static readonly RoutedUICommand FindCommand = new("Find", nameof(FindCommand), typeof(MainWindow));
    public static readonly RoutedUICommand FindNextCommand = new("Find next", nameof(FindNextCommand), typeof(MainWindow));
    public static readonly RoutedUICommand FilterCommand = new("Filter by name", nameof(FilterCommand), typeof(MainWindow));

    const int WmClipboardUpdate = 0x031D;

    /// <summary>An entry of the sidebar, under its section's heading, with how many downloads it lists.</summary>
    /// <param name="queue">Set for a queue's entry, which has the queue context menu.</param>
    /// <param name="project">Set for a grabber project's entry, which has the project's menu.</param>
    /// <param name="counted">False for the grabber's entries: a project's files are in its own window, so they list nothing here.</param>
    sealed class Filter(string section, string label, Func<DownloadItem, bool> matches, DownloadQueue? queue = null, GrabberProjectInfo? project = null, bool counted = true) : Observable
    {
        public string Section { get; } = section;
        public string Label { get; } = label;
        public Func<DownloadItem, bool> Matches { get; } = matches;
        public DownloadQueue? Queue { get; } = queue;
        public GrabberProjectInfo? Project { get; } = project;
        public bool Counted { get; } = counted;

        /// <summary>How many downloads the entry lists; blank for none.</summary>
        public string Count { get => _count; set => Set(ref _count, value); }
        string _count = "";

        // The list's UI Automation name, which screen readers announce.
        public override string ToString() => Label;
    }

    List<Filter> _filters = new();

    readonly DownloadManager _manager;
    readonly GrabberProjects _projects;
    // The grabber windows open, by project, so a project opens in one window only.
    readonly Dictionary<string, GrabberWindow> _grabbers = new();
    readonly DispatcherTimer _timer;
    readonly ICollectionView _view;
    readonly CaptureCancels _cancels = new();
    TrayIcon? _tray;
    DateTime? _armedWake;
    string? _lastClipboardText;
    bool _trayHintShown;
    // A message the status bar shows for a few seconds instead of the counts.
    string? _statusNote;
    DateTime _statusNoteUntil;

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool AddClipboardFormatListener(IntPtr hwnd);

    public MainWindow(DownloadManager manager, GrabberProjects projects)
    {
        _manager = manager;
        _projects = projects;
        QueueMarkConverter.Manager = manager;
        InitializeComponent();
        Icon = AppIcon.ImageSource;

        _view = System.Windows.Data.CollectionViewSource.GetDefaultView(_manager.Items);
        Downloads.ItemsSource = _view;
        BuildFilters();
        InitList();
        InitOverview();
        BuildLanguageMenu();
        _manager.Queues.CollectionChanged += (_, _) => BuildFilters();

        _manager.DownloadCompleted += OnDownloadCompleted;
        _manager.DownloadFailed += item =>
        {
            if (item.GrabberProjectId == null) Notify(Lang.T("main.download_failed"), $"{item.FileName}: {item.Error}");
        };
        _manager.QueueFinished += OnQueueFinished;
        _manager.QuotaReached += resumesAt =>
            Notify(Lang.T("main.download_limit_reached"), Lang.T("main.limit_stopped_all", resumesAt));
        Sounds.Hook(_manager);

        CreateTray();
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => OnTick(), Dispatcher);
        _timer.Start();
        // Hidden in the tray or minimized, the window skips what nobody sees, and catches up once it shows.
        IsVisibleChanged += (_, _) => CatchUp();
        StateChanged += (_, _) => CatchUp();
        SourceInitialized += (_, _) => HookClipboard();
    }

    /// <summary>At start: offers to delete part files in the temporary folder that no download uses.</summary>
    public void OfferToDeleteOrphanedParts()
    {
        try
        {
            var count = _manager.OrphanedParts().Count;
            if (count == 0) return;
            var prompt = count == 1
                ? Lang.T("main.orphan_part", _manager.State.Settings.TempFolder)
                : Lang.T("main.orphan_parts", _manager.State.Settings.TempFolder, count);
            var answer = IsVisible
                ? MessageBox.Show(this, prompt, "ADM", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No)
                : MessageBox.Show(prompt, "ADM", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (answer == MessageBoxResult.Yes) _manager.DeleteOrphanedParts();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A temporary folder that can't be read has nothing to offer; a download using it will say why.
        }
    }

    public void ShowAndActivate()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
    }

    /// <summary>The Video tools window in front, as the browser's panel asks when a tool is missing; one at a time.</summary>
    public void ShowVideoTools()
    {
        ShowAndActivate();
        if (OwnedWindows.OfType<VideoToolsWindow>().FirstOrDefault() is { } open)
        {
            open.Activate();
            return;
        }
        new VideoToolsWindow(_manager.State.Settings) { Owner = this }.Show();
    }

    /// <summary>
    /// Adds a download from the browser or command line: "now" starts it, "queue" only queues it, anything else asks.
    /// A capture of the file a download is waiting to refresh can instead become that download's new address.
    /// <paramref name="silent"/>, from IDM's /n, asks nothing about a link already listed either: the setting under
    /// Options decides, and where that is to ask, the link is added again with a numbered name.
    /// </summary>
    public DownloadItem? HandleExternalDownload(DownloadRequest request, string? mode, bool silent = false)
    {
        if (_manager.FindRefreshTarget(request, DateTime.Now) is { } waiting && ConfirmRefresh(waiting, request))
        {
            _manager.ApplyRefresh(waiting, request);
            ShowProgress(waiting);
            return waiting;
        }
        return mode switch
        {
            "now" => AddDownload(request, true, null, silent),
            "queue" => AddDownload(request, false, _manager.MainQueue, silent),
            // As in IDM, the download is already under way while its dialog asks about it.
            _ => ShowAddDialog(request, _manager.StartEarly(request)),
        };
    }

    bool ConfirmRefresh(DownloadItem item, DownloadRequest request)
    {
        ShowAndActivate();
        var prompt = Lang.T("main.refresh_prompt", item.FileName, item.Url, request.Url);
        return MessageBox.Show(this, prompt, Lang.T("main.refresh_title"), MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
    }

    public DownloadItem? ShowAddDialog(DownloadRequest request) => ShowAddDialog(request, early: null);

    /// <param name="early">
    /// The download already under way for it, as <see cref="DownloadManager.StartEarly"/> began it: a yes keeps it, under
    /// the name and folder chosen, and a no stops it and deletes what it downloaded.
    /// </param>
    DownloadItem? ShowAddDialog(DownloadRequest request, DownloadItem? early)
    {
        var dialog = new AddDownloadWindow(_manager, request, early);
        PlaceDialog(dialog);
        var added = dialog.ShowDialog() == true;
        if (dialog.CategoriesChanged) BuildFilters();
        if (!added)
        {
            if (early != null) _ = _manager.DiscardAsync(early);
            if (request.Captured) OnCaptureCancelled(request.Url);
            return null;
        }
        if (early != null && _manager.Adopt(early, dialog.Result!, dialog.StartNow, dialog.Queue))
        {
            if (dialog.StartNow) ShowProgress(early);
            return early;
        }
        return AddDownload(dialog.Result!, dialog.StartNow, dialog.Queue);
    }

    /// <summary>The second cancel of a download taken from the browser offers to leave it to the browser from then on.</summary>
    void OnCaptureCancelled(string url)
    {
        var settings = _manager.State.Settings;
        if (!settings.PromptAfterTwoCancels || !_cancels.Cancelled(url)) return;

        var dialog = new TwoCancelsWindow(url);
        PlaceDialog(dialog);
        if (dialog.ShowDialog() != true) return;
        if (dialog.ExcludeSite) CaptureRules.AddException(settings, url, wholeSite: true);
        if (dialog.ExcludeAddress) CaptureRules.AddException(settings, url, wholeSite: false);
        if (dialog.DontAskAgain) settings.PromptAfterTwoCancels = false;
        _manager.MarkDirty();
    }

    /// <summary>"Download all links" from the browser: the user picks which of the page's links to add.</summary>
    public void ShowLinksDialog(PageLinks page)
    {
        if (page.Links.Count == 0)
        {
            Notify(Lang.T("main.no_links_found"), page.Referrer ?? Lang.T("main.page_has_no_links"));
            return;
        }
        var dialog = new LinksWindow(_manager, page);
        PlaceDialog(dialog);
        if (dialog.ShowDialog() != true) return;
        var queue = dialog.Queue ?? _manager.MainQueue;
        // A link that is already listed keeps its existing entry instead of being added twice.
        foreach (var request in dialog.Requests)
            _manager.Add(request, startNow: false, queue, DuplicateAction.Resume);
        if (dialog.StartQueue) _manager.StartQueue(queue);
    }

    /// <summary>
    /// A dialog opened from the main window belongs to it. One that comes while the user is elsewhere, as a download
    /// from the browser does, stands on its own, as IDM's does: Windows raises a dialog's owner with it, so the whole
    /// of ADM would come up behind a question about one file, even when it was minimized or under the browser.
    /// </summary>
    void PlaceDialog(Window dialog)
    {
        if (IsActive)
        {
            dialog.Owner = this;
            return;
        }
        dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        dialog.Topmost = true;
    }

    /// <summary>
    /// Adds through the engine, first settling what to do with a link that is already listed. Asking is left out when
    /// <paramref name="silent"/>; the engine then adds an "Ask" link again with a numbered name.
    /// </summary>
    DownloadItem? AddDownload(DownloadRequest request, bool startNow, DownloadQueue? queue, bool silent = false)
    {
        DuplicateAction? choice = null;
        if (_manager.FindDuplicate(request) is { } existing)
        {
            if (_manager.IsRunning(existing))
            {
                Notify(Lang.T("main.already_downloading"), existing.ToString());
                return existing;
            }
            if (_manager.State.Settings.OnDuplicate == DuplicateAction.Ask && !silent)
            {
                var dialog = new DuplicateLinkWindow(existing);
                PlaceDialog(dialog);
                if (dialog.ShowDialog() != true) return null;
                choice = dialog.Choice;
                if (dialog.Remember)
                {
                    _manager.State.Settings.OnDuplicate = dialog.Choice;
                    _manager.MarkDirty();
                }
            }
            if ((choice ?? _manager.State.Settings.OnDuplicate) == DuplicateAction.Resume && existing.Status == DownloadStatus.Completed)
            {
                Notify(Lang.T("main.already_downloaded"), existing.ToString());
                Downloads.SelectedItem = existing;
                Downloads.ScrollIntoView(existing);
            }
        }
        var item = _manager.Add(request, startNow, queue, choice);
        if (startNow) ShowProgress(item);
        return item;
    }

    void OnTick()
    {
        var now = DateTime.Now;
        _manager.Tick(now);

        var active = _manager.ActiveCount;
        Native.KeepAwake(active > 0);
        if (_tray != null) _tray.Text = active > 0 ? TrayText(Lang.T("main.tray_active", active, Format.Speed(_manager.TotalSpeed))) : "ADM";

        var wake = _manager.NextScheduledStart(now);
        if (wake != _armedWake)
        {
            _armedWake = wake;
            Native.SetWakeTime(wake);
        }

        // Hidden in the tray, as a download manager mostly is, or minimized: nobody sees the rest.
        if (Showing) UpdateView(now);
    }

    bool Showing => IsVisible && WindowState != WindowState.Minimized;

    /// <summary>What the window shows that moves on as downloads run: the bar of parts, a live sort, the pane under the list, and the status bar.</summary>
    void UpdateView(DateTime now, bool afterHidden = false)
    {
        Segments.InvalidateVisual();
        ResortIfLive(now, always: afterHidden);
        UpdateOverview();
        UpdateFilterCounts();
        UpdateToolbar();

        var active = _manager.ActiveCount;
        var listed = Listed.Count();
        StatusText.Text = _statusNote != null && now < _statusNoteUntil
            ? _statusNote
            : _manager.LimitHoldsUntil is DateTime until
            ? Lang.T("main.status_limit", until)
            : active > 0
            ? Lang.T("main.status_active", active, Format.Speed(_manager.TotalSpeed))
            : listed == 1
            ? Lang.T("main.status_downloads_one")
            : Lang.T("main.status_downloads", listed);
    }

    // The window starts hidden, until App shows it or leaves it in the tray.
    bool _wasShowing;

    void CatchUp()
    {
        var showing = Showing;
        if (showing && !_wasShowing) UpdateView(DateTime.Now, afterHidden: true);
        _wasShowing = showing;
    }

    // Windows refuses a longer tip, and a translation can be any length.
    static string TrayText(string text) => text.Length > MaxTrayText ? text[..MaxTrayText] : text;

    void BuildFilters()
    {
        // A grabber project's files and queue are the project's own, and show in its window, not here.
        string status = Lang.T("main.section_status"), categories = Lang.T("main.section_categories"),
            queues = Lang.T("main.filter_queues"), grabber = Lang.T("main.section_grabber");
        var filters = new List<Filter>
        {
            new(status, Lang.T("main.filter_all"), Mine(_ => true)),
            new(status, Lang.T("main.filter_unfinished"), Mine(i => i.Status != DownloadStatus.Completed)),
            new(status, Lang.T("main.filter_finished"), Mine(i => i.Status == DownloadStatus.Completed)),
        };
        filters.AddRange(_manager.State.Categories.Select(c => new Filter(categories, c.Name, Mine(i => i.Category == c.Name))));
        filters.Add(new Filter(queues, Lang.T("main.filter_all_queues"), Mine(i => i.QueueId != null)));
        filters.AddRange(UserQueues.Select(q => new Filter(queues, q.Name, Mine(i => i.QueueId == q.Id), q)));
        filters.Add(new Filter(grabber, Lang.T("main.filter_projects"), _ => false, counted: false));
        filters.AddRange(_projects.All.Select(p => new Filter(grabber, p.Name, _ => false, project: p, counted: false)));
        var selected = FilterList.SelectedIndex;
        _filters = filters;
        // Each section under its heading; the entries keep their order, so an index still names the same entry.
        var view = new System.Windows.Data.ListCollectionView(filters);
        view.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(nameof(Filter.Section)));
        FilterList.ItemsSource = view;
        FilterList.SelectedIndex = selected >= 0 && selected < filters.Count ? selected : 0;
        UpdateFilterCounts();
        // Queue names, kinds and schedules show in the Q column too.
        _view.Refresh();
    }

    /// <summary>Beside each entry, how many downloads it lists; on every tick the window shows, as states move on.</summary>
    void UpdateFilterCounts()
    {
        var listed = Listed.ToList();
        foreach (var filter in _filters)
            filter.Count = filter.Counted && listed.Count(filter.Matches) is var count and > 0 ? count.ToString("N0") : "";
    }

    static Func<DownloadItem, bool> Mine(Func<DownloadItem, bool> matches) => i => i.GrabberProjectId == null && matches(i);

    /// <summary>The downloads the list shows: all but a grabber project's.</summary>
    IEnumerable<DownloadItem> Listed => _manager.Items.Where(i => i.GrabberProjectId == null);

    /// <summary>The queues the user made or ADM has; a grabber project's queue is the project's.</summary>
    IEnumerable<DownloadQueue> UserQueues => _manager.Queues.Where(q => q.GrabberProjectId == null);

    DownloadQueue? SelectedQueue => (FilterList.SelectedItem as Filter)?.Queue;

    GrabberProjectInfo? SelectedProject => (FilterList.SelectedItem as Filter)?.Project;

    void QueueContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        var queue = SelectedQueue;
        // A project's entry has the project's menu, and every other entry the queues'.
        var project = SelectedProject != null || (FilterList.SelectedItem as Filter)?.Label == Lang.T("main.filter_projects");
        foreach (var item in new Control[] { QueueStartItem, QueueStopItem, QueueEditItem, QueueSeparator, QueueNewItem, QueueDeleteItem })
            item.Visibility = project ? Visibility.Collapsed : Visibility.Visible;
        foreach (var item in new Control[] { ProjectOpenItem, ProjectScheduleItem, ProjectDeleteItem, ProjectNewItem })
            item.Visibility = project ? Visibility.Visible : Visibility.Collapsed;
        ProjectOpenItem.IsEnabled = ProjectScheduleItem.IsEnabled = ProjectDeleteItem.IsEnabled = SelectedProject != null;
        QueueStartItem.IsEnabled = queue is { IsRunning: false };
        QueueStopItem.IsEnabled = queue is { IsRunning: true };
        QueueEditItem.IsEnabled = queue != null;
        QueueDeleteItem.IsEnabled = queue is { IsMain: false };
    }

    void QueueStart_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedQueue is { } queue) _manager.StartQueue(queue);
    }

    void QueueStop_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedQueue is { } queue) _manager.StopQueue(queue);
    }

    void QueueEdit_Click(object sender, RoutedEventArgs e) => ShowScheduler(SelectedQueue);

    void QueueDelete_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedQueue is not { IsMain: false } queue) return;
        var main = queue.Kind == QueueKind.Sync ? _manager.SyncQueue : _manager.MainQueue;
        var prompt = Lang.T("queue.delete_prompt", queue.Name, main.Name);
        if (MessageBox.Show(this, prompt, "ADM", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        _manager.DeleteQueue(queue);
    }

    void NewQueue_Click(object sender, RoutedEventArgs e)
    {
        var prompt = new PromptWindow(Lang.T("main.new_queue_title"), Lang.T("main.new_queue_prompt"), Lang.T("queue.default_name", _manager.Queues.Count + 1)) { Owner = this };
        if (prompt.ShowDialog() != true || string.IsNullOrWhiteSpace(prompt.Value)) return;
        var queue = new DownloadQueue { Name = prompt.Value.Trim() };
        _manager.Queues.Add(queue);
        _manager.MarkDirty();
    }

    void StartQueueMenu_SubmenuOpened(object sender, RoutedEventArgs e) =>
        FillQueueMenu(StartQueueMenu, q => !q.IsRunning, q => _manager.StartQueue(q));

    void StopQueueMenu_SubmenuOpened(object sender, RoutedEventArgs e) =>
        FillQueueMenu(StopQueueMenu, q => q.IsRunning, q => _manager.StopQueue(q));

    void MoveToQueueMenu_SubmenuOpened(object sender, RoutedEventArgs e) =>
        FillQueueMenu(MoveToQueueMenu, _ => true, MoveSelectedToQueue);

    /// <summary>Lists every queue in a submenu; the ones that don't apply are greyed out.</summary>
    void FillQueueMenu(MenuItem menu, Func<DownloadQueue, bool> enabled, Action<DownloadQueue> act)
    {
        menu.Items.Clear();
        foreach (var queue in UserQueues)
        {
            var item = new MenuItem { Header = queue.Name.Replace("_", "__"), IsEnabled = enabled(queue) };
            item.Click += (_, _) => act(queue);
            menu.Items.Add(item);
        }
    }

    void MoveSelectedToQueue(DownloadQueue queue)
    {
        var refused = Selected.Count(item => !_manager.MoveToQueue(item, queue));
        if (refused > 0)
            MessageBox.Show(this,
                refused == 1 ? Lang.T("main.complete_refused_one") : Lang.T("main.complete_refused", refused),
                Lang.T("main.move_to_queue_title"), MessageBoxButton.OK, MessageBoxImage.Information);
    }

    void RemoveFromQueue_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in Selected) _manager.RemoveFromQueue(item);
    }

    void FilterList_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyListFilter();

    /// <summary>The list shows what the sidebar's entry lists, narrowed to the names that have the filter box's text.</summary>
    void ApplyListFilter()
    {
        if (FilterList.SelectedItem is not Filter f) return;
        var query = NameFilter.Length > 0 ? new SearchQuery(NameFilter, SearchTargets.Names, MatchCase: false, WholeString: false) : null;
        _view.Filter = o => f.Matches((DownloadItem)o) && (query == null || DownloadSearch.Matches((DownloadItem)o, query));
    }

    string NameFilter => SearchBox.Text.Trim();

    IEnumerable<DownloadItem> Selected => Downloads.SelectedItems.Cast<DownloadItem>().ToList();

    void AddUrl_Click(object sender, RoutedEventArgs e)
    {
        var text = ReadClipboardText().Trim();
        // A command goes in whole, and the dialog reads it as it would one pasted there.
        var url = CurlCommand.IsCommand(text) || (Uri.TryCreate(text, UriKind.Absolute, out var uri) && Links.IsDownloadable(uri)) ? text : "";
        ShowAddDialog(new DownloadRequest { Url = url });
    }

    static string ReadClipboardText()
    {
        try { return Clipboard.ContainsText() ? Clipboard.GetText() : ""; }
        catch (COMException) { return ""; }
    }

    void AddBatch_Click(object sender, RoutedEventArgs e) => ShowBatchDialog(null, Lang.T("batchdownload.title"));

    void BatchFromClipboard_Click(object sender, RoutedEventArgs e)
    {
        var links = Links.Find(ReadClipboardText());
        if (links.Count == 0)
        {
            MessageBox.Show(this, Lang.T("main.no_links_in_clipboard"), "ADM", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        ShowBatchDialog(links, Lang.T("main.batch_from_clipboard"));
    }

    void ImportText_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = Lang.T("main.import_links_title"),
            Filter = Lang.Filter(Lang.T("main.filter_text_and_pages"), "*.txt;*.htm;*.html;*.csv", Lang.T("common.all_files"), "*.*"),
        };
        if (dialog.ShowDialog(this) != true) return;

        List<string> links;
        try
        {
            links = Links.Find(File.ReadAllText(dialog.FileName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, Lang.T("main.could_not_read", ex.Message), "ADM", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (links.Count == 0)
        {
            MessageBox.Show(this, Lang.T("main.no_links_in_file"), "ADM", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        ShowBatchDialog(links, Lang.T("main.import_from", Path.GetFileName(dialog.FileName)));
    }

    void ExportText_Click(object sender, RoutedEventArgs e)
    {
        if (!Listed.Any()) return;
        var dialog = new SaveFileDialog { Title = Lang.T("main.export_links_title"), Filter = Lang.Filter(Lang.T("main.filter_text_file"), "*.txt"), FileName = "ADM downloads.txt" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            // Oldest first, the order the downloads were added in.
            File.WriteAllLines(dialog.FileName, Listed.Reverse().Select(i => i.Url));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, Lang.T("main.could_not_write", ex.Message), "ADM", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    void ImportExportFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = Lang.T("main.import_idm_title"), Filter = Lang.Filter(Lang.T("main.filter_idm_export"), "*.ef2", Lang.T("common.all_files"), "*.*") };
        if (dialog.ShowDialog(this) == true) ImportExportFile(dialog.FileName);
    }

    /// <summary>Offers the downloads in an IDM export file, chosen from the Tasks menu or dropped on the window.</summary>
    void ImportExportFile(string path)
    {
        ExportFileContents contents;
        try
        {
            contents = ExportFile.Parse(ExportFile.ReadText(path));
        }
        catch (FormatException)
        {
            MessageBox.Show(this, Lang.T("main.not_idm_export"), "ADM", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, Lang.T("main.could_not_read", ex.Message), "ADM", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var skipped = contents.Skipped switch
        {
            0 => "",
            1 => Lang.T("main.skipped_one"),
            _ => Lang.T("main.skipped_many", contents.Skipped),
        };
        // The same address twice is one download.
        var requests = contents.Requests.DistinctBy(r => r.Url).ToList();
        if (requests.Count == 0)
        {
            MessageBox.Show(this, skipped.Length > 0 ? skipped : Lang.T("main.file_holds_no_downloads"), "ADM", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var byUrl = requests.ToDictionary(r => r.Url);
        var added = ShowBatchDialog(requests.Select(r => r.Url).ToList(), Lang.T("main.import_from", Path.GetFileName(path)), url => byUrl[url]);
        if (added && skipped.Length > 0) MessageBox.Show(this, skipped, "ADM", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    void ExportMenu_SubmenuOpened(object sender, RoutedEventArgs e) => ExportSelectedItem.IsEnabled = Downloads.SelectedItems.Count > 0;

    void ExportAll_Click(object sender, RoutedEventArgs e) => WriteExportFile(Listed.ToList());

    void ExportSelected_Click(object sender, RoutedEventArgs e)
    {
        var selected = Selected.ToHashSet();
        WriteExportFile(_manager.Items.Where(selected.Contains).ToList());
    }

    void WriteExportFile(IReadOnlyCollection<DownloadItem> items)
    {
        if (items.Count == 0) return;
        var dialog = new SaveFileDialog { Title = Lang.T("main.export_idm_title"), Filter = Lang.Filter(Lang.T("main.filter_idm_export"), "*.ef2"), FileName = "ADM downloads.ef2" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            // Oldest first, the order the downloads were added in.
            File.WriteAllText(dialog.FileName, ExportFile.Write(items.Reverse()));
            ShowStatus(Lang.T("main.exported_to", dialog.FileName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, Lang.T("main.could_not_write", ex.Message), "ADM", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <param name="requestFor">What to add for a chosen link; null adds the link alone.</param>
    /// <returns>Whether the user added the links.</returns>
    bool ShowBatchDialog(IReadOnlyList<string>? links, string title, Func<string, DownloadRequest>? requestFor = null)
    {
        var dialog = new BatchDownloadWindow(_manager, links, title);
        PlaceDialog(dialog);
        if (dialog.ShowDialog() != true) return false;
        var queue = dialog.Queue ?? _manager.MainQueue;
        // A link that is already listed keeps its existing entry instead of being added twice.
        foreach (var url in dialog.Urls)
            _manager.Add(requestFor?.Invoke(url) ?? new DownloadRequest { Url = url }, startNow: false, queue, DuplicateAction.Resume);
        if (dialog.StartQueue) _manager.StartQueue(queue);
        return true;
    }

    /// <summary>The one selected download, when its name says it is a ZIP archive.</summary>
    DownloadItem? SelectedZip =>
        Downloads.SelectedItems.Count == 1 && Downloads.SelectedItem is DownloadItem { Kind: DownloadKind.Http } item
            && item.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            ? item
            : null;

    void FileMenu_SubmenuOpened(object sender, RoutedEventArgs e) =>
        FilePreviewZipItem.Visibility = SelectedZip != null ? Visibility.Visible : Visibility.Collapsed;

    void PreviewZip_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedZip is not { } item) return;
        var window = new ZipPreviewWindow(_manager, item);
        PlaceDialog(window);
        window.Show();
    }

    void Resume_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in Selected)
        {
            _manager.Start(item);
            ShowProgress(item);
        }
    }

    void Pause_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in Selected) Pause(item, this);
    }

    /// <summary>
    /// Pauses, asking first about a live recording or a download that would start over; false when the user
    /// said no and the download carries on.
    /// </summary>
    internal bool Pause(DownloadItem item, Window owner)
    {
        if (item.IsLive && _manager.IsRunning(item))
        {
            // A live stream moves on while paused, so the useful choice is usually to save what was recorded.
            var answer = MessageBox.Show(owner,
                Lang.T("main.live_prompt", item),
                Lang.T("main.live_recording_title"), MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Yes) _manager.StopAndSave(item);
            else if (answer == MessageBoxResult.No) _manager.Pause(item);
            return answer != MessageBoxResult.Cancel;
        }
        if (WouldStartOver(item)
            && MessageBox.Show(owner,
                Lang.T("main.pause_loses", Format.Bytes(item.DownloadedBytes)),
                item.ToString(), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return false;
        _manager.Pause(item);
        return true;
    }

    /// <summary>A running download whose server can't resume: stopping it now throws its progress away.</summary>
    bool WouldStartOver(DownloadItem item) => _manager.IsRunning(item) && !item.IsCheckingFile && item.Resumable == false && item.DownloadedBytes > 0;

    /// <summary>Before stopping several downloads at once, asks once about those that would start over; true to go ahead.</summary>
    bool ConfirmStopping(IEnumerable<DownloadItem> items, string question)
    {
        var losing = items.Where(WouldStartOver).ToList();
        if (losing.Count == 0) return true;
        var list = string.Join("\n", losing.Select(i => $"• {i} ({Format.Bytes(i.DownloadedBytes)})"));
        var message = Lang.T("main.stopping_prompt", list, question);
        var answer = IsVisible
            ? MessageBox.Show(this, message, "ADM", MessageBoxButton.YesNo, MessageBoxImage.Warning)
            : MessageBox.Show(message, "ADM", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        return answer == MessageBoxResult.Yes;
    }

    /// <summary>Exit chosen by the user, who is asked first about downloads that would start over.</summary>
    async Task ExitByUserAsync()
    {
        if (ConfirmStopping(_manager.Items, Lang.T("main.exit_anyway"))) await ((App)Application.Current).ExitAsync();
    }

    void StopAndSave_Click(object sender, RoutedEventArgs e)
    {
        var live = Selected.Where(i => i.IsLive && i.Status != DownloadStatus.Completed).ToList();
        if (live.Count == 0)
        {
            MessageBox.Show(this, Lang.T("main.select_live_recording"), "ADM", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        foreach (var item in live) _manager.StopAndSave(item);
    }

    void PauseAll_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmStopping(_manager.Items, Lang.T("main.pause_all_anyway"))) return;
        foreach (var q in _manager.Queues) if (q.IsRunning) _manager.StopQueue(q);
        foreach (var item in _manager.Items.Where(_manager.IsRunning).ToList()) _manager.Pause(item);
    }

    void Delete_Click(object sender, RoutedEventArgs e) => DeleteSelected();

    void ShowStatus(string text)
    {
        _statusNote = text;
        _statusNoteUntil = DateTime.Now.AddSeconds(6);
        StatusText.Text = text;
    }

    void Properties_Click(object sender, RoutedEventArgs e)
    {
        if (Downloads.SelectedItem is DownloadItem item) ShowProperties(item);
    }

    void ShowProperties(DownloadItem item)
    {
        var dialog = new PropertiesWindow(_manager, item);
        PlaceDialog(dialog);
        dialog.ShowDialog();
    }

    /// <summary>Moves or renames one stopped download, finished or not, through a Save As dialog.</summary>
    async void MoveRename_Click(object sender, RoutedEventArgs e)
    {
        if (Downloads.SelectedItems.Count != 1 || Downloads.SelectedItem is not DownloadItem item) return;
        if (_manager.IsRunning(item) || _manager.IsMoving(item))
        {
            MessageBox.Show(this, Lang.T("main.pause_before_moving"), Lang.T("main.move_or_rename_title"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dialog = new SaveFileDialog
        {
            Title = Lang.T("main.move_or_rename_title"),
            InitialDirectory = Directory.Exists(item.Directory) ? item.Directory : null,
            FileName = item.FileName,
            Filter = Lang.Filter(Lang.T("common.all_files"), "*.*"),
            // Windows asks before an existing file is replaced; a yes is the permission MoveAsync needs.
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog(this) != true) return;

        if (string.Equals(Path.GetFullPath(dialog.FileName), Path.GetFullPath(item.FullPath), StringComparison.OrdinalIgnoreCase)) return;
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            await _manager.MoveAsync(item, dialog.FileName, replace: File.Exists(dialog.FileName));
            ShowStatus(Lang.T("main.moved_to", item.FullPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            MessageBox.Show(this, Lang.T("common.could_not_move", ex.Message), Lang.T("main.move_or_rename_title"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }

    void DeleteCompleted_Click(object sender, RoutedEventArgs e)
    {
        var count = Listed.Count(i => i.Status == DownloadStatus.Completed);
        if (count == 0) return;
        var prompt = count == 1 ? Lang.T("main.remove_finished_one") : Lang.T("main.remove_finished", count);
        if (MessageBox.Show(this, prompt, "ADM", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
            _manager.RemoveCompleted();
    }

    void Redownload_Click(object sender, RoutedEventArgs e)
    {
        var items = Selected.Where(i => !_manager.IsRunning(i)).ToList();
        if (items.Count == 0) return;
        if (items.Any(i => i.Status == DownloadStatus.Completed)
            && MessageBox.Show(this, Lang.T("main.redownload_prompt"),
                "ADM", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;
        foreach (var item in items)
        {
            _manager.Restart(item);
            ShowProgress(item);
        }
    }

    /// <summary>For an expired link: reopens the page it came from so the browser can capture a fresh address.</summary>
    void RefreshAddress_Click(object sender, RoutedEventArgs e)
    {
        if (Downloads.SelectedItem is not DownloadItem item || item.Status == DownloadStatus.Completed) return;
        _manager.BeginRefresh(item);
        if (PageFor(item) is { } page)
        {
            try { Process.Start(new ProcessStartInfo(page) { UseShellExecute = true }); } catch (Win32Exception) { }
        }
        Notify(Lang.T("main.refresh_title"), Lang.T("main.refresh_notice", item));
    }

    /// <summary>The page the download came from, else its site. Only web addresses reach the shell.</summary>
    static string? PageFor(DownloadItem item)
    {
        if (IsWebAddress(item.Referrer, out var page)) return page.AbsoluteUri;
        if (IsWebAddress(item.Url, out var file)) return file.GetLeftPart(UriPartial.Authority) + "/";
        return null;
    }

    static bool IsWebAddress(string? text, out Uri uri) =>
        Uri.TryCreate(text, UriKind.Absolute, out uri!) && Links.IsDownloadable(uri);

    void SpeedLimit_Click(object sender, RoutedEventArgs e)
    {
        var items = Selected.ToList();
        if (items.Count == 0) return;
        var prompt = new PromptWindow(Lang.T("main.speed_limit_title"),
            items.Count == 1 ? Lang.T("main.speed_limit_one") : Lang.T("main.speed_limit_many", items.Count),
            items[0].SpeedLimitKBps.ToString()) { Owner = this };
        if (prompt.ShowDialog() != true) return;
        if (!long.TryParse(prompt.Value, out var limit) || limit < 0)
        {
            MessageBox.Show(this, Lang.T("main.speed_limit_invalid"), Lang.T("main.speed_limit_title"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        foreach (var item in items) item.SpeedLimitKBps = limit;
        _manager.MarkDirty();
    }

    // ---- Site grabber

    void NewGrabber_Click(object sender, RoutedEventArgs e) => OpenGrabber(null);

    /// <summary>A project's window, brought forward if it's open; null starts a new project.</summary>
    void OpenGrabber(GrabberProject? project)
    {
        if (project != null && _grabbers.TryGetValue(project.Id, out var open))
        {
            if (open.WindowState == WindowState.Minimized) open.WindowState = WindowState.Normal;
            open.Activate();
            return;
        }
        var window = new GrabberWindow(_manager, _projects, project);
        if (IsVisible) window.Owner = this;
        window.Closed += (_, _) =>
        {
            foreach (var key in _grabbers.Where(g => g.Value == window).Select(g => g.Key).ToList()) _grabbers.Remove(key);
            BuildFilters();
        };
        // A new one is known by its project once it has one.
        window.Activated += (_, _) => { if (window.Project is { } made) _grabbers[made.Id] = window; };
        if (project != null) _grabbers[project.Id] = window;
        window.Show();
    }

    GrabberProject? OpenSelectedProject()
    {
        if (SelectedProject is not { } info) return null;
        var project = _projects.Open(info.Id);
        if (project == null) MessageBox.Show(this, Lang.T("main.project_unreadable", info.Name), "ADM", MessageBoxButton.OK, MessageBoxImage.Warning);
        return project;
    }

    void ProjectOpen_Click(object sender, RoutedEventArgs e)
    {
        if (OpenSelectedProject() is { } project) OpenGrabber(project);
    }

    void FilterList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => ProjectOpen_Click(sender, e);

    void ProjectSchedule_Click(object sender, RoutedEventArgs e)
    {
        if (OpenSelectedProject() is not { } project) return;
        new SchedulerWindow(_manager, _projects.QueueOf(project), _projects) { Owner = this }.ShowDialog();
    }

    async void ProjectDelete_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedProject is not { } info) return;
        if (MessageBox.Show(this, Lang.T("main.project_delete_prompt", info.Name), "ADM", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        if (_grabbers.TryGetValue(info.Id, out var window)) window.Abandon();
        await _projects.DeleteAsync(info.Id);
        BuildFilters();
    }

    void StartQueue_Click(object sender, RoutedEventArgs e) => _manager.StartQueue(_manager.MainQueue);

    void StopQueue_Click(object sender, RoutedEventArgs e) => _manager.StopQueue(_manager.MainQueue);

    void Scheduler_Click(object sender, RoutedEventArgs e) => ShowScheduler(null);

    void ShowScheduler(DownloadQueue? queue)
    {
        new SchedulerWindow(_manager, queue) { Owner = this }.ShowDialog();
        BuildFilters();
    }

    void Options_Click(object sender, RoutedEventArgs e) => ShowOptions(null);

    /// <param name="tab">The tab to open at, by its header; null for the first.</param>
    void ShowOptions(string? tab)
    {
        if (new OptionsWindow(_manager, tab) { Owner = this }.ShowDialog() == true) BuildFilters();
    }

    void CopyUrl_Click(object sender, RoutedEventArgs e)
    {
        var urls = string.Join(Environment.NewLine, Selected.Select(i => i.Url));
        if (urls.Length > 0) Clipboard.SetText(urls);
    }

    void Open_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in Selected.Where(i => i.Status == DownloadStatus.Completed)) OpenFile(item);
    }

    void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (Downloads.SelectedItem is not DownloadItem item) return;
        if (File.Exists(item.FullPath))
            Process.Start("explorer.exe", $"/select,\"{item.FullPath}\"");
        else if (Directory.Exists(item.Directory))
            Process.Start("explorer.exe", $"\"{item.Directory}\"");
    }

    static void OpenFile(DownloadItem item)
    {
        if (File.Exists(item.FullPath))
            Process.Start(new ProcessStartInfo(item.FullPath) { UseShellExecute = true });
    }

    async void Exit_Click(object sender, RoutedEventArgs e) => await ExitByUserAsync();

    protected override void OnClosing(CancelEventArgs e)
    {
        // Closing the window keeps ADM running in the tray, like most download managers.
        e.Cancel = true;
        Hide();
        if (!_trayHintShown)
        {
            _trayHintShown = true;
            Notify(Lang.T("main.still_running"), Lang.T("main.still_running_detail"));
        }
    }

    /// <summary>
    /// A queue's "when done" actions: its file opens first; sleep, hibernate and shut down follow a
    /// countdown the user can cancel. Not reached when the queue was stopped by hand.
    /// </summary>
    void OnQueueFinished(DownloadQueue queue)
    {
        if (queue.GrabberProjectId != null) Notify(Lang.T("main.project_finished"), queue.Name);
        else Notify(Lang.T("common.queue_finished"), queue.Name);
        var schedule = queue.Schedule;
        if (QueueFinish.CanRun(schedule.RunWhenDone))
        {
            try { Process.Start(new ProcessStartInfo(schedule.RunWhenDone!) { UseShellExecute = true }); }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                Notify(Lang.T("main.could_not_open"), $"{schedule.RunWhenDone}: {ex.Message}");
            }
        }

        var action = schedule.OnComplete;
        var force = schedule.ForceCloseApps;
        switch (action)
        {
            case CompletionAction.ExitApp:
                _ = ((App)Application.Current).ExitAsync();
                break;
            case CompletionAction.Sleep or CompletionAction.Hibernate or CompletionAction.Shutdown:
                ShowCountdown(Lang.T("main.countdown_queue", queue.Name), action, force);
                break;
        }
    }

    /// <summary>Counts down to sleep, hibernate or shut down, which whoever is at the computer can stop.</summary>
    void ShowCountdown(string reason, CompletionAction action, bool force)
    {
        // Shown without blocking: the engine's work goes on behind it, and nothing waits on the answer.
        var countdown = new CountdownWindow(reason, action, force, othersRunning: _manager.ActiveCount);
        countdown.Confirmed += () => TurnOff(action, force);
        countdown.Show();
        countdown.Activate();
    }

    void TurnOff(CompletionAction action, bool force)
    {
        _manager.Save();
        switch (action)
        {
            case CompletionAction.Sleep:
                Native.Suspend(hibernate: false);
                break;
            case CompletionAction.Hibernate:
                Native.Suspend(hibernate: true);
                break;
            case CompletionAction.Shutdown:
                var psi = new ProcessStartInfo("shutdown.exe") { CreateNoWindow = true, UseShellExecute = false };
                foreach (var arg in QueueFinish.ShutdownArguments(force)) psi.ArgumentList.Add(arg);
                Process.Start(psi);
                break;
        }
    }

    void CreateTray()
    {
        _tray = new TrayIcon(CreateTrayMenu, "ADM");
        _tray.Activated += ShowAndActivate;
    }

    /// <summary>The tray icon's menu, made as it opens, so it has the look, the font and the count of that moment.</summary>
    ContextMenu CreateTrayMenu()
    {
        var menu = new ContextMenu();
        menu.Items.Add(TrayItem(Lang.T("tray.open"), (_, _) => ShowAndActivate()));
        menu.Items.Add(TrayItem(Lang.T("tray.add_url"), (_, _) => { ShowAndActivate(); AddUrl_Click(this, new RoutedEventArgs()); }));
        menu.Items.Add(TrayItem(Lang.T("tray.start_main_queue"), (_, _) => _manager.StartQueue(_manager.MainQueue)));
        menu.Items.Add(TrayItem(Lang.T("tray.pause_all"), (_, _) => PauseAll_Click(this, new RoutedEventArgs())));
        // Download windows started minimized wait here.
        var hidden = HiddenProgressCount;
        if (hidden > 0) menu.Items.Add(TrayItem(Lang.T("tray.show_windows_count", hidden), (_, _) => ShowHiddenProgress()));
        menu.Items.Add(new Separator());
        menu.Items.Add(TrayItem(Lang.T("tray.exit"), async (_, _) => await ExitByUserAsync()));
        return menu;
    }

    // The texts are the app's own, so an underscore in one is shown, not taken as an access key. A header that isn't
    // a string gives the item no name, so a screen reader would read each one as blank without the name set here.
    static MenuItem TrayItem(string text, RoutedEventHandler click)
    {
        var item = new MenuItem { Header = new AccessText { Text = text.Replace("_", "__") } };
        System.Windows.Automation.AutomationProperties.SetName(item, text);
        item.Click += click;
        return item;
    }

    public void DisposeTray()
    {
        _tray?.Dispose();
        _tray = null;
    }

    void Notify(string title, string text) => _tray?.ShowBalloon(title, text.Length > 200 ? text[..200] : text);

    void HookClipboard()
    {
        var source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        if (source == null) return;
        source.AddHook(WndProc);
        AddClipboardFormatListener(source.Handle);
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmClipboardUpdate && _manager.State.Settings.MonitorClipboard)
            Dispatcher.BeginInvoke(new Action(CheckClipboardForDownload));
        return IntPtr.Zero;
    }

    /// <summary>
    /// Offers to download copied links that point at file types we categorise: one link opens the
    /// download dialog, several open a batch.
    /// </summary>
    void CheckClipboardForDownload()
    {
        var text = ReadClipboardText().Trim();
        if (text.Length == 0 || text == _lastClipboardText) return;
        _lastClipboardText = text;
        // Copying a command is often meant for a terminal, and its addresses would go without its cookies.
        if (CurlCommand.IsCommand(text)) return;

        var links = Links.Find(text)
            .Where(u => Categories.Resolve(u, _manager.State.Categories) != Categories.General && _manager.FindDuplicate(u) == null)
            .ToList();
        if (links.Count == 1 && links[0] == text) ShowAddDialog(new DownloadRequest { Url = text });
        else if (links.Count > 1) ShowBatchDialog(links, Lang.T("main.links_copied"));
    }
}
