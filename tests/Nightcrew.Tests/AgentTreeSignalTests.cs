using System.Diagnostics;
using Nightcrew.Core;
using Nightcrew.Platform;
using Xunit;

namespace Nightcrew.Tests;

public sealed class AgentTreeSignalTests
{
    private readonly FakeProcessTable _table = new();
    private TimeSpan _now = TimeSpan.FromHours(1);

    private AgentTreeSignal Signal(Settings? settings = null) => new(_table, settings ?? new Settings());

    private bool SampleAfter(AgentTreeSignal signal, int seconds)
    {
        _now += TimeSpan.FromSeconds(seconds);
        return signal.Sample(_now);
    }

    // claude (10) -> bash (11) -> python (12), all started at the same time.
    private void ClaudeTree()
    {
        _table.Add(10, 1, "claude");
        _table.Add(11, 10, "bash");
        _table.Add(12, 11, "python");
    }

    [Fact]
    public void First_sighting_only_records_a_baseline()
    {
        ClaudeTree();
        _table.Burn(12, 100);
        var signal = Signal();
        Assert.False(signal.Sample(_now));
        Assert.Null(signal.LastBusyProcess);
    }

    [Fact]
    public void Busy_tool_below_an_agent_is_busy()
    {
        ClaudeTree();
        var signal = Signal();
        signal.Sample(_now);
        _table.Burn(12, 2); // 2 s of CPU in 10 s = 20% of one core
        Assert.True(SampleAfter(signal, 10));
        Assert.Equal("python (pid 12)", signal.LastBusyProcess);
        Assert.Equal("agent-tools", signal.Name);
    }

    [Fact]
    public void Tool_below_its_threshold_is_not_busy()
    {
        ClaudeTree();
        var signal = Signal();
        signal.Sample(_now);
        _table.Burn(12, 2); // 2 s of CPU in 30 s = 6.7%
        Assert.False(SampleAfter(signal, 30));
    }

    [Fact]
    public void Busy_process_outside_agent_trees_is_ignored()
    {
        _table.Add(10, 1, "claude");
        _table.Add(20, 1, "python");
        var signal = Signal();
        signal.Sample(_now);
        _table.Burn(20, 10);
        Assert.False(SampleAfter(signal, 10));
    }

    [Fact]
    public void Agent_and_conhost_cpu_is_not_tool_activity()
    {
        _table.Add(10, 1, "claude");
        _table.Add(11, 10, "conhost");
        _table.Add(12, 10, "claude");
        var signal = Signal();
        signal.Sample(_now);
        _table.Burn(10, 5);
        _table.Burn(11, 5);
        _table.Burn(12, 5);
        Assert.False(SampleAfter(signal, 10));
    }

    [Fact]
    public void Self_cpu_agent_over_its_own_threshold_is_busy()
    {
        _table.Add(30, 1, "codex");
        var signal = Signal();
        signal.Sample(_now);
        _table.Burn(30, 0.3); // 3% of one core, default SelfCpuPercent is 2
        Assert.True(SampleAfter(signal, 10));
        Assert.Equal("codex (pid 30)", signal.LastBusyProcess);
    }

    [Fact]
    public void Agent_names_match_case_insensitively()
    {
        ClaudeTree();
        var signal = Signal(new Settings { AgentProcessNames = ["Claude"] });
        signal.Sample(_now);
        _table.Burn(12, 2);
        Assert.True(SampleAfter(signal, 10));
    }

    [Fact]
    public void Parent_that_started_after_the_child_is_not_followed()
    {
        _table.Add(10, 1, "claude", startMinute: 5);
        _table.Add(12, 10, "python", startMinute: 0); // its real parent exited and pid 10 was reused
        var signal = Signal();
        signal.Sample(_now);
        _table.Burn(12, 5);
        Assert.False(SampleAfter(signal, 10));
    }

