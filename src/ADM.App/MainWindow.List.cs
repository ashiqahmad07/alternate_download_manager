using System.Collections;
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using ADM.Core;

namespace ADM.App;

/// <summary>The download list: its columns and sorting, the Find bar, the toolbar, deleting, and double-click.</summary>
public partial class MainWindow
{
    // Sorting by these goes stale as transfers run, so the list is checked at most once a second, and sorted again
    // only when something has changed places.
    static readonly HashSet<DownloadSortKey> LiveKeys =
        new() { DownloadSortKey.Progress, DownloadSortKey.TransferRate, DownloadSortKey.TimeLeft, DownloadSortKey.Status };

    static readonly (DownloadSortKey Key, string Label)[] ArrangeKeys =
    {
        (DownloadSortKey.OrderAdded, Lang.T("arrange.order_added")), (DownloadSortKey.FileName, Lang.T("arrange.file_name")), (DownloadSortKey.Size, Lang.T("arrange.size")),
        (DownloadSortKey.Status, Lang.T("arrange.status")), (DownloadSortKey.TimeLeft, Lang.T("arrange.time_left")), (DownloadSortKey.TransferRate, Lang.T("arrange.transfer_rate")),
        (DownloadSortKey.LastTry, Lang.T("arrange.last_try")), (DownloadSortKey.Description, Lang.T("arrange.description")), (DownloadSortKey.SavePath, Lang.T("arrange.save_path")),
        (DownloadSortKey.Referer, Lang.T("arrange.referer")), (DownloadSortKey.ParentPage, Lang.T("arrange.parent_page")),
    };

    Dictionary<string, DataGridColumn> _columns = null!;
    bool _applyingColumns;
    DateTime _lastResort;
    int _lastActiveCount;

    Settings Prefs => _manager.State.Settings;

    void InitList()
    {
        _columns = new()
        {
            ["Q"] = ColQ, ["FileName"] = ColFileName, ["Size"] = ColSize, ["Progress"] = ColProgress, ["TransferRate"] = ColTransferRate,
            ["TimeLeft"] = ColTimeLeft, ["Status"] = ColStatus, ["Resume"] = ColResume, ["LastTry"] = ColLastTry, ["Description"] = ColDescription,
            ["Category"] = ColCategory, ["DateAdded"] = ColDateAdded, ["SaveTo"] = ColSaveTo, ["Referer"] = ColReferer, ["ParentPage"] = ColParentPage,
        };
        ApplyColumns();
        FillColumnResize.Enable(Downloads);
        var width = DependencyPropertyDescriptor.FromProperty(DataGridColumn.WidthProperty, typeof(DataGridColumn));
        foreach (var column in _columns.Values) width.AddValueChanged(column, (_, _) => SaveColumns());
        BuildArrangeMenu();
        BuildToolbar();
        // The labels' widths go by the font's size.
        Theme.Changed += BuildToolbar;
        ApplyCategories();
    }

    // ---- Columns

    /// <summary>Shows the saved layout, brought up to date; widths and order the user changes are saved as they change.</summary>
    void ApplyColumns()
    {
        var layout = ListColumns.Normalize(Prefs.Columns);
        Prefs.Columns = layout;
        _applyingColumns = true;
        try
        {
            for (int i = 0; i < layout.Count; i++)
            {
                var column = _columns[layout[i].Id];
                column.DisplayIndex = i;
                column.Visibility = layout[i].Visible ? Visibility.Visible : Visibility.Collapsed;
                column.Width = layout[i].Width == ListColumns.Fill ? new DataGridLength(1, DataGridLengthUnitType.Star) : new DataGridLength(layout[i].Width);
            }
        }
        finally
        {
            _applyingColumns = false;
        }
        ApplySort();
    }

    void SaveColumns()
    {
        if (_applyingColumns) return;
        var layout = _columns.OrderBy(c => c.Value.DisplayIndex).Select(c => new ListColumn
        {
            Id = c.Key,
            Visible = c.Value.Visibility == Visibility.Visible,
            // The file name taking the spare room keeps doing so: the grid changes its width as the window resizes.
            Width = c.Value.Width.IsStar ? ListColumns.Fill : Math.Round(c.Value.Width.IsAbsolute ? c.Value.Width.Value : c.Value.ActualWidth),
        }).ToList();
        if (Prefs.Columns != null && layout.Select(c => (c.Id, c.Visible, c.Width)).SequenceEqual(Prefs.Columns.Select(c => (c.Id, c.Visible, c.Width)))) return;
        Prefs.Columns = layout;
        _manager.MarkDirty();
    }

