using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using ADM.Core;
using Microsoft.Win32;

namespace ADM.App;

/// <summary>
/// Edits queues. Like the queue list itself, changes to a queue's files apply at once; the other settings
/// apply with Apply, Start now, on moving to another queue, and on closing.
/// </summary>
public partial class SchedulerWindow : ThemedWindow
{
    static readonly (CompletionAction Action, string Label)[] CompletionChoices =
    {
        (CompletionAction.None, Lang.T("scheduler.when_done_nothing")),
        (CompletionAction.ExitApp, Lang.T("scheduler.when_done_exit")),
        (CompletionAction.Sleep, Lang.T("scheduler.when_done_sleep")),
        (CompletionAction.Hibernate, Lang.T("scheduler.when_done_hibernate")),
        (CompletionAction.Shutdown, Lang.T("scheduler.when_done_shut_down")),
    };

    readonly DownloadManager _manager;
    readonly GrabberProjects? _projects;
    readonly Dictionary<DayOfWeek, CheckBox> _days = new();
    DownloadQueue? _shown;
    bool _reverting;

    /// <param name="projects">Given with a grabber project's queue, which then shows alone, with what the project runs.</param>
    public SchedulerWindow(DownloadManager manager, DownloadQueue? select = null, GrabberProjects? projects = null)
    {
        _manager = manager;
        _projects = projects;
        InitializeComponent();
        Icon = AppIcon.ImageSource;

        var culture = CultureInfo.CurrentCulture.DateTimeFormat;
        foreach (var day in Enumerable.Range(0, 7).Select(i => (DayOfWeek)(((int)culture.FirstDayOfWeek + i) % 7)))
        {
            var box = new CheckBox { Content = culture.GetAbbreviatedDayName(day), Margin = new Thickness(0, 0, 10, 4) };
            _days[day] = box;
            DaysPanel.Children.Add(box);
        }
        CompletionBox.ItemsSource = CompletionChoices.Select(c => c.Label).ToList();

        // A grabber project's queue is the project's own: it shows only when opened on it, and alone.
        QueueList.ItemsSource = select?.GrabberProjectId != null
            ? new[] { select }
            : new System.Windows.Data.ListCollectionView(manager.Queues) { Filter = q => ((DownloadQueue)q).GrabberProjectId == null };
        QueueList.SelectedItem = select ?? manager.MainQueue;
    }

    DownloadQueue? Current => QueueList.SelectedItem as DownloadQueue;

