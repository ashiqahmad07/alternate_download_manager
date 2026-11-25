using System.Windows;

namespace ADM.App;

/// <summary>Asks for the name to keep a new grabber project under, and, when it's closing, whether to keep it at all.</summary>
public partial class SaveProjectWindow : ThemedWindow
{
    public enum Answer { Cancel, Save, DontSave }

    /// <param name="name">A name no other project has.</param>
    /// <param name="offerNotSaving">The project is closing, so not keeping it is a choice too.</param>
    public SaveProjectWindow(string name, bool offerNotSaving)
    {
        InitializeComponent();
        Icon = AppIcon.ImageSource;
        MessageText.Text = offerNotSaving ? Lang.T("saveproject.closing") : Lang.T("saveproject.prompt");
        DontSaveButton.Visibility = offerNotSaving ? Visibility.Visible : Visibility.Collapsed;
        NameBox.Text = name;
        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    public Answer Choice { get; private set; }

    public string ProjectName => NameBox.Text.Trim();

    void Save_Click(object sender, RoutedEventArgs e)
    {
        if (ProjectName.Length == 0)
        {
            ProblemText.Text = Lang.T("saveproject.name_missing");
            NameBox.Focus();
            return;
        }
        Choice = Answer.Save;
        DialogResult = true;
    }

    void DontSave_Click(object sender, RoutedEventArgs e)
    {
        Choice = Answer.DontSave;
        DialogResult = true;
    }
}
