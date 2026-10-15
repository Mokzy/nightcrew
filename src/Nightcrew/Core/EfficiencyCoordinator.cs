namespace Nightcrew.Core;

/// <summary>
/// Each tick: asks the gate, and while you're away re-plans and drives Efficiency mode. Apps that became needed go
/// back; new apps go in.
/// </summary>
public sealed class EfficiencyCoordinator(IEfficiencyMode efficiency, IEfficiencySource source, Settings settings,
    Func<IReadOnlyCollection<int>> holderPids, ILog log)
{
    private readonly HashSet<string> _deniedLogged = new(StringComparer.OrdinalIgnoreCase);
    private string? _loggedKept;

    /// <summary>True while you're away and Efficiency mode is wanted.</summary>
    public bool Active { get; private set; }

    public int AppCount => efficiency.Applied.Count;

    /// <summary>
    /// The apps that would go into Efficiency mode if you were away now, by name, for Settings' Add an app list.
    /// Plans only; changes nothing.
    /// </summary>
    public IReadOnlyList<NamedApp> WouldSlowNow() =>
        Plan().Apply
            .Select(family => new NamedApp(family.DisplayName, family.RootName))
            .GroupBy(app => app.RootName, StringComparer.OrdinalIgnoreCase).Select(same => same.First())
            .OrderBy(app => app.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

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
            _loggedKept = null;
        }

        var generation = efficiency.Generation;
        var plan = Plan();
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

    private EfficiencyPlan Plan() => new EfficiencyPlanner(settings, source.DisplayName).Plan(source.Take(holderPids()));

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
