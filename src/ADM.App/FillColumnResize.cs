using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using ADM.Core;

namespace ADM.App;

/// <summary>
/// Sizing the columns of a list whose one star column takes the room the others leave, by
/// <see cref="ListColumns.Drag"/> and <see cref="ListColumns.Resize"/>. WPF's own sizing, while a star column shows,
/// gives a narrowed column's room to the column after it rather than to the star one, squeezes the columns at the far
/// end to widen one, shows widths it doesn't save, and turns the star column into a fixed width when its edge is
/// double-clicked.
/// </summary>
static class FillColumnResize
{
    static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached("IsEnabled", typeof(bool), typeof(FillColumnResize), new PropertyMetadata(false));

    /// <summary>
    /// A drag under way: its gripper, where the gripper was in the list when it began, the shown columns' widths
    /// then, as measured and as set, and the window whose Esc cancels it.
    /// </summary>
    sealed record Dragging(Thumb Thumb, double ThumbX, List<ShownColumn> Start, List<(DataGridColumn Column, DataGridLength Width)> Widths, Window? Window);

    static Dragging? _drag;

    static FillColumnResize()
    {
        // A class handler runs before the handlers a column header puts on its grippers, so marking the event
        // handled keeps WPF's own sizing out. Every Thumb comes through here; any not on such a list goes on as before.
        // The start and the end aren't marked: the header's own handlers keep the widths to put back on a cancel.
        EventManager.RegisterClassHandler(typeof(Thumb), Thumb.DragStartedEvent, new DragStartedEventHandler(OnDragStarted));
        EventManager.RegisterClassHandler(typeof(Thumb), Thumb.DragDeltaEvent, new DragDeltaEventHandler(OnDragDelta));
        EventManager.RegisterClassHandler(typeof(Thumb), Thumb.DragCompletedEvent, new DragCompletedEventHandler(OnDragCompleted));
        EventManager.RegisterClassHandler(typeof(Thumb), Control.MouseDoubleClickEvent, new MouseButtonEventHandler(OnDoubleClick));
    }

    // A column's own least width, as the list was built; its MinWidth is held at its width while a column fills.
    static readonly DependencyProperty LeastWidthProperty =
        DependencyProperty.RegisterAttached("LeastWidth", typeof(double), typeof(FillColumnResize), new PropertyMetadata(double.NaN));

    public static void Enable(DataGrid grid)
    {
        grid.SetValue(IsEnabledProperty, true);
        var width = DependencyPropertyDescriptor.FromProperty(DataGridColumn.WidthProperty, typeof(DataGridColumn));
        var visibility = DependencyPropertyDescriptor.FromProperty(DataGridColumn.VisibilityProperty, typeof(DataGridColumn));
        foreach (var column in grid.Columns)
        {
            column.SetValue(LeastWidthProperty, column.MinWidth);
            width.AddValueChanged(column, (_, _) => Hold(grid));
            visibility.AddValueChanged(column, (_, _) => Hold(grid));
        }
        Hold(grid);
    }

    static double LeastOf(DataGridColumn column) => column.GetValue(LeastWidthProperty) is double least && !double.IsNaN(least) ? least : column.MinWidth;

    /// <summary>
    /// While a column fills, holds each other column at its width. WPF shares out no less than the columns' least
    /// widths, and narrows the others towards theirs, 20 px, where the list is too narrow for them beside the file
    /// name's least (at 820 px: Q 24 px, Size 69); held, they keep their widths and the list scrolls instead.
    /// </summary>
    static void Hold(DataGrid grid)
    {
        var fills = grid.Columns.Any(c => c.Visibility == Visibility.Visible && c.Width.IsStar);
        foreach (var column in grid.Columns)
        {
            if (column.Width.IsStar) continue;
            var least = LeastOf(column);
            column.MinWidth = fills && column.Width.IsAbsolute ? Math.Max(least, column.Width.Value) : least;
        }
    }

    /// <summary>Sizes a held column: let go first, so it can narrow, and held again at its new width.</summary>
    static void SetWidth(DataGridColumn column, double width)
    {
        column.MinWidth = Math.Min(column.MinWidth, Math.Max(LeastOf(column), width));
        column.Width = new DataGridLength(width);
    }

