# Nightcrew UI Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give Nightcrew the Moonlit look from the UI spec (lantern and moon-and-star tray marks, a click panel, an app icon and a dark README) without changing what it does.

**Architecture:** Pure additions in `Core` carry the new information: signals report holders, the engine publishes an immutable `EngineSnapshot`, and `PanelModel` turns it into every string the panel shows. The UI is code-only WPF in `App/Ui`, hosted on the existing WinForms STA thread; the timer thread only posts snapshots. The app icon and the README images are rendered from the same code by `build/render-assets.ps1`, which loads the built exe as a library, so the tray app never starts.

**Tech Stack:** C# 14 on .NET Framework 4.8 (SDK-style, PolySharp), WinForms plus WPF, xUnit v3 on Microsoft Testing Platform, Windows PowerShell 5.1 and headless Chrome for the README pages.

**Spec:** `docs/superpowers/specs/2026-10-13-nightcrew-ui-design.md` (section 10, added in Task 6, records where the build differs from the first draft).

## Global Constraints

- Target `net48` with C# 14; the build finishes with 0 warnings (`TreatWarningsAsErrors`).
- One exe: `build/check-single-file.ps1` passes; no NuGet runtime packages (PolySharp only).
- Dark only. Tokens are the spec's section 3 values: Night `#1E1F21`, NightRaised `#26282B`, Moon `#FFFFEB`, MoonMuted `#C9C7B6`, MoonFaint `#8C8A7C`, Hairline Moon at 10%, Lavender `#F0D7FF`, Ink `#1A1A1A`, Warn `#F5C27A`.
- Headline EB Garamond 26 px (regular and italic); everything else Figtree regular and medium, 11–13 px; fallbacks Georgia and Segoe UI. Both fonts are SIL OFL and embedded as WPF resources.
- Wording (spec 5.5): sentence case, short plain sentences, contractions, no exclamation marks, no "please", no "successfully"; tooltip at most 63 characters.
- Tests never change the machine: no power-setting writes, power requests, sleep, hibernate, monitor off, lock, Run-key writes or scheduled tasks. Don't start the tray app during development; the render script loads the exe as a library.
- GPT Image output (optional richer icon art, optional atmosphere image) is reviewed by the owner before it is committed, and is never used for images that contain text.
- Commands are Git Bash. With the per-user SDK, run first: `export DOTNET_ROOT="$LOCALAPPDATA/Microsoft/dotnet-sdk" PATH="$LOCALAPPDATA/Microsoft/dotnet-sdk:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1`

## Review Focus

1. A holder with a very long process name: the row trims with an ellipsis and the panel stays 320 px wide. Test: `PanelVisualTests.A_long_holder_name_is_trimmed_instead_of_widening_the_panel` (Task 5).
2. Many holders at once make the panel taller than the room above the taskbar: its top stays on screen. Test: `PanelPlacementTests.A_panel_taller_than_the_work_area_keeps_its_top_on_screen` (Task 5).
3. An embedded font fails to load: Georgia or Segoe UI takes over instead of the panel failing. Test: `ThemeTests.A_font_that_will_not_load_falls_back_to_the_system_font` (Task 3).
4. A light taskbar or display scaling at 125–200%: the tray mark uses Night ink with the deep lavender and is drawn at 20, 24 or 32 px. Tests: `MarkTests.A_light_taskbar_gets_the_deep_lavender_flame`, `MarkTests.Tray_bitmaps_come_out_at_the_requested_size_with_something_drawn` (Task 4), `TrayIconsTests` (Task 5).
5. A desktop with no battery: the fact row says "No battery" instead of a blank. Test: `PanelModelTests.No_battery_is_said_plainly` (Task 2).

---

### Task 1: Holders and the engine snapshot

Signals say who is holding the laptop awake, and the engine publishes one immutable snapshot per tick for the tray and the panel. The log gains one line whenever the set of holders changes.

**Files:**
- Create: `src/Nightcrew/Core/Holder.cs`
- Modify: `src/Nightcrew/Core/TranscriptSignal.cs`, `src/Nightcrew/Core/AgentTreeSignal.cs`, `src/Nightcrew/Core/GpuSignal.cs`, `src/Nightcrew/Core/ActivityMonitor.cs`, `src/Nightcrew/Core/Engine.cs`
- Test: `tests/Nightcrew.Tests/HolderTests.cs`, `tests/Nightcrew.Tests/EngineSnapshotTests.cs`

**Interfaces:**
- Consumes: `IBusySignal`, `ActivityMonitor`, `Engine` and `Controller` from v1.
- Produces: `record struct Holder(string Label, string Detail)`; `interface IHolderSource { IReadOnlyList<Holder> Holders { get; } }`; `AgentNames.Display(string)`; `TranscriptSignal(IFileScanner, IReadOnlyList<string> roots, IReadOnlyList<string> ignorePathContains, TimeSpan? holderWindow = null)`; `GpuSignal(IGpuUsage, double thresholdPercent, Func<int, string?>? nameOf = null)`; `ActivityMonitor.Holders`; `enum EngineWarning { None, PolicyBlocks, ArmBackoff, RestorePending }`; `record EngineSnapshot(Mode Mode, LidState Lid, bool Live, IReadOnlyList<Holder> Holders, PowerSource Source, int? BatteryPercent, LidAction OwnerLid, bool Hold, bool Paused, EngineWarning Warning)` with `EngineSnapshot.Initial`; `Engine.Snapshot`.

- [ ] **Step 1: Write the failing tests**

`tests/Nightcrew.Tests/HolderTests.cs`:

```csharp
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
```

`tests/Nightcrew.Tests/EngineSnapshotTests.cs`:

```csharp
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
```

- [ ] **Step 2: Run the tests to verify they fail**

Run:

```bash
export DOTNET_ROOT="$LOCALAPPDATA/Microsoft/dotnet-sdk" PATH="$LOCALAPPDATA/Microsoft/dotnet-sdk:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
dotnet build -c Release 2>&1 | grep -E "error|Warning\(s\)|Error\(s\)" | sort -u
dotnet test -c Release --no-build 2>&1 | grep -E "^failed |total:|failed:"
```

Expected: FAIL: the build stops with `error CS0246` for `Holder`, `IHolderSource` and `EngineSnapshot`.

- [ ] **Step 3: Write the implementation**

`src/Nightcrew/Core/Holder.cs`:

```csharp
namespace Nightcrew.Core;

/// <summary>Something holding the laptop awake, as the panel shows it: "Claude Code" · "2 sessions".</summary>
public readonly record struct Holder(string Label, string Detail);

/// <summary>A signal that can say who made its most recent busy sample busy.</summary>
public interface IHolderSource
{
    /// <summary>Holders from the most recent busy sample; empty before the first one.</summary>
    IReadOnlyList<Holder> Holders { get; }
}

public static class AgentNames
{
    /// <summary>"claude" → "Claude Code", "codex" → "Codex"; any other process name is shown as it is.</summary>
    public static string Display(string processName) => processName.ToLowerInvariant() switch
    {
        "claude" => "Claude Code",
        "codex" => "Codex",
        _ => processName,
    };
}
```

`src/Nightcrew/Core/TranscriptSignal.cs`:

```csharp
namespace Nightcrew.Core;

public interface IFileScanner
{
    /// <summary>Full path -> length in bytes of every *.jsonl under the roots, recursive.</summary>
    IReadOnlyDictionary<string, long> Scan(IReadOnlyList<string> roots);
}

/// <summary>Busy when an agent transcript grew or appeared since the previous sample.</summary>
public sealed class TranscriptSignal(IFileScanner scanner, IReadOnlyList<string> roots, IReadOnlyList<string> ignorePathContains,
    TimeSpan? holderWindow = null) : IBusySignal, IHolderSource
{
    private readonly TimeSpan _window = holderWindow ?? TimeSpan.FromMinutes(5);
    private readonly Dictionary<string, TimeSpan> _grewAt = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyDictionary<string, long>? _previous;

    public string Name => "transcripts";

    /// <summary>The file that made the most recent busy sample busy.</summary>
    public string? LastChangedPath { get; private set; }

    /// <summary>One holder per agent, counting the transcripts that grew within the holder window.</summary>
    public IReadOnlyList<Holder> Holders { get; private set; } = [];

    public bool Sample(TimeSpan now)
    {
        var previous = _previous;
        var current = scanner.Scan(roots);
        _previous = current;
        if (previous is null) return false;

        var busy = false;
        foreach (var file in current)
        {
            if (IsIgnored(file.Key)) continue;
            if (previous.TryGetValue(file.Key, out var before) && file.Value <= before) continue;
            if (!busy) LastChangedPath = file.Key;
            busy = true;
            _grewAt[file.Key] = now;
        }

        foreach (var stale in _grewAt.Where(pair => now - pair.Value > _window).Select(pair => pair.Key).ToList())
            _grewAt.Remove(stale);
        if (busy) Holders = BuildHolders();
        return busy;
    }

    private IReadOnlyList<Holder> BuildHolders() =>
        _grewAt.Keys
            .GroupBy(LabelFor)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new Holder(group.Key, group.Count() == 1 ? "1 session" : $"{group.Count()} sessions"))
            .ToList();

    private string LabelFor(string path)
    {
        if (path.IndexOf(@"\.claude\", StringComparison.OrdinalIgnoreCase) >= 0) return "Claude Code";
        if (path.IndexOf(@"\.codex\", StringComparison.OrdinalIgnoreCase) >= 0) return "Codex";
        foreach (var root in roots)
        {
            var expanded = Environment.ExpandEnvironmentVariables(root).TrimEnd('\\', '/');
            if (expanded.Length > 0 && path.StartsWith(expanded, StringComparison.OrdinalIgnoreCase))
                return Path.GetFileName(expanded);
        }
        return "Transcripts";
    }

    private bool IsIgnored(string path) =>
        ignorePathContains.Any(part => part.Length > 0 && path.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0);
}
```

`src/Nightcrew/Core/AgentTreeSignal.cs`:

```csharp
namespace Nightcrew.Core;

/// <summary>One process. Name is lowercased, without ".exe".</summary>
public readonly record struct ProcessEntry(int Pid, int ParentPid, string Name);

public interface IProcessTable
{
    IReadOnlyList<ProcessEntry> List();
    /// <summary>Returns false if the process has exited or access is denied.</summary>
    bool TryGetTimes(int pid, out DateTime startUtc, out TimeSpan cpu);
}

/// <summary>
/// Busy while a tool process started by an agent, or a self-CPU agent itself, uses CPU.
/// CPU is a percent of one core; it is not divided by the processor count.
/// </summary>
public sealed class AgentTreeSignal(IProcessTable table, Settings settings) : IBusySignal, IHolderSource
{
    private Dictionary<(int Pid, DateTime Start), (TimeSpan Cpu, TimeSpan At)> _baselines = [];

    public string Name => "agent-tools";

    /// <summary>The process that made the most recent busy sample, e.g. "python (pid 1234)".</summary>
    public string? LastBusyProcess { get; private set; }

    /// <summary>Busy processes of the last busy sample: "Codex" · "working" for self-CPU agents, "python" · "busy" for tools.</summary>
    public IReadOnlyList<Holder> Holders { get; private set; } = [];

    public bool Sample(TimeSpan now)
    {
        var processes = table.List();
        var agents = new HashSet<string>(settings.AgentProcessNames, StringComparer.OrdinalIgnoreCase);
        var selfCpuAgents = new HashSet<string>(settings.SelfCpuAgentNames, StringComparer.OrdinalIgnoreCase);

        var times = new Dictionary<int, (DateTime Start, TimeSpan Cpu)?>();
        (DateTime Start, TimeSpan Cpu)? TimesOf(int pid)
        {
            if (!times.TryGetValue(pid, out var found))
                times[pid] = found = table.TryGetTimes(pid, out var start, out var cpu) ? (start, cpu) : null;
            return found;
        }

        var watched = new Dictionary<int, (ProcessEntry Process, double Threshold)>();
        foreach (var process in processes)
            if (selfCpuAgents.Contains(process.Name)) watched[process.Pid] = (process, settings.SelfCpuPercent);

        var children = processes.ToLookup(p => p.ParentPid);
        var queue = new Queue<ProcessEntry>(processes.Where(p => agents.Contains(p.Name)));
        var visited = new HashSet<int>(queue.Select(p => p.Pid));
        while (queue.Count > 0)
        {
            var parent = queue.Dequeue();
            if (TimesOf(parent.Pid) is not { } parentTimes) continue;
            foreach (var child in children[parent.Pid])
            {
                // A parent that started after its child is a reused pid, not the real parent.
                if (visited.Contains(child.Pid) || TimesOf(child.Pid) is not { } childTimes || parentTimes.Start > childTimes.Start) continue;
                visited.Add(child.Pid);
                queue.Enqueue(child);
                var isTool = !agents.Contains(child.Name) && !string.Equals(child.Name, "conhost", StringComparison.OrdinalIgnoreCase);
                if (isTool && !watched.ContainsKey(child.Pid)) watched[child.Pid] = (child, settings.ToolCpuPercent);
            }
        }

        var busy = new List<ProcessEntry>();
        var next = new Dictionary<(int Pid, DateTime Start), (TimeSpan Cpu, TimeSpan At)>();
        foreach (var (process, threshold) in watched.Values)
        {
            if (TimesOf(process.Pid) is not { } current) continue;
            var key = (process.Pid, current.Start);
            next[key] = (current.Cpu, now);
            if (!_baselines.TryGetValue(key, out var last) || now <= last.At) continue;
            var percent = (current.Cpu - last.Cpu).TotalMilliseconds / (now - last.At).TotalMilliseconds * 100;
            if (percent < threshold) continue;
            if (busy.Count == 0) LastBusyProcess = $"{process.Name} (pid {process.Pid})";
            busy.Add(process);
        }

        _baselines = next; // processes not seen this sample (exited or no longer watched) are dropped
        if (busy.Count == 0) return false;
        Holders = busy
            .Select(process => selfCpuAgents.Contains(process.Name)
                ? new Holder(AgentNames.Display(process.Name), "working")
                : new Holder(process.Name, "busy"))
            .GroupBy(holder => holder.Label, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        return true;
    }
}
```

`src/Nightcrew/Core/GpuSignal.cs`:

```csharp
namespace Nightcrew.Core;

public interface IGpuUsage
{
    /// <summary>Process id to the utilization percent of its busiest GPU engine. Empty when unavailable.</summary>
    IReadOnlyDictionary<int, double> PercentByPid();
}

/// <summary><paramref name="nameOf"/> turns a pid into a process name for the panel; null names show as "pid N".</summary>
public sealed class GpuSignal(IGpuUsage gpu, double thresholdPercent, Func<int, string?>? nameOf = null) : IBusySignal, IHolderSource
{
    public string Name => "gpu";

    /// <summary>The busiest pid of the last busy sample; kept after the GPU goes quiet.</summary>
    public int? LastBusyPid { get; private set; }

    /// <summary>Every process at or above the threshold in the last busy sample, busiest first.</summary>
    public IReadOnlyList<Holder> Holders { get; private set; } = [];

    public bool Sample(TimeSpan now)
    {
        var busy = gpu.PercentByPid()
            .Where(pair => pair.Value >= thresholdPercent)
            .OrderByDescending(pair => pair.Value)
            .ToList();
        if (busy.Count == 0) return false;
        LastBusyPid = busy[0].Key;
        Holders = busy.Select(pair => new Holder(nameOf?.Invoke(pair.Key) ?? $"pid {pair.Key}", $"GPU {Math.Round(pair.Value):0}%")).ToList();
        return true;
    }
}
```

`src/Nightcrew/Core/ActivityMonitor.cs`:

```csharp
namespace Nightcrew.Core;

/// <summary>Live while held, or while any signal was busy within the grace window.</summary>
public sealed class ActivityMonitor(IReadOnlyList<IBusySignal> signals, TimeSpan grace)
{
    private readonly Dictionary<IBusySignal, TimeSpan> _lastBusy = [];
    private readonly Dictionary<IBusySignal, IReadOnlyList<Holder>> _lastHolders = [];
    private IReadOnlyList<Holder> _signalHolders = [];
    private string? _signalReason;

    public bool Hold { get; set; }

    public bool Live => Reason is not null;

    /// <summary>"hold", or the name of the first signal busy within grace; null when not live.</summary>
    public string? Reason => Hold ? "hold" : _signalReason;

    /// <summary>
    /// Who is holding the laptop awake: "Hold awake" first when held, then the holders of every signal busy within
    /// grace, in signal order. A label appears once; the first signal to report it wins.
    /// </summary>
    public IReadOnlyList<Holder> Holders =>
        Hold ? [new Holder("Hold awake", "on"), .. _signalHolders.Where(holder => holder.Label != "Hold awake")] : _signalHolders;

    /// <summary>Samples every signal, even when held, so their baselines stay current.</summary>
    public void Sample(TimeSpan now)
    {
        _signalReason = null;
        var holders = new List<Holder>();
        foreach (var signal in signals)
        {
            if (signal.Sample(now))
            {
                _lastBusy[signal] = now;
                if (signal is IHolderSource source) _lastHolders[signal] = source.Holders;
            }
            if (!_lastBusy.TryGetValue(signal, out var at) || now - at > grace) continue;
            _signalReason ??= signal.Name;
            if (!_lastHolders.TryGetValue(signal, out var reported)) continue;
            foreach (var holder in reported)
                if (!holders.Any(existing => existing.Label == holder.Label)) holders.Add(holder);
        }
        _signalHolders = holders;
    }
}
```

`src/Nightcrew/Core/Engine.cs`:

