using System.Windows;
using System.Windows.Forms.Integration;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Nightcrew.Core;

namespace Nightcrew.App.Ui;

/// <summary>
/// The borderless click panel. It lives on the tray app's UI thread; WinForms runs the message loop, and
/// <see cref="ElementHost.EnableModelessKeyboardInterop"/> lets it take Esc.
/// </summary>
public sealed class PanelWindow : Window
{
    private static readonly TimeSpan FadeIn = TimeSpan.FromMilliseconds(120);
    private const double Slide = 8;
    private const double Gap = 12;

    private readonly System.Windows.Controls.Border _frame;
    private readonly TranslateTransform _slide = new();
    private readonly PanelActions _actions;
    private DateTime _hiddenAt = DateTime.MinValue;

    public PanelWindow(PanelActions actions)
    {
        _actions = actions;
        Title = "Nightcrew";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        _frame = new System.Windows.Controls.Border { RenderTransform = _slide };
        Content = _frame;
        Deactivated += (_, _) => HidePanel();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) HidePanel();
        };
        ElementHost.EnableModelessKeyboardInterop(this);
    }

    /// <summary>
    /// True just after the panel hid itself because it lost focus. A click on the tray icon first takes focus
    /// from the panel, then arrives as a click: without this it would close the panel and open it again.
    /// </summary>
    public bool JustHid => DateTime.UtcNow - _hiddenAt < TimeSpan.FromMilliseconds(400);

    public void Render(PanelView view, MarkKind mark) => _frame.Child = PanelVisual.Build(view, mark, _actions);

    /// <summary>Opens beside the taskbar near <paramref name="click"/> (physical pixels), fading and sliding in.</summary>
    public void ShowNear(System.Drawing.Point click)
    {
        new WindowInteropHelper(this).EnsureHandle();
        var dpi = VisualTreeHelper.GetDpi(this);
        _frame.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = _frame.DesiredSize;

        var screen = System.Windows.Forms.Screen.FromPoint(click);
        var room = PanelVisual.ShadowRoom * dpi.DpiScaleX;
        var placement = PanelPlacement.Place(ToRect(screen.Bounds), ToRect(screen.WorkingArea), new Point(click.X, click.Y),
            new Size(size.Width * dpi.DpiScaleX, size.Height * dpi.DpiScaleY), Gap * dpi.DpiScaleX - room);
        Left = placement.TopLeft.X / dpi.DpiScaleX;
        Top = placement.TopLeft.Y / dpi.DpiScaleY;

        var (property, from) = placement.Edge switch
        {
            TaskbarEdge.Top => (TranslateTransform.YProperty, -Slide),
            TaskbarEdge.Left => (TranslateTransform.XProperty, -Slide),
            TaskbarEdge.Right => (TranslateTransform.XProperty, Slide),
            _ => (TranslateTransform.YProperty, Slide),
        };
        _slide.X = _slide.Y = 0;
        Opacity = 0;
        Show();
        Activate();
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, FadeIn) { EasingFunction = ease });
        _slide.BeginAnimation(property, new DoubleAnimation(from, 0, FadeIn) { EasingFunction = ease });
    }

    public void HidePanel()
    {
        if (!IsVisible) return;
        _hiddenAt = DateTime.UtcNow;
        Hide();
    }

    private static Rect ToRect(System.Drawing.Rectangle r) => new(r.X, r.Y, r.Width, r.Height);
}
