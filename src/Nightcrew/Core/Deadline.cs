namespace Nightcrew.Core;

/// <summary>Work that must never hang Nightcrew: recovery and quitting, which can run while a stuck tick holds a lock.</summary>
public static class Deadline
{
    /// <summary>
    /// Runs the steps in order on a background thread, each one even if the one before it threw. True if they all
    /// finished within <paramref name="deadline"/>; otherwise the thread is left behind and the caller moves on.
    /// </summary>
    public static bool Run(TimeSpan deadline, params Action[] steps)
    {
        var worker = new Thread(() =>
        {
            foreach (var step in steps)
            {
                try
                {
                    step();
                }
                catch (Exception)
                {
                    // The next step may still put something back.
                }
            }
        })
        { IsBackground = true, Name = "Nightcrew deadline" };
        worker.Start();
        return worker.Join(deadline);
    }

    /// <summary>Runs <paramref name="action"/> holding <paramref name="gate"/>; false, without running it, if the gate stays held for <paramref name="timeout"/>.</summary>
    public static bool Under(object gate, TimeSpan timeout, Action action)
    {
        if (!Monitor.TryEnter(gate, timeout)) return false;
        try
        {
            action();
            return true;
        }
        finally
        {
            Monitor.Exit(gate);
        }
    }
}
