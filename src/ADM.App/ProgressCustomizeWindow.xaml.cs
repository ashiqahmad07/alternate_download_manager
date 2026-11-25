using System.Windows;
using ADM.Core;

namespace ADM.App;

/// <summary>How a download's progress window opens, and which of its tabs show.</summary>
public partial class ProgressCustomizeWindow : ThemedWindow
{
    readonly Settings _settings;

    public ProgressCustomizeWindow(Settings settings)
    {
        InitializeComponent();
        Icon = AppIcon.ImageSource;
        _settings = settings;
        NormalView.IsChecked = settings.ProgressView == ProgressWindowView.Normal;
        MinimizedView.IsChecked = settings.ProgressView == ProgressWindowView.Minimized;
        HiddenView.IsChecked = settings.ProgressView == ProgressWindowView.Hidden;
        SpeedTabBox.IsChecked = settings.ProgressSpeedTab;
        WhenDoneTabBox.IsChecked = settings.ProgressWhenDoneTab;
        HideButtonsBox.IsChecked = settings.ProgressHideTabButtons;
    }

    void Ok_Click(object sender, RoutedEventArgs e)
    {
        _settings.ProgressView = MinimizedView.IsChecked == true ? ProgressWindowView.Minimized
            : HiddenView.IsChecked == true ? ProgressWindowView.Hidden
            : ProgressWindowView.Normal;
        _settings.ProgressSpeedTab = SpeedTabBox.IsChecked == true;
        _settings.ProgressWhenDoneTab = WhenDoneTabBox.IsChecked == true;
        _settings.ProgressHideTabButtons = HideButtonsBox.IsChecked == true;
        DialogResult = true;
    }
}
