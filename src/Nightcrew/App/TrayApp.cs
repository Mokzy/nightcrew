using System.Diagnostics;
using System.Reflection;
using System.Windows.Forms;
using Nightcrew.App.Ui;
using Nightcrew.Core;
using Nightcrew.Platform;

namespace Nightcrew.App;

/// <summary>
/// The tray icon and the click panel with its settings page. A background timer ticks the engine; every engine call
/// holds one lock. Only the engine's immutable snapshot crosses to the UI thread.
/// </summary>
public sealed class TrayApp : ApplicationContext
{
    private readonly object _gate = new();
    private readonly Settings _settings;
    private readonly bool _dryRun;
    private readonly string _exePath;
    private readonly ActivityMonitor _activity;
    private readonly Engine _engine;
    private readonly ILog _log;
    private readonly TrayIcons _icons = new();
    private readonly NotifyIcon _tray = new();
    private readonly PanelWindow _panel;
    private ContextMenuStrip? _fallback;
    private readonly SynchronizationContext _ui;
    private readonly TimeSpan _period;
    private readonly System.Threading.Timer _timer;
    private readonly bool _watchdogMissing;
    private readonly IEfficiencyMode _efficiency;
    private readonly Func<TimeSpan> _idleTime;
    private readonly Func<IReadOnlyList<NamedApp>> _wouldSlowNow;
    private readonly System.Threading.Timer _inputTimer;
    private readonly FreezeGuard _guard;
    private readonly Action<string> _recover;
    private readonly System.Threading.Timer _watch;
    private static readonly TimeSpan WatchEvery = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan InputCheck = TimeSpan.FromSeconds(1);
    private EngineSnapshot _snapshot = EngineSnapshot.Initial; // UI thread
    private PanelView? _view; // UI thread
    private IReadOnlyList<NamedApp>? _addable; // UI thread: the open Add an app list, or null
    private bool _closed;
    private volatile bool _closing; // set first when quitting, so no timer or signal starts new work
    private bool _sessionEnding;
    private readonly (EventWaitHandle Handle, RegisteredWaitHandle Wait)? _showPanelSignal;
    private readonly (EventWaitHandle Handle, RegisteredWaitHandle Wait)? _exitSignal;

