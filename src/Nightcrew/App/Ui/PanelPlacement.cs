using System.Windows;

namespace Nightcrew.App.Ui;

public enum TaskbarEdge { Bottom, Top, Left, Right }

public readonly record struct Placement(Point TopLeft, TaskbarEdge Edge);

/// <summary>Where the panel goes: against the taskbar, centred on the click, kept inside the work area.</summary>
public static class PanelPlacement
{
    /// <summary>
    /// All values in physical pixels. The taskbar edge is the side where the work area is smaller than the screen;
    /// with an auto-hidden taskbar there is none, and the panel sits at the bottom like the default taskbar.
    /// </summary>
    public static Placement Place(Rect screen, Rect workArea, Point click, Size panel, double gap)
    {
        var edge = workArea.Top > screen.Top ? TaskbarEdge.Top
            : workArea.Left > screen.Left ? TaskbarEdge.Left
            : workArea.Right < screen.Right ? TaskbarEdge.Right
            : TaskbarEdge.Bottom;

        var (x, y) = edge switch
        {
            TaskbarEdge.Top => (click.X - panel.Width / 2, workArea.Top + gap),
            TaskbarEdge.Left => (workArea.Left + gap, click.Y - panel.Height / 2),
            TaskbarEdge.Right => (workArea.Right - gap - panel.Width, click.Y - panel.Height / 2),
            _ => (click.X - panel.Width / 2, workArea.Bottom - gap - panel.Height),
        };
        x = Clamp(x, workArea.Left + gap, workArea.Right - gap - panel.Width);
        y = Clamp(y, workArea.Top + gap, workArea.Bottom - gap - panel.Height);
        return new Placement(new Point(x, y), edge);
    }

    /// <summary>Prefers the low bound when the panel is larger than the room, so its top-left stays on screen.</summary>
    private static double Clamp(double value, double low, double high) => Math.Max(low, Math.Min(high, value));
}
