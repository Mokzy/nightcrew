using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Nightcrew.App.Ui;
using Xunit;

namespace Nightcrew.Tests;

public sealed class ThemeTests
{
    [Theory]
    [InlineData("serif", "EB Garamond")]
    [InlineData("sans", "Figtree")]
    public void The_embedded_fonts_load_from_the_exe(string which, string expected)
    {
        var name = Sta.Run(() =>
        {
            var family = which == "serif" ? Theme.Serif : Theme.Sans;
            var typeface = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            Assert.True(typeface.TryGetGlyphTypeface(out var glyphs));
            return glyphs.FamilyNames[CultureInfo.GetCultureInfo("en-US")];
        });
        Assert.Equal(expected, name);
    }

    [Fact]
    public void The_serif_has_a_real_italic_and_the_sans_a_real_medium()
    {
        Sta.Run(() =>
        {
            Assert.True(new Typeface(Theme.Serif, FontStyles.Italic, FontWeights.Normal, FontStretches.Normal)
                .TryGetGlyphTypeface(out var italic));
            Assert.Equal(FontStyles.Italic, italic.Style);

            Assert.True(new Typeface(Theme.Sans, FontStyles.Normal, FontWeights.Medium, FontStretches.Normal)
                .TryGetGlyphTypeface(out var medium));
            Assert.Equal(FontWeights.Medium, medium.Weight);
        });
    }

    [Fact]
    public void A_font_that_will_not_load_falls_back_to_the_system_font()
    {
        Assert.Equal("Georgia", Sta.Run(() => Theme.Load("No Such Font", "Georgia").Source));
    }

    [Fact]
    public void Brushes_are_frozen_so_any_thread_can_draw_them()
    {
        Assert.True(Theme.Brush(Theme.Lavender).IsFrozen);
    }

    [Fact]
    public void The_hairline_is_moon_at_ten_percent()
    {
        Assert.Equal((Theme.Moon.R, Theme.Moon.G, Theme.Moon.B), (Theme.Hairline.R, Theme.Hairline.G, Theme.Hairline.B));
        Assert.InRange(Theme.Hairline.A / 255.0, 0.095, 0.105);
    }
}
