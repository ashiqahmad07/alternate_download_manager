namespace ADM.Core;

/// <summary>
/// One column of the download list, as saved: shown or hidden, and its width in pixels. The file name's width can
/// be <see cref="ListColumns.Fill"/>: it then takes the room the other columns leave.
/// </summary>
public sealed class ListColumn
{
    public string Id { get; set; } = "";
    public bool Visible { get; set; } = true;
    public double Width { get; set; }
}

/// <summary>A column as the list shows it, for <see cref="ListColumns.Resize"/>: its width now, its least and most, and whether it fills.</summary>
public readonly record struct ShownColumn(double Width, double Min, double Max, bool Fills);

/// <summary>The download list's columns, and how a saved layout is brought up to date.</summary>
public static class ListColumns
{
    /// <summary>The file name's width when it takes the room the other columns leave.</summary>
    public const double Fill = 0;

    // In default order: the first seven show, the rest are there to be turned on. The file name leads and takes
    // the spare room; the queue mark, blank on most rows, comes after the status.
    static readonly (string Id, double Width, DownloadSortKey Sort)[] All =
    {
        ("FileName", Fill, DownloadSortKey.FileName),
        ("Size", 85, DownloadSortKey.Size),
        ("Progress", 130, DownloadSortKey.Progress),
        ("TransferRate", 100, DownloadSortKey.TransferRate),
        ("TimeLeft", 80, DownloadSortKey.TimeLeft),
        ("Status", 150, DownloadSortKey.Status),
        ("Q", 40, DownloadSortKey.Queue),
        ("Resume", 62, DownloadSortKey.Resume),
        ("LastTry", 130, DownloadSortKey.LastTry),
        ("Description", 160, DownloadSortKey.Description),
        ("Category", 90, DownloadSortKey.Category),
        ("DateAdded", 130, DownloadSortKey.OrderAdded),
        ("SaveTo", 200, DownloadSortKey.SavePath),
        ("Referer", 200, DownloadSortKey.Referer),
        ("ParentPage", 200, DownloadSortKey.ParentPage),
    };

    const int ShownByDefault = 7;
    const double MinWidth = 20;

    /// <summary>The widest a column is kept; a saved width beyond it goes back to the default.</summary>
    public const double MaxWidth = 2000;

    // What every version before 2026-11-24 started with: IDM's ten columns, the queue mark first and the file name
    // 260 pixels wide. A layout still exactly so was never changed, and takes today's defaults.
    static readonly (string Id, bool Visible, double Width)[] OldDefaults =
    {
        ("Q", true, 52), ("FileName", true, 260), ("Size", true, 85), ("Progress", true, 130), ("TransferRate", true, 90),
        ("TimeLeft", true, 80), ("Status", true, 150), ("Resume", true, 62), ("LastTry", true, 130), ("Description", true, 160),
        ("Category", false, 90), ("DateAdded", false, 130), ("SaveTo", false, 200), ("Referer", false, 200), ("ParentPage", false, 200),
    };

    public static List<ListColumn> Defaults() =>
        All.Select((c, i) => new ListColumn { Id = c.Id, Visible = i < ShownByDefault, Width = c.Width }).ToList();

    /// <summary>
    /// A saved layout from any version, made whole: unknown and repeated columns go, missing ones are added
    /// at the end, hidden; the file name always shows; a width out of range goes back to the default. A layout
    /// never changed from the old defaults takes today's.
    /// </summary>
    public static List<ListColumn> Normalize(IEnumerable<ListColumn>? saved)
    {
        var list = saved?.ToList();
        if (list == null || IsOldDefault(list)) return Defaults();
        var columns = new List<ListColumn>();
        foreach (var column in list)
        {
            var known = Array.FindIndex(All, c => string.Equals(c.Id, column?.Id, StringComparison.OrdinalIgnoreCase));
            if (known < 0 || columns.Any(c => c.Id == All[known].Id)) continue;
            var fills = All[known].Id == "FileName" && column!.Width == Fill;
            var width = fills || column!.Width is >= MinWidth and <= MaxWidth ? column.Width : All[known].Width;
            columns.Add(new ListColumn { Id = All[known].Id, Visible = column.Visible, Width = width });
        }
        foreach (var (id, width, _) in All)
            if (!columns.Any(c => c.Id == id)) columns.Add(new ListColumn { Id = id, Visible = false, Width = width });
        columns.First(c => c.Id == "FileName").Visible = true;
        return columns;
    }

