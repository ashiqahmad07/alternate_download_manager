namespace ADM.Core;

/// <summary>The totals of the downloads listed, or of those picked, which the main window shows when no single one is.</summary>
/// <param name="WithErrors">Failed, or waiting or paused after a try that failed.</param>
/// <param name="KnownSize">The sizes known, in bytes; a finished file of unknown size counts what was downloaded.</param>
/// <param name="UnknownSizes">How many unfinished downloads have no size yet.</param>
/// <param name="Downloaded">The bytes already downloaded.</param>
public sealed record ListSummary(int Count, int Finished, int Unfinished, int WithErrors, int Downloading, long KnownSize, int UnknownSizes, long Downloaded)
{
    public static ListSummary Of(IEnumerable<DownloadItem> items)
    {
        int count = 0, finished = 0, withErrors = 0, downloading = 0, unknownSizes = 0;
        long knownSize = 0, downloaded = 0;
        foreach (var item in items)
        {
            count++;
            var done = item.Status == DownloadStatus.Completed;
            if (done) finished++;
            if (item.Status == DownloadStatus.Downloading) downloading++;
            if (item.Status == DownloadStatus.Failed || (!done && item.Error != null)) withErrors++;

            var total = item.TotalBytes;
            var got = item.DownloadedBytes;
            if (total > 0) knownSize += total;
            else if (done) knownSize += got;
            else unknownSizes++;
            downloaded += done && total > 0 ? total : got;
        }
        return new(count, finished, count - finished, withErrors, downloading, knownSize, unknownSizes, downloaded);
    }
}
