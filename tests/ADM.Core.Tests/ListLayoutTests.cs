using ADM.Core;

namespace ADM.Core.Tests;

/// <summary>The main window's saved layout: which columns show, in what order and width, and the toolbar's buttons.</summary>
public sealed class ListLayoutTests
{
    static readonly string[] AllIds =
    {
        "FileName", "Size", "Progress", "TransferRate", "TimeLeft", "Status", "Q", "Resume", "LastTry", "Description",
        "Category", "DateAdded", "SaveTo", "Referer", "ParentPage",
    };

    // The layout every version before 2026-11-24 started with.
    static List<ListColumn> OldDefaults() =>
        new[] { "Q", "FileName", "Size", "Progress", "TransferRate", "TimeLeft", "Status", "Resume", "LastTry", "Description", "Category", "DateAdded", "SaveTo", "Referer", "ParentPage" }
            .Zip(new double[] { 52, 260, 85, 130, 90, 80, 150, 62, 130, 160, 90, 130, 200, 200, 200 })
            .Select((c, i) => new ListColumn { Id = c.First, Visible = i < 10, Width = c.Second })
            .ToList();

    [Fact]
    public void Defaults_show_seven_columns_with_the_file_name_first_taking_the_spare_room()
    {
        var columns = ListColumns.Defaults();

        Assert.Equal(AllIds, columns.Select(c => c.Id));
        Assert.Equal(AllIds.Take(7), columns.Where(c => c.Visible).Select(c => c.Id));
        Assert.Equal(new double[] { ListColumns.Fill, 85, 130, 100, 80, 150, 40, 62, 130, 160, 90, 130, 200, 200, 200 }, columns.Select(c => c.Width));
    }

    [Fact]
    public void A_layout_never_changed_from_the_old_defaults_takes_the_new_ones()
    {
        Assert.Equal(ListColumns.Defaults().Select(c => (c.Id, c.Visible, c.Width)), ListColumns.Normalize(OldDefaults()).Select(c => (c.Id, c.Visible, c.Width)));
    }

    [Theory]
    [InlineData("FileName", "Width", 261)]
    [InlineData("Description", "Visible", 0)]
    [InlineData("Q", "Order", 0)]
    public void A_layout_the_user_changed_is_kept(string id, string change, double width)
    {
        var saved = OldDefaults();
        var column = saved.First(c => c.Id == id);
        if (change == "Width") column.Width = width;
        else if (change == "Visible") column.Visible = false;
        else
        {
            saved.Remove(column);
            saved.Insert(7, column);
        }

        var columns = ListColumns.Normalize(saved);

        Assert.Equal(saved.Select(c => (c.Id, c.Visible, c.Width)), columns.Select(c => (c.Id, c.Visible, c.Width)));
    }

    [Fact]
    public void Only_the_file_name_can_take_the_spare_room()
    {
        var columns = ListColumns.Normalize(new[]
        {
            new ListColumn { Id = "FileName", Visible = true, Width = ListColumns.Fill },
            new ListColumn { Id = "Status", Visible = true, Width = ListColumns.Fill },
        });

        Assert.Equal(ListColumns.Fill, columns[0].Width);
        Assert.Equal(150, columns[1].Width);
    }

    [Fact]
    public void Saved_columns_are_normalised()
    {
        var saved = new List<ListColumn>
        {
            new() { Id = "SaveTo", Visible = true, Width = 210 },
            new() { Id = "Bogus", Visible = true, Width = 100 },
            new() { Id = "FileName", Visible = false, Width = 300 },
            new() { Id = "SaveTo", Visible = false, Width = 99 },
            new() { Id = "Size", Visible = true, Width = 0 },
        };

        var columns = ListColumns.Normalize(saved);

        Assert.Equal(new[] { "SaveTo", "FileName", "Size" }, columns.Take(3).Select(c => c.Id));
        Assert.True(columns[0].Visible);
        Assert.Equal(210, columns[0].Width);
        Assert.True(columns[1].Visible);
        Assert.Equal(300, columns[1].Width);
        Assert.Equal(85, columns[2].Width);
        Assert.Equal(AllIds.Order(), columns.Select(c => c.Id).Order());
        Assert.All(columns.Skip(3), c => Assert.False(c.Visible));
        Assert.Equal(AllIds.Except(new[] { "SaveTo", "FileName", "Size" }), columns.Skip(3).Select(c => c.Id));
    }

