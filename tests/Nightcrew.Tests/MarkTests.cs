using System.Windows.Media;
using System.Windows.Media.Imaging;
using Nightcrew.App.Ui;
using Nightcrew.Core;
using Xunit;

namespace Nightcrew.Tests;

public sealed class MarkTests
{
    [Theory]
    [InlineData("lantern", MarkKind.Lantern)]
    [InlineData("moon", MarkKind.MoonAndStar)]
    [InlineData(" Moon ", MarkKind.MoonAndStar)]
    [InlineData("lanturn", MarkKind.Lantern)]
    [InlineData("", MarkKind.Lantern)]
    [InlineData(null, MarkKind.Lantern)]
    public void Mark_names_parse_and_anything_unknown_is_the_lantern(string? name, MarkKind expected)
    {
        Assert.Equal(expected, MarkKinds.Parse(name));
    }

    [Fact]
    public void Mark_names_round_trip()
    {
        Assert.Equal(MarkKind.MoonAndStar, MarkKinds.Parse(MarkKinds.Name(MarkKind.MoonAndStar)));
        Assert.Equal(MarkKind.Lantern, MarkKinds.Parse(MarkKinds.Name(MarkKind.Lantern)));
    }

    [Fact]
    public void Settings_default_to_the_lantern()
    {
        Assert.Equal("lantern", new Settings().Mark);
    }

    [Theory]
    [InlineData(16, 2.0)]
    [InlineData(20, 2.0)]
    [InlineData(24, 1.5)]
    [InlineData(48, 1.5)]
    public void Strokes_are_heavier_below_24_px(double pixels, double expected)
    {
        Assert.Equal(expected, MarkGeometry.StrokeFor(pixels));
    }

    [Theory]
    [InlineData(MarkKind.Lantern)]
    [InlineData(MarkKind.MoonAndStar)]
    public void Each_mark_draws_its_two_states_differently_and_inside_the_grid(MarkKind kind)
    {
        Sta.Run(() =>
        {
            var states = new[] { MarkState.Idle, MarkState.Live };
            var pixels = states.Select(state => MarkGeometry.ToBgra(MarkGeometry.Render(kind, state, 32, Theme.Moon))).ToList();
            Assert.NotEqual(pixels[0], pixels[1]);

            foreach (var state in states)
            {
                var bounds = MarkGeometry.Build(kind, state, 2, Theme.Moon).Bounds;
                Assert.True(bounds.Left >= 0 && bounds.Top >= 0 && bounds.Right <= 24 && bounds.Bottom <= 24, $"{kind} {state}: {bounds}");
            }
        });
    }

    [Theory]
    [InlineData(16)]
    [InlineData(20)]
    [InlineData(24)]
    [InlineData(32)]
    public void Tray_bitmaps_come_out_at_the_requested_size_with_something_drawn(int size)
    {
        Sta.Run(() =>
        {
            var bitmap = MarkGeometry.Render(MarkKind.Lantern, MarkState.Idle, size, Theme.Moon);
            Assert.Equal((size, size), (bitmap.PixelWidth, bitmap.PixelHeight));
            var bgra = MarkGeometry.ToBgra(bitmap);
            Assert.Contains(bgra.Where((_, i) => i % 4 == 3), alpha => alpha > 200);
        });
    }

    [Fact]
    public void A_light_taskbar_gets_the_deep_amber()
    {
        var (dark, light) = Sta.Run(() => (
            At(MarkGeometry.ToBgra(MarkGeometry.Render(MarkKind.Lantern, MarkState.Live, 96, Theme.Moon)), FlameX, FlameY),
            At(MarkGeometry.ToBgra(MarkGeometry.Render(MarkKind.Lantern, MarkState.Live, 96, Theme.Night)), FlameX, FlameY)));

        Assert.Equal(Theme.Amber, Color.FromRgb(dark.R, dark.G, dark.B));
        Assert.Equal(Theme.AmberDeep, Color.FromRgb(light.R, light.G, light.B));
    }

    // Points of a 96 px tray render (4 px per grid unit, after the tray's 1.18 scale about the centre).
    private const int FlameX = 48, FlameY = 46; // grid (12, 11.5): the flame's body, above its core
    private const int CoreY = 64; // grid (12, 16): the flame's pale core
    private const int GlassX = 33, GlassY = 42; // grid (8.25, 10.5): the glass beside the flame

    /// <summary>The colour at a point of a 96 px render (4 px per grid unit), with its alpha.</summary>
    private static Color At(byte[] bgra, int x, int y)
    {
        var i = (y * 96 + x) * 4;
        return Color.FromArgb(bgra[i + 3], bgra[i + 2], bgra[i + 1], bgra[i]);
    }

