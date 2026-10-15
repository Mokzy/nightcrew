using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Nightcrew.Core;

namespace Nightcrew.App.Ui;

/// <summary>What the panel's controls do. Null when the panel is only drawn, as for the README.</summary>
public sealed record PanelActions(Action Hold, Action Pause, Action Settings);

/// <summary>
/// What the settings page's controls do: each setter gets the new value. Null when the page is only drawn.
/// <paramref name="ToggleAddList"/> opens or closes the Add an app list; <paramref name="RemoveFullSpeed"/> gets a process name.
/// </summary>
public sealed record SettingsActions(Action Back, Action<int> BatteryFloor, Action<int> LidClosedLimit,
    Action<bool> LockOnLidClose, Action<bool> StartAtLogin, Action<MarkKind> Mark, Action OpenLog, Action Quit,
    Action<bool>? EfficiencyWhileAway = null, Action? ToggleAddList = null, Action<NamedApp>? AddFullSpeed = null,
    Action<string>? RemoveFullSpeed = null);

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
        return Frame(body);
    }

    /// <summary>The settings page, in the same frame as the main page so the window can swap one for the other.</summary>
    public static FrameworkElement BuildSettings(SettingsPage page, SettingsActions? actions)
    {
        var body = new StackPanel();
        body.Children.Add(BackHeader(actions?.Back));
        body.Children.Add(Text("Battery floor", 12.5, Theme.Moon));
        body.Children.Add(Segments("Battery floor", page.BatteryFloor, actions?.BatteryFloor));
        var limit = Text("Lid-closed limit", 12.5, Theme.Moon);
        limit.Margin = new Thickness(0, 14, 0, 0);
        body.Children.Add(limit);
        body.Children.Add(Segments("Lid-closed limit", page.LidClosedLimit, actions?.LidClosedLimit));
        body.Children.Add(Hairline());
        body.Children.Add(SwitchRow("Lock when lid closes", page.LockOnLidClose, actions?.LockOnLidClose, bottom: 12));
        body.Children.Add(SwitchRow("Start at login", page.StartAtLogin, actions?.StartAtLogin, bottom: 12));
        body.Children.Add(SwitchRow("Efficiency mode while away", page.EfficiencyWhileAway, actions?.EfficiencyWhileAway, bottom: 0));
        if (page.EfficiencyWhileAway) body.Children.Add(FullSpeed(page, actions));
        body.Children.Add(Hairline());
        body.Children.Add(Text("Tray mark", 12.5, Theme.Moon));
        body.Children.Add(Segments("Tray mark", page.Mark, actions?.Mark));
        body.Children.Add(Hairline());
        body.Children.Add(Footer(actions));
        return Frame(body);
    }

    /// <summary>The Night card with its hairline border and shadow, inside transparent room for the shadow.</summary>
    private static FrameworkElement Frame(UIElement body)
    {
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

    private static Border BackHeader(Action? back)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var arrow = Text("\u2039", 18, Theme.Moon);
        arrow.Margin = new Thickness(0, 0, 8, 0);
        arrow.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(arrow);
        var title = Text("Settings", 13, Theme.Moon, FontWeights.Medium);
        title.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(title);
        var header = new Border { Background = Brushes.Transparent, Margin = new Thickness(0, 0, 0, 14), HorizontalAlignment = HorizontalAlignment.Left, Child = row };
        AutomationProperties.SetName(header, "Back");
        if (back is not null) OnClick(header, back);
        return header;
    }

    /// <summary>A row of choices on a raised track; the chosen one fills lavender.</summary>
    private static Border Segments<T>(string name, IReadOnlyList<Choice<T>> choices, Action<T>? pick)
    {
        var cells = new UniformGrid { Rows = 1, Columns = choices.Count };
        foreach (var choice in choices)
        {
            var label = Text(choice.Label, 12, choice.Selected ? Theme.Ink : Theme.MoonMuted, choice.Selected ? FontWeights.Medium : null);
            label.TextWrapping = TextWrapping.NoWrap;
            label.HorizontalAlignment = HorizontalAlignment.Center;
            var fill = choice.Selected ? Theme.Lavender : Colors.Transparent;
            var cell = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(0, 6, 0, 6), Background = Theme.Brush(fill), Child = label };
            AutomationProperties.SetName(cell, $"{name} {choice.Label}");
            if (pick is not null && !choice.Selected) Clickable(cell, () => pick(choice.Value), fill, Theme.NightHover);
            cells.Children.Add(cell);
        }
        return new Border
        {
            Background = Theme.Brush(Theme.NightRaised),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(3),
            Margin = new Thickness(0, 8, 0, 0),
            Child = cells,
        };
    }

    /// <summary>A label with an on/off switch; clicking anywhere on the row flips it.</summary>
    private static Border SwitchRow(string label, bool on, Action<bool>? set, double bottom)
    {
        var knob = new System.Windows.Shapes.Ellipse
        {
            Width = 12,
            Height = 12,
            Fill = Theme.Brush(on ? Theme.Ink : Theme.MoonFaint),
            Margin = new Thickness(3),
            HorizontalAlignment = on ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var track = new Border { Width = 30, Height = 18, CornerRadius = new CornerRadius(9), Background = Theme.Brush(on ? Theme.Lavender : Theme.NightRaised), Child = knob };
        var dock = new DockPanel();
        DockPanel.SetDock(track, Dock.Right);
        dock.Children.Add(track);
        var text = Text(label, 12.5, Theme.Moon);
        text.VerticalAlignment = VerticalAlignment.Center;
        dock.Children.Add(text);
        var row = new Border { Background = Brushes.Transparent, Margin = new Thickness(0, 0, 0, bottom), Child = dock };
        AutomationProperties.SetName(row, label);
        if (set is not null) OnClick(row, () => set(!on));
        return row;
    }

    /// <summary>Open log on the left, Quit on the right, in reading order.</summary>
    private static Grid Footer(SettingsActions? actions)
    {
        var footer = new Grid();
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var log = Text("Open log", 12, Theme.MoonFaint);
        log.HorizontalAlignment = HorizontalAlignment.Left;
        AutomationProperties.SetName(log, "Open log");
        var quit = Text("Quit Nightcrew", 12, Theme.MoonMuted);
        AutomationProperties.SetName(quit, "Quit Nightcrew");
        Grid.SetColumn(quit, 1);
        if (actions is not null)
        {
            Clickable(log, actions.OpenLog, Theme.MoonFaint, Theme.MoonMuted);
            Clickable(quit, actions.Quit, Theme.MoonMuted, Theme.Moon);
        }
        footer.Children.Add(log);
        footer.Children.Add(quit);
        return footer;
    }

    private static Border Hairline() => new() { Height = 1, Background = Theme.Brush(Theme.Hairline), Margin = new Thickness(0, 14, 0, 14) };

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

    /// <summary>
    /// Under the Efficiency mode switch: the apps you keep at full speed, then Add an app, which opens the apps
    /// Nightcrew would slow right now.
    /// </summary>
    private static StackPanel FullSpeed(SettingsPage page, SettingsActions? actions)
    {
        var section = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        section.Children.Add(Text("Keep at full speed", 11, Theme.MoonFaint, bottom: 2));
        foreach (var app in page.FullSpeed ?? []) section.Children.Add(KeptRow(app, actions?.RemoveFullSpeed));
        var add = Text("+ Add an app", 12.5, Theme.MoonMuted);
        add.Margin = new Thickness(0, 6, 0, 0);
        add.HorizontalAlignment = HorizontalAlignment.Left;
        AutomationProperties.SetName(add, "Add an app");
        if (actions?.ToggleAddList is { } toggle) Clickable(add, toggle, Theme.MoonMuted, Theme.Moon);
        section.Children.Add(add);
        if (page.Addable is { } addable) section.Children.Add(AddList(addable, actions?.AddFullSpeed));
        return section;
    }

    /// <summary>A kept app's name, then the cross that takes it off, in reading order.</summary>
    private static Grid KeptRow(FullSpeedApp app, Action<string>? remove)
    {
        var row = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var label = Text(app.Label, 12.5, Theme.Moon);
        label.TextWrapping = TextWrapping.NoWrap;
        label.TextTrimming = TextTrimming.CharacterEllipsis;
        label.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(label);
        var cross = Text("\u00D7", 14, Theme.MoonFaint);
        cross.Margin = new Thickness(12, 0, 0, 0);
        cross.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetName(cross, "Remove " + app.Label);
        if (remove is not null) Clickable(cross, () => remove(app.Name), Theme.MoonFaint, Theme.Moon);
        Grid.SetColumn(cross, 1);
        row.Children.Add(cross);
        return row;
    }

    /// <summary>The apps Nightcrew would slow right now, on a raised card; clicking one keeps it at full speed.</summary>
    private static Border AddList(IReadOnlyList<NamedApp> apps, Action<NamedApp>? add)
    {
        var rows = new StackPanel();
        if (apps.Count == 0)
        {
            rows.Children.Add(new Border { Padding = new Thickness(8, 5, 8, 5), Child = Text("Nothing to add right now", 12.5, Theme.MoonFaint) });
        }
        foreach (var app in apps)
        {
            var label = Text(app.DisplayName, 12.5, Theme.Moon);
            label.TextWrapping = TextWrapping.NoWrap;
            label.TextTrimming = TextTrimming.CharacterEllipsis;
            var row = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 5, 8, 5), Background = Brushes.Transparent, Child = label };
            AutomationProperties.SetName(row, "Add " + app.DisplayName);
            if (add is not null) Clickable(row, () => add(app), Colors.Transparent, Theme.NightHover);
            rows.Children.Add(row);
        }
        return new Border
        {
            Background = Theme.Brush(Theme.NightRaised),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(4),
            Margin = new Thickness(0, 6, 0, 0),
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
        AutomationProperties.SetName(button, label);
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
        // One line each: a long process name ends in an ellipsis instead of wrapping.
        var label = Text(left, 12.5, leftColour);
        label.TextWrapping = TextWrapping.NoWrap;
        label.TextTrimming = TextTrimming.CharacterEllipsis;
        var value = Text(right, 12.5, rightColour);
        value.TextWrapping = TextWrapping.NoWrap;
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
        OnClick(element, click);
    }

    private static void OnClick(FrameworkElement element, Action click)
    {
        element.Cursor = Cursors.Hand;
        element.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            click();
        };
    }
}
