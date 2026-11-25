using System.Windows;
using System.Windows.Media;
using ADM.Core;

namespace ADM.App;

/// <summary>Draws the file as a strip: downloaded ranges filled, live connections highlighted.</summary>
public sealed class SegmentBar : FrameworkElement
{
    public static readonly DependencyProperty ItemProperty = DependencyProperty.Register(
        nameof(Item), typeof(DownloadItem), typeof(SegmentBar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    static readonly DependencyProperty TrackProperty = RegisterBrush(nameof(Track));
    static readonly DependencyProperty DoneProperty = RegisterBrush(nameof(Done));
    static readonly DependencyProperty LiveProperty = RegisterBrush(nameof(Live));
    static readonly DependencyProperty DividerProperty = RegisterBrush(nameof(Divider));

    Brush Track => (Brush)GetValue(TrackProperty);
    Brush Done => (Brush)GetValue(DoneProperty);
    Brush Live => (Brush)GetValue(LiveProperty);
    Brush Divider => (Brush)GetValue(DividerProperty);

    public SegmentBar()
    {
        // Resource references, so the strip draws itself again when the palette changes.
        SetResourceReference(TrackProperty, "TrackBg");
        SetResourceReference(DoneProperty, "Accent");
        SetResourceReference(LiveProperty, "LiveFill");
        SetResourceReference(DividerProperty, "WindowBg");
    }

    public DownloadItem? Item
    {
        get => (DownloadItem?)GetValue(ItemProperty);
        set => SetValue(ItemProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Track, null, new Rect(0, 0, w, h));
        if (Item is not { } item) return;

        // A finished download, a file of unknown size, or a stream not run in this session: the progress so far.
        if (item.StripNow() is not { Total: > 0 } strip)
        {
            dc.DrawRectangle(Done, null, new Rect(0, 0, w * item.Progress / 100, h));
            return;
        }

        var scale = w / strip.Total;
        var divider = new Pen(Divider, 1);
        foreach (var piece in strip.Pieces)
        {
            double x = piece.Start * scale;
            double filled = Math.Min(piece.Filled, piece.Length) * scale;
            dc.DrawRectangle(Done, null, new Rect(x, 0, filled, h));
            // Where the connection is at work, however narrow its piece.
            if (piece.Live) dc.DrawRectangle(Live, null, new Rect(x + filled, 0, Math.Clamp(w - x - filled, 0, 3), h));
            if (piece.Marked) dc.DrawLine(divider, new Point(x, 0), new Point(x, h));
        }
    }

    static DependencyProperty RegisterBrush(string name) => DependencyProperty.Register(
        name, typeof(Brush), typeof(SegmentBar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
}
