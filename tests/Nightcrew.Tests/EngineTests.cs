using Nightcrew.Core;
using Nightcrew.Platform;
using Xunit;

namespace Nightcrew.Tests;

public sealed class EngineTests : IDisposable
{
    // Lid action: Sleep on AC, Hibernate on DC. Sleep after 15 min on AC, 10 min on DC.
    private static readonly PowerValues Owner = new(1, 2, 900, 600);
    private static readonly TimeSpan Grace = TimeSpan.FromMinutes(5);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nightcrew-tests-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _calls = [];
    private readonly Settings _settings = new();
    private readonly FakeClock _clock = new();
    private readonly FakeSignal _signal = new();
    private readonly FakeLog _log = new();
    private readonly FakePolicy _policy;
    private readonly FakeKeepAwake _keepAwake;
    private readonly FakeSystemPower _power;
    private readonly FakeEfficiency _efficiency;
    private readonly FakeSource _source = new();
    private readonly RestoreJournal _journal;
    private readonly Controller _controller;
    private readonly Engine _engine;

    public EngineTests()
    {
        _policy = new FakePolicy(Owner, _calls);
        _keepAwake = new FakeKeepAwake(_calls);
        _power = new FakeSystemPower(_calls);
        _efficiency = new FakeEfficiency(_calls);
        _journal = new RestoreJournal(_policy, RecordPath, _log);
        _controller = new Controller(_settings);
        _engine = new Engine(new ActivityMonitor([_signal], Grace), _controller, _journal,
            _policy, _keepAwake, _power, _log, _clock, new EfficiencyCoordinator(_efficiency, _source, _settings, () => [], _log));
    }

