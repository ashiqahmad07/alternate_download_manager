using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using ADM.Core;

namespace ADM.App;

/// <summary>
/// Counts down before a queue's or a download's "when done" sleep, hibernate or shut down, so whoever is at the computer
/// can stop it. Cancel is the default button; doing nothing lets the countdown finish.
/// </summary>
public partial class CountdownWindow : ThemedWindow
{
    const int Seconds = 60;

    readonly DispatcherTimer _timer;
    readonly CompletionAction _action;
    int _left = Seconds;
    bool _done;

    /// <param name="reason">What finished and who chose this, ending where ", the computer will …" follows in English.</param>
    public CountdownWindow(string reason, CompletionAction action, bool force, int othersRunning)
    {
        InitializeComponent();
        Icon = AppIcon.ImageSource;

        _action = action;
        (MessageText.Text, NowButton.Content) = action switch
        {
            CompletionAction.Sleep => (Lang.T("countdown.will_sleep", reason), Lang.T("countdown.sleep_now")),
            CompletionAction.Hibernate => (Lang.T("countdown.will_hibernate", reason), Lang.T("countdown.hibernate_now")),
            CompletionAction.Shutdown when force => (Lang.T("countdown.will_shut_down_force", reason), Lang.T("countdown.shut_down_now")),
            _ => (Lang.T("countdown.will_shut_down", reason), Lang.T("countdown.shut_down_now")),
        };
        OthersText.Text = othersRunning == 1 ? Lang.T("countdown.other_running_one")
            : othersRunning > 1 ? Lang.T("countdown.other_running", othersRunning)
            : "";
        OthersText.Visibility = othersRunning > 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowLeft();

        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Normal, (_, _) => OnTick(), Dispatcher);
        _timer.Start();
        Closed += (_, _) => _timer.Stop();
    }

    /// <summary>Raised once, when the countdown ends or the user chooses to go ahead now.</summary>
    public event Action? Confirmed;

    void OnTick()
    {
        if (--_left <= 0) Confirm();
        else ShowLeft();
    }

    void ShowLeft() => CountText.Text = (_action, _left == 1) switch
    {
        (CompletionAction.Sleep, true) => Lang.T("countdown.sleep_in_one"),
        (CompletionAction.Sleep, false) => Lang.T("countdown.sleep_in", _left),
        (CompletionAction.Hibernate, true) => Lang.T("countdown.hibernate_in_one"),
        (CompletionAction.Hibernate, false) => Lang.T("countdown.hibernate_in", _left),
        (_, true) => Lang.T("countdown.shut_down_in_one"),
        _ => Lang.T("countdown.shut_down_in", _left),
    };

    void Now_Click(object sender, RoutedEventArgs e) => Confirm();

    // The window is shown without blocking, where IsCancel does nothing, so Cancel and Esc close it here.
    void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape) Close();
    }

    void Confirm()
    {
        if (_done) return;
        _done = true;
        _timer.Stop();
        Close();
        Confirmed?.Invoke();
    }
}
