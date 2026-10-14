using Nightcrew.Core;
using Xunit;

namespace Nightcrew.Tests;

public sealed class HolderTests
{
    private const string ClaudeRoot = @"C:\Users\w\.claude\projects";
    private const string CodexRoot = @"C:\Users\w\.codex\sessions";
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    [Fact]
    public void Transcripts_count_sessions_per_agent()
    {
        var scanner = new FakeScanner();
        var signal = new TranscriptSignal(scanner, [ClaudeRoot, CodexRoot], [], Window);
        scanner.Files[$@"{ClaudeRoot}\a\1.jsonl"] = 10;
        scanner.Files[$@"{ClaudeRoot}\b\2.jsonl"] = 10;
        scanner.Files[$@"{CodexRoot}\2026\r.jsonl"] = 10;
        signal.Sample(At(0));

        scanner.Files[$@"{ClaudeRoot}\a\1.jsonl"] = 20;
        scanner.Files[$@"{ClaudeRoot}\b\2.jsonl"] = 20;
        scanner.Files[$@"{CodexRoot}\2026\r.jsonl"] = 20;
        Assert.True(signal.Sample(At(10)));

        Assert.Equal([new Holder("Claude Code", "2 sessions"), new Holder("Codex", "1 session")], signal.Holders);
    }

    [Fact]
    public void Transcripts_keep_counting_a_session_until_the_window_passes()
    {
        var scanner = new FakeScanner();
        var signal = new TranscriptSignal(scanner, [ClaudeRoot], [], Window);
        scanner.Files[$@"{ClaudeRoot}\a\1.jsonl"] = 10;
        scanner.Files[$@"{ClaudeRoot}\b\2.jsonl"] = 10;
        signal.Sample(At(0));

        scanner.Files[$@"{ClaudeRoot}\a\1.jsonl"] = 20;
        signal.Sample(At(10));
        scanner.Files[$@"{ClaudeRoot}\b\2.jsonl"] = 20;
        signal.Sample(At(200));
        Assert.Equal([new Holder("Claude Code", "2 sessions")], signal.Holders);

        scanner.Files[$@"{ClaudeRoot}\b\2.jsonl"] = 30;
        signal.Sample(At(400)); // the first session last grew 390 s ago, past the 5-minute window
        Assert.Equal([new Holder("Claude Code", "1 session")], signal.Holders);
    }

    [Fact]
    public void Transcripts_under_a_user_added_root_are_labelled_with_its_folder_name()
    {
        var scanner = new FakeScanner();
        var signal = new TranscriptSignal(scanner, [@"C:\Users\w\.gemini\tmp"], [], Window);
        scanner.Files[@"C:\Users\w\.gemini\tmp\x\chat.jsonl"] = 1;
        signal.Sample(At(0));
        scanner.Files[@"C:\Users\w\.gemini\tmp\x\chat.jsonl"] = 2;
        signal.Sample(At(10));

        Assert.Equal([new Holder("tmp", "1 session")], signal.Holders);
    }

    [Fact]
    public void Ignored_transcripts_are_not_holders()
    {
        var scanner = new FakeScanner();
        var signal = new TranscriptSignal(scanner, [ClaudeRoot], ["claude-mem-observer"], Window);
        scanner.Files[$@"{ClaudeRoot}\claude-mem-observer-sessions\o.jsonl"] = 1;
        scanner.Files[$@"{ClaudeRoot}\a\1.jsonl"] = 1;
        signal.Sample(At(0));
        scanner.Files[$@"{ClaudeRoot}\claude-mem-observer-sessions\o.jsonl"] = 2;
        scanner.Files[$@"{ClaudeRoot}\a\1.jsonl"] = 2;
        signal.Sample(At(10));

        Assert.Equal([new Holder("Claude Code", "1 session")], signal.Holders);
    }

    [Fact]
    public void Agent_tools_report_busy_tools_and_working_self_cpu_agents()
    {
        var table = new FakeTable();
        table.Add(1, 0, "claude");
        table.Add(2, 1, "python");
        table.Add(3, 0, "codex");
        var signal = new AgentTreeSignal(table, new Settings());
        signal.Sample(At(0));

        table.Burn(2, 5);  // 50% of one core over 10 s
        table.Burn(3, 1);  // 10%
        Assert.True(signal.Sample(At(10)));

        Assert.Equal([new Holder("Codex", "working"), new Holder("python", "busy")], signal.Holders.OrderBy(h => h.Label).ToList());
    }

