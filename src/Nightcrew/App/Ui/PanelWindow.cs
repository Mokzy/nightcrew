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
    private readonly SettingsActions _settingsActions;
    private (PanelView View, SettingsPage Settings, MarkKind Mark)? _content;
    private DateTime _hiddenAt = DateTime.MinValue;
    private PanelAnchor? _anchor;

    public PanelWindow(PanelActions actions, SettingsActions settingsActions)
    {
        _actions = actions;
        _settingsActions = settingsActions;
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
        SizeChanged += (_, e) =>
        {
            if (IsVisible && _anchor is not null) MoveTo(e.NewSize); // rows come and go while it's open
        };
        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            if (OnSettings) ShowPage(settings: false); // Esc on settings goes back, Esc on the main page closes
            else HidePanel();
        };
        ElementHost.EnableModelessKeyboardInterop(this);
    }

    /// <summary>
    /// True just after the panel hid itself because it lost focus. A click on the tray icon first takes focus
    /// from the panel, then arrives as a click: without this it would close the panel and open it again.
    /// </summary>
    public bool JustHid => DateTime.UtcNow - _hiddenAt < TimeSpan.FromMilliseconds(400);

    /// <summary>True while the settings page is showing instead of the main page.</summary>
    public bool OnSettings { get; private set; }

    /// <summary>Draws whichever page is showing; the other is kept so <see cref="ShowPage"/> can swap in place.</summary>
    public void Render(PanelView view, SettingsPage settings, MarkKind mark)
    {
        _content = (view, settings, mark);
        _frame.Child = OnSettings ? PanelVisual.BuildSettings(settings, _settingsActions) : PanelVisual.Build(view, mark, _actions);
    }

    /// <summary>Switches between the main page and the settings page without closing the panel.</summary>
    public void ShowPage(bool settings)
    {
        OnSettings = settings;
        if (_content is { } content) Render(content.View, content.Settings, content.Mark);
    }

    /// <summary>Opens beside the taskbar near <paramref name="click"/> (physical pixels), fading and sliding in.</summary>
    public void ShowNear(System.Drawing.Point click)
    {
        new WindowInteropHelper(this).EnsureHandle();
        var dpi = VisualTreeHelper.GetDpi(this);
        _frame.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var screen = System.Windows.Forms.Screen.FromPoint(click);
        _anchor = new PanelAnchor(ToRect(screen.Bounds), ToRect(screen.WorkingArea), new Point(click.X, click.Y), dpi.DpiScaleX);
        var edge = MoveTo(_frame.DesiredSize);

        var (property, from) = edge switch
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

    /// <summary>Puts the window against the taskbar for its current size.</summary>
    private TaskbarEdge MoveTo(Size size)
    {
        var placement = _anchor!.For(size, Gap, PanelVisual.ShadowRoom);
        Left = placement.TopLeft.X;
        Top = placement.TopLeft.Y;
        return placement.Edge;
    }

    public void HidePanel()
    {
        if (!IsVisible) return;
        _hiddenAt = DateTime.UtcNow;
        Hide();
    }

    private static Rect ToRect(System.Drawing.Rectangle r) => new(r.X, r.Y, r.Width, r.Height);
}
