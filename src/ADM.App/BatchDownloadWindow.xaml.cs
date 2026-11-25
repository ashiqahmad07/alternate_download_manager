using System.Windows;
using System.Windows.Controls;
using ADM.Core;

namespace ADM.App;

/// <summary>
/// Adds many links at once: either generated from a wildcard pattern (img*.jpg from 1 to 100)
/// or picked from a list found in the clipboard or an imported file.
/// </summary>
public partial class BatchDownloadWindow : ThemedWindow
{
    public sealed class Row : Observable
    {
        bool _checked = true;
        public string Url { get; init; } = "";
        public bool Checked { get => _checked; set => Set(ref _checked, value); }
    }

    List<Row> _rows = new();

    /// <param name="links">Links to choose from, or null to build them from a pattern.</param>
    public BatchDownloadWindow(DownloadManager manager, IReadOnlyList<string>? links, string? title = null)
    {
        InitializeComponent();
        Icon = AppIcon.ImageSource;
        Title = title ?? Lang.T("batchdownload.title");
        QueueBox.ItemsSource = manager.Queues;
        QueueBox.SelectedItem = manager.MainQueue;

        if (links != null)
        {
            PatternPanel.Visibility = Visibility.Collapsed;
            ShowRows(links);
        }
        else
        {
            Loaded += (_, _) => PatternBox.Focus();
            ShowRows(Array.Empty<string>());
        }
    }

    public List<string> Urls { get; private set; } = new();
    public DownloadQueue? Queue { get; private set; }
    public bool StartQueue { get; private set; }

    void Pattern_Changed(object sender, TextChangedEventArgs e)
    {
        if (!IsInitialized || PatternBox.Text.Trim().Length == 0) return;
        try
        {
            int.TryParse(WidthBox.Text, out var width);
            ShowRows(BatchPattern.Expand(PatternBox.Text.Trim(), FromBox.Text, ToBox.Text, Math.Clamp(width, 0, 12)));
            PatternError.Text = "";
        }
        catch (FormatException ex)
        {
            ShowRows(Array.Empty<string>());
            PatternError.Text = ex.Message;
        }
    }

    void ShowRows(IEnumerable<string> links)
    {
        _rows = links.Select(u => new Row { Url = u }).ToList();
        LinkList.ItemsSource = _rows;
        UpdateCount();
    }

    void UpdateCount() => CountText.Text = Lang.T("common.selected_count", _rows.Count(r => r.Checked), _rows.Count);

    void Row_Click(object sender, RoutedEventArgs e) => UpdateCount();

    void CheckAll_Click(object sender, RoutedEventArgs e) => SetAll(true);

    void UncheckAll_Click(object sender, RoutedEventArgs e) => SetAll(false);

    void SetAll(bool value)
    {
        foreach (var row in _rows) row.Checked = value;
        UpdateCount();
    }

    void Add_Click(object sender, RoutedEventArgs e)
    {
        Urls = _rows.Where(r => r.Checked).Select(r => r.Url).ToList();
        if (Urls.Count == 0)
        {
            MessageBox.Show(this, Lang.T("common.check_one_link"), Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        Queue = QueueBox.SelectedItem as DownloadQueue;
        StartQueue = StartQueueBox.IsChecked == true;
        DialogResult = true;
    }
}
