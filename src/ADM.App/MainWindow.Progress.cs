using System.Windows;
using ADM.Core;

namespace ADM.App;

/// <summary>Each download's own progress window, and what happens when the download finishes.</summary>
public partial class MainWindow
{
    readonly Dictionary<Guid, ProgressWindow> _progress = new();

    int HiddenProgressCount => _progress.Values.Count(w => !w.IsVisible);

    /// <summary>
    /// Opens the progress window of a download the user started, as Options say: shown, waiting in the tray
    /// menu, or none. A download a queue started never gets one, so a queue of many files opens no windows.
    /// </summary>
    /// <param name="always">The user asked for this download's window, so it shows whatever Options say.</param>
    void ShowProgress(DownloadItem item, bool always = false)
    {
        if (item.StartedByQueue || !_manager.IsRunning(item)) return;
        if (_progress.TryGetValue(item.Id, out var open))
        {
            if (always || open.IsVisible)
            {
                open.Show();
                open.Activate();
            }
            return;
        }
        var view = Prefs.ProgressView;
        if (view == ProgressWindowView.Hidden && !always) return;
        var window = new ProgressWindow(_manager, this, item);
        _progress[item.Id] = window;
        window.Closed += (_, _) => _progress.Remove(item.Id);
        if (view == ProgressWindowView.Normal || always) window.Show();
    }

    /// <summary>A deleted download's window goes with it.</summary>
    void CloseProgress(DownloadItem item)
    {
        if (_progress.Remove(item.Id, out var window)) window.CloseWithoutPausing();
    }

    void ShowHiddenProgress()
    {
        foreach (var window in _progress.Values.Where(w => !w.IsVisible).ToList())
        {
            window.Show();
            window.Activate();
        }
    }

    /// <summary>
    /// Closes the download's window, shows the complete dialog unless that window said not to or a queue
    /// started the download, then does what the When done tab says.
    /// </summary>
    void OnDownloadCompleted(DownloadItem item)
    {
        // A grabber project's files say nothing each; the project reports when it finishes.
        if (item.GrabberProjectId != null) return;
        _progress.TryGetValue(item.Id, out var window);
        var showDialog = window?.ShowCompleteDialog ?? (Prefs.ShowCompleteDialog && !item.StartedByQueue);
        var exit = window?.ExitWhenDone == true;
        var turnOff = window?.TurnOff ?? CompletionAction.None;
        var force = window?.ForceClose == true;
        window?.CloseWithoutPausing();
        // A window that waited in the tray was never shown, so don't count on its Closed event.
        _progress.Remove(item.Id);

        if (exit)
        {
            // The dialog would close with ADM at once.
            _ = ((App)Application.Current).ExitAsync();
            return;
        }
        if (showDialog)
        {
            // Not modal, so several can be open, and none holds up the engine. It doesn't take the focus: a key
            // pressed in another program as it appears must not act on a downloaded file.
            new CompleteWindow(_manager, item).Show();
        }
        else Notify(Lang.T("common.download_complete"), item.FileName);
        if (turnOff != CompletionAction.None) ShowCountdown(Lang.T("main.countdown_download", item), turnOff, force);
    }
}
