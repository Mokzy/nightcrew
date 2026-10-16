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
            "Lock when lid closes", "Stay awake for approvals", "Start at login", "Efficiency mode while away",
            "Keep at full speed", "+ Add an app",
            "Tray mark", "Lantern", "Moon and star",
            "Open log", "Quit Nightcrew",
        ], texts);
    }

    [Fact]
    public void The_chosen_value_and_switches_that_are_on_fill_lavender()
    {
        var page = SettingsModel.From(new Settings { StartAtLogin = false });
        var (chosen, other, lockSwitch, loginSwitch, approvalsSwitch) = Sta.Run(() =>
        {
            var root = PanelVisual.BuildSettings(page, null);
            return (Fill(Named<Border>(root, "Battery floor 20%")), Fill(Named<Border>(root, "Battery floor 30%")),
                Fill(Track(root, "Lock when lid closes")), Fill(Track(root, "Start at login")), Fill(Track(root, "Stay awake for approvals")));
        });

        Assert.Equal(Theme.Lavender, chosen);
        Assert.Equal(Colors.Transparent, other);
        Assert.Equal(Theme.Lavender, lockSwitch);
        Assert.Equal(Theme.NightRaised, loginSwitch);
        Assert.Equal(Theme.Lavender, approvalsSwitch);
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
                Quit: () => calls.Add("quit"),
                StayAwakeForApprovals: on => calls.Add($"approvals {on}")));

            foreach (var name in new[]
            {
                "Battery floor 30%", "Lid-closed limit Off", "Lock when lid closes", "Stay awake for approvals", "Start at login",
                "Tray mark Moon and star", "Open log", "Quit Nightcrew", "Back",
            })
            {
                Click(Named<FrameworkElement>(root, name));
            }
            return calls;
        });

        Assert.Equal(["floor 30", "limit 0", "lock False", "approvals False", "login False", "mark MoonAndStar", "log", "quit", "back"], calls);
    }

    [Fact]
    public void The_efficiency_switch_each_cross_and_add_an_app_report_back()
    {
        var page = SettingsModel.From(Kept());
        var calls = Sta.Run(() =>
        {
            var calls = new List<string>();
            var root = PanelVisual.BuildSettings(page, Actions() with
            {
                EfficiencyWhileAway = on => calls.Add($"efficiency {on}"),
                RemoveFullSpeed = name => calls.Add($"remove {name}"),
                ToggleAddList = () => calls.Add("add list"),
            });
            Click(Named<FrameworkElement>(root, "Efficiency mode while away"));
            Click(Named<FrameworkElement>(root, "Remove Microsoft Outlook"));
            Click(Named<FrameworkElement>(root, "Add an app"));
            return calls;
        });

        Assert.Equal(["efficiency False", "remove olk", "add list"], calls);
    }

    [Fact]
    public void Apps_kept_at_full_speed_sit_under_the_efficiency_switch_then_add_an_app()
    {
        var texts = Sta.Run(() => Texts(PanelVisual.BuildSettings(SettingsModel.From(Kept()), null)));

        var at = texts.IndexOf("Efficiency mode while away");
        Assert.Equal(["Efficiency mode while away", "Keep at full speed", "Microsoft Outlook", "×", "WhatsApp", "×", "+ Add an app", "Tray mark"],
            texts.Skip(at).Take(8));
    }

    [Fact]
    public void With_efficiency_mode_off_the_full_speed_list_is_hidden()
    {
        var page = SettingsModel.From(Kept()) with { EfficiencyWhileAway = false };

        var texts = Sta.Run(() => Texts(PanelVisual.BuildSettings(page, null)));

        Assert.DoesNotContain("Keep at full speed", texts);
        Assert.DoesNotContain("+ Add an app", texts);
        Assert.DoesNotContain("Microsoft Outlook", texts);
    }

    [Fact]
    public void Add_an_app_opens_the_apps_nightcrew_would_slow_and_each_reports_back()
    {
        var page = SettingsModel.From(new Settings(), [new NamedApp("Spotify", "spotify"), new NamedApp("Discord", "discord")]);
        var (texts, calls) = Sta.Run(() =>
        {
            var calls = new List<string>();
            var root = PanelVisual.BuildSettings(page, Actions() with { AddFullSpeed = app => calls.Add($"add {app.RootName}") });
            Click(Named<FrameworkElement>(root, "Add Spotify"));
            return (Texts(root), calls);
        });

        var at = texts.IndexOf("+ Add an app");
        Assert.Equal(["+ Add an app", "Discord", "Spotify", "Tray mark"], texts.Skip(at).Take(4));
        Assert.Equal(["add spotify"], calls);
    }

    [Fact]
    public void With_nothing_to_add_the_list_says_so()
    {
        var page = SettingsModel.From(new Settings(), []);

        var texts = Sta.Run(() => Texts(PanelVisual.BuildSettings(page, null)));

        var at = texts.IndexOf("+ Add an app");
        Assert.Equal(["+ Add an app", "Nothing to add right now", "Tray mark"], texts.Skip(at).Take(3));
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

    private static Settings Kept() => new()
    {
        LeaveAlone = ["whatsapp.root", "olk"],
        LeaveAloneLabels = new() { ["olk"] = "Microsoft Outlook", ["whatsapp.root"] = "WhatsApp" },
    };

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
