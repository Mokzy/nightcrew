using System.Diagnostics;
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
    private readonly SynchronizationContext _ui;
    private readonly TimeSpan _period;
    private readonly System.Threading.Timer _timer;
    private readonly bool _watchdogMissing;
    private EngineSnapshot _snapshot = EngineSnapshot.Initial; // UI thread
    private PanelView? _view; // UI thread
    private bool _closed;
    private bool _sessionEnding;

    /// <summary>Subscribes to <paramref name="events"/> but does not own it; the caller disposes it after this.</summary>
    public TrayApp(Settings settings, bool dryRun, string exePath, PowerEventsWindow events,
        ActivityMonitor activity, Controller controller, Engine engine, ILog log)
    {
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
                Settings: () => _panel!.ShowPage(settings: true)),
            new SettingsActions(
                Back: () => _panel!.ShowPage(settings: false),
                BatteryFloor: value => ChangeSetting("Battery floor", () => _settings.BatteryFloorPercent = value),
                LidClosedLimit: value => ChangeSetting("Lid-closed limit", () => _settings.LidClosedCapHours = value),
                LockOnLidClose: on => ChangeSetting("Lock when lid closes", () => _settings.LockOnLidClose = on),
                StartAtLogin: SetStartAtLogin,
                Mark: kind => ChangeSetting("Tray mark", () => _settings.Mark = MarkKinds.Name(kind)),
                OpenLog: () => Guarded("Open log", () => Process.Start(AppPaths.LogFile)?.Dispose()),
                Quit: Quit));
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
        events.LidChanged += lid => Apply("Lid change", () => engine.Lid = lid);
        events.Suspending += engine.NoteSuspend; // lock-free: the timer thread may hold the gate inside SetSuspendState
        events.Resumed += () => Apply("Resume", controller.OnResume);
        events.SessionEnding += () => Guarded("Restore at session end", () =>
        {
            _sessionEnding = true; // no tick may re-arm while Windows ends the session
            engine.RestoreNow();
            controller.Reset();
        });
        events.SessionEndCancelled += () => Apply("Session end cancelled", () => _sessionEnding = false);
        events.SessionEnded += () => Guarded("Exit at session end", () =>
        {
            _timer.Dispose(); // the settings were already restored on SessionEnding
            ExitThread();
        });
        _timer.Change(TimeSpan.Zero, _period);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Shutdown();
            _panel.Close();
            _tray.Dispose();
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
            var snapshot = _engine.Snapshot;
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
        if (_view is not null) _panel.Render(_view, SettingsModel.From(_settings), Mark);
    }

    /// <summary>
    /// Left click opens the panel on its main page, right click on the settings page. Clicking the icon again closes
    /// it; the other button switches page.
    /// </summary>
    private void OnTrayClick(object? sender, MouseEventArgs e)
    {
        if (e.Button is not (MouseButtons.Left or MouseButtons.Right) || _closed || _view is null) return;
        var settings = e.Button == MouseButtons.Right;
        try
        {
            if (_panel.IsVisible && _panel.OnSettings == settings)
            {
                _panel.HidePanel();
            }
            else if (_panel.IsVisible)
            {
                _panel.ShowPage(settings);
            }
            else if (!_panel.JustHid)
            {
                _panel.ShowPage(settings);
                RenderPanel();
                _panel.ShowNear(Cursor.Position);
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"Panel failed: {ex.Message}");
        }
    }

    private void SetHold(bool on) => Apply("Hold awake", () => _activity.Hold = on);

    private void SetPaused(bool on) => Apply("Pause", () => _engine.Paused = on);

    private void SetStartAtLogin(bool on) => ChangeSetting("Start at login", () =>
    {
        _settings.StartAtLogin = on;
        if (!_dryRun) Autostart.Set(on, _exePath);
    });

    /// <summary>Changes and saves a setting under the lock, ticks so it takes effect, and redraws the page at once.</summary>
    private void ChangeSetting(string what, Action change)
    {
        Apply(what, () =>
        {
            change();
            Save();
        });
        RenderPanel();
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
        if (!_closed) _timer.Change(TimeSpan.Zero, _period);
    }

    private void Save() => SettingsStore.Save(AppPaths.SettingsFile, _settings);

    private void Quit()
    {
        Shutdown();
        ExitThread();
    }

    /// <summary>Stops ticking and puts the owner's power settings back, once.</summary>
    private void Shutdown()
    {
        _timer.Dispose();
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
