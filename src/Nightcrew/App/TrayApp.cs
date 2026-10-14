using System.Diagnostics;
using System.Reflection;
using System.Windows.Forms;
using Nightcrew.App.Ui;
using Nightcrew.Core;
using Nightcrew.Platform;

namespace Nightcrew.App;

/// <summary>
/// The tray icon, its menu and the click panel. A background timer ticks the engine; every engine call holds one
/// lock. Only the engine's immutable snapshot crosses to the UI thread.
/// </summary>
public sealed class TrayApp : ApplicationContext
{
    private readonly object _gate = new();
    private readonly Settings _settings;
    private readonly bool _dryRun;
    private readonly ActivityMonitor _activity;
    private readonly Engine _engine;
    private readonly ILog _log;
    private readonly TrayIcons _icons = new();
    private readonly ContextMenuStrip _menu = new();
    private readonly ToolStripMenuItem _status = new() { Enabled = false };
    private readonly ToolStripMenuItem _holdItem;
    private readonly ToolStripMenuItem _pauseItem;
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
        _activity = activity;
        _engine = engine;
        _log = log;
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _period = TimeSpan.FromSeconds(Math.Max(1, settings.SampleSeconds));
        _timer = new System.Threading.Timer(_ => OnTimer(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _panel = new PanelWindow(new PanelActions(
            Hold: () => SetHold(!_snapshot.Hold),
            Pause: () => SetPaused(!_snapshot.Paused),
            Settings: ShowMenuFromPanel));
        UiSafety.Install(_panel.Dispatcher, log);

        _holdItem = Toggle("Hold awake", false, SetHold);
        _pauseItem = Toggle("Pause", false, SetPaused);
        _menu.Items.Add(_status);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_holdItem);
        _menu.Items.Add(_pauseItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(Choice("Battery floor", [10, 15, 20, 30, 0], "%", settings.BatteryFloorPercent,
            value => settings.BatteryFloorPercent = value));
        _menu.Items.Add(Choice("Lid-closed limit", [2, 4, 8, 12, 0], " h", settings.LidClosedCapHours,
            value => settings.LidClosedCapHours = value));
        _menu.Items.Add(Toggle("Lock when lid closes", settings.LockOnLidClose,
            on => Apply("Lock when lid closes", () => { settings.LockOnLidClose = on; Save(); })));
        _menu.Items.Add(Toggle("Start at login", settings.StartAtLogin, on => Apply("Start at login", () =>
        {
            settings.StartAtLogin = on;
            Save();
            if (!dryRun) Autostart.Set(on, exePath);
        })));
        _menu.Items.Add(MarkChoice());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Open log", null, (_, _) => Guarded("Open log", () => Process.Start(AppPaths.LogFile)?.Dispose()));
        _menu.Items.Add("Quit", null, (_, _) => Quit());

        if (!dryRun)
        {
            if (settings.StartAtLogin) Guarded("Start at login", () => Autostart.Set(true, exePath));
            var registered = false;
            Guarded("Watchdog task", () => registered = WatchdogTask.Register(exePath, log));
            _watchdogMissing = !registered;
        }

        _tray.ContextMenuStrip = _menu;
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
            _menu.Dispose();
            _icons.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>Timer thread. Skips the tick if the previous one, or a menu action, still holds the lock.</summary>
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

    /// <summary>UI thread: the tooltip, the menu's status line and checks, the tray mark, and the panel if it's open.</summary>
    private void Show(EngineSnapshot snapshot)
    {
        if (_closed) return;
        try
        {
            _snapshot = snapshot;
            var view = PanelModel.From(snapshot, _settings.BatteryFloorPercent, _watchdogMissing);
            _view = view;
            var tooltip = _dryRun ? "[dry run] " + view.Tooltip : view.Tooltip;
            _status.Text = tooltip;
            _tray.Text = tooltip.Length > PanelModel.TooltipLimit ? tooltip.Substring(0, PanelModel.TooltipLimit) : tooltip;
            _tray.Icon = _icons.Get(Mark, view.Mark, SystemInformation.SmallIconSize.Width, TaskbarTheme.IsLight());
            _holdItem.Checked = snapshot.Hold;
            _pauseItem.Checked = snapshot.Paused;
            if (_panel.IsVisible) _panel.Render(view, Mark);
        }
        catch (Exception ex)
        {
            _log.Warn($"Showing the state failed: {ex.Message}");
        }
    }

    private MarkKind Mark => MarkKinds.Parse(_settings.Mark);

    /// <summary>Left click toggles the panel. Right click is the menu, which NotifyIcon handles itself.</summary>
    private void OnTrayClick(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || _closed || _view is null) return;
        try
        {
            if (_panel.IsVisible)
            {
                _panel.HidePanel();
            }
            else if (!_panel.JustHid)
            {
                _panel.Render(_view, Mark);
                _panel.ShowNear(Cursor.Position);
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"Panel failed: {ex.Message}");
        }
    }

    /// <summary>The panel's Settings link opens the same menu as a right click, at the tray.</summary>
    private void ShowMenuFromPanel()
    {
        _panel.HidePanel();
        var showContextMenu = typeof(NotifyIcon).GetMethod("ShowContextMenu", BindingFlags.Instance | BindingFlags.NonPublic);
        if (showContextMenu is not null) showContextMenu.Invoke(_tray, null);
        else _menu.Show(Cursor.Position);
    }

    private void SetHold(bool on) => Apply("Hold awake", () => _activity.Hold = on);

    private void SetPaused(bool on) => Apply("Pause", () => _engine.Paused = on);

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

    private static ToolStripMenuItem Toggle(string text, bool initial, Action<bool> apply)
    {
        var item = new ToolStripMenuItem(text) { Checked = initial };
        item.Click += (_, _) =>
        {
            item.Checked = !item.Checked;
            apply(item.Checked);
        };
        return item;
    }

    /// <summary>A submenu of values with one checked; 0 is shown as Off.</summary>
    private ToolStripMenuItem Choice(string text, int[] values, string unit, int current, Action<int> set)
    {
        var parent = new ToolStripMenuItem(text);
        foreach (var value in values)
        {
            var item = new ToolStripMenuItem(value == 0 ? "Off" : value + unit) { Checked = value == current };
            item.Click += (_, _) =>
            {
                foreach (ToolStripMenuItem sibling in parent.DropDownItems) sibling.Checked = sibling == item;
                Apply(text, () => { set(value); Save(); });
            };
            parent.DropDownItems.Add(item);
        }
        return parent;
    }

    /// <summary>Lantern or moon and star, until the owner settles on one.</summary>
    private ToolStripMenuItem MarkChoice()
    {
        var parent = new ToolStripMenuItem("Tray mark");
        foreach (var (kind, text) in new[] { (MarkKind.Lantern, "Lantern"), (MarkKind.MoonAndStar, "Moon and star") })
        {
            var item = new ToolStripMenuItem(text) { Checked = Mark == kind };
            item.Click += (_, _) =>
            {
                foreach (ToolStripMenuItem sibling in parent.DropDownItems) sibling.Checked = sibling == item;
                Apply("Tray mark", () => { _settings.Mark = MarkKinds.Name(kind); Save(); });
            };
            parent.DropDownItems.Add(item);
        }
        return parent;
    }

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
