using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ADM.Core;

namespace ADM.App;

/// <summary>
/// One download's own window: its progress and connections, a speed limit for it alone, and what to do when it
/// finishes. Cancel and the close button pause the download. When it finishes, the main window closes this one.
/// </summary>
public partial class ProgressWindow : ThemedWindow
{
    /// <summary>A row of the connections list, changed in place so the list keeps its scroll and selection.</summary>
    public sealed class ConnectionRow(int number) : Observable
    {
        public int Number { get; } = number;
        public string Downloaded { get => _downloaded; set => Set(ref _downloaded, value); }
        string _downloaded = "";
        public string Info { get => _info; set => Set(ref _info, value); }
        string _info = "";
    }

    readonly DownloadManager _manager;
    readonly MainWindow _main;
    readonly DispatcherTimer _timer;
    readonly ObservableCollection<ConnectionRow> _rows = new();
    bool _closingForCompletion;
    // Ten times a second, as IDM's figures move: the bytes and the speed visibly keep pace with the transfer.
    static readonly TimeSpan RefreshEvery = TimeSpan.FromMilliseconds(100);
    // Where the bar is headed: it glides there over one refresh.
    double _barTarget;
    // Three frames a refresh: smooth for a bar that moves a few pixels at a time. At the screen's rate, which an
    // animation takes by default, WPF drew twice as often for it (measured: the drawing thread's time halved).
    const int GlideFramesPerSecond = 30;

