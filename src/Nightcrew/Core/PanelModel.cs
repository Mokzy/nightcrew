namespace Nightcrew.Core;

public enum PanelState { Quiet, Working, Paused }

/// <summary>Which version of the mark to draw: unlit, lit, or lit with the lid closed.</summary>
public enum MarkState { Idle, Live, LidClosed }

public sealed record PanelFact(string Label, string Value);

/// <summary>An app from the most recent time away, and whether it's on the Always leave alone list.</summary>
public sealed record AwayRow(string DisplayName, string RootName, bool LeftAlone);

/// <summary>Everything the click panel and the tray tooltip show, as plain strings.</summary>
public sealed record PanelView(
    PanelState State,
    string Headline,
    string? HeadlineItalic,
    string SubLine,
    IReadOnlyList<Holder> Holders,
    IReadOnlyList<PanelFact> Facts,
    bool HoldOn,
    string PauseLabel,
    string? Warning,
    MarkState Mark,
    string Tooltip,
    IReadOnlyList<AwayRow>? Away = null);

/// <summary>Turns an engine snapshot into the panel's words. Pure, so every string is unit-tested.</summary>
public static class PanelModel
{
    public const int TooltipLimit = 63;

    /// <param name="leaveAlone">The Always leave alone list, to show which apps from the time away are on it.</param>
    public static PanelView From(EngineSnapshot snapshot, int batteryFloorPercent, bool watchdogMissing = false,
        IReadOnlyCollection<string>? leaveAlone = null)
    {
        var view = MainPage(snapshot, batteryFloorPercent, watchdogMissing);
        // Once you're back: the lid is open and the time away has ended.
        if (snapshot.Lid == LidState.Closed || snapshot.Away is not { Count: > 0 } away) return view;
        var kept = new HashSet<string>(leaveAlone ?? [], StringComparer.OrdinalIgnoreCase);
        return view with { Away = away.Select(app => new AwayRow(app.DisplayName, app.RootName, kept.Contains(app.RootName))).ToList() };
    }

    private static PanelView MainPage(EngineSnapshot snapshot, int batteryFloorPercent, bool watchdogMissing)
    {
        var warning = WarningText(snapshot.Warning, watchdogMissing);
        var battery = new PanelFact("Battery", Battery(snapshot, batteryFloorPercent));

        if (snapshot.Paused)
        {
            return new PanelView(PanelState.Paused, "Paused.", null, "Windows handles the lid until you resume.",
                [], [battery], snapshot.Hold, "Resume", warning, MarkState.Idle, Tooltip("paused"));
        }

        if (snapshot.Mode == Mode.Armed)
        {
            var mark = snapshot.Lid == LidState.Closed ? MarkState.LidClosed : MarkState.Live;
            var first = snapshot.Holders.Count > 0 ? $" ({snapshot.Holders[0].Label})" : "";
            return new PanelView(PanelState.Working, "Working ", "through the night.",
                "Close the lid. Your laptop keeps working, then does what your lid normally does.",
                snapshot.Holders, [battery, new PanelFact("When done", WhenDone(snapshot.OwnerLid))],
                snapshot.Hold, "Pause", warning, mark, Tooltip("working" + first));
        }

        if (snapshot.Live)
        {
            // An agent is working but Nightcrew isn't holding the laptop awake: a policy block, a failed arm backing
            // off, or a stop at the battery floor or lid-closed limit. The mark stays unlit and the lid must stay open.
            return new PanelView(PanelState.Working, "Working ", "with the lid open.",
                "Nightcrew can't keep the laptop awake with the lid closed right now.",
                snapshot.Holders, [battery, new PanelFact("When the lid closes", LidCloses(snapshot.OwnerLid))],
                snapshot.Hold, "Pause", warning, MarkState.Idle, Tooltip("working, keep the lid open"));
        }

        return new PanelView(PanelState.Quiet, "All quiet.", null,
            "No agent is working. Close the lid and your laptop does what Windows says.",
            [], [battery, new PanelFact("When the lid closes", LidCloses(snapshot.OwnerLid))],
            snapshot.Hold, "Pause", warning, MarkState.Idle, Tooltip("all quiet"));
    }

    private static string Battery(EngineSnapshot snapshot, int floor) =>
        snapshot.BatteryPercent is not { } percent ? "No battery"
        : snapshot.Source == PowerSource.DC && floor > 0 ? $"{percent}% · stops at {floor}%"
        : $"{percent}%";

    private static string WhenDone(LidAction action) => action switch
    {
        LidAction.Sleep => "Sleeps",
        LidAction.DoNothing => "Stays on",
        _ => "Hibernates", // Hibernate, and Shut down, which Nightcrew turns into hibernate
    };

    private static string LidCloses(LidAction action) => action switch
    {
        LidAction.Sleep => "Sleep",
        LidAction.Hibernate => "Hibernate",
        LidAction.ShutDown => "Shut down",
        _ => "Do nothing",
    };

    private static string? WarningText(EngineWarning warning, bool watchdogMissing) => warning switch
    {
        EngineWarning.PolicyBlocks => "Windows policy blocks lid changes here. Keep the lid open.",
        EngineWarning.ArmBackoff => "Couldn't change power settings. Trying again in a few minutes.",
        EngineWarning.RestorePending => "Your power settings aren't back yet. Retrying.",
        EngineWarning.Stopped => "Stopped early at the battery floor or lid-closed limit. Open the lid to start again.",
        _ => watchdogMissing ? "The safety net isn't installed. Settings may stay changed if Nightcrew crashes." : null,
    };

    private static string Tooltip(string state)
    {
        var text = "Nightcrew · " + state;
        return text.Length <= TooltipLimit ? text : text.Substring(0, TooltipLimit);
    }
}
