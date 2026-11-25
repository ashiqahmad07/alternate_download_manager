using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using ADM.Core;

namespace ADM.App;

/// <summary>What an empty list says, and the totals the details pane shows when no single download is picked.</summary>
public partial class MainWindow
{
    void InitOverview()
    {
        // A filter, a sort, an add or a delete.
        ((INotifyCollectionChanged)_view).CollectionChanged += (_, _) => UpdateOverview();
        UpdateOverview();
    }

    void Downloads_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateOverview();
        UpdateToolbar();
    }

    /// <summary>On every change of the list or the selection, and each tick, as states and sizes move on.</summary>
    void UpdateOverview()
    {
        var empty = _view.IsEmpty;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        if (empty) ShowEmptyState();

        // Hidden rather than collapsed: the row is sized to its content, and keeping the details' room whichever
        // shows stops the list jumping as the selection changes.
        var picked = Downloads.SelectedItems.Count;
        DetailsPane.Visibility = picked == 1 ? Visibility.Visible : Visibility.Hidden;
        SummaryPane.Visibility = !empty && picked != 1 ? Visibility.Visible : Visibility.Hidden;
        if (SummaryPane.Visibility == Visibility.Visible) ShowSummary(picked);
    }

    void ShowEmptyState()
    {
        var filter = FilterList.SelectedItem as Filter;
        // A project's files are in its own window, so its entry always lists nothing here.
        var projects = filter?.Project != null || filter?.Label == Lang.T("main.filter_projects");
        if (projects)
        {
            EmptyTitle.Text = Lang.T("main.empty_projects_title");
            EmptyHint.Text = Lang.T("main.empty_projects_hint");
        }
        else if (!Listed.Any())
        {
            EmptyTitle.Text = Lang.T("main.empty_title");
            EmptyHint.Text = Lang.T("main.empty_hint");
        }
        else if (NameFilter.Length > 0)
        {
            EmptyTitle.Text = Lang.T("main.empty_search_title");
            EmptyHint.Text = Lang.T("main.empty_search_hint", filter?.ToString() ?? "", NameFilter);
            EmptyAddButton.Visibility = Visibility.Collapsed;
            return;
        }
        else
        {
            EmptyTitle.Text = Lang.T("main.empty_filter_title");
            EmptyHint.Text = Lang.T("main.empty_filter_hint", filter?.ToString() ?? "");
        }
        EmptyAddButton.Visibility = projects ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>The totals of the downloads picked when there are several, or else of those listed.</summary>
    void ShowSummary(int picked)
    {
        var summary = ListSummary.Of(picked > 1 ? Downloads.SelectedItems.Cast<DownloadItem>() : _view.Cast<DownloadItem>());

        // Finished and unfinished only where the two are mixed: "Unfinished" listing "0 finished" says nothing.
        var parts = new List<string>();
        if (summary.Finished > 0 && summary.Unfinished > 0)
        {
            parts.Add(Lang.T("main.summary_finished", summary.Finished));
            parts.Add(Lang.T("main.summary_unfinished", summary.Unfinished));
        }
        if (summary.Downloading > 0) parts.Add(Lang.T("main.summary_downloading", summary.Downloading));
        if (summary.WithErrors > 0) parts.Add(Lang.T("main.summary_with_errors", summary.WithErrors));
        var states = string.Join(", ", parts);

        if (picked > 1)
        {
            SummaryTitle.Text = Lang.T("main.summary_selected", summary.Count);
            SummaryStates.Text = states;
        }
        else
        {
            // The sidebar's entry names what is listed, with the filter box's text if any; the count follows.
            var listed = (FilterList.SelectedItem as Filter)?.ToString() ?? "";
            SummaryTitle.Text = NameFilter.Length > 0 ? Lang.T("main.summary_named", listed, NameFilter) : listed;
            var count = summary.Count == 1 ? Lang.T("main.status_downloads_one") : Lang.T("main.status_downloads", summary.Count);
            SummaryStates.Text = parts.Count > 0 ? Lang.T("main.summary_list", count, states) : count;
        }
        SummaryStates.Visibility = SummaryStates.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        // With some sizes unknown, what has come down can be more than the sizes known add up to, so it comes
        // first and the known total is called that. With none known, "0 bytes in all" would mislead.
        var downloaded = Format.Bytes(summary.Downloaded);
        SummarySizes.Text = summary.UnknownSizes == 0
            ? Lang.T("main.summary_size", Format.Bytes(summary.KnownSize), downloaded)
            : summary.KnownSize > 0
            ? Lang.T("main.summary_size_some_unknown", downloaded, Format.Bytes(summary.KnownSize), summary.UnknownSizes)
            : Lang.T("main.summary_downloaded", downloaded) + ", " + Lang.T("main.summary_unknown", summary.UnknownSizes);
    }
}
