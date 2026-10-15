using Nightcrew.Core;
using Nightcrew.Platform;

namespace Nightcrew.App;

/// <summary>Runs from the scheduled task: if the app is gone but left records, restores the power settings and takes apps out of Efficiency mode.</summary>
public static class Watchdog
{
    public static int Run(ILog log)
    {
        using var instance = new Mutex(initiallyOwned: false, @"Local\Nightcrew.Instance");
        bool owned;
        try { owned = instance.WaitOne(0); }
        catch (AbandonedMutexException) { owned = true; }
        if (!owned) return 0;

        try
        {
            var journal = new RestoreJournal(new PowrProfPolicy(), AppPaths.RestoreFile, log);
            if (journal.HasRecord)
            {
                log.Info("Watchdog: Nightcrew is not running but left a restore record; restoring power settings");
                journal.Restore();
            }
            new EfficiencyMode(AppPaths.EfficiencyFile, log).RestoreAll("watchdog");
            return 0;
        }
        finally
        {
            instance.ReleaseMutex();
        }
    }
}