    [Fact]
    public void No_saved_columns_means_the_defaults()
    {
        Assert.Equal(ListColumns.Defaults().Select(c => (c.Id, c.Visible, c.Width)), ListColumns.Normalize(null).Select(c => (c.Id, c.Visible, c.Width)));
    }

    // The list as it opens at 1120 px: the file name fills 325 px, with 205 to spare above its least width.
    static ShownColumn[] Shown() => new ShownColumn[]
    {
        new(325, 120, 2000, true), new(85, 20, 2000, false), new(130, 20, 2000, false), new(100, 20, 2000, false),
        new(80, 20, 2000, false), new(150, 20, 2000, false), new(40, 20, 2000, false),
    };

    [Theory]
    [InlineData(2, -50, 80)]
    [InlineData(2, 50, 180)]
    [InlineData(5, -200, 20)]
    [InlineData(6, 300, 245)]
    public void A_dragged_column_alone_changes_and_grows_only_into_the_file_names_room(int sized, double change, double width)
    {
        Assert.Equal((sized, width), ListColumns.Resize(Shown(), sized, change));
    }

    [Theory]
    [InlineData(-150, 235)]
    [InlineData(-300, 290)]
    [InlineData(50, 35)]
    [InlineData(250, 20)]
    public void The_file_names_edge_is_its_border_with_the_next_column(double change, double nextWidth)
    {
        Assert.Equal((1, nextWidth), ListColumns.Resize(Shown(), 0, change));
    }

    [Theory]
    [InlineData(3, -20, 3, 80)]
    [InlineData(3, 0, 3, 100)]
    [InlineData(0, 0, 1, 85)]
    [InlineData(5, 400, 5, 355)]
    public void A_drag_is_worked_out_from_the_widths_when_it_began(int sized, double moved, int column, double width)
    {
        // A drag taken back to where it began gives the column its first width, where Resize says nothing moves.
        Assert.Equal((column, width), ListColumns.Drag(Shown(), sized, moved));
    }

    [Fact]
    public void An_edge_that_sizes_nothing_drags_nothing()
    {
        var shown = Shown();

        Assert.Null(ListColumns.Drag(shown[..1], 0, 30));
        Assert.Null(ListColumns.Drag(shown, 7, 30));
        Assert.Null(ListColumns.Drag(shown.Select(c => c with { Fills = false }).ToList(), 2, 30));
    }

    [Fact]
    public void Nothing_moves_where_there_is_no_room()
    {
        var shown = Shown();
        shown[0] = shown[0] with { Width = 120 };

        shown[6] = shown[6] with { Width = 20 };

        Assert.Null(ListColumns.Resize(shown, 5, 40));
        Assert.Null(ListColumns.Resize(shown, 0, -40));
        Assert.Null(ListColumns.Resize(shown, 6, -30));
        Assert.Null(ListColumns.Resize(shown, 2, 0));
        // Narrowing needs no room.
        Assert.Equal((5, 110.0), ListColumns.Resize(shown, 5, -40));
    }

    [Fact]
    public void A_column_never_grows_past_its_most()
    {
        var shown = Shown();
        shown[0] = shown[0] with { Width = 3000 };

        Assert.Equal((3, 2000.0), ListColumns.Resize(shown, 3, 5000));
    }

    [Fact]
    public void The_file_name_last_has_no_border_to_move()
    {
        var shown = Shown().Skip(1).Append(new ShownColumn(325, 120, 2000, true)).ToArray();

        Assert.Null(ListColumns.Resize(shown, 6, -50));
        Assert.Equal((5, 100.0), ListColumns.Resize(shown, 5, 60));
    }