```csharp
namespace Nightcrew.Core;

/// <summary>What needs the owner's attention, most important first.</summary>
public enum EngineWarning { None, PolicyBlocks, ArmBackoff, RestorePending }

/// <summary>Everything the tray and the panel show, taken at the end of a tick.</summary>
public sealed record EngineSnapshot(Mode Mode, LidState Lid, bool Live, IReadOnlyList<Holder> Holders, PowerSource Source,
    int? BatteryPercent, LidAction OwnerLid, bool Hold, bool Paused, EngineWarning Warning)
{
    public static EngineSnapshot Initial { get; } =
        new(Mode.Idle, LidState.Unknown, false, [], PowerSource.AC, null, LidAction.Sleep, false, false, EngineWarning.None);
}

/// <summary>One tick: sample activity, ask the controller, carry out its commands.</summary>
public sealed class Engine(ActivityMonitor activity, Controller controller, RestoreJournal journal,
    IPowerPolicy policy, IKeepAwake keepAwake, ISystemPower power, ILog log, IClock clock)
{
    private static readonly TimeSpan CanWriteCheckEvery = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RestoreRetryEvery = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ArmRetryAfter = TimeSpan.FromMinutes(5);

    private TimeSpan? _canWriteCheckedAt;
    private bool _canWrite;
    private string? _loggedBlock;
    private TimeSpan _restoreTriedAt = clock.Now; // so the first retry comes at least a minute after startup
    private TimeSpan? _armBlockedUntil;
    private volatile bool _suspended;
    private string? _loggedHolders;

    public LidState Lid { get; set; }

    public bool Paused { get; set; }

    public string Status => activity.Reason is { } reason ? $"{controller.Status} ({reason})" : controller.Status;

    /// <summary>Taken at the end of every tick; <see cref="EngineSnapshot.Initial"/> before the first one.</summary>
    public EngineSnapshot Snapshot { get; private set; } = EngineSnapshot.Initial;

    /// <summary>Never throws: failures are logged, and a failing command does not stop the ones after it.</summary>
    public void Tick()
    {
        try
        {
            var now = clock.Now;
            if (_suspended)
            {
                _suspended = false;
                controller.OnResume();
            }
            activity.Sample(now);
            LogHoldersWhenTheyChange(activity.Holders);
            var source = power.Source;
            var battery = power.BatteryPercent;
            var ownerLid = (journal.OwnerValues ?? policy.Read()).LidFor(source);
            var policyAllows = CanWrite(now);
            var backingOff = _armBlockedUntil is { } until && now < until;
            var inputs = new Inputs(activity.Live, Lid, source, battery, power.IdleTime, Paused, policyAllows && !backingOff, ownerLid);
            foreach (var command in controller.Step(inputs, now)) Run(command, now);
            RetryRestore(now);

            var warning = !policyAllows ? EngineWarning.PolicyBlocks
                : _armBlockedUntil is { } blockedUntil && now < blockedUntil ? EngineWarning.ArmBackoff
                : controller.Mode != Mode.Armed && journal.HasRecord ? EngineWarning.RestorePending
                : EngineWarning.None;
            Snapshot = new EngineSnapshot(controller.Mode, Lid, activity.Live, activity.Holders, source, battery,
                ownerLid, activity.Hold, Paused, warning);
        }
        catch (Exception ex)
        {
            log.Warn($"Tick failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Called on PBT_APMSUSPEND without taking any lock. The next tick ends a Finishing run first, so a tick that
    /// fires late after wake (lid opening, lid event not yet handled) can't hibernate the laptop in front of the user.
    /// </summary>
    public void NoteSuspend() => _suspended = true;

    /// <summary>Releases the keep-awake request and restores the owner's settings (quit, session end, startup).</summary>
    public void RestoreNow() => ReleaseAndRestore(clock.Now);

    /// <summary>One log line whenever the set of holders changes, so the log says why the laptop stays awake.</summary>
    private void LogHoldersWhenTheyChange(IReadOnlyList<Holder> holders)
    {
        var labels = string.Join("|", holders.Select(holder => holder.Label));
        if (labels == _loggedHolders) return;
        _loggedHolders = labels;
        log.Info(holders.Count == 0 ? "Quiet" : "Live: " + string.Join(", ", holders.Select(holder => $"{holder.Label} · {holder.Detail}")));
    }

    private void ReleaseAndRestore(TimeSpan now)
    {
        _restoreTriedAt = now;
        keepAwake.Release();
        journal.Restore();
    }

    /// <summary>A restore that failed left its record behind: try again, at most once a minute, whenever we are not armed.</summary>
    private void RetryRestore(TimeSpan now)
    {
        if (controller.Mode == Mode.Armed || !journal.HasRecord) return;
        if (now - _restoreTriedAt < RestoreRetryEvery) return;
        log.Info("Retrying the restore of the power settings");
        ReleaseAndRestore(now);
    }

    private void Run(Command command, TimeSpan now)
    {
        log.Info($"{command} (activity: {activity.Reason ?? "none"})");
        try
        {
            switch (command)
            {
                case Command.Arm:
                    journal.Arm();
                    keepAwake.Hold();
                    break;
                case Command.Disarm:
                    ReleaseAndRestore(now);
                    break;
                case Command.Lock:
                    power.LockWorkstation();
                    break;
                case Command.MonitorOff:
                    power.MonitorOff();
                    break;
                case Command.Hibernate:
                    if (!power.Hibernate())
                    {
                        log.Warn("Hibernate failed; turning the screen off instead");
                        power.MonitorOff();
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            log.Warn($"{command} failed: {ex.Message}");
            if (command != Command.Arm) return;
            // Undo a half-done arm and go back to Idle. Arming waits 5 minutes, so a persistent failure doesn't churn every tick.
            ReleaseAndRestore(now);
            controller.Reset();
            _armBlockedUntil = now + ArmRetryAfter;
            log.Info("Not arming again for 5 minutes");
        }
    }

    /// <summary>Checks at most every five minutes; logs a block once per reason.</summary>
    private bool CanWrite(TimeSpan now)
    {
        if (_canWriteCheckedAt is { } at && now - at < CanWriteCheckEvery) return _canWrite;
        _canWriteCheckedAt = now;
        _canWrite = policy.CanWrite(out var reason);
        if (_canWrite)
        {
            _loggedBlock = null;
        }
        else if (reason != _loggedBlock)
        {
            log.Warn($"Can't change power settings: {reason}");
            _loggedBlock = reason;
        }
        return _canWrite;
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run:

```bash
export DOTNET_ROOT="$LOCALAPPDATA/Microsoft/dotnet-sdk" PATH="$LOCALAPPDATA/Microsoft/dotnet-sdk:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
dotnet build -c Release 2>&1 | grep -E "error|Warning\(s\)|Error\(s\)" | sort -u
dotnet test -c Release --no-build 2>&1 | grep -E "^failed |total:|failed:"
```

Expected: `0 Warning(s)`, `0 Error(s)`, `total: 179`, `failed: 0`.

- [ ] **Step 5: Commit**

```bash
git add src/Nightcrew/Core tests/Nightcrew.Tests/HolderTests.cs tests/Nightcrew.Tests/EngineSnapshotTests.cs
git commit -m "feat: signals report who keeps the laptop awake; the engine publishes a snapshot"
```

### Task 2: PanelModel

Every string the panel and tooltip show comes from one pure function, so all of section 5 of the spec is unit-tested.

**Files:**
- Create: `src/Nightcrew/Core/PanelModel.cs`
- Test: `tests/Nightcrew.Tests/PanelModelTests.cs`

**Interfaces:**
- Consumes: `EngineSnapshot`, `EngineWarning`, `Holder` (Task 1); `Mode`, `LidState`, `LidAction`, `PowerSource` (v1).
- Produces: `enum PanelState { Quiet, Working, Paused }`; `enum MarkState { Idle, Live, LidClosed }`; `record PanelFact(string Label, string Value)`; `record PanelView(PanelState State, string Headline, string? HeadlineItalic, string SubLine, IReadOnlyList<Holder> Holders, IReadOnlyList<PanelFact> Facts, bool HoldOn, string PauseLabel, string? Warning, MarkState Mark, string Tooltip)`; `PanelModel.From(EngineSnapshot, int batteryFloorPercent, bool watchdogMissing = false)`; `PanelModel.TooltipLimit = 63`.

- [ ] **Step 1: Write the failing test**

`tests/Nightcrew.Tests/PanelModelTests.cs`:

```csharp
using Nightcrew.Core;
using Xunit;

namespace Nightcrew.Tests;

public sealed class PanelModelTests
{
    private static EngineSnapshot Snap(Mode mode = Mode.Idle, bool live = false, LidState lid = LidState.Open,
        PowerSource source = PowerSource.AC, int? battery = 81, LidAction owner = LidAction.Sleep, bool hold = false,
        bool paused = false, EngineWarning warning = EngineWarning.None, params Holder[] holders) =>
        new(mode, lid, live, holders, source, battery, owner, hold, paused, warning);

    [Fact]
    public void Quiet_says_all_quiet_and_what_the_lid_does()
    {
        var view = PanelModel.From(Snap(owner: LidAction.DoNothing), 20);

        Assert.Equal(PanelState.Quiet, view.State);
        Assert.Equal("All quiet.", view.Headline);
        Assert.Null(view.HeadlineItalic);
        Assert.Equal("No agent is working. Close the lid and your laptop does what Windows says.", view.SubLine);
        Assert.Empty(view.Holders);
        Assert.Equal([new PanelFact("Battery", "81%"), new PanelFact("When the lid closes", "Do nothing")], view.Facts);
        Assert.Equal("Pause", view.PauseLabel);
        Assert.Equal(MarkState.Idle, view.Mark);
        Assert.Equal("Nightcrew · all quiet", view.Tooltip);
        Assert.Null(view.Warning);
    }

    [Fact]
    public void Working_shows_holders_battery_floor_and_what_happens_when_done()
    {
        var holders = new[] { new Holder("Claude Code", "2 sessions"), new Holder("brush_app", "GPU 94%") };
        var view = PanelModel.From(Snap(Mode.Armed, live: true, source: PowerSource.DC, battery: 64, holders: holders), 20);

        Assert.Equal(PanelState.Working, view.State);
        Assert.Equal("Working ", view.Headline);
        Assert.Equal("through the night.", view.HeadlineItalic);
        Assert.Equal("Close the lid. Your laptop keeps working, then does what your lid normally does.", view.SubLine);
        Assert.Equal(holders, view.Holders);
        Assert.Equal([new PanelFact("Battery", "64% · stops at 20%"), new PanelFact("When done", "Sleeps")], view.Facts);
        Assert.Equal(MarkState.Live, view.Mark);
        Assert.Equal("Nightcrew · working (Claude Code)", view.Tooltip);
    }

    [Fact]
    public void Working_with_the_lid_closed_uses_the_lid_closed_mark()
    {
        var view = PanelModel.From(Snap(Mode.Armed, live: true, lid: LidState.Closed), 20);
        Assert.Equal(MarkState.LidClosed, view.Mark);
    }

    [Fact]
    public void Live_but_not_armed_still_reads_as_working()
    {
        var view = PanelModel.From(Snap(Mode.Idle, live: true, warning: EngineWarning.PolicyBlocks), 20);

        Assert.Equal(PanelState.Working, view.State);
        Assert.Equal("Windows policy blocks lid changes here. Keep the lid open.", view.Warning);
    }

    [Fact]
    public void Paused_offers_resume_and_shows_only_the_battery()
    {
        var view = PanelModel.From(Snap(Mode.Paused, live: true, paused: true, hold: true), 20);

        Assert.Equal(PanelState.Paused, view.State);
        Assert.Equal("Paused.", view.Headline);
        Assert.Equal("Windows handles the lid until you resume.", view.SubLine);
        Assert.Equal("Resume", view.PauseLabel);
        Assert.True(view.HoldOn);
        Assert.Equal([new PanelFact("Battery", "81%")], view.Facts);
        Assert.Equal(MarkState.Idle, view.Mark);
        Assert.Equal("Nightcrew · paused", view.Tooltip);
    }

    [Theory]
    [InlineData(LidAction.Sleep, "Sleeps", "Sleep")]
    [InlineData(LidAction.Hibernate, "Hibernates", "Hibernate")]
    [InlineData(LidAction.ShutDown, "Hibernates", "Shut down")]
    [InlineData(LidAction.DoNothing, "Stays on", "Do nothing")]
    public void Lid_actions_read_plainly(LidAction action, string whenDone, string lidCloses)
    {
        Assert.Equal(whenDone, PanelModel.From(Snap(Mode.Armed, live: true, owner: action), 20).Facts[1].Value);
        Assert.Equal(lidCloses, PanelModel.From(Snap(owner: action), 20).Facts[1].Value);
    }

    [Theory]
    [InlineData(PowerSource.AC, 81, 20, "81%")]
    [InlineData(PowerSource.DC, 64, 20, "64% · stops at 20%")]
    [InlineData(PowerSource.DC, 64, 0, "64%")]
    public void Battery_shows_the_floor_only_on_battery(PowerSource source, int percent, int floor, string expected)
    {
        Assert.Equal(expected, PanelModel.From(Snap(source: source, battery: percent), floor).Facts[0].Value);
    }

    [Fact]
    public void No_battery_is_said_plainly()
    {
        Assert.Equal("No battery", PanelModel.From(Snap(battery: null), 20).Facts[0].Value);
    }

    [Theory]
    [InlineData(EngineWarning.PolicyBlocks, false, "Windows policy blocks lid changes here. Keep the lid open.")]
    [InlineData(EngineWarning.ArmBackoff, true, "Couldn't change power settings. Trying again in a few minutes.")]
    [InlineData(EngineWarning.RestorePending, true, "Your power settings aren't back yet. Retrying.")]
    [InlineData(EngineWarning.None, true, "The safety net isn't installed. Settings may stay changed if Nightcrew crashes.")]
    public void One_warning_shows_most_important_first(EngineWarning warning, bool watchdogMissing, string expected)
    {
        Assert.Equal(expected, PanelModel.From(Snap(warning: warning), 20, watchdogMissing).Warning);
    }

