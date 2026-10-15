using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Nightcrew.Core;

namespace Nightcrew.App.Ui;

/// <summary>
/// Both marks as path data on a 24-unit grid, like Insomnia's eye. Everything that draws a mark (tray icons,
/// the panel header, the app icon, the README assets) builds it here.
/// </summary>
public static class MarkGeometry
{
    public const double Grid = 24;

    // Lantern: a ring handle, a roof, the glass, a base. The flame sits inside the glass.
    internal const string LanternHandle = "M10,5 A2,2 0 0 1 14,5";
    internal const string LanternRoof = "M10,5 H14 L17.5,8 H6.5 Z";
    internal const string LanternGlass = "M7,8 H17 V19 H7 Z";
    internal const string LanternBase = "M7,19 H17 L16,21 H8 Z";
    internal const string LanternFlame = "M12,10 C14.1,12.2 14.7,13.6 14.7,14.6 A2.7,2.7 0 0 1 9.3,14.6 C9.3,13.6 9.9,12.2 12,10 Z";

    // Moon and star: a crescent opening to the upper right, and a four-point star in its hollow.
    internal const string Moon = "M19.5,15 A8.5,8.5 0 1 1 9,4.5 A7.5,7.5 0 0 0 19.5,15 Z";
    internal const string Star = "M18.5,1.8 Q18.5,5.5 22.2,5.5 Q18.5,5.5 18.5,9.2 Q18.5,5.5 14.8,5.5 Q18.5,5.5 18.5,1.8 Z";

    /// <summary>Sizes in the app's .ico. The 256 px frame is stored as PNG, the rest as bitmaps.</summary>
    public static IReadOnlyList<int> AppIconSizes { get; } = [16, 20, 24, 32, 40, 48, 64, 128, 256];

    /// <summary>Stroke width in grid units: 2 below 24 px so the tray mark stays legible, 1.5 from 24 px up.</summary>
    public static double StrokeFor(double pixels) => pixels < 24 ? 2.0 : 1.5;

    /// <summary>
    /// The lantern spans about 20 of the 24 units, the moon and star nearly all of them. Windows fixes the tray
    /// slot's size, so tray bitmaps scale the lantern up about the centre until it fills the slot's height.
    /// </summary>
    internal const double TrayLanternScale = 1.18;

    // The working flame: the lantern's flame 1.2 times as big, filling most of the glass, with a pale core near its base.
    private const double WorkingFlameScale = 1.2, WorkingFlameTip = 9.4, CoreScale = 0.62, CoreTip = 12.93;

    /// <summary>
    /// The mark for one state, in grid units, frozen. Working lights an amber flame in the lantern's empty glass, or
    /// fills the moon with amber, which stands out from the white icons beside it in the tray. <paramref name="ink"/>
    /// is the stroke colour; on a light taskbar it is Night, and the amber deepens so it still shows.
    /// </summary>
    public static Drawing Build(MarkKind kind, MarkState state, double strokeWidth, Color ink)
    {
        var group = new DrawingGroup();
        var pen = new Pen(Theme.Brush(ink), strokeWidth) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        var inkFill = Theme.Brush(ink);
        var light = ink == Theme.Night;
        var amber = Theme.Brush(light ? Theme.AmberDeep : Theme.Amber);
        var core = Theme.Brush(light ? Theme.AmberDeepCore : Theme.AmberCore);
        var working = state == MarkState.Live;

        void Add(string path, Brush? fill, Pen? stroke) => group.Children.Add(new GeometryDrawing(fill, stroke, Parse(path)));

        if (kind == MarkKind.Lantern)
        {
            Add(LanternHandle, null, pen);
            Add(LanternRoof, inkFill, pen);
            Add(LanternGlass, null, pen);
            if (working)
            {
                group.Children.Add(new GeometryDrawing(amber, null, FlameAt(WorkingFlameScale, WorkingFlameTip)));
                group.Children.Add(new GeometryDrawing(core, null, FlameAt(CoreScale, CoreTip)));
            }
            Add(LanternBase, inkFill, pen);
        }
        else
        {
            Add(Moon, working ? amber : null, pen);
            if (working)
            {
                var starPen = new Pen(inkFill, 1) { LineJoin = PenLineJoin.Round };
                starPen.Freeze();
                Add(Star, inkFill, starPen);
            }
        }
        group.Freeze();
        return group;
    }

