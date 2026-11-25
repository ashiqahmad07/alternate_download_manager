using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ADM.Core;
using Microsoft.Win32;

namespace ADM.App;

/// <summary>
/// Picks the palette and the font from the settings, and swaps them while windows are open. Everything drawn uses
/// DynamicResource, so no window needs to know. Windows' own message boxes and file dialogs aren't drawn by WPF, and keep their look.
/// </summary>
public static class Theme
{
    const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    const int DwmUseImmersiveDarkMode = 20;
    // Windows 10 before version 2004 used this number for the same thing.
    const int DwmUseImmersiveDarkModeOld = 19;
    const int WmNcActivate = 0x0086;

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// The parts of WPF's own templates that are left in use take their colours from these. Set in code because a
    /// compiled XAML dictionary can't name another entry under a key like these. Under a contrast theme they stay as Windows has them.
    /// </summary>
    static readonly (ResourceKey System, string Palette)[] SystemBrushes =
    {
        (SystemColors.WindowBrushKey, "InputBg"),
        (SystemColors.WindowTextBrushKey, "Text"),
        (SystemColors.ControlBrushKey, "PanelBg"),
        (SystemColors.ControlTextBrushKey, "Text"),
        (SystemColors.ControlLightBrushKey, "BorderBrush"),
        (SystemColors.ControlLightLightBrushKey, "HoverBg"),
        (SystemColors.ControlDarkBrushKey, "ControlBorder"),
        (SystemColors.ControlDarkDarkBrushKey, "ControlBorder"),
        (SystemColors.ActiveBorderBrushKey, "BorderBrush"),
        (SystemColors.ScrollBarBrushKey, "PanelBg"),
        (SystemColors.GrayTextBrushKey, "DisabledText"),
        (SystemColors.HighlightBrushKey, "SelectionBg"),
        (SystemColors.HighlightTextBrushKey, "SelectionText"),
        (SystemColors.InactiveSelectionHighlightBrushKey, "InactiveSelectionBg"),
        (SystemColors.InactiveSelectionHighlightTextBrushKey, "InactiveSelectionText"),
        (SystemColors.MenuBrushKey, "MenuBg"),
        (SystemColors.MenuBarBrushKey, "WindowBg"),
        (SystemColors.MenuTextBrushKey, "Text"),
        (SystemColors.InfoBrushKey, "ToolTipBg"),
        (SystemColors.InfoTextBrushKey, "Text"),
        (DataGrid.FocusBorderBrushKey, "FocusBorder"),
    };

    static Settings? _settings;
    // What App.xaml starts with.
    static string _palette = "Light";
    static string _family = Appearance.DefaultFontFamily;
    static double _size = Appearance.DefaultFontSize;

    /// <summary>Reads Windows' app mode. On-screen checks replace it, so they can follow a change without changing the user's Windows.</summary>
    public static Func<bool> WindowsUsesLight { get; set; } = ReadWindowsUsesLight;

    /// <summary>How much the chosen font is larger than the default, which is what every window's size was laid out for.</summary>
    public static double FontScale => Math.Max(1, _size / Appearance.DefaultFontSize);

    /// <summary>Changes when the palette does. A binding that looks a colour up in code names this, so it asks again.</summary>
    public static int Revision { get; private set; }

    public static event EventHandler<PropertyChangedEventArgs>? StaticPropertyChanged;

    /// <summary>The palette or the font changed; for what is laid out in code, which is the toolbar.</summary>
    public static event Action? Changed;

    public static void Start(Settings settings)
    {
        _settings = settings;
        SetSystemBrushes(Application.Current.Resources.MergedDictionaries[0]);
        Apply();
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    public static void Stop() => SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;

    /// <summary>Brings the palette, the font and the title bars in line with the settings and with Windows.</summary>
    public static void Apply()
    {
        if (_settings is not { } settings) return;

        var palette = SystemParameters.HighContrast ? "HighContrast"
            : Appearance.IsDark(settings.Theme, WindowsUsesLight()) ? "Dark"
            : "Light";
        var family = Appearance.FontFamilyOf(settings.FontFamily);
        var size = Appearance.FontSizeOf(settings.FontSize);
        // Windows announces many changes that have nothing to do with this, and redrawing every window for them would show.
        if (palette == _palette && family == _family && size == _size) return;

        if (palette != _palette)
        {
            _palette = palette;
            var loaded = new ResourceDictionary { Source = new Uri($"pack://application:,,,/ADM;component/Themes/{palette}.xaml") };
            if (palette != "HighContrast") SetSystemBrushes(loaded);
            Application.Current.Resources.MergedDictionaries[0] = loaded;
            Revision++;
            StaticPropertyChanged?.Invoke(null, new PropertyChangedEventArgs(nameof(Revision)));
            foreach (Window window in Application.Current.Windows) ApplyTitleBar(window);
        }
        if (family != _family || size != _size)
        {
            (_family, _size) = (family, size);
            ApplyFont();
            foreach (Window window in Application.Current.Windows) (window as ThemedWindow)?.FitToFont();
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// Tells Windows which title bar the window needs. Windows 10 repaints only the title's text until the frame next
    /// changes between active and inactive, so a window already on screen goes through that change, and ends as it was.
    /// </summary>
    public static void ApplyTitleBar(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        int dark = _palette == "Dark" ? 1 : 0;
        if (DwmSetWindowAttribute(handle, DwmUseImmersiveDarkMode, ref dark, sizeof(int)) != 0)
            DwmSetWindowAttribute(handle, DwmUseImmersiveDarkModeOld, ref dark, sizeof(int));
        if (window.IsVisible)
        {
            SendMessage(handle, WmNcActivate, window.IsActive ? IntPtr.Zero : new IntPtr(1), IntPtr.Zero);
            SendMessage(handle, WmNcActivate, window.IsActive ? new IntPtr(1) : IntPtr.Zero, IntPtr.Zero);
        }
    }

    static void ApplyFont()
    {
        var resources = Application.Current.Resources;
        resources["AppFontFamily"] = new FontFamily(_family);
        resources["AppFontSize"] = _size;
        resources["AppFontSizeSmall"] = Math.Max(Appearance.MinFontSize, _size - 2);
        resources["AppFontSizeLarge"] = _size + 1;
        resources["AppFontSizeTitle"] = _size + 3;
        resources["AppRowHeight"] = Math.Ceiling(_size * 2);
    }

    static void SetSystemBrushes(ResourceDictionary palette)
    {
        foreach (var (system, name) in SystemBrushes) palette[system] = palette[name];
    }

    // At a low priority, so that WPF has taken in the same change, which is where it learns of a contrast theme.
    static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e) =>
        Application.Current?.Dispatcher.InvokeAsync(Apply, DispatcherPriority.Background);

    static bool ReadWindowsUsesLight()
    {
        using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
        // Windows without app modes has no value, and those windows are light.
        return key?.GetValue("AppsUseLightTheme") is not int value || value != 0;
    }
}