    [Fact]
    public void The_tooltip_never_exceeds_the_tray_limit()
    {
        var view = PanelModel.From(Snap(Mode.Armed, live: true, holders: new Holder(new string('x', 80), "busy")), 20);
        Assert.Equal(PanelModel.TooltipLimit, view.Tooltip.Length);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run:

```bash
export DOTNET_ROOT="$LOCALAPPDATA/Microsoft/dotnet-sdk" PATH="$LOCALAPPDATA/Microsoft/dotnet-sdk:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
dotnet build -c Release 2>&1 | grep -E "error|Warning\(s\)|Error\(s\)" | sort -u
dotnet test -c Release --no-build 2>&1 | grep -E "^failed |total:|failed:"
```

Expected: FAIL: `error CS0246` for `PanelModel`, `PanelState`, `PanelFact` and `MarkState`.

- [ ] **Step 3: Write the implementation**

`src/Nightcrew/Core/PanelModel.cs`:

```csharp
namespace Nightcrew.Core;

public enum PanelState { Quiet, Working, Paused }

/// <summary>Which version of the mark to draw: unlit, lit, or lit with the lid closed.</summary>
public enum MarkState { Idle, Live, LidClosed }

public sealed record PanelFact(string Label, string Value);

/// <summary>Everything the click panel and the tray tooltip show, as plain strings.</summary>
public sealed record PanelView(
    PanelState State,
    string Headline,
    string? HeadlineItalic,
    string SubLine,
    IReadOnlyList<Holder> Holders,
    IReadOnlyList<PanelFact> Facts,
    bool HoldOn,
    string PauseLabel,
    string? Warning,
    MarkState Mark,
    string Tooltip);

/// <summary>Turns an engine snapshot into the panel's words. Pure, so every string is unit-tested.</summary>
public static class PanelModel
{
    public const int TooltipLimit = 63;

    public static PanelView From(EngineSnapshot snapshot, int batteryFloorPercent, bool watchdogMissing = false)
    {
        var warning = WarningText(snapshot.Warning, watchdogMissing);
        var battery = new PanelFact("Battery", Battery(snapshot, batteryFloorPercent));

        if (snapshot.Paused)
        {
            return new PanelView(PanelState.Paused, "Paused.", null, "Windows handles the lid until you resume.",
                [], [battery], snapshot.Hold, "Resume", warning, MarkState.Idle, Tooltip("paused"));
        }

        if (snapshot.Mode == Mode.Armed || snapshot.Live)
        {
            var mark = snapshot.Lid == LidState.Closed ? MarkState.LidClosed : MarkState.Live;
            var first = snapshot.Holders.Count > 0 ? $" ({snapshot.Holders[0].Label})" : "";
            return new PanelView(PanelState.Working, "Working ", "through the night.",
                "Close the lid. Your laptop keeps working, then does what your lid normally does.",
                snapshot.Holders, [battery, new PanelFact("When done", WhenDone(snapshot.OwnerLid))],
                snapshot.Hold, "Pause", warning, mark, Tooltip("working" + first));
        }

        return new PanelView(PanelState.Quiet, "All quiet.", null,
            "No agent is working. Close the lid and your laptop does what Windows says.",
            [], [battery, new PanelFact("When the lid closes", LidCloses(snapshot.OwnerLid))],
            snapshot.Hold, "Pause", warning, MarkState.Idle, Tooltip("all quiet"));
    }

    private static string Battery(EngineSnapshot snapshot, int floor) =>
        snapshot.BatteryPercent is not { } percent ? "No battery"
        : snapshot.Source == PowerSource.DC && floor > 0 ? $"{percent}% · stops at {floor}%"
        : $"{percent}%";

    private static string WhenDone(LidAction action) => action switch
    {
        LidAction.Sleep => "Sleeps",
        LidAction.DoNothing => "Stays on",
        _ => "Hibernates", // Hibernate, and Shut down, which Nightcrew turns into hibernate
    };

    private static string LidCloses(LidAction action) => action switch
    {
        LidAction.Sleep => "Sleep",
        LidAction.Hibernate => "Hibernate",
        LidAction.ShutDown => "Shut down",
        _ => "Do nothing",
    };

    private static string? WarningText(EngineWarning warning, bool watchdogMissing) => warning switch
    {
        EngineWarning.PolicyBlocks => "Windows policy blocks lid changes here. Keep the lid open.",
        EngineWarning.ArmBackoff => "Couldn't change power settings. Trying again in a few minutes.",
        EngineWarning.RestorePending => "Your power settings aren't back yet. Retrying.",
        _ => watchdogMissing ? "The safety net isn't installed. Settings may stay changed if Nightcrew crashes." : null,
    };

    private static string Tooltip(string state)
    {
        var text = "Nightcrew · " + state;
        return text.Length <= TooltipLimit ? text : text.Substring(0, TooltipLimit);
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run:

```bash
export DOTNET_ROOT="$LOCALAPPDATA/Microsoft/dotnet-sdk" PATH="$LOCALAPPDATA/Microsoft/dotnet-sdk:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
dotnet build -c Release 2>&1 | grep -E "error|Warning\(s\)|Error\(s\)" | sort -u
dotnet test -c Release --no-build 2>&1 | grep -E "^failed |total:|failed:"
```

Expected: `total: 197`, `failed: 0`.

- [ ] **Step 5: Commit**

```bash
git add src/Nightcrew/Core/PanelModel.cs tests/Nightcrew.Tests/PanelModelTests.cs
git commit -m "feat: PanelModel turns an engine snapshot into the panel's words"
```

### Task 3: Theme, fonts and WPF

Turns on WPF next to WinForms, embeds the two OFL fonts, and adds the Moonlit tokens. WinForms and WPF share type names, so both projects drop the WinForms implicit usings and each file names what it uses; `UseWPF` also drops `System.IO`, which comes back.

**Files:**
- Create: `src/Nightcrew/App/Ui/Theme.cs`, `THIRD-PARTY-NOTICES.md`, `src/Nightcrew/Fonts/EBGaramond-Regular.ttf`, `src/Nightcrew/Fonts/EBGaramond-Italic.ttf`, `src/Nightcrew/Fonts/Figtree-Regular.ttf`, `src/Nightcrew/Fonts/Figtree-Medium.ttf`
- Modify: `src/Nightcrew/Nightcrew.csproj`, `tests/Nightcrew.Tests/Nightcrew.Tests.csproj`
- Test: `tests/Nightcrew.Tests/Sta.cs`, `tests/Nightcrew.Tests/ThemeTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces: `Theme.Night`, `NightRaised`, `NightHover`, `Moon`, `MoonMuted`, `MoonFaint`, `Hairline`, `Lavender`, `LavenderDeep`, `Ink`, `Warn` (all `System.Windows.Media.Color`); `Theme.Serif`, `Theme.Sans` (`FontFamily`); `Theme.Brush(Color)` (frozen `SolidColorBrush`); `internal Theme.Load(string name, string fallback)`; test helper `Sta.Run(Func<T>)` and `Sta.Run(Action)`.

- [ ] **Step 1: Write the failing tests**

`tests/Nightcrew.Tests/Nightcrew.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <IsPackable>false</IsPackable>
    <UseWindowsForms>true</UseWindowsForms>
    <UseWPF>true</UseWPF>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="xunit.v3" Version="4.0.2" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\Nightcrew\Nightcrew.csproj" />
  </ItemGroup>
  <ItemGroup>
    <!-- Same as the app: each file says whether it means WinForms or WPF. -->
    <Using Remove="System.Drawing" />
    <Using Remove="System.Windows.Forms" />
    <Using Include="System.IO" />
  </ItemGroup>
</Project>
```

`tests/Nightcrew.Tests/Sta.cs`:

```csharp
using System.Runtime.ExceptionServices;

namespace Nightcrew.Tests;

/// <summary>Runs WPF code on its own STA thread, the way the tray app's UI thread is set up.</summary>
internal static class Sta
{
    public static T Run<T>(Func<T> work)
    {
        T result = default!;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = work();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        return result;
    }

    public static void Run(Action work) => Run(() =>
    {
        work();
        return 0;
    });
}
```

`tests/Nightcrew.Tests/ThemeTests.cs`:

```csharp
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Nightcrew.App.Ui;
using Xunit;

namespace Nightcrew.Tests;

public sealed class ThemeTests
{
    [Theory]
    [InlineData("serif", "EB Garamond")]
    [InlineData("sans", "Figtree")]
    public void The_embedded_fonts_load_from_the_exe(string which, string expected)
    {
        var name = Sta.Run(() =>
        {
            var family = which == "serif" ? Theme.Serif : Theme.Sans;
            var typeface = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            Assert.True(typeface.TryGetGlyphTypeface(out var glyphs));
            return glyphs.FamilyNames[CultureInfo.GetCultureInfo("en-US")];
        });
        Assert.Equal(expected, name);
    }

    [Fact]
    public void The_serif_has_a_real_italic_and_the_sans_a_real_medium()
    {
        Sta.Run(() =>
        {
            Assert.True(new Typeface(Theme.Serif, FontStyles.Italic, FontWeights.Normal, FontStretches.Normal)
                .TryGetGlyphTypeface(out var italic));
            Assert.Equal(FontStyles.Italic, italic.Style);

            Assert.True(new Typeface(Theme.Sans, FontStyles.Normal, FontWeights.Medium, FontStretches.Normal)
                .TryGetGlyphTypeface(out var medium));
            Assert.Equal(FontWeights.Medium, medium.Weight);
        });
    }

    [Fact]
    public void A_font_that_will_not_load_falls_back_to_the_system_font()
    {
        Assert.Equal("Georgia", Sta.Run(() => Theme.Load("No Such Font", "Georgia").Source));
    }

    [Fact]
    public void Brushes_are_frozen_so_any_thread_can_draw_them()
    {
        Assert.True(Theme.Brush(Theme.Lavender).IsFrozen);
    }

    [Fact]
    public void The_hairline_is_moon_at_ten_percent()
    {
        Assert.Equal((Theme.Moon.R, Theme.Moon.G, Theme.Moon.B), (Theme.Hairline.R, Theme.Hairline.G, Theme.Hairline.B));
        Assert.InRange(Theme.Hairline.A / 255.0, 0.095, 0.105);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run:

```bash
export DOTNET_ROOT="$LOCALAPPDATA/Microsoft/dotnet-sdk" PATH="$LOCALAPPDATA/Microsoft/dotnet-sdk:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
dotnet build -c Release 2>&1 | grep -E "error|Warning\(s\)|Error\(s\)" | sort -u
dotnet test -c Release --no-build 2>&1 | grep -E "^failed |total:|failed:"
```

Expected: FAIL: `error CS0234`/`CS0246`: `Nightcrew.App.Ui` and `Theme` don't exist.

- [ ] **Step 3: Add the fonts**

Download the static TTFs from the upstream repositories and check their hashes:

```bash
mkdir -p src/Nightcrew/Fonts
for f in EBGaramond-Regular EBGaramond-Italic; do
  gh api "repos/octaviopardo/EBGaramond12/contents/fonts/ttf/$f.ttf" -H "Accept: application/vnd.github.raw" > "src/Nightcrew/Fonts/$f.ttf"
done
for f in Figtree-Regular Figtree-Medium; do
  gh api "repos/erikdkennedy/figtree/contents/fonts/ttf/$f.ttf" -H "Accept: application/vnd.github.raw" > "src/Nightcrew/Fonts/$f.ttf"
done
sha256sum src/Nightcrew/Fonts/*.ttf
```

Expected:

```text
d4ad1d0a9390d26d6d3f176117a1d121441edb8c8632bfca52cfad084b9059cb  src/Nightcrew/Fonts/EBGaramond-Italic.ttf
2028dc06d3c130b4761693481436a32a8e35ed500bf58c25c53de004106125b8  src/Nightcrew/Fonts/EBGaramond-Regular.ttf
358a4de79ae6c3fd7c6a54fcd778c1101ba376af75b8463e715df6988387bc19  src/Nightcrew/Fonts/Figtree-Medium.ttf
448d74e778cc9774db27bd86cf451bba982b2e4dc348f610390eb6e3439cf6ca  src/Nightcrew/Fonts/Figtree-Regular.ttf
```

- [ ] **Step 4: Write the implementation**

`src/Nightcrew/Nightcrew.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <UseWindowsForms>true</UseWindowsForms>
    <UseWPF>true</UseWPF>
    <AssemblyName>nightcrew</AssemblyName>
    <RootNamespace>Nightcrew</RootNamespace>
    <ApplicationManifest>app.manifest</ApplicationManifest>
    <GenerateSupportedRuntime>false</GenerateSupportedRuntime>
    <AutoGenerateBindingRedirects>false</AutoGenerateBindingRedirects>
  </PropertyGroup>
  <ItemGroup>
    <InternalsVisibleTo Include="Nightcrew.Tests" />
    <!-- WinForms and WPF share type names (Color, FontFamily, Application), so each file says which it uses.
         UseWPF drops System.IO from the implicit usings; nothing here uses System.Windows.Shapes, so it comes back. -->
    <Using Remove="System.Drawing" />
    <Using Remove="System.Windows.Forms" />
    <Using Include="System.IO" />
  </ItemGroup>
  <ItemGroup>
    <!-- Both fonts are under the SIL Open Font License; see THIRD-PARTY-NOTICES.md. -->
    <Resource Include="Fonts\*.ttf" />
  </ItemGroup>
</Project>
```

`src/Nightcrew/App/Ui/Theme.cs`:

```csharp
using System.Windows;
using System.Windows.Media;

namespace Nightcrew.App.Ui;

/// <summary>The Moonlit tokens from the UI spec, and the two embedded fonts with their fallbacks.</summary>
public static class Theme
{
    public static Color Night { get; } = Rgb(0x1E, 0x1F, 0x21);
    public static Color NightRaised { get; } = Rgb(0x26, 0x28, 0x2B);
    public static Color NightHover { get; } = Rgb(0x2F, 0x31, 0x35);
    public static Color Moon { get; } = Rgb(0xFF, 0xFF, 0xEB);
    public static Color MoonMuted { get; } = Rgb(0xC9, 0xC7, 0xB6);
    public static Color MoonFaint { get; } = Rgb(0x8C, 0x8A, 0x7C);
    public static Color Hairline { get; } = Color.FromArgb(0x1A, 0xFF, 0xFF, 0xEB); // Moon at 10%
    public static Color Lavender { get; } = Rgb(0xF0, 0xD7, 0xFF);
    public static Color LavenderDeep { get; } = Rgb(0x8E, 0x5C, 0xC4); // lavender for tray marks on a light taskbar
    public static Color Ink { get; } = Rgb(0x1A, 0x1A, 0x1A);
    public static Color Warn { get; } = Rgb(0xF5, 0xC2, 0x7A);

    private static readonly Lazy<FontFamily> SerifFont = new(() => Load("EB Garamond", "Georgia"));
    private static readonly Lazy<FontFamily> SansFont = new(() => Load("Figtree", "Segoe UI"));

    /// <summary>EB Garamond, for the one headline. Georgia if the embedded font can't be loaded.</summary>
    public static FontFamily Serif => SerifFont.Value;

    /// <summary>Figtree, for everything else. Segoe UI if the embedded font can't be loaded.</summary>
    public static FontFamily Sans => SansFont.Value;

    /// <summary>A frozen brush, so snapshots of the panel can be drawn on any thread.</summary>
    public static SolidColorBrush Brush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);

    internal static FontFamily Load(string name, string fallback)
    {
        try
        {
            _ = System.Windows.Application.Current; // registers pack:// even though the app runs a WinForms loop
            var family = new FontFamily(new Uri("pack://application:,,,/nightcrew;component/Fonts/"), "./#" + name);
            var typeface = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            if (typeface.TryGetGlyphTypeface(out _)) return family;
        }
        catch (Exception)
        {
            // A missing or broken font must never stop the panel: fall through to the system font.
        }
        return new FontFamily(fallback);
    }
}
```

`THIRD-PARTY-NOTICES.md`:

````markdown
# Third-party notices

`nightcrew.exe` embeds two fonts. Both are under the SIL Open Font License 1.1,
which allows embedding them in software. Nightcrew uses them unmodified.

| Font | Files | Copyright |
|---|---|---|
| EB Garamond | `EBGaramond-Regular.ttf`, `EBGaramond-Italic.ttf` | Copyright 2017 The EB Garamond Project Authors (https://github.com/octaviopardo/EBGaramond12). Copyright 2025 Deborah Khodanovich (RCS citation glyphs) |
| Figtree | `Figtree-Regular.ttf`, `Figtree-Medium.ttf` | Copyright 2022 The Figtree Project Authors (https://github.com/erikdkennedy/figtree) |

The licence text below applies to both fonts. It is also available with a FAQ
at https://openfontlicense.org.

```text
-----------------------------------------------------------
SIL OPEN FONT LICENSE Version 1.1 - 26 February 2007
-----------------------------------------------------------

PREAMBLE
The goals of the Open Font License (OFL) are to stimulate worldwide
development of collaborative font projects, to support the font creation
efforts of academic and linguistic communities, and to provide a free and
open framework in which fonts may be shared and improved in partnership
with others.

The OFL allows the licensed fonts to be used, studied, modified and
redistributed freely as long as they are not sold by themselves. The
fonts, including any derivative works, can be bundled, embedded, 
redistributed and/or sold with any software provided that any reserved
names are not used by derivative works. The fonts and derivatives,
however, cannot be released under any other type of license. The
requirement for fonts to remain under this license does not apply
to any document created using the fonts or their derivatives.

DEFINITIONS
"Font Software" refers to the set of files released by the Copyright
Holder(s) under this license and clearly marked as such. This may
include source files, build scripts and documentation.

"Reserved Font Name" refers to any names specified as such after the
copyright statement(s).

"Original Version" refers to the collection of Font Software components as
distributed by the Copyright Holder(s).

"Modified Version" refers to any derivative made by adding to, deleting,
or substituting -- in part or in whole -- any of the components of the
Original Version, by changing formats or by porting the Font Software to a
new environment.

"Author" refers to any designer, engineer, programmer, technical
writer or other person who contributed to the Font Software.

PERMISSION & CONDITIONS
Permission is hereby granted, free of charge, to any person obtaining
a copy of the Font Software, to use, study, copy, merge, embed, modify,
redistribute, and sell modified and unmodified copies of the Font
Software, subject to the following conditions:

1) Neither the Font Software nor any of its individual components,
in Original or Modified Versions, may be sold by itself.

2) Original or Modified Versions of the Font Software may be bundled,
redistributed and/or sold with any software, provided that each copy
contains the above copyright notice and this license. These can be
included either as stand-alone text files, human-readable headers or
in the appropriate machine-readable metadata fields within text or
binary files as long as those fields can be easily viewed by the user.

3) No Modified Version of the Font Software may use the Reserved Font
Name(s) unless explicit written permission is granted by the corresponding
Copyright Holder. This restriction only applies to the primary font name as
presented to the users.

4) The name(s) of the Copyright Holder(s) or the Author(s) of the Font
Software shall not be used to promote, endorse or advertise any
Modified Version, except to acknowledge the contribution(s) of the
Copyright Holder(s) and the Author(s) or with their explicit written
permission.

5) The Font Software, modified or unmodified, in part or in whole,
must be distributed entirely under this license, and must not be
distributed under any other license. The requirement for fonts to
remain under this license does not apply to any document created
using the Font Software.

TERMINATION
This license becomes null and void if any of the above conditions are
not met.

DISCLAIMER
THE FONT SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO ANY WARRANTIES OF
MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT
OF COPYRIGHT, PATENT, TRADEMARK, OR OTHER RIGHT. IN NO EVENT SHALL THE
COPYRIGHT HOLDER BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY,
INCLUDING ANY GENERAL, SPECIAL, INDIRECT, INCIDENTAL, OR CONSEQUENTIAL
DAMAGES, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
FROM, OUT OF THE USE OR INABILITY TO USE THE FONT SOFTWARE OR FROM
OTHER DEALINGS IN THE FONT SOFTWARE.
```
````

- [ ] **Step 5: Run the tests to verify they pass**

Run:

```bash
export DOTNET_ROOT="$LOCALAPPDATA/Microsoft/dotnet-sdk" PATH="$LOCALAPPDATA/Microsoft/dotnet-sdk:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
dotnet build -c Release 2>&1 | grep -E "error|Warning\(s\)|Error\(s\)" | sort -u
dotnet test -c Release --no-build 2>&1 | grep -E "^failed |total:|failed:"
```

Expected: `0 Warning(s)`, `total: 203`, `failed: 0`. The theme tests prove the fonts load from `pack://application:,,,/nightcrew;component/Fonts/` without a WPF `Application` object.

Then check the exe is still alone:

```bash
powershell -NoProfile -File build/check-single-file.ps1
```

Expected: `Single-file gate passed`.

- [ ] **Step 6: Commit**

```bash
git add src/Nightcrew/Nightcrew.csproj src/Nightcrew/Fonts src/Nightcrew/App/Ui/Theme.cs THIRD-PARTY-NOTICES.md tests/Nightcrew.Tests/Nightcrew.Tests.csproj tests/Nightcrew.Tests/Sta.cs tests/Nightcrew.Tests/ThemeTests.cs
git commit -m "feat: Moonlit theme with EB Garamond and Figtree embedded; WPF alongside WinForms"
```

### Task 4: The two marks and the .ico writer

Both marks as path data on a 24-unit grid in one place, rendered for any size, plus a small .ico writer (32-bit bitmap frames, PNG for 256 px) and the `Mark` setting.

**Files:**
- Create: `src/Nightcrew/Core/MarkKind.cs`, `src/Nightcrew/App/Ui/MarkGeometry.cs`, `src/Nightcrew/App/Ui/IcoWriter.cs`
- Modify: `src/Nightcrew/Core/Settings.cs`
- Test: `tests/Nightcrew.Tests/MarkTests.cs`, `tests/Nightcrew.Tests/IcoWriterTests.cs`

**Interfaces:**
- Consumes: `Theme` (Task 3), `MarkState` (Task 2).
- Produces: `enum MarkKind { Lantern, MoonAndStar }`; `MarkKinds.Parse(string?)`, `MarkKinds.Name(MarkKind)`, `MarkKinds.LanternName = "lantern"`, `MarkKinds.MoonName = "moon"`; `Settings.Mark` (string, default `"lantern"`); `MarkGeometry.Grid`, `AppIconSizes`, `StrokeFor(double)`, `Build(MarkKind, MarkState, double strokeWidth, Color ink) : Drawing`, `Image(MarkKind, MarkState, double strokeWidth, Color ink) : ImageSource`, `Render(MarkKind, MarkState, int pixels, Color ink) : BitmapSource`, `RenderAppIcon(int) : BitmapSource`, `ToBgra(BitmapSource) : byte[]`, `ToPng(BitmapSource) : byte[]`, `AppIconFrames()`; `IcoWriter.Frame.Bitmap(int, byte[])`, `IcoWriter.Frame.Png(int, byte[])`, `IcoWriter.Write(IReadOnlyList<Frame>) : byte[]`.

- [ ] **Step 1: Write the failing tests**

`tests/Nightcrew.Tests/MarkTests.cs`:

```csharp
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Nightcrew.App.Ui;
using Nightcrew.Core;
using Xunit;

namespace Nightcrew.Tests;

public sealed class MarkTests
{
    [Theory]
    [InlineData("lantern", MarkKind.Lantern)]
    [InlineData("moon", MarkKind.MoonAndStar)]
    [InlineData(" Moon ", MarkKind.MoonAndStar)]
    [InlineData("lanturn", MarkKind.Lantern)]
    [InlineData("", MarkKind.Lantern)]
    [InlineData(null, MarkKind.Lantern)]
    public void Mark_names_parse_and_anything_unknown_is_the_lantern(string? name, MarkKind expected)
    {
        Assert.Equal(expected, MarkKinds.Parse(name));
    }

    [Fact]
    public void Mark_names_round_trip()
    {
        Assert.Equal(MarkKind.MoonAndStar, MarkKinds.Parse(MarkKinds.Name(MarkKind.MoonAndStar)));
        Assert.Equal(MarkKind.Lantern, MarkKinds.Parse(MarkKinds.Name(MarkKind.Lantern)));
    }

    [Fact]
    public void Settings_default_to_the_lantern()
    {
        Assert.Equal("lantern", new Settings().Mark);
    }

    [Theory]
    [InlineData(16, 2.0)]
    [InlineData(20, 2.0)]
    [InlineData(24, 1.5)]
    [InlineData(48, 1.5)]
    public void Strokes_are_heavier_below_24_px(double pixels, double expected)
    {
        Assert.Equal(expected, MarkGeometry.StrokeFor(pixels));
    }

    [Theory]
    [InlineData(MarkKind.Lantern)]
    [InlineData(MarkKind.MoonAndStar)]
    public void Each_mark_draws_its_three_states_differently_and_inside_the_grid(MarkKind kind)
    {
        Sta.Run(() =>
        {
            var states = new[] { MarkState.Idle, MarkState.Live, MarkState.LidClosed };
            var pixels = states.Select(state => MarkGeometry.ToBgra(MarkGeometry.Render(kind, state, 32, Theme.Moon))).ToList();
            Assert.NotEqual(pixels[0], pixels[1]);
            Assert.NotEqual(pixels[1], pixels[2]);
            Assert.NotEqual(pixels[0], pixels[2]);

            foreach (var state in states)
            {
                var bounds = MarkGeometry.Build(kind, state, 2, Theme.Moon).Bounds;
                Assert.True(bounds.Left >= 0 && bounds.Top >= 0 && bounds.Right <= 24 && bounds.Bottom <= 24, $"{kind} {state}: {bounds}");
            }
        });
    }

    [Theory]
    [InlineData(16)]
    [InlineData(20)]
    [InlineData(24)]
    [InlineData(32)]
    public void Tray_bitmaps_come_out_at_the_requested_size_with_something_drawn(int size)
    {
        Sta.Run(() =>
        {
            var bitmap = MarkGeometry.Render(MarkKind.Lantern, MarkState.Idle, size, Theme.Moon);
            Assert.Equal((size, size), (bitmap.PixelWidth, bitmap.PixelHeight));
            var bgra = MarkGeometry.ToBgra(bitmap);
            Assert.Contains(bgra.Where((_, i) => i % 4 == 3), alpha => alpha > 200);
        });
    }

    [Fact]
    public void A_light_taskbar_gets_the_deep_lavender_flame()
    {
        Sta.Run(() =>
        {
            Assert.Equal(Theme.Lavender, CentreOfFlame(Theme.Moon));
            Assert.Equal(Theme.LavenderDeep, CentreOfFlame(Theme.Night));
        });

        static Color CentreOfFlame(Color ink)
        {
            var bgra = MarkGeometry.ToBgra(MarkGeometry.Render(MarkKind.Lantern, MarkState.Live, 96, ink));
            var i = (58 * 96 + 48) * 4; // grid (12, 14.6) at 4 px per unit
            return Color.FromRgb(bgra[i + 2], bgra[i + 1], bgra[i]);
        }
    }

    [Fact]
    public void The_panel_header_mark_is_a_frozen_24_unit_image()
    {
        Sta.Run(() =>
        {
            var image = MarkGeometry.Image(MarkKind.MoonAndStar, MarkState.Live, 1.5, Theme.Moon);
            Assert.True(image.IsFrozen);
            Assert.Equal((24.0, 24.0), (image.Width, image.Height));
        });
    }

    [Fact]
    public void The_app_icon_has_every_size_and_a_night_tile()
    {
        Sta.Run(() =>
        {
            var frames = MarkGeometry.AppIconFrames();
            Assert.Equal([16, 20, 24, 32, 40, 48, 64, 128, 256], frames.Select(frame => frame.Size));
            Assert.True(frames[frames.Count - 1].IsPng);
            Assert.All(frames.Take(frames.Count - 1), frame => Assert.False(frame.IsPng));

            var corner = MarkGeometry.ToBgra(MarkGeometry.RenderAppIcon(64));
            var edge = (2 * 64 + 32) * 4; // top edge, middle: inside the tile
            Assert.Equal(Theme.Night, Color.FromRgb(corner[edge + 2], corner[edge + 1], corner[edge]));
            Assert.Equal(0, corner[3]); // the very corner is outside the rounded tile
        });
    }
}
```

`tests/Nightcrew.Tests/IcoWriterTests.cs`:

```csharp
using System.Drawing;
using Nightcrew.App.Ui;
using Xunit;

namespace Nightcrew.Tests;

public sealed class IcoWriterTests
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static byte[] Solid(int size, byte b, byte g, byte r, byte a) =>
        Enumerable.Range(0, size * size).SelectMany(_ => new[] { b, g, r, a }).ToArray();

    [Fact]
    public void An_ico_with_two_bitmap_frames_opens_in_System_Drawing_at_each_size()
    {
        var bytes = IcoWriter.Write([IcoWriter.Frame.Bitmap(16, Solid(16, 0, 0, 255, 255)), IcoWriter.Frame.Bitmap(32, Solid(32, 255, 0, 0, 255))]);

        Assert.Equal(2, BitConverter.ToUInt16(bytes, 4));
        using var small = new Icon(new MemoryStream(bytes), 16, 16);
        using var large = new Icon(new MemoryStream(bytes), 32, 32);
        Assert.Equal(16, small.Width);
        Assert.Equal(32, large.Width);

        using var bitmap = large.ToBitmap();
        Assert.Equal(Color.FromArgb(255, 0, 0, 255).ToArgb(), bitmap.GetPixel(5, 5).ToArgb());
    }

    [Fact]
    public void The_top_row_stays_on_top()
    {
        var pixels = Solid(16, 0, 0, 0, 0);
        for (var x = 0; x < 16; x++) pixels[x * 4 + 1] = pixels[x * 4 + 3] = 255; // top row green

        using var icon = new Icon(new MemoryStream(IcoWriter.Write([IcoWriter.Frame.Bitmap(16, pixels)])), 16, 16);
        using var bitmap = icon.ToBitmap();
        Assert.Equal(Color.FromArgb(255, 0, 255, 0).ToArgb(), bitmap.GetPixel(3, 0).ToArgb());
        Assert.Equal(0, bitmap.GetPixel(3, 15).A);
    }

    [Fact]
    public void A_256_frame_is_stored_as_png_with_a_zero_size_byte()
    {
        var png = PngSignature.Concat(new byte[20]).ToArray();
        var bytes = IcoWriter.Write([IcoWriter.Frame.Png(256, png)]);

        Assert.Equal(0, bytes[6]); // width 0 means 256
        Assert.Equal(0, bytes[7]);
        var offset = BitConverter.ToInt32(bytes, 6 + 12);
        Assert.Equal(PngSignature, bytes.Skip(offset).Take(8));
    }

    [Fact]
    public void Bad_frames_are_refused()
    {
        Assert.Throws<ArgumentException>(() => IcoWriter.Frame.Bitmap(16, new byte[10]));
        Assert.Throws<ArgumentOutOfRangeException>(() => IcoWriter.Frame.Png(300, PngSignature));
        Assert.Throws<ArgumentException>(() => IcoWriter.Write([]));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run:

```bash
export DOTNET_ROOT="$LOCALAPPDATA/Microsoft/dotnet-sdk" PATH="$LOCALAPPDATA/Microsoft/dotnet-sdk:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
dotnet build -c Release 2>&1 | grep -E "error|Warning\(s\)|Error\(s\)" | sort -u
dotnet test -c Release --no-build 2>&1 | grep -E "^failed |total:|failed:"
```

Expected: FAIL: `error CS0246` for `MarkKind`, `MarkGeometry` and `IcoWriter`.

- [ ] **Step 3: Write the implementation**

`src/Nightcrew/Core/MarkKind.cs`:

```csharp
namespace Nightcrew.Core;

/// <summary>The two tray marks. The owner picks one with the <c>Mark</c> setting.</summary>
public enum MarkKind { Lantern, MoonAndStar }

public static class MarkKinds
{
    public const string LanternName = "lantern";
    public const string MoonName = "moon";

    /// <summary>"moon" picks the moon and star; anything else, including a typo, is the lantern.</summary>
    public static MarkKind Parse(string? value) =>
        string.Equals(value?.Trim(), MoonName, StringComparison.OrdinalIgnoreCase) ? MarkKind.MoonAndStar : MarkKind.Lantern;

    public static string Name(MarkKind kind) => kind == MarkKind.MoonAndStar ? MoonName : LanternName;
}
```

`src/Nightcrew/Core/Settings.cs`:

```csharp
using System.Runtime.Serialization;

namespace Nightcrew.Core;

[DataContract]
public sealed class Settings
{
    public Settings() => ApplyDefaults();

    [DataMember] public int GraceMinutes { get; set; }
    [DataMember] public int BatteryFloorPercent { get; set; }
    [DataMember] public int LidClosedCapHours { get; set; }
    [DataMember] public int AwayMinutes { get; set; }
    [DataMember] public bool LockOnLidClose { get; set; }
    [DataMember] public bool StartAtLogin { get; set; }
    [DataMember] public bool DryRun { get; set; }
    [DataMember] public string[] TranscriptRoots { get; set; } = [];
    [DataMember] public string[] IgnorePathContains { get; set; } = [];
    [DataMember] public string[] AgentProcessNames { get; set; } = [];
    [DataMember] public string[] SelfCpuAgentNames { get; set; } = [];
    [DataMember] public double SelfCpuPercent { get; set; }
    [DataMember] public double ToolCpuPercent { get; set; }
    [DataMember] public double GpuPercent { get; set; }
    [DataMember] public int SampleSeconds { get; set; }
    [DataMember] public string Mark { get; set; } = MarkKinds.LanternName;

    [OnDeserializing]
    private void OnDeserializing(StreamingContext context) => ApplyDefaults();

    private void ApplyDefaults()
    {
        GraceMinutes = 5;
        BatteryFloorPercent = 20;
        LidClosedCapHours = 8;
        AwayMinutes = 5;
        LockOnLidClose = true;
        StartAtLogin = true;
        DryRun = false;
        TranscriptRoots = [@"%USERPROFILE%\.claude\projects", @"%USERPROFILE%\.codex\sessions"];
        IgnorePathContains = ["claude-mem-observer"];
        AgentProcessNames = ["claude", "codex"];
        SelfCpuAgentNames = ["codex"];
        SelfCpuPercent = 2;
        ToolCpuPercent = 10;
        GpuPercent = 30;
        SampleSeconds = 10;
        Mark = MarkKinds.LanternName;
    }
}
```

`src/Nightcrew/App/Ui/MarkGeometry.cs`:

```csharp
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Nightcrew.Core;

namespace Nightcrew.App.Ui;

/// <summary>
/// Both marks as path data on a 24-unit grid, like Insomnia's eye. Everything that draws a mark (tray icons,
/// the panel header, the app icon, the README assets) builds it here.
/// </summary>
public static class MarkGeometry
{
    public const double Grid = 24;

    // Lantern: a ring handle, a roof, the glass, a base. The flame sits inside the glass.
    internal const string LanternHandle = "M10,5 A2,2 0 0 1 14,5";
    internal const string LanternRoof = "M10,5 H14 L17.5,8 H6.5 Z";
    internal const string LanternGlass = "M7,8 H17 V19 H7 Z";
    internal const string LanternBase = "M7,19 H17 L16,21 H8 Z";
    internal const string LanternFlame = "M12,10 C14.1,12.2 14.7,13.6 14.7,14.6 A2.7,2.7 0 0 1 9.3,14.6 C9.3,13.6 9.9,12.2 12,10 Z";

    // Moon and star: a crescent opening to the upper right, and a four-point star in its hollow.
    internal const string Moon = "M19.5,15 A8.5,8.5 0 1 1 9,4.5 A7.5,7.5 0 0 0 19.5,15 Z";
    internal const string Star = "M18.5,1.8 Q18.5,5.5 22.2,5.5 Q18.5,5.5 18.5,9.2 Q18.5,5.5 14.8,5.5 Q18.5,5.5 18.5,1.8 Z";

    /// <summary>Sizes in the app's .ico. The 256 px frame is stored as PNG, the rest as bitmaps.</summary>
    public static IReadOnlyList<int> AppIconSizes { get; } = [16, 20, 24, 32, 40, 48, 64, 128, 256];

    /// <summary>Stroke width in grid units: 2 below 24 px so the tray mark stays legible, 1.5 from 24 px up.</summary>
    public static double StrokeFor(double pixels) => pixels < 24 ? 2.0 : 1.5;

    /// <summary>
    /// The mark for one state, in grid units, frozen. <paramref name="ink"/> is the stroke colour; on a light
    /// taskbar it is Night, and the lavender deepens so the flame and the star still show.
    /// </summary>
    public static Drawing Build(MarkKind kind, MarkState state, double strokeWidth, Color ink)
    {
        var group = new DrawingGroup();
        var pen = new Pen(Theme.Brush(ink), strokeWidth) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        var inkFill = Theme.Brush(ink);
        var lavender = Theme.Brush(ink == Theme.Night ? Theme.LavenderDeep : Theme.Lavender);

        void Add(string path, Brush? fill, Pen? stroke) => group.Children.Add(new GeometryDrawing(fill, stroke, Parse(path)));

        if (kind == MarkKind.Lantern)
        {
            Add(LanternHandle, null, pen);
            Add(LanternRoof, inkFill, pen);
            Add(LanternGlass, state == MarkState.LidClosed ? lavender : null, pen);
            Add(LanternBase, inkFill, pen);
            if (state == MarkState.Live) Add(LanternFlame, lavender, null);
        }
        else
        {
            Add(Moon, state == MarkState.LidClosed ? lavender : null, pen);
            if (state != MarkState.Idle)
            {
                var starFill = state == MarkState.LidClosed ? inkFill : lavender;
                var starPen = new Pen(starFill, 1) { LineJoin = PenLineJoin.Round };
                starPen.Freeze();
                Add(Star, starFill, starPen);
            }
        }
        group.Freeze();
        return group;
    }

    /// <summary>A vector image of the mark for the panel header, 24 units square.</summary>
    public static ImageSource Image(MarkKind kind, MarkState state, double strokeWidth, Color ink)
    {
        var image = new DrawingImage(Framed(Build(kind, state, strokeWidth, ink)));
        image.Freeze();
        return image;
    }

    /// <summary>The mark rendered at <paramref name="pixels"/> square, transparent around it.</summary>
    public static BitmapSource Render(MarkKind kind, MarkState state, int pixels, Color ink) =>
        RenderDrawing(Build(kind, state, StrokeFor(pixels), ink), pixels, tile: false);

    /// <summary>The app icon: the lit lantern on a Night tile with 22% corners.</summary>
    public static BitmapSource RenderAppIcon(int pixels) =>
        RenderDrawing(Build(MarkKind.Lantern, MarkState.Live, pixels < 32 ? 2.0 : 1.5, Theme.Moon), pixels, tile: true);

    /// <summary>Straight-alpha BGRA rows, top row first, as an .ico frame wants them.</summary>
    public static byte[] ToBgra(BitmapSource source)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var stride = converted.PixelWidth * 4;
        var pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);
        return pixels;
    }

    public static byte[] ToPng(BitmapSource source)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>Every frame of the app's .ico.</summary>
    public static IReadOnlyList<IcoWriter.Frame> AppIconFrames() =>
        AppIconSizes.Select(size =>
        {
            var bitmap = RenderAppIcon(size);
            return size >= 256 ? IcoWriter.Frame.Png(size, ToPng(bitmap)) : IcoWriter.Frame.Bitmap(size, ToBgra(bitmap));
        }).ToList();

    private static Geometry Parse(string path)
    {
        var geometry = Geometry.Parse(path);
        geometry.Freeze();
        return geometry;
    }

    /// <summary>Pins the drawing's bounds to the full 24-unit grid, so every mark and state lines up the same way.</summary>
    private static Drawing Framed(Drawing mark)
    {
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, Grid, Grid))));
        group.Children.Add(mark);
        group.ClipGeometry = new RectangleGeometry(new Rect(0, 0, Grid, Grid));
        group.Freeze();
        return group;
    }

    private static BitmapSource RenderDrawing(Drawing mark, int pixels, bool tile)
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            if (tile)
            {
                var corner = pixels * 0.22;
                context.DrawRoundedRectangle(Theme.Brush(Theme.Night), null, new Rect(0, 0, pixels, pixels), corner, corner);
                // The mark fills about 70% of the tile, centred; small tiles give it nearly all the room.
                var inset = pixels * (pixels < 32 ? 0.04 : 0.15);
                context.PushTransform(new TranslateTransform(inset, inset));
                context.PushTransform(new ScaleTransform((pixels - 2 * inset) / Grid, (pixels - 2 * inset) / Grid));
            }
            else
            {
                context.PushTransform(new ScaleTransform(pixels / Grid, pixels / Grid));
            }
            context.DrawDrawing(mark);
        }
        var bitmap = new RenderTargetBitmap(pixels, pixels, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }
}
```

`src/Nightcrew/App/Ui/IcoWriter.cs`:

```csharp
namespace Nightcrew.App.Ui;

/// <summary>Writes .ico files: 32-bit bitmap frames for small sizes, a PNG frame for 256 px.</summary>
public static class IcoWriter
{
    public sealed class Frame
    {
        private Frame(int size, byte[] data, bool isPng)
        {
            if (size is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(size), size, "An .ico frame is 1 to 256 px.");
            if (!isPng && data.Length != size * size * 4) throw new ArgumentException("Expected size x size BGRA pixels.", nameof(data));
            Size = size;
            Data = data;
            IsPng = isPng;
        }

        public int Size { get; }
        public byte[] Data { get; }
        public bool IsPng { get; }

        /// <summary>Straight-alpha BGRA pixels, top row first.</summary>
        public static Frame Bitmap(int size, byte[] bgra) => new(size, bgra, isPng: false);

        public static Frame Png(int size, byte[] png) => new(size, png, isPng: true);
    }

    public static byte[] Write(IReadOnlyList<Frame> frames)
    {
        if (frames.Count == 0) throw new ArgumentException("An .ico needs at least one frame.", nameof(frames));
        var images = frames.Select(frame => frame.IsPng ? frame.Data : Dib(frame.Size, frame.Data)).ToList();

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((ushort)0); // reserved
        writer.Write((ushort)1); // type: icon
        writer.Write((ushort)frames.Count);
        var offset = 6 + 16 * frames.Count;
        for (var i = 0; i < frames.Count; i++)
        {
            var size = frames[i].Size;
            writer.Write((byte)(size >= 256 ? 0 : size)); // 0 means 256
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)0); // no palette
            writer.Write((byte)0); // reserved
            writer.Write((ushort)1); // planes
            writer.Write((ushort)32); // bits per pixel
            writer.Write(images[i].Length);
            writer.Write(offset);
            offset += images[i].Length;
        }
        foreach (var image in images) writer.Write(image);
        writer.Flush();
        return stream.ToArray();
    }

    /// <summary>A BITMAPINFOHEADER, the pixels bottom row first, then an all-clear AND mask (alpha does the work).</summary>
    private static byte[] Dib(int size, byte[] bgra)
    {
        var maskStride = (size + 31) / 32 * 4;
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(40); // header size
        writer.Write(size); // width
        writer.Write(size * 2); // height of pixels plus mask
        writer.Write((ushort)1); // planes
        writer.Write((ushort)32); // bits per pixel
        writer.Write(0); // BI_RGB
        writer.Write(size * size * 4 + maskStride * size);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        for (var row = size - 1; row >= 0; row--) writer.Write(bgra, row * size * 4, size * 4);
        writer.Write(new byte[maskStride * size]);
        writer.Flush();
        return stream.ToArray();
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run:

```bash
export DOTNET_ROOT="$LOCALAPPDATA/Microsoft/dotnet-sdk" PATH="$LOCALAPPDATA/Microsoft/dotnet-sdk:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
dotnet build -c Release 2>&1 | grep -E "error|Warning\(s\)|Error\(s\)" | sort -u
dotnet test -c Release --no-build 2>&1 | grep -E "^failed |total:|failed:"
```

Expected: `total: 228`, `failed: 0`.

- [ ] **Step 5: Commit**

```bash
git add src/Nightcrew/Core/MarkKind.cs src/Nightcrew/Core/Settings.cs src/Nightcrew/App/Ui/MarkGeometry.cs src/Nightcrew/App/Ui/IcoWriter.cs tests/Nightcrew.Tests/MarkTests.cs tests/Nightcrew.Tests/IcoWriterTests.cs
git commit -m "feat: lantern and moon-and-star marks on a 24-unit grid, and an .ico writer"
```

### Task 5: Tray icons, the click panel and the tray wiring

The tray shows the chosen mark for the current state, rendered at the size Windows asks for and in Night ink on a light taskbar. A left click toggles the panel beside the taskbar; right click and the panel's Settings link open the existing menu, which gains **Tray mark**. Only the snapshot crosses from the timer thread to the UI thread. `AssetRenderer` renders the same panel tree for the README.

**Files:**
- Create: `src/Nightcrew/Platform/TaskbarTheme.cs`, `src/Nightcrew/App/Ui/PanelVisual.cs`, `src/Nightcrew/App/Ui/PanelPlacement.cs`, `src/Nightcrew/App/Ui/PanelWindow.cs`, `src/Nightcrew/App/Ui/AssetRenderer.cs`
- Modify: `src/Nightcrew/App/TrayIcons.cs`, `src/Nightcrew/App/TrayApp.cs`, `src/Nightcrew/Program.cs`
- Test: `tests/Nightcrew.Tests/TrayIconsTests.cs`, `tests/Nightcrew.Tests/PanelPlacementTests.cs`, `tests/Nightcrew.Tests/PanelVisualTests.cs`

**Interfaces:**
- Consumes: `PanelModel`, `PanelView`, `MarkState` (Task 2); `Theme` (Task 3); `MarkGeometry`, `IcoWriter`, `MarkKind`, `Settings.Mark` (Task 4); `Engine.Snapshot`, `GpuSignal(..., nameOf)`, `TranscriptSignal(..., holderWindow)` (Task 1).
- Produces: `TrayIcons.Get(MarkKind, MarkState, int pixels, bool lightTaskbar) : Icon`; `TaskbarTheme.IsLight()`; `record PanelActions(Action Hold, Action Pause, Action Settings)`; `PanelVisual.Build(PanelView, MarkKind, PanelActions?)`, `PanelVisual.Width = 320`, `PanelVisual.ShadowRoom = 16`; `enum TaskbarEdge`, `record struct Placement(Point TopLeft, TaskbarEdge Edge)`, `PanelPlacement.Place(Rect screen, Rect workArea, Point click, Size panel, double gap)`; `PanelWindow(PanelActions)` with `Render(PanelView, MarkKind)`, `ShowNear(System.Drawing.Point)`, `HidePanel()`, `JustHid`; `AssetRenderer.Scale = 2`, `SamplePanels`, `WritePanels(string, MarkKind = Lantern)`, `WriteAppIcon(string)`, `WriteMarks(string)`, `RenderPanel(PanelView, MarkKind)`.

- [ ] **Step 1: Write the failing tests**

`tests/Nightcrew.Tests/TrayIconsTests.cs`:

```csharp
using Nightcrew.App;
using Nightcrew.App.Ui;
using Nightcrew.Core;
using Xunit;

namespace Nightcrew.Tests;

public sealed class TrayIconsTests
{
    [Fact]
    public void Tray_icons_are_made_once_per_mark_state_size_and_theme()
    {
        Sta.Run(() =>
        {
            using var icons = new TrayIcons();
            var idle = icons.Get(MarkKind.Lantern, MarkState.Idle, 16, lightTaskbar: false);
            Assert.Same(idle, icons.Get(MarkKind.Lantern, MarkState.Idle, 16, lightTaskbar: false));
            Assert.NotSame(idle, icons.Get(MarkKind.Lantern, MarkState.Live, 16, lightTaskbar: false));
            Assert.NotSame(idle, icons.Get(MarkKind.Lantern, MarkState.Idle, 16, lightTaskbar: true));
            Assert.NotSame(idle, icons.Get(MarkKind.MoonAndStar, MarkState.Idle, 16, lightTaskbar: false));
            Assert.Equal(16, idle.Width);
            Assert.Equal(20, icons.Get(MarkKind.Lantern, MarkState.Idle, 20, lightTaskbar: false).Width);
        });
    }
}
```

`tests/Nightcrew.Tests/PanelPlacementTests.cs`:

```csharp
using System.Windows;
using Nightcrew.App.Ui;
using Xunit;

namespace Nightcrew.Tests;

public sealed class PanelPlacementTests
{
    private static readonly Rect Screen = new(0, 0, 1920, 1080);
    private static readonly Size Panel = new(352, 500);

    [Fact]
    public void A_bottom_taskbar_puts_the_panel_above_it_centred_on_the_click()
    {
        var placement = PanelPlacement.Place(Screen, new Rect(0, 0, 1920, 1032), new Point(1000, 1050), Panel, 12);

        Assert.Equal(TaskbarEdge.Bottom, placement.Edge);
        Assert.Equal(new Point(1000 - 176, 1032 - 12 - 500), placement.TopLeft);
    }

    [Fact]
    public void A_click_near_the_corner_keeps_the_panel_inside_the_work_area()
    {
        var placement = PanelPlacement.Place(Screen, new Rect(0, 0, 1920, 1032), new Point(1900, 1050), Panel, 12);
        Assert.Equal(1920 - 12 - 352, placement.TopLeft.X);
    }

    [Fact]
    public void A_top_taskbar_puts_the_panel_below_it()
    {
        var placement = PanelPlacement.Place(Screen, new Rect(0, 48, 1920, 1032), new Point(1000, 20), Panel, 12);

        Assert.Equal(TaskbarEdge.Top, placement.Edge);
        Assert.Equal(48 + 12, placement.TopLeft.Y);
    }

    [Fact]
    public void A_left_taskbar_puts_the_panel_to_its_right()
    {
        var placement = PanelPlacement.Place(Screen, new Rect(60, 0, 1860, 1080), new Point(30, 900), Panel, 12);

        Assert.Equal(TaskbarEdge.Left, placement.Edge);
        Assert.Equal(new Point(72, 1080 - 12 - 500), placement.TopLeft); // centred on the click, clamped to the bottom
    }

    [Fact]
    public void A_right_taskbar_puts_the_panel_to_its_left()
    {
        var placement = PanelPlacement.Place(Screen, new Rect(0, 0, 1860, 1080), new Point(1890, 400), Panel, 12);

        Assert.Equal(TaskbarEdge.Right, placement.Edge);
        Assert.Equal(new Point(1860 - 12 - 352, 150), placement.TopLeft);
    }

    [Fact]
    public void An_auto_hidden_taskbar_is_treated_as_bottom()
    {
        var placement = PanelPlacement.Place(Screen, Screen, new Point(1800, 1075), Panel, 12);

        Assert.Equal(TaskbarEdge.Bottom, placement.Edge);
        Assert.Equal(1080 - 12 - 500, placement.TopLeft.Y);
    }

    [Fact]
    public void A_panel_taller_than_the_work_area_keeps_its_top_on_screen()
    {
        var placement = PanelPlacement.Place(Screen, new Rect(0, 0, 1920, 400), new Point(1000, 1050), Panel, 12);
        Assert.Equal(12, placement.TopLeft.Y);
    }

    [Fact]
    public void A_second_monitor_uses_its_own_coordinates()
    {
        var right = new Rect(1920, -200, 2560, 1440);
        var placement = PanelPlacement.Place(right, new Rect(1920, -200, 2560, 1392), new Point(4400, 1220), Panel, 12);

        Assert.Equal(TaskbarEdge.Bottom, placement.Edge);
        Assert.Equal(new Point(1920 + 2560 - 12 - 352, -200 + 1392 - 12 - 500), placement.TopLeft);
    }
}
```

`tests/Nightcrew.Tests/PanelVisualTests.cs`:

```csharp
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Nightcrew.App.Ui;
using Nightcrew.Core;
using Xunit;

namespace Nightcrew.Tests;

public sealed class PanelVisualTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nightcrew-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static PanelView View(string name) => AssetRenderer.SamplePanels.Single(sample => sample.Name == name).View;

    /// <summary>Every string the panel shows, in order: text blocks, with a headline's runs joined.</summary>
    private static List<string> Texts(DependencyObject root)
    {
        var texts = new List<string>();
        void Walk(object node)
        {
            if (node is TextBlock block)
            {
                texts.Add(block.Inlines.Count > 0 ? string.Concat(block.Inlines.OfType<Run>().Select(run => run.Text)) : block.Text);
                return;
            }
            if (node is DependencyObject parent)
            {
                foreach (var child in LogicalTreeHelper.GetChildren(parent)) Walk(child);
            }
        }
        Walk(root);
        return texts;
    }

    [Fact]
    public void Working_shows_the_headline_holders_facts_and_buttons()
    {
        var texts = Sta.Run(() => Texts(PanelVisual.Build(View("working"), MarkKind.Lantern, null)));

        Assert.Equal(
        [
            "Settings", "Nightcrew", "Working through the night.",
            "Close the lid. Your laptop keeps working, then does what your lid normally does.",
            "Keeping it awake", "Claude Code", "2 sessions", "Codex", "working",
            "Battery", "64% · stops at 20%", "When done", "Sleeps", "Hold awake", "Pause",
        ], texts);
    }

    [Fact]
    public void Quiet_and_paused_have_no_holder_box()
    {
        var quiet = Sta.Run(() => Texts(PanelVisual.Build(View("quiet"), MarkKind.Lantern, null)));
        var paused = Sta.Run(() => Texts(PanelVisual.Build(View("paused"), MarkKind.MoonAndStar, null)));

        Assert.DoesNotContain("Keeping it awake", quiet);
        Assert.Contains("When the lid closes", quiet);
        Assert.DoesNotContain("Keeping it awake", paused);
        Assert.Contains("Resume", paused);
    }

    [Fact]
    public void A_warning_is_the_last_row()
    {
        var view = View("quiet") with { Warning = "Your power settings aren't back yet. Retrying." };
        var texts = Sta.Run(() => Texts(PanelVisual.Build(view, MarkKind.Lantern, null)));
        Assert.Equal("Your power settings aren't back yet. Retrying.", texts[texts.Count - 1]);
    }

    [Fact]
    public void A_long_holder_name_is_trimmed_instead_of_widening_the_panel()
    {
        var view = View("working") with { Holders = [new Holder(new string('x', 120), "busy"), new Holder("Codex", "working")] };
        var width = Sta.Run(() => AssetRenderer.RenderPanel(view, MarkKind.Lantern).PixelWidth);
        Assert.Equal((PanelVisual.Width + 2 * PanelVisual.ShadowRoom) * AssetRenderer.Scale, width);
    }

    [Fact]
    public void Hold_awake_fills_lavender_while_it_is_on()
    {
        var (off, on) = Sta.Run(() => (HoldFill(View("quiet")), HoldFill(View("quiet") with { HoldOn = true })));
        Assert.Equal(Theme.NightRaised, off);
        Assert.Equal(Theme.Lavender, on);

        static Color HoldFill(PanelView view)
        {
            var root = PanelVisual.Build(view, MarkKind.Lantern, null);
            var button = Find<Border>(root, border => System.Windows.Automation.AutomationProperties.GetName(border) == "Hold awake");
            return ((SolidColorBrush)button.Background).Color;
        }
    }

    [Fact]
    public void The_controls_call_their_actions()
    {
        var calls = Sta.Run(() =>
        {
            var calls = new List<string>();
            var root = PanelVisual.Build(View("working"), MarkKind.Lantern,
                new PanelActions(() => calls.Add("hold"), () => calls.Add("pause"), () => calls.Add("settings")));
            Click(Find<Border>(root, border => System.Windows.Automation.AutomationProperties.GetName(border) == "Pause"));
            Click(Find<Border>(root, border => System.Windows.Automation.AutomationProperties.GetName(border) == "Hold awake"));
            Click(Find<TextBlock>(root, text => text.Text == "Settings"));
            return calls;
        });
        Assert.Equal(["pause", "hold", "settings"], calls);

        static void Click(UIElement element) =>
            element.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
    }

    [Fact]
    public void The_readme_renders_write_three_panels_at_twice_the_size()
    {
        var paths = Sta.Run(() => AssetRenderer.WritePanels(_dir));

        Assert.Equal(["panel-quiet.png", "panel-working.png", "panel-paused.png"], paths.Select(Path.GetFileName));
        foreach (var path in paths)
        {
            using var stream = File.OpenRead(path);
            var frame = System.Windows.Media.Imaging.BitmapFrame.Create(stream, System.Windows.Media.Imaging.BitmapCreateOptions.None,
                System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
            Assert.Equal((PanelVisual.Width + 2 * PanelVisual.ShadowRoom) * 2, frame.PixelWidth);
            Assert.InRange(frame.PixelHeight, 400, 1200);
        }
    }

    [Fact]
    public void The_readme_marks_are_the_tile_and_the_lantern_in_each_state()
    {
        var paths = Sta.Run(() => AssetRenderer.WriteMarks(_dir));
        Assert.Equal(["tile.png", "mark-idle.png", "mark-live.png", "mark-lidclosed.png"], paths.Select(Path.GetFileName));
        Assert.All(paths, path => Assert.True(new FileInfo(path).Length > 0));
    }

    [Fact]
    public void The_app_icon_file_has_every_size()
    {
        var path = Path.Combine(_dir, "nightcrew.ico");
        Sta.Run(() => AssetRenderer.WriteAppIcon(path));

        var sizes = IcoSizes(File.ReadAllBytes(path));
        Assert.Equal(MarkGeometry.AppIconSizes, sizes);
    }

    private static List<int> IcoSizes(byte[] ico) =>
        Enumerable.Range(0, BitConverter.ToUInt16(ico, 4)).Select(i => ico[6 + 16 * i] is 0 ? 256 : (int)ico[6 + 16 * i]).ToList();

    private static T Find<T>(DependencyObject root, Func<T, bool> match) where T : DependencyObject
    {
        if (root is T hit && match(hit)) return hit;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            var found = TryFind(child, match);
            if (found is not null) return found;
        }
        throw new InvalidOperationException($"No {typeof(T).Name} matched");

        static T? TryFind(DependencyObject node, Func<T, bool> match)
        {
            if (node is T candidate && match(candidate)) return candidate;
            foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
            {
                var found = TryFind(child, match);
                if (found is not null) return found;
            }
            return null;
        }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run:

```bash
export DOTNET_ROOT="$LOCALAPPDATA/Microsoft/dotnet-sdk" PATH="$LOCALAPPDATA/Microsoft/dotnet-sdk:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
dotnet build -c Release 2>&1 | grep -E "error|Warning\(s\)|Error\(s\)" | sort -u
dotnet test -c Release --no-build 2>&1 | grep -E "^failed |total:|failed:"
```

Expected: FAIL: `error CS1501`/`CS0246`: `TrayIcons.Get`, `PanelPlacement`, `PanelVisual` and `AssetRenderer` don't exist yet.

- [ ] **Step 3: Write the implementation**

`src/Nightcrew/App/TrayIcons.cs`:

```csharp
using System.Drawing;
using Nightcrew.App.Ui;
using Nightcrew.Core;

namespace Nightcrew.App;

/// <summary>
/// Tray icons drawn from <see cref="MarkGeometry"/> at the size Windows asks for, so they follow display scaling.
/// Each one is made once per mark, state, size and taskbar theme.
/// </summary>
public sealed class TrayIcons : IDisposable
{
    private readonly Dictionary<(MarkKind, MarkState, int, bool), Icon> _icons = [];

    /// <summary>UI thread only, because the marks are drawn with WPF.</summary>
    public Icon Get(MarkKind kind, MarkState state, int pixels, bool lightTaskbar)
    {
        var key = (kind, state, pixels, lightTaskbar);
        if (_icons.TryGetValue(key, out var icon)) return icon;

        var ink = lightTaskbar ? Theme.Night : Theme.Moon;
        var bgra = MarkGeometry.ToBgra(MarkGeometry.Render(kind, state, pixels, ink));
        var ico = IcoWriter.Write([IcoWriter.Frame.Bitmap(pixels, bgra)]);
        icon = new Icon(new MemoryStream(ico), pixels, pixels); // owns its handle, so Dispose frees it
        _icons[key] = icon;
        return icon;
    }

    public void Dispose()
    {
        foreach (var icon in _icons.Values) icon.Dispose();
        _icons.Clear();
    }
}
```

`src/Nightcrew/Platform/TaskbarTheme.cs`:

```csharp
using Microsoft.Win32;

namespace Nightcrew.Platform;

public static class TaskbarTheme
{
    /// <summary>True when Windows draws a light taskbar. Read each time, so a theme switch shows on the next tick.</summary>
    public static bool IsLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int value && value == 1;
        }
        catch (Exception)
        {
            return false; // the default taskbar is dark
        }
    }
}
```

`src/Nightcrew/App/Ui/PanelVisual.cs`:

```csharp
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Nightcrew.Core;

namespace Nightcrew.App.Ui;

/// <summary>What the panel's three controls do. Null when the panel is only drawn, as for the README.</summary>
public sealed record PanelActions(Action Hold, Action Pause, Action Settings);

/// <summary>Builds the click panel from a <see cref="PanelView"/>. The same tree is shown in the window and rendered for the README.</summary>
public static class PanelVisual
{
    public const double Width = 320;

    /// <summary>Transparent room around the panel for its shadow.</summary>
    public const double ShadowRoom = 16;

    private const double Gutter = 20;

    /// <summary>The panel with its shadow room, ready to be a window's content or rendered to a bitmap.</summary>
    public static FrameworkElement Build(PanelView view, MarkKind mark, PanelActions? actions)
    {
        var body = new StackPanel();
        body.Children.Add(Header(mark, view.Mark, actions));
        body.Children.Add(Headline(view));
        body.Children.Add(Text(view.SubLine, 12.5, Theme.MoonMuted, bottom: 16, lineHeight: 18));
        if (view.Holders.Count > 0) body.Children.Add(Holders(view.Holders));
        body.Children.Add(Facts(view.Facts));
        body.Children.Add(Buttons(view, actions));
        if (view.Warning is { } warning) body.Children.Add(WarningRow(warning));

        var panel = new Border
        {
            Width = Width,
            Background = Theme.Brush(Theme.Night),
            BorderBrush = Theme.Brush(Theme.Hairline),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(Gutter, 16, Gutter, 18),
            Child = body,
            Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 4, Direction = 270, Opacity = 0.45, Color = Colors.Black },
        };
        TextOptions.SetTextFormattingMode(panel, TextFormattingMode.Ideal);
        return new Border { Padding = new Thickness(ShadowRoom), Background = Brushes.Transparent, Child = panel };
    }

    private static FrameworkElement Header(MarkKind kind, MarkState state, PanelActions? actions)
    {
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 14), LastChildFill = false };
        var settings = Text("Settings", 12, Theme.MoonFaint);
        settings.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(settings, Dock.Right);
        if (actions is not null) Clickable(settings, actions.Settings, Theme.MoonFaint, Theme.MoonMuted);
        header.Children.Add(settings);

        // The header is never smaller than 20 px on screen, so the mark takes the lighter stroke.
        var image = new Image { Source = MarkGeometry.Image(kind, state, 1.5, Theme.Moon), Width = 20, Height = 20, VerticalAlignment = VerticalAlignment.Center };
        header.Children.Add(image);
        var name = Text("Nightcrew", 13, Theme.Moon, FontWeights.Medium);
        name.Margin = new Thickness(8, 0, 0, 0);
        name.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(name);
        return header;
    }

    private static TextBlock Headline(PanelView view)
    {
        var headline = new TextBlock
        {
            FontFamily = Theme.Serif,
            FontSize = 26,
            Foreground = Theme.Brush(Theme.Moon),
            Margin = new Thickness(0, 0, 0, 6),
            TextWrapping = TextWrapping.Wrap,
        };
        headline.Inlines.Add(new Run(view.Headline));
        if (view.HeadlineItalic is { } italic) headline.Inlines.Add(new Run(italic) { FontStyle = FontStyles.Italic });
        return headline;
    }

    private static Border Holders(IReadOnlyList<Holder> holders)
    {
        var rows = new StackPanel();
        rows.Children.Add(Text("Keeping it awake", 11, Theme.MoonFaint, bottom: 6));
        foreach (var holder in holders)
        {
            rows.Children.Add(Row(holder.Label, Theme.Moon, holder.Detail, Theme.MoonMuted, new Thickness(0, 2, 0, 2)));
        }
        return new Border
        {
            Background = Theme.Brush(Theme.NightRaised),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 0, 0, 12),
            Child = rows,
        };
    }

    private static StackPanel Facts(IReadOnlyList<PanelFact> facts)
    {
        var list = new StackPanel();
        foreach (var fact in facts)
        {
            list.Children.Add(new Border
            {
                BorderBrush = Theme.Brush(Theme.Hairline),
                BorderThickness = new Thickness(0, 1, 0, 0),
                Child = Row(fact.Label, Theme.MoonMuted, fact.Value, Theme.Moon, new Thickness(0, 8, 0, 8)),
            });
        }
        return list;
    }

    private static Grid Buttons(PanelView view, PanelActions? actions)
    {
        var grid = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());

        var hold = Button("Hold awake", view.HoldOn, actions?.Hold);
        var pause = Button(view.PauseLabel, false, actions?.Pause);
        Grid.SetColumn(pause, 2);
        grid.Children.Add(hold);
        grid.Children.Add(pause);
        return grid;
    }

    private static Border Button(string label, bool on, Action? click)
    {
        var fill = on ? Theme.Lavender : Theme.NightRaised;
        var text = Text(label, 12.5, on ? Theme.Ink : Theme.Moon, FontWeights.Medium);
        text.HorizontalAlignment = HorizontalAlignment.Center;
        text.VerticalAlignment = VerticalAlignment.Center;
        var button = new Border { Height = 34, CornerRadius = new CornerRadius(8), Background = Theme.Brush(fill), Child = text };
        System.Windows.Automation.AutomationProperties.SetName(button, label);
        if (click is not null) Clickable(button, click, fill, on ? Theme.Lavender : Theme.NightHover);
        return button;
    }

    private static DockPanel WarningRow(string warning)
    {
        var row = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
        var dot = new System.Windows.Shapes.Ellipse { Width = 6, Height = 6, Fill = Theme.Brush(Theme.Warn), Margin = new Thickness(0, 6, 8, 0), VerticalAlignment = VerticalAlignment.Top };
        row.Children.Add(dot);
        row.Children.Add(Text(warning, 12, Theme.Warn, lineHeight: 17));
        return row;
    }

    private static Grid Row(string left, Color leftColour, string right, Color rightColour, Thickness padding)
    {
        var grid = new Grid { Margin = padding };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var label = Text(left, 12.5, leftColour);
        label.TextTrimming = TextTrimming.CharacterEllipsis;
        var value = Text(right, 12.5, rightColour);
        value.Margin = new Thickness(12, 0, 0, 0);
        Grid.SetColumn(value, 1);
        grid.Children.Add(label);
        grid.Children.Add(value);
        return grid;
    }

    private static TextBlock Text(string text, double size, Color colour, FontWeight? weight = null, double bottom = 0, double lineHeight = double.NaN) => new()
    {
        Text = text,
        FontFamily = Theme.Sans,
        FontSize = size,
        FontWeight = weight ?? FontWeights.Normal,
        Foreground = Theme.Brush(colour),
        TextWrapping = TextWrapping.Wrap,
        LineHeight = lineHeight,
        Margin = new Thickness(0, 0, 0, bottom),
    };

    /// <summary>Hand cursor, a hover colour, and the action on a left click.</summary>
    private static void Clickable(FrameworkElement element, Action click, Color normal, Color hover)
    {
        element.Cursor = Cursors.Hand;
        void Paint(Color colour)
        {
            if (element is Border border) border.Background = Theme.Brush(colour);
            else if (element is TextBlock text) text.Foreground = Theme.Brush(colour);
        }
        element.MouseEnter += (_, _) => Paint(hover);
        element.MouseLeave += (_, _) => Paint(normal);
        element.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            click();
        };
    }
}
```

`src/Nightcrew/App/Ui/PanelPlacement.cs`:

```csharp
using System.Windows;

namespace Nightcrew.App.Ui;

public enum TaskbarEdge { Bottom, Top, Left, Right }

public readonly record struct Placement(Point TopLeft, TaskbarEdge Edge);

/// <summary>Where the panel goes: against the taskbar, centred on the click, kept inside the work area.</summary>
public static class PanelPlacement
{
    /// <summary>
    /// All values in physical pixels. The taskbar edge is the side where the work area is smaller than the screen;
    /// with an auto-hidden taskbar there is none, and the panel sits at the bottom like the default taskbar.
    /// </summary>
    public static Placement Place(Rect screen, Rect workArea, Point click, Size panel, double gap)
    {
        var edge = workArea.Top > screen.Top ? TaskbarEdge.Top
            : workArea.Left > screen.Left ? TaskbarEdge.Left
            : workArea.Right < screen.Right ? TaskbarEdge.Right
            : TaskbarEdge.Bottom;

        var (x, y) = edge switch
        {
            TaskbarEdge.Top => (click.X - panel.Width / 2, workArea.Top + gap),
            TaskbarEdge.Left => (workArea.Left + gap, click.Y - panel.Height / 2),
            TaskbarEdge.Right => (workArea.Right - gap - panel.Width, click.Y - panel.Height / 2),
            _ => (click.X - panel.Width / 2, workArea.Bottom - gap - panel.Height),
        };
        x = Clamp(x, workArea.Left + gap, workArea.Right - gap - panel.Width);
        y = Clamp(y, workArea.Top + gap, workArea.Bottom - gap - panel.Height);
        return new Placement(new Point(x, y), edge);
    }

    /// <summary>Prefers the low bound when the panel is larger than the room, so its top-left stays on screen.</summary>
    private static double Clamp(double value, double low, double high) => Math.Max(low, Math.Min(high, value));
}
```

`src/Nightcrew/App/Ui/PanelWindow.cs`:

```csharp
using System.Windows;
using System.Windows.Forms.Integration;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Nightcrew.Core;

namespace Nightcrew.App.Ui;

/// <summary>
/// The borderless click panel. It lives on the tray app's UI thread; WinForms runs the message loop, and
/// <see cref="ElementHost.EnableModelessKeyboardInterop"/> lets it take Esc.
/// </summary>
public sealed class PanelWindow : Window
{
    private static readonly TimeSpan FadeIn = TimeSpan.FromMilliseconds(120);
    private const double Slide = 8;
    private const double Gap = 12;

    private readonly System.Windows.Controls.Border _frame;
    private readonly TranslateTransform _slide = new();
    private readonly PanelActions _actions;
    private DateTime _hiddenAt = DateTime.MinValue;

    public PanelWindow(PanelActions actions)
    {
        _actions = actions;
        Title = "Nightcrew";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        _frame = new System.Windows.Controls.Border { RenderTransform = _slide };
        Content = _frame;
        Deactivated += (_, _) => HidePanel();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) HidePanel();
        };
        ElementHost.EnableModelessKeyboardInterop(this);
    }

    /// <summary>
    /// True just after the panel hid itself because it lost focus. A click on the tray icon first takes focus
    /// from the panel, then arrives as a click: without this it would close the panel and open it again.
    /// </summary>
    public bool JustHid => DateTime.UtcNow - _hiddenAt < TimeSpan.FromMilliseconds(400);

    public void Render(PanelView view, MarkKind mark) => _frame.Child = PanelVisual.Build(view, mark, _actions);

    /// <summary>Opens beside the taskbar near <paramref name="click"/> (physical pixels), fading and sliding in.</summary>
    public void ShowNear(System.Drawing.Point click)
    {
        new WindowInteropHelper(this).EnsureHandle();
        var dpi = VisualTreeHelper.GetDpi(this);
        _frame.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = _frame.DesiredSize;

        var screen = System.Windows.Forms.Screen.FromPoint(click);
        var room = PanelVisual.ShadowRoom * dpi.DpiScaleX;
        var placement = PanelPlacement.Place(ToRect(screen.Bounds), ToRect(screen.WorkingArea), new Point(click.X, click.Y),
            new Size(size.Width * dpi.DpiScaleX, size.Height * dpi.DpiScaleY), Gap * dpi.DpiScaleX - room);
        Left = placement.TopLeft.X / dpi.DpiScaleX;
        Top = placement.TopLeft.Y / dpi.DpiScaleY;

        var (property, from) = placement.Edge switch
        {
            TaskbarEdge.Top => (TranslateTransform.YProperty, -Slide),
            TaskbarEdge.Left => (TranslateTransform.XProperty, -Slide),
            TaskbarEdge.Right => (TranslateTransform.XProperty, Slide),
            _ => (TranslateTransform.YProperty, Slide),
        };
        _slide.X = _slide.Y = 0;
        Opacity = 0;
        Show();
        Activate();
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, FadeIn) { EasingFunction = ease });
        _slide.BeginAnimation(property, new DoubleAnimation(from, 0, FadeIn) { EasingFunction = ease });
    }

    public void HidePanel()
    {
        if (!IsVisible) return;
        _hiddenAt = DateTime.UtcNow;
        Hide();
    }

    private static Rect ToRect(System.Drawing.Rectangle r) => new(r.X, r.Y, r.Width, r.Height);
}
```

`src/Nightcrew/App/Ui/AssetRenderer.cs`:

```csharp
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Nightcrew.Core;

namespace Nightcrew.App.Ui;

/// <summary>
/// Renders the README's panel images and the app's .ico from the real code. <c>build/render-assets.ps1</c> loads
/// the built exe as a library and calls these; the tray app's Main never runs. Call on an STA thread.
/// </summary>
public static class AssetRenderer
{
    public const double Scale = 2;

    /// <summary>The three panel states from fixed sample snapshots, as the README shows them.</summary>
    public static IReadOnlyList<(string Name, PanelView View)> SamplePanels { get; } =
    [
        ("quiet", PanelModel.From(new EngineSnapshot(Mode.Idle, LidState.Open, false, [], PowerSource.AC, 81,
            LidAction.Sleep, false, false, EngineWarning.None), 20)),
        ("working", PanelModel.From(new EngineSnapshot(Mode.Armed, LidState.Open, true,
            [new Holder("Claude Code", "2 sessions"), new Holder("Codex", "working")], PowerSource.DC, 64,
            LidAction.Sleep, false, false, EngineWarning.None), 20)),
        ("paused", PanelModel.From(new EngineSnapshot(Mode.Paused, LidState.Open, false, [], PowerSource.AC, 81,
            LidAction.Sleep, false, true, EngineWarning.None), 20)),
    ];

    /// <summary>Writes panel-quiet.png, panel-working.png and panel-paused.png at 2x.</summary>
    public static IReadOnlyList<string> WritePanels(string directory, MarkKind mark = MarkKind.Lantern)
    {
        Directory.CreateDirectory(directory);
        return SamplePanels.Select(sample =>
        {
            var path = Path.Combine(directory, $"panel-{sample.Name}.png");
            File.WriteAllBytes(path, MarkGeometry.ToPng(RenderPanel(sample.View, mark)));
            return path;
        }).ToList();
    }

    /// <summary>Writes the app's multi-size .ico, which the project sets as its ApplicationIcon.</summary>
    public static void WriteAppIcon(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllBytes(path, IcoWriter.Write(MarkGeometry.AppIconFrames()));
    }

    /// <summary>
    /// The images the README's HTML pages use: the app tile and the lantern in each state. Writing them from the
    /// real geometry keeps the README's marks identical to the app's.
    /// </summary>
    public static IReadOnlyList<string> WriteMarks(string directory)
    {
        Directory.CreateDirectory(directory);
        var written = new List<string>();
        void Write(string name, BitmapSource bitmap)
        {
            var path = Path.Combine(directory, name);
            File.WriteAllBytes(path, MarkGeometry.ToPng(bitmap));
            written.Add(path);
        }
        Write("tile.png", MarkGeometry.RenderAppIcon(512));
        foreach (var state in new[] { MarkState.Idle, MarkState.Live, MarkState.LidClosed })
        {
            Write($"mark-{state.ToString().ToLowerInvariant()}.png", MarkGeometry.Render(MarkKind.Lantern, state, 192, Theme.Moon));
        }
        return written;
    }

    public static BitmapSource RenderPanel(PanelView view, MarkKind mark)
    {
        var element = PanelVisual.Build(view, mark, actions: null);
        element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        element.Arrange(new Rect(element.DesiredSize));
        element.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth * Scale), (int)Math.Ceiling(element.ActualHeight * Scale),
            96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
        bitmap.Render(element);
        bitmap.Freeze();
        return bitmap;
    }
}
```

`src/Nightcrew/App/TrayApp.cs`:

```csharp
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
```

`src/Nightcrew/Program.cs`:

```csharp
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

        using var instance = new Mutex(initiallyOwned: false, @"Local\Nightcrew.Instance");
        if (!TakeInstance(instance)) return 0;
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
        using var instance = new Mutex(initiallyOwned: false, @"Local\Nightcrew.Instance");
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
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        var settings = SettingsStore.Load(AppPaths.SettingsFile);
        var dryRun = dryRunFlag || settings.DryRun; // the flag is not saved into settings
        log.Info(dryRun ? "Nightcrew started (dry run)" : "Nightcrew started");

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

        using var events = new PowerEventsWindow();
        using var realKeepAwake = new KeepAwake();
        using var gpu = new PdhGpuUsage();
        var realPower = new SystemPower(() => events.Handle);
        IPowerPolicy policy = dryRun ? new DryRunPolicy(realPolicy, log) : realPolicy;
        ISystemPower power = dryRun ? new DryRunSystemPower(realPower, log) : realPower;
        IKeepAwake keepAwake = dryRun ? new DryRunKeepAwake(log) : realKeepAwake;
        var journal = new RestoreJournal(policy, AppPaths.RestoreFile, log);

        var grace = TimeSpan.FromMinutes(settings.GraceMinutes);
        IBusySignal[] signals =
        [
            new TranscriptSignal(new JsonlFileScanner(), settings.TranscriptRoots, settings.IgnorePathContains, grace),
            new AgentTreeSignal(new ToolhelpProcessTable(), settings),
            new GpuSignal(gpu, settings.GpuPercent, ProcessName),
        ];
        var activity = new ActivityMonitor(signals, grace);
        var controller = new Controller(settings);
        var engine = new Engine(activity, controller, journal, policy, keepAwake, power, log, new SystemClock());

        using var tray = new TrayApp(settings, dryRun, exePath, events, activity, controller, engine, log);
        Application.Run(tray);
    }

    /// <summary>For the panel's holder rows; null when the process has already gone.</summary>
    private static string? ProcessName(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return process.ProcessName;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run:

```bash
export DOTNET_ROOT="$LOCALAPPDATA/Microsoft/dotnet-sdk" PATH="$LOCALAPPDATA/Microsoft/dotnet-sdk:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
dotnet build -c Release 2>&1 | grep -E "error|Warning\(s\)|Error\(s\)" | sort -u
dotnet test -c Release --no-build 2>&1 | grep -E "^failed |total:|failed:"
```

Expected: `0 Warning(s)`, `total: 246`, `failed: 0`.

```bash
powershell -NoProfile -File build/check-single-file.ps1
```

Expected: `Single-file gate passed`.

- [ ] **Step 5: Commit**

```bash
git add src/Nightcrew tests/Nightcrew.Tests
git commit -m "feat: the click panel, tray marks by state and taskbar theme, and the Tray mark menu"
```

- [ ] **Step 6: Owner check (the owner runs this, not the agent)**

Quit the running Nightcrew, then start the new build with `nightcrew --dry-run`. Left-click the lantern: the panel opens beside the taskbar, closes on Esc, on a click elsewhere and on a second click of the icon. Check the quiet, working (start an agent) and paused states, **Hold awake**, **Settings**, and switch **Tray mark** between Lantern and Moon and star.

### Task 6: App icon, README images and the README

`build/render-assets.ps1` writes the app icon and the panel images from the real code, then screenshots four HTML pages with headless Chrome. The README is rewritten around them in Insomnia's plain voice, dark only. The spec records where the build differs from its first draft.

**Files:**
- Create: `build/render-assets.ps1`, `docs/assets/src/style.css`, `docs/assets/src/banner.html`, `docs/assets/src/night.html`, `docs/assets/src/changes.html`, `docs/assets/src/social.html`, and the generated `src/Nightcrew/nightcrew.ico`, `docs/assets/*.png`, `docs/assets/src/*.png`
- Modify: `src/Nightcrew/Nightcrew.csproj`, `README.md`, `CONTRIBUTING.md`, `docs/superpowers/specs/2026-10-13-nightcrew-ui-design.md`
- Test: `tests/Nightcrew.Tests/AppIconFileTests.cs`

**Interfaces:**
- Consumes: `AssetRenderer.WriteAppIcon`, `WritePanels`, `WriteMarks` and `MarkGeometry.AppIconSizes` (Tasks 4 and 5).
- Produces: the committed `nightcrew.ico` (set as `ApplicationIcon`) and the README images.

- [ ] **Step 1: Write the failing test**

`tests/Nightcrew.Tests/AppIconFileTests.cs`:

```csharp
using Nightcrew.App.Ui;
using Xunit;

namespace Nightcrew.Tests;

/// <summary>The committed .ico is made by build/render-assets.ps1; this catches a stale one.</summary>
public sealed class AppIconFileTests
{
    [Fact]
    public void The_committed_app_icon_matches_the_sizes_the_geometry_makes()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "Nightcrew.slnx"))) root = Path.GetDirectoryName(root.TrimEnd('\\'))!;
        var sizes = IcoSizes(File.ReadAllBytes(Path.Combine(root, "src", "Nightcrew", "nightcrew.ico")));
        Assert.Equal(MarkGeometry.AppIconSizes, sizes);
    }

    private static List<int> IcoSizes(byte[] ico) =>
        Enumerable.Range(0, BitConverter.ToUInt16(ico, 4)).Select(i => ico[6 + 16 * i] is 0 ? 256 : (int)ico[6 + 16 * i]).ToList();
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run:

```bash
export DOTNET_ROOT="$LOCALAPPDATA/Microsoft/dotnet-sdk" PATH="$LOCALAPPDATA/Microsoft/dotnet-sdk:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
dotnet build -c Release 2>&1 | grep -E "error|Warning\(s\)|Error\(s\)" | sort -u
dotnet test -c Release --no-build 2>&1 | grep -E "^failed |total:|failed:"
```

Expected: `total: 247`, `failed: 1`: `FileNotFoundException` for `src/Nightcrew/nightcrew.ico`.

This build also leaves the Task 5 exe in `src/Nightcrew/bin/Release/net48`, which Step 4 renders from.

- [ ] **Step 3: Write the render script, the README pages and the docs**

`build/render-assets.ps1`:

```powershell
# Renders the app icon and the README's images from the built exe.
#
#   dotnet build -c Release
#   powershell -ExecutionPolicy Bypass -File build/render-assets.ps1
#
# It loads nightcrew.exe as a library and calls AssetRenderer, so the tray app's Main never runs and nothing
# touches power settings. The HTML pages in docs/assets/src are then screenshotted by headless Chrome.
# Needs Windows PowerShell 5.1 (powershell.exe), which runs on .NET Framework like Nightcrew, and Google Chrome.
param(
  [string]$Exe = "src/Nightcrew/bin/Release/net48/nightcrew.exe",
  [string]$Chrome = "$env:ProgramFiles\Google\Chrome\Application\chrome.exe"
)
$ErrorActionPreference = "Stop"
if ($PSVersionTable.PSEdition -ne "Desktop") { throw "Run this with Windows PowerShell 5.1 (powershell.exe), not pwsh." }
if ([Threading.Thread]::CurrentThread.ApartmentState -ne "STA") { throw "Run this on an STA thread: powershell.exe -STA" }

$root = Split-Path -Parent $PSScriptRoot
$assets = Join-Path $root "docs\assets"
$sources = Join-Path $assets "src"

Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase
$assembly = [Reflection.Assembly]::LoadFrom([string](Resolve-Path (Join-Path $root $Exe)))
$renderer = $assembly.GetType("Nightcrew.App.Ui.AssetRenderer", $true)
$lantern = [Enum]::Parse($assembly.GetType("Nightcrew.Core.MarkKind", $true), "Lantern")

$renderer.GetMethod("WriteAppIcon").Invoke($null, @([string](Join-Path $root "src\Nightcrew\nightcrew.ico"))) | Out-Null
$renderer.GetMethod("WritePanels").Invoke($null, @([string]$assets, $lantern)) | Out-Null
$renderer.GetMethod("WriteMarks").Invoke($null, @([string]$sources)) | Out-Null
"Wrote nightcrew.ico, the panel images and the marks"

# name, width, height, device scale
$pages = @(
  @("banner", 1280, 400, 2),
  @("night", 1280, 400, 2),
  @("changes", 1280, 500, 2),
  @("social", 1280, 640, 1)
)
foreach ($page in $pages) {
  $name, $width, $height, $scale = $page
  $html = [Uri]::new((Join-Path $sources "$name.html")).AbsoluteUri
  $png = Join-Path $assets "$name.png"
  if (Test-Path $png) { Remove-Item $png }
  # Chrome reports progress on stderr, which Windows PowerShell would treat as an error.
  # --allow-file-access-from-files lets the page load the fonts from src/Nightcrew/Fonts.
  $ErrorActionPreference = "Continue"
  & $Chrome --headless=new --disable-gpu --hide-scrollbars --allow-file-access-from-files `
    --force-device-scale-factor=$scale --window-size="$width,$height" --virtual-time-budget=2000 `
    --screenshot="$png" $html 2>&1 | Out-Null
  $ErrorActionPreference = "Stop"
  if (-not (Test-Path $png)) { throw "Chrome didn't write $png" }
  "Wrote $name.png"
}
```

`docs/assets/src/style.css`:

```css
/* Moonlit tokens, the same as App/Ui/Theme.cs. Dark only. */
@font-face { font-family: "EB Garamond"; font-style: normal; src: url("../../../src/Nightcrew/Fonts/EBGaramond-Regular.ttf"); }
@font-face { font-family: "EB Garamond"; font-style: italic; src: url("../../../src/Nightcrew/Fonts/EBGaramond-Italic.ttf"); }
@font-face { font-family: "Figtree"; font-weight: 400; src: url("../../../src/Nightcrew/Fonts/Figtree-Regular.ttf"); }
@font-face { font-family: "Figtree"; font-weight: 500; src: url("../../../src/Nightcrew/Fonts/Figtree-Medium.ttf"); }

:root {
  --night: #1E1F21;
  --raised: #26282B;
  --moon: #FFFFEB;
  --muted: #C9C7B6;
  --faint: #8C8A7C;
  --hairline: rgba(255, 255, 235, 0.10);
  --lavender: #F0D7FF;
  --ink: #1A1A1A;
}

* { box-sizing: border-box; }

html, body {
  margin: 0;
  width: 100vw;
  height: 100vh;
  overflow: hidden;
  background: var(--night);
  color: var(--moon);
  font-family: "Figtree", sans-serif;
  -webkit-font-smoothing: antialiased;
}

.serif { font-family: "EB Garamond", serif; font-weight: 400; }
.serif em { font-style: italic; }

/* A faint lavender glow, like the lantern lighting the room. */
.glow {
  position: absolute;
  border-radius: 50%;
  background: radial-gradient(closest-side, rgba(240, 215, 255, 0.16), rgba(240, 215, 255, 0));
  pointer-events: none;
}
```

`docs/assets/src/banner.html`:

```html
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<title>Nightcrew banner</title>
<link rel="stylesheet" href="style.css">
<style>
  body { display: flex; align-items: center; padding: 0 112px; gap: 60px; position: relative; }
  .glow { width: 520px; height: 520px; left: 0; top: -60px; }
  .tile { width: 176px; height: 176px; position: relative; }
  .name { font-weight: 500; font-size: 22px; color: var(--muted); letter-spacing: 0.01em; margin-bottom: 14px; }
  h1 { margin: 0; font-size: 60px; line-height: 1.06; letter-spacing: -0.005em; }
</style>
</head>
<body>
  <div class="glow"></div>
  <img class="tile" src="tile.png" alt="">
  <div>
    <div class="name">Nightcrew</div>
    <h1 class="serif">Awake while your agents work.<br><em>Asleep when they're done.</em></h1>
  </div>
</body>
</html>
```

`docs/assets/src/night.html`:

```html
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<title>Nightcrew night timeline</title>
<link rel="stylesheet" href="style.css">
<style>
  body { padding: 64px 80px; }
  h2 { margin: 0 0 44px; font-size: 40px; line-height: 1.1; }
  .steps { display: grid; grid-template-columns: repeat(4, 1fr); gap: 32px; position: relative; }
  /* The hairline that runs through the four marks. */
  .steps::before { content: ""; position: absolute; left: 28px; right: 28px; top: 28px; height: 1px; background: var(--hairline); }
  .mark { width: 56px; height: 56px; display: block; position: relative; background: var(--night); padding: 0 2px; }
  .dim { opacity: 0.45; }
  .number { margin: 22px 0 8px; font-size: 14px; font-weight: 500; color: var(--faint); letter-spacing: 0.06em; }
  h3 { margin: 0 0 10px; font-size: 30px; line-height: 1.1; }
  p { margin: 0; font-size: 17px; line-height: 1.5; color: var(--muted); }
</style>
</head>
<body>
  <h2 class="serif">One night, <em>four steps.</em></h2>
  <div class="steps">
    <div>
      <img class="mark" src="mark-live.png" alt="">
      <div class="number">01</div>
      <h3 class="serif">An agent starts.</h3>
      <p>Claude Code or Codex gets to work. The lantern lights.</p>
    </div>
    <div>
      <img class="mark" src="mark-lidclosed.png" alt="">
      <div class="number">02</div>
      <h3 class="serif">You close the lid.</h3>
      <p>The laptop stays awake and locks. The screen goes dark.</p>
    </div>
    <div>
      <img class="mark" src="mark-idle.png" alt="">
      <div class="number">03</div>
      <h3 class="serif">The work ends.</h3>
      <p>Five quiet minutes later, your power settings go back.</p>
    </div>
    <div>
      <img class="mark dim" src="mark-idle.png" alt="">
      <div class="number">04</div>
      <h3 class="serif">It sleeps.</h3>
      <p>Your laptop does what the lid normally does.</p>
    </div>
  </div>
</body>
</html>
```

`docs/assets/src/changes.html`:

```html
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<title>What Nightcrew changes</title>
<link rel="stylesheet" href="style.css">
<style>
  body { padding: 64px 80px; }
  h2 { margin: 0 0 36px; font-size: 40px; line-height: 1.1; }
  table { width: 100%; border-collapse: collapse; font-size: 19px; }
  th { text-align: left; font-weight: 500; font-size: 14px; letter-spacing: 0.06em; color: var(--faint); padding: 0 0 14px; }
  td { padding: 17px 0; border-top: 1px solid var(--hairline); }
  td:first-child { color: var(--muted); width: 38%; }
  td:nth-child(2) { width: 34%; }
  .on { display: inline-block; background: var(--lavender); color: var(--ink); font-weight: 500; font-size: 16px; padding: 5px 14px; border-radius: 8px; }
  .back { color: var(--moon); }
  .none { color: var(--faint); }
  .note { margin: 26px 0 0; font-size: 17px; line-height: 1.5; color: var(--muted); }
</style>
</head>
<body>
  <h2 class="serif">What changes, <em>and what goes back.</em></h2>
  <table>
    <tr><th>SETTING</th><th>WHILE AN AGENT WORKS</th><th>WHEN IT'S DONE</th></tr>
    <tr><td>When I close the lid</td><td><span class="on">Do nothing</span></td><td class="back">Your setting again</td></tr>
    <tr><td>Sleep after</td><td><span class="on">Never</span></td><td class="back">Your setting again</td></tr>
    <tr><td>Keep the system running</td><td><span class="on">Requested</span></td><td class="back">Released</td></tr>
    <tr><td>Closing the lid</td><td><span class="on">Locks the PC</span></td><td class="none">Nothing extra</td></tr>
  </table>
  <p class="note">Your values are saved before anything changes. If Nightcrew crashes, a watchdog puts them back within 5 minutes.</p>
</body>
</html>
```

`docs/assets/src/social.html`:

```html
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<title>Nightcrew social preview</title>
<link rel="stylesheet" href="style.css">
<style>
  body { display: flex; flex-direction: column; justify-content: center; padding: 0 112px; position: relative; }
  .glow { width: 640px; height: 640px; left: -120px; top: -140px; }
  .tile { width: 132px; height: 132px; position: relative; margin-bottom: 40px; }
  h1 { margin: 0; font-size: 72px; line-height: 1.04; letter-spacing: -0.005em; position: relative; }
  p { margin: 28px 0 0; font-size: 24px; line-height: 1.4; color: var(--muted); max-width: 820px; position: relative; }
</style>
</head>
<body>
  <div class="glow"></div>
  <img class="tile" src="tile.png" alt="">
  <h1 class="serif">Awake while your agents work.<br><em>Asleep when they're done.</em></h1>
  <p>Nightcrew keeps a Windows laptop awake with the lid closed while Claude Code or Codex works.</p>
</body>
</html>
```

`README.md`:

```markdown
<p align="center">
  <img src="docs/assets/banner.png" alt="Nightcrew. Awake while your agents work. Asleep when they're done." width="100%">
</p>

<p align="center">
  <img src="https://img.shields.io/badge/Windows-10%20%C2%B7%2011-F0D7FF?style=flat-square&labelColor=26282B" alt="Windows 10 and 11">
  <img src="https://img.shields.io/badge/license-MIT-F0D7FF?style=flat-square&labelColor=26282B" alt="MIT licence">
  <img src="https://img.shields.io/badge/status-experimental-F5C27A?style=flat-square&labelColor=26282B" alt="Status: experimental">
</p>

Close your laptop and walk away while Claude Code or Codex keeps working.

- While an agent is working, Nightcrew keeps a Windows laptop awake with the
  lid closed.
- When the work ends, it puts your power settings back and does what your lid
  action says: sleep, hibernate or nothing.
- It lives in the tray. There's nothing to set up.

> [!WARNING]
> **Heat.** A closed laptop in a bag has almost no airflow. Don't run hard
> jobs, such as long builds or GPU work, in a closed bag. Nightcrew doesn't
> watch temperatures.

## Install

1. Download `nightcrew.exe` from [Releases](../../releases/latest).
2. Move it to a folder where it can stay, for example
   `%LOCALAPPDATA%\Programs\Nightcrew`. Start at login and the watchdog task
   remember this path.
3. Run it. The lantern appears in the tray.

Nightcrew needs Windows 10 (1903 or later) or Windows 11. It uses
.NET Framework 4.8, which is already part of Windows.

Nightcrew isn't code-signed. The first time you run it, SmartScreen shows
"Windows protected your PC". Click **More info**, then **Run anyway**. If
Smart App Control is on, it blocks unsigned apps it doesn't already trust and
offers no Run anyway, so Nightcrew may not start.

Each release includes `nightcrew.exe.sha256` and a GitHub build attestation.
To check your download, run `Get-FileHash .\nightcrew.exe` and compare the
hash with the one in that file.

## Using it

<img src="docs/assets/panel-working.png" alt="The Nightcrew panel while Claude Code and Codex are working" width="352" align="right">

The lantern in the tray shows what Nightcrew is doing:

- **Unlit.** No agent is working. Close the lid and Windows does what it
  normally does.
- **Flame.** An agent is working. Close the lid and the laptop keeps going.
- **Filled.** An agent is working and the lid is closed.

Click the lantern to open the panel. It says who is keeping the laptop awake,
your battery, and what happens when the work is done. **Hold awake** keeps the
laptop awake until you turn it off. **Pause** hands the lid back to Windows
until you resume.

Right-click the lantern, or click **Settings** in the panel, for the menu:

- **Hold awake** and **Pause**, as in the panel.
- **Battery floor.** 10, 15, 20 or 30%, or Off.
- **Lid-closed limit.** 2, 4, 8 or 12 hours, or Off.
- **Lock when lid closes.**
- **Start at login.**
- **Tray mark.** The lantern, or a moon and star.
- **Open log.**
- **Quit.** Puts your settings back, then exits.

<br clear="right">

### How it decides an agent is busy

Every 10 seconds Nightcrew looks for any of these:

- **Transcripts.** A Claude Code or Codex session file (`*.jsonl` under
  `%USERPROFILE%\.claude\projects` or `%USERPROFILE%\.codex\sessions`) grew,
  or a new one appeared.
- **Agent tool processes.** A program started by `claude` or `codex`, such as
  a build, a test run or a script, used at least 10% of one CPU core. Codex
  itself counts at 2%.
- **GPU.** Any process kept a GPU engine at least 30% busy. A game or a video
  counts too.
- **Hold awake.** You turned it on. It counts as busy until you turn it off.

Work counts as live until 5 minutes after the last busy check. The panel lists
each of these under "Keeping it awake".

## What happens when the lid closes

<img src="docs/assets/night.png" alt="One night in four steps: an agent starts, you close the lid, the work ends, it sleeps" width="100%">

While work is live, Nightcrew:

- sets the lid-close action to Do nothing and "Sleep after" to Never, on mains
  power and on battery
- asks Windows to keep the system running
- locks the PC when you close the lid during work, or when work starts while
  the lid is already closed and there has been no keyboard or mouse input for
  5 minutes (turn off **Lock when lid closes** if you don't want that)

When the work ends, Nightcrew puts your settings back. If the lid is open,
that's all. If the lid is closed, it waits until there has been no keyboard or
mouse input for 5 minutes, then follows your Windows lid action for the
current power source:

- **Sleep.** It turns the screen off and lets Windows sleep. If the laptop is
  still awake 5 minutes later, it hibernates.
- **Hibernate** or **Shut down.** It hibernates.
- **Do nothing.** The laptop stays on, as your setting says.

If work starts again first, Nightcrew keeps the laptop awake again. If you open
the lid first, it does nothing.

Your lid action is in Settings > System > Power & battery > Lid & power button
controls, or in Control Panel > Power Options > Choose what closing the lid
does.

### Battery floor and lid-closed limit

With the lid closed, Nightcrew stops early and hibernates, even if an agent is
still working, when:

- it's on battery and the charge falls to the battery floor (default 20%), or
- the lid has been closed for the lid-closed limit (default 8 hours).

It puts your settings back first. After that it won't keep the laptop awake
again until you open the lid. If hibernation is turned off on your PC
(`powercfg /a` lists what's available), Windows' own sleep timer takes over
instead.

The battery floor applies whenever the lid is closed on battery, even while
you work on a docked laptop. The lid-closed limit counts only time without
keyboard or mouse input, which starts 5 minutes after your last input, so
time spent working on a docked laptop with its lid closed doesn't count
toward it.

## How it puts things back

<img src="docs/assets/changes.png" alt="What changes while an agent works, and what goes back when it's done" width="100%">

Nightcrew saves your original values in `%LOCALAPPDATA%\Nightcrew\restore.json`
before changing anything. If you change one of these settings yourself while
Nightcrew is working, your change is kept.

It puts your settings back when the work ends, when you pause or quit it, when
you sign out or shut down, and the next time it starts after a crash or a
power cut. If Nightcrew crashes or is killed, a scheduled task called
Nightcrew Watchdog puts your settings back within 5 minutes.

If your organization's policy locks the power settings, Nightcrew can't change
them. It stays idle and the panel says so.

## Limits

- Don't switch Windows power plans while an agent is working. Nightcrew puts
  your settings back into the plan that is active at that moment, so the plan
  you switched away from keeps Do nothing and Never.
- A game or video that keeps a GPU engine at least 30% busy counts as busy,
  so the laptop stays awake while it runs.
- If the laptop leaves your Wi-Fi, for example in a bag, the agent loses its
  connection. To have Windows switch to your phone's hotspot by itself, open
  Settings > Network & internet > Wi-Fi > Manage known networks, select the
  hotspot and turn on **Connect automatically when in range**.

## Other agents

Nightcrew knows Claude Code and Codex. Any other agent is covered when it
uses the GPU, or when you turn on **Hold awake**. To teach Nightcrew about
another agent, edit `%LOCALAPPDATA%\Nightcrew\settings.json`:

- Add its process name to `AgentProcessNames`, so programs it starts count
  as agent tools.
- Add it to `SelfCpuAgentNames` too if the agent does its work in its own
  process.
- Add the folder where it writes `*.jsonl` session files to `TranscriptRoots`.

Quit Nightcrew before editing the file, then start it again. A key you leave
out keeps its default. The log is `nightcrew.log` in the same folder.

| Key | Default | Meaning |
|---|---|---|
| `GraceMinutes` | 5 | How long work stays live after the last busy check |
| `BatteryFloorPercent` | 20 | Hibernate at this charge with the lid closed and on battery; 0 = off |
| `LidClosedCapHours` | 8 | Hibernate after this long with the lid closed and no input; 0 = off |
| `AwayMinutes` | 5 | Minutes without input before the lid action runs, before lid-closed time counts toward the limit, and before the lock when work starts with the lid closed |
| `LockOnLidClose` | true | Lock the PC when the lid closes |
| `StartAtLogin` | true | Start Nightcrew when you sign in |
| `DryRun` | false | Log what Nightcrew would do without doing it |
| `TranscriptRoots` | Claude Code and Codex folders | Where session files are watched |
| `IgnorePathContains` | `claude-mem-observer` | Session files whose path contains this are ignored |
| `AgentProcessNames` | `claude`, `codex` | Agent processes; programs they start count as agent tools |
| `SelfCpuAgentNames` | `codex` | Agents whose own CPU use counts |
| `SelfCpuPercent` | 2 | CPU threshold for those agents, % of one core |
| `ToolCpuPercent` | 10 | CPU threshold for agent tools, % of one core |
| `GpuPercent` | 30 | GPU engine threshold, % |
| `SampleSeconds` | 10 | Seconds between checks |
| `Mark` | `lantern` | The tray mark: `lantern` or `moon` |

To watch what Nightcrew would do without letting it change anything, run
`nightcrew --dry-run` and read the log.

## Uninstall

1. Quit Nightcrew from the tray.
2. Open a terminal in the folder that holds the exe and run
   `.\nightcrew.exe --uninstall`. It puts your power settings back, removes
   Start at login and deletes the Nightcrew Watchdog task.
3. Delete `nightcrew.exe`. To remove settings and logs too, delete
   `%LOCALAPPDATA%\Nightcrew`.

## Credits

- The idea and the plain, honest voice come from
  [Insomnia](https://github.com/krishhgg/Insomnia) by Krish Garg.
- The cream ink, serif headline and lavender come from Wispr Flow's design
  language.
- Type is [EB Garamond](https://github.com/octaviopardo/EBGaramond12) and
  [Figtree](https://github.com/erikdkennedy/figtree), both under the SIL Open
  Font License. See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## License

MIT
```

`CONTRIBUTING.md`:

````markdown
# Contributing

Nightcrew is deliberately small. It keeps a laptop awake while agents work,
puts the settings back and follows the lid action when they stop. Please keep
changes to that job.

## Build and test

You need the .NET 10 SDK; `global.json` pins the version. In Git Bash, from
the repo root:

```bash
dotnet build -c Release
dotnet test -c Release
pwsh -File build/check-single-file.ps1
```

If the SDK is installed per user in `%LOCALAPPDATA%\Microsoft\dotnet-sdk`,
run this first:

```bash
export DOTNET_ROOT="$LOCALAPPDATA/Microsoft/dotnet-sdk"; export PATH="$DOTNET_ROOT:$PATH"; export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
```

To run one test class:

```bash
dotnet test -c Release -- --filter-class Nightcrew.Tests.<ClassName>
```

Without PowerShell 7, use `powershell -NoProfile -File` for the
single-file check.

The build must finish with 0 warnings, because warnings are errors.

## One exe

The release is a single `nightcrew.exe` and nothing else.
`build/check-single-file.ps1` fails if `src/Nightcrew/bin/Release/net48`
holds any other file.

- Don't add NuGet runtime packages. PolySharp is allowed because it only
  generates source at build time.
- Don't add files next to the exe. The fonts and the app icon are compiled
  into it, and the tray icons are drawn in code from `MarkGeometry`.
- JSON goes through `Nightcrew.Core.Json` (DataContractJsonSerializer).
  Persisted classes need `[DataContract]` and `[DataMember]`.

## C# on .NET Framework 4.8

The app targets `net48` with C# 14.

- **Allowed:** records, `init`, `required`, primary constructors, collection
  expressions and file-scoped namespaces.
- **Not allowed:**
  - default interface methods, static abstract members and ref fields
  - array range slicing (`arr[1..]`)
  - `Span`, `ValueTask`, `System.Text.Json` and `IAsyncEnumerable`
- Fix nullable warnings properly. Don't suppress them.

## Where code goes

| Namespace | Contents |
|---|---|
| `Nightcrew.Core` | Pure logic with no P/Invoke, unit-tested with fakes |
| `Nightcrew.Platform` | Thin Windows wrappers (P/Invoke and WinForms) |
| `Nightcrew.App` | Tray icon, menu and wiring |
| `Nightcrew.App.Ui` | The WPF panel, the marks, the theme and the asset renderer |

## Words and looks

Everything Nightcrew shows, from the panel to the tooltip and the menu, uses
sentence case, short plain sentences and contractions. No exclamation marks,
no "please", no "successfully". Say what happens, not how. The panel's words
all come from `PanelModel`, so they're unit-tested.

Nightcrew is dark only. Colours and fonts live in `App/Ui/Theme.cs`, and the
README pages in `docs/assets/src/style.css` use the same values.

## Icon and README images

The app icon (`src/Nightcrew/nightcrew.ico`) and the images in `docs/assets`
are made from the real code. After changing the marks, the panel or the pages
in `docs/assets/src`, build and render them again:

```bash
dotnet build -c Release
powershell -ExecutionPolicy Bypass -File build/render-assets.ps1
```

The script loads `nightcrew.exe` as a library and calls `AssetRenderer`, so
the tray app never starts. It needs Windows PowerShell 5.1 and Google Chrome.
GitHub's social preview, `docs/assets/social.png`, is uploaded by hand in the
repository settings.

## Tests

Every change to behaviour needs a test in `tests/Nightcrew.Tests`. Write the
test first and watch it fail.

Tests must never change the machine they run on. Use fakes for anything that:

- writes power settings
- holds a power request
- sleeps, hibernates, turns off the monitor or locks the workstation
- writes the `HKCU\...\Run` key
- registers a scheduled task

Tests may call read-only APIs on the real machine, such as reading power
values, the access check, battery status, the process list, performance
counters and file scans.

Changes to lid, sleep or hibernate behaviour also need the relevant steps of
[docs/hardware-checklist.md](docs/hardware-checklist.md) on a real laptop.

## Releases

Push a tag such as `v1.0.0`. The release workflow tests and builds the exe,
checks that it's a single file, writes `nightcrew.exe.sha256`, attests the
build and publishes the GitHub release. Pin the workflow's actions to full
commit SHAs before the first public release.
````

`docs/superpowers/specs/2026-10-13-nightcrew-ui-design.md`:

```markdown
# Nightcrew UI design

Date: 13 October 2026. Status: approved by the owner on 14 October 2026;
section 10 records what changed while building it. This builds on
[the v1 design](2026-10-13-nightcrew-design.md) and the code on `feat/v1`. It
changes how Nightcrew looks and reads, not what it does.

## 1. Goal

Give Nightcrew the design language of Insomnia and Wispr Flow:
- **Insomnia:** a single line mark that changes with state, a dark night
  palette, and short, plain, honest wording.
- **Wispr Flow:** a warm cream ink, a serif headline with an italic turn, and
  lavender accents.

Three surfaces change:
- **the tray mark**
- **a new click panel**
- **the README and GitHub page**

## 2. Owner decisions (13 October 2026)

- **Direction:** "Moonlit". A dark night panel in the Insomnia style, warmed
  with Wispr's serif headline and lavender accent. **Dark only**, with no light
  mode in the app or the README.
- **Marks:** two are built, **lantern** and **moon-and-star**. A `Mark`
  setting switches between them (default `lantern`) until the owner picks one.
- **Panel:** left-click opens it. It has three states (all quiet, working,
  paused) plus a warning row. There is no finishing state: it happens with the
  lid closed and nobody watching.
- **Agents:** Claude Code and Codex are detected out of the box. Other agents
  are covered by the GPU signal and Hold awake, and power users can add them
  through the settings lists (`AgentProcessNames`, `TranscriptRoots`). Built-in
  presets for other agents are a v2 item.
- **Tech:** WPF, which is part of .NET Framework 4.8. The single-file property
  is kept because WPF needs nothing extra, though it isn't a hard requirement.
- **Images:** GPT Image may be used for the app icon art and one optional
  atmosphere image, never for images that contain text.

## 3. Tokens

| Token | Value | Use |
|---|---|---|
| `Night` | `#1E1F21` | panel background, icon tile |
| `NightRaised` | `#26282B` | the "keeping it awake" box, hover |
| `Moon` | `#FFFFEB` | primary text and mark strokes (Wispr's cream, used as ink) |
| `MoonMuted` | `#C9C7B6` | secondary text |
| `MoonFaint` | `#8C8A7C` | the Settings link, captions |
| `Hairline` | `#FFFFEB` at 10% | dividers |
| `Lavender` | `#F0D7FF` | the lit flame or star, Hold awake when on, badges |
| `Ink` | `#1A1A1A` | text on lavender |
| `Warn` | `#F5C27A` | warning row text |
| `LavenderDeep` | `#8E5CC4` | the flame or star on a light taskbar |

**Type:**
- **Headline:** EB Garamond, regular and italic, 26 px. It is used for the
  single headline only.
- **Everything else:** Figtree, regular and medium, 11–13 px.
- **Embedding:** both fonts are embedded in the exe as WPF resources. Both are
  under the SIL Open Font License, which allows embedding; the licence text
  ships in `THIRD-PARTY-NOTICES.md`.
- **Fallback:** if a font fails to load, Georgia replaces EB Garamond and
  Segoe UI replaces Figtree.

**Shape:** the panel has 14 px corners, controls and rows 8 px. Spacing is on
a 4 px grid. There are no shadows inside the panel; the window itself gets the
standard Windows 11 shadow.

## 4. Marks

Both marks are drawn on a 24-unit grid, like Insomnia's eye, and kept as path
data in one place (`MarkGeometry`).

| State | Lantern | Moon and star |
|---|---|---|
| Idle | outline only, unlit | crescent outline |
| Live, lid open | lavender flame inside | small lavender star beside the moon |
| Live, lid closed | body filled lavender | moon filled lavender, star in cream |
| Paused | same as idle | same as idle |

**Stroke width:** 2 units at 16–20 px, so the mark stays legible in the tray,
and 1.5 units at 24 px and above.

**Tray icons:**
- **Rendering:** at runtime, for the size Windows asks for
  (`SystemInformation.SmallIconSize`, so it follows the display scaling).
- **Caching:** one icon per mark, per state, per size. These replace the
  coloured dots in `TrayIcons`.
- **Colours:** a cream stroke with lavender fills, which reads on both dark and
  light taskbars.

**App icon:**
- **What it is:** the lantern on a `Night` tile with 22% corners.
- **How it's made:** generated as a multi-size `.ico` (16 to 256 px) from the
  same geometry by a small build-time tool, and set as `ApplicationIcon`.
- **GPT Image:** may be used to explore a richer 256 px tile. The owner picks.
  The shipped `.ico` always keeps the vector mark at 16–48 px.

## 5. Click panel

### 5.1 Behaviour

- **Opening and closing:** a left-click on the tray icon toggles the panel. It
  closes when you click elsewhere, press Esc, or click the icon again.
- **Size and position:** 320 px wide, with the height set by its content. It
  sits beside the tray, against the taskbar edge, whichever side the taskbar
  is on.
- **Animation:** it fades and slides 8 px over 120 ms.
- **Right-click** still opens the existing settings menu. The panel's
  "Settings" link opens that same menu.
- **Updates:** the panel refreshes after every engine tick while it's open.

### 5.2 Content

| Part | All quiet | Working | Paused |
|---|---|---|---|
| Header | mark, "Nightcrew", Settings | same | same |
| Headline (serif) | All quiet. | Working *through the night.* | Paused. |
| Sub line | No agent is working. Close the lid and your laptop does what Windows says. | Close the lid. Your laptop keeps working, then does what your lid normally does. | Windows handles the lid until you resume. |
| Keeping it awake | hidden | one row per holder (see 5.3) | hidden |
| Facts | Battery · When the lid closes | Battery and floor · When done | Battery |
| Buttons | Hold awake · Pause | Hold awake · Pause | Hold awake · Resume |
| Warning row | when there is one (see 5.4) | same | same |

**Fact rows:**
- **Battery:** `81%` on AC. On battery: `64% · stops at 20%`, or `64%` alone
  when the floor is off.
- **When the lid closes** (all quiet): the owner's Windows lid action for the
  current power source, as `Sleep`, `Hibernate`, `Do nothing` or `Shut down`.
- **When done** (working): what happens after the work ends: `Sleeps`,
  `Hibernates`, `Stays on` (Do nothing), or `Hibernates` (Shut down).

**Hold awake** fills lavender while it's on.

### 5.3 Holders

Each signal reports who is holding the laptop awake, as label and detail
pairs:

| Source | Label | Detail |
|---|---|---|
| Claude transcripts | `Claude Code` | `2 sessions` (files under the Claude root that grew within the grace period) |
| Codex transcripts or codex CPU | `Codex` | `working` |
| A tool process under an agent | the process name, e.g. `python` | `busy` |
| GPU | the process name | `GPU 41%` |
| Hold awake | `Hold awake` | `on` |

`ActivityMonitor` keeps the holders seen within the grace period, so the rows
match "Live". Holders from user-added agents use the process name, or the
transcript root's folder name.

### 5.4 Warnings

One row, in `Warn` colour, shows the most important current problem:

| Condition | Text |
|---|---|
| The gate fails (policy or access) | Windows policy blocks lid changes here. Keep the lid open. |
| A failed arm is backing off | Couldn't change power settings. Trying again in a few minutes. |
| A restore is pending | Your power settings aren't back yet. Retrying. |
| The watchdog failed to register | The safety net isn't installed. Settings may stay changed if Nightcrew crashes. |

### 5.5 Wording rules

These apply everywhere, including the tooltip, menu and notifications:
- sentence case
- short, plain sentences
- contractions
- no exclamation marks, no "please", no "successfully"
- say what happens, not how

**Tooltip,** at most 63 characters:
- `Nightcrew · all quiet`
- `Nightcrew · working (Claude Code)`
- `Nightcrew · paused`

## 6. Architecture

| Unit | Where | Does |
|---|---|---|
| `MarkGeometry` | `App/Ui/MarkGeometry.cs` | Path data for both marks; builds a WPF `Drawing` for (mark, state, size); renders tray bitmaps |
| `Theme` | `App/Ui/Theme.cs` | Colour and brush constants; loads the embedded fonts, with fallback |
| `PanelModel` | `Core/PanelModel.cs` (pure) | Maps an engine snapshot to the strings and rows in section 5. Unit-tested |
| `PanelWindow` | `App/Ui/PanelWindow.xaml` + code-behind | The borderless WPF window; binds to `PanelModel`; positions itself; fade animation |
| `Holder` reporting | `Core/*Signal.cs`, `ActivityMonitor` | Signals expose `IReadOnlyList<Holder> Holders` from their last busy sample, and the monitor aggregates them within the grace period |
| `EngineSnapshot` | `Core/Engine.cs` | A record carrying mode, lid, holders, battery, floor, owner lid action, hold, paused and warning; built at the end of each tick |
| Tray wiring | `App/TrayApp.cs` | Left-click toggles the panel. After each tick, posts the snapshot to the UI thread, refreshes the open panel, and swaps the tray icon |

**Threading:**
- **UI thread:** the panel lives on the existing STA UI thread. WinForms and WPF
  share it, and `ElementHost.EnableModelessKeyboardInterop` gives the window
  keyboard input.
- **Engine:** ticks still run on the timer thread. Only the immutable snapshot
  crosses over to the UI thread.

**Build:**
- **Project:** `UseWPF` is added next to `UseWindowsForms`. The fonts are
  `<Resource>` items.
- **Single-file gate:** still passes, because WPF assemblies come with .NET
  Framework.
- **Exe size:** grows by about 0.6 MB of fonts.

## 7. README and GitHub

Everything is in the dark style. The assets live in `docs/assets/`:

| Asset | Made with | Shows |
|---|---|---|
| `banner.png` | HTML with the real fonts, rendered by headless Chrome at 2× | lantern tile, "Nightcrew", the serif tagline |
| `night.png` | same | the four-step night timeline: an agent starts, you close the lid, the work ends, it sleeps |
| `changes.png` | same | what Nightcrew changes and what it puts back |
| `panel.png` | the real WPF panel, rendered off-screen | the click panel while working |
| `social.png` | from the banner, 1280×640 | GitHub's social preview |
| `atmosphere.png` (optional) | GPT Image, no text | a closed laptop on a desk at night with the lantern glowing |

**`panel.png`:** comes from a hidden dev verb, `nightcrew --render-panel <dir>`.
It renders the three panel states from fixed sample snapshots to PNG, so the
README always shows the real panel.

**Tagline:** "Awake while your agents work. *Asleep when they're done.*"

**README sections,** in Insomnia's plain voice:
1. heat warning
2. install
3. using it
4. what happens when the lid closes
5. how it puts things back
6. limits
7. other agents
8. uninstall
9. credits

**Badges** use the token colours: Windows 10 and 11, MIT, status experimental.

**GPT Image:** model `gpt-image-1`, with the key from the user environment
variable `OPENAI_API_KEY`. Generated images are reviewed by the owner before
they are committed.

## 8. Testing

**Unit tests:**
- **`PanelModel`:** every row and string in section 5 for each state, the
  holder aggregation, the warning priority and the tooltip length.
- **`MarkGeometry`:** both marks parse, the three states differ, and the tray
  bitmaps come out at the requested size. Rendering runs off-screen on an STA
  test thread.
- **`--render-panel`:** writes three PNGs of the expected size.

**Owner check:** run `nightcrew --dry-run` and look at the panel in each
state, then switch `Mark` between `lantern` and `moon`.

## 9. Out of scope

- light mode
- built-in presets for other agents
- animations beyond the panel fade
- a finishing panel state
- a settings window (settings stay in the right-click menu and `settings.json`)

## 10. Changes while building (14 October 2026)

- **Code-only WPF.** The panel is built in C# (`App/Ui/PanelVisual.cs`) and
  hosted by `App/Ui/PanelWindow.cs`, with no XAML. The same tree is shown in
  the window and rendered for the README.
- **No hidden exe verb.** `build/render-assets.ps1` loads the built exe as a
  library and calls `AssetRenderer`, so rendering never starts the tray app.
  It writes `panel-quiet.png`, `panel-working.png` and `panel-paused.png`;
  the README shows `panel-working.png`. The same script writes the app icon
  and screenshots the README pages with headless Chrome.
- **App icon.** `src/Nightcrew/nightcrew.ico` is committed and set as
  `ApplicationIcon`. A test checks its sizes against `MarkGeometry`.
- **Light taskbars.** The tray mark uses `Night` ink and `LavenderDeep` when
  Windows' taskbar is light, read from the registry on each update.
- **Menu.** The `Mark` setting also has a **Tray mark** submenu.
- **Exe size.** About 1.5 MB, because EB Garamond's two files are about
  0.6 MB each.
- **Header mark.** 20 px with the 1.5-unit stroke, because the panel is never
  shown smaller than that.
```

- [ ] **Step 4: Render the icon and the images**

```bash
powershell -NoProfile -ExecutionPolicy Bypass -File build/render-assets.ps1
```

Expected:

```text
Wrote nightcrew.ico, the panel images and the marks
Wrote banner.png
Wrote night.png
Wrote changes.png
Wrote social.png
```

Open `docs/assets/banner.png`, `night.png`, `changes.png` and `panel-working.png` and check them against the spec: dark backgrounds, EB Garamond headlines with the italic turn, Figtree body text, the lit lantern.

- [ ] **Step 5: Set the app icon**

`src/Nightcrew/Nightcrew.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <UseWindowsForms>true</UseWindowsForms>
    <UseWPF>true</UseWPF>
    <AssemblyName>nightcrew</AssemblyName>
    <RootNamespace>Nightcrew</RootNamespace>
    <ApplicationManifest>app.manifest</ApplicationManifest>
    <!-- Made by build/render-assets.ps1 from MarkGeometry; rerun it after changing the marks. -->
    <ApplicationIcon>nightcrew.ico</ApplicationIcon>
    <GenerateSupportedRuntime>false</GenerateSupportedRuntime>
    <AutoGenerateBindingRedirects>false</AutoGenerateBindingRedirects>
  </PropertyGroup>
  <ItemGroup>
    <InternalsVisibleTo Include="Nightcrew.Tests" />
    <!-- WinForms and WPF share type names (Color, FontFamily, Application), so each file says which it uses.
         UseWPF drops System.IO from the implicit usings; nothing here uses System.Windows.Shapes, so it comes back. -->
    <Using Remove="System.Drawing" />
    <Using Remove="System.Windows.Forms" />
    <Using Include="System.IO" />
  </ItemGroup>
  <ItemGroup>
    <!-- Both fonts are under the SIL Open Font License; see THIRD-PARTY-NOTICES.md. -->
    <Resource Include="Fonts\*.ttf" />
  </ItemGroup>
</Project>
```

- [ ] **Step 6: Run the tests to verify they pass**

Run:

```bash
export DOTNET_ROOT="$LOCALAPPDATA/Microsoft/dotnet-sdk" PATH="$LOCALAPPDATA/Microsoft/dotnet-sdk:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
dotnet build -c Release 2>&1 | grep -E "error|Warning\(s\)|Error\(s\)" | sort -u
dotnet test -c Release --no-build 2>&1 | grep -E "^failed |total:|failed:"
```

Expected: `0 Warning(s)`, `total: 247`, `failed: 0`.

```bash
powershell -NoProfile -File build/check-single-file.ps1
```

Expected: `Single-file gate passed`.

- [ ] **Step 7: Commit**

```bash
git add build/render-assets.ps1 docs/assets src/Nightcrew/nightcrew.ico src/Nightcrew/Nightcrew.csproj README.md CONTRIBUTING.md docs/superpowers/specs/2026-10-13-nightcrew-ui-design.md tests/Nightcrew.Tests/AppIconFileTests.cs
git commit -m "docs: app icon, dark README images rendered from the real code, and the README rewrite"
```

- [ ] **Step 8: Owner follow-ups (not part of the branch)**

- Upload `docs/assets/social.png` as the social preview in the repository settings.
- Optional: GPT Image candidates for a richer 256 px icon tile and the atmosphere image go to the owner for review before anything is committed.
