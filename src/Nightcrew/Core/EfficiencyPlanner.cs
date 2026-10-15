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
