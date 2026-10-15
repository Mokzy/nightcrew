using System.Windows.Forms;
using Nightcrew.App;
using Nightcrew.Core;
using Nightcrew.Platform;

namespace Nightcrew;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var parsed = CommandLine.Parse(args);
        var log = new FileLog(AppPaths.LogFile);
        var exePath = Application.ExecutablePath;
        if (parsed.Watchdog) return Watchdog.Run(log);
        if (parsed.Uninstall) return Uninstall(exePath, log);

        using var instance = new Mutex(initiallyOwned: false, @"Local\Nightcrew.Instance");
        if (!TakeInstance(instance)) return 0;
        try
        {
            RunTray(parsed.DryRun, exePath, log);
            return 0;
        }
        finally
        {
            instance.ReleaseMutex();
        }
    }

    /// <summary>False if Nightcrew is already running. Waits briefly because the watchdog holds the mutex while it checks.</summary>
    private static bool TakeInstance(Mutex instance)
    {
        try
        {
            return instance.WaitOne(TimeSpan.FromSeconds(5));
        }
        catch (AbandonedMutexException)
        {
            return true;
        }
    }

    /// <summary>
    /// Refuses while the tray app runs, because it could change the power settings again after the restore.
    /// Waits up to 5 seconds first, because the watchdog holds the mutex briefly while it checks.
    /// </summary>
    private static int Uninstall(string exePath, ILog log)
    {
        using var instance = new Mutex(initiallyOwned: false, @"Local\Nightcrew.Instance");
        bool owned;
        try { owned = instance.WaitOne(TimeSpan.FromSeconds(5)); }
        catch (AbandonedMutexException) { owned = true; }
        if (!owned)
        {
            MessageBox.Show("Quit Nightcrew from the tray first, then run nightcrew --uninstall again.", "Nightcrew");
            return 1;
        }

        try
        {
            var restored = new RestoreJournal(new PowrProfPolicy(), AppPaths.RestoreFile, log).Restore();
            Autostart.Set(false, exePath);
            WatchdogTask.Unregister(log);
            log.Info("Uninstalled: start at login and the watchdog task removed");
            MessageBox.Show(
                restored ? "Nightcrew removed." : "Nightcrew removed, but your power settings could not be restored. See the log in %LOCALAPPDATA%\\Nightcrew.",
                "Nightcrew");
            return 0;
        }
        finally
        {
            instance.ReleaseMutex();
        }
    }

    private static void RunTray(bool dryRunFlag, string exePath, ILog log)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        var settings = SettingsStore.Load(AppPaths.SettingsFile);
        var dryRun = dryRunFlag || settings.DryRun; // the flag is not saved into settings
        log.Info(dryRun ? "Nightcrew started (dry run)" : "Nightcrew started");

        // A record left by a crash or power cut: put the owner's settings back first. This uses the real
        // policy even in a dry run, so a dry run never deletes a real record without restoring it.
        var realPolicy = new PowrProfPolicy();
        var leftover = new RestoreJournal(realPolicy, AppPaths.RestoreFile, log);
        if (leftover.HasRecord) leftover.Restore();
        if (dryRun && leftover.HasRecord)
        {
            // The dry-run journal only logs writes, so it must never touch a real record it could not restore.
            log.Warn("Dry run refused: a real restore record could not be restored");
            MessageBox.Show("Nightcrew couldn't put back power settings left by an earlier run. Start it normally (without --dry-run) so it can restore them.", "Nightcrew");
            return;
        }

        // The panel is small and rarely open; GPU rendering would keep about 75 MB allocated for the rest of the session.
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

        var clock = new SystemClock();
        using var events = new PowerEventsWindow();
        using var realKeepAwake = new KeepAwake();
        using var gpu = new PdhGpuUsage();
        using var transcripts = new WatchingJsonlScanner(new JsonlFileScanner(), clock);
        var realPower = new SystemPower(() => events.Handle);
        IPowerPolicy policy = dryRun ? new DryRunPolicy(realPolicy, log) : realPolicy;
        ISystemPower power = dryRun ? new DryRunSystemPower(realPower, log) : realPower;
        IKeepAwake keepAwake = dryRun ? new DryRunKeepAwake(log) : realKeepAwake;
        var journal = new RestoreJournal(policy, AppPaths.RestoreFile, log);

        var grace = TimeSpan.FromMinutes(settings.GraceMinutes);
        IBusySignal[] signals =
        [
            new TranscriptSignal(transcripts, settings.TranscriptRoots, settings.IgnorePathContains, grace),
            new AgentTreeSignal(new ToolhelpProcessTable(), settings),
            new GpuSignal(gpu, settings.GpuPercent, ProcessName),
        ];
        var activity = new ActivityMonitor(signals, grace);
        var controller = new Controller(settings);
        var engine = new Engine(activity, controller, journal, policy, keepAwake, power, log, clock);

        using var tray = new TrayApp(settings, dryRun, exePath, events, activity, controller, engine, log);
        Application.Run(tray);
    }

    /// <summary>For the panel's holder rows; null when the process has already gone.</summary>
    private static string? ProcessName(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return process.ProcessName;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
