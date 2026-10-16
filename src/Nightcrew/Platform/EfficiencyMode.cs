using System.Runtime.InteropServices;
using Nightcrew.Core;

namespace Nightcrew.Platform;

/// <summary>
/// Windows' Efficiency mode as Task Manager applies it: power throttling (EcoQoS) plus idle priority. Every change is
/// written to a record first, so a later run, or the watchdog, can put it back after a crash. A process that already
/// made its own choice (a low priority, or any throttling setting) is left alone, and on the way back a priority
/// someone changed meanwhile is kept. Windows gives a process started by an idle parent the idle priority (but not the
/// throttling), so what a slowed app starts while slowed is recorded, or found on the way back, and put back too.
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
    private readonly IProcessTable _table;
    private readonly List<EfficiencyEntry> _entries;
    private int _generation;

    /// <summary>
    /// Loads what an earlier run left in the record, so <see cref="RestoreAll"/> puts that back too. <paramref name="table"/>
    /// lists processes for the sweep that finds processes a slowed app started; the real process list by default.
    /// </summary>
    public EfficiencyMode(string path, ILog log, IProcessTable? table = null)
    {
        _path = path;
        _log = log;
        _table = table ?? new ToolhelpProcessTable();
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
            var recorded = _entries.Where(entry => entry.Root == root).ToList();
            var appliedSince = recorded.Where(entry => entry.AppliedUtcTicks > 0).Select(entry => entry.AppliedUtcTicks).DefaultIfEmpty(0).Min();
            var rootPriority = (recorded.FirstOrDefault(entry => entry.Process == root) ?? recorded.FirstOrDefault())?.OriginalPriority;
            var now = DateTime.UtcNow.Ticks;
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
                    if (!StartedAt(handle, process.StartUtc))
                    {
                        CloseHandle(handle);
                        continue;
                    }
                    var entry = new EfficiencyEntry
                    {
                        Pid = process.Pid,
                        StartUtcTicks = process.StartUtc.Ticks,
                        RootPid = root.Pid,
                        RootStartUtcTicks = root.StartUtc.Ticks,
                        Name = family.DisplayName,
                        AppliedUtcTicks = now,
                    };
                    if (MadeItsOwnChoice(handle, out var priority))
                    {
                        // Idle, and started after we slowed its family: Windows passed the idle priority down. Record it
                        // with the family's own priority, so it comes back with the family, even after a crash. Windows
                        // doesn't pass throttling down, so a process with a throttling choice made it itself, as a
                        // background browser renderer does, and is left alone.
                        if (priority == IdlePriority && ControlMaskOf(handle) == 0
                            && appliedSince > 0 && process.StartUtc.Ticks > appliedSince && rootPriority is { } original)
                        {
                            entry.OriginalPriority = original;
                            entry.Inherited = true;
                            pending.Add((entry, handle));
                        }
                        else
                        {
                            CloseHandle(handle);
                        }
                        continue;
                    }
                    entry.OriginalPriority = priority;
                    pending.Add((entry, handle));
                }
                var wasOn = _entries.Any(entry => entry.Root == root);
                if (pending.Count == 0) return wasOn ? ApplyResult.Applied : ApplyResult.Unchanged;

                _entries.AddRange(pending.Select(item => item.Entry));
                if (!Save())
                {
                    _entries.RemoveAll(entry => pending.Any(item => item.Entry == entry));
                    return ApplyResult.Unchanged;
                }
                foreach (var (entry, handle) in pending)
                {
                    if (entry.Inherited) continue; // already idle, and not ours to slow further
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
            foreach (var entry in _entries.Concat(Sweep(_entries))) Put(entry);
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
        foreach (var entry in family.Concat(Sweep(family))) Put(entry);
        _entries.RemoveAll(family.Contains);
        Save();
    }

    /// <summary>
    /// Processes at idle priority that a recorded process started after it was slowed, and what those started in turn:
    /// Windows passed the idle priority down, so they come back with the family. Each one gets the priority of the
    /// recorded process it descends from. A live parent must still be the recorded process, not a reused pid.
    /// Never throws; a process list that can't be read finds nothing.
    /// </summary>
    private List<EfficiencyEntry> Sweep(IReadOnlyCollection<EfficiencyEntry> recorded)
    {
        var found = new List<EfficiencyEntry>();
        if (recorded.Count == 0) return found;
        IReadOnlyList<ProcessEntry> processes;
        try
        {
            processes = _table.List();
        }
        catch (Exception)
        {
            return found;
        }

        var seen = new HashSet<int>(recorded.Select(entry => entry.Pid)) { OwnPid };
        var parents = recorded.ToList();
        while (parents.Count > 0)
        {
            var next = new List<EfficiencyEntry>();
            foreach (var process in processes)
            {
                if (seen.Contains(process.Pid)) continue;
                var parent = parents.FirstOrDefault(entry => entry.Pid == process.ParentPid);
                if (parent is null || !_table.TryGetTimes(process.Pid, out var start, out _)) continue;
                var since = parent.AppliedUtcTicks > 0 ? parent.AppliedUtcTicks : parent.StartUtcTicks;
                if (start.Ticks <= since) continue;
                if (_table.TryGetTimes(parent.Pid, out var parentStart, out _) && parentStart.Ticks != parent.StartUtcTicks) continue;
                if (!IdleWithoutOwnThrottling(process.Pid, start)) continue;
                seen.Add(process.Pid);
                next.Add(new EfficiencyEntry
                {
                    Pid = process.Pid,
                    StartUtcTicks = start.Ticks,
                    OriginalPriority = parent.OriginalPriority,
                    RootPid = parent.RootPid,
                    RootStartUtcTicks = parent.RootStartUtcTicks,
                    Name = parent.Name,
                    AppliedUtcTicks = parent.AppliedUtcTicks,
                    Inherited = true,
                });
            }
            found.AddRange(next);
            parents = next;
        }
        return found;
    }

    /// <summary>
    /// True for the process that started at <paramref name="startUtc"/> when it is at idle priority with no throttling
    /// choice of its own: what Windows hands down from an idle parent. A process that set throttling chose for itself.
    /// </summary>
    private static bool IdleWithoutOwnThrottling(int pid, DateTime startUtc)
    {
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero) return false;
        try
        {
            return StartedAt(handle, startUtc) && GetPriorityClass(handle) == IdlePriority && ControlMaskOf(handle) == 0;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>The process's own throttling choices (the control mask); all bits set when it can't be read.</summary>
    private static uint ControlMaskOf(IntPtr handle)
    {
        var state = new ThrottlingState { Version = ThrottlingVersion };
        return GetProcessInformation(handle, ProcessPowerThrottling, ref state, StateSize) ? state.ControlMask : uint.MaxValue;
    }

    /// <summary>
    /// Clears throttling that's still on, and puts the priority back if it's still idle. An inherited process only gets
    /// its priority back: Nightcrew never throttled it, so any throttling it has is its own.
    /// </summary>
    private static void Put(EfficiencyEntry entry)
    {
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_SET_INFORMATION, false, entry.Pid);
        if (handle == IntPtr.Zero) return;
        try
        {
            if (!StartedAt(handle, entry.Process.StartUtc)) return;
            var state = new ThrottlingState { Version = ThrottlingVersion };
            var readable = GetProcessInformation(handle, ProcessPowerThrottling, ref state, StateSize);
            if (!entry.Inherited && (!readable || ((state.ControlMask & ExecutionSpeed) != 0 && (state.StateMask & ExecutionSpeed) != 0)))
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

    /// <summary>
    /// A low priority, any throttling setting of its own (any control bit, not just execution speed: setting the
    /// state replaces the whole mask), or a priority we can't read. So every process we touch started with Windows
    /// deciding its throttling, and restoring "Windows decides" puts it back exactly.
    /// </summary>
    private static bool MadeItsOwnChoice(IntPtr handle, out uint priority)
    {
        priority = GetPriorityClass(handle);
        if (priority is 0 or IdlePriority or BelowNormalPriority) return true;
        var state = new ThrottlingState { Version = ThrottlingVersion };
        return GetProcessInformation(handle, ProcessPowerThrottling, ref state, StateSize) && state.ControlMask != 0;
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

    /// <summary>
    /// The entries an earlier run left. Never throws: startup, the watchdog and uninstall all load the record, and a
    /// damaged one (a power cut can leave zero bytes) must not stop them. It's moved aside to .corrupt instead.
    /// </summary>
    private List<EfficiencyEntry> Load()
    {
        try
        {
            if (!File.Exists(_path)) return [];
            var entries = Json.Deserialize<EfficiencyRecord?>(File.ReadAllText(_path))?.Entries ?? [];
            return entries.Where(entry => entry is not null).ToList();
        }
        catch (Exception ex)
        {
            var aside = _path + ".corrupt";
            try
            {
                File.Delete(aside);
                File.Move(_path, aside);
                _log.Warn($"Efficiency mode record was unreadable, moved to {aside}: {ex.Message}");
            }
            catch (Exception moveFailed)
            {
                _log.Warn($"Efficiency mode record was unreadable ({ex.Message}) and couldn't be moved aside: {moveFailed.Message}");
            }
            return [];
        }
    }
}
