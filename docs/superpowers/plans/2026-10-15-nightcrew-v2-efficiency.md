# Nightcrew v2: Efficiency mode while away — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** while an agent works with the lid closed and nobody at the laptop, put the apps the agents don't need into Windows' Efficiency mode, and put them back exactly as they were when you return.

**Architecture:** a pure gate and planner decide what to change each tick; `EfficiencyMode` (Platform) applies power throttling plus idle priority and keeps a record so a crash can be undone; `EfficiencyCoordinator` drives it from the engine, and the tray app restores at once on lid open, input, suspend, session end, pause and quit.

**Tech Stack:** C# 14 on .NET Framework 4.8 (one exe, WinForms + WPF), xUnit v3 on Microsoft.Testing.Platform, Win32 P/Invoke (kernel32, user32, dwmapi, iphlpapi).

**Spec:** `docs/superpowers/specs/2026-10-15-nightcrew-v2-efficiency-design.md`

Every line of code below was compiled and tested in a scratch copy before it went into this plan: each task's commit builds on its own and the whole suite passes after it.

## Global Constraints

- One exe: no new NuGet packages, and `build/check-single-file.ps1` must still pass.
- `TreatWarningsAsErrors` is on; unused constructor parameters and unreachable code fail the build.
- Never run `nightcrew.exe` itself; tests only touch processes and windows they create.
- Efficiency mode = `SetProcessInformation(ProcessPowerThrottling)` with ControlMask = StateMask = `PROCESS_POWER_THROTTLING_EXECUTION_SPEED`, plus `IDLE_PRIORITY_CLASS`. Access: `PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_SET_INFORMATION` only.
- A process with priority Idle or Below normal, or with any throttling choice of its own, is left alone and not recorded. On restore: priority back only if still Idle; throttling cleared only if still on (or unreadable).
- The record is `%LOCALAPPDATA%\Nightcrew\efficiency.json`, written atomically before every batch of changes and deleted when empty.
- Settings keys and defaults: `EfficiencyWhileAway` true, `LeaveAlone` [], `ConnectorHostNames` [`chrome-native-host`, `extension-host`].
- Dry-run log lines start with `[dry run] would`. Real log lines: `Efficiency mode on: <Name> (<root>, N process(es))`, `Efficiency mode off for N app(s): <reason>`, `Left alone: <root> (<rule>), …`, `Can't change <root>: access denied`.
- UI copy, exactly: `Efficiency mode while away`, `Always leave alone`, `In Efficiency mode while you were away`, `Leave alone`, `Remove <name>` (automation name of the ×). No explanations under settings.
- Commit messages follow the repo: `feat:`/`docs:` and a plain sentence, ending with the Co-Authored-By line.

## Review Focus

- An app that exits between the plan and `Apply`: skipped, nothing recorded (task 1, `A_process_that_has_exited_is_skipped`).
- A corrupt `efficiency.json` at startup: moved aside to `.corrupt`, a warning, nothing else breaks (task 1, `A_corrupt_record_is_moved_aside_and_nothing_breaks`).
- Switching **Efficiency mode while away** off while away: every app back on the next tick (task 4, `Switching_it_off_while_away_puts_everything_back`).
- Adding an app to **Always leave alone** while it's slowed: back on the next tick (task 4, `An_app_added_to_your_list_while_away_goes_back_on_the_next_tick`).
- The lid opening in the middle of an apply pass: nothing changes after the restore (task 1, `A_pass_that_began_before_RestoreAll_changes_nothing`; task 4, `A_pass_overtaken_by_a_restore_stops`).

## How to build and test

From the repo root, in PowerShell:

```powershell
$env:DOTNET_ROOT = "$env:LOCALAPPDATA\Microsoft\dotnet-sdk"; $env:PATH = "$env:DOTNET_ROOT;$env:PATH"
dotnet build -c Release
dotnet test -c Release --no-build -- --filter-class Nightcrew.Tests.<Class>
dotnet test -c Release --no-build
powershell -NoProfile -File build/check-single-file.ps1
```

---

### Task 1: Efficiency mode core

Spec sections 5.1–5.4. This is the classifier checkpoint (spec section 10): it's the only code that changes other processes, so it comes first and is tested only on processes the tests start themselves.

**Files:**
- Create: `src/Nightcrew/Core/Efficiency.cs`
- Create: `src/Nightcrew/Core/EfficiencyRecord.cs`
- Create: `src/Nightcrew/Platform/EfficiencyMode.cs`
- Test (create): `tests/Nightcrew.Tests/EfficiencyModeTests.cs`

**Interfaces:**
- Consumes: `Json`, `AtomicFile`, `ILog`, `ToolhelpProcessTable.TryGetTimes` (tests only)
- Produces: `ProcessRef(int Pid, DateTime StartUtc)`
- Produces: `AppFamily(string DisplayName, string RootName, IReadOnlyList<ProcessRef> Processes)` with `ProcessRef Root` (the first process)
- Produces: `enum ApplyResult { Applied, Unchanged, Denied, Stale }`
- Produces: `IEfficiencyMode`: `int Generation`, `IReadOnlyCollection<ProcessRef> Applied` (roots), `ApplyResult Apply(AppFamily family, int generation)`, `void Restore(ProcessRef root, string reason)`, `int RestoreAll(string reason)` (number of apps)
- Produces: `EfficiencyMode(string path, ILog log) : IEfficiencyMode`; its constructor loads leftovers from the record, so `RestoreAll` also puts back what a crashed run left
- Produces: `internal static string EfficiencyMode.Describe(AppFamily family, int processes)` → `"Microsoft Outlook (olk, 4 processes)"`
- Produces: `EfficiencyRecord { List<EfficiencyEntry> Entries }`, `EfficiencyEntry { Pid, StartUtcTicks, OriginalPriority, RootPid, RootStartUtcTicks, Name, Process, Root }`

- [ ] **Step 1: Write the failing tests**

Create `tests/Nightcrew.Tests/EfficiencyModeTests.cs`:

```csharp
using System.Diagnostics;
using System.Runtime.InteropServices;
using Nightcrew.Core;
using Nightcrew.Platform;
using Xunit;

namespace Nightcrew.Tests;

/// <summary>Runs against real child processes that each test starts and ends itself.</summary>
public sealed class EfficiencyModeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nightcrew-tests-" + Guid.NewGuid().ToString("N"));
    private readonly List<Process> _children = [];
    private readonly ListLog _log = new();

    private string RecordPath => Path.Combine(_dir, "efficiency.json");

    public void Dispose()
    {
        foreach (var child in _children)
        {
            try { if (!child.HasExited) child.Kill(); } catch (InvalidOperationException) { }
            child.Dispose();
        }
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private sealed class ListLog : ILog
    {
        public List<string> Infos { get; } = [];
        public List<string> Warnings { get; } = [];

        public void Info(string message) => Infos.Add(message);

        public void Warn(string message) => Warnings.Add(message);
    }

    private EfficiencyMode Create() => new(RecordPath, _log);

    /// <summary>A child that idles for about 30 seconds, and the start time that identifies it.</summary>
    private (Process Process, ProcessRef Ref) StartChild()
    {
        var child = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 30 127.0.0.1 >nul") { UseShellExecute = false, CreateNoWindow = true })!;
        _children.Add(child);
        Assert.True(new ToolhelpProcessTable().TryGetTimes(child.Id, out var start, out _));
        return (child, new ProcessRef(child.Id, start));
    }

    private static AppFamily Family(params ProcessRef[] processes) => new("Test app", "cmd", processes);

    private static ProcessPriorityClass PriorityOf(Process process)
    {
        process.Refresh();
        return process.PriorityClass;
    }

    // Read with the Windows API directly, so the test doesn't check EfficiencyMode against itself.
    [StructLayout(LayoutKind.Sequential)]
    private struct ThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessInformation(IntPtr process, int infoClass, ref ThrottlingState info, int size);

    private static bool ThrottledOf(Process process)
    {
        var state = new ThrottlingState { Version = 1 };
        Assert.True(GetProcessInformation(process.Handle, 4, ref state, Marshal.SizeOf<ThrottlingState>()));
        return (state.ControlMask & 1) != 0 && (state.StateMask & 1) != 0;
    }

    [Fact]
    public void Apply_puts_a_process_into_efficiency_mode_and_RestoreAll_puts_it_back()
    {
        var (child, process) = StartChild();
        var mode = Create();

        Assert.Equal(ApplyResult.Applied, mode.Apply(Family(process), mode.Generation));

        Assert.Equal(ProcessPriorityClass.Idle, PriorityOf(child));
        Assert.True(ThrottledOf(child));
        Assert.Equal([process], mode.Applied);

        Assert.Equal(1, mode.RestoreAll("test"));

        Assert.Equal(ProcessPriorityClass.Normal, PriorityOf(child));
        Assert.False(ThrottledOf(child));
        Assert.Empty(mode.Applied);
    }

    [Fact]
    public void The_record_is_written_before_the_change_and_deleted_after_RestoreAll()
    {
        var (_, process) = StartChild();
        var mode = Create();

        mode.Apply(Family(process), mode.Generation);

        var record = Json.Deserialize<EfficiencyRecord>(File.ReadAllText(RecordPath));
        var entry = Assert.Single(record.Entries);
        Assert.Equal(process, entry.Process);
        Assert.Equal(process, entry.Root);
        Assert.Equal(0x20u, entry.OriginalPriority); // NORMAL_PRIORITY_CLASS

        mode.RestoreAll("test");

        Assert.False(File.Exists(RecordPath));
    }

    [Fact]
    public void A_new_instance_puts_back_what_an_earlier_run_left_in_the_record()
    {
        var (child, process) = StartChild();
        Create().Apply(Family(process), 0); // as if Nightcrew then crashed

        var restarted = Create();
        Assert.Equal(1, restarted.RestoreAll("startup"));

        Assert.Equal(ProcessPriorityClass.Normal, PriorityOf(child));
        Assert.False(File.Exists(RecordPath));
    }

    [Fact]
    public void A_process_already_at_a_low_priority_is_left_alone_and_not_recorded()
    {
        var (child, process) = StartChild();
        child.PriorityClass = ProcessPriorityClass.BelowNormal;
        var mode = Create();

        Assert.Equal(ApplyResult.Unchanged, mode.Apply(Family(process), mode.Generation));

        Assert.Equal(ProcessPriorityClass.BelowNormal, PriorityOf(child));
        Assert.Empty(mode.Applied);
        Assert.False(File.Exists(RecordPath));
    }

    [Fact]
    public void A_priority_changed_after_efficiency_mode_was_set_is_kept()
    {
        var (child, process) = StartChild();
        var mode = Create();
        mode.Apply(Family(process), mode.Generation);

        child.PriorityClass = ProcessPriorityClass.AboveNormal; // the app, or the user, changed it meanwhile
        mode.RestoreAll("test");

        Assert.Equal(ProcessPriorityClass.AboveNormal, PriorityOf(child));
    }

    [Fact]
    public void A_reused_pid_is_never_touched()
    {
        var (child, process) = StartChild();
        var mode = Create();
        var stale = process with { StartUtc = process.StartUtc.AddSeconds(-5) };

        Assert.Equal(ApplyResult.Unchanged, mode.Apply(Family(stale), mode.Generation));

        Assert.Equal(ProcessPriorityClass.Normal, PriorityOf(child));
    }

    [Fact]
    public void A_process_windows_wont_let_it_change_is_denied_and_the_family_is_put_back()
    {
        var (child, process) = StartChild();
        using var system = Process.GetProcessesByName("wininit").First();
        var protectedRef = new ProcessRef(system.Id, DateTime.MinValue);
        var mode = Create();

        Assert.Equal(ApplyResult.Denied, mode.Apply(Family(process, protectedRef), mode.Generation));

        Assert.Equal(ProcessPriorityClass.Normal, PriorityOf(child));
        Assert.Empty(mode.Applied);
    }

    [Fact]
    public void A_pass_that_began_before_RestoreAll_changes_nothing()
    {
        var (child, process) = StartChild();
        var mode = Create();
        var generation = mode.Generation;

        mode.RestoreAll("lid opened");

        Assert.Equal(ApplyResult.Stale, mode.Apply(Family(process), generation));
        Assert.Equal(ProcessPriorityClass.Normal, PriorityOf(child));
    }

    [Fact]
    public void Restore_puts_back_one_family_and_keeps_the_others()
    {
        var (first, a) = StartChild();
        var (second, b) = StartChild();
        var mode = Create();
        mode.Apply(Family(a), mode.Generation);
        mode.Apply(Family(b), mode.Generation);

        mode.Restore(a, "needed");

        Assert.Equal(ProcessPriorityClass.Normal, PriorityOf(first));
        Assert.Equal(ProcessPriorityClass.Idle, PriorityOf(second));
        Assert.Equal([b], mode.Applied);
        Assert.Single(Json.Deserialize<EfficiencyRecord>(File.ReadAllText(RecordPath)).Entries);
    }

    [Fact]
    public void A_process_that_has_exited_is_skipped()
    {
        var (child, process) = StartChild();
        child.Kill();
        child.WaitForExit(); // the Process object still holds a handle, so Windows keeps the exited process around
        var mode = Create();

        Assert.Equal(ApplyResult.Unchanged, mode.Apply(Family(process), mode.Generation));
        Assert.False(File.Exists(RecordPath));
    }

    [Fact]
    public void A_corrupt_record_is_moved_aside_and_nothing_breaks()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(RecordPath, "{ not json");

        var mode = Create();

        Assert.Equal(0, mode.RestoreAll("startup"));
        Assert.True(File.Exists(RecordPath + ".corrupt"));
        Assert.False(File.Exists(RecordPath));
        Assert.Contains(_log.Warnings, line => line.Contains("corrupt"));
    }

    [Fact]
    public void Each_app_is_logged_once_when_it_goes_in_and_together_when_they_come_out()
    {
        var (_, process) = StartChild();
        var mode = Create();
        var family = new AppFamily("Windows Command Processor", "cmd", [process]);

        mode.Apply(family, mode.Generation);
        mode.Apply(family, mode.Generation);
        mode.RestoreAll("lid opened");

        Assert.Equal(["Efficiency mode on: Windows Command Processor (cmd, 1 process)", "Efficiency mode off for 1 app: lid opened"], _log.Infos);
    }
}
```

- [ ] **Step 2: Add the new types, with stubs, so the tests compile**

Create `src/Nightcrew/Core/Efficiency.cs`:

```csharp
namespace Nightcrew.Core;

/// <summary>One process, identified by its pid and its start time, so a reused pid is never mistaken for it.</summary>
public readonly record struct ProcessRef(int Pid, DateTime StartUtc);

/// <summary>
/// An app: the processes of one family, root first, the name the panel shows, and the root's process name (what
/// "Leave alone" saves). The root identifies the family.
/// </summary>
public sealed record AppFamily(string DisplayName, string RootName, IReadOnlyList<ProcessRef> Processes)
{
    public ProcessRef Root => Processes[0];
}

public enum ApplyResult
{
    /// <summary>At least one process is now in Efficiency mode.</summary>
    Applied,

    /// <summary>Every process was already in Efficiency mode, had made its own choice, or had exited.</summary>
    Unchanged,

    /// <summary>Windows wouldn't let Nightcrew change a process, so the whole family was left (or put back) as it was.</summary>
    Denied,

    /// <summary>Everything was put back since this pass began, so nothing was changed.</summary>
    Stale,
}

/// <summary>Puts apps into Windows' Efficiency mode and back. <see cref="RestoreAll"/> may be called from any thread.</summary>
public interface IEfficiencyMode
{
    /// <summary>Increases with every <see cref="RestoreAll"/>; a pass that started before one changes nothing.</summary>
    int Generation { get; }

    /// <summary>The families that have at least one process in Efficiency mode, by root.</summary>
    IReadOnlyCollection<ProcessRef> Applied { get; }

    ApplyResult Apply(AppFamily family, int generation);

    /// <summary>Puts back the family with this root.</summary>
    void Restore(ProcessRef root, string reason);

    /// <summary>Puts back everything it changed, including what an earlier run left in the record; returns the number of apps.</summary>
    int RestoreAll(string reason);
}
```

Create `src/Nightcrew/Core/EfficiencyRecord.cs`:

```csharp
using System.Runtime.Serialization;

namespace Nightcrew.Core;

/// <summary>One process Nightcrew put into Efficiency mode, and the priority to put back.</summary>
[DataContract]
public sealed class EfficiencyEntry
{
    [DataMember(IsRequired = true)] public int Pid { get; set; }
    [DataMember(IsRequired = true)] public long StartUtcTicks { get; set; }
    [DataMember(IsRequired = true)] public uint OriginalPriority { get; set; }
    [DataMember(IsRequired = true)] public int RootPid { get; set; }
    [DataMember(IsRequired = true)] public long RootStartUtcTicks { get; set; }
    [DataMember] public string Name { get; set; } = "";

    public ProcessRef Process => new(Pid, new DateTime(StartUtcTicks, DateTimeKind.Utc));

    public ProcessRef Root => new(RootPid, new DateTime(RootStartUtcTicks, DateTimeKind.Utc));
}

/// <summary>Everything in Efficiency mode, saved before each change so a crash can be undone.</summary>
[DataContract]
public sealed class EfficiencyRecord
{
    [DataMember(IsRequired = true)] public List<EfficiencyEntry> Entries { get; set; } = [];
}
```

Stub for src/Nightcrew/Platform/EfficiencyMode.cs:

```csharp
using Nightcrew.Core;

namespace Nightcrew.Platform;

public sealed class EfficiencyMode(string path, ILog log) : IEfficiencyMode
{
    public int Generation => throw new NotImplementedException($"{path}{log}");

    public IReadOnlyCollection<ProcessRef> Applied => throw new NotImplementedException();

    public ApplyResult Apply(AppFamily family, int generation) => throw new NotImplementedException();

    public void Restore(ProcessRef root, string reason) => throw new NotImplementedException();

    public int RestoreAll(string reason) => throw new NotImplementedException();
}
```

- [ ] **Step 3: Run the tests and watch them fail**

```powershell
dotnet test -c Release --no-build -- --filter-class Nightcrew.Tests.EfficiencyModeTests
```

Expected: All 12 tests in `EfficiencyModeTests` fail with `NotImplementedException`.

- [ ] **Step 4: Implement**

Create `src/Nightcrew/Platform/EfficiencyMode.cs`:

