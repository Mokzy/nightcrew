using System.Windows;
using Nightcrew.App.Ui;
using Xunit;

namespace Nightcrew.Tests;

public sealed class PanelPlacementTests
{
    private static readonly Rect Screen = new(0, 0, 1920, 1080);
    private static readonly Size Panel = new(352, 500);

    [Fact]
    public void A_bottom_taskbar_puts_the_panel_above_it_centred_on_the_click()
    {
        var placement = PanelPlacement.Place(Screen, new Rect(0, 0, 1920, 1032), new Point(1000, 1050), Panel, 12);

        Assert.Equal(TaskbarEdge.Bottom, placement.Edge);
        Assert.Equal(new Point(1000 - 176, 1032 - 12 - 500), placement.TopLeft);
    }

    [Fact]
    public void A_click_near_the_corner_keeps_the_panel_inside_the_work_area()
    {
        var placement = PanelPlacement.Place(Screen, new Rect(0, 0, 1920, 1032), new Point(1900, 1050), Panel, 12);
        Assert.Equal(1920 - 12 - 352, placement.TopLeft.X);
    }

    [Fact]
    public void A_top_taskbar_puts_the_panel_below_it()
    {
        var placement = PanelPlacement.Place(Screen, new Rect(0, 48, 1920, 1032), new Point(1000, 20), Panel, 12);

        Assert.Equal(TaskbarEdge.Top, placement.Edge);
        Assert.Equal(48 + 12, placement.TopLeft.Y);
    }

    [Fact]
    public void A_left_taskbar_puts_the_panel_to_its_right()
    {
        var placement = PanelPlacement.Place(Screen, new Rect(60, 0, 1860, 1080), new Point(30, 900), Panel, 12);

        Assert.Equal(TaskbarEdge.Left, placement.Edge);
        Assert.Equal(new Point(72, 1080 - 12 - 500), placement.TopLeft); // centred on the click, clamped to the bottom
    }

    [Fact]
    public void A_right_taskbar_puts_the_panel_to_its_left()
    {
        var placement = PanelPlacement.Place(Screen, new Rect(0, 0, 1860, 1080), new Point(1890, 400), Panel, 12);

        Assert.Equal(TaskbarEdge.Right, placement.Edge);
        Assert.Equal(new Point(1860 - 12 - 352, 150), placement.TopLeft);
    }

    [Fact]
    public void An_auto_hidden_taskbar_is_treated_as_bottom()
    {
        var placement = PanelPlacement.Place(Screen, Screen, new Point(1800, 1075), Panel, 12);

        Assert.Equal(TaskbarEdge.Bottom, placement.Edge);
        Assert.Equal(1080 - 12 - 500, placement.TopLeft.Y);
    }

    [Fact]
    public void A_panel_taller_than_the_work_area_keeps_its_top_on_screen()
    {
        var placement = PanelPlacement.Place(Screen, new Rect(0, 0, 1920, 400), new Point(1000, 1050), Panel, 12);
        Assert.Equal(12, placement.TopLeft.Y);
    }

    [Fact]
    public void A_second_monitor_uses_its_own_coordinates()
    {
        var right = new Rect(1920, -200, 2560, 1440);
        var placement = PanelPlacement.Place(right, new Rect(1920, -200, 2560, 1392), new Point(4400, 1220), Panel, 12);

        Assert.Equal(TaskbarEdge.Bottom, placement.Edge);
        Assert.Equal(new Point(1920 + 2560 - 12 - 352, -200 + 1392 - 12 - 500), placement.TopLeft);
    }
}
