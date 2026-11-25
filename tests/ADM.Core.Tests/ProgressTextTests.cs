using ADM.Core;

namespace ADM.Core.Tests;

/// <summary>
/// The percentage a download shows, in the list and its window: blank while neither its size nor a stream's count of
/// parts is known, as 0% would say nothing has come (seen 2026-11-25: "6.8 MB (0.0%)" for a file with no size).
/// </summary>
public sealed class ProgressTextTests
{
    [Fact]
    public void A_file_of_unknown_size_has_no_percentage_until_it_ends()
    {
        var item = new DownloadItem { FileName = "f.bin", Status = DownloadStatus.Downloading, TotalBytes = -1 };
        item.AddDownloaded(7_000_000);

        Assert.False(item.ProgressKnown);
        Assert.Equal("", item.ProgressText);

        item.Status = DownloadStatus.Completed;

        Assert.True(item.ProgressKnown);
        Assert.Equal("100%", item.ProgressText);
    }

    [Fact]
    public void A_known_size_or_a_streams_parts_give_a_percentage()
    {
        var sized = new DownloadItem { FileName = "f.bin", Status = DownloadStatus.Paused, TotalBytes = 4_000_000 };
        sized.AddDownloaded(1_000_000);
        var stream = new DownloadItem { FileName = "v.mp4", Kind = DownloadKind.Hls, Status = DownloadStatus.Paused, TotalBytes = -1, HlsSegmentCount = 8, HlsSegmentsDone = 2 };

        Assert.Equal("25.0%", sized.ProgressText);
        Assert.Equal("25.0%", stream.ProgressText);
    }

    [Fact]
    public void A_stream_with_no_parts_counted_yet_has_no_percentage()
    {
        var stream = new DownloadItem { FileName = "v.mp4", Kind = DownloadKind.Hls, Status = DownloadStatus.Downloading, TotalBytes = -1 };

        Assert.Equal("", stream.ProgressText);
    }
}
