using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using Nightcrew.Core;
using Nightcrew.Platform;

namespace Nightcrew.App;

/// <summary>The tray icon and its menu. A background timer ticks the engine; every engine call holds one lock.</summary>
public sealed class TrayApp : ApplicationContext
{
    private readonly object _gate = new();
    private readonly Settings _settings;
    private readonly bool _dryRun;
    private readonly Controller _controller;
    private readonly Engine _engine;
    private readonly ILog _log;
    private readonly TrayIcons _icons = new();
    private readonly ContextMenuStrip _menu = new();
    private readonly ToolStripMenuItem _status = new() { Enabled = false };
    private readonly NotifyIcon _tray = new();
    private readonly SynchronizationContext _ui;
    private readonly TimeSpan _period;
    private readonly System.Threading.Timer _timer;
    private bool _closed;
    private bool _sessionEnding;

    /// <summary>Subscribes to <paramref name="events"/> but does not own it; the caller disposes it after this.</summary>
    public TrayApp(Settings settings, bool dryRun, string exePath, PowerEventsWindow events,
        ActivityMonitor activity, Controller controller, Engine engine, ILog log)
    {
        _settings = settings;
        _dryRun = dryRun;
        _controller = controller;
        _engine = engine;
        _log = log;
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _period = TimeSpan.FromSeconds(Math.Max(1, settings.SampleSeconds));
        _timer = new System.Threading.Timer(_ => OnTimer(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        _menu.Items.Add(_status);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(Toggle("Hold awake", false, on => Apply("Hold awake", () => activity.Hold = on)));
        _menu.Items.Add(Toggle("Pause", false, on => Apply("Pause", () => engine.Paused = on)));
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
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Open log", null, (_, _) => Guarded("Open log", () => Process.Start(AppPaths.LogFile)?.Dispose()));
        _menu.Items.Add("Quit", null, (_, _) => Quit());

        if (!dryRun)
        {
            if (settings.StartAtLogin) Guarded("Start at login", () => Autostart.Set(true, exePath));
            Guarded("Watchdog task", () => WatchdogTask.Register(exePath, log));
        }

        _tray.ContextMenuStrip = _menu;
        var (status, icon) = Snapshot();
        ShowStatus(status, icon);
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
            var (status, icon) = Snapshot();
            _ui.Post(_ => ShowStatus(status, icon), null);
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

    /// <summary>Call under the lock.</summary>
    private (string Status, Icon Icon) Snapshot()
    {
        var status = (_dryRun ? "[dry run] " : "") + _engine.Status;
        var icon = _controller.Mode != Mode.Armed ? _icons.Idle
            : _engine.Lid == LidState.Closed ? _icons.LidClosed
            : _icons.Live;
        return (status, icon);
    }

    /// <summary>UI thread.</summary>
    private void ShowStatus(string status, Icon icon)
    {
        if (_closed) return;
        _status.Text = status;
        _tray.Text = status.Length > 63 ? status.Substring(0, 63) : status;
        _tray.Icon = icon;
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
    }
}
