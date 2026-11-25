using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using ADM.Core;

namespace ADM.App;

/// <summary>Links dropped on the window or pasted with Ctrl+V, and finished files dragged out of the list.</summary>
public partial class MainWindow
{
    // What a browser's link carries besides text. Firefox's holds the address, then the link's text, on the next line.
    const string UrlFormat = "UniformResourceLocatorW";
    const string MozUrlFormat = "text/x-moz-url";

    /// <summary>What a drop or the clipboard held: files, or else text; neither when it held something else.</summary>
    sealed record Dropped(string[]? Files, string? Text)
    {
        public static readonly Dropped Nothing = new(null, null);
    }

    bool _draggingOut;
    // DragOver comes with every move of the mouse; what the drag holds is read once.
    IDataObject? _dragData;
    bool _dragHoldsLinks;
    // A press on a finished file in the list, which a drag takes out as a file.
    Point? _pressedAt;
    bool _keepSelection;

    // ---- Dropping and pasting

    void Window_DragOver(object sender, DragEventArgs e)
    {
        var effect = _draggingOut || !HoldsLinks(e.Data) ? DragDropEffects.None
            : e.AllowedEffects.HasFlag(DragDropEffects.Copy) ? DragDropEffects.Copy
            : e.AllowedEffects.HasFlag(DragDropEffects.Link) ? DragDropEffects.Link
            : DragDropEffects.None;
        e.Effects = effect;
        DropOverlay.Visibility = effect == DragDropEffects.None ? Visibility.Collapsed : Visibility.Visible;
        e.Handled = true;
    }

    // Moving from one part of the window to another leaves one and enters the next, which shows it again at once.
    void Window_DragLeave(object sender, DragEventArgs e) => DropOverlay.Visibility = Visibility.Collapsed;

    // A text box that takes the drop itself handles it before the window hears of it.
    void Window_PreviewDrop(object sender, DragEventArgs e) => DropOverlay.Visibility = Visibility.Collapsed;

    void Window_Drop(object sender, DragEventArgs e)
    {
        _dragData = null;
        if (_draggingOut) return;
        e.Handled = true;
        // Read now: the data can go once the drop returns. A dialog shown inside it would hold up the browser's drag.
        var dropped = Read(e.Data);
        Dispatcher.BeginInvoke(() =>
        {
            ShowAndActivate();
            if (!AddDropped(dropped, Lang.T("main.links_dropped")))
                MessageBox.Show(this, Lang.T("main.no_links_dropped"), "ADM", MessageBoxButton.OK, MessageBoxImage.Information);
        });
    }