```csharp
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using Nightcrew.Core;

namespace Nightcrew.Platform;

/// <summary>
/// Windows' Efficiency mode as Task Manager applies it: power throttling (EcoQoS) plus idle priority. Every change is
/// written to a record first, so a later run, or the watchdog, can put it back after a crash. A process that already
/// made its own choice (a low priority, or any throttling setting) is left alone, and on the way back a priority
/// someone changed meanwhile is kept.
/// </summary>
public sealed class EfficiencyMode : IEfficiencyMode
{
    private const uint PROCESS_SET_INFORMATION = 0x0200;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const int ERROR_ACCESS_DENIED = 5;
    private const int ProcessPowerThrottling = 4;
    private const uint ThrottlingVersion = 1;
    private const uint ExecutionSpeed = 0x1;
    private const uint IdlePriority = 0x40;
    private const uint BelowNormalPriority = 0x4000;

    [StructLayout(LayoutKind.Sequential)]
    private struct ThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern bool GetProcessTimes(IntPtr process, out long creationTime, out long exitTime, out long kernelTime, out long userTime);

    [DllImport("kernel32.dll")]
    private static extern uint GetPriorityClass(IntPtr process);

    [DllImport("kernel32.dll")]
    private static extern bool SetPriorityClass(IntPtr process, uint priorityClass);

    [DllImport("kernel32.dll")]
    private static extern bool GetProcessInformation(IntPtr process, int infoClass, ref ThrottlingState info, int size);

    [DllImport("kernel32.dll")]
    private static extern bool SetProcessInformation(IntPtr process, int infoClass, ref ThrottlingState info, int size);

    private static readonly int StateSize = Marshal.SizeOf<ThrottlingState>();
    private static readonly int OwnPid = System.Diagnostics.Process.GetCurrentProcess().Id;

    private readonly object _lock = new();
    private readonly string _path;
    private readonly ILog _log;
    private readonly List<EfficiencyEntry> _entries;
    private int _generation;

    /// <summary>Loads what an earlier run left in the record, so <see cref="RestoreAll"/> puts that back too.</summary>
    public EfficiencyMode(string path, ILog log)
    {
        _path = path;
        _log = log;
        _entries = Load();
    }

    public int Generation
    {
        get { lock (_lock) return _generation; }
    }

    public IReadOnlyCollection<ProcessRef> Applied
    {
        get { lock (_lock) return _entries.Select(entry => entry.Root).Distinct().ToList(); }
    }

    public ApplyResult Apply(AppFamily family, int generation)
    {
        lock (_lock)
        {
            if (generation != _generation) return ApplyResult.Stale;
            var root = family.Root;
            var known = new HashSet<ProcessRef>(_entries.Select(entry => entry.Process));
            var pending = new List<(EfficiencyEntry Entry, IntPtr Handle)>();
            try
            {
                foreach (var process in family.Processes)
                {
                    if (process.Pid == OwnPid || known.Contains(process)) continue;
                    var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_SET_INFORMATION, false, process.Pid);
                    if (handle == IntPtr.Zero)
                    {
                        if (Marshal.GetLastWin32Error() != ERROR_ACCESS_DENIED) continue; // it has exited
                        RestoreFamily(root);
                        return ApplyResult.Denied;
                    }
                    if (!StartedAt(handle, process.StartUtc) || MadeItsOwnChoice(handle, out var priority))
                    {
                        CloseHandle(handle);
                        continue;
                    }
                    pending.Add((new EfficiencyEntry
                    {
                        Pid = process.Pid,
                        StartUtcTicks = process.StartUtc.Ticks,
                        OriginalPriority = priority,
                        RootPid = root.Pid,
                        RootStartUtcTicks = root.StartUtc.Ticks,
                        Name = family.DisplayName,
                    }, handle));
                }
                var wasOn = _entries.Any(entry => entry.Root == root);
                if (pending.Count == 0) return wasOn ? ApplyResult.Applied : ApplyResult.Unchanged;

                _entries.AddRange(pending.Select(item => item.Entry));
                if (!Save())
                {
                    _entries.RemoveAll(entry => pending.Any(item => item.Entry == entry));
                    return ApplyResult.Unchanged;
                }
                foreach (var (_, handle) in pending)
                {
                    var state = new ThrottlingState { Version = ThrottlingVersion, ControlMask = ExecutionSpeed, StateMask = ExecutionSpeed };
                    SetProcessInformation(handle, ProcessPowerThrottling, ref state, StateSize);
                    if (SetPriorityClass(handle, IdlePriority)) continue;
                    RestoreFamily(root);
                    return ApplyResult.Denied;
                }
                if (!wasOn) _log.Info($"Efficiency mode on: {Describe(family, pending.Count)}");
                return ApplyResult.Applied;
            }
            finally
            {
                foreach (var (_, handle) in pending) CloseHandle(handle);
            }
        }
    }

    public void Restore(ProcessRef root, string reason)
    {
        lock (_lock)
        {
            var name = _entries.FirstOrDefault(entry => entry.Root == root)?.Name;
            if (name is null) return;
            RestoreFamily(root);
            _log.Info($"Efficiency mode off for {name}: {reason}");
        }
    }

    public int RestoreAll(string reason)
    {
        lock (_lock)
        {
            _generation++;
            var apps = _entries.Select(entry => entry.Root).Distinct().Count();
            foreach (var entry in _entries) Put(entry);
            _entries.Clear();
            Save();
            if (apps > 0) _log.Info($"Efficiency mode off for {apps} {(apps == 1 ? "app" : "apps")}: {reason}");
            return apps;
        }
    }

    /// <summary>"Microsoft Outlook (olk, 4 processes)", as the log shows an app.</summary>
    internal static string Describe(AppFamily family, int processes) =>
        $"{family.DisplayName} ({family.RootName}, {processes} {(processes == 1 ? "process" : "processes")})";

    /// <summary>Under the lock: puts back every process of one family and saves the record.</summary>
    private void RestoreFamily(ProcessRef root)
    {
        var family = _entries.Where(entry => entry.Root == root).ToList();
        if (family.Count == 0) return;
        foreach (var entry in family) Put(entry);
        _entries.RemoveAll(family.Contains);
        Save();
    }

    /// <summary>Clears throttling that's still on, and puts the priority back if it's still idle.</summary>
    private static void Put(EfficiencyEntry entry)
    {
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_SET_INFORMATION, false, entry.Pid);
        if (handle == IntPtr.Zero) return;
        try
        {
            if (!StartedAt(handle, entry.Process.StartUtc)) return;
            var state = new ThrottlingState { Version = ThrottlingVersion };
            var readable = GetProcessInformation(handle, ProcessPowerThrottling, ref state, StateSize);
            if (!readable || ((state.ControlMask & ExecutionSpeed) != 0 && (state.StateMask & ExecutionSpeed) != 0))
            {
                var systemDecides = new ThrottlingState { Version = ThrottlingVersion };
                SetProcessInformation(handle, ProcessPowerThrottling, ref systemDecides, StateSize);
            }
            if (GetPriorityClass(handle) == IdlePriority) SetPriorityClass(handle, entry.OriginalPriority);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>False when the process has exited or the pid now belongs to a different process.</summary>
    private static bool StartedAt(IntPtr handle, DateTime startUtc) =>
        GetProcessTimes(handle, out var created, out var exited, out _, out _)
        && exited == 0
        && DateTime.FromFileTimeUtc(created) == startUtc;

    /// <summary>A low priority, any throttling setting of its own, or a priority we can't read.</summary>
    private static bool MadeItsOwnChoice(IntPtr handle, out uint priority)
    {
        priority = GetPriorityClass(handle);
        if (priority is 0 or IdlePriority or BelowNormalPriority) return true;
        var state = new ThrottlingState { Version = ThrottlingVersion };
        return GetProcessInformation(handle, ProcessPowerThrottling, ref state, StateSize) && (state.ControlMask & ExecutionSpeed) != 0;
    }

    /// <summary>Writes the record, or deletes it when nothing is in Efficiency mode. False if it couldn't be written.</summary>
    private bool Save()
    {
        try
        {
            if (_entries.Count == 0)
            {
                File.Delete(_path);
                return true;
            }
            AtomicFile.WriteAllText(_path, Json.Serialize(new EfficiencyRecord { Entries = _entries }));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn($"Couldn't write the Efficiency mode record: {ex.Message}");
            return false;
        }
    }

    /// <summary>The entries an earlier run left; a corrupt record is moved aside to .corrupt.</summary>
    private List<EfficiencyEntry> Load()
    {
        if (!File.Exists(_path)) return [];
        try
        {
            return Json.Deserialize<EfficiencyRecord?>(File.ReadAllText(_path))?.Entries ?? [];
        }
        catch (SerializationException ex)
        {
            var aside = _path + ".corrupt";
            File.Delete(aside);
            File.Move(_path, aside);
            _log.Warn($"Efficiency mode record was corrupt, moved to {aside}: {ex.Message}");
            return [];
        }
    }
}
```

- [ ] **Step 5: Run the tests and watch them pass, then the whole suite**

```powershell
dotnet build -c Release
dotnet test -c Release --no-build -- --filter-class Nightcrew.Tests.EfficiencyModeTests
dotnet test -c Release --no-build
powershell -NoProfile -File build/check-single-file.ps1
```

Expected: all pass, 0 warnings, the single-file gate passes.

- [ ] **Step 6: Commit**

```powershell
git add src/Nightcrew/Core/Efficiency.cs src/Nightcrew/Core/EfficiencyRecord.cs src/Nightcrew/Platform/EfficiencyMode.cs tests/Nightcrew.Tests/EfficiencyModeTests.cs
git commit -m "feat: Efficiency mode for an app's processes, with a record so a crash can be undone" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

### Task 2: When it applies and which apps

Spec sections 2, 3 and 4: the gate, the built-in lists and the planner, all pure.

**Files:**
- Create: `src/Nightcrew/Core/EfficiencyGate.cs`
- Create: `src/Nightcrew/Core/EfficiencyPlanner.cs`
- Create: `src/Nightcrew/Core/LeaveAloneRules.cs`
- Modify: `src/Nightcrew/Core/Settings.cs`
- Test (create): `tests/Nightcrew.Tests/EfficiencyGateTests.cs`
- Test (create): `tests/Nightcrew.Tests/EfficiencyPlannerTests.cs`

**Interfaces:**
- Consumes: `AppFamily`, `ProcessRef` (task 1); `Mode`, `LidState`, `LidAction` (existing)
- Produces: `Settings.EfficiencyWhileAway` (default true), `Settings.LeaveAlone` (default empty), `Settings.ConnectorHostNames` (default `chrome-native-host`, `extension-host`)
- Produces: `EfficiencyGate.Wanted(Mode mode, LidState lid, TimeSpan idleTime, LidAction ownerLid, Settings settings)`
- Produces: `ProcessInfo(int Pid, int ParentPid, string Name, DateTime StartUtc, int Session)`
- Produces: `enum TcpState { Other, Listen, Established }`, `TcpRow(int OwnerPid, int LocalPort, int RemotePort, bool RemoteIsLoopback, TcpState State)`
- Produces: `EfficiencySnapshot(int Session, IReadOnlyList<ProcessInfo> Processes, IReadOnlyCollection<int> WindowOwners, IReadOnlyCollection<int> HolderPids, IReadOnlyList<TcpRow> Connections)`
- Produces: `KeptApp(string RootName, string Rule)`, `EfficiencyPlan(IReadOnlyList<AppFamily> Apply, IReadOnlyList<KeptApp> Kept)`
- Produces: `EfficiencyPlanner(Settings settings, Func<ProcessInfo, string>? displayName = null).Plan(EfficiencySnapshot)`; rules are `system`, `game`, `agent`, `holder`, `connector`, `connection`, `category`, `yours`
- Produces: `LeaveAloneRules.Categories`, `.Games`, `.System`, `.StopAbove`, `.SharedRuntimes`, `.Matches(list, name)` (an entry ending in `*` is a prefix)

- [ ] **Step 1: Write the failing tests**

Create `tests/Nightcrew.Tests/EfficiencyGateTests.cs`:

```csharp
using Nightcrew.Core;
using Xunit;

namespace Nightcrew.Tests;

public sealed class EfficiencyGateTests
{
    private static readonly TimeSpan Away = TimeSpan.FromMinutes(5);

    private static bool Wanted(Mode mode = Mode.Armed, LidState lid = LidState.Closed, TimeSpan? idle = null,
        LidAction owner = LidAction.Sleep, bool enabled = true) =>
        EfficiencyGate.Wanted(mode, lid, idle ?? Away, owner, new Settings { EfficiencyWhileAway = enabled });

    [Fact]
    public void Wanted_when_an_agent_works_with_the_lid_closed_and_nobody_there() => Assert.True(Wanted());

    [Theory]
    [InlineData(LidAction.Sleep)]
    [InlineData(LidAction.Hibernate)]
    [InlineData(LidAction.ShutDown)]
    public void Wanted_for_every_lid_action_that_would_stop_the_laptop(LidAction owner) => Assert.True(Wanted(owner: owner));

    [Fact]
    public void Not_wanted_when_the_lid_action_is_do_nothing() => Assert.False(Wanted(owner: LidAction.DoNothing));

    [Theory]
    [InlineData(Mode.Idle)]
    [InlineData(Mode.Finishing)]
    [InlineData(Mode.Paused)]
    public void Not_wanted_unless_armed(Mode mode) => Assert.False(Wanted(mode: mode));

    [Theory]
    [InlineData(LidState.Open)]
    [InlineData(LidState.Unknown)]
    public void Not_wanted_unless_the_lid_is_closed(LidState lid) => Assert.False(Wanted(lid: lid));

    [Fact]
    public void Not_wanted_until_five_minutes_without_input() =>
        Assert.False(Wanted(idle: Away - TimeSpan.FromSeconds(1)));

    [Fact]
    public void Not_wanted_when_switched_off() => Assert.False(Wanted(enabled: false));

    [Fact]
    public void Away_minutes_comes_from_the_settings()
    {
        var settings = new Settings { AwayMinutes = 10 };
        Assert.False(EfficiencyGate.Wanted(Mode.Armed, LidState.Closed, TimeSpan.FromMinutes(9), LidAction.Sleep, settings));
        Assert.True(EfficiencyGate.Wanted(Mode.Armed, LidState.Closed, TimeSpan.FromMinutes(10), LidAction.Sleep, settings));
    }
}
```

Create `tests/Nightcrew.Tests/EfficiencyPlannerTests.cs`:

```csharp
using Nightcrew.Core;
using Xunit;

namespace Nightcrew.Tests;

public sealed class EfficiencyPlannerTests
{
    private const int Session = 1;
    private static readonly DateTime T0 = new(2026, 10, 15, 8, 0, 0, DateTimeKind.Utc);

    private readonly List<ProcessInfo> _processes = [];
    private readonly HashSet<int> _windows = [];
    private readonly HashSet<int> _holders = [];
    private readonly List<TcpRow> _tcp = [];
    private readonly Settings _settings = new();

    public EfficiencyPlannerTests()
    {
        Add(4, 0, "system", 0, session: 0);
        Add(2, 1, "explorer", 1);
    }

    /// <summary>A process that started <paramref name="minute"/> minutes after T0.</summary>
    private ProcessInfo Add(int pid, int parent, string name, int minute, int session = Session)
    {
        var process = new ProcessInfo(pid, parent, name, T0.AddMinutes(minute), session);
        _processes.Add(process);
        return process;
    }

    private void Window(int pid) => _windows.Add(pid);

    private EfficiencyPlan Plan(Func<ProcessInfo, string>? displayName = null) =>
        new EfficiencyPlanner(_settings, displayName).Plan(new EfficiencySnapshot(Session, _processes, _windows, _holders, _tcp));

    private static int[] Pids(AppFamily family) => family.Processes.Select(p => p.Pid).ToArray();

    private static string RuleFor(EfficiencyPlan plan, string rootName) => Assert.Single(plan.Kept, k => k.RootName == rootName).Rule;

    [Fact]
    public void A_window_owner_and_its_helpers_are_one_app()
    {
        Add(10, 2, "chrome", 5);
        Add(11, 10, "chrome", 6);
        Add(12, 10, "chrome", 6);
        Window(10);

        var family = Assert.Single(Plan().Apply);

        Assert.Equal([10, 11, 12], Pids(family));
        Assert.Equal("chrome", family.RootName);
        Assert.Equal(new ProcessRef(10, T0.AddMinutes(5)), family.Root);
    }

    [Fact]
    public void The_root_is_the_app_that_started_the_window_owner_and_comes_first()
    {
        Add(20, 2, "outlookhost", 5);
        Add(21, 20, "olk", 6);
        Add(22, 21, "msedgewebview2", 7);
        Window(21);

        Assert.Equal([20, 21, 22], Pids(Assert.Single(Plan().Apply)));
    }

    [Fact]
    public void The_root_stops_below_system_processes()
    {
        Add(5, 4, "svchost", 0);
        Add(30, 5, "whatsapp.root", 5, session: Session);
        Window(30);

        var family = Assert.Single(Plan().Apply);

        Assert.Equal("whatsapp.root", family.RootName);
        Assert.Equal([30], Pids(family));
    }

    [Fact]
    public void A_parent_that_started_after_its_child_is_a_reused_pid_and_is_not_followed()
    {
        Add(41, 2, "spotify", 9); // took over the pid of the real parent, which has exited
        Add(40, 41, "notepad", 5);
        Window(40);

        var family = Assert.Single(Plan().Apply);

        Assert.Equal("notepad", family.RootName);
        Assert.Equal([40], Pids(family));
    }

    [Fact]
    public void Orphans_with_the_roots_name_join_its_app()
    {
        Add(10, 2, "chrome", 5);
        Add(13, 999, "chrome", 6); // its parent has exited
        Add(14, 13, "chrome", 7);
        Window(10);

        Assert.Equal([10, 13, 14], Pids(Assert.Single(Plan().Apply)));
    }

    [Fact]
    public void Orphans_of_shared_runtimes_never_join_an_app()
    {
        Add(50, 2, "node", 5);
        Add(51, 999, "node", 6);
        Window(50);

        Assert.Equal([50], Pids(Assert.Single(Plan().Apply)));
    }

    [Fact]
    public void A_window_in_another_session_is_ignored()
    {
        Add(60, 2, "notepad", 5, session: 2);
        Window(60);

        var plan = Plan();

        Assert.Empty(plan.Apply);
        Assert.Empty(plan.Kept);
    }

    [Fact]
    public void An_app_running_an_agent_is_left_alone()
    {
        Add(70, 2, "chatgpt", 5);
        Add(71, 70, "codex", 6);
        Window(70);

        var plan = Plan();

        Assert.Empty(plan.Apply);
        Assert.Equal("agent", RuleFor(plan, "chatgpt"));
    }

    [Fact]
    public void An_app_with_a_process_keeping_the_laptop_awake_is_left_alone()
    {
        Add(80, 2, "blender", 5);
        Add(81, 80, "blender", 6);
        Window(80);
        _holders.Add(81);

        Assert.Equal("holder", RuleFor(Plan(), "blender"));
    }

    [Fact]
    public void A_browser_with_a_connector_running_is_left_alone()
    {
        Add(90, 2, "brave", 5);
        Add(91, 90, "cmd", 6);
        Add(92, 91, "chrome-native-host", 6);
        Window(90);

        Assert.Equal("connector", RuleFor(Plan(), "brave"));
    }