    void Downloads_ColumnReordered(object? sender, DataGridColumnEventArgs e) => SaveColumns();

    void HeaderMenu_Opened(object sender, RoutedEventArgs e)
    {
        var menu = (ContextMenu)sender;
        menu.Items.Clear();
        foreach (var (id, column) in _columns.OrderBy(c => c.Value.DisplayIndex))
        {
            // The file name always shows.
            var item = new MenuItem { Header = ColumnsWindow.Label(id), IsCheckable = true, IsChecked = column.Visibility == Visibility.Visible, IsEnabled = id != "FileName" };
            item.Click += (_, _) =>
            {
                column.Visibility = item.IsChecked ? Visibility.Visible : Visibility.Collapsed;
                SaveColumns();
            };
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var more = new MenuItem { Header = Lang.T("main.columns_menu") };
        more.Click += Columns_Click;
        menu.Items.Add(more);
    }

    void Columns_Click(object sender, RoutedEventArgs e)
    {
        SaveColumns();
        var dialog = new ColumnsWindow(Prefs.Columns) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        Prefs.Columns = dialog.Result;
        ApplyColumns();
        _manager.MarkDirty();
    }

    // ---- Sorting

    void Downloads_Sorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        SortBy(ListColumns.SortKeyFor(_columns.First(c => c.Value == e.Column).Key));
    }

    /// <summary>Sorts by <paramref name="key"/>; choosing the current key again turns the order round.</summary>
    void SortBy(DownloadSortKey key)
    {
        if (Prefs.SortKey == key) Prefs.SortDescending = !Prefs.SortDescending;
        else
        {
            Prefs.SortKey = key;
            Prefs.SortDescending = false;
        }
        ApplySort();
        _manager.MarkDirty();
    }

    void ApplySort()
    {
        var selected = Downloads.SelectedItems.Cast<object>().ToList();
        ((ListCollectionView)_view).CustomSort = (IComparer)DownloadSort.Comparer(Prefs.SortKey, Prefs.SortDescending);
        foreach (var (id, column) in _columns)
            column.SortDirection = ListColumns.SortKeyFor(id) == Prefs.SortKey
                ? Prefs.SortDescending ? ListSortDirection.Descending : ListSortDirection.Ascending
                : null;
        Reselect(selected);
    }

    /// <param name="always">After the window was hidden: whatever ran meanwhile may have moved things.</param>
    void ResortIfLive(DateTime now, bool always = false)
    {
        var active = _manager.ActiveCount;
        // One more pass after the last transfer stops, for its final status.
        var stopped = active == 0 && _lastActiveCount == 0;
        _lastActiveCount = active;
        if (!LiveKeys.Contains(Prefs.SortKey) || (!always && (stopped || now - _lastResort < TimeSpan.FromSeconds(1)))) return;
        _lastResort = now;
        // Sorting again rebuilds every row shown, and most seconds nothing has changed places.
        if (DownloadSort.InOrder(_view.Cast<DownloadItem>(), DownloadSort.Comparer(Prefs.SortKey, Prefs.SortDescending))) return;
        var selected = Downloads.SelectedItems.Cast<object>().ToList();
        _view.Refresh();
        Reselect(selected);
    }

    void Reselect(List<object> items)
    {
        if (items.Count == 0 || Downloads.SelectedItems.Count == items.Count) return;
        Downloads.SelectedItems.Clear();
        foreach (var item in items)
            if (_view.Contains(item)) Downloads.SelectedItems.Add(item);
    }

    void BuildArrangeMenu()
    {
        ArrangeMenu.Items.Clear();
        foreach (var (key, label) in ArrangeKeys)
        {
            var item = new MenuItem { Header = label, IsCheckable = true, Tag = key };
            item.Click += (_, _) => SortBy(key);
            ArrangeMenu.Items.Add(item);
        }
    }

