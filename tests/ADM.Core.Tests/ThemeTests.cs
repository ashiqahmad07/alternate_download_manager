using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ADM.Core;

namespace ADM.Core.Tests;

/// <summary>The look's settings, and checks on the app's XAML: the app's windows can't be loaded from here, but their files can be read.</summary>
public class ThemeTests
{
    static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    static readonly string AppFolder = Path.Combine(RepoRoot(), "src", "ADM.App");

    /// <summary>Every text colour that can sit on an ordinary surface.</summary>
    static readonly string[] SurfaceText = { "Text", "MutedText", "AccentText", "ErrorText", "SyncText", "WarningText", "DisabledText" };

    static readonly string[] Surfaces = { "WindowBg", "PanelBg", "InputBg", "MenuBg", "HeaderBg", "AltRowBg" };

    /// <summary>Text on the surfaces only some text reaches.</summary>
    static readonly (string Text, string Background)[] OtherPairs =
    {
        ("Text", "ButtonBg"),
        ("Text", "ToolTipBg"),
        ("Text", "TrackBg"),
        ("Text", "AccentSoft"),
        // The list's progress bars, filled by the download's state, with the percentage on them.
        ("Text", "DoneFill"),
        ("Text", "FailedFill"),
        ("Text", "WarningFill"),
        ("Text", "PausedFill"),
        ("HoverText", "HoverBg"),
        ("HoverText", "PressedBg"),
        ("AccentText", "ButtonBg"),
        ("AccentText", "HoverBg"),
        ("AccentText", "PressedBg"),
        ("DisabledText", "DisabledBg"),
        ("SelectionText", "SelectionBg"),
        ("InactiveSelectionText", "InactiveSelectionBg"),
    };

    /// <summary>Lines and marks that show where a control is; these need 3:1.</summary>
    static readonly (string Mark, string Background)[] ControlPairs =
    {
        ("ControlBorder", "WindowBg"),
        ("ControlBorder", "PanelBg"),
        ("FocusBorder", "WindowBg"),
        ("FocusBorder", "InputBg"),
        ("Accent", "WindowBg"),
        ("Accent", "TrackBg"),
        ("SelectionText", "Accent"),
        ("ScrollThumb", "WindowBg"),
        ("ScrollThumb", "PanelBg"),
    };

    // ---- Settings

