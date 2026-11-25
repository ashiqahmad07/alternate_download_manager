using System.Windows;

namespace ADM.App;

/// <summary>
/// Asks before downloads leave the list. Files on disk are deleted only when the box is ticked, and it starts
/// unticked every time; it applies to finished downloads only. Unfinished ones always lose their progress.
/// </summary>
public partial class DeleteConfirmWindow : ThemedWindow
{
    /// <param name="finished">Finished downloads to ask about; 0 hides that part.</param>
    /// <param name="unfinished">Unfinished downloads to ask about; 0 hides that part.</param>
    public DeleteConfirmWindow(int finished, int unfinished)
    {
        InitializeComponent();
        Icon = AppIcon.ImageSource;
        FinishedPanel.Visibility = finished > 0 ? Visibility.Visible : Visibility.Collapsed;
        UnfinishedPanel.Visibility = unfinished > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (finished == 0) UnfinishedPanel.Margin = new Thickness(0);
        FinishedText.Text = finished == 1
            ? Lang.T("deleteconfirm.remove_one")
            : Lang.T("deleteconfirm.remove_many", finished);
        UnfinishedText.Text = unfinished == 1
            ? Lang.T("deleteconfirm.delete_one")
            : Lang.T("deleteconfirm.delete_many", unfinished);
    }

    public bool DeleteFiles { get; private set; }
    public bool DontAskFinished { get; private set; }
    public bool DontAskUnfinished { get; private set; }

    void Delete_Click(object sender, RoutedEventArgs e)
    {
        DeleteFiles = FinishedPanel.IsVisible && DeleteFilesBox.IsChecked == true;
        DontAskFinished = FinishedPanel.IsVisible && DontAskFinishedBox.IsChecked == true;
        DontAskUnfinished = UnfinishedPanel.IsVisible && DontAskUnfinishedBox.IsChecked == true;
        DialogResult = true;
    }
}
