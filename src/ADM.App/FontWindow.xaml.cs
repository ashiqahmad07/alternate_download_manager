using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ADM.Core;

namespace ADM.App;

/// <summary>Chooses the font for every window. Nothing changes until OK.</summary>
public partial class FontWindow : ThemedWindow
{
    public string ChosenFamily => FamilyList.SelectedItem as string ?? Appearance.DefaultFontFamily;

    public double ChosenSize => SizeList.SelectedItem is double size ? size : Appearance.DefaultFontSize;

    public FontWindow(string family, double size)
    {
        InitializeComponent();
        Icon = AppIcon.ImageSource;

        var families = ReadableFamilies();
        // A saved font can be one this list leaves out, or one no longer installed; the window still shows it as chosen.
        if (!families.Contains(family, StringComparer.OrdinalIgnoreCase))
            families = families.Append(family).Order(StringComparer.CurrentCultureIgnoreCase).ToList();
        FamilyList.ItemsSource = families;
        FamilyList.SelectedItem = families.First(f => string.Equals(f, family, StringComparison.OrdinalIgnoreCase));
        FamilyList.ScrollIntoView(FamilyList.SelectedItem);

        var sizes = Enumerable.Range((int)Appearance.MinFontSize, (int)(Appearance.MaxFontSize - Appearance.MinFontSize) + 1)
            .Select(s => (double)s).ToList();
        if (!sizes.Contains(size)) sizes = sizes.Append(size).Order().ToList();
        SizeList.ItemsSource = sizes;
        SizeList.SelectedItem = size;
        SizeList.ScrollIntoView(size);
    }

    /// <summary>The installed fonts with ordinary letters: a symbol font would turn every window, this one included, into pictures.</summary>
    static List<string> ReadableFamilies()
    {
        var families = new List<string>();
        foreach (var family in Fonts.SystemFontFamilies)
        {
            try
            {
                var typeface = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
                if (typeface.TryGetGlyphTypeface(out var glyphs) && !glyphs.Symbol && glyphs.CharacterToGlyphMap.ContainsKey('a'))
                    families.Add(family.Source);
            }
            catch (IOException)
            {
                // A damaged font file is no reason to fail; it just isn't offered.
            }
        }
        return families.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    void Choice_Changed(object sender, SelectionChangedEventArgs e)
    {
        PreviewText.FontFamily = new FontFamily(ChosenFamily);
        PreviewText.FontSize = ChosenSize;
    }

    void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
