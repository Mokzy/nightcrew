using System.Windows;
using System.Windows.Media;

namespace Nightcrew.App.Ui;

/// <summary>The Moonlit tokens from the UI spec, and the two embedded fonts with their fallbacks.</summary>
public static class Theme
{
    public static Color Night { get; } = Rgb(0x1E, 0x1F, 0x21);
    public static Color NightRaised { get; } = Rgb(0x26, 0x28, 0x2B);
    public static Color NightHover { get; } = Rgb(0x2F, 0x31, 0x35);
    public static Color Moon { get; } = Rgb(0xFF, 0xFF, 0xEB);
    public static Color MoonMuted { get; } = Rgb(0xC9, 0xC7, 0xB6);
    public static Color MoonFaint { get; } = Rgb(0x8C, 0x8A, 0x7C);
    public static Color Hairline { get; } = Color.FromArgb(0x1A, 0xFF, 0xFF, 0xEB); // Moon at 10%
    public static Color Lavender { get; } = Rgb(0xF0, 0xD7, 0xFF);

    // The working tray mark's flame: amber with a pale core, deeper on a light taskbar so it still stands out.
    public static Color Amber { get; } = Rgb(0xFF, 0xB1, 0x3B);
    public static Color AmberCore { get; } = Rgb(0xFF, 0xE9, 0xB8);
    public static Color AmberDeep { get; } = Rgb(0xE0, 0x86, 0x00);
    public static Color AmberDeepCore { get; } = Rgb(0xFF, 0xCB, 0x6B);
    public static Color Ink { get; } = Rgb(0x1A, 0x1A, 0x1A);
    public static Color Warn { get; } = Rgb(0xF5, 0xC2, 0x7A);

    private static readonly Lazy<FontFamily> SerifFont = new(() => Load("EB Garamond", "Georgia"));
    private static readonly Lazy<FontFamily> SansFont = new(() => Load("Figtree", "Segoe UI"));

    /// <summary>EB Garamond, for the one headline. Georgia if the embedded font can't be loaded.</summary>
    public static FontFamily Serif => SerifFont.Value;

    /// <summary>Figtree, for everything else. Segoe UI if the embedded font can't be loaded.</summary>
    public static FontFamily Sans => SansFont.Value;

    /// <summary>A frozen brush, so snapshots of the panel can be drawn on any thread.</summary>
    public static SolidColorBrush Brush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);

    internal static FontFamily Load(string name, string fallback)
    {
        try
        {
            _ = System.Windows.Application.Current; // registers pack:// even though the app runs a WinForms loop
            var family = new FontFamily(new Uri("pack://application:,,,/nightcrew;component/Fonts/"), "./#" + name);
            var typeface = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            if (typeface.TryGetGlyphTypeface(out _)) return family;
        }
        catch (Exception)
        {
            // A missing or broken font must never stop the panel: fall through to the system font.
        }
        return new FontFamily(fallback);
    }
}
