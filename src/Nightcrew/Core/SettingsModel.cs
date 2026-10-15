namespace Nightcrew.Core;

/// <summary>One option in a row of choices, such as "20%" for the battery floor.</summary>
public sealed record Choice<T>(string Label, T Value, bool Selected);

/// <summary>An app on the Keep at full speed list: the name shown, and the process name on <see cref="Settings.LeaveAlone"/>.</summary>
public sealed record FullSpeedApp(string Label, string Name);

/// <summary>
/// Everything the settings page shows, from the current settings. <paramref name="Addable"/> is the open Add an app
/// list, or null while it's closed.
/// </summary>
public sealed record SettingsPage(
    IReadOnlyList<Choice<int>> BatteryFloor,
    IReadOnlyList<Choice<int>> LidClosedLimit,
    bool LockOnLidClose,
    bool StartAtLogin,
    IReadOnlyList<Choice<MarkKind>> Mark,
    bool EfficiencyWhileAway = true,
    IReadOnlyList<FullSpeedApp>? FullSpeed = null,
    IReadOnlyList<NamedApp>? Addable = null);

/// <summary>Turns the settings into the settings page's choices. Pure, so the page's words are unit-tested.</summary>
public static class SettingsModel
{
    private static readonly int[] BatteryFloors = [10, 15, 20, 30];
    private static readonly int[] LidClosedLimits = [2, 4, 8, 12];

    /// <param name="addable">The apps Nightcrew would slow right now, while the Add an app list is open.</param>
    public static SettingsPage From(Settings settings, IReadOnlyList<NamedApp>? addable = null)
    {
        var mark = MarkKinds.Parse(settings.Mark);
        var kept = new HashSet<string>(settings.LeaveAlone, StringComparer.OrdinalIgnoreCase);
        var labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in settings.LeaveAloneLabels ?? []) labels[pair.Key] = pair.Value;
        return new SettingsPage(
            Choices(BatteryFloors, settings.BatteryFloorPercent, value => value + "%"),
            Choices(LidClosedLimits, settings.LidClosedCapHours, value => value + " h"),
            settings.LockOnLidClose,
            settings.StartAtLogin,
            [new("Lantern", MarkKind.Lantern, mark == MarkKind.Lantern), new("Moon and star", MarkKind.MoonAndStar, mark == MarkKind.MoonAndStar)],
            settings.EfficiencyWhileAway,
            settings.LeaveAlone
                .Select(name => new FullSpeedApp(labels.TryGetValue(name, out var label) ? label : name, name))
                .OrderBy(app => app.Label, StringComparer.CurrentCultureIgnoreCase)
                .ToList(),
            addable?.Where(app => !kept.Contains(app.RootName))
                .OrderBy(app => app.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToList());
    }

    /// <summary>
    /// The usual values then Off (0). A value set by hand in settings.json gets its own choice, in order; zero or less
    /// is Off, as the controller treats it.
    /// </summary>
    private static List<Choice<int>> Choices(int[] usual, int setting, Func<int, string> label)
    {
        var current = Math.Max(0, setting);
        var values = usual.ToList();
        if (current > 0 && !values.Contains(current))
        {
            values.Add(current);
            values.Sort();
        }
        values.Add(0);
        return values.Select(value => new Choice<int>(value == 0 ? "Off" : label(value), value, value == current)).ToList();
    }
}
