namespace Nightcrew.App;

/// <summary>What a start of the tray app does when another copy may already be running.</summary>
public static class InstanceStart
{
    /// <summary>
    /// True if this start runs the tray. A running copy answers the panel signal, so this start ends at once instead
    /// of waiting for the instance lock. Only when nothing answers does it wait: the watchdog holds the lock briefly
    /// and doesn't answer. Asking first also means a start can't take over from a copy that is quitting.
    /// </summary>
    public static bool ShouldRun(Func<bool> showRunningPanel, Func<bool> takeInstance)
    {
        if (showRunningPanel()) return false;
        if (takeInstance()) return true;
        showRunningPanel(); // a copy that started while this one waited
        return false;
    }
}
