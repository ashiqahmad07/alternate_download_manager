using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ADM.App;

/// <summary>What Windows shows for a file: its icon and type name, by extension, so the file needn't exist yet.</summary>
static class ShellInfo
{
    const uint ShgfiIcon = 0x100;
    const uint ShgfiLargeIcon = 0x0;
    const uint ShgfiSmallIcon = 0x1;
    const uint ShgfiTypeName = 0x400;
    const uint ShgfiUseFileAttributes = 0x10;
    const uint FileAttributeNormal = 0x80;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ShFileInfo
    {
        public IntPtr Icon;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SHGetFileInfo(string path, uint attributes, ref ShFileInfo info, uint size, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool DestroyIcon(IntPtr icon);

    public static (ImageSource? Icon, string TypeName) For(string fileName)
    {
        var info = new ShFileInfo();
        var name = string.IsNullOrEmpty(fileName) ? "file" : fileName;
        if (SHGetFileInfo(name, FileAttributeNormal, ref info, (uint)Marshal.SizeOf<ShFileInfo>(),
                ShgfiIcon | ShgfiLargeIcon | ShgfiTypeName | ShgfiUseFileAttributes) == IntPtr.Zero)
            return (null, Lang.T("shell.file"));
        return (TakeIcon(info.Icon), string.IsNullOrEmpty(info.TypeName) ? Lang.T("shell.file") : info.TypeName);
    }

    // The list asks for a row's icon each time the row is drawn; one per extension does for every file.
    static readonly Dictionary<string, ImageSource?> SmallIcons = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The small icon Explorer's lists show for a file of this name's extension.</summary>
    public static ImageSource? SmallIconFor(string fileName)
    {
        var extension = System.IO.Path.GetExtension(fileName ?? "");
        if (SmallIcons.TryGetValue(extension, out var cached)) return cached;
        var info = new ShFileInfo();
        ImageSource? icon = null;
        if (SHGetFileInfo("file" + extension, FileAttributeNormal, ref info, (uint)Marshal.SizeOf<ShFileInfo>(),
                ShgfiIcon | ShgfiSmallIcon | ShgfiUseFileAttributes) != IntPtr.Zero)
            icon = TakeIcon(info.Icon);
        return SmallIcons[extension] = icon;
    }

    /// <summary>A frozen copy of the icon, which is then destroyed.</summary>
    static ImageSource? TakeIcon(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return null;
        try
        {
            var icon = Imaging.CreateBitmapSourceFromHIcon(handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            icon.Freeze();
            return icon;
        }
        finally
        {
            DestroyIcon(handle);
        }
    }
}
