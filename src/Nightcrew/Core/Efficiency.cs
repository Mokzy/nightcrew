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

/// <summary>Reads what the planner needs from Windows.</summary>
public interface IEfficiencySource
{
    EfficiencySnapshot Take(IReadOnlyCollection<int> holderPids);

    /// <summary>The name the panel shows for an app, such as "Microsoft Outlook" for olk.</summary>
    string DisplayName(ProcessInfo root);
}

/// <summary>An app that was in Efficiency mode while you were away, as the panel lists it.</summary>
public sealed record AwayApp(string DisplayName, string RootName);
