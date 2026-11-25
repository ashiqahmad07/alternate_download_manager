using System.Windows;

namespace ADM.App;

/// <summary>After a download from the browser is cancelled twice, offers to stop capturing its site or address.</summary>
public partial class TwoCancelsWindow : ThemedWindow
{
    public TwoCancelsWindow(string url)
    {
        InitializeComponent();
        Icon = AppIcon.ImageSource;
        // The site's name stands out wherever the language puts it in the sentence.
        var parts = Lang.T("twocancels.every_download_from", "\n").Split('\n');
        SiteBefore.Text = parts[0];
        SiteText.Text = new Uri(url).Host;
        SiteAfter.Text = parts[^1];
        AddressText.Text = Lang.T("twocancels.only_this_address", url);
    }

    public bool ExcludeSite { get; private set; }
    public bool ExcludeAddress { get; private set; }
    public bool DontAskAgain { get; private set; }

    void Ok_Click(object sender, RoutedEventArgs e)
    {
        ExcludeSite = SiteBox.IsChecked == true;
        // The whole site covers the address too.
        ExcludeAddress = !ExcludeSite && AddressBox.IsChecked == true;
        DontAskAgain = DontAskBox.IsChecked == true;
        DialogResult = true;
    }
}
