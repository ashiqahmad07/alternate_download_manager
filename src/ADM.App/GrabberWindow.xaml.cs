using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ADM.Core;

namespace ADM.App;

/// <summary>
/// The site grabber: four steps that set a project up, then its list, from which it explores, downloads and updates.
/// Back and Next go between the steps at any time while the project isn't running; Next on the last step explores, or
/// carries on. A new project is kept under a name only once the user saves it, and closing asks first.
/// </summary>
public partial class GrabberWindow : ThemedWindow
{
    public sealed class Row : Observable
    {
        readonly GrabberWindow _owner;
        bool _checked;
        string _status = "", _sizeText = "", _type = "", _saveTo = "", _name = "";
        long _size;

        public Row(GrabberWindow owner, GrabberEntry entry)
        {
            _owner = owner;
            Entry = entry;
            Url = entry.Url;
            LinkText = entry.LinkText ?? "";
        }

        public GrabberEntry Entry { get; }
        public string Url { get; }
        public string LinkText { get; }
        public string Name { get => _name; private set => Set(ref _name, value); }
        public string Type { get => _type; private set => Set(ref _type, value); }
        public long Size { get => _size; private set => Set(ref _size, value); }
        public string SizeText { get => _sizeText; private set => Set(ref _sizeText, value); }
        public string Status { get => _status; private set => Set(ref _status, value); }
        public string SaveTo { get => _saveTo; private set => Set(ref _saveTo, value); }

        public bool Checked
        {
            get => _checked;
            set
            {
                if (!Set(ref _checked, value)) return;
                lock (_owner.Gate) Entry.Checked = value;
            }
        }

        /// <summary>Takes up what the entry says now; called with the project's lock held.</summary>
        internal void Refresh()
        {
            Name = Entry.FileName ?? Entry.Url;
            Type = Entry.ContentType ?? "";
            Size = Entry.Size;
            SizeText = Entry.Size >= 0 ? Format.Bytes(Entry.Size) : "";
            Status = StatusText(Entry);
            SaveTo = Entry.LocalPath ?? "";
            if (_checked != Entry.Checked)
            {
                _checked = Entry.Checked;
                Raise(nameof(Checked));
            }
        }
    }

    public sealed class Node : Observable
    {
        bool _expanded;
        public required string Text { get; init; }
        public required string Key { get; init; }
        public List<Node> Children { get; init; } = new();
        public bool Expanded { get => _expanded; set => Set(ref _expanded, value); }
    }

    static readonly JsonSerializerOptions Copying = new();

    readonly DownloadManager _manager;
    readonly GrabberProjects _projects;
    readonly DispatcherTimer _timer;
    readonly Dictionary<GrabberEntry, Row> _rows = new();
    readonly HashSet<string> _expanded = new() { GrabberListing.AllKey };
    GrabberSettings _settings;
    GrabberProject? _project;
    GrabberRun? _run;
    ObservableCollection<Row> _shown = new();
    string _branch = GrabberListing.AllKey;
    int _step;
    bool _named;
    bool _listChanged = true;
    int _treeCount = -1;
    DateTime _treeBuilt;
    bool _showingTemplate;
    string? _note;
    DateTime _noteUntil;

