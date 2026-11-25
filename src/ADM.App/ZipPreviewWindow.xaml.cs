using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using ADM.Core;
using Microsoft.Win32;

namespace ADM.App;

/// <summary>
/// Lists a ZIP archive without downloading it, and takes out only the ticked files. Shown without blocking, as the
/// reads can take a while; closing the window stops them.
/// </summary>
public partial class ZipPreviewWindow : ThemedWindow
{
    public sealed class Row : Observable
    {
        bool _checked;
        public required ZipEntryInfo Entry { get; init; }
        public string Name => Entry.Name;
        public string PackedText => Entry.IsFolder ? "" : Format.Bytes(Entry.Packed);
        public string SizeText => Entry.IsFolder ? "" : Format.Bytes(Entry.Size);
        public string ModifiedText => Entry.Modified == DateTime.MinValue ? "" : Entry.Modified.ToString("g");
        public bool Checked { get => _checked; set => Set(ref _checked, value); }

        // Grid rows use this as their UI Automation name, which screen readers announce.
        public override string ToString() => Entry.Name;
    }

    // A report past this many lines wouldn't fit on the screen.
    const int MostProblemsListed = 20;

    readonly DownloadManager _manager;
    readonly DownloadItem _item;
    List<Row> _rows = new();
    CancellationTokenSource? _work;
    bool _settingAll;

    public ZipPreviewWindow(DownloadManager manager, DownloadItem item)
    {
        InitializeComponent();
        Icon = AppIcon.ImageSource;
        _manager = manager;
        _item = item;
        Title = Lang.T("zippreview.title_of", item);
        Loaded += async (_, _) => await ListAsync();
    }

    async Task ListAsync()
    {
        using var work = _work = new CancellationTokenSource();
        MessageText.Text = Lang.T("zippreview.reading");
        try
        {
            var entries = await _manager.ListZipAsync(_item, work.Token);
            _rows = entries.Select(entry => new Row { Entry = entry }).ToList();
            foreach (var row in _rows) row.PropertyChanged += Row_Changed;
            EntryList.ItemsSource = _rows;
            SelectAllBox.IsEnabled = _rows.Count > 0;
            var files = entries.Where(entry => !entry.IsFolder).ToList();
            var unpacked = Format.Bytes(files.Sum(entry => entry.Size));
            MessageText.Text = files.Count == 1 ? Lang.T("zippreview.summary_one", unpacked) : Lang.T("zippreview.summary_many", files.Count, unpacked);
            UpdateCount();
        }
        catch (Exception ex)
        {
            // Cancelled only when the window closes, so nobody is left to tell.
            if (!work.IsCancellationRequested) MessageText.Text = ex.Message;
        }
        finally
        {
            _work = null;
        }
    }

    void Row_Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (!_settingAll) UpdateCount();
    }

    void UpdateCount()
    {
        var chosen = _rows.Count(row => row.Checked);
        CountText.Text = Lang.T("common.selected_count", chosen, _rows.Count);
        ExtractButton.IsEnabled = chosen > 0 && _work == null;
        _settingAll = true;
        SelectAllBox.IsChecked = _rows.Count > 0 && chosen == _rows.Count;
        _settingAll = false;
    }

    void SelectAll_Changed(object sender, RoutedEventArgs e)
    {
        if (_settingAll) return;
        _settingAll = true;
        foreach (var row in _rows) row.Checked = SelectAllBox.IsChecked == true;
        _settingAll = false;
        UpdateCount();
    }

    async void Extract_Click(object sender, RoutedEventArgs e)
    {
        var chosen = _rows.Where(row => row.Checked).Select(row => row.Entry).ToList();
        if (chosen.Count == 0) return;
        var dialog = new OpenFolderDialog { Title = Lang.T("zippreview.extract_to"), InitialDirectory = Directory.Exists(_item.Directory) ? _item.Directory : null };
        if (dialog.ShowDialog(this) != true) return;
        var folder = dialog.FolderName;

        var total = chosen.Where(entry => !entry.IsFolder).Sum(entry => entry.Size);
        var shown = Stopwatch.StartNew();
        var progress = new Progress<long>(done =>
        {
            if (shown.ElapsedMilliseconds < 100) return;
            shown.Restart();
            ExtractProgress.Value = done;
            MessageText.Text = Lang.T("zippreview.extracting_progress", folder, Format.Bytes(done), Format.Bytes(total));
        });

        using var work = _work = new CancellationTokenSource();
        ExtractButton.IsEnabled = false;
        StopButton.Visibility = ExtractProgress.Visibility = Visibility.Visible;
        ExtractProgress.Maximum = Math.Max(total, 1);
        ExtractProgress.Value = 0;
        MessageText.Text = Lang.T("zippreview.extracting", folder);
        try
        {
            Report(await _manager.ExtractZipAsync(_item, chosen, folder, progress, work.Token), folder);
        }
        catch (Exception ex)
        {
            MessageText.Text = work.IsCancellationRequested ? Lang.T("zippreview.stopped") : ex.Message;
        }
        finally
        {
            _work = null;
            StopButton.Visibility = ExtractProgress.Visibility = Visibility.Collapsed;
            UpdateCount();
        }
    }

    void Report(IReadOnlyList<ZipExtractResult> results, string folder)
    {
        var taken = results.Count(result => result.Problem == null);
        MessageText.Text = Lang.T("zippreview.took", taken, results.Count, folder);
        var problems = results.Where(result => result.Problem != null).Select(result => $"• {result.Entry.Name}: {result.Problem}").ToList();
        var findings = results.Where(result => result.ScanResult != null).Select(result => $"• {result.Entry.Name}: {result.ScanResult}").ToList();
        if (problems.Count + findings.Count == 0) return;

        var report = MessageText.Text;
        if (problems.Count > 0) report += "\n\n" + Lang.T("zippreview.left_out", Lines(problems));
        if (findings.Count > 0) report += "\n\n" + Lang.T("zippreview.with_finding", Lines(findings));
        MessageBox.Show(this, report, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    static string Lines(List<string> lines) =>
        string.Join('\n', lines.Take(MostProblemsListed)) + (lines.Count > MostProblemsListed ? "\n" + Lang.T("zippreview.and_more", lines.Count - MostProblemsListed) : "");

    void Stop_Click(object sender, RoutedEventArgs e) => _work?.Cancel();

    // The window is shown without blocking, where IsCancel does nothing, so Close and Esc are handled here.
    void Close_Click(object sender, RoutedEventArgs e) => Close();

    void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _work?.Cancel();
        base.OnClosed(e);
    }
}