    [Fact]
    public void Working_lights_an_amber_flame_in_empty_glass_and_idle_leaves_the_lantern_empty()
    {
        var (working, idle) = Sta.Run(() => (
            MarkGeometry.ToBgra(MarkGeometry.Render(MarkKind.Lantern, MarkState.Live, 96, Theme.Moon)),
            MarkGeometry.ToBgra(MarkGeometry.Render(MarkKind.Lantern, MarkState.Idle, 96, Theme.Moon))));

        var flame = At(working, FlameX, FlameY);
        var core = At(working, FlameX, CoreY);
        Assert.Equal(Theme.Amber, Color.FromRgb(flame.R, flame.G, flame.B));
        Assert.Equal(255, flame.A);
        Assert.Equal(Theme.AmberCore, Color.FromRgb(core.R, core.G, core.B));
        Assert.Equal(0, At(working, GlassX, GlassY).A);

        Assert.Equal(0, At(idle, FlameX, FlameY).A);
        Assert.Equal(0, At(idle, FlameX, CoreY).A);
    }

    [Fact]
    public void The_working_flame_is_bigger_than_the_app_icons()
    {
        var (tall, small) = Sta.Run(() =>
        {
            var bgra = MarkGeometry.ToBgra(MarkGeometry.Render(MarkKind.Lantern, MarkState.Live, 96, Theme.Moon));
            var rows = Enumerable.Range(0, 96).Where(y => At(bgra, FlameX, y) is var c && c.A == 255 && c.R == Theme.Amber.R && c.G == Theme.Amber.G).ToList();
            return (rows.Last() - rows.First(), 7.3 * 4 * 1.18); // the app icon's flame is 7.3 units tall
        });

        Assert.True(tall > small * 1.1, $"flame column spans {tall} px, the app icon's flame would be {small:F0} px");
    }

    [Fact]
    public void Working_fills_the_moon_and_idle_leaves_it_empty()
    {
        var (working, idle) = Sta.Run(() => (
            At(MarkGeometry.ToBgra(MarkGeometry.Render(MarkKind.MoonAndStar, MarkState.Live, 96, Theme.Moon)), 24, 60),
            At(MarkGeometry.ToBgra(MarkGeometry.Render(MarkKind.MoonAndStar, MarkState.Idle, 96, Theme.Moon)), 24, 60)));

        // Grid (6, 15): inside the crescent.
        Assert.Equal(Theme.Amber, Color.FromRgb(working.R, working.G, working.B));
        Assert.Equal(0, idle.A);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(24)]
    [InlineData(32)]
    public void The_tray_lantern_fills_the_height_of_the_icon(int size)
    {
        var (top, bottom) = Sta.Run(() =>
        {
            var bgra = MarkGeometry.ToBgra(MarkGeometry.Render(MarkKind.Lantern, MarkState.Idle, size, Theme.Moon));
            var rows = Enumerable.Range(0, size).Where(y => Enumerable.Range(0, size).Any(x => bgra[(y * size + x) * 4 + 3] > 128)).ToList();
            return (rows.First(), rows.Last());
        });

        // About a pixel of room at most, above and below: the slot is all Windows gives a tray icon.
        Assert.InRange(top, 0, 1);
        Assert.InRange(bottom, size - 2, size - 1);
    }

    [Fact]
    public void The_app_icon_keeps_its_flame_in_empty_glass()
    {
        var (flame, glass) = Sta.Run(() =>
        {
            var bgra = MarkGeometry.ToBgra(MarkGeometry.RenderAppIcon(64));
            Color Pixel(int x, int y)
            {
                var i = (y * 64 + x) * 4;
                return Color.FromRgb(bgra[i + 2], bgra[i + 1], bgra[i]);
            }
            // The mark sits on the tile 9.6 px in, at 1.867 px per grid unit.
            return (Pixel(32, 37), Pixel(26, 42)); // grid (12, 14.6) in the flame; grid (9, 17.5) in the glass beside it
        });

        Assert.Equal(Theme.Lavender, flame);
        Assert.Equal(Theme.Night, glass);
    }

    [Fact]
    public void The_panel_header_mark_is_a_frozen_24_unit_image()
    {
        Sta.Run(() =>
        {
            var image = MarkGeometry.Image(MarkKind.MoonAndStar, MarkState.Live, 1.5, Theme.Moon);
            Assert.True(image.IsFrozen);
            Assert.Equal((24.0, 24.0), (image.Width, image.Height));
        });
    }

    [Fact]
    public void The_app_icon_has_every_size_and_a_night_tile()
    {
        Sta.Run(() =>
        {
            var frames = MarkGeometry.AppIconFrames();
            Assert.Equal([16, 20, 24, 32, 40, 48, 64, 128, 256], frames.Select(frame => frame.Size));
            Assert.True(frames[frames.Count - 1].IsPng);
            Assert.All(frames.Take(frames.Count - 1), frame => Assert.False(frame.IsPng));

            var corner = MarkGeometry.ToBgra(MarkGeometry.RenderAppIcon(64));
            var edge = (2 * 64 + 32) * 4; // top edge, middle: inside the tile
            Assert.Equal(Theme.Night, Color.FromRgb(corner[edge + 2], corner[edge + 1], corner[edge]));
            Assert.Equal(0, corner[3]); // the very corner is outside the rounded tile
        });
    }
}
