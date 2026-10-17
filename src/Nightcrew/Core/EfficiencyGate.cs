namespace Nightcrew.Core;

/// <summary>Whether apps should be in Efficiency mode right now: Windows would have slept, but an agent is working.</summary>
public static class EfficiencyGate
{
    /// <param name="mode">Only Armed counts: an agent is working and Nightcrew holds the laptop awake.</param>
    /// <param name="ownerLid">The user's own lid action for the current power source, not the Do nothing Nightcrew writes.</param>
    public static bool Wanted(Mode mode, LidState lid, TimeSpan idleTime, LidAction ownerLid, Settings settings) =>
        settings.EfficiencyWhileAway
        && mode == Mode.Armed
        && lid == LidState.Closed
        && idleTime >= TimeSpan.FromMinutes(settings.AwayMinutes)
        && ownerLid != LidAction.DoNothing;
}
