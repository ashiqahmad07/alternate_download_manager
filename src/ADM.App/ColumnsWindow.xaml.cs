using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using ADM.Core;

namespace ADM.App;

/// <summary>Chooses the download list's columns, their order and widths. The file name always shows.</summary>
public partial class ColumnsWindow : ThemedWindow
{
    static readonly Dictionary<string, string> Labels = new()
    {
        ["Q"] = Lang.T("column.q"), ["FileName"] = Lang.T("column.file_name"), ["Size"] = Lang.T("column.size"), ["Progress"] = Lang.T("column.progress"), ["TransferRate"] = Lang.T("column.transfer_rate"),
        ["TimeLeft"] = Lang.T("column.time_left"), ["Status"] = Lang.T("column.status"), ["Resume"] = Lang.T("column.resume"), ["LastTry"] = Lang.T("column.last_try"), ["Description"] = Lang.T("column.description"),
        ["Category"] = Lang.T("column.category"), ["DateAdded"] = Lang.T("column.date_added"), ["SaveTo"] = Lang.T("column.save_to"), ["Referer"] = Lang.T("column.referer"), ["ParentPage"] = Lang.T("column.parent_page"),
    };

    public static string Label(string id) => Labels.TryGetValue(id, out var label) ? label : id;

    public sealed class Entry : INotifyPropertyChanged
    {
        bool _visible;

        public Entry(ListColumn column)
        {
            Id = column.Id;
            _visible = column.Visible;
            Width = column.Width;
        }

        public string Id { get; }
        public string Label => ColumnsWindow.Label(Id);
        public bool CanHide => Id != "FileName";
        public double Width { get; set; }

        public bool Visible
        {
            get => _visible;
            set { _visible = value || !CanHide; Raise(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        // The list's UI Automation name, which screen readers announce.
        public override string ToString() => Label;
    }

    readonly ObservableCollection<Entry> _entries;

    public ColumnsWindow(IEnumerable<ListColumn> columns)
    {
        InitializeComponent();
        Icon = AppIcon.ImageSource;
        _entries = new(ListColumns.Normalize(columns).Select(c => new Entry(c)));
        ColumnList.ItemsSource = _entries;
        ColumnList.SelectedIndex = 0;
    }

    public List<ListColumn> Result { get; private set; } = new();

    Entry? Selected => ColumnList.SelectedItem as Entry;

    // The file name with no width takes the room the other columns leave.
    static bool CanFill(Entry entry) => entry.Id == "FileName";

    void ColumnList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        WidthBox.Text = Selected is not { } entry ? "" : entry.Width == ListColumns.Fill && CanFill(entry) ? "" : entry.Width.ToString("0");

    void WidthBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (Selected is not { } entry) return;
        if (double.TryParse(WidthBox.Text, out var width) && width is >= 20 and <= 2000) entry.Width = width;
        else if (WidthBox.Text.Trim().Length == 0 && CanFill(entry)) entry.Width = ListColumns.Fill;
    }

    void MoveUp_Click(object sender, RoutedEventArgs e) => Move(-1);

    void MoveDown_Click(object sender, RoutedEventArgs e) => Move(1);

    void Move(int by)
    {
        var index = ColumnList.SelectedIndex;
        if (index < 0 || index + by < 0 || index + by >= _entries.Count) return;
        _entries.Move(index, index + by);
        ColumnList.SelectedIndex = index + by;
        ColumnList.ScrollIntoView(ColumnList.SelectedItem);
    }

    void Show_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } entry) entry.Visible = true;
    }

    void Hide_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } entry) entry.Visible = false;
    }

    void Reset_Click(object sender, RoutedEventArgs e)
    {
        _entries.Clear();
        foreach (var column in ListColumns.Defaults()) _entries.Add(new Entry(column));
        ColumnList.SelectedIndex = 0;
    }

    void Ok_Click(object sender, RoutedEventArgs e)
    {
        var fills = WidthBox.Text.Trim().Length == 0 && Selected is { } entry && CanFill(entry);
        if (!fills && (!double.TryParse(WidthBox.Text, out var width) || width is < 20 or > 2000))
        {
            MessageBox.Show(this, Lang.T("columns.width_invalid"), Lang.T("columns.title"), MessageBoxButton.OK, MessageBoxImage.Warning);
            WidthBox.Focus();
            return;
        }
        Result = ListColumns.Normalize(_entries.Select(x => new ListColumn { Id = x.Id, Visible = x.Visible, Width = x.Width }));
        DialogResult = true;
    }
}