    void ViewMenu_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        CategoriesItem.Header = Prefs.ShowCategories ? Lang.T("main.hide_categories") : Lang.T("main.show_categories");
        foreach (MenuItem item in ArrangeMenu.Items) item.IsChecked = (DownloadSortKey)item.Tag == Prefs.SortKey;
        LargeButtonsItem.IsChecked = Prefs.LargeToolbarButtons;
        SmallButtonsItem.IsChecked = !Prefs.LargeToolbarButtons;
        ThemeFollowItem.IsChecked = Prefs.Theme == ThemeChoice.FollowWindows;
        ThemeLightItem.IsChecked = Prefs.Theme == ThemeChoice.Light;
        ThemeDarkItem.IsChecked = Prefs.Theme == ThemeChoice.Dark;
    }

    // ---- Dark mode and font

    void ThemeFollow_Click(object sender, RoutedEventArgs e) => SetTheme(ThemeChoice.FollowWindows);

    void ThemeLight_Click(object sender, RoutedEventArgs e) => SetTheme(ThemeChoice.Light);

    void ThemeDark_Click(object sender, RoutedEventArgs e) => SetTheme(ThemeChoice.Dark);

    void SetTheme(ThemeChoice choice)
    {
        Prefs.Theme = choice;
        Theme.Apply();
        _manager.MarkDirty();
    }

    void ChooseFont_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new FontWindow(Appearance.FontFamilyOf(Prefs.FontFamily), Appearance.FontSizeOf(Prefs.FontSize)) { Owner = this };
        if (dialog.ShowDialog() == true) SetFont(dialog.ChosenFamily, dialog.ChosenSize);
    }

    void ResetFont_Click(object sender, RoutedEventArgs e) => SetFont(Appearance.DefaultFontFamily, Appearance.DefaultFontSize);

    void SetFont(string family, double size)
    {
        Prefs.FontFamily = family;
        Prefs.FontSize = size;
        Theme.Apply();
        _manager.MarkDirty();
    }

    // ---- Language

    // Built each time it opens, so a file just put in the folder is offered. A menu with no items never opens, so it starts built too.
    void LanguageMenu_Opened(object sender, RoutedEventArgs e) => BuildLanguageMenu();

    void BuildLanguageMenu()
    {
        LanguageMenu.Items.Clear();
        foreach (var language in Lang.Available())
        {
            var item = new MenuItem
            {
                // A menu takes an underscore for its access key.
                Header = language.Name.Replace("_", "__"),
                IsCheckable = true,
                IsChecked = string.Equals(language.Code, Prefs.Language, StringComparison.OrdinalIgnoreCase),
            };
            item.Click += (_, _) => ChooseLanguage(language);
            LanguageMenu.Items.Add(item);
        }
    }

    async void ChooseLanguage(LanguageFile language)
    {
        Prefs.Language = language.Code;
        _manager.MarkDirty();
        if (string.Equals(language.Code, Lang.Code, StringComparison.OrdinalIgnoreCase)) return;
        if (MessageBox.Show(this, Lang.T("main.language_restart", language.Name), "ADM", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes
            && ConfirmStopping(_manager.Items, Lang.T("main.restart_anyway")))
            await ((App)Application.Current).ExitAsync(restart: true);
    }

    // ---- Categories pane

    void ToggleCategories_Click(object sender, RoutedEventArgs e)
    {
        Prefs.ShowCategories = !Prefs.ShowCategories;
        ApplyCategories();
        _manager.MarkDirty();
    }

    void ApplyCategories()
    {
        var show = Prefs.ShowCategories;
        // A hidden pane must not leave a hidden filter on the list.
        if (!show) FilterList.SelectedIndex = 0;
        FilterList.Visibility = SidebarSplitter.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        SidebarColumn.MinWidth = show ? 120 : 0;
        SidebarColumn.Width = new GridLength(show ? 190 : 0);
        SplitterColumn.Width = new GridLength(show ? 4 : 0);
    }

    // ---- Toolbar

    // A large button's label, in widths of the font's size: "Start queue" fits on one line, with room for a wider
    // font, and "Delete completed" takes two.
    const double LargeLabelEms = 5.6;

    // The toolbar's buttons by id, to grey out those that can't act.
    readonly Dictionary<string, Button> _toolbarButtons = new();

    void BuildToolbar()
    {
        Prefs.Toolbar = ToolbarButtons.Normalize(Prefs.Toolbar);
        ToolbarPanel.Children.Clear();
        _toolbarButtons.Clear();
        foreach (var id in Prefs.Toolbar)
        {
            // The buttons are spaced evenly: a separator in the saved layout takes no room.
            if (id == ToolbarButtons.Separator) continue;
            var (glyph, label) = ToolbarWindow.Look[id];
            var glyphText = new TextBlock { Text = glyph, Style = (Style)FindResource("ToolGlyph") };
            var labelText = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
            var content = new StackPanel();
            if (!Prefs.LargeToolbarButtons)
            {
                content.Orientation = Orientation.Horizontal;
                glyphText.FontSize = 14;
                glyphText.Margin = new Thickness(0, 0, 6, 0);
            }
            else
            {
                // A name longer than the others goes onto a second line, rather than one button standing twice as
                // wide as the rest; IDM cut it short ("Delete Co..."), which looked like a fault. Past two lines it
                // is cut short, and the tip has it whole.
                var size = (double)FindResource("AppFontSize");
                labelText.MaxWidth = LargeLabelEms * size;
                labelText.MaxHeight = 2.8 * size;
                labelText.TextWrapping = TextWrapping.Wrap;
                labelText.TextTrimming = TextTrimming.CharacterEllipsis;
                labelText.TextAlignment = TextAlignment.Center;
                labelText.HorizontalAlignment = HorizontalAlignment.Center;
                labelText.VerticalAlignment = VerticalAlignment.Top;
                content.VerticalAlignment = VerticalAlignment.Top;
            }
            content.Children.Add(glyphText);
            content.Children.Add(labelText);
            var button = new Button { Style = (Style)FindResource("ToolButton"), Content = content, ToolTip = label };
            AutomationProperties.SetName(button, label);
            AutomationProperties.SetAutomationId(button, "Toolbar" + id);
            button.Click += ToolbarHandler(id);
            ToolbarPanel.Children.Add(button);
            _toolbarButtons[id] = button;
        }
        UpdateToolbar();
    }

    /// <summary>
    /// Greys out the buttons that can't act on the selection or on the list as it is: Resume with nothing to resume
    /// selected, Pause with nothing running selected, and so on. On each change of the selection, and each tick
    /// the window shows.
    /// </summary>
    void UpdateToolbar()
    {
        var selected = Downloads.SelectedItems.Cast<DownloadItem>().ToList();
        SetEnabled("Resume", selected.Any(i => i.Status != DownloadStatus.Completed && !_manager.IsRunning(i) && !_manager.IsMoving(i)));
        SetEnabled("Pause", selected.Any(_manager.IsRunning));
        SetEnabled("Delete", selected.Any(i => !_manager.IsMoving(i)));
        SetEnabled("PauseAll", _manager.ActiveCount > 0 || _manager.Queues.Any(q => q.IsRunning));
        SetEnabled("DeleteCompleted", Listed.Any(i => i.Status == DownloadStatus.Completed));
        SetEnabled("StartQueue", !_manager.MainQueue.IsRunning);
        SetEnabled("StopQueue", _manager.MainQueue.IsRunning);
    }

    void SetEnabled(string id, bool enabled)
    {
        if (_toolbarButtons.TryGetValue(id, out var button)) button.IsEnabled = enabled;
    }

    RoutedEventHandler ToolbarHandler(string id) => id switch
    {
        "AddUrl" => AddUrl_Click,
        "Resume" => Resume_Click,
        "Pause" => Pause_Click,
        "PauseAll" => PauseAll_Click,
        "Delete" => Delete_Click,
        "DeleteCompleted" => DeleteCompleted_Click,
        "StartQueue" => StartQueue_Click,
        "StopQueue" => StopQueue_Click,
        "Scheduler" => Scheduler_Click,
        "Grabber" => NewGrabber_Click,
        "Find" => Find_Click,
        _ => Options_Click,
    };

    void ToolbarMenu_Opened(object sender, RoutedEventArgs e)
    {
        ToolbarLargeItem.IsChecked = Prefs.LargeToolbarButtons;
        ToolbarSmallItem.IsChecked = !Prefs.LargeToolbarButtons;
    }

    void LargeButtons_Click(object sender, RoutedEventArgs e) => SetLargeButtons(true);

    void SmallButtons_Click(object sender, RoutedEventArgs e) => SetLargeButtons(false);

    void SetLargeButtons(bool large)
    {
        Prefs.LargeToolbarButtons = large;
        BuildToolbar();
        _manager.MarkDirty();
    }

    void CustomizeToolbar_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ToolbarWindow(Prefs.Toolbar) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        Prefs.Toolbar = dialog.Result;
        BuildToolbar();
        _manager.MarkDirty();
    }

    // ---- The filter box

    void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var typed = SearchBox.Text.Length > 0;
        SearchHint.Visibility = typed ? Visibility.Collapsed : Visibility.Visible;
        SearchClear.Visibility = typed ? Visibility.Visible : Visibility.Collapsed;
        ApplyListFilter();
    }

    void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            // Esc empties the box; in an empty box it goes back to the list.
            if (SearchBox.Text.Length > 0) SearchBox.Clear();
            else Downloads.Focus();
        }
        else if (e.Key is Key.Enter or Key.Down && Downloads.Items.Count > 0)
        {
            Downloads.SelectedIndex = Math.Max(0, Downloads.SelectedIndex);
            Downloads.Focus();
        }
        else return;
        e.Handled = true;
    }

    void SearchClear_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Clear();
        SearchBox.Focus();
    }

    void Filter_Executed(object sender, RoutedEventArgs e)
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    // ---- Find bar

    void Find_Click(object sender, RoutedEventArgs e)
    {
        FindBar.Visibility = Visibility.Visible;
        FindBox.Focus();
        FindBox.SelectAll();
    }

    void FindNext_Click(object sender, RoutedEventArgs e)
    {
        if (FindBar.Visibility != Visibility.Visible || FindBox.Text.Length == 0) Find_Click(sender, e);
        else FindNext();
    }

    void CloseFind_Click(object sender, RoutedEventArgs e)
    {
        FindBar.Visibility = Visibility.Collapsed;
        FindMessage.Text = "";
        Downloads.Focus();
    }

    void FindBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) FindNext();
        else if (e.Key == Key.Escape) CloseFind_Click(sender, e);
        else return;
        e.Handled = true;
    }

    void FindSettings_Click(object sender, RoutedEventArgs e)
    {
        if (new FindSettingsWindow(Prefs) { Owner = this }.ShowDialog() == true) _manager.MarkDirty();
    }

    /// <summary>Selects the next match below the selection, in the list's order, going round to the top.</summary>
    void FindNext()
    {
        var query = new SearchQuery(FindBox.Text, Prefs.SearchIn, Prefs.SearchMatchCase, Prefs.SearchWholeString);
        var from = Downloads.SelectedItem as DownloadItem;
        var (found, wrapped) = DownloadSearch.Next(_view.Cast<DownloadItem>().ToList(), from, query);
        if (found == null && (FilterList.SelectedIndex != 0 || NameFilter.Length > 0))
        {
            // The sidebar's view or the filter box hides every match: search all downloads, and show them all.
            var all = Listed.Order(DownloadSort.Comparer(Prefs.SortKey, Prefs.SortDescending)).ToList();
            (found, wrapped) = DownloadSearch.Next(all, from, query);
            if (found != null)
            {
                FilterList.SelectedIndex = 0;
                SearchBox.Clear();
            }
        }
        if (found == null)
        {
            FindMessage.Text = Lang.T("main.find_no_match");
            return;
        }
        FindMessage.Text = wrapped ? Lang.T("main.find_wrapped") : "";
        Downloads.SelectedItem = found;
        Downloads.ScrollIntoView(found);
    }

    // ---- Deleting

    /// <summary>
    /// Takes the selected downloads off the list, asking first unless turned off. Files on disk go only when
    /// the user ticks the box, which starts unticked every time; an unfinished download's partial data goes.
    /// </summary>
    async void DeleteSelected()
    {
        // A download being moved has its files in transit; it can go once the move is done.
        var items = Selected.Where(i => !_manager.IsMoving(i)).ToList();
        if (items.Count == 0) return;
        var finished = items.Count(i => i.Status == DownloadStatus.Completed);
        var unfinished = items.Count - finished;
        var askFinished = finished > 0 && Prefs.ConfirmDeleteCompleted;
        var askUnfinished = unfinished > 0 && Prefs.ConfirmDeleteUnfinished;
        var deleteFiles = false;
        if (askFinished || askUnfinished)
        {
            var dialog = new DeleteConfirmWindow(askFinished ? finished : 0, askUnfinished ? unfinished : 0);
            PlaceDialog(dialog);
            if (dialog.ShowDialog() != true) return;
            deleteFiles = dialog.DeleteFiles;
            if (dialog.DontAskFinished) Prefs.ConfirmDeleteCompleted = false;
            if (dialog.DontAskUnfinished) Prefs.ConfirmDeleteUnfinished = false;
            _manager.MarkDirty();
        }
        foreach (var item in items)
        {
            await _manager.RemoveAsync(item, deleteFiles && item.Status == DownloadStatus.Completed);
            CloseProgress(item);
        }
    }

    // ---- Double-click and the right-click menu

    void Downloads_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (RowOf(e.OriginalSource)?.Item is not DownloadItem item) return;
        if (Prefs.OnDoubleClick == DoubleClickAction.Properties) ShowProperties(item);
        else if (item.Status == DownloadStatus.Completed) OpenFile(item);
        else if (_manager.IsRunning(item)) ShowProgress(item, always: true);
        else
            MessageBox.Show(this, Lang.T("main.not_complete_cant_open"),
                item.ToString(), MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>The row a mouse event happened in; null on a header or empty space.</summary>
    static DataGridRow? RowOf(object source)
    {
        for (var node = source as DependencyObject; node != null; node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
            if (node is DataGridRow row) return row;
        return null;
    }

    void RowMenu_Opened(object sender, RoutedEventArgs e)
    {
        DoubleClickOpenItem.IsChecked = Prefs.OnDoubleClick == DoubleClickAction.Open;
        DoubleClickPropertiesItem.IsChecked = Prefs.OnDoubleClick == DoubleClickAction.Properties;
        RowPreviewZipItem.Visibility = SelectedZip != null ? Visibility.Visible : Visibility.Collapsed;
    }

    void DoubleClickOpen_Click(object sender, RoutedEventArgs e) => SetDoubleClick(DoubleClickAction.Open);

    void DoubleClickProperties_Click(object sender, RoutedEventArgs e) => SetDoubleClick(DoubleClickAction.Properties);

    void SetDoubleClick(DoubleClickAction action)
    {
        Prefs.OnDoubleClick = action;
        _manager.MarkDirty();
    }

    void OpenWith_Click(object sender, RoutedEventArgs e)
    {
        if (Downloads.SelectedItem is DownloadItem { Status: DownloadStatus.Completed } item && System.IO.File.Exists(item.FullPath))
            OpenWith.Show(this, item.FullPath);
    }

    // ---- Speed limiter

    void SpeedLimiterMenu_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        LimiterOnItem.IsChecked = Prefs.SpeedLimiterOn;
        LimiterOffItem.IsChecked = !Prefs.SpeedLimiterOn;
    }

    void LimiterOn_Click(object sender, RoutedEventArgs e) => SetLimiter(true);

    void LimiterOff_Click(object sender, RoutedEventArgs e) => SetLimiter(false);

    void SetLimiter(bool on)
    {
        Prefs.SpeedLimiterOn = on;
        _manager.ApplySettings();
        ShowStatus(!on ? Lang.T("main.limiter_off")
            : Prefs.SpeedLimitKBps > 0 ? Lang.T("main.limiter_on", Format.Speed(Prefs.SpeedLimitKBps * 1024))
            : Lang.T("main.limiter_on_no_limit"));
    }

    void LimiterSettings_Click(object sender, RoutedEventArgs e) => ShowOptions("Connection");
}
