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
