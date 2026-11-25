using System.ComponentModel;
using ADM.Core;

namespace ADM.Core.Tests;

/// <summary>How the list shows a download's state at a glance, and the totals shown when no single download is picked.</summary>
public sealed class ListLookTests
{
    static DownloadItem Item(DownloadStatus status, string? error = null, long total = -1, long downloaded = 0) => new()
    {
        FileName = "f.bin",
        Url = "https://site.test/f.bin",
        Status = status,
        Error = error,
        TotalBytes = total,
        DownloadedBytes = downloaded,
    };

    // ---- The look of one download

    [Theory]
    [InlineData(DownloadStatus.Queued, null, StatusLook.Waiting)]
    [InlineData(DownloadStatus.Queued, "Timed out", StatusLook.Warning)]
    [InlineData(DownloadStatus.Downloading, null, StatusLook.Active)]
    [InlineData(DownloadStatus.Paused, null, StatusLook.Paused)]
    [InlineData(DownloadStatus.Paused, "Timed out", StatusLook.Warning)]
    [InlineData(DownloadStatus.Failed, "Not found", StatusLook.Failed)]
    [InlineData(DownloadStatus.Completed, null, StatusLook.Done)]
    public void Each_state_has_its_look(DownloadStatus status, string? error, StatusLook look) =>
        Assert.Equal(look, Item(status, error).Look);

    [Fact]
    public void A_finished_file_the_scanner_reported_on_is_a_warning()
    {
        var item = Item(DownloadStatus.Completed);
        item.ScanResult = "Threat found";

        Assert.Equal(StatusLook.Warning, item.Look);
    }

    [Fact]
    public void A_finished_file_being_checked_for_changes_is_active()
    {
        var item = Item(DownloadStatus.Completed);
        item.IsChecking = true;

        Assert.Equal(StatusLook.Active, item.Look);
    }

    [Fact]
    public void The_look_changes_with_the_status()
    {
        var item = Item(DownloadStatus.Downloading);
        var raised = new List<string?>();
        ((INotifyPropertyChanged)item).PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        item.Status = DownloadStatus.Failed;
        item.Error = "Not found";

        Assert.Equal(2, raised.Count(name => name == nameof(DownloadItem.Look)));
    }

    // ---- The totals of a list or a selection

    [Fact]
    public void An_empty_list_sums_to_nothing()
    {
        var summary = ListSummary.Of(Array.Empty<DownloadItem>());

        Assert.Equal(new ListSummary(0, 0, 0, 0, 0, 0, 0, 0), summary);
    }

    [Fact]
    public void Downloads_are_counted_by_state()
    {
        var summary = ListSummary.Of(new[]
        {
            Item(DownloadStatus.Completed, total: 100),
            Item(DownloadStatus.Completed, total: 100),
            Item(DownloadStatus.Downloading, total: 100),
            Item(DownloadStatus.Queued, "Timed out", total: 100),
            Item(DownloadStatus.Failed, "Not found", total: 100),
            Item(DownloadStatus.Paused, total: 100),
        });

        Assert.Equal(6, summary.Count);
        Assert.Equal(2, summary.Finished);
        Assert.Equal(4, summary.Unfinished);
        Assert.Equal(2, summary.WithErrors);
        Assert.Equal(1, summary.Downloading);
    }

    [Fact]
    public void Sizes_add_up_and_unknown_ones_are_counted_apart()
    {
        var summary = ListSummary.Of(new[]
        {
            Item(DownloadStatus.Completed, total: 1000, downloaded: 1000),
            Item(DownloadStatus.Downloading, total: 500, downloaded: 200),
            Item(DownloadStatus.Downloading, total: -1, downloaded: 300),
        });

        Assert.Equal(1500, summary.KnownSize);
        Assert.Equal(1, summary.UnknownSizes);
        Assert.Equal(1500, summary.Downloaded);
    }

    [Fact]
    public void A_finished_file_of_unknown_size_counts_what_was_downloaded()
    {
        // A stream's total is never known; once finished, what arrived is its size.
        var summary = ListSummary.Of(new[] { Item(DownloadStatus.Completed, total: -1, downloaded: 700) });

        Assert.Equal(700, summary.KnownSize);
        Assert.Equal(0, summary.UnknownSizes);
        Assert.Equal(700, summary.Downloaded);
    }
}
