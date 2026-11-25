using System.Collections.ObjectModel;
using System.Windows;
using ADM.Core;

namespace ADM.App;

/// <summary>The grabber's named file masks: the user's to change, add to and delete, and to put back as ADM gives them.</summary>
public partial class GrabberFiltersWindow : ThemedWindow
{
    readonly List<GrabberFilter> _saved;
    readonly ObservableCollection<GrabberFilter> _editing;

    /// <param name="filters">Changed in place when the user says OK.</param>
    public GrabberFiltersWindow(List<GrabberFilter> filters)
    {
        InitializeComponent();
        Icon = AppIcon.ImageSource;
        _saved = filters;
        _editing = new(filters.Select(f => new GrabberFilter { Name = f.Name, Mask = f.Mask }));
        FilterGrid.ItemsSource = _editing;
    }

    void Add_Click(object sender, RoutedEventArgs e)
    {
        var filter = new GrabberFilter { Name = Lang.T("grabberfilters.new_name"), Mask = "*.*" };
        _editing.Add(filter);
        FilterGrid.SelectedItem = filter;
        FilterGrid.ScrollIntoView(filter);
    }

    void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (FilterGrid.SelectedItem is GrabberFilter filter) _editing.Remove(filter);
    }

    void Defaults_Click(object sender, RoutedEventArgs e)
    {
        _editing.Clear();
        foreach (var filter in GrabberFilter.Defaults()) _editing.Add(filter);
    }

    void Ok_Click(object sender, RoutedEventArgs e)
    {
        FilterGrid.CommitEdit();
        FilterGrid.CommitEdit();
        foreach (var filter in _editing)
        {
            if (filter.Name.Trim().Length == 0 || GrabberMask.Parse(filter.Mask).IsEmpty)
            {
                ProblemText.Text = Lang.T("grabberfilters.invalid");
                FilterGrid.SelectedItem = filter;
                return;
            }
        }
        _saved.Clear();
        _saved.AddRange(_editing.Select(f => new GrabberFilter { Name = f.Name.Trim(), Mask = f.Mask.Trim() }));
        DialogResult = true;
    }
}