    static bool IsOldDefault(List<ListColumn> saved) =>
        saved.Count == OldDefaults.Length
        && saved.Zip(OldDefaults).All(p => p.First != null && p.First.Id == p.Second.Id && p.First.Visible == p.Second.Visible && p.First.Width == p.Second.Width);

    /// <summary>
    /// Where a dragged column edge goes while one shown column fills: the column being sized alone changes, and the
    /// filling column takes up the difference, so it never grows wider than the room the filling column has above
    /// its least width. The filling column's own edge is its border with the column after it, which gives or
    /// takes the difference. <paramref name="sized"/> is the column the edge belongs to, by its place among
    /// <paramref name="shown"/>; null when nothing moves.
    /// </summary>
    public static (int Column, double Width)? Resize(IReadOnlyList<ShownColumn> shown, int sized, double change) =>
        Drag(shown, sized, change) is { } dragged && dragged.Width != shown[dragged.Column].Width ? dragged : null;

    /// <summary>
    /// Where a drag leaves the column it sizes, as <see cref="Resize"/> says, from the widths <paramref name="start"/>
    /// the columns had when the drag began and the distance <paramref name="moved"/> since. So a drag taken back to
    /// where it began gives the column its first width again. Null only for an edge that sizes nothing.
    /// </summary>
    public static (int Column, double Width)? Drag(IReadOnlyList<ShownColumn> start, int sized, double moved)
    {
        var fill = -1;
        for (int i = 0; i < start.Count; i++)
            if (start[i].Fills) fill = i;
        if (fill < 0 || sized < 0 || sized >= start.Count) return null;
        if (sized == fill)
        {
            // The border moves with the mouse: the file name widening is the column after it narrowing.
            sized++;
            moved = -moved;
            if (sized >= start.Count) return null;
        }
        var column = start[sized];
        var room = Math.Max(0, start[fill].Width - start[fill].Min);
        var least = Math.Min(column.Min, column.Width);
        var most = Math.Max(column.Width, Math.Min(column.Max, column.Width + room));
        return (sized, Math.Clamp(column.Width + moved, least, most));
    }

    public static DownloadSortKey SortKeyFor(string columnId) =>
        All.FirstOrDefault(c => c.Id == columnId) is { Id: not null } column ? column.Sort : DownloadSortKey.OrderAdded;
}

/// <summary>The toolbar's buttons, in order; "-" is a separator.</summary>
public static class ToolbarButtons
{
    public const string Separator = "-";

    public static readonly IReadOnlyList<string> All =
        new[] { "AddUrl", "Resume", "Pause", "PauseAll", "Delete", "DeleteCompleted", "StartQueue", "StopQueue", "Scheduler", "Grabber", "Find", "Options" };

    public static List<string> Defaults() => new()
    {
        "AddUrl", "Resume", "Pause", "PauseAll", "Delete", "DeleteCompleted", Separator, "StartQueue", "StopQueue", "Scheduler", "Grabber", Separator, "Options",
    };

    /// <summary>
    /// Drops unknown and repeated buttons and stray separators. A button the user removed stays removed;
    /// only a toolbar never saved gets the defaults.
    /// </summary>
    public static List<string> Normalize(IEnumerable<string>? saved)
    {
        if (saved == null) return Defaults();
        var buttons = new List<string>();
        foreach (var id in saved)
        {
            if (id == Separator)
            {
                if (buttons.Count > 0 && buttons[^1] != Separator) buttons.Add(Separator);
            }
            else if (All.Contains(id) && !buttons.Contains(id))
            {
                buttons.Add(id);
            }
        }
        if (buttons.Count > 0 && buttons[^1] == Separator) buttons.RemoveAt(buttons.Count - 1);
        return buttons;
    }
}
