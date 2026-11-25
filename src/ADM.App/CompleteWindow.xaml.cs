using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using ADM.Core;

namespace ADM.App;

/// <summary>
/// Says a download the user started has finished, with ways to open it. The icon can be dragged into a folder
/// or a program, which gets a copy or a link: never a move, so the file stays where ADM saved it.
/// </summary>
public partial class CompleteWindow : ThemedWindow
{
    readonly DownloadManager _manager;
    readonly string _path;
    Point _dragStart;

    public CompleteWindow(DownloadManager manager, DownloadItem item)
    {
        InitializeComponent();
        Icon = AppIcon.ImageSource;
        _manager = manager;
        _path = item.FullPath;
        FileIcon.Source = ShellInfo.For(item.FileName).Icon;
        NameText.Text = item.ToString();
        var size = File.Exists(_path) ? new FileInfo(_path).Length : item.TotalBytes;
        SizeText.Text = Lang.T("complete.downloaded", Format.Bytes(size), size);
        UrlText.Text = item.Url;
        PathText.Text = _path;
        if (item.ScanResult != null)
        {
            ScanText.Text = item.ScanResult;
            ScanText.Visibility = Visibility.Visible;
        }
    }

    void Open_Click(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(_path)) return;
        try
        {
            Process.Start(new ProcessStartInfo(_path) { UseShellExecute = true });
        }
        catch (Win32Exception)
        {
            // No program is set for this kind of file: let the user choose one.
            OpenWith.Show(this, _path);
        }
        Close();
    }

    void OpenWith_Click(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(_path)) return;
        OpenWith.Show(this, _path);
        Close();
    }

    void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (File.Exists(_path)) Process.Start("explorer.exe", $"/select,\"{_path}\"");
        else if (Directory.Exists(Path.GetDirectoryName(_path))) Process.Start("explorer.exe", $"\"{Path.GetDirectoryName(_path)}\"");
        Close();
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();

    void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
    }

    void DragSource_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => _dragStart = e.GetPosition(this);

    void DragSource_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || !File.Exists(_path)) return;
        var moved = e.GetPosition(this) - _dragStart;
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        DragDrop.DoDragDrop(DragSource, new DataObject(DataFormats.FileDrop, new[] { _path }), DragDropEffects.Copy | DragDropEffects.Link);
    }

    protected override void OnClosed(EventArgs e)
    {
        if (DontShowBox.IsChecked == true)
        {
            _manager.State.Settings.ShowCompleteDialog = false;
            _manager.MarkDirty();
        }
        base.OnClosed(e);
    }
}
