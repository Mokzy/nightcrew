namespace Nightcrew.Core;

/// <summary>Live while held, or while any signal was busy within the grace window.</summary>
public sealed class ActivityMonitor(IReadOnlyList<IBusySignal> signals, TimeSpan grace)
{
    private readonly Dictionary<IBusySignal, TimeSpan> _lastBusy = [];
    private string? _signalReason;

    public bool Hold { get; set; }

    public bool Live => Reason is not null;

    /// <summary>"hold", or the name of the first signal busy within grace; null when not live.</summary>
    public string? Reason => Hold ? "hold" : _signalReason;

    /// <summary>Samples every signal, even when held, so their baselines stay current.</summary>
    public void Sample(TimeSpan now)
    {
        _signalReason = null;
        foreach (var signal in signals)
        {
            if (signal.Sample(now)) _lastBusy[signal] = now;
            if (_signalReason is null && _lastBusy.TryGetValue(signal, out var at) && now - at <= grace)
                _signalReason = signal.Name;
        }
    }
}
