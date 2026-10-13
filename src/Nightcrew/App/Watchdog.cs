using Nightcrew.Core;
using Nightcrew.Platform;

namespace Nightcrew.App;

/// <summary>Runs from the scheduled task: restores the owner's power settings if the app is gone but left a record.</summary>
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
            return 0;
        }
        finally
        {
            instance.ReleaseMutex();
        }
    }
}
