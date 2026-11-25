using System.Collections.ObjectModel;
using System.Windows;
using ADM.Core;

namespace ADM.App;

/// <summary>Chooses the toolbar's buttons and their order. Separators can be added as often as wanted.</summary>
public partial class ToolbarWindow : ThemedWindow
{
    /// <summary>Each button's Segoe MDL2 glyph and label.</summary>
    public static readonly IReadOnlyDictionary<string, (string Glyph, string Label)> Look = new Dictionary<string, (string, string)>
    {
        ["AddUrl"] = ("", Lang.T("toolbar.button_add_url")),
        ["Resume"] = ("", Lang.T("toolbar.button_resume")),
        ["Pause"] = ("", Lang.T("toolbar.button_pause")),
        ["PauseAll"] = ("", Lang.T("toolbar.button_pause_all")),
        ["Delete"] = ("", Lang.T("toolbar.button_delete")),
        ["DeleteCompleted"] = ("", Lang.T("toolbar.button_delete_completed")),
        ["StartQueue"] = ("", Lang.T("toolbar.button_start_queue")),
        ["StopQueue"] = ("", Lang.T("toolbar.button_stop_queue")),
        ["Scheduler"] = ("", Lang.T("toolbar.button_scheduler")),
        ["Grabber"] = ("", Lang.T("toolbar.button_grabber")),
        ["Find"] = ("", Lang.T("toolbar.button_find")),
        ["Options"] = ("", Lang.T("toolbar.button_options")),
    };

    // A class, not a record: two separators are two entries, and the lists select by reference.
    sealed class Entry(string id)
    {
        public string Id { get; } = id;

        // The lists' UI Automation names, which screen readers announce.
        public override string ToString() => Id == ToolbarButtons.Separator ? Lang.T("toolbar.separator") : Look[Id].Label;
    }

    readonly ObservableCollection<Entry> _available = new();
    readonly ObservableCollection<Entry> _shown = new();

    public ToolbarWindow(IEnumerable<string> buttons)
    {
        InitializeComponent();
        Icon = AppIcon.ImageSource;
        AvailableList.ItemsSource = _available;
        ShownList.ItemsSource = _shown;
        Fill(ToolbarButtons.Normalize(buttons));
    }

    public List<string> Result { get; private set; } = new();

    void Fill(IEnumerable<string> shown)
    {
        _shown.Clear();
        foreach (var id in shown) _shown.Add(new Entry(id));
        RefreshAvailable();
    }

    // Buttons not on the toolbar, and a separator, which is always available.
    void RefreshAvailable()
    {
        _available.Clear();
        _available.Add(new Entry(ToolbarButtons.Separator));
        foreach (var id in ToolbarButtons.All.Where(id => !_shown.Any(e => e.Id == id))) _available.Add(new Entry(id));
    }

    void Add_Click(object sender, RoutedEventArgs e)
    {
        if (AvailableList.SelectedItem is not Entry entry) return;
        // A new button goes after the selected one on the toolbar, or at the end.
        var at = ShownList.SelectedIndex >= 0 ? ShownList.SelectedIndex + 1 : _shown.Count;
        var added = new Entry(entry.Id);
        _shown.Insert(at, added);
        ShownList.SelectedItem = added;
        var index = AvailableList.SelectedIndex;
        RefreshAvailable();
        AvailableList.SelectedIndex = Math.Min(index, _available.Count - 1);
    }

    void Remove_Click(object sender, RoutedEventArgs e)
    {
        var index = ShownList.SelectedIndex;
        if (index < 0) return;
        _shown.RemoveAt(index);
        ShownList.SelectedIndex = Math.Min(index, _shown.Count - 1);
        RefreshAvailable();
    }

    void MoveUp_Click(object sender, RoutedEventArgs e) => Move(-1);

    void MoveDown_Click(object sender, RoutedEventArgs e) => Move(1);

    void Move(int by)
    {
        var index = ShownList.SelectedIndex;
        if (index < 0 || index + by < 0 || index + by >= _shown.Count) return;
        _shown.Move(index, index + by);
        ShownList.SelectedIndex = index + by;
    }

    void Reset_Click(object sender, RoutedEventArgs e) => Fill(ToolbarButtons.Defaults());

    void Ok_Click(object sender, RoutedEventArgs e)
    {
        Result = ToolbarButtons.Normalize(_shown.Select(x => x.Id));
        DialogResult = true;
    }
}