    /// <summary>
    /// The list a header gripper belongs to, its shown columns in display order, and which of them the gripper sizes.
    /// Null for any other Thumb, and while no column fills, as then WPF only sizes the one column.
    /// </summary>
    static (DataGrid Grid, List<DataGridColumn> Shown, int Sized)? Find(object sender)
    {
        if (sender is not Thumb { TemplatedParent: DataGridColumnHeader { Column: { } column } header } thumb) return null;
        DependencyObject? parent = header;
        while (parent is not null and not DataGrid) parent = VisualTreeHelper.GetParent(parent);
        if (parent is not DataGrid grid || !(bool)grid.GetValue(IsEnabledProperty)) return null;
        var shown = grid.Columns.Where(c => c.Visibility == Visibility.Visible).OrderBy(c => c.DisplayIndex).ToList();
        var sized = shown.IndexOf(column);
        // A header's left gripper sizes the column before it.
        if (thumb.Name == "PART_LeftHeaderGripper") sized--;
        else if (thumb.Name != "PART_RightHeaderGripper") return null;
        if (sized < 0 || !shown.Any(c => c.Width.IsStar)) return null;
        return (grid, shown, sized);
    }

    static List<ShownColumn> Measure(List<DataGridColumn> shown) =>
        shown.Select(c => new ShownColumn(c.ActualWidth, LeastOf(c), Math.Min(c.MaxWidth, ListColumns.MaxWidth), c.Width.IsStar)).ToList();

    static void OnDragStarted(object sender, DragStartedEventArgs e)
    {
        if (Find(sender) is { } found) Begin((Thumb)sender, found.Grid, found.Shown);
    }

    static void Begin(Thumb thumb, DataGrid grid, List<DataGridColumn> shown)
    {
        End();
        var window = Window.GetWindow(thumb);
        _drag = new Dragging(thumb, thumb.TranslatePoint(default, grid).X, Measure(shown), shown.Select(c => (c, c.Width)).ToList(), window);
        if (window != null) window.PreviewKeyDown += OnKeyDuringDrag;
    }

    static void End()
    {
        if (_drag?.Window is { } window) window.PreviewKeyDown -= OnKeyDuringDrag;
        _drag = null;
    }

    static void OnDragDelta(object sender, DragDeltaEventArgs e)
    {
        if (Find(sender) is not { } found) return;
        e.Handled = true;
        var thumb = (Thumb)sender;
        if (_drag?.Thumb != thumb) Begin(thumb, found.Grid, found.Shown);
        // The change is from where the press landed on the gripper, and a gripper moves only when its border does.
        // A column after the file name keeps its right edge where it is, as the file name takes up the difference,
        // so each move gives the whole distance so far. Where the gripper has got to since the drag began makes that
        // the pointer's distance from where it began, whichever border it is, and the widths are worked out afresh.
        var moved = thumb.TranslatePoint(default, found.Grid).X - _drag!.ThumbX + e.HorizontalChange;
        if (ListColumns.Drag(_drag.Start, found.Sized, moved) is { } dragged)
            SetWidth(found.Shown[dragged.Column], dragged.Width);
    }

    static void OnDragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (_drag?.Thumb != sender) return;
        if (e.Canceled)
            foreach (var (column, width) in _drag.Widths)
            {
                if (width.IsAbsolute) SetWidth(column, width.Value);
                else column.Width = width;
            }
        End();
    }

    // Esc while dragging puts the widths back. WPF itself cancels a drag only when the gripper loses the mouse.
    static void OnKeyDuringDrag(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || _drag is not { } drag) return;
        if (!drag.Thumb.IsDragging)
        {
            End();
            return;
        }
        e.Handled = true;
        drag.Thumb.CancelDrag();
    }

    static void OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Find(sender) is not { } found) return;
        e.Handled = true;
        var (grid, shown, sized) = found;
        var column = shown[sized];
        // The filling column already fits what the others leave.
        if (column.Width.IsStar) return;
        // Fitted to its header and the rows in view, within the room the filling column has, as a fixed width the
        // list saves. WPF's own fit, Auto, measures too narrow while a column fills (Status: 40 px for 93 px of
        // "Completed") and squeezes the columns after it.
        var unbounded = new Size(double.PositiveInfinity, double.PositiveInfinity);
        var fitted = 0.0;
        foreach (var element in Descendants(grid))
        {
            var owner = element switch { DataGridCell cell => cell.Column, DataGridColumnHeader header => header.Column, _ => null };
            if (owner != column) continue;
            element.Measure(unbounded);
            fitted = Math.Max(fitted, element.DesiredSize.Width);
            // Measured again within its column at the next layout.
            element.InvalidateMeasure();
        }
        if (ListColumns.Resize(Measure(shown), sized, Math.Ceiling(fitted) - column.ActualWidth) is { } resized)
            SetWidth(shown[resized.Column], resized.Width);
    }

    static IEnumerable<UIElement> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is UIElement element) yield return element;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
