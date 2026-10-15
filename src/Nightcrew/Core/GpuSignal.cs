namespace Nightcrew.Core;

public interface IGpuUsage
{
    /// <summary>Process id to the utilization percent of its busiest GPU engine. Empty when unavailable.</summary>
    IReadOnlyDictionary<int, double> PercentByPid();
}

/// <summary><paramref name="nameOf"/> turns a pid into a process name for the panel; null names show as "pid N".</summary>
public sealed class GpuSignal(IGpuUsage gpu, double thresholdPercent, Func<int, string?>? nameOf = null) : IBusySignal, IHolderSource
{
    public string Name => "gpu";

    /// <summary>The busiest pid of the last busy sample; kept after the GPU goes quiet.</summary>
    public int? LastBusyPid { get; private set; }

    /// <summary>Every process at or above the threshold in the last busy sample, busiest first.</summary>
    public IReadOnlyList<Holder> Holders { get; private set; } = [];

    /// <summary>The pids behind <see cref="Holders"/>, so Efficiency mode leaves their apps alone.</summary>
    public IReadOnlyList<int> BusyPids { get; private set; } = [];

    public bool Sample(TimeSpan now)
    {
        var busy = gpu.PercentByPid()
            .Where(pair => pair.Value >= thresholdPercent)
            .OrderByDescending(pair => pair.Value)
            .ToList();
        if (busy.Count == 0) return false;
        LastBusyPid = busy[0].Key;
        BusyPids = busy.Select(pair => pair.Key).ToList();
        Holders = busy.Select(pair => new Holder(nameOf?.Invoke(pair.Key) ?? $"pid {pair.Key}", $"GPU {Math.Round(pair.Value):0}%")).ToList();
        return true;
    }
}
