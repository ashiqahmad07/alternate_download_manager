using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using ADM.Core;

namespace ADM.App;

/// <summary>A grabber project's counts: what was read, listed, checked and saved, and what went wrong.</summary>
public partial class GrabberStatsWindow : ThemedWindow
{
    public GrabberStatsWindow(GrabberStatistics counts, bool limitReached)
    {
        InitializeComponent();
        Icon = AppIcon.ImageSource;
        Add(Lang.T("grabberstats.pages_read"), counts.PagesRead.ToString("N0"));
        Add(Lang.T("grabberstats.listed"), counts.Listed.ToString("N0"));
        Add(Lang.T("grabberstats.checked"), counts.Checked.ToString("N0"));
        Add(Lang.T("grabberstats.saved"), counts.Saved.ToString("N0"));
        Add(Lang.T("grabberstats.saved_size"), Format.Bytes(counts.SavedBytes));
        Add(Lang.T("grabberstats.changed"), counts.Changed.ToString("N0"));
        Add(Lang.T("grabberstats.waiting"), counts.Waiting.ToString("N0"));
        Add(Lang.T("grabberstats.not_found"), counts.NotFound.ToString("N0"));
        Add(Lang.T("grabberstats.not_saved"), counts.NotSaved.ToString("N0"));
        if (limitReached)
        {
            LimitText.Text = Lang.T("grabber.limit_reached", GrabberCrawler.MaxEntries);
            LimitText.Visibility = Visibility.Visible;
        }
    }

    void Add(string label, string value)
    {
        int row = Counts.RowDefinitions.Count;
        Counts.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var name = new TextBlock { Text = label, Margin = new Thickness(0, 2, 16, 2) };
        var number = new TextBlock { Text = value, Margin = new Thickness(0, 2, 0, 2), HorizontalAlignment = HorizontalAlignment.Right };
        AutomationProperties.SetName(number, label);
        Grid.SetRow(name, row);
        Grid.SetRow(number, row);
        Grid.SetColumn(number, 1);
        Counts.Children.Add(name);
        Counts.Children.Add(number);
    }
}