    [Fact]
    public void An_app_an_agent_is_connected_to_over_loopback_is_left_alone()
    {
        Add(100, 2, "claude", 5);
        Add(101, 100, "node", 6);
        Add(110, 2, "postman", 5);
        Window(110);
        _tcp.Add(new TcpRow(110, 5173, 0, false, TcpState.Listen));
        _tcp.Add(new TcpRow(101, 50000, 5173, true, TcpState.Established));

        Assert.Equal("connection", RuleFor(Plan(), "postman"));
    }

    [Fact]
    public void A_connection_that_is_not_loopback_or_not_from_an_agent_keeps_nothing()
    {
        Add(100, 2, "claude", 5);
        Add(120, 2, "spotify", 5);
        Add(121, 2, "discord", 5);
        Add(130, 2, "postman", 5);
        Window(130);
        _tcp.Add(new TcpRow(130, 5173, 0, false, TcpState.Listen));
        _tcp.Add(new TcpRow(100, 50000, 5173, false, TcpState.Established)); // not loopback
        _tcp.Add(new TcpRow(121, 50001, 5173, true, TcpState.Established)); // not an agent

        Assert.Equal("postman", Assert.Single(Plan().Apply).RootName);
    }

    [Theory]
    [InlineData("code", "category")]
    [InlineData("windowsterminal", "category")]
    [InlineData("ollama app", "category")]
    [InlineData("com.docker.backend", "category")]
    [InlineData("steam", "game")]
    [InlineData("explorer", "system")]
    [InlineData("nightcrew", "system")]
    public void Built_in_lists_leave_the_app_alone(string name, string rule)
    {
        Add(140, 2, name, 5);
        Window(140);

        Assert.Equal(rule, RuleFor(Plan(), name));
    }

    [Fact]
    public void A_game_started_by_a_launcher_is_left_alone_with_it()
    {
        Add(150, 2, "steam", 5);
        Add(151, 150, "eldenring", 6);
        Window(151);

        Assert.Equal("game", RuleFor(Plan(), "steam"));
    }

    [Fact]
    public void A_console_window_counts_as_a_terminal_by_its_host()
    {
        Add(160, 2, "cmd", 5);
        Add(161, 160, "conhost", 5);
        Window(161);

        Assert.Equal("category", RuleFor(Plan(), "cmd"));
    }

    [Fact]
    public void A_terminal_helper_inside_an_app_without_its_own_window_does_not_keep_the_app()
    {
        Add(170, 2, "slack", 5);
        Add(171, 170, "cmd", 6);
        Add(172, 171, "conhost", 6);
        Window(170);

        Assert.Equal([170, 171, 172], Pids(Assert.Single(Plan().Apply)));
    }

    [Fact]
    public void Your_list_leaves_the_app_alone_whatever_the_case()
    {
        _settings.LeaveAlone = ["OLK"];
        Add(180, 2, "olk", 5);
        Window(180);

        Assert.Equal("yours", RuleFor(Plan(), "olk"));
    }

    [Fact]
    public void One_kept_member_keeps_the_whole_app()
    {
        Add(10, 2, "chrome", 5);
        Add(11, 10, "chrome", 6);
        Window(10);
        _holders.Add(11);

        var plan = Plan();

        Assert.Empty(plan.Apply);
        Assert.Equal("holder", RuleFor(plan, "chrome"));
    }

    [Fact]
    public void Two_windows_of_one_app_make_one_family()
    {
        Add(10, 2, "chrome", 5);
        Add(11, 10, "chrome", 6);
        Window(10);
        Window(11);

        Assert.Single(Plan().Apply);
    }

    [Fact]
    public void The_display_name_comes_from_the_root()
    {
        Add(20, 2, "olk", 5);
        Window(20);

        var family = Assert.Single(Plan(root => root.Name == "olk" ? "Microsoft Outlook" : "?").Apply);

        Assert.Equal("Microsoft Outlook", family.DisplayName);
        Assert.Equal("olk", family.RootName);
    }
}
```

- [ ] **Step 2: Add the new types, with stubs, so the tests compile**

Modify `src/Nightcrew/Core/Settings.cs`:

```diff
diff --git a/src/Nightcrew/Core/Settings.cs b/src/Nightcrew/Core/Settings.cs
index b0dc29e..df05776 100644
--- a/src/Nightcrew/Core/Settings.cs
+++ b/src/Nightcrew/Core/Settings.cs
@@ -22,8 +22,11 @@ public sealed class Settings
     [DataMember] public double ToolCpuPercent { get; set; }
     [DataMember] public double GpuPercent { get; set; }
     [DataMember] public int SampleSeconds { get; set; }
     [DataMember] public string Mark { get; set; } = MarkKinds.LanternName;
+    [DataMember] public bool EfficiencyWhileAway { get; set; }
+    [DataMember] public string[] LeaveAlone { get; set; } = [];
+    [DataMember] public string[] ConnectorHostNames { get; set; } = [];
 
     [OnDeserializing]
     private void OnDeserializing(StreamingContext context) => ApplyDefaults();
 
@@ -44,6 +47,9 @@ public sealed class Settings
         ToolCpuPercent = 10;
         GpuPercent = 30;
         SampleSeconds = 10;
         Mark = MarkKinds.LanternName;
+        EfficiencyWhileAway = true;
+        LeaveAlone = [];
+        ConnectorHostNames = ["chrome-native-host", "extension-host"];
     }
 }
```

Stub for src/Nightcrew/Core/EfficiencyGate.cs (stub) and the types at the top of src/Nightcrew/Core/EfficiencyPlanner.cs:

```csharp
// EfficiencyGate.cs
namespace Nightcrew.Core;

public static class EfficiencyGate
{
    public static bool Wanted(Mode mode, LidState lid, TimeSpan idleTime, LidAction ownerLid, Settings settings) =>
        throw new NotImplementedException();
}

// EfficiencyPlanner.cs: the records exactly as in step 4 (ProcessInfo, TcpState, TcpRow, EfficiencySnapshot,
// KeptApp, EfficiencyPlan), and this class body:
public sealed class EfficiencyPlanner(Settings settings, Func<ProcessInfo, string>? displayName = null)
{
    public EfficiencyPlan Plan(EfficiencySnapshot snapshot) => throw new NotImplementedException($"{settings}{displayName}");
}
```

- [ ] **Step 3: Run the tests and watch them fail**

```powershell
dotnet test -c Release --no-build -- --filter-class Nightcrew.Tests.EfficiencyGateTests
dotnet test -c Release --no-build -- --filter-class Nightcrew.Tests.EfficiencyPlannerTests
```

Expected: Every test in `EfficiencyGateTests` and `EfficiencyPlannerTests` fails with `NotImplementedException`.

- [ ] **Step 4: Implement**

Create `src/Nightcrew/Core/EfficiencyGate.cs`:

```csharp
namespace Nightcrew.Core;

/// <summary>Whether apps should be in Efficiency mode right now: Windows would have slept, but an agent is working.</summary>
public static class EfficiencyGate
{
    /// <param name="mode">Only Armed counts: an agent is working and Nightcrew holds the laptop awake.</param>
    /// <param name="ownerLid">The owner's own lid action for the current power source, not the Do nothing Nightcrew writes.</param>
    public static bool Wanted(Mode mode, LidState lid, TimeSpan idleTime, LidAction ownerLid, Settings settings) =>
        settings.EfficiencyWhileAway
        && mode == Mode.Armed
        && lid == LidState.Closed
        && idleTime >= TimeSpan.FromMinutes(settings.AwayMinutes)
        && ownerLid != LidAction.DoNothing;
}
```

Create `src/Nightcrew/Core/EfficiencyPlanner.cs`:

```csharp
namespace Nightcrew.Core;

/// <summary>One process as the planner sees it. Name is lower-case, without ".exe".</summary>
public sealed record ProcessInfo(int Pid, int ParentPid, string Name, DateTime StartUtc, int Session);

public enum TcpState { Other, Listen, Established }

/// <summary>One row of Windows' TCP table. Ports are in host byte order.</summary>
public readonly record struct TcpRow(int OwnerPid, int LocalPort, int RemotePort, bool RemoteIsLoopback, TcpState State);

/// <summary>Everything the planner needs from one tick.</summary>
public sealed record EfficiencySnapshot(
    int Session,
    IReadOnlyList<ProcessInfo> Processes,
    IReadOnlyCollection<int> WindowOwners,
    IReadOnlyCollection<int> HolderPids,
    IReadOnlyList<TcpRow> Connections);

/// <summary>An app left alone, and the rule that kept it: agent, holder, connector, connection, category, game, system or yours.</summary>
public sealed record KeptApp(string RootName, string Rule);

public sealed record EfficiencyPlan(IReadOnlyList<AppFamily> Apply, IReadOnlyList<KeptApp> Kept);

/// <summary>
/// Turns a snapshot into the apps to put into Efficiency mode and the apps to leave alone. An app is the family of a
/// process that owns an Alt+Tab window: its topmost same-session ancestor below the shell, every descendant, and
/// orphans with the root's name. If any member is kept, the whole family is.
/// </summary>
public sealed class EfficiencyPlanner(Settings settings, Func<ProcessInfo, string>? displayName = null)
{
    public EfficiencyPlan Plan(EfficiencySnapshot snapshot)
    {
        var byPid = snapshot.Processes.GroupBy(p => p.Pid).ToDictionary(g => g.Key, g => g.First());
        var children = snapshot.Processes.ToLookup(p => p.ParentPid);
        var agentTree = Descendants(snapshot.Processes.Where(p => IsIn(settings.AgentProcessNames, p.Name)), children);
        var listeners = snapshot.Connections.Where(row => row.State == TcpState.Listen)
            .GroupBy(row => row.LocalPort).ToDictionary(g => g.Key, g => g.Select(row => row.OwnerPid).ToHashSet());
        var serving = snapshot.Connections
            .Where(row => row.State == TcpState.Established && row.RemoteIsLoopback && agentTree.Contains(row.OwnerPid))
            .SelectMany(row => listeners.TryGetValue(row.RemotePort, out var owners) ? owners : [])
            .ToHashSet();
        var holders = snapshot.HolderPids.ToHashSet();
        var owners = snapshot.WindowOwners.ToHashSet();

        var roots = snapshot.WindowOwners
            .Where(pid => byPid.TryGetValue(pid, out var p) && p.Session == snapshot.Session)
            .Select(pid => RootOf(byPid[pid], byPid))
            .Distinct()
            .OrderBy(root => root.Pid)
            .ToList();

        var claimed = new HashSet<int>();
        var apply = new List<AppFamily>();
        var kept = new List<KeptApp>();
        foreach (var root in roots)
        {
            var members = FamilyOf(root, snapshot, children, byPid, claimed);
            var rule = Rule(root, members, owners, holders, agentTree, serving);
            if (rule is not null)
            {
                kept.Add(new KeptApp(root.Name, rule));
                continue;
            }
            var processes = members.Select(p => new ProcessRef(p.Pid, p.StartUtc)).ToList();
            apply.Add(new AppFamily(displayName?.Invoke(root) ?? root.Name, root.Name, processes));
        }
        return new EfficiencyPlan(apply, kept);
    }

    /// <summary>The first rule that keeps this family, or null.</summary>
    private string? Rule(ProcessInfo root, IReadOnlyList<ProcessInfo> members, HashSet<int> owners, HashSet<int> holders,
        HashSet<int> agentTree, HashSet<int> serving)
    {
        // Names that speak for the app: its root, and every member that owns a window.
        var faces = members.Where(p => p.Pid == root.Pid || owners.Contains(p.Pid)).Select(p => p.Name).ToList();
        if (faces.Any(name => LeaveAloneRules.Matches(LeaveAloneRules.System, name))) return "system";
        if (faces.Any(name => LeaveAloneRules.Matches(LeaveAloneRules.Games, name))) return "game";
        if (members.Any(p => agentTree.Contains(p.Pid))) return "agent";
        if (members.Any(p => holders.Contains(p.Pid))) return "holder";
        if (members.Any(p => IsIn(settings.ConnectorHostNames, p.Name))) return "connector";
        if (members.Any(p => serving.Contains(p.Pid))) return "connection";
        if (faces.Any(name => LeaveAloneRules.Matches(LeaveAloneRules.Categories, name))) return "category";
        if (faces.Any(name => IsIn(settings.LeaveAlone, name))) return "yours";
        return null;
    }

    /// <summary>Climbs while the parent is real (started first), in the same session, and not the shell or a system process.</summary>
    private static ProcessInfo RootOf(ProcessInfo process, Dictionary<int, ProcessInfo> byPid)
    {
        var current = process;
        for (var depth = 0; depth < 64; depth++)
        {
            if (!byPid.TryGetValue(current.ParentPid, out var parent) || parent.Pid == current.Pid) break;
            if (parent.StartUtc > current.StartUtc || parent.Session != current.Session) break;
            if (LeaveAloneRules.Matches(LeaveAloneRules.StopAbove, parent.Name)) break;
            current = parent;
        }
        return current;
    }

    /// <summary>The root first, then its descendants and same-name orphans, each process in at most one family.</summary>
    private static List<ProcessInfo> FamilyOf(ProcessInfo root, EfficiencySnapshot snapshot, ILookup<int, ProcessInfo> children,
        Dictionary<int, ProcessInfo> byPid, HashSet<int> claimed)
    {
        var heads = new List<ProcessInfo> { root };
        if (!LeaveAloneRules.Matches(LeaveAloneRules.SharedRuntimes, root.Name))
        {
            heads.AddRange(snapshot.Processes.Where(p =>
                p.Pid != root.Pid && p.Session == root.Session && string.Equals(p.Name, root.Name, StringComparison.OrdinalIgnoreCase)
                && !HasRealParent(p, byPid)));
        }
        var members = new List<ProcessInfo>();
        foreach (var process in Descendants(heads, children).Select(pid => byPid[pid]))
            if (claimed.Add(process.Pid)) members.Add(process);
        var rootFirst = members.Where(p => p.Pid == root.Pid).Concat(members.Where(p => p.Pid != root.Pid).OrderBy(p => p.Pid));
        return rootFirst.ToList();
    }

    private static bool HasRealParent(ProcessInfo process, Dictionary<int, ProcessInfo> byPid) =>
        byPid.TryGetValue(process.ParentPid, out var parent) && parent.Pid != process.Pid && parent.StartUtc <= process.StartUtc;

    /// <summary>The given processes and every same-session descendant whose parent link is real (the parent started first).</summary>
    private static HashSet<int> Descendants(IEnumerable<ProcessInfo> heads, ILookup<int, ProcessInfo> children)
    {
        var queue = new Queue<ProcessInfo>(heads);
        var seen = new HashSet<int>(queue.Select(p => p.Pid));
        while (queue.Count > 0)
        {
            var parent = queue.Dequeue();
            foreach (var child in children[parent.Pid])
            {
                if (child.StartUtc < parent.StartUtc || child.Session != parent.Session || !seen.Add(child.Pid)) continue;
                queue.Enqueue(child);
            }
        }
        return seen;
    }

    private static bool IsIn(IEnumerable<string> names, string name) =>
        names.Any(entry => string.Equals(entry, name, StringComparison.OrdinalIgnoreCase));
}
```

Create `src/Nightcrew/Core/LeaveAloneRules.cs`:

```csharp
namespace Nightcrew.Core;

/// <summary>
/// The built-in lists of apps Efficiency mode never touches. Entries are lower-case process names without ".exe", as
/// <see cref="ProcessEntry"/> has them; an entry ending in * matches any name that starts with the rest.
/// </summary>
public static class LeaveAloneRules
{
    /// <summary>Terminals and editors, Docker/WSL/VMs, VPNs, password managers and SSH agents, local models, remote desktop.</summary>
    public static readonly IReadOnlyList<string> Categories =
    [
        "windowsterminal", "openconsole", "conhost", "code", "cursor", "devenv", "rider64", "idea64", "pycharm64",
        "webstorm64", "clion64", "goland64", "datagrip64", "rubymine64", "phpstorm64", "studio64", "wezterm-gui",
        "alacritty",
        "docker desktop", "com.docker.*", "wsl", "wslhost", "wslservice", "vmmem*", "virtualbox", "virtualboxvm",
        "vmware", "vmware-vmx",
        "nordvpn", "openvpn*", "wireguard", "tailscale*", "protonvpn*", "mullvad*",
        "1password", "bitwarden", "nordpass", "keepassxc", "dashlane*", "proton pass", "ssh-agent", "pageant",
        "ollama*", "lm studio",
        "mstsc", "anydesk", "teamviewer*", "parsec*", "rustdesk", "remoting_host",
    ];

    /// <summary>Game launchers and anti-cheat, and so everything they start.</summary>
    public static readonly IReadOnlyList<string> Games =
    [
        "steam", "steamwebhelper", "epicgameslauncher", "epicwebhelper", "riotclientservices", "riot client",
        "riotclientux*", "leagueclient*", "vgtray", "eadesktop", "ealauncher", "battle.net", "galaxyclient*", "upc",
        "ubisoftconnect", "easyanticheat*", "beservice", "faceit*",
    ];

    /// <summary>Windows itself, the shell, accessibility and input tools, system utilities, and Nightcrew.</summary>
    public static readonly IReadOnlyList<string> System =
    [
        "system", "registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "fontdrvhost", "dwm",
        "userinit", "explorer", "sihost", "shellexperiencehost", "shellhost", "startmenuexperiencehost", "searchhost",
        "searchapp", "textinputhost", "ctfmon", "chsime", "lockapp", "applicationframehost", "runtimebroker",
        "svchost", "taskhostw", "dllhost", "systemsettings", "taskmgr", "procexp*", "systeminformer", "powertoys*",
        "widgets", "widgetservice", "phoneexperiencehost", "crossdevice*", "securityhealthsystray", "narrator",
        "magnify", "nvda", "nightcrew",
    ];

    /// <summary>Where a family's root stops climbing: an app started by one of these is its own root.</summary>
    public static readonly IReadOnlyList<string> StopAbove =
    [
        "system", "smss", "csrss", "wininit", "winlogon", "services", "userinit", "explorer", "sihost", "svchost",
        "dllhost", "runtimebroker", "taskhostw",
    ];

    /// <summary>Runtimes many unrelated apps share, so an orphan is never grouped with a family by this name.</summary>
    public static readonly IReadOnlyList<string> SharedRuntimes =
    [
        "msedgewebview2", "node", "bun", "deno", "python", "pythonw", "java", "javaw", "cmd", "powershell", "pwsh",
    ];

