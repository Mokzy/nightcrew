using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Nightcrew.App.Ui;
using Nightcrew.Core;
using Xunit;

namespace Nightcrew.Tests;

public sealed class PanelVisualTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nightcrew-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static PanelView View(string name) => AssetRenderer.SamplePanels.Single(sample => sample.Name == name).View;

    /// <summary>Every string the panel shows, in order: text blocks, with a headline's runs joined.</summary>
    private static List<string> Texts(DependencyObject root)
    {
        var texts = new List<string>();
        void Walk(object node)
        {
            if (node is TextBlock block)
            {
                texts.Add(block.Inlines.Count > 0 ? string.Concat(block.Inlines.OfType<Run>().Select(run => run.Text)) : block.Text);
                return;
            }
            if (node is DependencyObject parent)
            {
                foreach (var child in LogicalTreeHelper.GetChildren(parent)) Walk(child);
            }
        }
        Walk(root);
        return texts;
    }

    [Fact]
    public void Working_shows_the_headline_holders_facts_and_buttons()
    {
        var texts = Sta.Run(() => Texts(PanelVisual.Build(View("working"), MarkKind.Lantern, null)));

        Assert.Equal(
        [
            "Settings", "Nightcrew", "Working through the night.",
            "Close the lid. Your laptop keeps working, then does what your lid normally does.",
            "Keeping it awake", "Claude Code", "2 sessions", "Codex", "working",
            "Battery", "64% · stops at 20%", "When done", "Sleeps", "Hold awake", "Pause",
        ], texts);
    }

    [Fact]
    public void Quiet_and_paused_have_no_holder_box()
    {
        var quiet = Sta.Run(() => Texts(PanelVisual.Build(View("quiet"), MarkKind.Lantern, null)));
        var paused = Sta.Run(() => Texts(PanelVisual.Build(View("paused"), MarkKind.MoonAndStar, null)));

        Assert.DoesNotContain("Keeping it awake", quiet);
        Assert.Contains("When the lid closes", quiet);
        Assert.DoesNotContain("Keeping it awake", paused);
        Assert.Contains("Resume", paused);
    }

    [Fact]
    public void A_warning_is_the_last_row()
    {
        var view = View("quiet") with { Warning = "Your power settings aren't back yet. Retrying." };
        var texts = Sta.Run(() => Texts(PanelVisual.Build(view, MarkKind.Lantern, null)));
        Assert.Equal("Your power settings aren't back yet. Retrying.", texts[texts.Count - 1]);
    }

    [Fact]
    public void A_long_holder_name_is_trimmed_to_one_line_instead_of_widening_the_panel()
    {
        var shortName = View("working") with { Holders = [new Holder("python", "busy"), new Holder("Codex", "working")] };
        var longName = shortName with { Holders = [new Holder(new string('x', 120), "busy"), new Holder("Codex", "working")] };
        var (shortSize, longSize) = Sta.Run(() =>
        {
            var a = AssetRenderer.RenderPanel(shortName, MarkKind.Lantern);
            var b = AssetRenderer.RenderPanel(longName, MarkKind.Lantern);
            return ((a.PixelWidth, a.PixelHeight), (b.PixelWidth, b.PixelHeight));
        });
        Assert.Equal((PanelVisual.Width + 2 * PanelVisual.ShadowRoom) * AssetRenderer.Scale, longSize.Item1);
        Assert.Equal(shortSize, longSize);
    }

    [Fact]
    public void Hold_awake_fills_lavender_while_it_is_on()
    {
        var (off, on) = Sta.Run(() => (HoldFill(View("quiet")), HoldFill(View("quiet") with { HoldOn = true })));
        Assert.Equal(Theme.NightRaised, off);
        Assert.Equal(Theme.Lavender, on);

        static Color HoldFill(PanelView view)
        {
            var root = PanelVisual.Build(view, MarkKind.Lantern, null);
            var button = Find<Border>(root, border => System.Windows.Automation.AutomationProperties.GetName(border) == "Hold awake");
            return ((SolidColorBrush)button.Background).Color;
        }
    }

    [Fact]
    public void The_controls_call_their_actions()
    {
        var calls = Sta.Run(() =>
        {
            var calls = new List<string>();
            var root = PanelVisual.Build(View("working"), MarkKind.Lantern,
                new PanelActions(() => calls.Add("hold"), () => calls.Add("pause"), () => calls.Add("settings")));
            Click(Find<Border>(root, border => System.Windows.Automation.AutomationProperties.GetName(border) == "Pause"));
            Click(Find<Border>(root, border => System.Windows.Automation.AutomationProperties.GetName(border) == "Hold awake"));
            Click(Find<TextBlock>(root, text => text.Text == "Settings"));
            return calls;
        });
        Assert.Equal(["pause", "hold", "settings"], calls);

        static void Click(UIElement element) =>
            element.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
    }

    [Fact]
    public void The_readme_renders_write_the_three_panels_and_settings_at_twice_the_size()
    {
        var paths = Sta.Run(() => AssetRenderer.WritePanels(_dir));

        Assert.Equal(["panel-quiet.png", "panel-working.png", "panel-paused.png", "panel-settings.png"], paths.Select(Path.GetFileName));
        foreach (var path in paths)
        {
            using var stream = File.OpenRead(path);
            var frame = System.Windows.Media.Imaging.BitmapFrame.Create(stream, System.Windows.Media.Imaging.BitmapCreateOptions.None,
                System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
            Assert.Equal((PanelVisual.Width + 2 * PanelVisual.ShadowRoom) * 2, frame.PixelWidth);
            Assert.InRange(frame.PixelHeight, 400, 1200);
        }
    }

    [Fact]
    public void The_readme_marks_are_the_tile_and_the_lantern_in_each_state()
    {
        var paths = Sta.Run(() => AssetRenderer.WriteMarks(_dir));
        Assert.Equal(["tile.png", "mark-idle.png", "mark-live.png", "mark-lidclosed.png"], paths.Select(Path.GetFileName));
        Assert.All(paths, path => Assert.True(new FileInfo(path).Length > 0));
    }

    [Fact]
    public void The_app_icon_file_has_every_size()
    {
        var path = Path.Combine(_dir, "nightcrew.ico");
        Sta.Run(() => AssetRenderer.WriteAppIcon(path));

        var sizes = IcoSizes(File.ReadAllBytes(path));
        Assert.Equal(MarkGeometry.AppIconSizes, sizes);
    }

    private static List<int> IcoSizes(byte[] ico) =>
        Enumerable.Range(0, BitConverter.ToUInt16(ico, 4)).Select(i => ico[6 + 16 * i] is 0 ? 256 : (int)ico[6 + 16 * i]).ToList();

    private static T Find<T>(DependencyObject root, Func<T, bool> match) where T : DependencyObject
    {
        if (root is T hit && match(hit)) return hit;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            var found = TryFind(child, match);
            if (found is not null) return found;
        }
        throw new InvalidOperationException($"No {typeof(T).Name} matched");

        static T? TryFind(DependencyObject node, Func<T, bool> match)
        {
            if (node is T candidate && match(candidate)) return candidate;
            foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
            {
                var found = TryFind(child, match);
                if (found is not null) return found;
            }
            return null;
        }
    }
}
