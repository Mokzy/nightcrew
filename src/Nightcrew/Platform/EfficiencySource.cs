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
