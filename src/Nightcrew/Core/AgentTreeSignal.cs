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
public sealed class AgentTreeSignal(IProcessTable table, Settings settings) : IBusySignal
{
    private Dictionary<(int Pid, DateTime Start), (TimeSpan Cpu, TimeSpan At)> _baselines = [];

    public string Name => "agent-tools";

    /// <summary>The process that made the most recent busy sample, e.g. "python (pid 1234)".</summary>
    public string? LastBusyProcess { get; private set; }

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

        var busy = false;
        var next = new Dictionary<(int Pid, DateTime Start), (TimeSpan Cpu, TimeSpan At)>();
        foreach (var (process, threshold) in watched.Values)
        {
            if (TimesOf(process.Pid) is not { } current) continue;
            var key = (process.Pid, current.Start);
            next[key] = (current.Cpu, now);
            if (!_baselines.TryGetValue(key, out var last) || now <= last.At) continue;
            var percent = (current.Cpu - last.Cpu).TotalMilliseconds / (now - last.At).TotalMilliseconds * 100;
            if (busy || percent < threshold) continue;
            busy = true;
            LastBusyProcess = $"{process.Name} (pid {process.Pid})";
        }

        _baselines = next; // processes not seen this sample (exited or no longer watched) are dropped
        return busy;
    }
}
