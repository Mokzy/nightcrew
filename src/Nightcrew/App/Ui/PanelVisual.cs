using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Nightcrew.Core;

namespace Nightcrew.App.Ui;

/// <summary>What the panel's three controls do. Null when the panel is only drawn, as for the README.</summary>
public sealed record PanelActions(Action Hold, Action Pause, Action Settings);

/// <summary>Builds the click panel from a <see cref="PanelView"/>. The same tree is shown in the window and rendered for the README.</summary>
public static class PanelVisual
{
    public const double Width = 320;

    /// <summary>Transparent room around the panel for its shadow.</summary>
    public const double ShadowRoom = 16;

    private const double Gutter = 20;

    /// <summary>The panel with its shadow room, ready to be a window's content or rendered to a bitmap.</summary>
    public static FrameworkElement Build(PanelView view, MarkKind mark, PanelActions? actions)
    {
        var body = new StackPanel();
        body.Children.Add(Header(mark, view.Mark, actions));
        body.Children.Add(Headline(view));
        body.Children.Add(Text(view.SubLine, 12.5, Theme.MoonMuted, bottom: 16, lineHeight: 18));
        if (view.Holders.Count > 0) body.Children.Add(Holders(view.Holders));
        body.Children.Add(Facts(view.Facts));
        body.Children.Add(Buttons(view, actions));
        if (view.Warning is { } warning) body.Children.Add(WarningRow(warning));

        var panel = new Border
        {
            Width = Width,
            Background = Theme.Brush(Theme.Night),
            BorderBrush = Theme.Brush(Theme.Hairline),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(Gutter, 16, Gutter, 18),
            Child = body,
            Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 4, Direction = 270, Opacity = 0.45, Color = Colors.Black },
        };
        TextOptions.SetTextFormattingMode(panel, TextFormattingMode.Ideal);
        return new Border { Padding = new Thickness(ShadowRoom), Background = Brushes.Transparent, Child = panel };
    }

    private static FrameworkElement Header(MarkKind kind, MarkState state, PanelActions? actions)
    {
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 14), LastChildFill = false };
        var settings = Text("Settings", 12, Theme.MoonFaint);
        settings.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(settings, Dock.Right);
        if (actions is not null) Clickable(settings, actions.Settings, Theme.MoonFaint, Theme.MoonMuted);
        header.Children.Add(settings);

        // The header is never smaller than 20 px on screen, so the mark takes the lighter stroke.
        var image = new Image { Source = MarkGeometry.Image(kind, state, 1.5, Theme.Moon), Width = 20, Height = 20, VerticalAlignment = VerticalAlignment.Center };
        header.Children.Add(image);
        var name = Text("Nightcrew", 13, Theme.Moon, FontWeights.Medium);
        name.Margin = new Thickness(8, 0, 0, 0);
        name.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(name);
        return header;
    }

    private static TextBlock Headline(PanelView view)
    {
        var headline = new TextBlock
        {
            FontFamily = Theme.Serif,
            FontSize = 26,
            Foreground = Theme.Brush(Theme.Moon),
            Margin = new Thickness(0, 0, 0, 6),
            TextWrapping = TextWrapping.Wrap,
        };
        headline.Inlines.Add(new Run(view.Headline));
        if (view.HeadlineItalic is { } italic) headline.Inlines.Add(new Run(italic) { FontStyle = FontStyles.Italic });
        return headline;
    }

    private static Border Holders(IReadOnlyList<Holder> holders)
    {
        var rows = new StackPanel();
        rows.Children.Add(Text("Keeping it awake", 11, Theme.MoonFaint, bottom: 6));
        foreach (var holder in holders)
        {
            rows.Children.Add(Row(holder.Label, Theme.Moon, holder.Detail, Theme.MoonMuted, new Thickness(0, 2, 0, 2)));
        }
        return new Border
        {
            Background = Theme.Brush(Theme.NightRaised),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 0, 0, 12),
            Child = rows,
        };
    }

    private static StackPanel Facts(IReadOnlyList<PanelFact> facts)
    {
        var list = new StackPanel();
        foreach (var fact in facts)
        {
            list.Children.Add(new Border
            {
                BorderBrush = Theme.Brush(Theme.Hairline),
                BorderThickness = new Thickness(0, 1, 0, 0),
                Child = Row(fact.Label, Theme.MoonMuted, fact.Value, Theme.Moon, new Thickness(0, 8, 0, 8)),
            });
        }
        return list;
    }

    private static Grid Buttons(PanelView view, PanelActions? actions)
    {
        var grid = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());

        var hold = Button("Hold awake", view.HoldOn, actions?.Hold);
        var pause = Button(view.PauseLabel, false, actions?.Pause);
        Grid.SetColumn(pause, 2);
        grid.Children.Add(hold);
        grid.Children.Add(pause);
        return grid;
    }

    private static Border Button(string label, bool on, Action? click)
    {
        var fill = on ? Theme.Lavender : Theme.NightRaised;
        var text = Text(label, 12.5, on ? Theme.Ink : Theme.Moon, FontWeights.Medium);
        text.HorizontalAlignment = HorizontalAlignment.Center;
        text.VerticalAlignment = VerticalAlignment.Center;
        var button = new Border { Height = 34, CornerRadius = new CornerRadius(8), Background = Theme.Brush(fill), Child = text };
        System.Windows.Automation.AutomationProperties.SetName(button, label);
        if (click is not null) Clickable(button, click, fill, on ? Theme.Lavender : Theme.NightHover);
        return button;
    }

    private static DockPanel WarningRow(string warning)
    {
        var row = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
        var dot = new System.Windows.Shapes.Ellipse { Width = 6, Height = 6, Fill = Theme.Brush(Theme.Warn), Margin = new Thickness(0, 6, 8, 0), VerticalAlignment = VerticalAlignment.Top };
        row.Children.Add(dot);
        row.Children.Add(Text(warning, 12, Theme.Warn, lineHeight: 17));
        return row;
    }

    private static Grid Row(string left, Color leftColour, string right, Color rightColour, Thickness padding)
    {
        var grid = new Grid { Margin = padding };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var label = Text(left, 12.5, leftColour);
        label.TextTrimming = TextTrimming.CharacterEllipsis;
        var value = Text(right, 12.5, rightColour);
        value.Margin = new Thickness(12, 0, 0, 0);
        Grid.SetColumn(value, 1);
        grid.Children.Add(label);
        grid.Children.Add(value);
        return grid;
    }

    private static TextBlock Text(string text, double size, Color colour, FontWeight? weight = null, double bottom = 0, double lineHeight = double.NaN) => new()
    {
        Text = text,
        FontFamily = Theme.Sans,
        FontSize = size,
        FontWeight = weight ?? FontWeights.Normal,
        Foreground = Theme.Brush(colour),
        TextWrapping = TextWrapping.Wrap,
        LineHeight = lineHeight,
        Margin = new Thickness(0, 0, 0, bottom),
    };

    /// <summary>Hand cursor, a hover colour, and the action on a left click.</summary>
    private static void Clickable(FrameworkElement element, Action click, Color normal, Color hover)
    {
        element.Cursor = Cursors.Hand;
        void Paint(Color colour)
        {
            if (element is Border border) border.Background = Theme.Brush(colour);
            else if (element is TextBlock text) text.Foreground = Theme.Brush(colour);
        }
        element.MouseEnter += (_, _) => Paint(hover);
        element.MouseLeave += (_, _) => Paint(normal);
        element.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            click();
        };
    }
}
