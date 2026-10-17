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

    /// <summary>
    /// A child that idles for about 30 seconds at normal priority, and the start time that identifies it. A child
    /// inherits a below-normal parent's priority, as on a CI runner, and Nightcrew leaves a low priority alone.
    /// </summary>
    private (Process Process, ProcessRef Ref) StartChild()
    {
        var child = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 30 127.0.0.1 >nul") { UseShellExecute = false, CreateNoWindow = true })!;
        _children.Add(child);
        child.PriorityClass = ProcessPriorityClass.Normal;
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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessInformation(IntPtr process, int infoClass, ref ThrottlingState info, int size);

    private static bool ThrottledOf(Process process)
    {
        var (control, state) = MasksOf(process);
        return (control & 1) != 0 && (state & 1) != 0;
    }

    private static (uint Control, uint State) MasksOf(Process process)
    {
        var state = new ThrottlingState { Version = 1 };
        Assert.True(GetProcessInformation(process.Handle, 4, ref state, Marshal.SizeOf<ThrottlingState>()));
        return (state.ControlMask, state.StateMask);
    }

    /// <summary>
    /// A parent at normal priority that waits on its input and starts a ping only when <see cref="StartLateChild"/>
    /// closes it, so the ping surely starts after whatever the test did to the parent first. Comparing start times
    /// can't tell: net48's DateTime.UtcNow lags by up to a clock tick, and Windows stamps a new process precisely.
    /// </summary>
    private (Process Process, ProcessRef Ref) StartParent()
    {
        var parent = Process.Start(new ProcessStartInfo("cmd.exe", "/c set /p go=& ping -n 30 127.0.0.1 >nul")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true })!;
        _children.Add(parent);
        parent.PriorityClass = ProcessPriorityClass.Normal;
        Assert.True(new ToolhelpProcessTable().TryGetTimes(parent.Id, out var start, out _));
        return (parent, new ProcessRef(parent.Id, start));
    }

    /// <summary>Lets the parent start its ping, and waits up to 10 seconds for it.</summary>
    private (Process Process, ProcessRef Ref) StartLateChild(Process parent, ProcessRef parentRef)
    {
        parent.StandardInput.Close();
        var table = new ToolhelpProcessTable();
        for (var tries = 0; tries < 100; tries++)
        {
            foreach (var entry in table.List().Where(entry => entry.ParentPid == parent.Id && entry.Name == "ping"))
            {
                // A ping older than the parent belonged to an earlier process with the same pid.
                if (!table.TryGetTimes(entry.Pid, out var start, out _) || start <= parentRef.StartUtc) continue;
                var child = Process.GetProcessById(entry.Pid);
                _children.Add(child);
                return (child, new ProcessRef(entry.Pid, start));
            }
            Thread.Sleep(100);
        }
        throw new InvalidOperationException("The parent never started its ping.");
    }

    [Fact]
    public void A_child_started_while_its_parent_is_slowed_is_recorded_and_put_back()
    {
        var (parent, parentRef) = StartParent();
        var mode = Create();
        Assert.Equal(ApplyResult.Applied, mode.Apply(Family(parentRef), mode.Generation));
        var (child, childRef) = StartLateChild(parent, parentRef);
        Assert.Equal(ProcessPriorityClass.Idle, PriorityOf(child)); // Windows passed the parent's priority down
        Assert.False(ThrottledOf(child));                           // but not the power throttling

        mode.Apply(Family(parentRef, childRef), mode.Generation);

        var record = Json.Deserialize<EfficiencyRecord>(File.ReadAllText(RecordPath));
        Assert.Contains(record.Entries, entry => entry.Process == childRef && entry.Inherited && entry.OriginalPriority == 0x20u);
        mode.RestoreAll("test");
        Assert.Equal(ProcessPriorityClass.Normal, PriorityOf(child));
        Assert.Equal(ProcessPriorityClass.Normal, PriorityOf(parent));
        Assert.False(ThrottledOf(child));
    }

    [Fact]
    public void RestoreAll_sweeps_an_idle_child_that_was_never_in_a_family()
    {
        var (parent, parentRef) = StartParent();
        var mode = Create();
        Assert.Equal(ApplyResult.Applied, mode.Apply(Family(parentRef), mode.Generation));
        var (child, _) = StartLateChild(parent, parentRef);
        Assert.Equal(ProcessPriorityClass.Idle, PriorityOf(child));

        mode.RestoreAll("test");

        Assert.Equal(ProcessPriorityClass.Normal, PriorityOf(child));
        Assert.False(ThrottledOf(child));
    }

    /// <summary>As a Chromium renderer does in the background: idle priority plus throttling, chosen by the app itself.</summary>
    private static void ChooseOwnEfficiency(Process process)
    {
        var own = new ThrottlingState { Version = 1, ControlMask = 1, StateMask = 1 };
        Assert.True(SetProcessInformation(process.Handle, 4, ref own, Marshal.SizeOf<ThrottlingState>()));
    }

    [Fact]
    public void A_child_that_chose_its_own_throttling_is_left_alone_by_the_sweep()
    {
        var (parent, parentRef) = StartParent();
        var mode = Create();
        Assert.Equal(ApplyResult.Applied, mode.Apply(Family(parentRef), mode.Generation));
        var (child, _) = StartLateChild(parent, parentRef);
        ChooseOwnEfficiency(child);

        mode.RestoreAll("test");

        Assert.Equal(ProcessPriorityClass.Idle, PriorityOf(child));
        Assert.True(ThrottledOf(child));
    }

    [Fact]
    public void A_child_that_chose_its_own_throttling_is_not_recorded_as_inherited()
    {
        var (parent, parentRef) = StartParent();
        var mode = Create();
        Assert.Equal(ApplyResult.Applied, mode.Apply(Family(parentRef), mode.Generation));
        var (child, childRef) = StartLateChild(parent, parentRef);
        ChooseOwnEfficiency(child);

        mode.Apply(Family(parentRef, childRef), mode.Generation);

        var record = Json.Deserialize<EfficiencyRecord>(File.ReadAllText(RecordPath));
        Assert.DoesNotContain(record.Entries, entry => entry.Process == childRef);
    }

    [Fact]
    public void A_process_that_was_idle_before_efficiency_mode_is_left_alone_by_the_sweep()
    {
        var (bystander, _) = StartChild();
        bystander.PriorityClass = ProcessPriorityClass.Idle;
        var (_, parentRef) = StartParent();
        var mode = Create();
        mode.Apply(Family(parentRef), mode.Generation);

        mode.RestoreAll("test");

        Assert.Equal(ProcessPriorityClass.Idle, PriorityOf(bystander));
    }

    [Fact]
    public void The_watchdog_sweep_works_from_the_record_alone()
    {
        var (parent, parentRef) = StartParent();
        Assert.Equal(ApplyResult.Applied, Create().Apply(Family(parentRef), 0)); // as if Nightcrew then crashed
        var (child, _) = StartLateChild(parent, parentRef);
        Assert.Equal(ProcessPriorityClass.Idle, PriorityOf(child));

        Create().RestoreAll("watchdog");

        Assert.Equal(ProcessPriorityClass.Normal, PriorityOf(child));
    }

    [Fact]
    public void A_process_with_any_throttling_choice_of_its_own_is_left_alone()
    {
        var (child, process) = StartChild();
        // The app opted out of timer-resolution throttling (PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION).
        var own = new ThrottlingState { Version = 1, ControlMask = 4, StateMask = 0 };
        Assert.True(SetProcessInformation(child.Handle, 4, ref own, Marshal.SizeOf<ThrottlingState>()));
        var mode = Create();

        Assert.Equal(ApplyResult.Unchanged, mode.Apply(Family(process), mode.Generation));

        Assert.Equal((4u, 0u), MasksOf(child));
        Assert.Equal(ProcessPriorityClass.Normal, PriorityOf(child));
        Assert.False(File.Exists(RecordPath));
    }

    [Fact]
    public void A_record_of_zero_bytes_from_a_power_cut_is_moved_aside_and_nothing_breaks()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(RecordPath, new byte[64]);

        var mode = Create();

        Assert.Equal(0, mode.RestoreAll("startup"));
        Assert.True(File.Exists(RecordPath + ".corrupt"));
        Assert.False(File.Exists(RecordPath));
    }

    [Fact]
    public void A_record_with_an_empty_entry_still_puts_back_the_rest()
    {
        var (child, process) = StartChild();
        Create().Apply(Family(process), 0);
        var record = File.ReadAllText(RecordPath);
        File.WriteAllText(RecordPath, record.Replace("\"Entries\":[", "\"Entries\":[null,"));

        Assert.Equal(1, Create().RestoreAll("startup"));

        Assert.Equal(ProcessPriorityClass.Normal, PriorityOf(child));
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
