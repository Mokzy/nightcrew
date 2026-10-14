using Nightcrew.Core;
using Xunit;

namespace Nightcrew.Tests;

public sealed class EngineSnapshotTests : IDisposable
{
    // Lid action Sleep on AC and DC; sleep after 15 min on AC, 10 min on DC.
    private static readonly PowerValues Owner = new(1, 1, 900, 600);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nightcrew-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakeClock _clock = new();
    private readonly Signal _signal = new();
    private readonly Policy _policy = new(Owner);
    private readonly KeepAwake _keepAwake = new();
    private readonly Power _power = new();
    private readonly Log _log = new();
    private readonly Controller _controller = new(new Settings());
    private readonly ActivityMonitor _activity;
    private readonly Engine _engine;

    public EngineSnapshotTests()
    {
        _activity = new ActivityMonitor([_signal], TimeSpan.FromMinutes(5));
        var journal = new RestoreJournal(_policy, Path.Combine(_dir, "restore.json"), _log);
        _engine = new Engine(_activity, _controller, journal, _policy, _keepAwake, _power, _log, _clock) { Lid = LidState.Open };
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private void Tick(double afterMinutes = 0)
    {
        _clock.Advance(TimeSpan.FromMinutes(afterMinutes));
        _engine.Tick();
    }

    [Fact]
    public void Before_the_first_tick_the_snapshot_is_the_initial_one()
    {
        Assert.Same(EngineSnapshot.Initial, _engine.Snapshot);
    }

    [Fact]
    public void Arming_shows_in_the_snapshot_with_holders_and_the_owner_lid_action()
    {
        _signal.Busy = true;
        Tick();

        var snapshot = _engine.Snapshot;
        Assert.Equal(Mode.Armed, snapshot.Mode);
        Assert.True(snapshot.Live);
        Assert.Equal([new Holder("Claude Code", "1 session")], snapshot.Holders);
        Assert.Equal(LidAction.Sleep, snapshot.OwnerLid);
        Assert.Equal(LidState.Open, snapshot.Lid);
        Assert.Equal(80, snapshot.BatteryPercent);
        Assert.Equal(PowerSource.DC, snapshot.Source);
        Assert.Equal(EngineWarning.None, snapshot.Warning);
    }

    [Fact]
    public void A_policy_block_is_a_warning()
    {
        _policy.Allowed = false;
        _signal.Busy = true;
        Tick();

        Assert.Equal(EngineWarning.PolicyBlocks, _engine.Snapshot.Warning);
        Assert.Equal(Mode.Idle, _engine.Snapshot.Mode);
    }

    [Fact]
    public void A_failed_arm_is_a_backoff_warning()
    {
        _keepAwake.Fail = true;
        _signal.Busy = true;
        Tick();

        Assert.Equal(EngineWarning.ArmBackoff, _engine.Snapshot.Warning);
    }

    [Fact]
    public void A_restore_that_failed_is_a_pending_warning()
    {
        _signal.Busy = true;
        Tick();
        _signal.Busy = false;
        _policy.FailWrites = true;

        Tick(afterMinutes: 6); // grace over: disarm, but the restore write fails and the record stays

        Assert.Equal(EngineWarning.RestorePending, _engine.Snapshot.Warning);
    }

    [Fact]
    public void Stopping_at_the_battery_floor_is_a_warning_until_the_lid_opens()
    {
        var controller = new Controller(new Settings { BatteryFloorPercent = 90 }); // the fake battery is at 80% on DC
        var journal = new RestoreJournal(_policy, Path.Combine(_dir, "floor.json"), _log);
        var engine = new Engine(_activity, controller, journal, _policy, _keepAwake, _power, _log, _clock) { Lid = LidState.Closed };
        _signal.Busy = true;

        engine.Tick(); // arms
        _clock.Advance(TimeSpan.FromSeconds(10));
        engine.Tick(); // the floor stops it

        Assert.Equal(Mode.Idle, engine.Snapshot.Mode);
        Assert.Equal(EngineWarning.Stopped, engine.Snapshot.Warning);

        engine.Lid = LidState.Open;
        _clock.Advance(TimeSpan.FromSeconds(10));
        engine.Tick();
        Assert.Equal(EngineWarning.None, engine.Snapshot.Warning);
    }

    [Fact]
    public void Holders_are_logged_when_their_labels_change_not_on_every_tick()
    {
        _signal.Busy = true;
        Tick();
        _signal.Detail = "2 sessions";
        Tick(afterMinutes: 1);
        _signal.Busy = false;
        Tick(afterMinutes: 6);

        Assert.Equal(["Live: Claude Code · 1 session", "Quiet"],
            _log.Infos.Where(line => line.StartsWith("Live") || line == "Quiet").ToList());
    }

    private sealed class Signal : IBusySignal, IHolderSource
    {
        public string Name => "transcripts";
        public bool Busy { get; set; }
        public string Detail { get; set; } = "1 session";
        public IReadOnlyList<Holder> Holders => [new Holder("Claude Code", Detail)];

        public bool Sample(TimeSpan now) => Busy;
    }

    private sealed class Policy(PowerValues values) : IPowerPolicy
    {
        public PowerValues Values { get; private set; } = values;
        public bool Allowed { get; set; } = true;
        public bool FailWrites { get; set; }

        public PowerValues Read() => Values;

        public void Write(PowerValues values)
        {
            if (FailWrites) throw new InvalidOperationException("PowerWriteACValueIndex failed (5)");
            Values = values;
        }

        public bool CanWrite(out string reason)
        {
            reason = Allowed ? "" : "policy blocks lid action AC (1260)";
            return Allowed;
        }
    }

    private sealed class KeepAwake : IKeepAwake
    {
        public bool IsHeld { get; private set; }
        public bool Fail { get; set; }

        public void Hold() => IsHeld = Fail ? throw new InvalidOperationException("PowerSetRequest failed (5)") : true;

        public void Release() => IsHeld = false;
    }

    private sealed class Power : ISystemPower
    {
        public PowerSource Source => PowerSource.DC;
        public int? BatteryPercent => 80;
        public TimeSpan IdleTime => TimeSpan.Zero;
        public bool HibernateAvailable => true;

        public bool Hibernate() => true;

        public void MonitorOff() { }

        public void LockWorkstation() { }
    }

    private sealed class Log : ILog
    {
        public List<string> Infos { get; } = [];

        public void Info(string message) => Infos.Add(message);

        public void Warn(string message) { }
    }
}
