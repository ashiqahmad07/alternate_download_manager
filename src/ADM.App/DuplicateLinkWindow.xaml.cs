using System.Windows;
using ADM.Core;

namespace ADM.App;

/// <summary>Asks what to do when a link that is already listed is added again.</summary>
public partial class DuplicateLinkWindow : ThemedWindow
{
    public DuplicateLinkWindow(DownloadItem existing)
    {
        InitializeComponent();
        Icon = AppIcon.ImageSource;
        NameText.Text = existing.ToString();
        StatusText.Text = existing.Status == DownloadStatus.Completed
            ? Lang.T("duplicatelink.downloaded_to", existing.FullPath)
            : Lang.T("duplicatelink.status_done", existing.StatusText,
                existing.ProgressKnown ? existing.ProgressText : Format.Bytes(existing.DownloadedBytes));
    }

    public DuplicateAction Choice { get; private set; }
    public bool Remember { get; private set; }

    void Ok_Click(object sender, RoutedEventArgs e)
    {
        Choice = OverwriteOption.IsChecked == true ? DuplicateAction.Overwrite
            : ResumeOption.IsChecked == true ? DuplicateAction.Resume
            : DuplicateAction.AddNumbered;
        Remember = RememberBox.IsChecked == true;
        DialogResult = true;
    }
}