    /// <param name="project">A saved project to open at its list; null starts a new one at the first step.</param>
    public GrabberWindow(DownloadManager manager, GrabberProjects projects, GrabberProject? project)
    {
        _manager = manager;
        _projects = projects;
        InitializeComponent();
        Icon = AppIcon.ImageSource;
        CategoryBox.ItemsSource = manager.State.Categories;
        Files.ItemsSource = _shown;

        if (project != null)
        {
            Attach(project);
            _named = true;
            _settings = Copy(project.Settings);
        }
        else
        {
            _settings = new GrabberSettings { StartAtOnce = true };
            GrabberTemplates.Apply(GrabberTemplates.BuiltIn[0], _settings);
        }
        ShowTemplates(GrabberTemplates.Custom);
        WriteFields();
        ShowStep(project != null ? 4 : 0);

        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => OnTick(), Dispatcher);
        _timer.Start();
        Loaded += (_, _) => (_step == 0 ? StartBox : (Control)Files).Focus();
    }

    object Gate => _project?.SyncRoot ?? new object();

    public GrabberProject? Project => _project;

    static GrabberSettings Copy(GrabberSettings settings) =>
        JsonSerializer.Deserialize<GrabberSettings>(JsonSerializer.Serialize(settings, Copying), Copying)!;

    void Attach(GrabberProject project)
    {
        _project = project;
        _run = _projects.RunOf(project);
        if (_run != null) _run.Changed += OnRunChanged;
        Title = Lang.T("grabber.title_project", project.Name);
    }

    void OnRunChanged() => _listChanged = true;

    // ---- Steps

    void ShowStep(int step)
    {
        _step = step;
        var pages = new FrameworkElement[] { StartPage, SavePage, ExplorePage, FilesPage, ListPage };
        for (int i = 0; i < pages.Length; i++) pages[i].Visibility = i == step ? Visibility.Visible : Visibility.Collapsed;
        StepText.Text = step switch
        {
            0 => Lang.T("grabber.step_start"),
            1 => Lang.T("grabber.step_save"),
            2 => Lang.T("grabber.step_explore"),
            3 => Lang.T("grabber.step_files"),
            _ => _project?.Name ?? "",
        };
        ProblemText.Text = "";
        UpdateButtons();
        if (step == 4) RefreshList(force: true);
    }

    void UpdateButtons()
    {
        bool running = Running;
        // Settings can't change under a run that is reading them.
        BackButton.IsEnabled = _step > 0 && !running;
        NextButton.IsEnabled = _step < 4;
        NextButton.Content = _step == 3 ? Lang.T("grabber.explore") : Lang.T("grabber.next");
        ExploreButton.IsEnabled = DownloadButton.IsEnabled = UpdateButton.IsEnabled = !running;
        StopButton.IsEnabled = running;
    }

    bool Running => _run?.Busy == true || (_project != null && _projects.QueueOf(_project)?.IsRunning == true);

    void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_step > 0 && !Running) ShowStep(_step - 1);
    }

    void Next_Click(object sender, RoutedEventArgs e)
    {
        if (!ReadFields(_step)) return;
        if (_step < 3)
        {
            ShowStep(_step + 1);
            return;
        }
        // The last step: the project takes these settings, and explores, or carries on.
        if (!EnsureProject()) return;
        ShowStep(4);
        if (!Running) StartRun(ExploreMode);
    }

    GrabberRunMode ExploreMode => _settings.StartAtOnce ? GrabberRunMode.ExploreAndDownload : GrabberRunMode.ExploreOnly;

    /// <summary>The project, made at the first run under a name that can't clash; its settings are the ones shown.</summary>
    bool EnsureProject()
    {
        if (_project == null)
        {
            var name = NameBox.Text.Trim();
            if (name.Length == 0) name = GrabberCrawler.StartAddress(_settings.StartUrl)?.Host ?? Lang.T("grabber.default_name");
            Attach(_projects.Create(name, Copy(_settings)));
        }
        else if (!Running)
        {
            CopyInto(_project.Settings, _settings);
            if (_named) _projects.Save(_project);
        }
        return true;
    }

    // The settings object a crawl holds stays the same one, with what it says changed.
    static void CopyInto(GrabberSettings target, GrabberSettings from)
    {
        foreach (var property in typeof(GrabberSettings).GetProperties().Where(p => p.CanWrite))
            property.SetValue(target, property.GetValue(from));
    }

    void StartRun(GrabberRunMode mode)
    {
        if (_project == null) return;
        if (!_projects.Start(_project, mode)) return;
        _listChanged = true;
        UpdateButtons();
    }

    // ---- Fields

    void WriteFields()
    {
        var s = _settings;
        StartBox.Text = s.StartUrl;
        NameBox.Text = _project?.Name ?? "";
        UserBox.Text = s.UserName ?? "";
        PasswordBox.Password = Secrets.Unprotect(s.ProtectedPassword) ?? "";
        CookiesBox.Text = Secrets.Unprotect(s.ProtectedCookies) ?? "";

        (s.SaveMode switch
        {
            GrabberSaveMode.ByCategory => ByCategoryOption,
            GrabberSaveMode.CategoryFolder => CategoryFolderOption,
            _ => FolderOption,
        }).IsChecked = true;
        CategoryBox.SelectedItem = _manager.State.Categories.FirstOrDefault(c => c.Name == s.Category) ?? _manager.State.Categories.FirstOrDefault();
        FolderBox.Text = s.Folder;
        SubfoldersBox.IsChecked = s.KeepSubfolders;
        OverwriteBox.IsChecked = s.Overwrite;
        WriteTemplateFields();
        ExploreAtOnceBox.Text = s.ExploreAtOnce.ToString();
        StartAtOnceBox.IsChecked = s.StartAtOnce;
        DescriptionBox.IsChecked = s.LinkTextAsDescription;
        DownloadAtOnceBox.Text = s.DownloadAtOnce.ToString();
    }

    // What a template sets.
    void WriteTemplateFields()
    {
        var s = _settings;
        ConvertBox.IsChecked = s.ConvertLinks;
        (s.WholeSite ? WholeSiteOption : LevelsOption).IsChecked = true;
        SiteLevelsBox.Text = s.SiteLevels.ToString();
        OtherLevelsBox.Text = s.OtherLevels.ToString();
        NoParentsBox.IsChecked = s.NoParents;
        MainDomainBox.IsChecked = s.MainDomain;
        ExploreOnlyBox.Text = s.ExploreOnly;
        ExploreNeverBox.Text = s.ExploreNever;
        IncludeBox.Text = s.Include;
        ExcludeBox.Text = s.Exclude;
        ThisSiteOnlyBox.IsChecked = s.ThisSiteOnly;
        HideCopiesBox.IsChecked = s.HideCopies;
        FilesOnlyInBox.Text = s.FilesOnlyIn;
        FilesNeverInBox.Text = s.FilesNeverIn;
        MinSizeBox.Text = s.MinSizeKB?.ToString() ?? "";
        MaxSizeBox.Text = s.MaxSizeKB?.ToString() ?? "";
    }

    /// <summary>Takes up what a step's fields say; a mistake is shown, with its box in focus, and nothing is taken.</summary>
    /// <param name="step">The step to check; -1 takes up every field that reads, checking nothing.</param>
    bool ReadFields(int step)
    {
        var s = _settings;
        bool all = step < 0;
        if (all || step == 0)
        {
            var start = StartBox.Text.Trim();
            if (!all && start.Length == 0) return Problem(Lang.T("grabber.start_missing"), StartBox);
            if (!all && GrabberCrawler.StartAddress(start) == null) return Problem(Lang.T("grabber.start_invalid"), StartBox);
            var user = UserBox.Text.Trim();
            var password = PasswordBox.Password;
            if (!all && user.Length > 0 && password.Length == 0) return Problem(Lang.T("grabber.login_half"), PasswordBox);
            if (!all && user.Length == 0 && password.Length > 0) return Problem(Lang.T("grabber.login_half"), UserBox);
            var cookies = CookiesBox.Text.Trim();
            // A pasted curl command gives its cookies.
            if (CurlCommand.IsCommand(cookies)) cookies = CurlCommand.Parse(cookies).Request?.Cookies ?? "";
            s.StartUrl = start;
            s.UserName = user.Length > 0 ? user : null;
            s.ProtectedPassword = Secrets.Protect(password.Length > 0 ? password : null);
            s.ProtectedCookies = Secrets.Protect(cookies.Length > 0 ? cookies : null);
        }
        if (all || step == 1)
        {
            var folder = FolderBox.Text.Trim();
            if (!all && FolderOption.IsChecked == true && folder.Length > 0 && !Path.IsPathFullyQualified(folder))
                return Problem(Lang.T("grabber.folder_invalid"), FolderBox);
            s.SaveMode = ByCategoryOption.IsChecked == true ? GrabberSaveMode.ByCategory
                : CategoryFolderOption.IsChecked == true ? GrabberSaveMode.CategoryFolder
                : GrabberSaveMode.Folder;
            s.Category = (CategoryBox.SelectedItem as Category)?.Name ?? Categories.General;
            s.Folder = folder;
            s.KeepSubfolders = SubfoldersBox.IsChecked == true;
            s.ConvertLinks = ConvertBox.IsChecked == true;
            s.Overwrite = OverwriteBox.IsChecked == true;
        }
        if (all || step == 2)
        {
            if (!TryNumber(SiteLevelsBox, 0, 99, out var site) && !all) return Problem(Lang.T("grabber.levels_invalid"), SiteLevelsBox);
            if (!TryNumber(OtherLevelsBox, 0, 99, out var other) && !all) return Problem(Lang.T("grabber.levels_invalid"), OtherLevelsBox);
            if (!TryNumber(ExploreAtOnceBox, 1, 10, out var atOnce) && !all) return Problem(Lang.T("grabber.at_once_invalid"), ExploreAtOnceBox);
            s.WholeSite = WholeSiteOption.IsChecked == true;
            if (site >= 0) s.SiteLevels = site;
            if (other >= 0) s.OtherLevels = other;
            if (atOnce > 0) s.ExploreAtOnce = atOnce;
            s.NoParents = NoParentsBox.IsChecked == true;
            s.MainDomain = MainDomainBox.IsChecked == true;
            s.ExploreOnly = ExploreOnlyBox.Text.Trim();
            s.ExploreNever = ExploreNeverBox.Text.Trim();
        }
        if (all || step == 3)
        {
            if (!TrySize(MinSizeBox, out var least) && !all) return Problem(Lang.T("grabber.size_invalid"), MinSizeBox);
            if (!TrySize(MaxSizeBox, out var most) && !all) return Problem(Lang.T("grabber.size_invalid"), MaxSizeBox);
            if (!TryNumber(DownloadAtOnceBox, 1, 10, out var atOnce) && !all) return Problem(Lang.T("grabber.at_once_invalid"), DownloadAtOnceBox);
            s.Include = IncludeBox.Text.Trim();
            s.Exclude = ExcludeBox.Text.Trim();
            s.ThisSiteOnly = ThisSiteOnlyBox.IsChecked == true;
            s.HideCopies = HideCopiesBox.IsChecked == true;
            s.StartAtOnce = StartAtOnceBox.IsChecked == true;
            s.LinkTextAsDescription = DescriptionBox.IsChecked == true;
            s.FilesOnlyIn = FilesOnlyInBox.Text.Trim();
            s.FilesNeverIn = FilesNeverInBox.Text.Trim();
            s.MinSizeKB = least;
            s.MaxSizeKB = most;
            if (atOnce > 0) s.DownloadAtOnce = atOnce;
        }
        return true;
    }

    static bool TryNumber(TextBox box, int least, int most, out int value)
    {
        if (int.TryParse(box.Text.Trim(), out value) && value >= least && value <= most) return true;
        value = -1;
        return false;
    }

    static bool TrySize(TextBox box, out long? value)
    {
        value = null;
        var text = box.Text.Trim();
        if (text.Length == 0) return true;
        if (!long.TryParse(text, out var number) || number < 0) return false;
        value = number;
        return true;
    }

    bool Problem(string message, Control box)
    {
        ProblemText.Text = message;
        box.Focus();
        if (box is TextBox text) text.SelectAll();
        return false;
    }

    void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { InitialDirectory = Directory.Exists(FolderBox.Text.Trim()) ? FolderBox.Text.Trim() : null };
        if (dialog.ShowDialog(this) != true) return;
        FolderBox.Text = dialog.FolderName;
        FolderOption.IsChecked = true;
    }

    void Folder_Focused(object sender, RoutedEventArgs e) => FolderOption.IsChecked = true;

    // ---- Templates

    void ShowTemplates(string select)
    {
        _showingTemplate = true;
        TemplateBox.ItemsSource = GrabberTemplates.All(_manager.State.Settings).ToList();
        TemplateBox.SelectedItem = GrabberTemplates.Find(_manager.State.Settings, select);
        _showingTemplate = false;
    }

    void Template_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_showingTemplate || TemplateBox.SelectedItem is not GrabberTemplate template) return;
        // What's typed on the other steps is kept, and the template sets its own part over it.
        ReadFields(-1);
        GrabberTemplates.Apply(template, _settings);
        WriteTemplateFields();
    }

    void SaveTemplate_Click(object sender, RoutedEventArgs e)
    {
        ReadFields(-1);
        var prompt = new PromptWindow(Lang.T("grabber.save_template_title"), Lang.T("grabber.save_template_prompt"), "") { Owner = this };
        if (prompt.ShowDialog() != true) return;
        var settings = _manager.State.Settings;
        var saved = GrabberTemplates.Save(settings, prompt.Value, _settings);
        if (saved == TemplateSaved.NameTaken
            && MessageBox.Show(this, Lang.T("grabber.template_replace", prompt.Value), Title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            saved = GrabberTemplates.Save(settings, prompt.Value, _settings, replace: true);
        if (saved == TemplateSaved.BuiltInName)
            MessageBox.Show(this, Lang.T("grabber.template_built_in", prompt.Value), Title, MessageBoxButton.OK, MessageBoxImage.Information);
        if (saved is not (TemplateSaved.Saved or TemplateSaved.Replaced)) return;
        _manager.MarkDirty();
        ShowTemplates(prompt.Value);
    }

    void DeleteTemplate_Click(object sender, RoutedEventArgs e)
    {
        if (TemplateBox.SelectedItem is not GrabberTemplate template) return;
        if (template.BuiltIn)
        {
            MessageBox.Show(this, Lang.T("grabber.template_built_in", template.Name), Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (MessageBox.Show(this, Lang.T("grabber.template_delete", template.Name), Title, MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        GrabberTemplates.Delete(_manager.State.Settings, template.Name);
        _manager.MarkDirty();
        ShowTemplates(GrabberTemplates.Custom);
    }

    // ---- Filters

    void Filters_Click(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        var box = (string)button.Tag == "Exclude" ? ExcludeBox : IncludeBox;
        var menu = new ContextMenu { PlacementTarget = button };
        foreach (var filter in _manager.State.Settings.GrabberFilters)
        {
            var item = new MenuItem { Header = filter.Name.Replace("_", "__"), ToolTip = filter.Mask };
            item.Click += (_, _) => AddMask(box, filter.Mask);
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var edit = new MenuItem { Header = Lang.T("grabber.edit_filters") };
        edit.Click += (_, _) =>
        {
            if (new GrabberFiltersWindow(_manager.State.Settings.GrabberFilters) { Owner = this }.ShowDialog() == true) _manager.MarkDirty();
        };
        menu.Items.Add(edit);
        menu.IsOpen = true;
    }

    // A filter's masks go after what's there, once each.
    static void AddMask(TextBox box, string mask)
    {
        var have = box.Text.Split(',', ';').Select(m => m.Trim()).Where(m => m.Length > 0).ToList();
        if (have.Count == 1 && have[0] is "*" or "*.*") have.Clear();
        foreach (var part in mask.Split(',', ';').Select(m => m.Trim()))
            if (part.Length > 0 && !have.Contains(part, StringComparer.OrdinalIgnoreCase)) have.Add(part);
        box.Text = string.Join(",", have);
    }

    // ---- The list

    void OnTick()
    {
        UpdateButtons();
        if (_step != 4) return;
        RefreshList(force: false);
        // The tree catches up with a list that grew within its last couple of seconds, and at once when the run stops.
        if (_project != null && (!Running || DateTime.Now - _treeBuilt > TimeSpan.FromSeconds(2)))
        {
            int count;
            lock (_project.SyncRoot) count = _project.Entries.Count;
            if (count != _treeCount) BuildTree(count);
        }
        ShowListStatus();
    }

    void ShowListStatus()
    {
        if (_project == null) return;
        if (_note != null && DateTime.Now < _noteUntil)
        {
            ListStatus.Text = _note;
            return;
        }
        var counts = GrabberListing.Statistics(_project);
        var text = Lang.T("grabber.counts", counts.Listed, counts.Checked, counts.Saved);
        if (_run?.Crawler != null) text = Lang.T("grabber.exploring", counts.PagesRead, counts.Waiting) + "  " + text;
        else if (Running) text = Lang.T("grabber.downloading") + "  " + text;
        if (_run?.Error is { } error) text = error + "  " + text;
        if (_run?.Crawler?.LimitReached == true) text += "  " + Lang.T("grabber.limit_reached", GrabberCrawler.MaxEntries);
        ListStatus.Text = text;
    }

    void Note(string text)
    {
        _note = text;
        _noteUntil = DateTime.Now.AddSeconds(6);
        ListStatus.Text = text;
    }

    /// <summary>At most twice a second, as the timer calls it: the rows take up what changed, and new ones join.</summary>
    void RefreshList(bool force)
    {
        if (_project == null || (!force && !_listChanged)) return;
        _listChanged = false;
        var filter = GrabberListing.Filter(_branch);
        var wanted = new List<Row>();
        int count;
        lock (_project.SyncRoot)
        {
            count = _project.Entries.Count;
            foreach (var entry in _project.Entries)
            {
                if (!filter(entry)) continue;
                if (!_rows.TryGetValue(entry, out var row)) _rows[entry] = row = new Row(this, entry);
                row.Refresh();
                wanted.Add(row);
            }
        }
        // Usually only new rows at the end; anything else shows the list afresh.
        bool grows = wanted.Count >= _shown.Count && _shown.Select((r, i) => wanted[i] == r).All(same => same);
        if (grows)
        {
            for (int i = _shown.Count; i < wanted.Count; i++) _shown.Add(wanted[i]);
        }
        else
        {
            _shown = new ObservableCollection<Row>(wanted);
            Files.ItemsSource = _shown;
        }
        // The tree is rebuilt as the list grows, every couple of seconds at most.
        if (force || (count != _treeCount && DateTime.Now - _treeBuilt > TimeSpan.FromSeconds(2))) BuildTree(count);
    }

    void BuildTree(int count)
    {
        if (_project == null) return;
        _treeCount = count;
        _treeBuilt = DateTime.Now;
        GrabberNode tree;
        lock (_project.SyncRoot)
            tree = GrabberListing.Tree(_project.Entries, Lang.T("grabber.all_files"), Lang.T("grabber.by_link"), Lang.T("grabber.by_folder"));
        Node Make(GrabberNode node) => new()
        {
            Text = $"{node.Label} ({node.Count})",
            Key = node.Key,
            Expanded = _expanded.Contains(node.Key),
            Children = node.Children.Select(Make).ToList(),
        };
        var root = Make(tree);
        root.Expanded = true;
        foreach (var node in Walk(root)) node.PropertyChanged += (sender, _) => Remember((Node)sender!);
        Tree.ItemsSource = new[] { root };
    }

    static IEnumerable<Node> Walk(Node node) => node.Children.SelectMany(Walk).Prepend(node);

    void Remember(Node node)
    {
        if (node.Expanded) _expanded.Add(node.Key);
        else _expanded.Remove(node.Key);
    }

    void Tree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is not Node node || node.Key == _branch) return;
        _branch = node.Key;
        RefreshList(force: true);
    }

    static string StatusText(GrabberEntry entry) => GrabberListing.StatusOf(entry) switch
    {
        GrabberEntryStatus.Waiting => Lang.T("grabber.status_waiting"),
        GrabberEntryStatus.NotFound => Lang.T("grabber.status_not_found"),
        GrabberEntryStatus.Queued => Lang.T("grabber.status_queued"),
        GrabberEntryStatus.Saved => Lang.T("grabber.status_saved"),
        GrabberEntryStatus.NotSaved => Lang.T("grabber.status_not_saved"),
        GrabberEntryStatus.Changed => Lang.T("grabber.status_changed"),
        _ => Lang.T("grabber.status_listed"),
    };

    IEnumerable<Row> SelectedRows => Files.SelectedItems.OfType<Row>().ToList();

    void SetChecked(IEnumerable<Row> rows, bool value)
    {
        foreach (var row in rows) row.Checked = value;
    }

    void CheckAll_Click(object sender, RoutedEventArgs e) => SetChecked(_shown, true);

    void UncheckAll_Click(object sender, RoutedEventArgs e) => SetChecked(_shown, false);

    void CheckSelected_Click(object sender, RoutedEventArgs e) => SetChecked(SelectedRows, true);

    void UncheckSelected_Click(object sender, RoutedEventArgs e) => SetChecked(SelectedRows, false);

    void Explore_Click(object sender, RoutedEventArgs e)
    {
        if (EnsureProject()) StartRun(ExploreMode);
    }

    void Download_Click(object sender, RoutedEventArgs e)
    {
        if (EnsureProject()) StartRun(GrabberRunMode.DownloadChecked);
    }

    void Update_Click(object sender, RoutedEventArgs e)
    {
        if (EnsureProject()) StartRun(GrabberRunMode.Sync);
    }

    void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (_project != null) _projects.Stop(_project);
        UpdateButtons();
    }

    void AddToMainList_Click(object sender, RoutedEventArgs e)
    {
        if (_project == null) return;
        List<GrabberEntry> chosen;
        lock (_project.SyncRoot) chosen = _project.Entries.Where(en => en.Matched && en.Checked).ToList();
        if (chosen.Count == 0)
        {
            MessageBox.Show(this, Lang.T("common.check_one_link"), Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var added = _projects.AddToMainList(_project, chosen, null);
        if (added < 0)
            MessageBox.Show(this, Lang.T("grabber.too_many_for_main_list", chosen.Count, GrabberProjects.MaxAddToMainList), Title, MessageBoxButton.OK, MessageBoxImage.Information);
        else
            Note(Lang.T("grabber.added_to_main_list", added));
    }

    void Statistics_Click(object sender, RoutedEventArgs e)
    {
        if (_project != null) new GrabberStatsWindow(GrabberListing.Statistics(_project), _run?.Crawler?.LimitReached == true) { Owner = this }.ShowDialog();
    }

    void Schedule_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureProject() || _project == null) return;
        if (!_named && !SaveUnderName()) return;
        new SchedulerWindow(_manager, _projects.QueueOf(_project), _projects) { Owner = this }.ShowDialog();
        // A sync turns overwriting on, and what's shown is the project's settings as they are now.
        _settings = Copy(_project.Settings);
        WriteFields();
    }

    void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!ReadFields(-1) || !EnsureProject() || _project == null) return;
        if (!_named) SaveUnderName();
        else
        {
            _projects.Save(_project);
            Note(Lang.T("grabber.saved", _project.Name));
        }
    }

    /// <summary>Asks for the name to keep a new project under; false if the user turned it down.</summary>
    bool SaveUnderName()
    {
        if (_project == null) return false;
        var dialog = new SaveProjectWindow(_projects.UniqueName(NameBox.Text.Trim().Length > 0 ? NameBox.Text : _project.Name, _project.Id), offerNotSaving: false) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Choice != SaveProjectWindow.Answer.Save) return false;
        Rename(dialog.ProjectName);
        return true;
    }

    void Rename(string name)
    {
        if (_project == null) return;
        _project.Name = name;
        _projects.Save(_project);
        _named = true;
        NameBox.Text = _project.Name;
        Title = Lang.T("grabber.title_project", _project.Name);
        if (_step == 4) StepText.Text = _project.Name;
    }

    Row? RowAt(object source)
    {
        for (var node = source as DependencyObject; node != null; node = System.Windows.Media.VisualTreeHelper.GetParent(node))
            if (node is DataGridRow row) return row.Item as Row;
        return null;
    }

    void Files_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (RowAt(e.OriginalSource) is not { } row) return;
        if (File.Exists(row.SaveTo)) Open(row.SaveTo);
        else ShowProperties(row);
    }

    void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in SelectedRows.Where(r => File.Exists(r.SaveTo))) Open(row.SaveTo);
    }

    static void Open(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Win32Exception) { }
    }

    void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRows.FirstOrDefault() is not { } row) return;
        if (File.Exists(row.SaveTo)) Process.Start("explorer.exe", $"/select,\"{row.SaveTo}\"");
        else if (Path.GetDirectoryName(row.SaveTo) is { } folder && Directory.Exists(folder)) Process.Start("explorer.exe", $"\"{folder}\"");
    }

    void Properties_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRows.FirstOrDefault() is { } row) ShowProperties(row);
    }

    void ShowProperties(Row row)
    {
        string text;
        lock (Gate)
        {
            var en = row.Entry;
            text = Lang.T("grabber.properties", en.Url, en.FinalUrl ?? en.Url, en.Referrer ?? "", en.LinkText ?? "", en.ContentType ?? "",
                en.Size >= 0 ? Format.Bytes(en.Size) : "", row.Status, en.LocalPath ?? "", en.Error ?? "");
        }
        MessageBox.Show(this, text, row.Name, MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ---- Closing

    void Close_Click(object sender, RoutedEventArgs e) => Close();

    bool _closing;

    protected override async void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        // ADM closing takes the project as it is, and asks nothing.
        if (_closing || _project == null || App.Exiting)
        {
            _timer.Stop();
            return;
        }
        e.Cancel = true;
        if (!_named)
        {
            var dialog = new SaveProjectWindow(_projects.UniqueName(_project.Name, _project.Id), offerNotSaving: true) { Owner = this };
            if (dialog.ShowDialog() != true || dialog.Choice == SaveProjectWindow.Answer.Cancel) return;
            if (dialog.Choice == SaveProjectWindow.Answer.Save) Rename(dialog.ProjectName);
            else
            {
                // Not kept: the project goes, and what it saved stays.
                Detach();
                await _projects.DeleteAsync(_project.Id);
                Finish();
                return;
            }
        }
        if (!Running)
        {
            ReadFields(-1);
            CopyInto(_project.Settings, _settings);
        }
        _projects.Save(_project);
        Detach();
        await _projects.CloseAsync(_project);
        Finish();
    }

    /// <summary>Closes without asking anything, as the project is being deleted.</summary>
    internal void Abandon()
    {
        Detach();
        Finish();
    }

    void Detach()
    {
        _timer.Stop();
        if (_run != null) _run.Changed -= OnRunChanged;
    }

    void Finish()
    {
        _closing = true;
        Close();
    }
}
