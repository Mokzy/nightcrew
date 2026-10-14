namespace Nightcrew.Core;

/// <summary>Live while held, or while any signal was busy within the grace window.</summary>
public sealed class ActivityMonitor(IReadOnlyList<IBusySignal> signals, TimeSpan grace)
{
    private readonly Dictionary<IBusySignal, TimeSpan> _lastBusy = [];
    private readonly Dictionary<IBusySignal, IReadOnlyList<Holder>> _lastHolders = [];
    private IReadOnlyList<Holder> _signalHolders = [];
    private string? _signalReason;

    public bool Hold { get; set; }

    public bool Live => Reason is not null;

    /// <summary>"hold", or the name of the first signal busy within grace; null when not live.</summary>
    public string? Reason => Hold ? "hold" : _signalReason;

    /// <summary>
    /// Who is holding the laptop awake: "Hold awake" first when held, then the holders of every signal busy within
    /// grace, in signal order. A label appears once; the first signal to report it wins.
    /// </summary>
    public IReadOnlyList<Holder> Holders =>
        Hold ? [new Holder("Hold awake", "on"), .. _signalHolders.Where(holder => holder.Label != "Hold awake")] : _signalHolders;

    /// <summary>Samples every signal, even when held, so their baselines stay current.</summary>
    public void Sample(TimeSpan now)
    {
        _signalReason = null;
        var holders = new List<Holder>();
        foreach (var signal in signals)
        {
            if (signal.Sample(now))
            {
                _lastBusy[signal] = now;
                if (signal is IHolderSource source) _lastHolders[signal] = source.Holders;
            }
            if (!_lastBusy.TryGetValue(signal, out var at) || now - at > grace) continue;
            _signalReason ??= signal.Name;
            if (!_lastHolders.TryGetValue(signal, out var reported)) continue;
            foreach (var holder in reported)
                if (!holders.Any(existing => existing.Label == holder.Label)) holders.Add(holder);
        }
        _signalHolders = holders;
    }
}