    [Fact]
    public void Without_a_filling_column_the_list_sizes_columns_its_own_way()
    {
        var shown = Shown();
        shown[0] = shown[0] with { Fills = false };

        Assert.Null(ListColumns.Resize(shown, 2, -50));
        Assert.Null(ListColumns.Resize(Shown(), 7, -50));
    }

    [Fact]
    public void Every_column_sorts_by_its_own_key()
    {
        var keys = AllIds.Select(ListColumns.SortKeyFor).ToList();

        Assert.Equal(keys.Count, keys.Distinct().Count());
        Assert.Equal(DownloadSortKey.OrderAdded, ListColumns.SortKeyFor("DateAdded"));
        Assert.Equal(DownloadSortKey.ParentPage, ListColumns.SortKeyFor("ParentPage"));
    }

    [Fact]
    public void Toolbar_keeps_the_users_choice()
    {
        var buttons = ToolbarButtons.Normalize(new[] { "-", "Find", "Bogus", "Find", "-", "-", "Options", "-" });

        Assert.Equal(new[] { "Find", "-", "Options" }, buttons);
    }

    [Fact]
    public void Toolbar_defaults_when_nothing_is_saved()
    {
        Assert.Equal(
            new[] { "AddUrl", "Resume", "Pause", "PauseAll", "Delete", "DeleteCompleted", "-", "StartQueue", "StopQueue", "Scheduler", "Grabber", "-", "Options" },
            ToolbarButtons.Normalize(null));
        Assert.Equal(ToolbarButtons.Defaults(), ToolbarButtons.Normalize(null));
        Assert.Empty(ToolbarButtons.Normalize(Array.Empty<string>()));
    }

    [Fact]
    public void Older_settings_load_with_main_window_defaults()
    {
        var path = Path.Combine(Path.GetTempPath(), "adm-tests", Guid.NewGuid().ToString("N"), "state.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """{ "Settings": { "DefaultConnections": 8, "SpeedLimitKBps": 0, "ReconnectMinutes": 60 } }""");
        try
        {
            var s = Store.Load(path).Settings;

            Assert.Equal(ListColumns.Defaults().Select(c => (c.Id, c.Visible, c.Width)), s.Columns.Select(c => (c.Id, c.Visible, c.Width)));
            Assert.Equal(DownloadSortKey.OrderAdded, s.SortKey);
            Assert.True(s.SortDescending);
            Assert.Equal(ToolbarButtons.Defaults(), s.Toolbar);
            Assert.True(s.LargeToolbarButtons);
            Assert.True(s.ShowCategories);
            Assert.Equal(SearchTargets.Names | SearchTargets.Descriptions, s.SearchIn);
            Assert.False(s.SearchMatchCase);
            Assert.False(s.SearchWholeString);
            Assert.Equal(DoubleClickAction.Open, s.OnDoubleClick);
            Assert.True(s.ConfirmDeleteCompleted);
            Assert.True(s.ConfirmDeleteUnfinished);
            Assert.True(s.ShowCompleteDialog);
            Assert.Equal(ProgressWindowView.Normal, s.ProgressView);
            Assert.True(s.ProgressSpeedTab);
            Assert.True(s.ProgressWhenDoneTab);
            Assert.True(s.ProgressHideTabButtons);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }

    [Fact]
    public void Layout_survives_a_save()
    {
        var path = Path.Combine(Path.GetTempPath(), "adm-tests", Guid.NewGuid().ToString("N"), "state.json");
        var state = new AppState();
        state.Settings.Columns[1].Width = 333;
        state.Settings.SearchIn = SearchTargets.Addresses | SearchTargets.Names;
        state.Settings.Toolbar = new() { "Find" };
        try
        {
            Store.Save(path, state);
            var s = Store.Load(path).Settings;

            Assert.Equal(333, s.Columns[1].Width);
            Assert.Equal(SearchTargets.Addresses | SearchTargets.Names, s.SearchIn);
            Assert.Equal(new[] { "Find" }, s.Toolbar);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }
}
