using System.Collections;
using System.Runtime.InteropServices;

namespace ADM.Core;

/// <summary>What the download list can be sorted by.</summary>
public enum DownloadSortKey
{
    OrderAdded, Queue, FileName, Size, Progress, Status, TimeLeft, TransferRate, Resume, LastTry, Description, Category, SavePath, Referer, ParentPage,
}

/// <summary>
/// Orders downloads by one key, in either direction. A value not known yet (no size, no time left, no last
/// try, an empty text) goes last either way, and downloads equal on the key keep newest first.
/// </summary>
public static class DownloadSort
{
    /// <remarks>The comparer is also a non-generic <see cref="IComparer"/>, for a WPF view's CustomSort.</remarks>
    public static IComparer<DownloadItem> Comparer(DownloadSortKey key, bool descending) => new ByKey(key, descending);

    /// <summary>
    /// Whether <paramref name="items"/> are already in the comparer's order. The list checks this before sorting
    /// again by a column whose values move as transfers run: most seconds nothing has changed places, and sorting
    /// would rebuild every row it shows.
    /// </summary>
    public static bool InOrder(IEnumerable<DownloadItem> items, IComparer<DownloadItem> comparer)
    {
        DownloadItem? previous = null;
        foreach (var item in items)
        {
            if (previous != null && comparer.Compare(previous, item) > 0) return false;
            previous = item;
        }
        return true;
    }

    sealed class ByKey(DownloadSortKey key, bool descending) : IComparer<DownloadItem>, IComparer
    {
        public int Compare(DownloadItem? a, DownloadItem? b)
        {
            if (ReferenceEquals(a, b)) return 0;
            if (a == null) return 1;
            if (b == null) return -1;
            bool knownA = IsKnown(key, a), knownB = IsKnown(key, b);
            if (knownA != knownB) return knownA ? -1 : 1;
            if (knownA)
            {
                var order = CompareKnown(key, a, b);
                if (order != 0) return descending ? -order : order;
            }
            // Newest first, then by id, whichever way the key goes.
            var added = b.AddedAt.CompareTo(a.AddedAt);
            return added != 0 ? added : a.Id.CompareTo(b.Id);
        }

        int IComparer.Compare(object? a, object? b) => Compare(a as DownloadItem, b as DownloadItem);
    }

    static bool IsKnown(DownloadSortKey key, DownloadItem item) => key switch
    {
        DownloadSortKey.Queue => item.QueuePosition > 0,
        DownloadSortKey.FileName => item.FileName.Length > 0,
        DownloadSortKey.Size => item.TotalBytes >= 0,
        DownloadSortKey.TimeLeft => item.SecondsLeft != null,
        DownloadSortKey.TransferRate => item.Status == DownloadStatus.Downloading,
        DownloadSortKey.Resume => item.Resumable != null,
        DownloadSortKey.LastTry => item.LastTryAt != null,
        DownloadSortKey.Description => !string.IsNullOrEmpty(item.Description),
        DownloadSortKey.Category => !string.IsNullOrEmpty(item.Category),
        DownloadSortKey.SavePath => !string.IsNullOrEmpty(item.Directory),
        DownloadSortKey.Referer => !string.IsNullOrEmpty(item.Referrer),
        DownloadSortKey.ParentPage => !string.IsNullOrEmpty(item.PageUrl),
        _ => true,
    };

    static int CompareKnown(DownloadSortKey key, DownloadItem a, DownloadItem b) => key switch
    {
        DownloadSortKey.OrderAdded => a.AddedAt.CompareTo(b.AddedAt),
        DownloadSortKey.Queue => a.QueuePosition != b.QueuePosition
            ? a.QueuePosition.CompareTo(b.QueuePosition)
            : string.CompareOrdinal(a.QueueId, b.QueueId),
        DownloadSortKey.FileName => Logical(a.FileName, b.FileName),
        DownloadSortKey.Size => a.TotalBytes.CompareTo(b.TotalBytes),
        DownloadSortKey.Progress => a.Progress.CompareTo(b.Progress),
        DownloadSortKey.Status => Rank(a.Status).CompareTo(Rank(b.Status)),
        DownloadSortKey.TimeLeft => a.SecondsLeft!.Value.CompareTo(b.SecondsLeft!.Value),
        DownloadSortKey.TransferRate => a.Speed.CompareTo(b.Speed),
        DownloadSortKey.Resume => a.Resumable!.Value.CompareTo(b.Resumable!.Value),
        DownloadSortKey.LastTry => a.LastTryAt!.Value.CompareTo(b.LastTryAt!.Value),
        DownloadSortKey.Description => Logical(a.Description!, b.Description!),
        DownloadSortKey.Category => Logical(a.Category, b.Category),
        DownloadSortKey.SavePath => Logical(a.FullPath, b.FullPath),
        DownloadSortKey.Referer => Logical(a.Referrer!, b.Referrer!),
        DownloadSortKey.ParentPage => Logical(a.PageUrl!, b.PageUrl!),
        _ => 0,
    };

    // Active downloads first.
    static int Rank(DownloadStatus status) => status switch
    {
        DownloadStatus.Downloading => 0,
        DownloadStatus.Queued => 1,
        DownloadStatus.Paused => 2,
        DownloadStatus.Failed => 3,
        _ => 4,
    };

    /// <summary>As Explorer orders names: ignoring case, with runs of digits compared as numbers, so "file2" comes before "file10".</summary>
    static int Logical(string a, string b) => StrCmpLogicalW(a, b);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern int StrCmpLogicalW(string a, string b);
}