    void Paste_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        Dropped pasted;
        try
        {
            pasted = Clipboard.GetDataObject() is { } data ? Read(data) : Dropped.Nothing;
        }
        catch (ExternalException)
        {
            // Another program holding the clipboard.
            pasted = Dropped.Nothing;
        }
        if (!AddDropped(pasted, Lang.T("main.batch_from_clipboard")))
            MessageBox.Show(this, Lang.T("main.no_links_in_clipboard"), "ADM", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    bool HoldsLinks(IDataObject data)
    {
        if (ReferenceEquals(data, _dragData)) return _dragHoldsLinks;
        _dragData = data;
        var dropped = Read(data);
        return _dragHoldsLinks = dropped.Files != null
            ? dropped.Files.Any(f => DroppedLinks.KindOf(f) != DroppedFileKind.Other)
            : dropped.Text != null && DroppedLinks.FromText(dropped.Text).Count > 0;
    }

    static Dropped Read(IDataObject data)
    {
        try
        {
            if (data.GetDataPresent(DataFormats.FileDrop))
                return data.GetData(DataFormats.FileDrop) is string[] files ? new(files, null) : Dropped.Nothing;
            foreach (var format in new[] { DataFormats.UnicodeText, DataFormats.Text })
                if (data.GetDataPresent(format) && data.GetData(format) is string text)
                    return new(null, text);
            if (data.GetDataPresent(UrlFormat) && data.GetData(UrlFormat) is MemoryStream url)
                return new(null, Encoding.Unicode.GetString(url.ToArray()));
            if (data.GetDataPresent(MozUrlFormat) && data.GetData(MozUrlFormat) is MemoryStream moz)
                return new(null, Encoding.Unicode.GetString(moz.ToArray()).Split('\n')[0]);
        }
        catch (ExternalException)
        {
            // A program that offered data it then couldn't give.
        }
        return Dropped.Nothing;
    }

    /// <returns>False when it held nothing that could hold links.</returns>
    bool AddDropped(Dropped dropped, string title) =>
        dropped.Files != null ? AddFromFiles(dropped.Files) : dropped.Text != null && OfferLinks(DroppedLinks.FromText(dropped.Text), title);

    /// <summary>Export files are imported as the Tasks menu imports them; the links in the others are offered together.</summary>
    /// <returns>False when none of the files can hold links.</returns>
    bool AddFromFiles(IReadOnlyList<string> files)
    {
        var exports = files.Where(f => DroppedLinks.KindOf(f) == DroppedFileKind.ExportFile).ToList();
        var others = files.Where(f => DroppedLinks.KindOf(f) is DroppedFileKind.Links or DroppedFileKind.Shortcut).ToList();
        if (exports.Count == 0 && others.Count == 0) return false;
        foreach (var file in exports) ImportExportFile(file);
        if (others.Count == 0) return true;

        var links = new List<string>();
        var unread = false;
        foreach (var file in others)
        {
            try
            {
                links.AddRange(DroppedLinks.FromFile(file, File.ReadAllText(file)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                unread = true;
                MessageBox.Show(this, Lang.T("main.could_not_read", ex.Message), "ADM", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        var title = others.Count == 1 ? Lang.T("main.import_from", Path.GetFileName(others[0])) : Lang.T("main.links_dropped");
        if (!OfferLinks(links.Distinct().ToList(), title) && !unread)
            MessageBox.Show(this, Lang.T("main.no_links_in_file"), "ADM", MessageBoxButton.OK, MessageBoxImage.Information);
        return true;
    }

    /// <summary>One link, or a cURL command, opens the Add dialog with it; several open a batch.</summary>
    /// <returns>False when there are none.</returns>
    bool OfferLinks(IReadOnlyList<string> links, string title)
    {
        if (links.Count == 0) return false;
        if (links.Count == 1) ShowAddDialog(new DownloadRequest { Url = links[0] });
        else ShowBatchDialog(links, title);
        return true;
    }

    // ---- Dragging finished files out of the list, as from Explorer

    static bool CanDragOut(DownloadItem item) => item.Status == DownloadStatus.Completed && File.Exists(item.FullPath);

    void Downloads_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _pressedAt = null;
        _keepSelection = false;
        // A press elsewhere, or on an unfinished download, keeps the list's own dragging, which selects rows.
        if (e.ClickCount != 1 || RowAt(e.OriginalSource as DependencyObject) is not { Item: DownloadItem item } row || !CanDragOut(item)) return;
        _pressedAt = e.GetPosition(Downloads);
        // Pressing on one of several selected rows keeps them all selected, so they go together; letting go
        // without dragging picks that one alone, when the button comes up.
        if (row.IsSelected && Downloads.SelectedItems.Count > 1 && Keyboard.Modifiers == ModifierKeys.None)
        {
            _keepSelection = true;
            e.Handled = true;
        }
    }

    void Downloads_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_pressedAt is not Point from || e.LeftButton != MouseButtonState.Pressed) return;
        var moved = e.GetPosition(Downloads) - from;
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _pressedAt = null;
        _keepSelection = false;
        var files = Selected.Where(CanDragOut).Select(i => i.FullPath).ToArray();
        if (files.Length == 0) return;
        // The list stops selecting by dragging, which this drag takes the place of.
        Mouse.Capture(null);
        _draggingOut = true;
        try
        {
            // Not moved: the file stays where its download says it is.
            DragDrop.DoDragDrop(Downloads, new DataObject(DataFormats.FileDrop, files), DragDropEffects.Copy | DragDropEffects.Link);
        }
        finally
        {
            _draggingOut = false;
        }
    }

    void Downloads_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_keepSelection && RowAt(e.OriginalSource as DependencyObject) is { Item: DownloadItem item })
            Downloads.SelectedItem = item;
        _keepSelection = false;
        _pressedAt = null;
    }

    static DataGridRow? RowAt(DependencyObject? element)
    {
        // A run of text is no visual, and goes up by the logical tree.
        while (element != null && element is not DataGridRow)
            element = element is Visual or Visual3D ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        return element as DataGridRow;
    }
}