    [Fact]
    public void Cycle_in_parent_links_terminates()
    {
        _table.Add(10, 12, "claude");
        _table.Add(11, 10, "bash");
        _table.Add(12, 11, "python");
        var signal = Signal();
        signal.Sample(_now);
        _table.Burn(12, 5);
        Assert.True(SampleAfter(signal, 10));
    }

    [Fact]
    public void New_process_reusing_a_pid_gets_a_fresh_baseline()
    {
        ClaudeTree();
        var signal = Signal();
        signal.Sample(_now);
        _table.Remove(12);
        _table.Add(12, 11, "python", startMinute: 1);
        _table.Burn(12, 5);
        Assert.False(SampleAfter(signal, 10));
    }

    [Fact]
    public void Vanished_or_denied_processes_are_skipped()
    {
        ClaudeTree();
        _table.Add(13, 11, "node");
        _table.Add(14, 11, "go");
        var signal = Signal();
        signal.Sample(_now);
        _table.Deny(12);    // access denied
        _table.Vanish(13);  // still listed, but it exited before its times were read
        _table.Burn(12, 5);
        _table.Burn(13, 5);
        Assert.False(SampleAfter(signal, 10));

        _table.Burn(14, 2); // the sample still completes for the others
        Assert.True(SampleAfter(signal, 10));
        Assert.Equal("go (pid 14)", signal.LastBusyProcess);
    }

    [Fact]
    public void Exited_process_is_dropped_from_the_cache()
    {
        ClaudeTree();
        var signal = Signal();
        signal.Sample(_now);
        _table.Remove(12);
        Assert.False(SampleAfter(signal, 10));
        _table.Add(12, 11, "python");
        _table.Burn(12, 5);
        Assert.False(SampleAfter(signal, 10)); // baseline again, not a delta from before it exited
        _table.Burn(12, 2);
        Assert.True(SampleAfter(signal, 10));
    }

    [Fact]
    public void Toolhelp_table_lists_the_current_process_and_reads_its_times()
    {
        using var current = Process.GetCurrentProcess();
        var table = new ToolhelpProcessTable();
        var entry = Assert.Single(table.List(), p => p.Pid == current.Id);
        Assert.Equal(current.ProcessName.ToLowerInvariant(), entry.Name);
        Assert.NotEqual(0, entry.ParentPid);
        Assert.True(table.TryGetTimes(current.Id, out var start, out var cpu));
        Assert.True(start <= DateTime.UtcNow);
        Assert.True(cpu > TimeSpan.Zero);
    }

    private sealed class FakeProcessTable : IProcessTable
    {
        private static readonly DateTime T0 = new(2026, 10, 13, 20, 0, 0, DateTimeKind.Utc);
        private readonly Dictionary<int, Row> _rows = [];

        public void Add(int pid, int parentPid, string name, int startMinute = 0) =>
            _rows[pid] = new Row { Entry = new ProcessEntry(pid, parentPid, name), Start = T0.AddMinutes(startMinute) };

        public void Burn(int pid, double seconds) => _rows[pid].Cpu += TimeSpan.FromSeconds(seconds);

        public void Remove(int pid) => _rows.Remove(pid);

        public void Deny(int pid) => _rows[pid].Denied = true;

        public void Vanish(int pid) => _rows[pid].Vanished = true;

        public IReadOnlyList<ProcessEntry> List() => _rows.Values.Select(r => r.Entry).ToList();

        public bool TryGetTimes(int pid, out DateTime startUtc, out TimeSpan cpu)
        {
            startUtc = default;
            cpu = default;
            if (!_rows.TryGetValue(pid, out var row) || row.Denied || row.Vanished) return false;
            startUtc = row.Start;
            cpu = row.Cpu;
            return true;
        }

        private sealed class Row
        {
            public ProcessEntry Entry { get; set; }
            public DateTime Start { get; set; }
            public TimeSpan Cpu { get; set; }
            public bool Denied { get; set; }
            public bool Vanished { get; set; }
        }
    }
}
