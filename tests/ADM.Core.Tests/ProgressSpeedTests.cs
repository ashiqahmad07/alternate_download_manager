using ADM.Core;

namespace ADM.Core.Tests;

/// <summary>
/// The speed a running download shows: what arrived over the last second, however often it is sampled, so a
/// window that samples ten times a second shows a number that moves with the transfer.
/// </summary>
public sealed class ProgressSpeedTests
{
    static readonly DateTime T0 = new(2026, 11, 20, 12, 0, 0);

    static DownloadItem Running() => new() { FileName = "f.bin", Status = DownloadStatus.Downloading };

    /// <summary>Ticks every 100 ms for <paramref name="seconds"/>, with <paramref name="bytesPerSecond"/> arriving between ticks.</summary>
    static DateTime Run(DownloadItem item, DateTime from, double seconds, long bytesPerSecond)
    {
        var at = from;
        for (int i = 0; i < (int)Math.Round(seconds * 10); i++)
        {
            item.AddDownloaded(bytesPerSecond / 10);
            at = at.AddMilliseconds(100);
            item.Tick(at);
        }
        return at;
    }

    [Fact]
    public void A_stall_reads_zero_within_a_second()
    {
        var item = Running();
        item.Tick(T0);
        var at = Run(item, T0, 2, 1_000_000);

        Run(item, at, 1, 0);

        Assert.Equal(0, item.Speed);
    }

    [Fact]
    public void A_new_speed_shows_in_full_within_a_second()
    {
        var item = Running();
        item.Tick(T0);
        var at = Run(item, T0, 2, 1_000_000);

        Run(item, at, 1, 2_000_000);

        Assert.InRange(item.Speed, 1_999_999, 2_000_001);
    }

    [Fact]
    public void Samples_at_two_rates_read_the_same_second()
    {
        // The list samples twice a second and the progress window ten times; both see one speed.
        var item = Running();
        item.Tick(T0);
        var at = Run(item, T0, 2, 1_000_000);
        item.AddDownloaded(500_000);
        item.Tick(at.AddMilliseconds(500));

        Assert.InRange(item.Speed, 999_999, 1_000_001);
    }

    [Fact]
    public void Progress_taken_back_reads_no_negative_speed_and_then_recovers()
    {
        // A part that fails takes its bytes off again.
        var item = Running();
        item.Tick(T0);
        var at = Run(item, T0, 2, 1_000_000);
        item.AddDownloaded(-600_000);
        at = at.AddMilliseconds(100);
        item.Tick(at);
        Assert.True(item.Speed >= 0, $"speed {item.Speed}");

        Run(item, at, 1.5, 1_000_000);

        Assert.InRange(item.Speed, 999_999, 1_000_001);
    }
}
