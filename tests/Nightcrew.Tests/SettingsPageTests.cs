using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Nightcrew.App.Ui;
using Nightcrew.Core;
using Xunit;

namespace Nightcrew.Tests;

public sealed class SettingsPageTests
{
    private static readonly SettingsPage Defaults = SettingsModel.From(new Settings());

    [Fact]
    public void The_page_shows_every_setting_in_order()
    {
        var texts = Sta.Run(() => Texts(PanelVisual.BuildSettings(Defaults, null)));

        Assert.Equal(
        [
            "‹", "Settings",
            "Battery floor", "10%", "15%", "20%", "30%", "Off",
            "Lid-closed limit", "2 h", "4 h", "8 h", "12 h", "Off",
            "Lock when lid closes", "Start at login",
            "Tray mark", "Lantern", "Moon and star",
            "Open log", "Quit Nightcrew",
        ], texts);
    }

    [Fact]
    public void The_chosen_value_and_switches_that_are_on_fill_lavender()
    {
        var page = SettingsModel.From(new Settings { StartAtLogin = false });
        var (chosen, other, lockSwitch, loginSwitch) = Sta.Run(() =>
        {
            var root = PanelVisual.BuildSettings(page, null);
            return (Fill(Named<Border>(root, "Battery floor 20%")), Fill(Named<Border>(root, "Battery floor 30%")),
                Fill(Track(root, "Lock when lid closes")), Fill(Track(root, "Start at login")));
        });

        Assert.Equal(Theme.Lavender, chosen);
        Assert.Equal(Colors.Transparent, other);
        Assert.Equal(Theme.Lavender, lockSwitch);
        Assert.Equal(Theme.NightRaised, loginSwitch);
    }

    [Fact]
    public void Each_control_reports_the_new_value()
    {
        var calls = Sta.Run(() =>
        {
            var calls = new List<string>();
            var root = PanelVisual.BuildSettings(Defaults, new SettingsActions(
                Back: () => calls.Add("back"),
                BatteryFloor: value => calls.Add($"floor {value}"),
                LidClosedLimit: value => calls.Add($"limit {value}"),
                LockOnLidClose: on => calls.Add($"lock {on}"),
                StartAtLogin: on => calls.Add($"login {on}"),
                Mark: kind => calls.Add($"mark {kind}"),
                OpenLog: () => calls.Add("log"),
                Quit: () => calls.Add("quit")));

            foreach (var name in new[]
            {
                "Battery floor 30%", "Lid-closed limit Off", "Lock when lid closes", "Start at login",
                "Tray mark Moon and star", "Open log", "Quit Nightcrew", "Back",
            })
            {
                Click(Named<FrameworkElement>(root, name));
            }
            return calls;
        });

        Assert.Equal(["floor 30", "limit 0", "lock False", "login False", "mark MoonAndStar", "log", "quit", "back"], calls);
    }

    [Fact]
    public void The_page_is_as_wide_as_the_panel()
    {
        var width = Sta.Run(() => AssetRenderer.Render(PanelVisual.BuildSettings(Defaults, null)).PixelWidth);
        Assert.Equal((PanelVisual.Width + 2 * PanelVisual.ShadowRoom) * AssetRenderer.Scale, width);
    }

    [Fact]
    public void The_window_switches_between_the_main_page_and_settings_in_place()
    {
        var (main, settings, back) = Sta.Run(() =>
        {
            var window = new PanelWindow(new PanelActions(() => { }, () => { }, () => { }), Actions());
            var view = AssetRenderer.SamplePanels[0].View;
            window.Render(view, Defaults, MarkKind.Lantern);
            var main = Texts((DependencyObject)window.Content);
            window.ShowPage(settings: true);
            var settings = (window.OnSettings, Texts((DependencyObject)window.Content));
            window.ShowPage(settings: false);
            var back = Texts((DependencyObject)window.Content);
            window.Close();
            return (main, settings, back);
        });

        Assert.Contains("All quiet.", main);
        Assert.True(settings.Item1);
        Assert.Contains("Battery floor", settings.Item2);
        Assert.DoesNotContain("All quiet.", settings.Item2);
        Assert.Equal(main, back);
    }

    private static SettingsActions Actions() =>
        new(() => { }, _ => { }, _ => { }, _ => { }, _ => { }, _ => { }, () => { }, () => { });

    private static Color Fill(Border border) => ((SolidColorBrush)border.Background).Color;

    /// <summary>The switch's track is the row's only rounded Border child.</summary>
    private static Border Track(DependencyObject root, string row) =>
        LogicalTreeHelper.GetChildren(Named<FrameworkElement>(root, row)).OfType<DependencyObject>()
            .SelectMany(child => child is Border border ? [border] : LogicalTreeHelper.GetChildren(child).OfType<Border>())
            .First(border => border.CornerRadius.TopLeft > 0);

    private static void Click(UIElement element) =>
        element.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });

    private static T Named<T>(DependencyObject root, string name) where T : DependencyObject =>
        Find<T>(root, element => AutomationProperties.GetName(element) == name) ?? throw new InvalidOperationException($"No {name}");

    private static T? Find<T>(DependencyObject node, Func<T, bool> match) where T : DependencyObject
    {
        if (node is T candidate && match(candidate)) return candidate;
        foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
        {
            var found = Find(child, match);
            if (found is not null) return found;
        }
        return null;
    }

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
}
