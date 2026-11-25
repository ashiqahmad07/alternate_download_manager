using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Threading;
using ADM.Core;

namespace ADM.App;

/// <summary>
/// ADM's icon in the notification area, through <c>Shell_NotifyIcon</c>, with a WPF menu that takes the app's look
/// like any other. Windows Forms' NotifyIcon loaded about 16 MB of modules for this alone. The icon's messages go to
/// a hidden window of its own: the main window has no handle while ADM starts in the tray. A plain Windows window,
/// not WPF's: WPF's starts up its drawing on the graphics card, Direct3D 11 and the card's driver among it, even for
/// a window that draws nothing.
/// </summary>
sealed class TrayIcon : IDisposable
{
    const int Id = 1;
    // WM_APP + 1.
    const int CallbackMessage = 0x8001;

    const int NimAdd = 0, NimModify = 1, NimDelete = 2, NimSetVersion = 4;
    const int NifMessage = 0x01, NifIcon = 0x02, NifTip = 0x04, NifInfo = 0x10, NifShowTip = 0x80;
    const int NiifInfo = 0x01;
    // The version that reports the keyboard as well as the mouse, and where on screen the icon is.
    const int NotifyIconVersion4 = 4;

    const int WmContextMenu = 0x007B;
    const int WmLButtonDblClk = 0x0203;
    const int NinKeySelect = 0x0401;
    const int WsExToolWindow = 0x80;
    static int _windows;
    const int SmCxSmIcon = 49;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct NotifyIconData
    {
        public int Size;
        public IntPtr Window;
        public int Id;
        public int Flags;
        public int CallbackMessage;
        public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public int State;
        public int StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public int TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public int InfoFlags;
        public Guid Item;
        public IntPtr BalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern bool Shell_NotifyIcon(int message, ref NotifyIconData data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int RegisterWindowMessage(string name);

    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr CreateIconFromResourceEx(byte[] bits, int length, bool icon, int version, int width, int height, int flags);

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr icon);

    delegate IntPtr WindowProcedure(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WindowClass
    {
        public int Size;
        public int Style;
        public WindowProcedure Procedure;
        public int ClassExtra;
        public int WindowExtra;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr Background;
        public string? MenuName;
        public string ClassName;
        public IntPtr SmallIcon;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern ushort RegisterClassEx(ref WindowClass windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool UnregisterClass(string className, IntPtr instance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateWindowEx(int extendedStyle, string className, string name, int style, int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);

    [DllImport("user32.dll")]
    static extern bool DestroyWindow(IntPtr window);

    [DllImport("user32.dll")]
    static extern IntPtr DefWindowProc(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr GetModuleHandle(string? name);

    readonly Func<ContextMenu> _createMenu;
    readonly Dispatcher _dispatcher;
    // Kept here, as Windows calls it for as long as the window lives and the collector can't see that.
    readonly WindowProcedure _procedure;
    readonly string _className;
    readonly IntPtr _window;
    readonly int _taskbarCreated;
    readonly IntPtr _icon;
    string _text;
    bool _disposed;

    /// <param name="createMenu">
    /// Makes the menu, on a right-click or the menu key while the icon has the keyboard. Made each time: a menu in no
    /// window isn't told when the app's look or font changes, so one kept would stay as it was first drawn.
    /// </param>
    public TrayIcon(Func<ContextMenu> createMenu, string text)
    {
        _createMenu = createMenu;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _text = text;
        _icon = CreateIcon(GetSystemMetrics(SmCxSmIcon));
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");

        _procedure = Procedure;
        _className = $"ADM.Tray.{Environment.ProcessId}.{Interlocked.Increment(ref _windows)}";
        var windowClass = new WindowClass
        {
            Size = Marshal.SizeOf<WindowClass>(),
            Procedure = _procedure,
            Instance = GetModuleHandle(null),
            ClassName = _className,
        };
        if (RegisterClassEx(ref windowClass) == 0) throw new Win32Exception();
        // Hidden, and off the taskbar and Alt+Tab even so. Still a top-level window, as only those hear that the taskbar came back.
        _window = CreateWindowEx(WsExToolWindow, _className, "ADM tray", 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, windowClass.Instance, IntPtr.Zero);
        if (_window == IntPtr.Zero) throw new Win32Exception();
        Add();
    }

    /// <summary>The menu opened last, if any.</summary>
    public ContextMenu? Menu { get; private set; }

    /// <summary>Whether Windows has the icon. Not yet while Explorer starts; it is added once the taskbar is there.</summary>
    public bool Shown { get; private set; }

    /// <summary>The hidden window the icon's messages go to.</summary>
    public IntPtr Handle => _window;

    /// <summary>A double-click on the icon, or Enter while it has the keyboard.</summary>
    public event Action? Activated;

    /// <summary>The tip over the icon. Windows keeps 127 characters of it.</summary>
    public string Text
    {
        get => _text;
        set
        {
            if (value == _text) return;
            _text = value;
            if (!Shown) return;
            var data = Data(NifTip | NifShowTip);
            Shell_NotifyIcon(NimModify, ref data);
        }
    }

    /// <summary>
    /// A notification from the icon, and whether Windows took it. Windows keeps 63 characters of the title and 255
    /// of the text, and decides how long it stays.
    /// </summary>
    public bool ShowBalloon(string title, string text)
    {
        if (!Shown) return false;
        var data = Data(NifInfo | NifTip | NifShowTip);
        data.InfoTitle = title;
        data.Info = text;
        data.InfoFlags = NiifInfo;
        return Shell_NotifyIcon(NimModify, ref data);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (Menu != null) Menu.IsOpen = false;
        if (Shown)
        {
            var data = Data(0);
            Shell_NotifyIcon(NimDelete, ref data);
        }
        Shown = false;
        DestroyWindow(_window);
        UnregisterClass(_className, GetModuleHandle(null));
        if (_icon != IntPtr.Zero) DestroyIcon(_icon);
    }

    void Add()
    {
        var data = Data(NifMessage | NifIcon | NifTip | NifShowTip);
        // A taskbar that announces itself while it still has the icon refuses it a second time; it takes the change.
        Shown = Shell_NotifyIcon(NimAdd, ref data) || Shell_NotifyIcon(NimModify, ref data);
        if (!Shown) return;
        data.TimeoutOrVersion = NotifyIconVersion4;
        Shell_NotifyIcon(NimSetVersion, ref data);
    }

    /// <summary>The icon's data with what this call changes; the rest of the fields are blank.</summary>
    NotifyIconData Data(int flags) => new()
    {
        Size = Marshal.SizeOf<NotifyIconData>(),
        Window = _window,
        Id = Id,
        Flags = flags,
        CallbackMessage = CallbackMessage,
        Icon = _icon,
        Tip = _text,
        Info = "",
        InfoTitle = "",
    };

    /// <summary>
    /// Called by Windows, from the dispatcher's message loop. What it does goes through the dispatcher, where an
    /// exception reaches the app's handler; thrown here, through Windows' frames, it would end the process.
    /// </summary>
    IntPtr Procedure(IntPtr window, int message, IntPtr wParam, IntPtr lParam)
    {
        if (message == CallbackMessage)
        {
            // With version 4, the event is in the low word; the high word is the icon's id.
            switch ((int)(lParam.ToInt64() & 0xFFFF))
            {
                // Each checks it's still wanted: ADM may have begun closing since the click.
                case WmContextMenu:
                    _dispatcher.BeginInvoke(() =>
                    {
                        if (!_disposed) ShowMenu();
                    });
                    break;
                case WmLButtonDblClk or NinKeySelect:
                    _dispatcher.BeginInvoke(() =>
                    {
                        if (!_disposed) Activated?.Invoke();
                    });
                    break;
            }
            return IntPtr.Zero;
        }
        if (message == _taskbarCreated)
        {
            // Explorer started again, or for the first time since ADM did: its new taskbar has no icon of ours.
            _dispatcher.BeginInvoke(() =>
            {
                if (!_disposed) Add();
            });
            return IntPtr.Zero;
        }
        return DefWindowProc(window, message, wParam, lParam);
    }

    void ShowMenu()
    {
        if (Menu != null) Menu.IsOpen = false;
        Menu = _createMenu();
        Menu.Placement = PlacementMode.MousePoint;
        Menu.IsOpen = true;
        // A menu from the notification area closes on a click elsewhere only while its window is in front, and hears
        // Esc and the arrow keys only once it has the keyboard: its window coming to the front doesn't give it that.
        if (PresentationSource.FromVisual(Menu) is HwndSource source) SetForegroundWindow(source.Handle);
        Menu.Focus();
    }

    /// <summary>The app icon's image nearest <paramref name="size"/>, as Windows needs it; none if the file can't be read.</summary>
    static IntPtr CreateIcon(int size)
    {
        var file = AppIcon.FileBytes();
        if (IconFile.Pick(file, size) is not (var offset, var length)) return IntPtr.Zero;
        // The version every icon image has had since Windows 3.
        return CreateIconFromResourceEx(file[offset..(offset + length)], length, true, 0x00030000, size, size, 0);
    }
}