    /// <summary>The app icon's mark: the lantern with a lavender flame in empty glass.</summary>
    private static Drawing AppIconMark(double strokeWidth)
    {
        var group = new DrawingGroup();
        group.Children.Add(Build(MarkKind.Lantern, MarkState.Idle, strokeWidth, Theme.Moon));
        group.Children.Add(new GeometryDrawing(Theme.Brush(Theme.Lavender), null, Parse(LanternFlame)));
        group.Freeze();
        return group;
    }

    /// <summary>A vector image of the mark for the panel header, 24 units square.</summary>
    public static ImageSource Image(MarkKind kind, MarkState state, double strokeWidth, Color ink)
    {
        var image = new DrawingImage(Framed(Build(kind, state, strokeWidth, ink)));
        image.Freeze();
        return image;
    }

    /// <summary>The mark rendered at <paramref name="pixels"/> square, transparent around it, as the tray shows it.</summary>
    public static BitmapSource Render(MarkKind kind, MarkState state, int pixels, Color ink)
    {
        var mark = Build(kind, state, StrokeFor(pixels), ink);
        if (kind == MarkKind.Lantern)
        {
            var scaled = new DrawingGroup { Transform = new ScaleTransform(TrayLanternScale, TrayLanternScale, Grid / 2, Grid / 2) };
            scaled.Children.Add(mark);
            scaled.Freeze();
            mark = scaled;
        }
        return RenderDrawing(mark, pixels, tile: false);
    }

    /// <summary>The app icon: the lantern with its flame on a Night tile with 22% corners.</summary>
    public static BitmapSource RenderAppIcon(int pixels) =>
        RenderDrawing(AppIconMark(pixels < 32 ? 2.0 : 1.5), pixels, tile: true);

    /// <summary>Straight-alpha BGRA rows, top row first, as an .ico frame wants them.</summary>
    public static byte[] ToBgra(BitmapSource source)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var stride = converted.PixelWidth * 4;
        var pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);
        return pixels;
    }

    public static byte[] ToPng(BitmapSource source)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>Every frame of the app's .ico.</summary>
    public static IReadOnlyList<IcoWriter.Frame> AppIconFrames() =>
        AppIconSizes.Select(size =>
        {
            var bitmap = RenderAppIcon(size);
            return size >= 256 ? IcoWriter.Frame.Png(size, ToPng(bitmap)) : IcoWriter.Frame.Bitmap(size, ToBgra(bitmap));
        }).ToList();

    private static Geometry Parse(string path)
    {
        var geometry = Geometry.Parse(path);
        geometry.Freeze();
        return geometry;
    }

    /// <summary>The lantern's flame scaled about its tip, with the tip moved to <paramref name="tipY"/>.</summary>
    private static Geometry FlameAt(double scale, double tipY)
    {
        var transform = new TransformGroup();
        transform.Children.Add(new ScaleTransform(scale, scale, 12, 10));
        transform.Children.Add(new TranslateTransform(0, tipY - 10));
        var geometry = Geometry.Parse(LanternFlame).Clone(); // Parse returns it frozen
        geometry.Transform = transform;
        geometry.Freeze();
        return geometry;
    }

    /// <summary>Pins the drawing's bounds to the full 24-unit grid, so every mark and state lines up the same way.</summary>
    private static Drawing Framed(Drawing mark)
    {
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, Grid, Grid))));
        group.Children.Add(mark);
        group.ClipGeometry = new RectangleGeometry(new Rect(0, 0, Grid, Grid));
        group.Freeze();
        return group;
    }

    private static BitmapSource RenderDrawing(Drawing mark, int pixels, bool tile)
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            if (tile)
            {
                var corner = pixels * 0.22;
                context.DrawRoundedRectangle(Theme.Brush(Theme.Night), null, new Rect(0, 0, pixels, pixels), corner, corner);
                // The mark fills about 70% of the tile, centred; small tiles give it nearly all the room.
                var inset = pixels * (pixels < 32 ? 0.04 : 0.15);
                context.PushTransform(new TranslateTransform(inset, inset));
                context.PushTransform(new ScaleTransform((pixels - 2 * inset) / Grid, (pixels - 2 * inset) / Grid));
            }
            else
            {
                context.PushTransform(new ScaleTransform(pixels / Grid, pixels / Grid));
            }
            context.DrawDrawing(mark);
        }
        var bitmap = new RenderTargetBitmap(pixels, pixels, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }
}