    /// <summary>
    /// Subscribes to <paramref name="events"/> but does not own it; the caller disposes it after this. <paramref name="efficiency"/>
    /// is called directly, without the lock, whenever apps must come out of Efficiency mode at once.
    /// <paramref name="wouldSlowNow"/> fills Settings' Add an app list and is called under the lock.
    /// <paramref name="recover"/> puts everything back and restarts Nightcrew when <paramref name="guard"/> says no tick has
    /// finished for too long; it is called without the lock, which the frozen tick holds. On <paramref name="firstRun"/> the
    /// panel opens once, because Windows hides a new tray icon in the overflow.
    /// </summary>
    public TrayApp(Settings settings, bool dryRun, string exePath, PowerEventsWindow events,
        ActivityMonitor activity, Controller controller, Engine engine, ILog log, IEfficiencyMode efficiency, Func<TimeSpan> idleTime,
        Func<IReadOnlyList<NamedApp>> wouldSlowNow, FreezeGuard guard, Action<string> recover, bool firstRun = false)
    {
        _guard = guard;
        _recover = recover;
        _watch = new System.Threading.Timer(_ => Watch(), null, WatchEvery, WatchEvery);
        _efficiency = efficiency;
        _idleTime = idleTime;
        _wouldSlowNow = wouldSlowNow;
        _inputTimer = new System.Threading.Timer(_ => CheckInput(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _settings = settings;
        _dryRun = dryRun;
        _exePath = exePath;
        _activity = activity;
        _engine = engine;
        _log = log;
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _period = TimeSpan.FromSeconds(Math.Max(1, settings.SampleSeconds));
        _timer = new System.Threading.Timer(_ => OnTimer(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _panel = new PanelWindow(
            new PanelActions(
                Hold: () => SetHold(!_snapshot.Hold),
                Pause: () => SetPaused(!_snapshot.Paused),
                Settings: () => ShowPage(settings: true)),
            new SettingsActions(
                Back: () => ShowPage(settings: false),
                BatteryFloor: value => ChangeSetting("Battery floor", () => _settings.BatteryFloorPercent = value),
                LidClosedLimit: value => ChangeSetting("Lid-closed limit", () => _settings.LidClosedCapHours = value),
                LockOnLidClose: on => ChangeSetting("Lock when lid closes", () => _settings.LockOnLidClose = on),
                StartAtLogin: SetStartAtLogin,
                Mark: kind => ChangeSetting("Tray mark", () => _settings.Mark = MarkKinds.Name(kind)),
                OpenLog: OpenLog,
                Quit: Quit,
                EfficiencyWhileAway: on =>
                {
                    _addable = null;
                    ChangeSetting("Efficiency mode while away", () => _settings.EfficiencyWhileAway = on);
                },
                ToggleAddList: ToggleAddList,
                AddFullSpeed: app => SetFullSpeed(app.RootName, app.DisplayName),
                RemoveFullSpeed: name => SetFullSpeed(name, null),
                StayAwakeForApprovals: on => ChangeSetting("Stay awake for approvals", () => _settings.StayAwakeForApprovals = on)));
        UiSafety.Install(_panel.Dispatcher, log);

        if (!dryRun)
        {
            if (settings.StartAtLogin) Guarded("Start at login", () => Autostart.Set(true, exePath));
            var registered = false;
            Guarded("Watchdog task", () => registered = WatchdogTask.Register(exePath, log));
            _watchdogMissing = !registered;
        }

        _tray.MouseClick += OnTrayClick;
        Show(_engine.Snapshot);
        _tray.Visible = true;

        engine.Lid = events.Lid;
        events.LidChanged += lid =>
        {
            if (lid != LidState.Closed) efficiency.RestoreAll("lid opened"); // at once, not after the tick
            Apply("Lid change", () => engine.Lid = lid);
        };
        events.Suspending += () =>
        {
            // Lock-free: the timer thread may hold the gate inside SetSuspendState. Efficiency mode has its own lock.
            efficiency.RestoreAll("suspend");
            engine.NoteSuspend();
        };
        events.Resumed += () => Apply("Resume", controller.OnResume);
        events.SessionEnding += () => efficiency.RestoreAll("session end");
        events.SessionEnding += () => Guarded("Restore at session end", () =>
        {
            _sessionEnding = true; // no tick may re-arm while Windows ends the session
            engine.RestoreNow();
            controller.Reset();
        });
        events.SessionEndCancelled += () => Apply("Session end cancelled", () => _sessionEnding = false);
        events.SessionEnded += () => Guarded("Exit at session end", () =>
        {
            _closing = true;
            _timer.Dispose(); // the settings were already restored on SessionEnding
            ExitThread();
        });

        // Another start of nightcrew.exe: a second start opens the panel, nightcrew --exit quits.
        _showPanelSignal = Listen(InstanceSignals.ShowPanel, OpenPanelAtTray);
        _exitSignal = Listen(InstanceSignals.Exit, Quit);
        if (firstRun) _ui.Post(_ => OpenPanelAtTray(), null);
        SafeChange(_timer, TimeSpan.Zero, _period);
    }

    /// <summary>A named event the running copy owns; each signal runs <paramref name="onUi"/> on the UI thread.</summary>
    private (EventWaitHandle Handle, RegisteredWaitHandle Wait)? Listen(string name, Action onUi)
    {
        try
        {
            var handle = new EventWaitHandle(false, EventResetMode.AutoReset, name);
            var wait = ThreadPool.RegisterWaitForSingleObject(handle, (_, _) => _ui.Post(_ => onUi(), null), null, Timeout.Infinite, executeOnlyOnce: false);
            return (handle, wait);
        }
        catch (Exception ex)
        {
            _log.Warn($"Listening for {name} failed: {ex.Message}");
            return null;
        }
    }

    private static void StopListening((EventWaitHandle Handle, RegisteredWaitHandle Wait)? listener)
    {
        if (listener is not { } value) return;
        value.Wait.Unregister(null);
        value.Handle.Dispose();
    }

    /// <summary>UI thread: the main page, near the clock on the primary screen, where the tray is.</summary>
    private void OpenPanelAtTray()
    {
        if (_closed || _closing || _view is null) return;
        try
        {
            ShowPage(settings: false);
            if (_panel.IsVisible) return;
            var area = Screen.PrimaryScreen?.WorkingArea ?? SystemInformation.WorkingArea;
            _panel.ShowNear(new System.Drawing.Point(area.Right - 1, area.Bottom - 1));
        }
        catch (Exception ex)
        {
            _log.Warn($"Panel failed: {ex.Message}");
        }
    }

    /// <summary>Changing a timer after quitting began is a no-op, not an exception on a pool thread.</summary>
    private static void SafeChange(System.Threading.Timer timer, TimeSpan due, TimeSpan period)
    {
        try
        {
            timer.Change(due, period);
        }
        catch (ObjectDisposedException)
        {
            // Quitting or the session ending disposed it.
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Shutdown();
            _panel.Close();
            _tray.Dispose();
            _fallback?.Dispose();
            _icons.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>Timer thread. Skips the tick if the previous one, or a panel action, still holds the lock.</summary>
    private void OnTimer()
    {
        if (!Monitor.TryEnter(_gate)) return;
        try
        {
            if (_closed || _sessionEnding) return;
            _engine.Tick();
            _guard.TickDone();
            var snapshot = _engine.Snapshot;
            // Check for keyboard and mouse input every second, but only while apps are in Efficiency mode.
            var check = snapshot.EfficiencyApps > 0 ? InputCheck : Timeout.InfiniteTimeSpan;
            SafeChange(_inputTimer, check, check);
            _ui.Post(_ => Show(snapshot), null);
        }
        catch (Exception ex)
        {
            _log.Warn($"Tick failed: {ex.Message}");
        }
        finally
        {
            Monitor.Exit(_gate);
        }
    }

    /// <summary>Its own timer, once a minute, never taking the lock: a tick that hasn't finished for 3 minutes is a freeze.</summary>
    private void Watch()
    {
        if (_closed || _closing || _sessionEnding || !_guard.Frozen) return;
        _recover("no tick finished for 3 minutes");
    }

    /// <summary>UI thread: the tooltip, the tray mark, and the panel if it's open.</summary>
    private void Show(EngineSnapshot snapshot)
    {
        if (_closed) return;
        try
        {
            _snapshot = snapshot;
            var view = PanelModel.From(snapshot, _settings.BatteryFloorPercent, _watchdogMissing);
            _view = view;
            var tooltip = _dryRun ? "[dry run] " + view.Tooltip : view.Tooltip;
            _tray.Text = tooltip.Length > PanelModel.TooltipLimit ? tooltip.Substring(0, PanelModel.TooltipLimit) : tooltip;
            _tray.Icon = _icons.Get(Mark, view.Mark, SystemInformation.SmallIconSize.Width, TaskbarTheme.IsLight());
            if (_panel.IsVisible) RenderPanel();
        }
        catch (Exception ex)
        {
            _log.Warn($"Showing the state failed: {ex.Message}");
        }
    }

    private MarkKind Mark => MarkKinds.Parse(_settings.Mark);

    private void RenderPanel()
    {
        if (_view is not null) _panel.Render(_view, SettingsModel.From(_settings, _addable), Mark);
    }

    /// <summary>Switches page, with the Add an app list closed.</summary>
    private void ShowPage(bool settings)
    {
        _addable = null;
        _panel.ShowPage(settings);
        RenderPanel();
    }

    /// <summary>Opens the Add an app list with the apps Nightcrew would slow right now, or closes it.</summary>
    private void ToggleAddList()
    {
        if (_addable is null)
        {
            IReadOnlyList<NamedApp> apps = [];
            Guarded("Add an app", () => apps = _wouldSlowNow());
            _addable = apps;
        }
        else
        {
            _addable = null;
        }
        RenderPanel();
    }

    /// <summary>
    /// Left click opens the panel on its main page, right click on the settings page; see <see cref="PanelToggle"/>.
    /// If the panel can't open, a right click shows a small menu with Open log and Quit, so Nightcrew can always be quit.
    /// </summary>
    private void OnTrayClick(object? sender, MouseEventArgs e)
    {
        if (e.Button is not (MouseButtons.Left or MouseButtons.Right) || _closed) return;
        var settings = e.Button == MouseButtons.Right;
        try
        {
            if (_view is null) throw new InvalidOperationException("no engine state yet");
            switch (PanelToggle.Decide(_panel.IsVisible, _panel.OnSettings, _panel.JustHid, settings))
            {
                case PanelClick.Close:
                    _panel.HidePanel();
                    break;
                case PanelClick.SwitchPage:
                    ShowPage(settings);
                    break;
                case PanelClick.Open:
                    ShowPage(settings);
                    _panel.ShowNear(Cursor.Position);
                    break;
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"Panel failed: {ex.Message}");
            if (settings) ShowFallbackMenu();
        }
    }

    /// <summary>The way out when the panel can't open: a plain menu with Open log and Quit, at the tray.</summary>
    private void ShowFallbackMenu()
    {
        try
        {
            if (_fallback is null)
            {
                _fallback = new ContextMenuStrip();
                _fallback.Items.Add("Open log", null, (_, _) => OpenLog());
                _fallback.Items.Add("Quit Nightcrew", null, (_, _) => Quit());
            }
            // NotifyIcon's own ShowContextMenu takes the foreground first, so the menu closes on a click elsewhere.
            _tray.ContextMenuStrip = _fallback;
            typeof(NotifyIcon).GetMethod("ShowContextMenu", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(_tray, null);
        }
        catch (Exception ex)
        {
            _log.Warn($"Fallback menu failed: {ex.Message}");
        }
        finally
        {
            _tray.ContextMenuStrip = null; // right click goes back to opening the settings page
        }
    }

    private void OpenLog() => Guarded("Open log", () => Process.Start(AppPaths.LogFile)?.Dispose());

    private void SetHold(bool on) => Apply("Hold awake", () => _activity.Hold = on);

    private void SetPaused(bool on)
    {
        if (on) _efficiency.RestoreAll("pause");
        Apply("Pause", () => _engine.Paused = on);
    }

    /// <summary>
    /// Keeps an app at full speed under the name Settings shows, or with no <paramref name="label"/> takes it off the
    /// list. Closes the Add an app list.
    /// </summary>
    private void SetFullSpeed(string rootName, string? label)
    {
        _addable = null;
        ChangeSetting("Keep at full speed", () =>
        {
            bool Same(string name) => string.Equals(name, rootName, StringComparison.OrdinalIgnoreCase);
            var names = _settings.LeaveAlone.Where(name => !Same(name)).ToList();
            var labels = (_settings.LeaveAloneLabels ?? []).Where(pair => !Same(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value);
            if (label is not null)
            {
                names.Add(rootName);
                labels[rootName] = label;
            }
            _settings.LeaveAlone = names.ToArray();
            _settings.LeaveAloneLabels = labels;
        });
    }

    /// <summary>Timer thread, every second while apps are in Efficiency mode: any input brings them back at once.</summary>
    private void CheckInput()
    {
        if (_closed || _idleTime() >= TimeSpan.FromSeconds(2)) return;
        SafeChange(_inputTimer, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        if (_efficiency.RestoreAll("input") > 0 && !_closed) SafeChange(_timer, TimeSpan.Zero, _period);
    }

    /// <summary>The Run key first: if writing it fails, the setting stays as it was and nothing is saved.</summary>
    private void SetStartAtLogin(bool on) => ChangeSetting("Start at login", () =>
    {
        if (!_dryRun) Autostart.Set(on, _exePath);
        _settings.StartAtLogin = on;
    });

    /// <summary>
    /// Changes and saves a setting under the lock, ticks so it takes effect, and shows it at once: the page, the tray
    /// mark and the main page's facts, without waiting for the tick.
    /// </summary>
    private void ChangeSetting(string what, Action change)
    {
        Apply(what, () =>
        {
            change();
            Save();
        });
        Show(_snapshot);
    }

    private void Guarded(string what, Action action)
    {
        lock (_gate)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                _log.Warn($"{what} failed: {ex.Message}");
            }
        }
    }

    /// <summary>Applies a change under the lock, then ticks at once so it takes effect and shows.</summary>
    private void Apply(string what, Action change)
    {
        Guarded(what, change);
        if (!_closed) SafeChange(_timer, TimeSpan.Zero, _period);
    }

    private void Save() => SettingsStore.Save(AppPaths.SettingsFile, _settings);

    /// <summary>Exits even if putting the settings back fails, so the process never lingers without its icon.</summary>
    private void Quit()
    {
        try
        {
            Shutdown();
        }
        finally
        {
            ExitThread();
        }
    }

    /// <summary>Stops ticking and puts the owner's power settings back, once.</summary>
    private void Shutdown()
    {
        _closing = true;
        StopListening(_showPanelSignal);
        StopListening(_exitSignal);
        _watch.Dispose();
        _timer.Dispose();
        _inputTimer.Dispose();
        _efficiency.RestoreAll("quit");
        Guarded("Restore on exit", () =>
        {
            if (_closed) return;
            _closed = true;
            _engine.RestoreNow();
        });
        _tray.Visible = false;
        _panel.HidePanel();
    }
}
