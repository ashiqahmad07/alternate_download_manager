using ADM.Core;

namespace ADM.Core.Tests;

/// <summary>Sorting the download list by any column, as View > Arrange files does.</summary>
public sealed class DownloadSortTests
{
    static readonly DateTime T0 = new(2026, 10, 30, 12, 0, 0);

    static DownloadItem Item(string name, int addedMinute = 0) => new() { FileName = name, AddedAt = T0.AddMinutes(addedMinute) };

    static string Sorted(IEnumerable<DownloadItem> items, DownloadSortKey key, bool descending) =>
        string.Join(" ", items.Order(DownloadSort.Comparer(key, descending)).Select(i => i.FileName));

    /// <summary>A running download at <paramref name="bytesPerSecond"/> with <paramref name="left"/> bytes to go.</summary>
    static DownloadItem Running(string name, long left, double bytesPerSecond, int addedMinute = 0)
    {
        var item = Item(name, addedMinute);
        item.Status = DownloadStatus.Downloading;
        item.TotalBytes = left + (long)bytesPerSecond;
        item.Tick(T0);
        item.AddDownloaded((long)bytesPerSecond);
        item.Tick(T0.AddSeconds(1));
        return item;
    }

    [Fact]
    public void Names_sort_like_explorer()
    {
        var items = new[] { Item("file10.zip"), Item("file2.zip"), Item("File1.zip") };

        Assert.Equal("File1.zip file2.zip file10.zip", Sorted(items, DownloadSortKey.FileName, descending: false));
        Assert.Equal("file10.zip file2.zip File1.zip", Sorted(items, DownloadSortKey.FileName, descending: true));
    }

    [Fact]
    public void Unknown_sizes_sort_last_both_ways()
    {
        var a = Item("a");
        a.TotalBytes = 100;
        var b = Item("b");
        var c = Item("c");
        c.TotalBytes = 50;

        Assert.Equal("c a b", Sorted(new[] { a, b, c }, DownloadSortKey.Size, descending: false));
        Assert.Equal("a c b", Sorted(new[] { a, b, c }, DownloadSortKey.Size, descending: true));
    }

    [Fact]
    public void Status_sorts_active_first()
    {
        var items = new[]
        {
            new DownloadItem { FileName = "completed", Status = DownloadStatus.Completed },
            new DownloadItem { FileName = "failed", Status = DownloadStatus.Failed },
            new DownloadItem { FileName = "paused", Status = DownloadStatus.Paused },
            new DownloadItem { FileName = "queued", Status = DownloadStatus.Queued },
            new DownloadItem { FileName = "downloading", Status = DownloadStatus.Downloading },
        };

        Assert.Equal("downloading queued paused failed completed", Sorted(items, DownloadSortKey.Status, descending: false));
        Assert.Equal("completed failed paused queued downloading", Sorted(items, DownloadSortKey.Status, descending: true));
    }

    [Fact]
    public void Time_left_puts_finished_and_paused_last()
    {
        var soon = Running("soon", left: 1000, bytesPerSecond: 100);
        var later = Running("later", left: 10000, bytesPerSecond: 100);
        var paused = Item("paused", addedMinute: 1);
        paused.Status = DownloadStatus.Paused;
        var done = Item("done", addedMinute: 2);
        done.Status = DownloadStatus.Completed;
        var items = new[] { paused, later, done, soon };

        Assert.Equal(10, soon.SecondsLeft);
        Assert.Null(paused.SecondsLeft);
        Assert.Equal("soon later done paused", Sorted(items, DownloadSortKey.TimeLeft, descending: false));
        Assert.Equal("later soon done paused", Sorted(items, DownloadSortKey.TimeLeft, descending: true));
    }

    [Fact]
    public void Time_left_text_comes_from_the_seconds_left()
    {
        Assert.Equal("1m 40s", Running("a", left: 10000, bytesPerSecond: 100).EtaText);
        Assert.Equal("", Item("b").EtaText);
    }

    [Fact]
    public void Equal_keys_keep_newest_first()
    {
        var items = new[] { Item("old", 1), Item("newest", 3), Item("middle", 2) };
        foreach (var item in items) item.TotalBytes = 10;

        Assert.Equal("newest middle old", Sorted(items, DownloadSortKey.Size, descending: false));
        Assert.Equal("newest middle old", Sorted(items, DownloadSortKey.Size, descending: true));
    }

    [Fact]
    public void Same_time_added_falls_back_to_the_id()
    {
        var first = new DownloadItem { FileName = "first", Id = new Guid("00000000-0000-0000-0000-000000000001"), AddedAt = T0 };
        var second = new DownloadItem { FileName = "second", Id = new Guid("00000000-0000-0000-0000-000000000002"), AddedAt = T0 };

        Assert.Equal("first second", Sorted(new[] { second, first }, DownloadSortKey.OrderAdded, descending: true));
        Assert.Equal("first second", Sorted(new[] { second, first }, DownloadSortKey.OrderAdded, descending: false));
    }

    [Fact]
    public void Order_added_descending_is_newest_first()
    {
        var items = new[] { Item("b", 2), Item("a", 1), Item("c", 3) };

        Assert.Equal("c b a", Sorted(items, DownloadSortKey.OrderAdded, descending: true));
        Assert.Equal("a b c", Sorted(items, DownloadSortKey.OrderAdded, descending: false));
    }

    [Fact]
    public void Empty_text_and_no_last_try_sort_last()
    {
        var described = Item("described");
        described.Description = "notes";
        described.LastTryAt = T0;
        var blank = Item("blank", addedMinute: 1);
        blank.Description = "";
        var other = Item("other", addedMinute: 2);
        other.Description = "about";
        other.LastTryAt = T0.AddMinutes(5);

        Assert.Equal("other described blank", Sorted(new[] { blank, described, other }, DownloadSortKey.Description, descending: false));
        Assert.Equal("described other blank", Sorted(new[] { blank, described, other }, DownloadSortKey.Description, descending: true));
        Assert.Equal("described other blank", Sorted(new[] { blank, described, other }, DownloadSortKey.LastTry, descending: false));
        Assert.Equal("other described blank", Sorted(new[] { blank, described, other }, DownloadSortKey.LastTry, descending: true));
    }

    [Fact]
    public void In_order_holds_until_a_running_download_overtakes_another()
    {
        var slow = Running("slow", left: 1000, bytesPerSecond: 10);
        var fast = Running("fast", left: 1000, bytesPerSecond: 100);
        var byRate = DownloadSort.Comparer(DownloadSortKey.TransferRate, descending: true);

        Assert.True(DownloadSort.InOrder(new[] { fast, slow }, byRate));
        Assert.True(DownloadSort.InOrder(Array.Empty<DownloadItem>(), byRate));

        // The slow one speeds up past the other: the list's order is stale.
        slow.AddDownloaded(1000);
        slow.Tick(T0.AddSeconds(2));
        Assert.False(DownloadSort.InOrder(new[] { fast, slow }, byRate));
        Assert.True(DownloadSort.InOrder(new[] { slow, fast }, byRate));
    }

    [Fact]
    public void In_order_takes_ties_as_the_comparer_breaks_them()
    {
        // Equal on the key, so newest first.
        var older = Item("same", addedMinute: 0);
        var newer = Item("same", addedMinute: 1);
        var byName = DownloadSort.Comparer(DownloadSortKey.FileName, descending: false);

        Assert.True(DownloadSort.InOrder(new[] { newer, older }, byName));
        Assert.False(DownloadSort.InOrder(new[] { older, newer }, byName));
    }
}