    public ProgressWindow(DownloadManager manager, MainWindow main, DownloadItem item)
    {
        InitializeComponent();
        Icon = AppIcon.ImageSource;
        _manager = manager;
        _main = main;
        Item = item;

        UrlText.Text = item.Url;
        Segments.Item = item;
        ConnectionsGrid.ItemsSource = _rows;
        LimitBox.IsChecked = item.SpeedLimitKBps > 0;
        LimitKBBox.Text = (item.SpeedLimitKBps > 0 ? item.SpeedLimitKBps : 100).ToString();
        KeepLimitBox.IsChecked = item.KeepSpeedLimit;
        ShowCompleteBox.IsChecked = manager.State.Settings.ShowCompleteDialog;
        LimitBox.Checked += (_, _) => ApplyLimit();
        LimitBox.Unchecked += (_, _) => ApplyLimit();
        LimitKBBox.TextChanged += (_, _) => ApplyLimit();
        KeepLimitBox.Checked += (_, _) => ApplyKeep();
        KeepLimitBox.Unchecked += (_, _) => ApplyKeep();
        TurnOffBox.Checked += (_, _) => UpdateForce();
        TurnOffBox.Unchecked += (_, _) => UpdateForce();
        TurnOffChoice.SelectionChanged += (_, _) => UpdateForce();
        UpdateForce();
        ApplyTabSettings();

        // Only while the window shows: one waiting in the tray, as Options can have it, would otherwise refresh
        // ten times a second for nobody, and its download's row in the main list with it. (The constructor that
        // takes a handler starts the timer at once.)
        _timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = RefreshEvery };
        _timer.Tick += (_, _) => Refresh();
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible)
            {
                _timer.Stop();
                return;
            }
            Refresh();
            _timer.Start();
        };
        Refresh();
    }

    public DownloadItem Item { get; }

    // The When done tab's choices, which last only as long as this window.
    public bool ShowCompleteDialog => ShowCompleteBox.IsChecked == true;
    public bool ExitWhenDone => ExitBox.IsChecked == true;
    public CompletionAction TurnOff => TurnOffBox.IsChecked != true ? CompletionAction.None
        : TurnOffChoice.SelectedIndex switch { 1 => CompletionAction.Sleep, 2 => CompletionAction.Hibernate, _ => CompletionAction.Shutdown };
    public bool ForceClose => ForceBox.IsChecked == true && TurnOff == CompletionAction.Shutdown;

    /// <summary>The download finished or was deleted: close without pausing anything.</summary>
    public void CloseWithoutPausing()
    {
        _closingForCompletion = true;
        Close();
    }

    void Refresh()
    {
        var item = Item;
        var running = _manager.IsRunning(item);
        var now = DateTime.Now;
        // A sample each refresh, so the speed and time left move as often as the bytes do.
        if (running) item.Tick(now);
        // With no size known, a percentage would read 0 however much has come, so the bytes stand in for it.
        var downloaded = Format.Bytes(item.DownloadedBytes);
        Title = item.ProgressKnown ? $"{item.ProgressText} {item}" : $"{downloaded} {item}";
        StatusValue.Text = item.StatusText;
        SizeValue.Text = item.TotalBytes >= 0 ? Lang.T("common.size_and_bytes", Format.Bytes(item.TotalBytes), item.TotalBytes) : Lang.T("common.unknown");
        DownloadedValue.Text = item.ProgressKnown ? $"{downloaded} ({item.ProgressText})" : downloaded;
        RateValue.Text = item.SpeedText;
        TimeLeftValue.Text = item.EtaText;
        ResumeValue.Text = item.Resumable switch { true => Lang.T("progress.resume_yes"), false => Lang.T("progress.resume_no"), null => Lang.T("progress.resume_not_known") };
        Glide(item.Progress);
        PauseButton.Content = running ? Lang.T("progress.pause_button") : Lang.T("progress.resume_button");
        CurrentRateText.Text = !running ? Lang.T("progress.not_downloading") : item.IsCheckingFile ? item.StatusText : Lang.T("progress.downloading_at", item.SpeedText);
        if (DetailsPanel.IsVisible)
        {
            Segments.InvalidateVisual();
            ShowConnections(item.ConnectionsNow());
        }
    }

    /// <summary>One row per connection, as IDM lists them: what each has brought, and what it is doing.</summary>
    void ShowConnections(IReadOnlyList<ConnectionInfo> connections)
    {
        while (_rows.Count > connections.Count) _rows.RemoveAt(_rows.Count - 1);
        while (_rows.Count < connections.Count) _rows.Add(new ConnectionRow(_rows.Count + 1));
        for (int i = 0; i < connections.Count; i++)
        {
            _rows[i].Downloaded = Format.Bytes(connections[i].Downloaded);
            _rows[i].Info = StageText(connections[i].Stage);
        }
        NoConnectionsText.Visibility = connections.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    static string StageText(ConnectionStage stage) => stage switch
    {
        ConnectionStage.Connecting => Lang.T("progress.part_connecting"),
        ConnectionStage.Asking => Lang.T("progress.part_asking"),
        ConnectionStage.Receiving => Lang.T("progress.part_receiving"),
        ConnectionStage.Waiting => Lang.T("progress.part_waiting"),
        ConnectionStage.Complete => Lang.T("progress.part_complete"),
        _ => Lang.T("progress.part_idle"),
    };

    /// <summary>
    /// Glides the bar to <paramref name="value"/> over one refresh, so it moves on steadily between refreshes rather
    /// than stepping at each. Going back, as a download that starts over does, or with Windows' animations off, it
    /// goes there at once.
    /// </summary>
    void Glide(double value)
    {
        if (value == _barTarget) return;
        if (value < _barTarget || !SystemParameters.ClientAreaAnimation)
        {
            Bar.BeginAnimation(RangeBase.ValueProperty, null);
            Bar.Value = value;
        }
        else
        {
            var glide = new DoubleAnimation(value, RefreshEvery);
            Timeline.SetDesiredFrameRate(glide, GlideFramesPerSecond);
            Bar.BeginAnimation(RangeBase.ValueProperty, glide);
        }
        _barTarget = value;
    }

    void Details_Click(object sender, RoutedEventArgs e)
    {
        var show = DetailsPanel.Visibility != Visibility.Visible;
        DetailsPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        DetailsButton.Content = show ? Lang.T("progress.hide_details") : Lang.T("progress.show_details");
        Refresh();
    }

    void Pause_Click(object sender, RoutedEventArgs e)
    {
        if (_manager.IsRunning(Item)) _main.Pause(Item, this);
        else _manager.Start(Item);
        Refresh();
    }

    void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        // As in IDM, closing the window stops the download; the user may still say no to losing progress. A virus
        // scan has nothing left to download, so it carries on.
        if (!_closingForCompletion && _manager.IsRunning(Item) && !Item.IsCheckingFile && !_main.Pause(Item, this)) e.Cancel = true;
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        base.OnClosed(e);
    }

    void ApplyLimit()
    {
        if (LimitBox.IsChecked != true) Item.SpeedLimitKBps = 0;
        else if (long.TryParse(LimitKBBox.Text, out var limit) && limit > 0) Item.SpeedLimitKBps = limit;
        _manager.MarkDirty();
    }

    void ApplyKeep()
    {
        Item.KeepSpeedLimit = KeepLimitBox.IsChecked == true;
        _manager.MarkDirty();
    }

    void UpdateForce() => ForceBox.IsEnabled = TurnOffBox.IsChecked == true && TurnOffChoice.SelectedIndex == 0;

    void HideSpeedTab_Click(object sender, RoutedEventArgs e)
    {
        _manager.State.Settings.ProgressSpeedTab = false;
        _manager.MarkDirty();
        ApplyTabSettings();
    }

    void HideWhenDoneTab_Click(object sender, RoutedEventArgs e)
    {
        _manager.State.Settings.ProgressWhenDoneTab = false;
        _manager.MarkDirty();
        ApplyTabSettings();
    }

    void Customize_Click(object sender, RoutedEventArgs e)
    {
        if (new ProgressCustomizeWindow(_manager.State.Settings) { Owner = this }.ShowDialog() != true) return;
        _manager.MarkDirty();
        ApplyTabSettings();
    }

    void ApplyTabSettings()
    {
        var s = _manager.State.Settings;
        SpeedTab.Visibility = s.ProgressSpeedTab ? Visibility.Visible : Visibility.Collapsed;
        WhenDoneTab.Visibility = s.ProgressWhenDoneTab ? Visibility.Visible : Visibility.Collapsed;
        HideSpeedTabButton.Visibility = HideWhenDoneTabButton.Visibility = s.ProgressHideTabButtons ? Visibility.Visible : Visibility.Collapsed;
        if (Tabs.SelectedItem is TabItem { Visibility: not Visibility.Visible }) Tabs.SelectedIndex = 0;
    }
}