    [Fact]
    public void Gpu_reports_every_busy_process_busiest_first_with_names()
    {
        var gpu = new FakeGpu();
        var signal = new GpuSignal(gpu, 30, pid => pid == 7 ? "brush_app" : null);
        gpu.Percent[7] = 94.4;
        gpu.Percent[8] = 41.6;
        gpu.Percent[9] = 5;

        Assert.True(signal.Sample(At(0)));

        Assert.Equal([new Holder("brush_app", "GPU 94%"), new Holder("pid 8", "GPU 42%")], signal.Holders);
    }

    [Fact]
    public void Monitor_shows_holders_of_signals_busy_within_grace()
    {
        var transcripts = new ScriptedSignal("transcripts", new Holder("Claude Code", "1 session"));
        var gpu = new ScriptedSignal("gpu", new Holder("brush_app", "GPU 90%"));
        var monitor = new ActivityMonitor([transcripts, gpu], Window);

        transcripts.Busy = gpu.Busy = true;
        monitor.Sample(At(0));
        Assert.Equal([new Holder("Claude Code", "1 session"), new Holder("brush_app", "GPU 90%")], monitor.Holders);

        transcripts.Busy = false;
        monitor.Sample(At(200));
        Assert.Equal(2, monitor.Holders.Count); // still within grace

        gpu.Busy = true;
        monitor.Sample(At(301));
        Assert.Equal([new Holder("brush_app", "GPU 90%")], monitor.Holders);
    }

    [Fact]
    public void Monitor_lists_hold_awake_first_and_each_label_once()
    {
        var first = new ScriptedSignal("transcripts", new Holder("Codex", "1 session"));
        var second = new ScriptedSignal("agent-tools", new Holder("Codex", "working"));
        var monitor = new ActivityMonitor([first, second], Window) { Hold = true };

        first.Busy = second.Busy = true;
        monitor.Sample(At(0));

        Assert.Equal([new Holder("Hold awake", "on"), new Holder("Codex", "1 session")], monitor.Holders);
    }

    [Fact]
    public void Monitor_is_empty_when_quiet()
    {
        var monitor = new ActivityMonitor([new ScriptedSignal("gpu", new Holder("x", "y"))], Window);
        monitor.Sample(At(0));
        Assert.Empty(monitor.Holders);
    }

    private static TimeSpan At(double seconds) => TimeSpan.FromSeconds(seconds);

    private sealed class FakeScanner : IFileScanner
    {
        public Dictionary<string, long> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyDictionary<string, long> Scan(IReadOnlyList<string> roots) => new Dictionary<string, long>(Files);
    }

    private sealed class FakeTable : IProcessTable
    {
        private static readonly DateTime T0 = new(2026, 10, 14, 1, 0, 0, DateTimeKind.Utc);
        private readonly Dictionary<int, (ProcessEntry Entry, TimeSpan Cpu)> _rows = [];

        public void Add(int pid, int parentPid, string name) => _rows[pid] = (new ProcessEntry(pid, parentPid, name), TimeSpan.Zero);

        public void Burn(int pid, double seconds) => _rows[pid] = (_rows[pid].Entry, _rows[pid].Cpu + TimeSpan.FromSeconds(seconds));

        public IReadOnlyList<ProcessEntry> List() => _rows.Values.Select(row => row.Entry).ToList();

        public bool TryGetTimes(int pid, out DateTime startUtc, out TimeSpan cpu)
        {
            startUtc = T0;
            cpu = _rows.TryGetValue(pid, out var row) ? row.Cpu : default;
            return _rows.ContainsKey(pid);
        }
    }

    private sealed class FakeGpu : IGpuUsage
    {
        public Dictionary<int, double> Percent { get; } = [];

        public IReadOnlyDictionary<int, double> PercentByPid() => Percent;
    }

    private sealed class ScriptedSignal(string name, Holder holder) : IBusySignal, IHolderSource
    {
        public string Name => name;
        public bool Busy { get; set; }
        public IReadOnlyList<Holder> Holders => [holder];

        public bool Sample(TimeSpan now) => Busy;
    }
}
