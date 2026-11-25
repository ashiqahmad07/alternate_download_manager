using System.Windows;
using System.Windows.Controls;
using ADM.Core;

namespace ADM.App;

/// <summary>Where the Find bar looks, and how strictly it matches. At least one place to look stays ticked.</summary>
public partial class FindSettingsWindow : ThemedWindow
{
    readonly Settings _settings;

    public FindSettingsWindow(Settings settings)
    {
        InitializeComponent();
        Icon = AppIcon.ImageSource;
        _settings = settings;
        NamesBox.IsChecked = settings.SearchIn.HasFlag(SearchTargets.Names);
        DescriptionsBox.IsChecked = settings.SearchIn.HasFlag(SearchTargets.Descriptions);
        AddressesBox.IsChecked = settings.SearchIn.HasFlag(SearchTargets.Addresses);
        MatchCaseBox.IsChecked = settings.SearchMatchCase;
        WholeStringBox.IsChecked = settings.SearchWholeString;
        foreach (var box in new[] { NamesBox, DescriptionsBox, AddressesBox })
        {
            box.Checked += (_, _) => UpdateOk();
            box.Unchecked += (_, _) => UpdateOk();
        }
        UpdateOk();
    }

    SearchTargets Targets =>
        (NamesBox.IsChecked == true ? SearchTargets.Names : 0)
        | (DescriptionsBox.IsChecked == true ? SearchTargets.Descriptions : 0)
        | (AddressesBox.IsChecked == true ? SearchTargets.Addresses : 0);

    void UpdateOk() => OkButton.IsEnabled = Targets != SearchTargets.None;

    void Ok_Click(object sender, RoutedEventArgs e)
    {
        _settings.SearchIn = Targets;
        _settings.SearchMatchCase = MatchCaseBox.IsChecked == true;
        _settings.SearchWholeString = WholeStringBox.IsChecked == true;
        DialogResult = true;
    }
}
