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
        if (parsed.Exit) return ExitRunning();

        // Already running: show its panel instead of doing nothing.
        using var instance = new Mutex(initiallyOwned: false, InstanceSignals.Instance);
        if (!InstanceStart.ShouldRun(() => InstanceSignals.Signal(InstanceSignals.ShowPanel), () => TakeInstance(instance))) return 0;
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

    /// <summary>
    /// nightcrew --exit: asks the running copy to quit, as Quit does, and waits up to 10 seconds for it to go. 0 once it
    /// has gone or wasn't running, 1 if it is still running.
    /// </summary>
    private static int ExitRunning()
    {
        if (!InstanceSignals.Signal(InstanceSignals.Exit)) return 0;
        using var instance = new Mutex(initiallyOwned: false, InstanceSignals.Instance);
        try
        {
            if (!instance.WaitOne(TimeSpan.FromSeconds(10))) return 1;
        }
        catch (AbandonedMutexException)
        {
            // It ended without releasing the mutex; it's gone all the same, and this thread now owns it.
        }
        instance.ReleaseMutex();
        return 0;
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
        using var instance = new Mutex(initiallyOwned: false, InstanceSignals.Instance);
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
            new EfficiencyMode(AppPaths.EfficiencyFile, log).RestoreAll("uninstall"); // the watchdog that would do it is going
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
        // Crashes on any thread go through recovery once it's wired below; before that there is nothing armed to undo.
        Action<string>? recover = null;
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Crashed(e.Exception, recover, log);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Crashed(e.ExceptionObject as Exception, recover, log);

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        var settings = SettingsStore.Load(AppPaths.SettingsFile, out var firstRun);
        var dryRun = dryRunFlag || settings.DryRun; // the flag is not saved into settings
        log.Info(dryRun ? $"Nightcrew {AppVersion.Text} started (dry run)" : $"Nightcrew {AppVersion.Text} started");

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

        // Apps a crash left in Efficiency mode go back first, with the real class even in a dry run.
        new EfficiencyMode(AppPaths.EfficiencyFile, log).RestoreAll("startup");
        IEfficiencyMode efficiency = dryRun ? new DryRunEfficiency(log) : new EfficiencyMode(AppPaths.EfficiencyFile, log);

        var clock = new SystemClock();
        using var events = new PowerEventsWindow();
        using var realKeepAwake = new KeepAwake();
        using var gpu = new PdhGpuUsage();
        using var transcripts = new WatchingJsonlScanner(new JsonlFileScanner(), clock);
        var realPower = new SystemPower(() => events.Handle);
        IPowerPolicy policy = dryRun ? new DryRunPolicy(realPolicy, log) : realPolicy;
        ISystemPower power = dryRun ? new DryRunSystemPower(realPower, log) : realPower;
        IKeepAwake keepAwake = dryRun ? new DryRunKeepAwake(log) : realKeepAwake;
        var journal = new RestoreJournal(policy, AppPaths.RestoreFile, log, () => RestoreJournal.ArmValuesFor(settings.LidClosedCapHours));

        var grace = TimeSpan.FromMinutes(settings.GraceMinutes);
        var processes = new TickProcessTable(new ToolhelpProcessTable(), clock); // one list per tick, shared
        var gpuSignal = new GpuSignal(gpu, settings.GpuPercent, ProcessNames.From(processes));
        IBusySignal[] signals =
        [
            new TranscriptSignal(transcripts, settings.TranscriptRoots, settings.IgnorePathContains, grace),
            new ClaudeSessionSignal(new SessionFileReader(), processes, settings, () => events.Lid, log),
            new AgentTreeSignal(processes, settings),
            gpuSignal,
        ];
        var activity = new ActivityMonitor(signals, grace);
        var controller = new Controller(settings);
        var coordinator = new EfficiencyCoordinator(efficiency, new EfficiencySource(processes), settings, () => gpuSignal.BusyPids, log);
        var engine = new Engine(activity, controller, journal, policy, keepAwake, power, log, clock, coordinator);

        var guard = new FreezeGuard(clock, TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(10));
        var arguments = dryRunFlag ? "--dry-run" : ""; // a dry run restarts as a dry run
        recover = why => Recovery.Run(why, guard.MayRestart, efficiency, engine, exePath, arguments, log);

        using var tray = new TrayApp(settings, dryRun, exePath, events, activity, controller, engine, log, efficiency,
            () => realPower.IdleTime, coordinator.WouldSlowNow, guard, recover, firstRun);
        Application.Run(tray);
    }

    /// <summary>An exception nothing else caught: put everything back and restart, or, before that's possible, log and exit.</summary>
    private static void Crashed(Exception? exception, Action<string>? recover, ILog log)
    {
        var why = "unhandled exception: " + (exception?.Message ?? "unknown");
        if (recover is not null)
        {
            recover(why);
            return;
        }
        try { log.Warn(why); }
        catch (Exception) { }
        Environment.Exit(3);
    }
}
