using System.IO;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ADM.App;

/// <summary>The app icon, built in from <c>Assets\ADM.ico</c>, which <c>scripts\make-icons.py</c> makes from the logo.</summary>
static class AppIcon
{
    const string Resource = "ADM.App.Assets.ADM.ico";

    static ImageSource? _image;

    public static ImageSource ImageSource => _image ??= CreateImageSource();

    /// <summary>The icon file as it is, for the tray, which picks its own size out of it.</summary>
    public static byte[] FileBytes()
    {
        using var stream = Open();
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    static ImageSource CreateImageSource()
    {
        // A frame of the decoder, not a copy of one: WPF looks through its decoder's frames for the size each window needs.
        var frame = new IconBitmapDecoder(Open(), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        frame.Freeze();
        return frame;
    }

    static Stream Open() => Assembly.GetExecutingAssembly().GetManifestResourceStream(Resource)
        ?? throw new InvalidOperationException($"{Resource} is not built into the app.");
}
