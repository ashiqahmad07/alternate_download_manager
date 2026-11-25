using System.IO;
using System.Windows;
using System.Windows.Controls;
using ADM.Core;
using Microsoft.Win32;

namespace ADM.App;

/// <summary>
/// "Download all links" from the browser: lists a page's links, filtered by type, and adds the
/// checked ones saved by category, to one category, or to one folder.
/// </summary>
public partial class LinksWindow : ThemedWindow
{
    public sealed class Row : Observable
    {
        bool _checked = true;
        public required PageLink Link { get; init; }
        public string Name => Link.FileName;
        public string Text => Link.Text;
        public string Url => Link.Url;
        public bool Checked { get => _checked; set => Set(ref _checked, value); }
    }

    readonly PageLinks _page;
    readonly List<Row> _all;
    List<Row> _shown = new();
    bool _settingAll;

    public LinksWindow(DownloadManager manager, PageLinks page)
    {
        InitializeComponent();
        Icon = AppIcon.ImageSource;
        _page = page;
        _all = page.Links.Select(l => new Row { Link = l }).ToList();
        // Follows the rows rather than clicks, so keyboard and screen-reader toggles count too.
        foreach (var row in _all)
            row.PropertyChanged += (_, _) => { if (!_settingAll) UpdateCount(); };
        PageText.Text = page.Referrer is { } referrer ? Lang.T("links.links_on", referrer) : Lang.T("links.from_browser");

        QueueBox.ItemsSource = manager.Queues;
        QueueBox.SelectedItem = manager.MainQueue;
        CategoryBox.ItemsSource = manager.State.Categories;
        CategoryBox.SelectedIndex = 0;
        FolderBox.Text = manager.State.Settings.DownloadRoot;
        ShowRows();
    }

    public List<DownloadRequest> Requests { get; private set; } = new();
    public DownloadQueue? Queue { get; private set; }
    public bool StartQueue { get; private set; }

    void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (IsInitialized) ShowRows();
    }

    void ShowRows()
    {
        var shown = _page.Filter(TypesBox.Text, HideImagesBox.IsChecked == true).ToHashSet();
        _shown = _all.Where(r => shown.Contains(r.Link)).ToList();
        LinkList.ItemsSource = _shown;
        UpdateCount();
    }

    void UpdateCount() => CountText.Text = Lang.T("common.selected_count", _shown.Count(r => r.Checked), _shown.Count);

    void CheckAll_Click(object sender, RoutedEventArgs e) => SetAll(true);

    void UncheckAll_Click(object sender, RoutedEventArgs e) => SetAll(false);

    void SetAll(bool value)
    {
        _settingAll = true;
        foreach (var row in _shown) row.Checked = value;
        _settingAll = false;
        UpdateCount();
    }

    void Category_Opened(object? sender, EventArgs e) => OneCategoryOption.IsChecked = true;

    void Folder_Focused(object sender, RoutedEventArgs e) => OneFolderOption.IsChecked = true;

    void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { InitialDirectory = Directory.Exists(FolderBox.Text) ? FolderBox.Text : null };
        if (dialog.ShowDialog(this) != true) return;
        FolderBox.Text = dialog.FolderName;
        OneFolderOption.IsChecked = true;
    }

    void Add_Click(object sender, RoutedEventArgs e)
    {
        // Only links the filter still shows are added, whatever was checked before filtering.
        var links = _shown.Where(r => r.Checked).Select(r => r.Link).ToList();
        if (links.Count == 0)
        {
            MessageBox.Show(this, Lang.T("common.check_one_link"), Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var folder = FolderBox.Text.Trim();
        if (OneFolderOption.IsChecked == true && !Path.IsPathFullyQualified(folder))
        {
            MessageBox.Show(this, Lang.T("links.folder_full_path"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            FolderBox.Focus();
            return;
        }

        var category = OneCategoryOption.IsChecked == true ? (CategoryBox.SelectedItem as Category)?.Name : null;
        Requests = links.Select(link =>
        {
            var request = _page.ToRequest(link);
            request.Category = category;
            if (OneFolderOption.IsChecked == true) request.Directory = folder;
            return request;
        }).ToList();
        Queue = QueueBox.SelectedItem as DownloadQueue;
        StartQueue = StartQueueBox.IsChecked == true;
        DialogResult = true;
    }
}
