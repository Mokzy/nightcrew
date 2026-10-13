namespace Nightcrew.Core;

public interface IGpuUsage
{
    /// <summary>Process id to the utilization percent of its busiest GPU engine. Empty when unavailable.</summary>
    IReadOnlyDictionary<int, double> PercentByPid();
}

public sealed class GpuSignal(IGpuUsage gpu, double thresholdPercent) : IBusySignal
{
    public string Name => "gpu";

    /// <summary>The busiest pid of the last busy sample; kept after the GPU goes quiet.</summary>
    public int? LastBusyPid { get; private set; }

    public bool Sample(TimeSpan now)
    {
        int? busiest = null;
        double busiestPercent = 0;
        foreach (var pair in gpu.PercentByPid())
        {
            if (pair.Value >= thresholdPercent && (busiest is null || pair.Value > busiestPercent))
            {
                busiest = pair.Key;
                busiestPercent = pair.Value;
            }
        }
        if (busiest is null) return false;
        LastBusyPid = busiest;
        return true;
    }
}