    public static bool Matches(IReadOnlyList<string> list, string name)
    {
        foreach (var entry in list)
        {
            if (entry.EndsWith("*", StringComparison.Ordinal))
            {
                if (name.StartsWith(entry.Substring(0, entry.Length - 1), StringComparison.OrdinalIgnoreCase)) return true;
            }
            else if (string.Equals(entry, name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}
```

- [ ] **Step 5: Run the tests and watch them pass, then the whole suite**

```powershell
dotnet build -c Release
dotnet test -c Release --no-build -- --filter-class Nightcrew.Tests.EfficiencyGateTests
dotnet test -c Release --no-build -- --filter-class Nightcrew.Tests.EfficiencyPlannerTests
dotnet test -c Release --no-build
powershell -NoProfile -File build/check-single-file.ps1
```

Expected: all pass, 0 warnings, the single-file gate passes.

- [ ] **Step 6: Commit**

```powershell
git add src/Nightcrew/Core/EfficiencyGate.cs src/Nightcrew/Core/EfficiencyPlanner.cs src/Nightcrew/Core/LeaveAloneRules.cs src/Nightcrew/Core/Settings.cs tests/Nightcrew.Tests/EfficiencyGateTests.cs tests/Nightcrew.Tests/EfficiencyPlannerTests.cs
git commit -m "feat: when Efficiency mode applies, and which apps it leaves alone" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

### Task 3: Windows readers and the snapshot source

Spec sections 3.1, 3.3 rule 4 and 4: Alt+Tab window owners, the TCP tables, one process list per tick, and the snapshot with display names.

**Files:**
- Modify: `src/Nightcrew/Core/Efficiency.cs`
- Create: `src/Nightcrew/Platform/EfficiencySource.cs`
- Create: `src/Nightcrew/Platform/LoopbackConnections.cs`
- Create: `src/Nightcrew/Platform/TickProcessTable.cs`
- Create: `src/Nightcrew/Platform/WindowOwners.cs`
- Test (create): `tests/Nightcrew.Tests/EfficiencySourceTests.cs`
- Test (create): `tests/Nightcrew.Tests/WindowReaderTests.cs`

**Interfaces:**
- Consumes: `TcpRow`, `TcpState`, `ProcessInfo`, `EfficiencySnapshot` (task 2); `IProcessTable`, `ToolhelpProcessTable`, `IClock`, `FakeClock`, `Sta` (existing)
- Produces: `WindowOwners.List()` → `IReadOnlyCollection<int>`
- Produces: `LoopbackConnections.List()` → `IReadOnlyList<TcpRow>` (IPv4 and IPv6, ports in host order)
- Produces: `TickProcessTable(IProcessTable inner, IClock clock) : IProcessTable` (lists at most once a second)
- Produces: `IEfficiencySource { EfficiencySnapshot Take(IReadOnlyCollection<int> holderPids); string DisplayName(ProcessInfo root); }`
- Produces: `AwayApp(string DisplayName, string RootName)`
- Produces: `EfficiencySource(IProcessTable table) : IEfficiencySource`

- [ ] **Step 1: Write the failing tests**

Create `tests/Nightcrew.Tests/EfficiencySourceTests.cs`:

```csharp
using System.Diagnostics;
using Nightcrew.Core;
using Nightcrew.Platform;
using Xunit;

namespace Nightcrew.Tests;

/// <summary>The planner's snapshot from Windows, and display names.</summary>
public sealed class EfficiencySourceTests : IDisposable
{
    private Process? _child;

    public void Dispose()
    {
        try { if (_child is { HasExited: false }) _child.Kill(); } catch (InvalidOperationException) { }
        _child?.Dispose();
    }

    [Fact]
    public void The_snapshot_has_this_process_with_its_session_and_start_time()
    {
        using var self = Process.GetCurrentProcess();
        var table = new ToolhelpProcessTable();

        var snapshot = new EfficiencySource(table).Take([42]);

        var me = Assert.Single(snapshot.Processes, p => p.Pid == self.Id);
        Assert.Equal(self.SessionId, me.Session);
        Assert.Equal(self.SessionId, snapshot.Session);
        Assert.True(table.TryGetTimes(self.Id, out var start, out _));
        Assert.Equal(start, me.StartUtc);
        Assert.Equal([42], snapshot.HolderPids);
    }

    [Fact]
    public void The_display_name_is_the_file_description()
    {
        _child = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 30 127.0.0.1 >nul") { UseShellExecute = false, CreateNoWindow = true })!;
        Assert.True(new ToolhelpProcessTable().TryGetTimes(_child.Id, out var start, out _));
        var source = new EfficiencySource(new ToolhelpProcessTable());

        Assert.Equal("Windows Command Processor", source.DisplayName(new ProcessInfo(_child.Id, 0, "cmd", start, 1)));
    }

    [Fact]
    public void Without_a_description_the_display_name_is_the_process_name()
    {
        var source = new EfficiencySource(new ToolhelpProcessTable());

        Assert.Equal("gone", source.DisplayName(new ProcessInfo(0x7FFFFFFC, 0, "gone", DateTime.UtcNow, 1)));
    }
}
```

Create `tests/Nightcrew.Tests/WindowReaderTests.cs`:

```csharp
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Windows.Forms;
using Nightcrew.Core;
using Nightcrew.Platform;
using Xunit;

namespace Nightcrew.Tests;

/// <summary>The Windows readers, against windows and sockets these tests open themselves.</summary>
public sealed class WindowReaderTests
{
    private static readonly int Self = Process.GetCurrentProcess().Id;

    /// <summary>Shows a form far off screen on an STA thread, reads the owners, and closes it.</summary>
    private static bool ListedWith(Action<Form> setUp) => Sta.Run(() =>
    {
        using var form = new Form
        {
            Text = "Nightcrew test window",
            StartPosition = FormStartPosition.Manual,
            Location = new System.Drawing.Point(-32000, -32000),
        };
        setUp(form);
        form.Show();
        Application.DoEvents();
        try
        {
            return WindowOwners.List().Contains(Self);
        }
        finally
        {
            form.Close();
        }
    });

    [Fact]
    public void A_normal_window_makes_its_process_an_owner() =>
        Assert.True(ListedWith(_ => { }));

    [Fact]
    public void A_window_owned_by_another_window_does_not() =>
        Assert.False(ListedWith(form => form.ShowInTaskbar = false)); // WinForms gives it a hidden owner

    /// <summary>A tool window with no owner and no WS_EX_APPWINDOW, which WinForms adds to anything in the taskbar.</summary>
    private sealed class ToolForm : Form
    {
        protected override CreateParams CreateParams
        {
            get
            {
                var parameters = base.CreateParams;
                parameters.ExStyle = (parameters.ExStyle & ~0x40000) | 0x80; // drop WS_EX_APPWINDOW, add WS_EX_TOOLWINDOW
                return parameters;
            }
        }
    }

    [Fact]
    public void A_tool_window_does_not() => Assert.False(Sta.Run(() =>
    {
        using var form = new ToolForm { Text = "Nightcrew tool window", StartPosition = FormStartPosition.Manual, Location = new System.Drawing.Point(-32000, -32000) };
        form.Show();
        Application.DoEvents();
        try
        {
            return WindowOwners.List().Contains(Self);
        }
        finally
        {
            form.Close();
        }
    }));

    [Fact]
    public void A_window_without_a_title_does_not() =>
        Assert.False(ListedWith(form => form.Text = ""));

    [Fact]
    public void A_listener_and_a_loopback_connection_to_it_are_both_read()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var client = new TcpClient(AddressFamily.InterNetwork);
            client.Connect(IPAddress.Loopback, port);

            var rows = LoopbackConnections.List().Where(row => row.OwnerPid == Self).ToList();

            Assert.Contains(rows, row => row.State == TcpState.Listen && row.LocalPort == port);
            Assert.Contains(rows, row => row.State == TcpState.Established && row.RemotePort == port && row.RemoteIsLoopback);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public void An_ipv6_loopback_connection_is_read_as_loopback()
    {
        var listener = new TcpListener(IPAddress.IPv6Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var client = new TcpClient(AddressFamily.InterNetworkV6);
            client.Connect(IPAddress.IPv6Loopback, port);

            var rows = LoopbackConnections.List().Where(row => row.OwnerPid == Self).ToList();

            Assert.Contains(rows, row => row.State == TcpState.Established && row.RemotePort == port && row.RemoteIsLoopback);
        }
        finally
        {
            listener.Stop();
        }
    }

    private sealed class CountingTable : IProcessTable
    {
        public int Lists { get; private set; }

        public IReadOnlyList<ProcessEntry> List()
        {
            Lists++;
            return [new ProcessEntry(1, 0, "x")];
        }

        public bool TryGetTimes(int pid, out DateTime startUtc, out TimeSpan cpu)
        {
            startUtc = default;
            cpu = default;
            return false;
        }
    }

    [Fact]
    public void The_process_list_is_read_once_per_tick()
    {
        var inner = new CountingTable();
        var clock = new FakeClock();
        var table = new TickProcessTable(inner, clock);

        table.List();
        clock.Advance(TimeSpan.FromMilliseconds(200));
        table.List();
        Assert.Equal(1, inner.Lists);

        clock.Advance(TimeSpan.FromSeconds(10));
        table.List();
        Assert.Equal(2, inner.Lists);
    }
}
```

- [ ] **Step 2: Add the new types, with stubs, so the tests compile**

Modify `src/Nightcrew/Core/Efficiency.cs`:

```diff
diff --git a/src/Nightcrew/Core/Efficiency.cs b/src/Nightcrew/Core/Efficiency.cs
index 3bc7551..1ed7655 100644
--- a/src/Nightcrew/Core/Efficiency.cs
+++ b/src/Nightcrew/Core/Efficiency.cs
@@ -43,4 +43,16 @@ public interface IEfficiencyMode
 
     /// <summary>Puts back everything it changed, including what an earlier run left in the record; returns the number of apps.</summary>
     int RestoreAll(string reason);
 }
+
+/// <summary>Reads what the planner needs from Windows.</summary>
+public interface IEfficiencySource
+{
+    EfficiencySnapshot Take(IReadOnlyCollection<int> holderPids);
+
+    /// <summary>The name the panel shows for an app, such as "Microsoft Outlook" for olk.</summary>
+    string DisplayName(ProcessInfo root);
+}
+
+/// <summary>An app that was in Efficiency mode while you were away, as the panel lists it.</summary>
+public sealed record AwayApp(string DisplayName, string RootName);
```

Stub for the four new Platform classes, as stubs:

```csharp
using Nightcrew.Core;

namespace Nightcrew.Platform;

public static class WindowOwners
{
    public static IReadOnlyCollection<int> List() => throw new NotImplementedException();
}

public static class LoopbackConnections
{
    public static IReadOnlyList<TcpRow> List() => throw new NotImplementedException();
}

public sealed class TickProcessTable(IProcessTable inner, IClock clock) : IProcessTable
{
    public IReadOnlyList<ProcessEntry> List() => throw new NotImplementedException($"{inner}{clock}");

    public bool TryGetTimes(int pid, out DateTime startUtc, out TimeSpan cpu) => throw new NotImplementedException();
}

public sealed class EfficiencySource(IProcessTable table) : IEfficiencySource
{
    public EfficiencySnapshot Take(IReadOnlyCollection<int> holderPids) => throw new NotImplementedException($"{table}");

    public string DisplayName(ProcessInfo root) => throw new NotImplementedException();
}
```

- [ ] **Step 3: Run the tests and watch them fail**

```powershell
dotnet test -c Release --no-build -- --filter-class Nightcrew.Tests.WindowReaderTests
dotnet test -c Release --no-build -- --filter-class Nightcrew.Tests.EfficiencySourceTests
```

Expected: Every test in `WindowReaderTests` and `EfficiencySourceTests` fails with `NotImplementedException`.

- [ ] **Step 4: Implement**

Create `src/Nightcrew/Platform/EfficiencySource.cs`:

```csharp
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Nightcrew.Core;

namespace Nightcrew.Platform;

/// <summary>
/// The planner's snapshot from Windows: the tick's process list with start times and sessions, the Alt+Tab window
/// owners and the TCP tables. Display names come from the image's file description, read once per process name.
/// </summary>
public sealed class EfficiencySource(IProcessTable table) : IEfficiencySource
{
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll")]
    private static extern bool ProcessIdToSessionId(int processId, out int sessionId);

    [DllImport("kernel32.dll")]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

    private readonly int _session = Process.GetCurrentProcess().SessionId;
    private readonly Dictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);

    public EfficiencySnapshot Take(IReadOnlyCollection<int> holderPids)
    {
        var processes = new List<ProcessInfo>();
        foreach (var entry in table.List())
        {
            if (!table.TryGetTimes(entry.Pid, out var start, out _) || !ProcessIdToSessionId(entry.Pid, out var session)) continue;
            processes.Add(new ProcessInfo(entry.Pid, entry.ParentPid, entry.Name, start, session));
        }
        return new EfficiencySnapshot(_session, processes, WindowOwners.List(), holderPids, LoopbackConnections.List());
    }

    public string DisplayName(ProcessInfo root)
    {
        if (_names.TryGetValue(root.Name, out var known)) return known;
        var name = Describe(root.Pid) ?? root.Name;
        _names[root.Name] = name;
        return name;
    }

    private static string? Describe(int pid)
    {
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var path = new StringBuilder(1024);
            var size = path.Capacity;
            if (!QueryFullProcessImageName(handle, 0, path, ref size)) return null;
            var description = FileVersionInfo.GetVersionInfo(path.ToString()).FileDescription?.Trim();
            return string.IsNullOrEmpty(description) ? null : description;
        }
        catch (Exception ex) when (ex is FileNotFoundException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }
}
```

Create `src/Nightcrew/Platform/LoopbackConnections.cs`:

```csharp
using System.Runtime.InteropServices;
using Nightcrew.Core;

namespace Nightcrew.Platform;

/// <summary>Windows' TCP tables, IPv4 and IPv6, with the owning process of each row.</summary>
public static class LoopbackConnections
{
    private const int AF_INET = 2;
    private const int AF_INET6 = 23;
    private const int TCP_TABLE_OWNER_PID_ALL = 5;
    private const uint ERROR_INSUFFICIENT_BUFFER = 122;
    private const uint MIB_TCP_STATE_LISTEN = 2;
    private const uint MIB_TCP_STATE_ESTAB = 5;
    private const int Row4Size = 24;  // state, local address, local port, remote address, remote port, pid
    private const int Row6Size = 56;  // local address[16], scope, port, remote address[16], scope, port, state, pid

    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool sort, int family, int tableClass, uint reserved);

    /// <summary>Every row of both tables; empty for a table Windows won't give.</summary>
    public static IReadOnlyList<TcpRow> List()
    {
        var rows = new List<TcpRow>();
        Read(AF_INET, Row4Size, (buffer, at) => rows.Add(new TcpRow(
            OwnerPid: Marshal.ReadInt32(buffer, at + 20),
            LocalPort: Port(Marshal.ReadInt32(buffer, at + 8)),
            RemotePort: Port(Marshal.ReadInt32(buffer, at + 16)),
            RemoteIsLoopback: (Marshal.ReadInt32(buffer, at + 12) & 0xFF) == 127,
            State: StateOf((uint)Marshal.ReadInt32(buffer, at)))));
        Read(AF_INET6, Row6Size, (buffer, at) => rows.Add(new TcpRow(
            OwnerPid: Marshal.ReadInt32(buffer, at + 52),
            LocalPort: Port(Marshal.ReadInt32(buffer, at + 20)),
            RemotePort: Port(Marshal.ReadInt32(buffer, at + 44)),
            RemoteIsLoopback: IsLoopback6(buffer, at + 24),
            State: StateOf((uint)Marshal.ReadInt32(buffer, at + 48)))));
        return rows;
    }

    private static void Read(int family, int rowSize, Action<IntPtr, int> row)
    {
        var size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, TCP_TABLE_OWNER_PID_ALL, 0);
        for (var attempt = 0; attempt < 3 && size > 0; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                var status = GetExtendedTcpTable(buffer, ref size, false, family, TCP_TABLE_OWNER_PID_ALL, 0);
                if (status == ERROR_INSUFFICIENT_BUFFER) continue; // the table grew; size now holds the new length
                if (status != 0) return;
                var count = Marshal.ReadInt32(buffer);
                for (var i = 0; i < count; i++) row(buffer, 4 + i * rowSize);
                return;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    /// <summary>The port is in network byte order in the low 16 bits.</summary>
    private static int Port(int raw) => ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF);

    private static TcpState StateOf(uint state) => state switch
    {
        MIB_TCP_STATE_LISTEN => TcpState.Listen,
        MIB_TCP_STATE_ESTAB => TcpState.Established,
        _ => TcpState.Other,
    };

    /// <summary>::1, or an IPv4-mapped 127.x.x.x.</summary>
    private static bool IsLoopback6(IntPtr buffer, int at)
    {
        var bytes = new byte[16];
        Marshal.Copy(buffer + at, bytes, 0, 16);
        var zeroTo9 = bytes.Take(10).All(b => b == 0);
        if (zeroTo9 && bytes[10] == 0 && bytes[11] == 0 && bytes.Skip(12).Take(3).All(b => b == 0) && bytes[15] == 1) return true;
        return zeroTo9 && bytes[10] == 0xFF && bytes[11] == 0xFF && bytes[12] == 127;
    }
}
```

Create `src/Nightcrew/Platform/TickProcessTable.cs`:

```csharp
using Nightcrew.Core;

namespace Nightcrew.Platform;

/// <summary>
/// Lists processes at most once per tick, so the agent signal and the Efficiency mode planner share one list; it's
/// most of a tick's cost. Times are always read fresh.
/// </summary>
public sealed class TickProcessTable(IProcessTable inner, IClock clock) : IProcessTable
{
    private static readonly TimeSpan Reuse = TimeSpan.FromSeconds(1);

    private IReadOnlyList<ProcessEntry>? _list;
    private TimeSpan _listedAt;

    public IReadOnlyList<ProcessEntry> List()
    {
        var now = clock.Now;
        if (_list is null || now - _listedAt >= Reuse || now < _listedAt)
        {
            _list = inner.List();
            _listedAt = now;
        }
        return _list;
    }

    public bool TryGetTimes(int pid, out DateTime startUtc, out TimeSpan cpu) => inner.TryGetTimes(pid, out startUtc, out cpu);
}
```

Create `src/Nightcrew/Platform/WindowOwners.cs`:

```csharp
using System.Runtime.InteropServices;

namespace Nightcrew.Platform;

/// <summary>
/// The processes that own a window you could Alt+Tab to. Only reads windows; never sends them messages. A window on
/// another virtual desktop counts; one the app has cloaked itself doesn't.
/// </summary>
public static class WindowOwners
{
    private const int GWL_EXSTYLE = -20;
    private const uint GW_OWNER = 4;
    private const int WS_EX_TOOLWINDOW = 0x80;
    private const int WS_EX_APPWINDOW = 0x40000;
    private const int DWMWA_CLOAKED = 14;
    private const int DWM_CLOAKED_APP = 1;

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr window, uint command);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr window, int index);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out int processId);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);

    public static IReadOnlyCollection<int> List()
    {
        var owners = new HashSet<int>();
        EnumWindows((window, _) =>
        {
            if (Eligible(window) && GetWindowThreadProcessId(window, out var pid) != 0) owners.Add(pid);
            return true;
        }, IntPtr.Zero);
        return owners;
    }

    private static bool Eligible(IntPtr window)
    {
        if (!IsWindowVisible(window) || GetWindowTextLength(window) == 0) return false;
        var exStyle = GetWindowLong(window, GWL_EXSTYLE);
        var appWindow = (exStyle & WS_EX_APPWINDOW) != 0;
        if (!appWindow && ((exStyle & WS_EX_TOOLWINDOW) != 0 || GetWindow(window, GW_OWNER) != IntPtr.Zero)) return false;
        return DwmGetWindowAttribute(window, DWMWA_CLOAKED, out var cloaked, sizeof(int)) != 0 || cloaked != DWM_CLOAKED_APP;
    }
}
```

- [ ] **Step 5: Run the tests and watch them pass, then the whole suite**

```powershell
dotnet build -c Release
dotnet test -c Release --no-build -- --filter-class Nightcrew.Tests.WindowReaderTests
dotnet test -c Release --no-build -- --filter-class Nightcrew.Tests.EfficiencySourceTests
dotnet test -c Release --no-build
powershell -NoProfile -File build/check-single-file.ps1
```

Expected: all pass, 0 warnings, the single-file gate passes.

- [ ] **Step 6: Commit**

```powershell
git add src/Nightcrew/Core/Efficiency.cs src/Nightcrew/Platform/EfficiencySource.cs src/Nightcrew/Platform/LoopbackConnections.cs src/Nightcrew/Platform/TickProcessTable.cs src/Nightcrew/Platform/WindowOwners.cs tests/Nightcrew.Tests/EfficiencySourceTests.cs tests/Nightcrew.Tests/WindowReaderTests.cs
git commit -m "feat: read Alt+Tab window owners, the TCP tables and one process list per tick" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

### Task 4: Coordinator, dry run, engine and watchdog

Spec sections 2 (the restore table's engine rows), 5.3 (watchdog), 5.5 (dry run) and 8 (engine changes).

**Files:**
- Modify: `src/Nightcrew/App/Watchdog.cs`
- Create: `src/Nightcrew/Core/EfficiencyCoordinator.cs`
- Modify: `src/Nightcrew/Core/Engine.cs`
- Modify: `src/Nightcrew/Core/GpuSignal.cs`
- Modify: `src/Nightcrew/Platform/AppPaths.cs`
- Modify: `src/Nightcrew/Platform/DryRun.cs`
- Test (create): `tests/Nightcrew.Tests/DryRunEfficiencyTests.cs`
- Test (create): `tests/Nightcrew.Tests/EfficiencyCoordinatorTests.cs`
- Test (modify): `tests/Nightcrew.Tests/EngineTests.cs`

**Interfaces:**
- Consumes: everything from tasks 1–3
- Produces: `EfficiencyCoordinator(IEfficiencyMode efficiency, IEfficiencySource source, Settings settings, Func<IReadOnlyCollection<int>> holderPids, ILog log)` with `bool Active`, `IReadOnlyList<AwayApp> Away`, `int AppCount`, `void Step(Mode mode, LidState lid, TimeSpan idleTime, LidAction ownerLid)`, `int RestoreAll(string reason)`
- Produces: `DryRunEfficiency(ILog log) : IEfficiencyMode` (logs `[dry run] would put into Efficiency mode: …` and `[dry run] would end Efficiency mode for …`)
- Produces: `GpuSignal.BusyPids` (busiest first)
- Produces: `Engine(..., IClock clock, EfficiencyCoordinator? efficiency = null)`; Disarm calls `RestoreAll("work ended")`, or `"battery floor or lid-closed limit"` when the controller is spent, before the power settings go back
- Produces: `EngineSnapshot(..., int EfficiencyApps = 0, IReadOnlyList<AwayApp>? Away = null)`; `Away` is filled only once the time away has ended
- Produces: `AppPaths.EfficiencyFile` (`efficiency.json`); `Watchdog.Run` also calls `new EfficiencyMode(AppPaths.EfficiencyFile, log).RestoreAll("watchdog")`

- [ ] **Step 1: Write the failing tests**

Create `tests/Nightcrew.Tests/DryRunEfficiencyTests.cs`:

```csharp
using Nightcrew.Core;
using Nightcrew.Platform;
using Xunit;

namespace Nightcrew.Tests;

/// <summary>The dry run, and the GPU holder pids the planner leaves alone.</summary>
public sealed class DryRunEfficiencyTests
{
    private readonly ListLog _log = new();

    private sealed class ListLog : ILog
    {
        public List<string> Infos { get; } = [];

        public void Info(string message) => Infos.Add(message);

        public void Warn(string message) { }
    }

    private static AppFamily Family() => new("Windows Command Processor", "cmd", [new ProcessRef(1234, new DateTime(2026, 10, 15, 8, 0, 0, DateTimeKind.Utc))]);

    [Fact]
    public void The_dry_run_logs_what_it_would_do_and_keeps_track_like_the_real_thing()
    {
        var dryRun = new DryRunEfficiency(_log);

        Assert.Equal(ApplyResult.Applied, dryRun.Apply(Family(), dryRun.Generation));
        Assert.Equal(ApplyResult.Applied, dryRun.Apply(Family(), dryRun.Generation));
        Assert.Equal([Family().Root], dryRun.Applied);
        Assert.Equal(1, dryRun.RestoreAll("lid opened"));
        Assert.Empty(dryRun.Applied);

        Assert.Equal(["[dry run] would put into Efficiency mode: Windows Command Processor (cmd, 1 process)",
            "[dry run] would end Efficiency mode for 1 app: lid opened"], _log.Infos);
    }

    [Fact]
    public void A_dry_run_pass_that_began_before_a_restore_changes_nothing()
    {
        var dryRun = new DryRunEfficiency(_log);
        var generation = dryRun.Generation;
        dryRun.RestoreAll("lid opened");

        Assert.Equal(ApplyResult.Stale, dryRun.Apply(Family(), generation));
        Assert.Empty(dryRun.Applied);
    }

    [Fact]
    public void A_dry_run_restore_of_one_app_is_logged_with_its_name()
    {
        var dryRun = new DryRunEfficiency(_log);
        dryRun.Apply(Family(), dryRun.Generation);

        dryRun.Restore(Family().Root, "left alone now");

        Assert.Empty(dryRun.Applied);
        Assert.Equal("[dry run] would end Efficiency mode for Windows Command Processor: left alone now", _log.Infos.Last());
    }

    private sealed class FakeGpu(Dictionary<int, double> percent) : IGpuUsage
    {
        public IReadOnlyDictionary<int, double> PercentByPid() => percent;
    }

    [Fact]
    public void The_gpu_signal_reports_the_pids_of_its_busy_processes_busiest_first()
    {
        var signal = new GpuSignal(new FakeGpu(new() { [10] = 50, [11] = 5, [12] = 80 }), 30);

        Assert.True(signal.Sample(TimeSpan.Zero));

        Assert.Equal([12, 10], signal.BusyPids);
    }
}
```

Create `tests/Nightcrew.Tests/EfficiencyCoordinatorTests.cs`:

```csharp
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
    public void Away_puts_each_planned_app_into_efficiency_mode_and_lists_it()
    {
        App(10, "spotify");
        App(20, "olk");

        StepAway();

        Assert.Equal(["spotify", "olk"], _efficiency.AppliedNames);
        Assert.True(_coordinator.Active);
        Assert.Equal(2, _coordinator.AppCount);
        Assert.Equal([new AwayApp("Spotify!", "spotify"), new AwayApp("Olk!", "olk")], _coordinator.Away);
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
        Assert.Equal(2, _coordinator.Away.Count);
    }

    [Fact]
    public void A_denied_app_is_logged_once()
    {
        App(10, "steamvr");
        _efficiency.Deny.Add("steamvr");

        StepAway();
        StepAway();

        Assert.Single(_log.Infos, line => line == "Can't change steamvr: access denied");
        Assert.Empty(_coordinator.Away);
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
    public void The_away_list_starts_over_the_next_time_away()
    {
        App(10, "spotify");
        StepAway();
        _coordinator.Step(Mode.Armed, LidState.Open, Away, LidAction.Sleep);
        Assert.Single(_coordinator.Away);

        _source.Windows.Remove(10);
        StepAway();

        Assert.Empty(_coordinator.Away);
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
```

Modify `tests/Nightcrew.Tests/EngineTests.cs`:

```diff
diff --git a/tests/Nightcrew.Tests/EngineTests.cs b/tests/Nightcrew.Tests/EngineTests.cs
index 19401c6..de8035f 100644
--- a/tests/Nightcrew.Tests/EngineTests.cs
+++ b/tests/Nightcrew.Tests/EngineTests.cs
@@ -18,8 +18,10 @@ public sealed class EngineTests : IDisposable
     private readonly FakeLog _log = new();
     private readonly FakePolicy _policy;
     private readonly FakeKeepAwake _keepAwake;
     private readonly FakeSystemPower _power;
+    private readonly FakeEfficiency _efficiency;
+    private readonly FakeSource _source = new();
     private readonly RestoreJournal _journal;
     private readonly Controller _controller;
     private readonly Engine _engine;
 
@@ -27,12 +29,13 @@ public sealed class EngineTests : IDisposable
     {
         _policy = new FakePolicy(Owner, _calls);
         _keepAwake = new FakeKeepAwake(_calls);
         _power = new FakeSystemPower(_calls);
+        _efficiency = new FakeEfficiency(_calls);
         _journal = new RestoreJournal(_policy, RecordPath, _log);
         _controller = new Controller(_settings);
         _engine = new Engine(new ActivityMonitor([_signal], Grace), _controller, _journal,
-            _policy, _keepAwake, _power, _log, _clock);
+            _policy, _keepAwake, _power, _log, _clock, new EfficiencyCoordinator(_efficiency, _source, _settings, () => [], _log));
     }
 
     private string RecordPath => Path.Combine(_dir, "restore.json");
 
@@ -325,8 +328,114 @@ public sealed class EngineTests : IDisposable
         Assert.Contains(_log.Infos, line => line.StartsWith("[dry run]") && line.Contains("lock"));
         Assert.Contains(_log.Infos, line => line.StartsWith("[dry run]") && line.Contains("write power settings"));
     }
 
+    /// <summary>An agent works, the lid is closed and nobody has touched the laptop for 10 minutes; Spotify is open.</summary>
+    private void AwayWithSpotifyOpen()
+    {
+        _source.Window(10, "spotify");
+        _signal.Busy = true;
+        _engine.Lid = LidState.Closed;
+        _power.IdleTime = TimeSpan.FromMinutes(10);
+        Tick();
+    }
+
+    [Fact]
+    public void Away_with_an_agent_working_puts_open_apps_into_efficiency_mode()
+    {
+        AwayWithSpotifyOpen();
+
+        Assert.Equal(["spotify"], _efficiency.Names);
+        Assert.Equal(1, _engine.Snapshot.EfficiencyApps);
+    }
+
+    [Fact]
+    public void When_the_work_ends_apps_come_out_before_the_power_settings_go_back()
+    {
+        AwayWithSpotifyOpen();
+        _signal.Busy = false;
+
+        Tick(afterMinutes: 6);
+
+        var restore = _calls.IndexOf("RestoreAll:work ended");
+        Assert.True(restore >= 0);
+        Assert.True(restore < _calls.LastIndexOf("Release"));
+        Assert.Empty(_efficiency.Names);
+    }
+
+    [Fact]
+    public void At_the_battery_floor_apps_come_out_before_hibernating()
+    {
+        AwayWithSpotifyOpen();
+        _power.Source = PowerSource.DC;
+        _power.BatteryPercent = 10;
+
+        Tick(afterMinutes: 1);
+
+        var restore = _calls.IndexOf("RestoreAll:battery floor or lid-closed limit");
+        Assert.True(restore >= 0);
+        Assert.True(restore < _calls.IndexOf("Hibernate"));
+    }
+
+    [Fact]
+    public void After_the_lid_opens_the_snapshot_lists_the_apps_from_the_time_away()
+    {
+        AwayWithSpotifyOpen();
+        Assert.Empty(_engine.Snapshot.Away!);
+
+        _engine.Lid = LidState.Open;
+        Tick();
+
+        Assert.Equal([new AwayApp("spotify", "spotify")], _engine.Snapshot.Away);
+        Assert.Equal(0, _engine.Snapshot.EfficiencyApps);
+        Assert.Contains("RestoreAll:lid opened", _calls);
+    }
+
+    /// <summary>Applies in memory and records a restore in the call list only when there was something to put back.</summary>
+    private sealed class FakeEfficiency(List<string> calls) : IEfficiencyMode
+    {
+        private readonly Dictionary<ProcessRef, string> _applied = [];
+
+        public List<string> Names => _applied.Values.ToList();
+        public int Generation { get; private set; }
+        public IReadOnlyCollection<ProcessRef> Applied => _applied.Keys.ToList();
+
+        public ApplyResult Apply(AppFamily family, int generation)
+        {
+            if (generation != Generation) return ApplyResult.Stale;
+            _applied[family.Root] = family.RootName;
+            return ApplyResult.Applied;
+        }
+
+        public void Restore(ProcessRef root, string reason) => _applied.Remove(root);
+
+        public int RestoreAll(string reason)
+        {
+            Generation++;
+            var apps = _applied.Count;
+            if (apps > 0) calls.Add("RestoreAll:" + reason);
+            _applied.Clear();
+            return apps;
+        }
+    }
+
+    private sealed class FakeSource : IEfficiencySource
+    {
+        private static readonly DateTime T0 = new(2026, 10, 15, 8, 0, 0, DateTimeKind.Utc);
+        private readonly List<ProcessInfo> _processes = [new ProcessInfo(2, 1, "explorer", T0, 1)];
+        private readonly List<int> _windows = [];
+
+        public void Window(int pid, string name)
+        {
+            _processes.Add(new ProcessInfo(pid, 2, name, T0.AddHours(1), 1));
+            _windows.Add(pid);
+        }
+
+        public EfficiencySnapshot Take(IReadOnlyCollection<int> holderPids) => new(1, _processes, _windows, holderPids, []);
+
+        public string DisplayName(ProcessInfo root) => root.Name;
+    }
+
     private sealed class FakeSignal : IBusySignal
     {
         public string Name => "transcripts";
         public bool Busy { get; set; }
```

- [ ] **Step 2: Add the new types, with stubs, so the tests compile**

Modify `src/Nightcrew/Platform/AppPaths.cs`:

```diff
diff --git a/src/Nightcrew/Platform/AppPaths.cs b/src/Nightcrew/Platform/AppPaths.cs
index 92129cc..f280595 100644
--- a/src/Nightcrew/Platform/AppPaths.cs
+++ b/src/Nightcrew/Platform/AppPaths.cs
@@ -6,6 +6,7 @@ public static class AppPaths
         Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nightcrew");
 
     public static string SettingsFile => Path.Combine(DataDir, "settings.json");
     public static string RestoreFile => Path.Combine(DataDir, "restore.json");
+    public static string EfficiencyFile => Path.Combine(DataDir, "efficiency.json");
     public static string LogFile => Path.Combine(DataDir, "nightcrew.log");
 }
```

Modify `src/Nightcrew/Core/GpuSignal.cs`:

```diff
diff --git a/src/Nightcrew/Core/GpuSignal.cs b/src/Nightcrew/Core/GpuSignal.cs
index eabce52..4435c90 100644
--- a/src/Nightcrew/Core/GpuSignal.cs
+++ b/src/Nightcrew/Core/GpuSignal.cs
@@ -16,16 +16,20 @@ public sealed class GpuSignal(IGpuUsage gpu, double thresholdPercent, Func<int,
 
     /// <summary>Every process at or above the threshold in the last busy sample, busiest first.</summary>
     public IReadOnlyList<Holder> Holders { get; private set; } = [];
 
+    /// <summary>The pids behind <see cref="Holders"/>, so Efficiency mode leaves their apps alone.</summary>
+    public IReadOnlyList<int> BusyPids { get; private set; } = [];
+
     public bool Sample(TimeSpan now)
     {
         var busy = gpu.PercentByPid()
             .Where(pair => pair.Value >= thresholdPercent)
             .OrderByDescending(pair => pair.Value)
             .ToList();
         if (busy.Count == 0) return false;
         LastBusyPid = busy[0].Key;
+        BusyPids = busy.Select(pair => pair.Key).ToList();
         Holders = busy.Select(pair => new Holder(nameOf?.Invoke(pair.Key) ?? $"pid {pair.Key}", $"GPU {Math.Round(pair.Value):0}%")).ToList();
         return true;
     }
 }
```

Stub for src/Nightcrew/Core/EfficiencyCoordinator.cs and the DryRunEfficiency class, as stubs; Engine and EngineSnapshot get their new parameters but ignore them:

```csharp
public sealed class EfficiencyCoordinator(IEfficiencyMode efficiency, IEfficiencySource source, Settings settings,
    Func<IReadOnlyCollection<int>> holderPids, ILog log)
{
    public bool Active => throw new NotImplementedException($"{efficiency}{source}{settings}{holderPids}{log}");
    public IReadOnlyList<AwayApp> Away => throw new NotImplementedException();
    public int AppCount => throw new NotImplementedException();
    public void Step(Mode mode, LidState lid, TimeSpan idleTime, LidAction ownerLid) => throw new NotImplementedException();
    public int RestoreAll(string reason) => throw new NotImplementedException();
}

public sealed class DryRunEfficiency(ILog log) : IEfficiencyMode
{
    public int Generation => throw new NotImplementedException($"{log}");
    public IReadOnlyCollection<ProcessRef> Applied => throw new NotImplementedException();
    public ApplyResult Apply(AppFamily family, int generation) => throw new NotImplementedException();
    public void Restore(ProcessRef root, string reason) => throw new NotImplementedException();
    public int RestoreAll(string reason) => throw new NotImplementedException();
}
```

- [ ] **Step 3: Run the tests and watch them fail**

```powershell
dotnet test -c Release --no-build -- --filter-class Nightcrew.Tests.EfficiencyCoordinatorTests
dotnet test -c Release --no-build -- --filter-class Nightcrew.Tests.DryRunEfficiencyTests
dotnet test -c Release --no-build -- --filter-class Nightcrew.Tests.EngineTests
```

Expected: `EfficiencyCoordinatorTests` and `DryRunEfficiencyTests` fail with `NotImplementedException`; the four new `EngineTests` fail because the engine never calls the coordinator. The 14 existing `EngineTests` still pass.

- [ ] **Step 4: Implement**

Modify `src/Nightcrew/App/Watchdog.cs`:

```diff
diff --git a/src/Nightcrew/App/Watchdog.cs b/src/Nightcrew/App/Watchdog.cs
index 824bf2c..78b3ac4 100644
--- a/src/Nightcrew/App/Watchdog.cs
+++ b/src/Nightcrew/App/Watchdog.cs
@@ -2,9 +2,9 @@ using Nightcrew.Core;
 using Nightcrew.Platform;
 
 namespace Nightcrew.App;
 
-/// <summary>Runs from the scheduled task: restores the owner's power settings if the app is gone but left a record.</summary>
+/// <summary>Runs from the scheduled task: if the app is gone but left records, restores the power settings and takes apps out of Efficiency mode.</summary>
 public static class Watchdog
 {
     public static int Run(ILog log)
     {
@@ -21,8 +21,9 @@ public static class Watchdog
             {
                 log.Info("Watchdog: Nightcrew is not running but left a restore record; restoring power settings");
                 journal.Restore();
             }
+            new EfficiencyMode(AppPaths.EfficiencyFile, log).RestoreAll("watchdog");
             return 0;
         }
         finally
         {
```

Create `src/Nightcrew/Core/EfficiencyCoordinator.cs`:

```csharp
namespace Nightcrew.Core;

/// <summary>
/// Each tick: asks the gate, and while you're away re-plans and drives Efficiency mode. Apps that became needed go
/// back; new apps go in. Keeps the list of apps from the most recent time away for the panel.
/// </summary>
public sealed class EfficiencyCoordinator(IEfficiencyMode efficiency, IEfficiencySource source, Settings settings,
    Func<IReadOnlyCollection<int>> holderPids, ILog log)
{
    private readonly List<AwayApp> _away = [];
    private readonly HashSet<string> _deniedLogged = new(StringComparer.OrdinalIgnoreCase);
    private string? _loggedKept;

    /// <summary>True while you're away and Efficiency mode is wanted.</summary>
    public bool Active { get; private set; }

    /// <summary>The apps put into Efficiency mode during the most recent time away.</summary>
    public IReadOnlyList<AwayApp> Away => _away.ToList();

    public int AppCount => efficiency.Applied.Count;

    public void Step(Mode mode, LidState lid, TimeSpan idleTime, LidAction ownerLid)
    {
        if (!EfficiencyGate.Wanted(mode, lid, idleTime, ownerLid, settings))
        {
            Active = false;
            if (efficiency.Applied.Count > 0) efficiency.RestoreAll(OffReason(mode, lid, idleTime, ownerLid));
            return;
        }
        if (!Active)
        {
            Active = true;
            _away.Clear();
            _loggedKept = null;
        }

        var generation = efficiency.Generation;
        var plan = new EfficiencyPlanner(settings, source.DisplayName).Plan(source.Take(holderPids()));
        var planned = new HashSet<ProcessRef>(plan.Apply.Select(family => family.Root));
        foreach (var root in efficiency.Applied.Where(root => !planned.Contains(root)).ToList())
            efficiency.Restore(root, "left alone now");

        foreach (var family in plan.Apply)
        {
            switch (efficiency.Apply(family, generation))
            {
                case ApplyResult.Stale:
                    return;
                case ApplyResult.Denied:
                    if (_deniedLogged.Add(family.RootName)) log.Info($"Can't change {family.RootName}: access denied");
                    break;
                case ApplyResult.Applied:
                    if (!_away.Any(app => app.RootName == family.RootName)) _away.Add(new AwayApp(family.DisplayName, family.RootName));
                    break;
            }
        }

        var kept = string.Join(", ", plan.Kept.Select(app => $"{app.RootName} ({app.Rule})"));
        if (kept.Length == 0 || kept == _loggedKept) return;
        _loggedKept = kept;
        log.Info("Left alone: " + kept);
    }

    /// <summary>Puts every app back now, without waiting for the next tick to notice.</summary>
    public int RestoreAll(string reason)
    {
        Active = false;
        return efficiency.RestoreAll(reason);
    }

    private string OffReason(Mode mode, LidState lid, TimeSpan idleTime, LidAction ownerLid)
    {
        if (!settings.EfficiencyWhileAway) return "switched off";
        if (mode == Mode.Paused) return "pause";
        if (mode != Mode.Armed) return "work ended";
        if (lid != LidState.Closed) return "lid opened";
        if (idleTime < TimeSpan.FromMinutes(settings.AwayMinutes)) return "input";
        return ownerLid == LidAction.DoNothing ? "lid action is Do nothing" : "not away";
    }
}
```

Modify `src/Nightcrew/Core/Engine.cs`:

```diff
diff --git a/src/Nightcrew/Core/Engine.cs b/src/Nightcrew/Core/Engine.cs
index 8c8eca5..9b678ac 100644
--- a/src/Nightcrew/Core/Engine.cs
+++ b/src/Nightcrew/Core/Engine.cs
@@ -2,19 +2,23 @@ namespace Nightcrew.Core;
 
 /// <summary>What needs the owner's attention, most important first.</summary>
 public enum EngineWarning { None, PolicyBlocks, ArmBackoff, RestorePending, Stopped }
 
-/// <summary>Everything the tray and the panel show, taken at the end of a tick.</summary>
+/// <summary>
+/// Everything the tray and the panel show, taken at the end of a tick. <paramref name="EfficiencyApps"/> counts the apps in
+/// Efficiency mode now; <paramref name="Away"/> lists the apps from the most recent time away, once it has ended.
+/// </summary>
 public sealed record EngineSnapshot(Mode Mode, LidState Lid, bool Live, IReadOnlyList<Holder> Holders, PowerSource Source,
-    int? BatteryPercent, LidAction OwnerLid, bool Hold, bool Paused, EngineWarning Warning)
+    int? BatteryPercent, LidAction OwnerLid, bool Hold, bool Paused, EngineWarning Warning, int EfficiencyApps = 0,
+    IReadOnlyList<AwayApp>? Away = null)
 {
     public static EngineSnapshot Initial { get; } =
         new(Mode.Idle, LidState.Unknown, false, [], PowerSource.AC, null, LidAction.Sleep, false, false, EngineWarning.None);
 }
 
 /// <summary>One tick: sample activity, ask the controller, carry out its commands.</summary>
 public sealed class Engine(ActivityMonitor activity, Controller controller, RestoreJournal journal,
-    IPowerPolicy policy, IKeepAwake keepAwake, ISystemPower power, ILog log, IClock clock)
+    IPowerPolicy policy, IKeepAwake keepAwake, ISystemPower power, ILog log, IClock clock, EfficiencyCoordinator? efficiency = null)
 {
     private static readonly TimeSpan CanWriteCheckEvery = TimeSpan.FromMinutes(5);
     private static readonly TimeSpan RestoreRetryEvery = TimeSpan.FromSeconds(60);
     private static readonly TimeSpan ArmRetryAfter = TimeSpan.FromMinutes(5);
@@ -55,17 +59,19 @@ public sealed class Engine(ActivityMonitor activity, Controller controller, Rest
             var policyAllows = CanWrite(now);
             var backingOff = _armBlockedUntil is { } until && now < until;
             var inputs = new Inputs(activity.Live, Lid, source, battery, power.IdleTime, Paused, policyAllows && !backingOff, ownerLid);
             foreach (var command in controller.Step(inputs, now)) Run(command, now);
+            efficiency?.Step(controller.Mode, Lid, inputs.IdleTime, ownerLid);
             RetryRestore(now);
 
             var warning = !policyAllows ? EngineWarning.PolicyBlocks
                 : _armBlockedUntil is { } blockedUntil && now < blockedUntil ? EngineWarning.ArmBackoff
                 : controller.Mode != Mode.Armed && journal.HasRecord ? EngineWarning.RestorePending
                 : controller.Spent ? EngineWarning.Stopped
                 : EngineWarning.None;
             Snapshot = new EngineSnapshot(controller.Mode, Lid, activity.Live, activity.Holders, source, battery,
-                ownerLid, activity.Hold, Paused, warning);
+                ownerLid, activity.Hold, Paused, warning, efficiency?.AppCount ?? 0,
+                efficiency is { Active: false } ? efficiency.Away : []);
         }
         catch (Exception ex)
         {
             log.Warn($"Tick failed: {ex.Message}");
@@ -117,8 +123,10 @@ public sealed class Engine(ActivityMonitor activity, Controller controller, Rest
                     journal.Arm();
                     keepAwake.Hold();
                     break;
                 case Command.Disarm:
+                    // Apps first, so nothing stays slowed down once the laptop goes to sleep.
+                    efficiency?.RestoreAll(controller.Spent ? "battery floor or lid-closed limit" : "work ended");
                     ReleaseAndRestore(now);
                     break;
                 case Command.Lock:
                     power.LockWorkstation();
```

Modify `src/Nightcrew/Platform/DryRun.cs`:

```diff
diff --git a/src/Nightcrew/Platform/DryRun.cs b/src/Nightcrew/Platform/DryRun.cs
index 94f3d91..ed09ccc 100644
--- a/src/Nightcrew/Platform/DryRun.cs
+++ b/src/Nightcrew/Platform/DryRun.cs
@@ -50,4 +50,56 @@ public sealed class DryRunSystemPower(ISystemPower real, ILog log) : ISystemPowe
     public void MonitorOff() => log.Info("[dry run] would turn the monitor off");
 
     public void LockWorkstation() => log.Info("[dry run] would lock the workstation");
 }
+
+/// <summary>Logs what Efficiency mode would do; changes no process.</summary>
+public sealed class DryRunEfficiency(ILog log) : IEfficiencyMode
+{
+    private readonly object _lock = new();
+    private readonly Dictionary<ProcessRef, string> _applied = [];
+    private int _generation;
+
+    public int Generation
+    {
+        get { lock (_lock) return _generation; }
+    }
+
+    public IReadOnlyCollection<ProcessRef> Applied
+    {
+        get { lock (_lock) return _applied.Keys.ToList(); }
+    }
+
+    public ApplyResult Apply(AppFamily family, int generation)
+    {
+        lock (_lock)
+        {
+            if (generation != _generation) return ApplyResult.Stale;
+            if (_applied.ContainsKey(family.Root)) return ApplyResult.Applied;
+            _applied[family.Root] = family.DisplayName;
+            log.Info($"[dry run] would put into Efficiency mode: {EfficiencyMode.Describe(family, family.Processes.Count)}");
+            return ApplyResult.Applied;
+        }
+    }
+
+    public void Restore(ProcessRef root, string reason)
+    {
+        lock (_lock)
+        {
+            if (!_applied.TryGetValue(root, out var name)) return;
+            _applied.Remove(root);
+            log.Info($"[dry run] would end Efficiency mode for {name}: {reason}");
+        }
+    }
+
+    public int RestoreAll(string reason)
+    {
+        lock (_lock)
+        {
+            _generation++;
+            var apps = _applied.Count;
+            _applied.Clear();
+            if (apps > 0) log.Info($"[dry run] would end Efficiency mode for {apps} {(apps == 1 ? "app" : "apps")}: {reason}");
+            return apps;
+        }
+    }
+}
```

- [ ] **Step 5: Run the tests and watch them pass, then the whole suite**

```powershell
dotnet build -c Release
dotnet test -c Release --no-build -- --filter-class Nightcrew.Tests.EfficiencyCoordinatorTests
dotnet test -c Release --no-build -- --filter-class Nightcrew.Tests.DryRunEfficiencyTests
dotnet test -c Release --no-build -- --filter-class Nightcrew.Tests.EngineTests
dotnet test -c Release --no-build
powershell -NoProfile -File build/check-single-file.ps1
```

Expected: all pass, 0 warnings, the single-file gate passes.

- [ ] **Step 6: Commit**

```powershell
git add src/Nightcrew/App/Watchdog.cs src/Nightcrew/Core/EfficiencyCoordinator.cs src/Nightcrew/Core/Engine.cs src/Nightcrew/Core/GpuSignal.cs src/Nightcrew/Platform/AppPaths.cs src/Nightcrew/Platform/DryRun.cs tests/Nightcrew.Tests/DryRunEfficiencyTests.cs tests/Nightcrew.Tests/EfficiencyCoordinatorTests.cs tests/Nightcrew.Tests/EngineTests.cs
git commit -m "feat: the coordinator drives Efficiency mode each tick; apps come back first when the work ends" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

### Task 5: What you see, and the wiring

Spec sections 6, 7 and 8 (TrayApp and Program).

**Files:**
- Modify: `src/Nightcrew/App/TrayApp.cs`
- Modify: `src/Nightcrew/App/Ui/PanelVisual.cs`
- Modify: `src/Nightcrew/Core/PanelModel.cs`
- Modify: `src/Nightcrew/Core/SettingsModel.cs`
- Modify: `src/Nightcrew/Program.cs`
- Test (modify): `tests/Nightcrew.Tests/PanelModelTests.cs`
- Test (modify): `tests/Nightcrew.Tests/PanelVisualTests.cs`
- Test (modify): `tests/Nightcrew.Tests/SettingsModelTests.cs`
- Test (modify): `tests/Nightcrew.Tests/SettingsPageTests.cs`

**Interfaces:**
- Consumes: everything from tasks 1–4
- Produces: `AwayRow(string DisplayName, string RootName, bool LeftAlone)`; `PanelView(..., IReadOnlyList<AwayRow>? Away = null)`
- Produces: `PanelModel.From(EngineSnapshot snapshot, int batteryFloorPercent, bool watchdogMissing = false, IReadOnlyCollection<string>? leaveAlone = null)`
- Produces: `SettingsPage(..., bool EfficiencyWhileAway = true, IReadOnlyList<string>? LeaveAlone = null)` (sorted)
- Produces: `PanelActions(..., Action<string, bool>? LeaveAlone = null)`, `SettingsActions(..., Action<bool>? EfficiencyWhileAway = null, Action<string>? RemoveLeaveAlone = null)`
- Produces: `TrayApp(..., ILog log, IEfficiencyMode efficiency, Func<TimeSpan> idleTime)`

- [ ] **Step 1: Write the failing tests**

Modify `tests/Nightcrew.Tests/PanelModelTests.cs`:

```diff
diff --git a/tests/Nightcrew.Tests/PanelModelTests.cs b/tests/Nightcrew.Tests/PanelModelTests.cs
index 5ff4321..2cd5e1e 100644
--- a/tests/Nightcrew.Tests/PanelModelTests.cs
+++ b/tests/Nightcrew.Tests/PanelModelTests.cs
@@ -9,8 +9,28 @@ public sealed class PanelModelTests
         PowerSource source = PowerSource.AC, int? battery = 81, LidAction owner = LidAction.Sleep, bool hold = false,
         bool paused = false, EngineWarning warning = EngineWarning.None, params Holder[] holders) =>
         new(mode, lid, live, holders, source, battery, owner, hold, paused, warning);
 
+    [Fact]
+    public void Once_you_are_back_the_apps_from_the_time_away_are_listed_with_their_leave_alone_state()
+    {
+        var snapshot = Snap(mode: Mode.Armed, live: true) with { Away = [new AwayApp("Microsoft Outlook", "olk"), new AwayApp("WhatsApp", "whatsapp.root")] };
+
+        var view = PanelModel.From(snapshot, 20, leaveAlone: ["OLK"]);
+
+        Assert.Equal([new AwayRow("Microsoft Outlook", "olk", true), new AwayRow("WhatsApp", "whatsapp.root", false)], view.Away);
+    }
+
+    [Fact]
+    public void With_the_lid_closed_or_nothing_from_the_time_away_there_is_no_list()
+    {
+        var closed = Snap(mode: Mode.Armed, live: true, lid: LidState.Closed) with { Away = [new AwayApp("WhatsApp", "whatsapp.root")] };
+
+        Assert.Null(PanelModel.From(closed, 20).Away);
+        Assert.Null(PanelModel.From(Snap(), 20).Away);
+        Assert.Null(PanelModel.From(Snap() with { Away = [] }, 20).Away);
+    }
+
     [Fact]
     public void Quiet_says_all_quiet_and_what_the_lid_does()
     {
         var view = PanelModel.From(Snap(owner: LidAction.DoNothing), 20);
```

Modify `tests/Nightcrew.Tests/PanelVisualTests.cs`:

```diff
diff --git a/tests/Nightcrew.Tests/PanelVisualTests.cs b/tests/Nightcrew.Tests/PanelVisualTests.cs
index 50f8ae8..3cd5165 100644
--- a/tests/Nightcrew.Tests/PanelVisualTests.cs
+++ b/tests/Nightcrew.Tests/PanelVisualTests.cs
@@ -122,8 +122,39 @@ public sealed class PanelVisualTests : IDisposable
         static void Click(UIElement element) =>
             element.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
     }
 
+    [Fact]
+    public void The_apps_from_the_time_away_sit_between_the_holders_and_the_facts_and_each_switch_reports_back()
+    {
+        var view = View("quiet") with { Away = [new AwayRow("Microsoft Outlook", "olk", false), new AwayRow("WhatsApp", "whatsapp.root", true)] };
+        var (texts, calls) = Sta.Run(() =>
+        {
+            var calls = new List<string>();
+            var root = PanelVisual.Build(view, MarkKind.Lantern,
+                new PanelActions(() => { }, () => { }, () => { }, (name, on) => calls.Add($"{name} {on}")));
+            foreach (var app in new[] { "Microsoft Outlook", "WhatsApp" })
+            {
+                var row = Find<Border>(root, border => System.Windows.Automation.AutomationProperties.GetName(border) == app);
+                row.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
+            }
+            return (Texts(root), calls);
+        });
+
+        var offTrack = Sta.Run(() =>
+        {
+            var row = Find<Border>(PanelVisual.Build(view, MarkKind.Lantern, null), border => System.Windows.Automation.AutomationProperties.GetName(border) == "Microsoft Outlook");
+            var track = (Border)((DockPanel)row.Child).Children[0];
+            return ((SolidColorBrush)track.Background).Color;
+        });
+        Assert.NotEqual(Theme.NightRaised, offTrack); // the card's own colour would hide an off switch
+
+        var at = texts.IndexOf("In Efficiency mode while you were away");
+        Assert.Equal(["In Efficiency mode while you were away", "Leave alone", "Microsoft Outlook", "WhatsApp", "Battery"],
+            texts.Skip(at).Take(5));
+        Assert.Equal(["olk True", "whatsapp.root False"], calls);
+    }
+
     [Fact]
     public void The_readme_renders_write_the_three_panels_and_settings_at_twice_the_size()
     {
         var paths = Sta.Run(() => AssetRenderer.WritePanels(_dir));
```

Modify `tests/Nightcrew.Tests/SettingsModelTests.cs`:

```diff
diff --git a/tests/Nightcrew.Tests/SettingsModelTests.cs b/tests/Nightcrew.Tests/SettingsModelTests.cs
index 9d5ec9a..80cb2d9 100644
--- a/tests/Nightcrew.Tests/SettingsModelTests.cs
+++ b/tests/Nightcrew.Tests/SettingsModelTests.cs
@@ -4,8 +4,20 @@ using Xunit;
 namespace Nightcrew.Tests;
 
 public sealed class SettingsModelTests
 {
+    [Fact]
+    public void Efficiency_mode_is_on_by_default_and_the_leave_alone_list_is_sorted()
+    {
+        Assert.True(SettingsModel.From(new Settings()).EfficiencyWhileAway);
+        Assert.Empty(SettingsModel.From(new Settings()).LeaveAlone!);
+
+        var page = SettingsModel.From(new Settings { EfficiencyWhileAway = false, LeaveAlone = ["whatsapp.root", "Olk", "discord"] });
+
+        Assert.False(page.EfficiencyWhileAway);
+        Assert.Equal(["discord", "Olk", "whatsapp.root"], page.LeaveAlone);
+    }
+
     [Fact]
     public void Defaults_select_20_percent_8_hours_and_the_lantern()
     {
         var page = SettingsModel.From(new Settings());
```

Modify `tests/Nightcrew.Tests/SettingsPageTests.cs`:

```diff
diff --git a/tests/Nightcrew.Tests/SettingsPageTests.cs b/tests/Nightcrew.Tests/SettingsPageTests.cs
index 0c4221b..63af9d8 100644
--- a/tests/Nightcrew.Tests/SettingsPageTests.cs
+++ b/tests/Nightcrew.Tests/SettingsPageTests.cs
@@ -23,9 +23,9 @@ public sealed class SettingsPageTests
         [
             "‹", "Settings",
             "Battery floor", "10%", "15%", "20%", "30%", "Off",
             "Lid-closed limit", "2 h", "4 h", "8 h", "12 h", "Off",
-            "Lock when lid closes", "Start at login",
+            "Lock when lid closes", "Start at login", "Efficiency mode while away",
             "Tray mark", "Lantern", "Moon and star",
             "Open log", "Quit Nightcrew",
         ], texts);
     }
@@ -76,8 +76,40 @@ public sealed class SettingsPageTests
 
         Assert.Equal(["floor 30", "limit 0", "lock False", "login False", "mark MoonAndStar", "log", "quit", "back"], calls);
     }
 
+    [Fact]
+    public void The_efficiency_switch_and_each_cross_report_back()
+    {
+        var page = SettingsModel.From(new Settings { LeaveAlone = ["whatsapp.root", "olk"] });
+        var calls = Sta.Run(() =>
+        {
+            var calls = new List<string>();
+            var root = PanelVisual.BuildSettings(page, Actions() with
+            {
+                EfficiencyWhileAway = on => calls.Add($"efficiency {on}"),
+                RemoveLeaveAlone = name => calls.Add($"remove {name}"),
+            });
+            Click(Named<FrameworkElement>(root, "Efficiency mode while away"));
+            Click(Named<FrameworkElement>(root, "Remove olk"));
+            return calls;
+        });
+
+        Assert.Equal(["efficiency False", "remove olk"], calls);
+    }
+
+    [Fact]
+    public void The_always_leave_alone_list_shows_only_when_it_has_apps()
+    {
+        var (empty, listed) = Sta.Run(() => (
+            Texts(PanelVisual.BuildSettings(Defaults, null)),
+            Texts(PanelVisual.BuildSettings(SettingsModel.From(new Settings { LeaveAlone = ["whatsapp.root", "olk"] }), null))));
+
+        Assert.DoesNotContain("Always leave alone", empty);
+        var at = listed.IndexOf("Always leave alone");
+        Assert.Equal(["Always leave alone", "olk", "×", "whatsapp.root", "×"], listed.Skip(at).Take(5));
+    }
+
     [Fact]
     public void The_page_is_as_wide_as_the_panel()
     {
         var width = Sta.Run(() => AssetRenderer.Render(PanelVisual.BuildSettings(Defaults, null)).PixelWidth);
```

- [ ] **Step 2: Run the tests and watch them fail**

```powershell
dotnet test -c Release --no-build -- --filter-class Nightcrew.Tests.PanelModelTests
dotnet test -c Release --no-build -- --filter-class Nightcrew.Tests.SettingsModelTests
dotnet test -c Release --no-build -- --filter-class Nightcrew.Tests.SettingsPageTests
dotnet test -c Release --no-build -- --filter-class Nightcrew.Tests.PanelVisualTests
```

Expected: The new tests in `PanelModelTests`, `SettingsModelTests`, `SettingsPageTests` and `PanelVisualTests` fail to compile until the records and actions gain their new members. Add just those members from step 3 first (AwayRow, PanelView.Away, SettingsPage.EfficiencyWhileAway and LeaveAlone, PanelActions.LeaveAlone, SettingsActions.EfficiencyWhileAway and RemoveLeaveAlone, the leaveAlone parameter of PanelModel.From); then the new tests fail on their assertions.

- [ ] **Step 3: Implement**

Modify `src/Nightcrew/App/TrayApp.cs`:

```diff
diff --git a/src/Nightcrew/App/TrayApp.cs b/src/Nightcrew/App/TrayApp.cs
index d9c3c35..28e4c7d 100644
--- a/src/Nightcrew/App/TrayApp.cs
+++ b/src/Nightcrew/App/TrayApp.cs
@@ -27,17 +27,27 @@ public sealed class TrayApp : ApplicationContext
     private readonly SynchronizationContext _ui;
     private readonly TimeSpan _period;
     private readonly System.Threading.Timer _timer;
     private readonly bool _watchdogMissing;
+    private readonly IEfficiencyMode _efficiency;
+    private readonly Func<TimeSpan> _idleTime;
+    private readonly System.Threading.Timer _inputTimer;
+    private static readonly TimeSpan InputCheck = TimeSpan.FromSeconds(1);
     private EngineSnapshot _snapshot = EngineSnapshot.Initial; // UI thread
     private PanelView? _view; // UI thread
     private bool _closed;
     private bool _sessionEnding;
 
-    /// <summary>Subscribes to <paramref name="events"/> but does not own it; the caller disposes it after this.</summary>
+    /// <summary>
+    /// Subscribes to <paramref name="events"/> but does not own it; the caller disposes it after this. <paramref name="efficiency"/>
+    /// is called directly, without the lock, whenever apps must come out of Efficiency mode at once.
+    /// </summary>
     public TrayApp(Settings settings, bool dryRun, string exePath, PowerEventsWindow events,
-        ActivityMonitor activity, Controller controller, Engine engine, ILog log)
+        ActivityMonitor activity, Controller controller, Engine engine, ILog log, IEfficiencyMode efficiency, Func<TimeSpan> idleTime)
     {
+        _efficiency = efficiency;
+        _idleTime = idleTime;
+        _inputTimer = new System.Threading.Timer(_ => CheckInput(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
         _settings = settings;
         _dryRun = dryRun;
         _exePath = exePath;
         _activity = activity;
@@ -49,18 +59,21 @@ public sealed class TrayApp : ApplicationContext
         _panel = new PanelWindow(
             new PanelActions(
                 Hold: () => SetHold(!_snapshot.Hold),
                 Pause: () => SetPaused(!_snapshot.Paused),
-                Settings: () => _panel!.ShowPage(settings: true)),
+                Settings: () => _panel!.ShowPage(settings: true),
+                LeaveAlone: SetLeaveAlone),
             new SettingsActions(
                 Back: () => _panel!.ShowPage(settings: false),
                 BatteryFloor: value => ChangeSetting("Battery floor", () => _settings.BatteryFloorPercent = value),
                 LidClosedLimit: value => ChangeSetting("Lid-closed limit", () => _settings.LidClosedCapHours = value),
                 LockOnLidClose: on => ChangeSetting("Lock when lid closes", () => _settings.LockOnLidClose = on),
                 StartAtLogin: SetStartAtLogin,
                 Mark: kind => ChangeSetting("Tray mark", () => _settings.Mark = MarkKinds.Name(kind)),
                 OpenLog: OpenLog,
-                Quit: Quit));
+                Quit: Quit,
+                EfficiencyWhileAway: on => ChangeSetting("Efficiency mode while away", () => _settings.EfficiencyWhileAway = on),
+                RemoveLeaveAlone: name => SetLeaveAlone(name, false)));
         UiSafety.Install(_panel.Dispatcher, log);
 
         if (!dryRun)
         {
@@ -74,11 +87,21 @@ public sealed class TrayApp : ApplicationContext
         Show(_engine.Snapshot);
         _tray.Visible = true;
 
         engine.Lid = events.Lid;
-        events.LidChanged += lid => Apply("Lid change", () => engine.Lid = lid);
-        events.Suspending += engine.NoteSuspend; // lock-free: the timer thread may hold the gate inside SetSuspendState
+        events.LidChanged += lid =>
+        {
+            if (lid != LidState.Closed) efficiency.RestoreAll("lid opened"); // at once, not after the tick
+            Apply("Lid change", () => engine.Lid = lid);
+        };
+        events.Suspending += () =>
+        {
+            // Lock-free: the timer thread may hold the gate inside SetSuspendState. Efficiency mode has its own lock.
+            efficiency.RestoreAll("suspend");
+            engine.NoteSuspend();
+        };
         events.Resumed += () => Apply("Resume", controller.OnResume);
+        events.SessionEnding += () => efficiency.RestoreAll("session end");
         events.SessionEnding += () => Guarded("Restore at session end", () =>
         {
             _sessionEnding = true; // no tick may re-arm while Windows ends the session
             engine.RestoreNow();
@@ -114,8 +137,11 @@ public sealed class TrayApp : ApplicationContext
         {
             if (_closed || _sessionEnding) return;
             _engine.Tick();
             var snapshot = _engine.Snapshot;
+            // Check for keyboard and mouse input every second, but only while apps are in Efficiency mode.
+            var check = snapshot.EfficiencyApps > 0 ? InputCheck : Timeout.InfiniteTimeSpan;
+            _inputTimer.Change(check, check);
             _ui.Post(_ => Show(snapshot), null);
         }
         catch (Exception ex)
         {
@@ -133,9 +159,9 @@ public sealed class TrayApp : ApplicationContext
         if (_closed) return;
         try
         {
             _snapshot = snapshot;
-            var view = PanelModel.From(snapshot, _settings.BatteryFloorPercent, _watchdogMissing);
+            var view = PanelModel.From(snapshot, _settings.BatteryFloorPercent, _watchdogMissing, _settings.LeaveAlone);
             _view = view;
             var tooltip = _dryRun ? "[dry run] " + view.Tooltip : view.Tooltip;
             _tray.Text = tooltip.Length > PanelModel.TooltipLimit ? tooltip.Substring(0, PanelModel.TooltipLimit) : tooltip;
             _tray.Icon = _icons.Get(Mark, view.Mark, SystemInformation.SmallIconSize.Width, TaskbarTheme.IsLight());
@@ -215,9 +241,29 @@ public sealed class TrayApp : ApplicationContext
     private void OpenLog() => Guarded("Open log", () => Process.Start(AppPaths.LogFile)?.Dispose());
 
     private void SetHold(bool on) => Apply("Hold awake", () => _activity.Hold = on);
 
-    private void SetPaused(bool on) => Apply("Pause", () => _engine.Paused = on);
+    private void SetPaused(bool on)
+    {
+        if (on) _efficiency.RestoreAll("pause");
+        Apply("Pause", () => _engine.Paused = on);
+    }
+
+    /// <summary>Adds the app's root process name to the Always leave alone list, or takes it off.</summary>
+    private void SetLeaveAlone(string rootName, bool on) => ChangeSetting("Leave alone", () =>
+    {
+        var names = _settings.LeaveAlone.Where(name => !string.Equals(name, rootName, StringComparison.OrdinalIgnoreCase)).ToList();
+        if (on) names.Add(rootName);
+        _settings.LeaveAlone = names.ToArray();
+    });
+
+    /// <summary>Timer thread, every second while apps are in Efficiency mode: any input brings them back at once.</summary>
+    private void CheckInput()
+    {
+        if (_closed || _idleTime() >= TimeSpan.FromSeconds(2)) return;
+        _inputTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
+        if (_efficiency.RestoreAll("input") > 0 && !_closed) _timer.Change(TimeSpan.Zero, _period);
+    }
 
     /// <summary>The Run key first: if writing it fails, the setting stays as it was and nothing is saved.</summary>
     private void SetStartAtLogin(bool on) => ChangeSetting("Start at login", () =>
     {
@@ -279,8 +325,10 @@ public sealed class TrayApp : ApplicationContext
     /// <summary>Stops ticking and puts the owner's power settings back, once.</summary>
     private void Shutdown()
     {
         _timer.Dispose();
+        _inputTimer.Dispose();
+        _efficiency.RestoreAll("quit");
         Guarded("Restore on exit", () =>
         {
             if (_closed) return;
             _closed = true;
```

Modify `src/Nightcrew/App/Ui/PanelVisual.cs`:

```diff
diff --git a/src/Nightcrew/App/Ui/PanelVisual.cs b/src/Nightcrew/App/Ui/PanelVisual.cs
index d641105..083c9c2 100644
--- a/src/Nightcrew/App/Ui/PanelVisual.cs
+++ b/src/Nightcrew/App/Ui/PanelVisual.cs
@@ -9,14 +9,18 @@ using System.Windows.Media.Effects;
 using Nightcrew.Core;
 
 namespace Nightcrew.App.Ui;
 
-/// <summary>What the panel's three controls do. Null when the panel is only drawn, as for the README.</summary>
-public sealed record PanelActions(Action Hold, Action Pause, Action Settings);
+/// <summary>
+/// What the panel's controls do. Null when the panel is only drawn, as for the README. <paramref name="LeaveAlone"/> gets an
+/// app's root process name and whether it should now be left alone.
+/// </summary>
+public sealed record PanelActions(Action Hold, Action Pause, Action Settings, Action<string, bool>? LeaveAlone = null);
 
 /// <summary>What the settings page's controls do: each setter gets the new value. Null when the page is only drawn.</summary>
 public sealed record SettingsActions(Action Back, Action<int> BatteryFloor, Action<int> LidClosedLimit,
-    Action<bool> LockOnLidClose, Action<bool> StartAtLogin, Action<MarkKind> Mark, Action OpenLog, Action Quit);
+    Action<bool> LockOnLidClose, Action<bool> StartAtLogin, Action<MarkKind> Mark, Action OpenLog, Action Quit,
+    Action<bool>? EfficiencyWhileAway = null, Action<string>? RemoveLeaveAlone = null);
 
 /// <summary>Builds the click panel from a <see cref="PanelView"/>. The same tree is shown in the window and rendered for the README.</summary>
 public static class PanelVisual
 {
@@ -34,8 +38,9 @@ public static class PanelVisual
         body.Children.Add(Header(mark, view.Mark, actions));
         body.Children.Add(Headline(view));
         body.Children.Add(Text(view.SubLine, 12.5, Theme.MoonMuted, bottom: 16, lineHeight: 18));
         if (view.Holders.Count > 0) body.Children.Add(Holders(view.Holders));
+        if (view.Away is { Count: > 0 } away) body.Children.Add(AwayCard(away, actions?.LeaveAlone));
         body.Children.Add(Facts(view.Facts));
         body.Children.Add(Buttons(view, actions));
         if (view.Warning is { } warning) body.Children.Add(WarningRow(warning));
         return Frame(body);
@@ -53,9 +58,16 @@ public static class PanelVisual
         body.Children.Add(limit);
         body.Children.Add(Segments("Lid-closed limit", page.LidClosedLimit, actions?.LidClosedLimit));
         body.Children.Add(Hairline());
         body.Children.Add(SwitchRow("Lock when lid closes", page.LockOnLidClose, actions?.LockOnLidClose, bottom: 12));
-        body.Children.Add(SwitchRow("Start at login", page.StartAtLogin, actions?.StartAtLogin, bottom: 0));
+        body.Children.Add(SwitchRow("Start at login", page.StartAtLogin, actions?.StartAtLogin, bottom: 12));
+        body.Children.Add(SwitchRow("Efficiency mode while away", page.EfficiencyWhileAway, actions?.EfficiencyWhileAway, bottom: 0));
+        if (page.LeaveAlone is { Count: > 0 } kept)
+        {
+            body.Children.Add(Hairline());
+            body.Children.Add(Text("Always leave alone", 12.5, Theme.Moon, bottom: 4));
+            foreach (var name in kept) body.Children.Add(KeptRow(name, actions?.RemoveLeaveAlone));
+        }
         body.Children.Add(Hairline());
         body.Children.Add(Text("Tray mark", 12.5, Theme.Moon));
         body.Children.Add(Segments("Tray mark", page.Mark, actions?.Mark));
         body.Children.Add(Hairline());
@@ -140,10 +152,13 @@ public static class PanelVisual
             Child = cells,
         };
     }
 
-    /// <summary>A label with an on/off switch; clicking anywhere on the row flips it.</summary>
-    private static Border SwitchRow(string label, bool on, Action<bool>? set, double bottom)
+    /// <summary>
+    /// A label with an on/off switch; clicking anywhere on the row flips it. On a raised card, pass a lighter
+    /// <paramref name="offTrack"/> so an off switch's track still shows.
+    /// </summary>
+    private static Border SwitchRow(string label, bool on, Action<bool>? set, double bottom, Color? offTrack = null)
     {
         var knob = new System.Windows.Shapes.Ellipse
         {
             Width = 12,
@@ -152,9 +167,9 @@ public static class PanelVisual
             Margin = new Thickness(3),
             HorizontalAlignment = on ? HorizontalAlignment.Right : HorizontalAlignment.Left,
             VerticalAlignment = VerticalAlignment.Center,
         };
-        var track = new Border { Width = 30, Height = 18, CornerRadius = new CornerRadius(9), Background = Theme.Brush(on ? Theme.Lavender : Theme.NightRaised), Child = knob };
+        var track = new Border { Width = 30, Height = 18, CornerRadius = new CornerRadius(9), Background = Theme.Brush(on ? Theme.Lavender : offTrack ?? Theme.NightRaised), Child = knob };
         var dock = new DockPanel();
         DockPanel.SetDock(track, Dock.Right);
         dock.Children.Add(track);
         var text = Text(label, 12.5, Theme.Moon);
@@ -222,8 +237,50 @@ public static class PanelVisual
             Child = rows,
         };
     }
 
+    /// <summary>The apps from the most recent time away, each with a Leave alone switch.</summary>
+    private static Border AwayCard(IReadOnlyList<AwayRow> apps, Action<string, bool>? leaveAlone)
+    {
+        var rows = new StackPanel();
+        rows.Children.Add(Text("In Efficiency mode while you were away", 11, Theme.MoonFaint, bottom: 2));
+        var column = Text("Leave alone", 11, Theme.MoonFaint, bottom: 6);
+        column.HorizontalAlignment = HorizontalAlignment.Right;
+        rows.Children.Add(column);
+        for (var i = 0; i < apps.Count; i++)
+        {
+            var app = apps[i];
+            Action<bool>? set = leaveAlone is null ? null : on => leaveAlone(app.RootName, on);
+            rows.Children.Add(SwitchRow(app.DisplayName, app.LeftAlone, set, bottom: i == apps.Count - 1 ? 0 : 8, offTrack: Theme.NightHover));
+        }
+        return new Border
+        {
+            Background = Theme.Brush(Theme.NightRaised),
+            CornerRadius = new CornerRadius(8),
+            Padding = new Thickness(12, 10, 12, 10),
+            Margin = new Thickness(0, 0, 0, 12),
+            Child = rows,
+        };
+    }
+
+    /// <summary>A name on the Always leave alone list, then the cross that takes it off, in reading order.</summary>
+    private static Grid KeptRow(string name, Action<string>? remove)
+    {
+        var row = new Grid { Margin = new Thickness(0, 4, 0, 0) };
+        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
+        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
+        var label = Text(name, 12.5, Theme.MoonMuted);
+        label.VerticalAlignment = VerticalAlignment.Center;
+        row.Children.Add(label);
+        var cross = Text("\u00D7", 14, Theme.MoonFaint);
+        cross.VerticalAlignment = VerticalAlignment.Center;
+        AutomationProperties.SetName(cross, "Remove " + name);
+        if (remove is not null) Clickable(cross, () => remove(name), Theme.MoonFaint, Theme.Moon);
+        Grid.SetColumn(cross, 1);
+        row.Children.Add(cross);
+        return row;
+    }
+
     private static StackPanel Facts(IReadOnlyList<PanelFact> facts)
     {
         var list = new StackPanel();
         foreach (var fact in facts)
```

Modify `src/Nightcrew/Core/PanelModel.cs`:

```diff
diff --git a/src/Nightcrew/Core/PanelModel.cs b/src/Nightcrew/Core/PanelModel.cs
index 3444893..074e957 100644
--- a/src/Nightcrew/Core/PanelModel.cs
+++ b/src/Nightcrew/Core/PanelModel.cs
@@ -6,8 +6,11 @@ public enum PanelState { Quiet, Working, Paused }
 public enum MarkState { Idle, Live, LidClosed }
 
 public sealed record PanelFact(string Label, string Value);
 
+/// <summary>An app from the most recent time away, and whether it's on the Always leave alone list.</summary>
+public sealed record AwayRow(string DisplayName, string RootName, bool LeftAlone);
+
 /// <summary>Everything the click panel and the tray tooltip show, as plain strings.</summary>
 public sealed record PanelView(
     PanelState State,
     string Headline,
@@ -18,16 +21,28 @@ public sealed record PanelView(
     bool HoldOn,
     string PauseLabel,
     string? Warning,
     MarkState Mark,
-    string Tooltip);
+    string Tooltip,
+    IReadOnlyList<AwayRow>? Away = null);
 
 /// <summary>Turns an engine snapshot into the panel's words. Pure, so every string is unit-tested.</summary>
 public static class PanelModel
 {
     public const int TooltipLimit = 63;
 
-    public static PanelView From(EngineSnapshot snapshot, int batteryFloorPercent, bool watchdogMissing = false)
+    /// <param name="leaveAlone">The Always leave alone list, to show which apps from the time away are on it.</param>
+    public static PanelView From(EngineSnapshot snapshot, int batteryFloorPercent, bool watchdogMissing = false,
+        IReadOnlyCollection<string>? leaveAlone = null)
+    {
+        var view = MainPage(snapshot, batteryFloorPercent, watchdogMissing);
+        // Once you're back: the lid is open and the time away has ended.
+        if (snapshot.Lid == LidState.Closed || snapshot.Away is not { Count: > 0 } away) return view;
+        var kept = new HashSet<string>(leaveAlone ?? [], StringComparer.OrdinalIgnoreCase);
+        return view with { Away = away.Select(app => new AwayRow(app.DisplayName, app.RootName, kept.Contains(app.RootName))).ToList() };
+    }
+
+    private static PanelView MainPage(EngineSnapshot snapshot, int batteryFloorPercent, bool watchdogMissing)
     {
         var warning = WarningText(snapshot.Warning, watchdogMissing);
         var battery = new PanelFact("Battery", Battery(snapshot, batteryFloorPercent));
```

Modify `src/Nightcrew/Core/SettingsModel.cs`:

```diff
diff --git a/src/Nightcrew/Core/SettingsModel.cs b/src/Nightcrew/Core/SettingsModel.cs
index 918c221..4e3e306 100644
--- a/src/Nightcrew/Core/SettingsModel.cs
+++ b/src/Nightcrew/Core/SettingsModel.cs
@@ -8,9 +8,11 @@ public sealed record SettingsPage(
     IReadOnlyList<Choice<int>> BatteryFloor,
     IReadOnlyList<Choice<int>> LidClosedLimit,
     bool LockOnLidClose,
     bool StartAtLogin,
-    IReadOnlyList<Choice<MarkKind>> Mark);
+    IReadOnlyList<Choice<MarkKind>> Mark,
+    bool EfficiencyWhileAway = true,
+    IReadOnlyList<string>? LeaveAlone = null);
 
 /// <summary>Turns the settings into the settings page's choices. Pure, so the page's words are unit-tested.</summary>
 public static class SettingsModel
 {
@@ -24,9 +26,11 @@ public static class SettingsModel
             Choices(BatteryFloors, settings.BatteryFloorPercent, value => value + "%"),
             Choices(LidClosedLimits, settings.LidClosedCapHours, value => value + " h"),
             settings.LockOnLidClose,
             settings.StartAtLogin,
-            [new("Lantern", MarkKind.Lantern, mark == MarkKind.Lantern), new("Moon and star", MarkKind.MoonAndStar, mark == MarkKind.MoonAndStar)]);
+            [new("Lantern", MarkKind.Lantern, mark == MarkKind.Lantern), new("Moon and star", MarkKind.MoonAndStar, mark == MarkKind.MoonAndStar)],
+            settings.EfficiencyWhileAway,
+            settings.LeaveAlone.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList());
     }
 
     /// <summary>
     /// The usual values then Off (0). A value set by hand in settings.json gets its own choice, in order; zero or less
```

Modify `src/Nightcrew/Program.cs`:

```diff
diff --git a/src/Nightcrew/Program.cs b/src/Nightcrew/Program.cs
index c9b9107..dd2a8ca 100644
--- a/src/Nightcrew/Program.cs
+++ b/src/Nightcrew/Program.cs
@@ -60,8 +60,9 @@ internal static class Program
 
         try
         {
             var restored = new RestoreJournal(new PowrProfPolicy(), AppPaths.RestoreFile, log).Restore();
+            new EfficiencyMode(AppPaths.EfficiencyFile, log).RestoreAll("uninstall"); // the watchdog that would do it is going
             Autostart.Set(false, exePath);
             WatchdogTask.Unregister(log);
             log.Info("Uninstalled: start at login and the watchdog task removed");
             MessageBox.Show(
@@ -98,8 +99,12 @@ internal static class Program
 
         // The panel is small and rarely open; GPU rendering would keep about 75 MB allocated for the rest of the session.
         System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
 
+        // Apps a crash left in Efficiency mode go back first, with the real class even in a dry run.
+        new EfficiencyMode(AppPaths.EfficiencyFile, log).RestoreAll("startup");
+        IEfficiencyMode efficiency = dryRun ? new DryRunEfficiency(log) : new EfficiencyMode(AppPaths.EfficiencyFile, log);
+
         var clock = new SystemClock();
         using var events = new PowerEventsWindow();
         using var realKeepAwake = new KeepAwake();
         using var gpu = new PdhGpuUsage();
@@ -110,19 +115,22 @@ internal static class Program
         IKeepAwake keepAwake = dryRun ? new DryRunKeepAwake(log) : realKeepAwake;
         var journal = new RestoreJournal(policy, AppPaths.RestoreFile, log);
 
         var grace = TimeSpan.FromMinutes(settings.GraceMinutes);
+        var processes = new TickProcessTable(new ToolhelpProcessTable(), clock); // one list per tick, shared
+        var gpuSignal = new GpuSignal(gpu, settings.GpuPercent, ProcessName);
         IBusySignal[] signals =
         [
             new TranscriptSignal(transcripts, settings.TranscriptRoots, settings.IgnorePathContains, grace),
-            new AgentTreeSignal(new ToolhelpProcessTable(), settings),
-            new GpuSignal(gpu, settings.GpuPercent, ProcessName),
+            new AgentTreeSignal(processes, settings),
+            gpuSignal,
         ];
         var activity = new ActivityMonitor(signals, grace);
         var controller = new Controller(settings);
-        var engine = new Engine(activity, controller, journal, policy, keepAwake, power, log, clock);
+        var coordinator = new EfficiencyCoordinator(efficiency, new EfficiencySource(processes), settings, () => gpuSignal.BusyPids, log);
+        var engine = new Engine(activity, controller, journal, policy, keepAwake, power, log, clock, coordinator);
 
-        using var tray = new TrayApp(settings, dryRun, exePath, events, activity, controller, engine, log);
+        using var tray = new TrayApp(settings, dryRun, exePath, events, activity, controller, engine, log, efficiency, () => realPower.IdleTime);
         Application.Run(tray);
     }
 
     /// <summary>For the panel's holder rows; null when the process has already gone.</summary>
```

- [ ] **Step 4: Run the tests and watch them pass, then the whole suite**

```powershell
dotnet build -c Release
dotnet test -c Release --no-build -- --filter-class Nightcrew.Tests.PanelModelTests
dotnet test -c Release --no-build -- --filter-class Nightcrew.Tests.SettingsModelTests
dotnet test -c Release --no-build -- --filter-class Nightcrew.Tests.SettingsPageTests
dotnet test -c Release --no-build -- --filter-class Nightcrew.Tests.PanelVisualTests
dotnet test -c Release --no-build
powershell -NoProfile -File build/check-single-file.ps1
```

Expected: all pass, 0 warnings, the single-file gate passes.

- [ ] **Step 5: Commit**

```powershell
git add src/Nightcrew/App/TrayApp.cs src/Nightcrew/App/Ui/PanelVisual.cs src/Nightcrew/Core/PanelModel.cs src/Nightcrew/Core/SettingsModel.cs src/Nightcrew/Program.cs tests/Nightcrew.Tests/PanelModelTests.cs tests/Nightcrew.Tests/PanelVisualTests.cs tests/Nightcrew.Tests/SettingsModelTests.cs tests/Nightcrew.Tests/SettingsPageTests.cs
git commit -m "feat: the Efficiency mode switch, the Always leave alone list and the away card; wire it all up" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

### Task 6: README and hardware checklist

Spec sections 6 (README) and 9 (the three checklist steps).

**Files:**
- Modify: `README.md`
- Modify: `docs/hardware-checklist.md`

**Interfaces:**
- Produces: README: a fourth reason up top, step 3 in How it works, the panel and settings lines, three settings.json rows, the uninstall line
- Produces: Checklist: Efficiency mode tools, steps 9–11, Uninstall renumbered to 12 and checking `efficiency.json`

- [ ] **Step 1: Make the changes**

Modify `README.md`:

```diff
diff --git a/README.md b/README.md
index 7f5f0e3..c6af84a 100644
--- a/README.md
+++ b/README.md
@@ -18,8 +18,10 @@ normally would.
 - **Only when needed.** With no agent working, closing the lid does what it
   always did.
 - **Leaves your settings alone.** It changes power settings only while an
   agent works and puts them back afterwards, even after a crash.
+- **Agents get the machine.** While you're away, apps they don't need go into
+  Efficiency mode, the way Task Manager does it.
 - **Light.** One 1.6 MB exe in the tray, using well under 1% of a CPU core.
 
 ## Install
 
@@ -42,11 +44,15 @@ build attestation for checking the download.
    started, or heavy GPU use. The lantern lights up.
 2. **You close the lid.** The laptop keeps running and locks itself. Nightcrew
    has saved your power settings, then set the lid to Do nothing and the sleep
    timer to Never.
-3. **The work ends.** Five minutes after the last sign of work, your settings
+3. **Other apps ease off.** Five minutes later, apps the agents don't need go
+   into Efficiency mode. Agents, what they're connected to, terminals,
+   editors, browsers with a Claude or Codex connector and games are left
+   alone. Everything's back to normal the moment you open the lid.
+4. **The work ends.** Five minutes after the last sign of work, your settings
    go back.
-4. **It sleeps.** Once nobody has touched the laptop for 5 minutes, it follows
+5. **It sleeps.** Once nobody has touched the laptop for 5 minutes, it follows
    your lid action: sleep, hibernate or nothing. If Windows hasn't gone to
    sleep 5 minutes later, Nightcrew hibernates it.
 
 **Safety limits.** With the lid closed, Nightcrew stops early and hibernates at
@@ -67,9 +73,12 @@ what happens when the work is done.
 - **Hold awake** keeps the laptop awake until you turn it off, for anything
   Nightcrew can't detect on its own.
 - **Pause** hands the lid back to Windows until you resume.
 - **Settings**, or right-click the lantern: battery floor, lid-closed limit,
-  lock on lid close, start at login and the tray mark.
+  lock on lid close, start at login, Efficiency mode while away, the apps to
+  always leave alone, and the tray mark.
+- After you've been away, the panel lists the apps that were in Efficiency
+  mode. Switch on **Leave alone** for any that should keep full speed.
 
 <br clear="right">
 
 ## Good to know
@@ -114,16 +123,19 @@ anything, run `nightcrew --dry-run` and read the log.
 | `ToolCpuPercent` | 10 | CPU threshold for agent tools, % of one core |
 | `GpuPercent` | 30 | GPU engine threshold, % |
 | `SampleSeconds` | 10 | Seconds between checks |
 | `Mark` | `lantern` | The tray mark: `lantern` or `moon` |
+| `EfficiencyWhileAway` | true | Put apps the agents don't need into Efficiency mode while you're away |
+| `LeaveAlone` | none | Process names never put into Efficiency mode |
+| `ConnectorHostNames` | `chrome-native-host`, `extension-host` | Browser connector helpers; their browser is left alone |
 
 </details>
 
 ## Uninstall
 
 Quit Nightcrew from the tray, then run `.\nightcrew.exe --uninstall` in its
-folder. That puts your settings back and removes the startup entry and the
-watchdog task. Then delete the exe, and `%LOCALAPPDATA%\Nightcrew` if you want
+folder. That puts your settings back, takes any app out of Efficiency mode,
+and removes the startup entry and the watchdog task. Then delete the exe, and `%LOCALAPPDATA%\Nightcrew` if you want
 the settings and log gone too.
 
 ## Credits
```

Modify `docs/hardware-checklist.md`:

````diff
diff --git a/docs/hardware-checklist.md b/docs/hardware-checklist.md
index f0abd48..67e8a9c 100644
--- a/docs/hardware-checklist.md
+++ b/docs/hardware-checklist.md
@@ -227,9 +227,77 @@ Last Result.
   values.
 - `restore.json` is gone.
 - The task's Last Result is 0.
 
-## 9. Uninstall
+## Efficiency mode tools
+
+**Priority log.** Open Notepad and leave it open. In a terminal (terminals are
+left alone, so this keeps running at full speed), delete
+`$env:TEMP\nightcrew-priority.txt` if it exists, then run:
+
+```powershell
+while ($true) { "{0:T} {1}" -f (Get-Date), (Get-Process notepad | Select-Object -First 1).PriorityClass | Add-Content $env:TEMP\nightcrew-priority.txt; Start-Sleep -Seconds 30 }
+```
+
+`Idle` means Notepad is in Efficiency mode; `Normal` means it's back.
+
+## 9. Efficiency mode, dry run
+
+**Do.** Open Notepad and a browser without the Claude or Codex extension. Run
+`.\nightcrew.exe --dry-run` from the exe's folder. Plug the laptop in, start
+a long agent task with N = 2 and close the lid. After 10 minutes, open the lid
+and quit Nightcrew from the tray.
+
+**Look at.** The log.
+
+**Pass.**
+
+- About 5 minutes after you closed the lid, the log shows `[dry run] would put
+  into Efficiency mode:` lines for Notepad and the browser.
+- A `Left alone:` line names the agent's app or terminal (`agent`), any
+  browser with a connector (`connector`), and apps on the built-in lists.
+- When you open the lid, the log shows `[dry run] would end Efficiency mode
+  for` the same number of apps, with `lid opened`.
+
+## 10. Efficiency mode on and off
+
+**Do.** Start Nightcrew normally and start the priority log. Plug the laptop
+in, start a long agent task with N = 2 and close the lid. After 15 minutes,
+open the lid and open the panel.
+
+**Look at.** The priority log, the log and the panel.
+
+**Pass.**
+
+- About 5 minutes after you closed the lid, the priority log turns `Idle` and
+  the log shows `Efficiency mode on: Notepad (notepad, 1 process)`.
+- The log shows `Efficiency mode off for` some apps with `lid opened` at the
+  moment you opened the lid, and the next priority log line says `Normal`.
+- The panel lists Notepad under "In Efficiency mode while you were away".
+  Turning on its **Leave alone** switch adds `notepad` under Settings >
+  Always leave alone; the × takes it off again.
+- `%LOCALAPPDATA%\Nightcrew\efficiency.json` is gone.
+
+## 11. Efficiency mode after a crash
+
+**Do.** Start Nightcrew normally and start the priority log. In a second
+terminal, run `Start-Sleep -Seconds 600; Stop-Process -Name nightcrew`, which
+ends Nightcrew in 10 minutes. Plug the laptop in, start a long agent task with
+N = 4 and close the lid. After 20 minutes, open the lid. Start Nightcrew again
+afterwards.
+
+**Look at.** The priority log, the log and `efficiency.json`.
+
+**Pass.**
+
+- The priority log turns `Idle` about 5 minutes after you closed the lid, and
+  back to `Normal` within 5 minutes of Nightcrew ending, before you open the
+  lid.
+- The log shows `Efficiency mode off for 1 app: watchdog` (or more apps, if
+  more were open).
+- `efficiency.json` is gone.
+
+## 12. Uninstall
 
 **Do.** Start Nightcrew normally. Start an agent task and wait until the
 settings check shows `0x00000000`. In Task Manager, end `nightcrew.exe`.
 Right away, before the watchdog's next run, run `.\nightcrew.exe --uninstall`
@@ -247,8 +315,8 @@ Also look at the settings check and `restore.json`.
 **Pass.**
 
 - Both queries report that the value or task can't be found.
 - The settings check shows your values.
-- `restore.json` is gone.
+- `restore.json` and `efficiency.json` are gone.
 
 When you're done, set your lid action back to what you normally use. To keep
 using Nightcrew, run `nightcrew.exe` again.
````

- [ ] **Step 2: Commit**

```powershell
git add README.md docs/hardware-checklist.md
git commit -m "docs: Efficiency mode while away in the README and the hardware checklist" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```