    private string RecordPath => Path.Combine(_dir, "restore.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private void Tick(double afterMinutes = 0)
    {
        _clock.Advance(TimeSpan.FromMinutes(afterMinutes));
        _engine.Tick();
    }

    private void TickAfterSeconds(int seconds)
    {
        _clock.Advance(TimeSpan.FromSeconds(seconds));
        _engine.Tick();
    }

    private void ArmWithLidOpen()
    {
        _signal.Busy = true;
        _engine.Lid = LidState.Open;
        Tick();
    }

    [Fact]
    public void Arm_writes_the_restore_record_then_holds()
    {
        ArmWithLidOpen();

        Assert.True(_journal.HasRecord);
        Assert.Equal(Owner, _journal.OwnerValues);
        Assert.Equal(RestoreJournal.ArmValues, _policy.Values);
        Assert.True(_keepAwake.IsHeld);
        Assert.Equal(["Write", "Hold"], _calls);
        Assert.Contains(_log.Infos, line => line.StartsWith("Arm") && line.Contains("transcripts"));
    }

    [Fact]
    public void Disarm_releases_then_restores_the_owner_values()
    {
        ArmWithLidOpen();
        _signal.Busy = false;

        Tick(afterMinutes: 6);

        Assert.Equal(Owner, _policy.Values);
        Assert.False(_journal.HasRecord);
        Assert.False(_keepAwake.IsHeld);
        Assert.Equal(["Write", "Hold", "Release", "Write"], _calls);
    }

    [Fact]
    public void Hibernate_failure_falls_back_to_monitor_off()
    {
        ArmWithLidOpen();
        _engine.Lid = LidState.Closed;
        _power.Source = PowerSource.DC;
        _power.BatteryPercent = 10;
        _power.IdleTime = TimeSpan.FromMinutes(10);
        _power.HibernateResult = false;

        Tick(afterMinutes: 1);

        Assert.Equal(["Write", "Hold", "Lock", "Release", "Write", "Hibernate", "MonitorOff"], _calls);
        Assert.Contains(_log.Warnings, line => line.Contains("Hibernate"));
        Assert.Equal(Owner, _policy.Values);
    }

    [Fact]
    public void A_throwing_command_is_logged_and_the_next_command_still_runs()
    {
        _signal.Busy = true;
        _engine.Lid = LidState.Closed;
        _power.IdleTime = TimeSpan.FromMinutes(10);
        _policy.FailWrites = true;

        Tick();

        Assert.Contains(_log.Warnings, line => line.StartsWith("Arm failed"));
        Assert.False(_keepAwake.IsHeld);
        Assert.False(_journal.HasRecord);
        Assert.Equal(["Release", "Lock"], _calls);
    }

    [Fact]
    public void A_failed_arm_is_undone_and_retried_after_five_minutes()
    {
        _keepAwake.FailHolds = true;
        ArmWithLidOpen();

        Assert.Contains(_log.Warnings, line => line.StartsWith("Arm failed"));
        Assert.Equal(["Write", "Release", "Write"], _calls);
        Assert.Equal(Owner, _policy.Values);
        Assert.False(_journal.HasRecord);
        Assert.Equal(Mode.Idle, _controller.Mode);

        _keepAwake.FailHolds = false;
        for (var i = 0; i < 29; i++) TickAfterSeconds(10);   // up to 4 min 50 s after the failure: blocked

        Assert.Equal(Mode.Idle, _controller.Mode);
        Assert.Equal(["Write", "Release", "Write"], _calls);
        Assert.Single(_log.Infos, line => line.StartsWith("Not arming again"));

        TickAfterSeconds(10);                                // 5 minutes after the failure

        Assert.Equal(Mode.Armed, _controller.Mode);
        Assert.True(_keepAwake.IsHeld);
        Assert.Equal(["Write", "Release", "Write", "Write", "Hold"], _calls);
        Assert.Single(_log.Infos, line => line.StartsWith("Not arming again"));
    }

    [Fact]
    public void A_failed_restore_is_retried_when_not_armed_at_most_once_a_minute()
    {
        ArmWithLidOpen();
        Tick(afterMinutes: 2);                         // armed, with a record: never retried
        Assert.DoesNotContain(_log.Infos, line => line.StartsWith("Retrying"));

        _policy.FailWrites = true;
        _signal.Busy = false;
        Tick(afterMinutes: 6);                         // Disarm: the restore fails and the record is kept
        Assert.True(_journal.HasRecord);
        Assert.Equal(Mode.Idle, _controller.Mode);
        Assert.Single(_log.Warnings, line => line.StartsWith("Restore failed"));

        for (var i = 0; i < 5; i++) TickAfterSeconds(10);
        Assert.Single(_log.Warnings, line => line.StartsWith("Restore failed"));

        TickAfterSeconds(10);                          // 60 s after the failed restore
        Assert.Equal(2, _log.Warnings.Count(line => line.StartsWith("Restore failed")));
        Assert.Single(_log.Infos, line => line.StartsWith("Retrying"));

        _policy.FailWrites = false;
        TickAfterSeconds(30);
        Assert.True(_journal.HasRecord);               // 30 s after the last try: too soon
        TickAfterSeconds(30);
        Assert.False(_journal.HasRecord);
        Assert.Equal(Owner, _policy.Values);
        Assert.False(_keepAwake.IsHeld);
    }

    [Fact]
    public void A_record_left_at_startup_is_first_retried_a_minute_after_the_engine_starts()
    {
        _journal.Arm();                                // a record left behind, as if a restore had failed
        _calls.Clear();

        Tick();
        TickAfterSeconds(50);
        Assert.True(_journal.HasRecord);
        Assert.DoesNotContain(_log.Infos, line => line.StartsWith("Retrying"));

        TickAfterSeconds(10);                          // 60 s after the engine was created
        Assert.Single(_log.Infos, line => line.StartsWith("Retrying"));
        Assert.False(_journal.HasRecord);
        Assert.Equal(Owner, _policy.Values);
        Assert.Equal(["Release", "Write"], _calls);
    }

    [Fact]
    public void Finishing_carries_out_the_restored_owner_lid_action()
    {
        _signal.Busy = true;
        _engine.Lid = LidState.Closed;
        _power.IdleTime = TimeSpan.FromMinutes(10);
        Tick();
        _signal.Busy = false;

        Tick(afterMinutes: 6);
        Tick(afterMinutes: 1);

        Assert.Equal(["Write", "Hold", "Lock", "Release", "Write", "MonitorOff"], _calls);
    }

    [Fact]
    public void A_suspend_during_finishing_means_the_late_tick_after_wake_never_hibernates()
    {
        _signal.Busy = true;
        _engine.Lid = LidState.Closed;
        _power.IdleTime = TimeSpan.FromMinutes(10);
        Tick();
        _signal.Busy = false;
        Tick(afterMinutes: 6);
        Tick(afterMinutes: 1); // MonitorOff sent: the laptop goes into Modern Standby

        _engine.NoteSuspend();
        _power.IdleTime = TimeSpan.FromHours(3);
        Tick(afterMinutes: 180); // overdue tick on wake, before the lid-open event is handled

        Assert.DoesNotContain("Hibernate", _calls);
        Assert.Equal(Mode.Idle, _controller.Mode);
    }

    [Fact]
    public void Status_combines_the_controller_status_and_the_reason()
    {
        Assert.Equal("Idle", _engine.Status);

        _signal.Busy = true;
        _engine.Lid = LidState.Closed;
        _power.Source = PowerSource.DC;
        _power.IdleTime = TimeSpan.FromMinutes(10);
        Tick();
        Tick(afterMinutes: 1);

        // While armed, the owner's DC lid action comes from the record, not from the policy (now Do nothing).
        Assert.Equal("Live \u2014 lid closed 0h01, when done: hibernate (transcripts)", _engine.Status);
    }

    [Fact]
    public void Can_write_is_checked_every_five_minutes_and_a_block_is_logged_once()
    {
        _policy.CanWriteResult = false;
        _signal.Busy = true;
        _engine.Lid = LidState.Open;

        Tick();
        Tick(afterMinutes: 1);
        Tick(afterMinutes: 1);

        Assert.Equal(1, _policy.CanWriteChecks);
        Assert.Empty(_calls);
        Assert.Equal("Idle \u2014 can't change power settings (transcripts)", _engine.Status);

        Tick(afterMinutes: 3);
        Assert.Equal(2, _policy.CanWriteChecks);
        Assert.Single(_log.Warnings, line => line.Contains(FakePolicy.BlockedReason));

        _policy.CanWriteResult = true;
        Tick(afterMinutes: 5);
        Assert.Equal(3, _policy.CanWriteChecks);
        Assert.Equal(["Write", "Hold"], _calls);
    }

    [Fact]
    public void RestoreNow_releases_and_restores()
    {
        ArmWithLidOpen();

        _engine.RestoreNow();

        Assert.Equal(Owner, _policy.Values);
        Assert.False(_journal.HasRecord);
        Assert.False(_keepAwake.IsHeld);
    }

    [Fact]
    public void A_failing_read_is_logged_and_tick_does_not_throw()
    {
        _policy.FailReads = true;
        _signal.Busy = true;

        Tick();

        Assert.Contains(_log.Warnings, line => line.StartsWith("Tick failed"));
        Assert.Empty(_calls);
    }

    [Fact]
    public void Dry_run_wrappers_read_the_real_values_but_only_log_changes()
    {
        var policy = new DryRunPolicy(_policy, _log);
        var keepAwake = new DryRunKeepAwake(_log);
        var power = new DryRunSystemPower(_power, _log);
        var journal = new RestoreJournal(policy, RecordPath, _log);
        var engine = new Engine(new ActivityMonitor([_signal], Grace), new Controller(_settings), journal,
            policy, keepAwake, power, _log, _clock);
        _signal.Busy = true;
        engine.Lid = LidState.Closed;
        _power.Source = PowerSource.DC;
        _power.BatteryPercent = 50;
        _power.IdleTime = TimeSpan.FromMinutes(10);

        engine.Tick();
        Assert.True(keepAwake.IsHeld);

        _power.BatteryPercent = 10;
        _clock.Advance(TimeSpan.FromMinutes(1));
        engine.Tick();

        Assert.False(keepAwake.IsHeld);
        Assert.False(journal.HasRecord);
        Assert.Equal(Owner, _policy.Values);
        Assert.Equal(1, _policy.CanWriteChecks);
        Assert.Empty(_calls);
        Assert.Contains(_log.Infos, line => line.StartsWith("[dry run]") && line.Contains("hibernate"));
        Assert.Contains(_log.Infos, line => line.StartsWith("[dry run]") && line.Contains("lock"));
        Assert.Contains(_log.Infos, line => line.StartsWith("[dry run]") && line.Contains("write power settings"));
    }

    /// <summary>An agent works, the lid is closed and nobody has touched the laptop for 10 minutes; Spotify is open.</summary>
    private void AwayWithSpotifyOpen()
    {
        _source.Window(10, "spotify");
        _signal.Busy = true;
        _engine.Lid = LidState.Closed;
        _power.IdleTime = TimeSpan.FromMinutes(10);
        Tick();
    }

    [Fact]
    public void Away_with_an_agent_working_puts_open_apps_into_efficiency_mode()
    {
        AwayWithSpotifyOpen();

        Assert.Equal(["spotify"], _efficiency.Names);
        Assert.Equal(1, _engine.Snapshot.EfficiencyApps);
    }

    [Fact]
    public void When_the_work_ends_apps_come_out_before_the_power_settings_go_back()
    {
        AwayWithSpotifyOpen();
        _signal.Busy = false;

        Tick(afterMinutes: 6);

        var restore = _calls.IndexOf("RestoreAll:work ended");
        Assert.True(restore >= 0);
        Assert.True(restore < _calls.LastIndexOf("Release"));
        Assert.Empty(_efficiency.Names);
    }

    [Fact]
    public void At_the_battery_floor_apps_come_out_before_hibernating()
    {
        AwayWithSpotifyOpen();
        _power.Source = PowerSource.DC;
        _power.BatteryPercent = 10;

        Tick(afterMinutes: 1);

        var restore = _calls.IndexOf("RestoreAll:battery floor or lid-closed limit");
        Assert.True(restore >= 0);
        Assert.True(restore < _calls.IndexOf("Hibernate"));
    }

    [Fact]
    public void When_the_lid_opens_the_apps_come_out()
    {
        AwayWithSpotifyOpen();

        _engine.Lid = LidState.Open;
        Tick();

        Assert.Equal(0, _engine.Snapshot.EfficiencyApps);
        Assert.Contains("RestoreAll:lid opened", _calls);
    }

    /// <summary>Applies in memory and records a restore in the call list only when there was something to put back.</summary>
    private sealed class FakeEfficiency(List<string> calls) : IEfficiencyMode
    {
        private readonly Dictionary<ProcessRef, string> _applied = [];

        public List<string> Names => _applied.Values.ToList();
        public int Generation { get; private set; }
        public IReadOnlyCollection<ProcessRef> Applied => _applied.Keys.ToList();

        public ApplyResult Apply(AppFamily family, int generation)
        {
            if (generation != Generation) return ApplyResult.Stale;
            _applied[family.Root] = family.RootName;
            return ApplyResult.Applied;
        }

        public void Restore(ProcessRef root, string reason) => _applied.Remove(root);

        public int RestoreAll(string reason)
        {
            Generation++;
            var apps = _applied.Count;
            if (apps > 0) calls.Add("RestoreAll:" + reason);
            _applied.Clear();
            return apps;
        }
    }

    private sealed class FakeSource : IEfficiencySource
    {
        private static readonly DateTime T0 = new(2026, 10, 15, 8, 0, 0, DateTimeKind.Utc);
        private readonly List<ProcessInfo> _processes = [new ProcessInfo(2, 1, "explorer", T0, 1)];
        private readonly List<int> _windows = [];

        public void Window(int pid, string name)
        {
            _processes.Add(new ProcessInfo(pid, 2, name, T0.AddHours(1), 1));
            _windows.Add(pid);
        }

        public EfficiencySnapshot Take(IReadOnlyCollection<int> holderPids) => new(1, _processes, _windows, holderPids, []);

        public string DisplayName(ProcessInfo root) => root.Name;
    }

    private sealed class FakeSignal : IBusySignal
    {
        public string Name => "transcripts";
        public bool Busy { get; set; }

        public bool Sample(TimeSpan now) => Busy;
    }

    private sealed class FakePolicy(PowerValues values, List<string> calls) : IPowerPolicy
    {
        public const string BlockedReason = "policy blocks lid action AC (5)";

        public PowerValues Values { get; private set; } = values;
        public bool FailWrites { get; set; }
        public bool FailReads { get; set; }
        public bool CanWriteResult { get; set; } = true;
        public int CanWriteChecks { get; private set; }

        public PowerValues Read() => FailReads ? throw new InvalidOperationException("PowerGetActiveScheme failed (5)") : Values;

        public void Write(PowerValues values)
        {
            if (FailWrites) throw new InvalidOperationException("PowerWriteACValueIndex failed (5)");
            Values = values;
            calls.Add("Write");
        }

        public bool CanWrite(out string reason)
        {
            CanWriteChecks++;
            reason = CanWriteResult ? "" : BlockedReason;
            return CanWriteResult;
        }
    }

    private sealed class FakeKeepAwake(List<string> calls) : IKeepAwake
    {
        public bool IsHeld { get; private set; }
        public bool FailHolds { get; set; }

        public void Hold()
        {
            if (FailHolds) throw new InvalidOperationException("PowerSetRequest failed (5)");
            IsHeld = true;
            calls.Add("Hold");
        }

        public void Release()
        {
            IsHeld = false;
            calls.Add("Release");
        }
    }

    private sealed class FakeSystemPower(List<string> calls) : ISystemPower
    {
        public PowerSource Source { get; set; } = PowerSource.AC;
        public int? BatteryPercent { get; set; } = 100;
        public TimeSpan IdleTime { get; set; }
        public bool HibernateAvailable => true;
        public bool HibernateResult { get; set; } = true;

        public bool Hibernate()
        {
            calls.Add("Hibernate");
            return HibernateResult;
        }

        public void MonitorOff() => calls.Add("MonitorOff");

        public void LockWorkstation() => calls.Add("Lock");
    }

    private sealed class FakeLog : ILog
    {
        public List<string> Infos { get; } = [];
        public List<string> Warnings { get; } = [];

        public void Info(string message) => Infos.Add(message);

        public void Warn(string message) => Warnings.Add(message);
    }
}
