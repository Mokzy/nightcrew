namespace Nightcrew.Core;

public enum PanelState { Quiet, Working, Paused }

/// <summary>Which version of the mark to draw: unlit, lit, or lit with the lid closed.</summary>
public enum MarkState { Idle, Live, LidClosed }

public sealed record PanelFact(string Label, string Value);

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
    string Tooltip);

/// <summary>Turns an engine snapshot into the panel's words. Pure, so every string is unit-tested.</summary>
public static class PanelModel
{
    public const int TooltipLimit = 63;

    public static PanelView From(EngineSnapshot snapshot, int batteryFloorPercent, bool watchdogMissing = false)
    {
        var warning = WarningText(snapshot.Warning, watchdogMissing);
        var battery = new PanelFact("Battery", Battery(snapshot, batteryFloorPercent));

        if (snapshot.Paused)
        {
            return new PanelView(PanelState.Paused, "Paused.", null, "Windows handles the lid until you resume.",
                [], [battery], snapshot.Hold, "Resume", warning, MarkState.Idle, Tooltip("paused"));
        }

        if (snapshot.Mode == Mode.Armed || snapshot.Live)
        {
            var mark = snapshot.Lid == LidState.Closed ? MarkState.LidClosed : MarkState.Live;
            var first = snapshot.Holders.Count > 0 ? $" ({snapshot.Holders[0].Label})" : "";
            return new PanelView(PanelState.Working, "Working ", "through the night.",
                "Close the lid. Your laptop keeps working, then does what your lid normally does.",
                snapshot.Holders, [battery, new PanelFact("When done", WhenDone(snapshot.OwnerLid))],
                snapshot.Hold, "Pause", warning, mark, Tooltip("working" + first));
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
        _ => watchdogMissing ? "The safety net isn't installed. Settings may stay changed if Nightcrew crashes." : null,
    };

    private static string Tooltip(string state)
    {
        var text = "Nightcrew · " + state;
        return text.Length <= TooltipLimit ? text : text.Substring(0, TooltipLimit);
    }
}
