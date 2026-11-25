using System.Windows;

namespace ADM.App;

/// <summary>
/// Every ADM window. A style for Window doesn't reach classes derived from it, so the colours and the font
/// are set here, and the title bar is set before the window first shows.
/// </summary>
public class ThemedWindow : Window
{
    Size? _designedMin;
    double _scale = 1;

    public ThemedWindow()
    {
        SetResourceReference(BackgroundProperty, "WindowBg");
        SetResourceReference(ForegroundProperty, "Text");
        SetResourceReference(FontFamilyProperty, "AppFontFamily");
        SetResourceReference(FontSizeProperty, "AppFontSize");
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Theme.ApplyTitleBar(this);
        FitToFont();
    }

    /// <summary>
    /// A window laid out for the default font would cut larger text off, so it grows in step with the font, as far as
    /// the screen allows. Going by the size it has now keeps a size the user chose in proportion.
    /// </summary>
    public void FitToFont()
    {
        _designedMin ??= new Size(MinWidth, MinHeight);
        var scale = Theme.FontScale;
        var change = scale / _scale;
        _scale = scale;
        var area = SystemParameters.WorkArea;
        // Read first: raising a minimum resizes the window at once.
        var (width, height) = (Width, Height);

        MinWidth = Math.Min(_designedMin.Value.Width * scale, area.Width);
        MinHeight = Math.Min(_designedMin.Value.Height * scale, area.Height);
        if (!double.IsNaN(width)) Width = Math.Min(width * change, area.Width);
        // A height that follows the content has no size to scale, only a limit.
        if (SizeToContent == SizeToContent.Height) MaxHeight = area.Height;
        else if (!double.IsNaN(height)) Height = Math.Min(height * change, area.Height);

        // A window already up can grow past the edge of the screen.
        if (!IsVisible) return;
        var right = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth;
        var bottom = SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight;
        if (Left >= SystemParameters.VirtualScreenLeft && Left + ActualWidth > right) Left = Math.Max(SystemParameters.VirtualScreenLeft, right - ActualWidth);
        if (Top >= SystemParameters.VirtualScreenTop && Top + ActualHeight > bottom) Top = Math.Max(SystemParameters.VirtualScreenTop, bottom - ActualHeight);
    }
}
