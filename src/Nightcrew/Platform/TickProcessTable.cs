using Nightcrew.Core;

namespace Nightcrew.Platform;

/// <summary>
/// Lists processes at most once per tick, so the agent signal and the Efficiency mode planner share one list; it's
/// most of a tick's cost. Times are always read fresh.
/// </summary>
public sealed class TickProcessTable(IProcessTable inner, IClock clock) : IProcessTable
{
    private static readonly TimeSpan Reuse = TimeSpan.FromSeconds(1);

    private IReadOnlyList<ProcessEntry>? _list;
    private TimeSpan _listedAt;

    public IReadOnlyList<ProcessEntry> List()
    {
        var now = clock.Now;
        if (_list is null || now - _listedAt >= Reuse || now < _listedAt)
        {
            _list = inner.List();
            _listedAt = now;
        }
        return _list;
    }

    public bool TryGetTimes(int pid, out DateTime startUtc, out TimeSpan cpu) => inner.TryGetTimes(pid, out startUtc, out cpu);
}
