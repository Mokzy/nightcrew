using System.Diagnostics;
using Nightcrew.Core;

namespace Nightcrew.App;

/// <summary>
/// The way out of a frozen check or a crash: apps out of Efficiency mode, the keep-awake request released and the power
/// settings put back, then a fresh start and an exit. Each step is tried even if the one before it failed, and the steps
/// get 30 seconds, because the stuck tick may hold a lock they need: after that Nightcrew exits anyway, which frees its
/// instance so the watchdog task can put the settings back. Runs once.
/// </summary>
public static class Recovery
{
    private static readonly TimeSpan StepsDeadline = TimeSpan.FromSeconds(30);
    private static int _started;

    /// <param name="arguments">The command line for the new copy, so a dry run restarts as a dry run.</param>
    public static void Run(string why, bool restart, IEfficiencyMode efficiency, Engine engine, string exePath, string arguments, ILog log)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        var finished = Deadline.Run(StepsDeadline,
            () => log.Warn($"Recovering: {why}"),
            () => efficiency.RestoreAll("recovery"),
            engine.RestoreNow);
        if (!finished) Deadline.Run(TimeSpan.FromSeconds(2), () => log.Warn("Recovery didn't finish in 30 seconds; exiting so the watchdog can put the settings back"));
        if (restart) Deadline.Run(TimeSpan.FromSeconds(5), () => Process.Start(new ProcessStartInfo(exePath, arguments) { UseShellExecute = false })?.Dispose());
        else Deadline.Run(TimeSpan.FromSeconds(2), () => log.Info("Not restarting: Nightcrew started less than 10 minutes ago, or it's quitting"));
        Environment.Exit(3);
    }
}
