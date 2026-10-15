using Nightcrew.Core;

namespace Nightcrew.Platform;

/// <summary>Reads the real power settings; logs writes instead of making them.</summary>
public sealed class DryRunPolicy(IPowerPolicy real, ILog log) : IPowerPolicy
{
    public PowerValues Read() => real.Read();

    public void Write(PowerValues values) => log.Info($"[dry run] would write power settings {values}");

    public bool CanWrite(out string reason) => real.CanWrite(out reason);
}

/// <summary>Logs instead of holding a power request.</summary>
public sealed class DryRunKeepAwake(ILog log) : IKeepAwake
{
    public bool IsHeld { get; private set; }

    public void Hold()
    {
        IsHeld = true;
        log.Info("[dry run] would hold the keep-awake request");
    }

    public void Release()
    {
        IsHeld = false;
        log.Info("[dry run] would release the keep-awake request");
    }
}

/// <summary>Reads the real power state; logs instead of hibernating, turning the screen off or locking.</summary>
public sealed class DryRunSystemPower(ISystemPower real, ILog log) : ISystemPower
{
    public PowerSource Source => real.Source;

    public int? BatteryPercent => real.BatteryPercent;

    public TimeSpan IdleTime => real.IdleTime;

    public bool HibernateAvailable => real.HibernateAvailable;

    public bool Hibernate()
    {
        log.Info("[dry run] would hibernate");
        return true;
    }

    public void MonitorOff() => log.Info("[dry run] would turn the monitor off");

    public void LockWorkstation() => log.Info("[dry run] would lock the workstation");
}

/// <summary>Logs what Efficiency mode would do; changes no process.</summary>
public sealed class DryRunEfficiency(ILog log) : IEfficiencyMode
{
    private readonly object _lock = new();
    private readonly Dictionary<ProcessRef, string> _applied = [];
    private int _generation;

    public int Generation
    {
        get { lock (_lock) return _generation; }
    }

    public IReadOnlyCollection<ProcessRef> Applied
    {
        get { lock (_lock) return _applied.Keys.ToList(); }
    }

    public ApplyResult Apply(AppFamily family, int generation)
    {
        lock (_lock)
        {
            if (generation != _generation) return ApplyResult.Stale;
            if (_applied.ContainsKey(family.Root)) return ApplyResult.Applied;
            _applied[family.Root] = family.DisplayName;
            log.Info($"[dry run] would put into Efficiency mode: {EfficiencyMode.Describe(family, family.Processes.Count)}");
            return ApplyResult.Applied;
        }
    }

    public void Restore(ProcessRef root, string reason)
    {
        lock (_lock)
        {
            if (!_applied.TryGetValue(root, out var name)) return;
            _applied.Remove(root);
            log.Info($"[dry run] would end Efficiency mode for {name}: {reason}");
        }
    }

    public int RestoreAll(string reason)
    {
        lock (_lock)
        {
            _generation++;
            var apps = _applied.Count;
            _applied.Clear();
            if (apps > 0) log.Info($"[dry run] would end Efficiency mode for {apps} {(apps == 1 ? "app" : "apps")}: {reason}");
            return apps;
        }
    }
}
