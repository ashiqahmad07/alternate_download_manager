using System.Windows;

namespace ADM.App;

/// <summary>Asks for one line of text.</summary>
public partial class PromptWindow : ThemedWindow
{
    public PromptWindow(string title, string message, string value)
    {
        InitializeComponent();
        Icon = AppIcon.ImageSource;
        Title = title;
        MessageText.Text = message;
        ValueBox.Text = value;
        Loaded += (_, _) =>
        {
            ValueBox.Focus();
            ValueBox.SelectAll();
        };
    }

    public string Value => ValueBox.Text.Trim();

    void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
