using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Nightcrew.Core;

namespace Nightcrew.App.Ui;

/// <summary>
/// Renders the README's panel images and the app's .ico from the real code. <c>build/render-assets.ps1</c> loads
/// the built exe as a library and calls these; the tray app's Main never runs. Call on an STA thread.
/// </summary>
public static class AssetRenderer
{
    public const double Scale = 2;

    /// <summary>The three panel states from fixed sample snapshots, as the README shows them.</summary>
    public static IReadOnlyList<(string Name, PanelView View)> SamplePanels { get; } =
    [
        ("quiet", PanelModel.From(new EngineSnapshot(Mode.Idle, LidState.Open, false, [], PowerSource.AC, 81,
            LidAction.Sleep, false, false, EngineWarning.None), 20)),
        ("working", PanelModel.From(new EngineSnapshot(Mode.Armed, LidState.Open, true,
            [new Holder("Claude Code", "2 sessions"), new Holder("Codex", "working")], PowerSource.DC, 64,
            LidAction.Sleep, false, false, EngineWarning.None), 20)),
        ("paused", PanelModel.From(new EngineSnapshot(Mode.Paused, LidState.Open, false, [], PowerSource.AC, 81,
            LidAction.Sleep, false, true, EngineWarning.None), 20)),
    ];

    /// <summary>Writes panel-quiet.png, panel-working.png and panel-paused.png at 2x.</summary>
    public static IReadOnlyList<string> WritePanels(string directory, MarkKind mark = MarkKind.Lantern)
    {
        Directory.CreateDirectory(directory);
        return SamplePanels.Select(sample =>
        {
            var path = Path.Combine(directory, $"panel-{sample.Name}.png");
            File.WriteAllBytes(path, MarkGeometry.ToPng(RenderPanel(sample.View, mark)));
            return path;
        }).ToList();
    }

    /// <summary>Writes the app's multi-size .ico, which the project sets as its ApplicationIcon.</summary>
    public static void WriteAppIcon(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllBytes(path, IcoWriter.Write(MarkGeometry.AppIconFrames()));
    }

    /// <summary>
    /// The images the README's HTML pages use: the app tile and the lantern in each state. Writing them from the
    /// real geometry keeps the README's marks identical to the app's.
    /// </summary>
    public static IReadOnlyList<string> WriteMarks(string directory)
    {
        Directory.CreateDirectory(directory);
        var written = new List<string>();
        void Write(string name, BitmapSource bitmap)
        {
            var path = Path.Combine(directory, name);
            File.WriteAllBytes(path, MarkGeometry.ToPng(bitmap));
            written.Add(path);
        }
        Write("tile.png", MarkGeometry.RenderAppIcon(512));
        foreach (var state in new[] { MarkState.Idle, MarkState.Live, MarkState.LidClosed })
        {
            Write($"mark-{state.ToString().ToLowerInvariant()}.png", MarkGeometry.Render(MarkKind.Lantern, state, 192, Theme.Moon));
        }
        return written;
    }

    public static BitmapSource RenderPanel(PanelView view, MarkKind mark)
    {
        var element = PanelVisual.Build(view, mark, actions: null);
        element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        element.Arrange(new Rect(element.DesiredSize));
        element.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth * Scale), (int)Math.Ceiling(element.ActualHeight * Scale),
            96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
        bitmap.Render(element);
        bitmap.Freeze();
        return bitmap;
    }
}
