using Nightcrew.Core;
using Xunit;

namespace Nightcrew.Tests;

public sealed class EfficiencyCoordinatorTests
{
    private static readonly DateTime T0 = new(2026, 10, 15, 8, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Away = TimeSpan.FromMinutes(5);

    private readonly FakeEfficiency _efficiency = new();
    private readonly FakeSource _source = new();
    private readonly Settings _settings = new();
    private readonly List<int> _holders = [];
    private readonly ListLog _log = new();
    private readonly EfficiencyCoordinator _coordinator;

    public EfficiencyCoordinatorTests()
    {
        _source.Add(2, 1, "explorer");
        _coordinator = new EfficiencyCoordinator(_efficiency, _source, _settings, () => _holders, _log);
    }

    private void StepAway() => _coordinator.Step(Mode.Armed, LidState.Closed, Away, LidAction.Sleep);

    private void App(int pid, string name)
    {
        _source.Add(pid, 2, name);
        _source.Windows.Add(pid);
    }

    [Fact]
    public void Away_puts_each_planned_app_into_efficiency_mode()
    {
        App(10, "spotify");
        App(20, "olk");

        StepAway();

        Assert.Equal(["spotify", "olk"], _efficiency.AppliedNames);
        Assert.True(_coordinator.Active);
        Assert.Equal(2, _coordinator.AppCount);
    }

    [Fact]
    public void Would_slow_now_lists_the_planned_apps_by_name_without_changing_any()
    {
        App(10, "spotify");
        App(20, "olk");
        App(30, "discord");
        _settings.LeaveAlone = ["DISCORD"];

        var apps = _coordinator.WouldSlowNow();

        Assert.Equal([new NamedApp("Olk!", "olk"), new NamedApp("Spotify!", "spotify")], apps);
        Assert.Empty(_efficiency.Calls);
        Assert.False(_coordinator.Active);
    }

    [Fact]
    public void Not_away_puts_everything_back_with_the_reason()
    {
        App(10, "spotify");
        StepAway();

        _coordinator.Step(Mode.Armed, LidState.Open, Away, LidAction.Sleep);

        Assert.Equal(["RestoreAll:lid opened"], _efficiency.Calls.Where(call => call.StartsWith("Restore")));
        Assert.False(_coordinator.Active);
        Assert.Equal(0, _coordinator.AppCount);
    }

    [Theory]
    [InlineData(Mode.Idle, "work ended")]
    [InlineData(Mode.Finishing, "work ended")]
    [InlineData(Mode.Paused, "pause")]
    public void Each_way_of_ending_has_its_reason(Mode mode, string reason)
    {
        App(10, "spotify");
        StepAway();

        _coordinator.Step(mode, LidState.Closed, Away, LidAction.Sleep);

        Assert.Contains($"RestoreAll:{reason}", _efficiency.Calls);
    }

    [Fact]
    public void Input_ends_it_with_the_reason_input()
    {
        App(10, "spotify");
        StepAway();

        _coordinator.Step(Mode.Armed, LidState.Closed, TimeSpan.FromSeconds(1), LidAction.Sleep);

        Assert.Contains("RestoreAll:input", _efficiency.Calls);
    }

    [Fact]
    public void Switching_it_off_while_away_puts_everything_back()
    {
        App(10, "spotify");
        StepAway();

        _settings.EfficiencyWhileAway = false;
        StepAway();

        Assert.Contains("RestoreAll:switched off", _efficiency.Calls);
        Assert.Equal(0, _coordinator.AppCount);
    }

    [Fact]
    public void An_app_added_to_your_list_while_away_goes_back_on_the_next_tick()
    {
        App(10, "spotify");
        StepAway();

        _settings.LeaveAlone = ["spotify"];
        StepAway();

        Assert.Contains("Restore:spotify:left alone now", _efficiency.Calls);
        Assert.Equal(0, _coordinator.AppCount);
    }

    [Fact]
    public void Nothing_is_put_back_when_nothing_is_in_efficiency_mode()
    {
        _coordinator.Step(Mode.Idle, LidState.Open, TimeSpan.Zero, LidAction.Sleep);

        Assert.Empty(_efficiency.Calls);
    }

    [Fact]
    public void An_app_that_becomes_needed_goes_back_on_the_next_tick()
    {
        App(10, "spotify");
        StepAway();

        _holders.Add(10);
        StepAway();

        Assert.Contains("Restore:spotify:left alone now", _efficiency.Calls);
        Assert.Equal(0, _coordinator.AppCount);
    }

    [Fact]
    public void An_app_that_opens_while_away_goes_in_on_the_next_tick()
    {
        App(10, "spotify");
        StepAway();

        App(20, "olk");
        StepAway();

        Assert.Equal(["spotify", "olk"], _efficiency.AppliedNames);
        Assert.Equal(2, _coordinator.AppCount);
    }

    [Fact]
    public void A_denied_app_is_logged_once()
    {
        App(10, "steamvr");
        _efficiency.Deny.Add("steamvr");

        StepAway();
        StepAway();

        Assert.Single(_log.Infos, line => line == "Can't change steamvr: access denied");
        Assert.Equal(0, _coordinator.AppCount);
    }

    [Fact]
    public void A_pass_overtaken_by_a_restore_stops()
    {
        App(10, "spotify");
        App(20, "olk");
        _efficiency.RestoreDuringFirstApply = true;

        StepAway();

        Assert.Equal(["spotify"], _efficiency.Calls.Where(call => call.StartsWith("Apply")).Select(call => call.Substring(6)));
        Assert.Empty(_efficiency.AppliedNames);
    }

    [Fact]
    public void Apps_left_alone_are_logged_when_they_change()
    {
        App(10, "code");
        StepAway();
        StepAway();
        App(20, "steam");
        StepAway();

        Assert.Equal(["Left alone: code (category)", "Left alone: code (category), steam (game)"],
            _log.Infos.Where(line => line.StartsWith("Left alone")));
    }

    [Fact]
    public void RestoreAll_puts_everything_back_at_once()
    {
        App(10, "spotify");
        StepAway();

        Assert.Equal(1, _coordinator.RestoreAll("quit"));

        Assert.False(_coordinator.Active);
        Assert.Contains("RestoreAll:quit", _efficiency.Calls);
    }

    /// <summary>Applies and restores by name in memory; records every call.</summary>
    private sealed class FakeEfficiency : IEfficiencyMode
    {
        private readonly Dictionary<ProcessRef, string> _applied = [];

        public List<string> Calls { get; } = [];
        public HashSet<string> Deny { get; } = [];
        public bool RestoreDuringFirstApply { get; set; }
        public int Generation { get; private set; }
        public IReadOnlyCollection<ProcessRef> Applied => _applied.Keys.ToList();
        public List<string> AppliedNames => _applied.Values.ToList();

        public ApplyResult Apply(AppFamily family, int generation)
        {
            Calls.Add("Apply:" + family.RootName);
            if (RestoreDuringFirstApply)
            {
                RestoreDuringFirstApply = false;
                RestoreAll("lid opened");
            }
            if (generation != Generation) return ApplyResult.Stale;
            if (Deny.Contains(family.RootName)) return ApplyResult.Denied;
            _applied[family.Root] = family.RootName;
            return ApplyResult.Applied;
        }

        public void Restore(ProcessRef root, string reason)
        {
            Calls.Add($"Restore:{_applied[root]}:{reason}");
            _applied.Remove(root);
        }

        public int RestoreAll(string reason)
        {
            Calls.Add("RestoreAll:" + reason);
            Generation++;
            var apps = _applied.Count;
            _applied.Clear();
            return apps;
        }
    }

    private sealed class FakeSource : IEfficiencySource
    {
        private readonly List<ProcessInfo> _processes = [];

        public HashSet<int> Windows { get; } = [];

        public void Add(int pid, int parent, string name) => _processes.Add(new ProcessInfo(pid, parent, name, T0.AddMinutes(pid), 1));

        public EfficiencySnapshot Take(IReadOnlyCollection<int> holderPids) => new(1, _processes, Windows.ToList(), holderPids.ToList(), []);

        public string DisplayName(ProcessInfo root) => char.ToUpperInvariant(root.Name[0]) + root.Name.Substring(1) + "!";
    }

    private sealed class ListLog : ILog
    {
        public List<string> Infos { get; } = [];

        public void Info(string message) => Infos.Add(message);

        public void Warn(string message) { }
    }
}