    [Fact]
    public void Older_state_loads_the_default_look()
    {
        var path = Path.Combine(Path.GetTempPath(), "adm-tests", Guid.NewGuid().ToString("N"), "state.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """{ "Settings": { "DefaultConnections": 4 } }""");
        try
        {
            var s = Store.Load(path).Settings;

            Assert.Equal(ThemeChoice.FollowWindows, s.Theme);
            Assert.Equal("Segoe UI", s.FontFamily);
            Assert.Equal(13, s.FontSize);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }

    [Fact]
    public void The_look_survives_a_save()
    {
        var path = Path.Combine(Path.GetTempPath(), "adm-tests", Guid.NewGuid().ToString("N"), "state.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            var state = new AppState();
            state.Settings.Theme = ThemeChoice.Dark;
            state.Settings.FontFamily = "Consolas";
            state.Settings.FontSize = 18;

            Store.Save(path, state);
            var s = Store.Load(path).Settings;

            Assert.Equal(ThemeChoice.Dark, s.Theme);
            Assert.Equal("Consolas", s.FontFamily);
            Assert.Equal(18, s.FontSize);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }

    [Theory]
    [InlineData(ThemeChoice.Light, true, false)]
    [InlineData(ThemeChoice.Light, false, false)]
    [InlineData(ThemeChoice.Dark, true, true)]
    [InlineData(ThemeChoice.Dark, false, true)]
    [InlineData(ThemeChoice.FollowWindows, true, false)]
    [InlineData(ThemeChoice.FollowWindows, false, true)]
    public void The_choice_decides_dark_and_following_asks_windows(ThemeChoice choice, bool windowsUsesLight, bool dark) =>
        Assert.Equal(dark, Appearance.IsDark(choice, windowsUsesLight));

    [Theory]
    [InlineData(8, 8)]
    [InlineData(13, 13)]
    [InlineData(24, 24)]
    [InlineData(7.9, 8)]
    [InlineData(25, 24)]
    [InlineData(0, 8)]
    [InlineData(-5, 8)]
    [InlineData(double.NaN, 13)]
    [InlineData(double.PositiveInfinity, 13)]
    public void A_saved_font_size_stays_in_range(double saved, double used) =>
        Assert.Equal(used, Appearance.FontSizeOf(saved));

    [Theory]
    [InlineData("Consolas", "Consolas")]
    [InlineData("  Consolas ", "Consolas")]
    [InlineData("", "Segoe UI")]
    [InlineData("   ", "Segoe UI")]
    [InlineData(null, "Segoe UI")]
    public void A_blank_font_family_means_the_default(string? saved, string used) =>
        Assert.Equal(used, Appearance.FontFamilyOf(saved));

    // ---- Palettes

    [Fact]
    public void Every_palette_has_the_same_keys()
    {
        var light = Keys("Light");
        Assert.NotEmpty(light);
        foreach (var other in new[] { "Dark", "HighContrast" })
            Assert.Equal(light.Order(), Keys(other).Order());
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void Text_has_at_least_4_5_to_1_contrast(string palette)
    {
        var colours = Colours(palette);
        var pairs = SurfaceText.SelectMany(text => Surfaces.Select(background => (text, background))).Concat(OtherPairs);
        var failed = pairs
            .Select(p => (p.Item1, p.Item2, Ratio: Contrast(colours[p.Item1], colours[p.Item2])))
            .Where(p => p.Ratio < 4.5)
            .Select(p => $"{p.Item1} on {p.Item2}: {p.Ratio:0.00}")
            .ToList();
        Assert.True(failed.Count == 0, string.Join("; ", failed));
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void Controls_have_at_least_3_to_1_contrast(string palette)
    {
        var colours = Colours(palette);
        var failed = ControlPairs
            .Select(p => (p.Mark, p.Background, Ratio: Contrast(colours[p.Mark], colours[p.Background])))
            .Where(p => p.Ratio < 3)
            .Select(p => $"{p.Mark} on {p.Background}: {p.Ratio:0.00}")
            .ToList();
        Assert.True(failed.Count == 0, string.Join("; ", failed));
    }

    [Fact]
    public void System_colours_come_from_the_palette()
    {
        var keys = Keys("Light");
        var mapped = Regex.Matches(File.ReadAllText(Path.Combine(AppFolder, "Theme.cs")), @"\(SystemColors\.(\w+BrushKey), ""(\w+)""\)")
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);
        foreach (var key in new[] { "WindowBrushKey", "ControlTextBrushKey", "HighlightBrushKey", "HighlightTextBrushKey", "InactiveSelectionHighlightBrushKey", "InactiveSelectionHighlightTextBrushKey", "GrayTextBrushKey" })
            Assert.True(mapped.ContainsKey(key), $"{key} isn't set");
        foreach (var name in mapped.Values)
            Assert.Contains(name, keys);
    }

    [Fact]
    public void High_contrast_leaves_the_system_colours_alone()
    {
        Assert.DoesNotContain(
            Palette("HighContrast").Elements().Select(e => (string?)e.Attribute(Xaml + "Key")),
            key => key?.StartsWith("{x:Static") == true);
    }

    // ---- The app's files

    [Fact]
    public void Every_window_is_a_themed_window()
    {
        var windows = WindowFiles().ToList();
        Assert.True(windows.Count >= 20, $"found only {windows.Count} windows");
        foreach (var file in windows)
        {
            var root = XDocument.Load(file).Root!;
            Assert.True(root.Name.LocalName == "ThemedWindow", $"{Path.GetFileName(file)} is a {root.Name.LocalName}");
            var code = File.ReadAllText(file + ".cs");
            Assert.False(Regex.IsMatch(code, @"partial class \w+ : Window\b"), $"{Path.GetFileName(file)}.cs still derives from Window");
        }
    }

    [Fact]
    public void No_window_names_a_colour_of_its_own()
    {
        var colourProperty = new Regex(@"^(\w+\.)?(Background|Foreground|BorderBrush|Fill|Stroke|\w+Brush|AlternatingRowBackground|RowBackground)$");
        foreach (var file in XamlFiles().Where(f => !IsPalette(f)))
        {
            foreach (var element in XDocument.Load(file).Descendants())
            {
                foreach (var attribute in element.Attributes().Where(a => colourProperty.IsMatch(a.Name.LocalName)))
                    Assert.True(attribute.Value.StartsWith("{") || attribute.Value == "Transparent",
                        $"{Path.GetFileName(file)}: {element.Name.LocalName} sets {attribute.Name.LocalName}=\"{attribute.Value}\"");
                if (element.Name.LocalName == "Setter" && colourProperty.IsMatch((string?)element.Attribute("Property") ?? "")
                    && element.Attribute("Value") is { } value)
                    Assert.True(value.Value.StartsWith("{") || value.Value == "Transparent",
                        $"{Path.GetFileName(file)}: a setter sets {(string?)element.Attribute("Property")}=\"{value.Value}\"");
                Assert.False(element.Name.LocalName is "SolidColorBrush" or "LinearGradientBrush" or "RadialGradientBrush" or "Color",
                    $"{Path.GetFileName(file)} defines a {element.Name.LocalName}");
            }
        }
    }

    [Fact]
    public void Code_draws_with_the_palette_too()
    {
        // Theme.cs as well, now that the tray menu is WPF's and takes the palette as the other menus do.
        var fixedColour = new Regex(@"Color\.FromRgb|Color\.FromArgb|\bBrushes\.\w+|\bColors\.\w+");
        foreach (var file in Directory.GetFiles(AppFolder, "*.cs"))
            Assert.False(fixedColour.IsMatch(File.ReadAllText(file)), $"{Path.GetFileName(file)} has a fixed colour");
    }

    [Fact]
    public void Palette_keys_are_found_at_run_time_and_exist()
    {
        var keys = Keys("Light").Concat(new[] { "AppFontFamily", "AppFontSize", "AppFontSizeSmall", "AppFontSizeLarge", "AppFontSizeTitle", "AppRowHeight" }).ToHashSet();
        var reference = new Regex(@"\{(StaticResource|DynamicResource) (\w+)\}");
        foreach (var file in XamlFiles().Where(f => !IsPalette(f)))
        {
            foreach (Match match in reference.Matches(File.ReadAllText(file)))
            {
                var key = match.Groups[2].Value;
                if (match.Groups[1].Value == "StaticResource")
                    Assert.False(keys.Contains(key), $"{Path.GetFileName(file)}: {match.Value} wouldn't change with the theme");
                else
                    Assert.True(keys.Contains(key), $"{Path.GetFileName(file)}: {match.Value} is in no palette");
            }
        }
    }

    [Fact]
    public void Local_styles_build_on_the_theme()
    {
        // A style without BasedOn replaces the theme's style for that control, and its template with it.
        var themed = XDocument.Load(Path.Combine(AppFolder, "Themes", "Controls.xaml")).Root!.Elements()
            .Where(e => e.Name.LocalName == "Style" && e.Attribute(Xaml + "Key") == null)
            .Select(e => (string)e.Attribute("TargetType")!)
            .ToHashSet();
        Assert.Contains("Button", themed);
        foreach (var file in XamlFiles().Where(f => Path.GetFileName(f) != "Controls.xaml" && !IsPalette(f)))
        {
            foreach (var style in XDocument.Load(file).Descendants().Where(e => e.Name.LocalName == "Style"))
            {
                var target = (string?)style.Attribute("TargetType");
                if (target == null || !themed.Contains(target)) continue;
                Assert.True(style.Attribute("BasedOn") != null, $"{Path.GetFileName(file)}: a {target} style has no BasedOn");
            }
        }
    }

    [Fact]
    public void Cells_edit_in_the_themes_text_box()
    {
        // A text column's editing box has a style of its own, which the theme's text box style doesn't reach.
        foreach (var file in XamlFiles())
        {
            foreach (var grid in XDocument.Load(file).Descendants().Where(e => e.Name.LocalName == "DataGrid" && (string?)e.Attribute("IsReadOnly") != "True"))
            {
                foreach (var column in grid.Descendants().Where(e => e.Name.LocalName == "DataGridTextColumn" && (string?)e.Attribute("IsReadOnly") != "True"))
                    Assert.True((string?)column.Attribute("EditingElementStyle") == "{StaticResource GridEditBox}",
                        $"{Path.GetFileName(file)}: the column {(string?)column.Attribute("Header")} edits in WPF's own box");
            }
        }
    }

    [Fact]
    public void Text_boxes_use_the_themes_right_click_menu()
    {
        var styles = XDocument.Load(Path.Combine(AppFolder, "Themes", "Controls.xaml")).Root!.Elements()
            .Where(e => e.Name.LocalName == "Style" && (string?)e.Attribute("TargetType") is "TextBox" or "PasswordBox")
            .Where(e => e.Attribute(Xaml + "Key") == null)
            .ToList();
        Assert.Equal(2, styles.Count);
        foreach (var style in styles)
            Assert.Contains(style.Elements(), e => (string?)e.Attribute("Property") == "ContextMenu" && (string?)e.Attribute("Value") == "{StaticResource EditMenu}");
    }

    // ---- Helpers

    static IEnumerable<string> XamlFiles() =>
        Directory.GetFiles(AppFolder, "*.xaml").Concat(Directory.GetFiles(Path.Combine(AppFolder, "Themes"), "*.xaml"));

    static IEnumerable<string> WindowFiles() =>
        Directory.GetFiles(AppFolder, "*.xaml").Where(f => Path.GetFileName(f) != "App.xaml");

    static bool IsPalette(string file) =>
        Path.GetFileNameWithoutExtension(file) is "Light" or "Dark" or "HighContrast";

    static XElement Palette(string name) => XDocument.Load(Path.Combine(AppFolder, "Themes", name + ".xaml")).Root!;

    /// <summary>The palette's own names, leaving out the system colours it overrides.</summary>
    static HashSet<string> Keys(string palette) =>
        Palette(palette).Elements()
            .Select(e => (string?)e.Attribute(Xaml + "Key"))
            .Where(key => key != null && !key.StartsWith("{"))
            .ToHashSet()!;

    static Dictionary<string, (double R, double G, double B)> Colours(string palette) =>
        Palette(palette).Elements()
            .Where(e => e.Name.LocalName == "SolidColorBrush" && e.Attribute(Xaml + "Key") != null)
            .ToDictionary(e => (string)e.Attribute(Xaml + "Key")!, e => Parse((string)e.Attribute("Color")!));

    static (double R, double G, double B) Parse(string hex)
    {
        Assert.Matches("^#[0-9A-Fa-f]{6}$", hex);
        double Channel(int at) => int.Parse(hex.Substring(at, 2), NumberStyles.HexNumber) / 255.0;
        return (Channel(1), Channel(3), Channel(5));
    }

    /// <summary>WCAG's contrast ratio.</summary>
    static double Contrast((double R, double G, double B) a, (double R, double G, double B) b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    static double Luminance((double R, double G, double B) c)
    {
        static double Linear(double v) => v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        return 0.2126 * Linear(c.R) + 0.7152 * Linear(c.G) + 0.0722 * Linear(c.B);
    }

    static string RepoRoot()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder != null && !File.Exists(Path.Combine(folder.FullName, "ADM.sln"))) folder = folder.Parent;
        return folder?.FullName ?? throw new InvalidOperationException("ADM.sln not found above the test folder");
    }
}
