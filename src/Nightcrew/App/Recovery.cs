using System.Diagnostics;
using Nightcrew.Core;

namespace Nightcrew.App;

/// <summary>
/// The way out of a frozen check or a crash: apps out of Efficiency mode, the keep-awake request released and the power
/// settings put back, then a fresh start and an exit. Each step is tried even if the one before it failed. Runs once.
/// </summary>
public static class Recovery
{
    private static int _started;

    /// <param name="arguments">The command line for the new copy, so a dry run restarts as a dry run.</param>
    public static void Run(string why, bool restart, IEfficiencyMode efficiency, Engine engine, string exePath, string arguments, ILog log)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        Try(() => log.Warn($"Recovering: {why}"));
        Try(() => efficiency.RestoreAll("recovery"));
        Try(engine.RestoreNow);
        if (restart) Try(() => Process.Start(new ProcessStartInfo(exePath, arguments) { UseShellExecute = false })?.Dispose());
        else Try(() => log.Info("Not restarting: Nightcrew started less than 10 minutes ago"));
        Environment.Exit(3);
    }

    private static void Try(Action action)
    {
        try
        {
            action();
        }
        catch (Exception)
        {
            // Recovery keeps going: the next step may still put something back.
        }
    }
}
