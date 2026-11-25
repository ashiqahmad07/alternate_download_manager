using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ADM.App;

/// <summary>Windows' own "Open with" dialog: the user picks a program, which then opens the file.</summary>
static class OpenWith
{
    const int OaifAllowRegistration = 0x1;
    const int OaifExec = 0x4;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct OpenAsInfo
    {
        public string File;
        public string? Class;
        public int Flags;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHOpenWithDialog(IntPtr parent, ref OpenAsInfo info);

    public static void Show(Window owner, string path)
    {
        var info = new OpenAsInfo { File = path, Flags = OaifAllowRegistration | OaifExec };
        // A cancelled dialog returns an error code; nothing else to do then.
        SHOpenWithDialog(new WindowInteropHelper(owner).Handle, ref info);
    }
}
