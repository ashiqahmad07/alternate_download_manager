using System.Globalization;

namespace ADM.Core;

/// <summary>
/// The words the engine uses for a download's status, which the windows show as they are. The app replaces them once,
/// with its language's texts. The engine's error messages aren't among them, and stay in English.
/// </summary>
public sealed class StatusWords
{
    public static StatusWords Current { get; set; } = new();

    public string Queued { get; init; } = "Queued";
    public string Downloading { get; init; } = "Downloading";
    public string Recording { get; init; } = "Recording";
    public string Paused { get; init; } = "Paused";
    public string Completed { get; init; } = "Completed";
    public string Checking { get; init; } = "Checking for changes";
    public string Scanning { get; init; } = "Scanning for viruses";

    // These three take the error as {0}.
    public string ErrorOf { get; init; } = "Error: {0}";
    public string RetryingOf { get; init; } = "Retrying: {0}";
    public string PausedOf { get; init; } = "Paused: {0}";

    public string LastComplete { get; init; } = "Complete";
    public string LastFailed { get; init; } = "Failed";
    public string LastWaiting { get; init; } = "Waiting in the queue";

    public string Hashing { get; init; } = "Checking SHA-256";
    public string HashMatches { get; init; } = "SHA-256 matches";
    public string HashNotChecked { get; init; } = "SHA-256 not checked";
    /// <summary>Takes the name the file was kept under as {0}.</summary>
    public string HashMismatchOf { get; init; } = "The file's SHA-256 doesn't match, so it was kept as {0}.";

    public string UnknownSize { get; init; } = "Unknown";

    public string ResumeYes { get; init; } = "Yes";
    public string ResumeNo { get; init; } = "No";

    public string FileChanged { get; init; } = "The file changed on the server; downloading it again.";
    public string MovingIntoPlace { get; init; } = "Moving the file into place";
    public string UsingBrowserProxy { get; init; } = "Using the browser's proxy";
    public string WaitingForNetwork { get; init; } = "Waiting for network";
    public string WaitingForVideo { get; init; } = "Waiting for the site to make the video available";
    public string ReadingVideo { get; init; } = "Reading the video's page";
    public string ScriptNotLoaded { get; init; } = "The proxy script could not be loaded; trying again";
    /// <summary>Takes the try's number as {0}.</summary>
    public string ReconnectingOf { get; init; } = "Reconnecting ({0})";

    public string Reconnecting(int attempt) => Fill(ReconnectingOf, attempt);

    public string HashMismatch(string keptAs) => Fill(HashMismatchOf, keptAs);

    public string StatusOf(DownloadItem item) => item.Status switch
    {
        DownloadStatus.Completed when item.IsChecking => Checking,
        DownloadStatus.Completed when item.ScanResult != null => item.ScanResult,
        DownloadStatus.Completed => Completed,
        DownloadStatus.Failed => Fill(ErrorOf, item.Error),
        DownloadStatus.Downloading when item.IsHashing => Hashing,
        DownloadStatus.Downloading when item.IsScanning => Scanning,
        DownloadStatus.Downloading => item.StatusNote ?? (item.IsLive ? Recording : Downloading),
        DownloadStatus.Queued when item.Error != null => Fill(RetryingOf, item.Error),
        DownloadStatus.Paused when item.Error != null => Fill(PausedOf, item.Error),
        DownloadStatus.Queued => Queued,
        _ => Paused,
    };

    /// <summary>How the last try ended, for the Properties dialog.</summary>
    public string LastResultOf(DownloadItem item) => item.Status switch
    {
        DownloadStatus.Completed => item.ScanResult ?? LastComplete,
        DownloadStatus.Failed => item.Error ?? LastFailed,
        DownloadStatus.Queued => LastWaiting,
        _ => StatusOf(item),
    };

    public string ResumeOf(DownloadItem item) => item.Resumable switch { true => ResumeYes, false => ResumeNo, null => "" };

    static string Fill(string text, object? value) => string.Format(CultureInfo.CurrentCulture, text, value);
}