    void QueueList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_reverting) return;
        // Leaving a queue keeps its edits; a mistake keeps the user on it to fix.
        if (_shown != null && _shown != Current && _manager.Queues.Contains(_shown) && !Apply(_shown))
        {
            _reverting = true;
            QueueList.SelectedItem = _shown;
            _reverting = false;
            return;
        }
        ShowQueue(Current);
    }

    void ShowQueue(DownloadQueue? q)
    {
        _shown = q;
        if (q is null) return;
        var s = q.Schedule;
        NameBox.Text = q.Name;
        (q.Kind == QueueKind.Sync ? SyncKindBox : DownloadKindBox).IsChecked = true;
        DownloadKindBox.IsEnabled = SyncKindBox.IsEnabled = !q.IsMain;
        KindFixedHint.Visibility = q.IsMain ? Visibility.Visible : Visibility.Collapsed;
        DeleteButton.IsEnabled = !q.IsMain && ProjectOf(q) == null;
        ShowRuns(q);

        StartOnLaunchBox.IsChecked = q.StartOnLaunch;
        EnabledBox.IsChecked = s.Enabled;
        StartBox.Text = s.StartTime?.ToString(@"hh\:mm") ?? "";
        RepeatBox.Text = s.RepeatMinutes.ToString();
        StopEnabledBox.IsChecked = s.StopTime != null;
        StopBox.Text = s.StopTime?.ToString(@"hh\:mm") ?? "";
        foreach (var (day, box) in _days) box.IsChecked = s.Days.Contains(day);

        RunFileBox.IsChecked = !string.IsNullOrWhiteSpace(s.RunWhenDone);
        RunFilePathBox.Text = s.RunWhenDone ?? "";
        CompletionBox.SelectedIndex = Math.Max(0, Array.FindIndex(CompletionChoices, c => c.Action == s.OnComplete));
        ForceCloseBox.IsChecked = s.ForceCloseApps;

        ConcurrentBox.Text = q.MaxConcurrent.ToString();
        LimitRetriesBox.IsChecked = q.LimitRetries;
        RetriesBox.Text = q.RetriesPerFile.ToString();
        ShowFiles(q);
        UpdateEnabled();
    }

    void ShowFiles(DownloadQueue q, IReadOnlyCollection<DownloadItem>? select = null)
    {
        FilesList.ItemsSource = _manager.QueueItems(q);
        if (select == null) return;
        foreach (var item in select) FilesList.SelectedItems.Add(item);
    }

    void Kind_Changed(object sender, RoutedEventArgs e) => UpdateEnabled();

    void CompletionBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateEnabled();

    void UpdateEnabled()
    {
        // Only sync queues repeat; forcing programs closed only means something when shutting down.
        if (RepeatPanel == null || ForceCloseBox == null) return;
        var sync = _shown != null && ProjectOf(_shown) != null ? RunSyncBox.IsChecked == true : SyncKindBox.IsChecked == true;
        RepeatPanel.Visibility = sync ? Visibility.Visible : Visibility.Collapsed;
        ForceCloseBox.IsEnabled = CompletionBox.SelectedIndex >= 0 && CompletionChoices[CompletionBox.SelectedIndex].Action == CompletionAction.Shutdown;
    }

    /// <summary>The project whose queue this is; null for an ordinary queue.</summary>
    GrabberProject? ProjectOf(DownloadQueue q) => q.GrabberProjectId is { } id ? _projects?.Open(id) : null;

    /// <summary>A project's queue shows what its schedule runs, in place of the queue's kind, which follows from it.</summary>
    void ShowRuns(DownloadQueue q)
    {
        var project = ProjectOf(q);
        RunPanel.Visibility = project != null ? Visibility.Visible : Visibility.Collapsed;
        DownloadKindBox.Visibility = SyncKindBox.Visibility = project != null ? Visibility.Collapsed : Visibility.Visible;
        // Its name is the project's, and a project's window is where it changes.
        NameBox.IsEnabled = NewQueueButton.IsEnabled = project == null;
        if (project == null) return;
        (project.ScheduledRun switch
        {
            GrabberRunMode.ExploreOnly => RunExploreBox,
            GrabberRunMode.DownloadChecked => RunCheckedBox,
            GrabberRunMode.Sync => RunSyncBox,
            _ => RunBothBox,
        }).IsChecked = true;
    }

    GrabberRunMode ChosenRun => RunExploreBox.IsChecked == true ? GrabberRunMode.ExploreOnly
        : RunCheckedBox.IsChecked == true ? GrabberRunMode.DownloadChecked
        : RunSyncBox.IsChecked == true ? GrabberRunMode.Sync
        : GrabberRunMode.ExploreAndDownload;

    bool Apply(DownloadQueue q)
    {
        var project = ProjectOf(q);
        var sync = project != null ? ChosenRun == GrabberRunMode.Sync : SyncKindBox.IsChecked == true;
        if (!int.TryParse(ConcurrentBox.Text, out var concurrent) || concurrent < 1 || concurrent > 20)
            return Fail(Lang.T("scheduler.files_invalid"), ConcurrentBox);
        if (!int.TryParse(RetriesBox.Text, out var retries) || retries < 0 || retries > 999)
            return Fail(Lang.T("scheduler.retries_invalid"), RetriesBox);
        if (!TryParseTime(StartBox.Text, out var start)) return Fail(Lang.T("scheduler.start_invalid"), StartBox);
        if (EnabledBox.IsChecked == true && start == null) return Fail(Lang.T("scheduler.start_missing"), StartBox);
        if (!TryParseTime(StopBox.Text, out var stop)) return Fail(Lang.T("scheduler.stop_invalid"), StopBox);
        if (StopEnabledBox.IsChecked == true && stop == null) return Fail(Lang.T("scheduler.stop_missing"), StopBox);
        var repeat = 0;
        if (sync && (!int.TryParse(RepeatBox.Text, out repeat) || repeat < 0 || repeat > 1440))
            return Fail(Lang.T("scheduler.repeat_invalid"), RepeatBox);
        var days = _days.Where(d => d.Value.IsChecked == true).Select(d => d.Key).ToList();
        if ((EnabledBox.IsChecked == true || StopEnabledBox.IsChecked == true) && days.Count == 0)
            return Fail(Lang.T("scheduler.days_invalid"), DaysPanel);
        var runFile = RunFilePathBox.Text.Trim().Trim('"');
        if (RunFileBox.IsChecked == true && !QueueFinish.CanRun(runFile))
            return Fail(Lang.T("scheduler.run_file_invalid"), RunFilePathBox);

        var s = q.Schedule;
        q.Name = string.IsNullOrWhiteSpace(NameBox.Text) ? q.Name : NameBox.Text.Trim();
        // A project's run sets its queue's kind, a sync being a sync queue.
        if (project != null && _projects != null) _projects.SetScheduledRun(project, ChosenRun);
        else _manager.SetKind(q, sync ? QueueKind.Sync : QueueKind.Download);
        q.StartOnLaunch = StartOnLaunchBox.IsChecked == true;
        q.MaxConcurrent = concurrent;
        q.LimitRetries = LimitRetriesBox.IsChecked == true;
        q.RetriesPerFile = retries;
        s.Enabled = EnabledBox.IsChecked == true;
        s.StartTime = start;
        s.RepeatMinutes = repeat;
        s.StopTime = StopEnabledBox.IsChecked == true ? stop : null;
        s.Days = days;
        s.RunWhenDone = RunFileBox.IsChecked == true ? runFile : null;
        s.OnComplete = CompletionChoices[Math.Max(0, CompletionBox.SelectedIndex)].Action;
        s.ForceCloseApps = ForceCloseBox.IsChecked == true;
        _manager.MarkDirty();
        ShowFiles(q);
        return true;
    }

    static bool TryParseTime(string text, out TimeSpan? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (!TimeSpan.TryParseExact(text.Trim(), new[] { @"h\:mm", @"hh\:mm" }, CultureInfo.InvariantCulture, out var t)) return false;
        value = t;
        return true;
    }

    /// <summary>Shows the problem, then switches to the tab holding the field and focuses it.</summary>
    bool Fail(string message, FrameworkElement field)
    {
        MessageBox.Show(this, message, Lang.T("scheduler.title"), MessageBoxButton.OK, MessageBoxImage.Warning);
        for (DependencyObject? parent = field; parent != null; parent = LogicalTreeHelper.GetParent(parent))
        {
            if (parent is TabItem tab)
            {
                Tabs.SelectedItem = tab;
                break;
            }
        }
        field.Focus();
        return false;
    }

    void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (Current is { } q) Apply(q);
    }

    void StartNow_Click(object sender, RoutedEventArgs e)
    {
        if (Current is { } q && Apply(q)) _manager.StartQueue(q);
    }

    void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (Current is { } q) _manager.StopQueue(q);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (!e.Cancel && Current is { } q && _manager.Queues.Contains(q) && !Apply(q)) e.Cancel = true;
    }

    void BrowseRunFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = Lang.T("scheduler.run_file_title"), Filter = Lang.Filter(Lang.T("common.all_files"), "*.*") };
        if (dialog.ShowDialog(this) != true) return;
        RunFilePathBox.Text = dialog.FileName;
        RunFileBox.IsChecked = true;
    }

    void NewQueue_Click(object sender, RoutedEventArgs e)
    {
        if (Current is { } q && !Apply(q)) return;
        var queue = new DownloadQueue { Name = Lang.T("queue.default_name", _manager.Queues.Count + 1) };
        _manager.Queues.Add(queue);
        _manager.MarkDirty();
        QueueList.SelectedItem = queue;
        NameBox.Focus();
        NameBox.SelectAll();
    }

    void DeleteQueue_Click(object sender, RoutedEventArgs e)
    {
        if (Current is not { IsMain: false } q) return;
        var main = q.Kind == QueueKind.Sync ? _manager.SyncQueue : _manager.MainQueue;
        var prompt = Lang.T("queue.delete_prompt", q.Name, main.Name);
        if (MessageBox.Show(this, prompt, Lang.T("scheduler.title"), MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        _manager.DeleteQueue(q);
        _shown = null;
        QueueList.SelectedItem = main;
    }

    List<DownloadItem> SelectedFiles => FilesList.SelectedItems.Cast<DownloadItem>().ToList();

    void Move(QueueMove move)
    {
        if (Current is not { } q || SelectedFiles is not { Count: > 0 } selected) return;
        _manager.MoveInQueue(selected, move);
        ShowFiles(q, selected);
        FilesList.ScrollIntoView(selected[0]);
    }

    void MoveTop_Click(object sender, RoutedEventArgs e) => Move(QueueMove.Top);
    void MoveUp_Click(object sender, RoutedEventArgs e) => Move(QueueMove.Up);
    void MoveDown_Click(object sender, RoutedEventArgs e) => Move(QueueMove.Down);
    void MoveBottom_Click(object sender, RoutedEventArgs e) => Move(QueueMove.Bottom);

    void RemoveFiles_Click(object sender, RoutedEventArgs e)
    {
        if (Current is not { } q) return;
        foreach (var item in SelectedFiles) _manager.RemoveFromQueue(item);
        ShowFiles(q);
    }
}
