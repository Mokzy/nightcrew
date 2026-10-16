namespace Nightcrew.Core;

/// <summary>
/// Notices a tick that never finishes: a hung tick keeps the power settings armed and skips every check after it, the
/// battery floor and the lid-closed limit included. Uses <see cref="IClock.Now"/>, which is meant to stop while Windows
/// sleeps; recovery also needs two frozen checks in a row, so a check that runs right after waking, before the first
/// tick, never counts on its own. Safe to use from any thread.
/// </summary>
public sealed class FreezeGuard(IClock clock, TimeSpan limit, TimeSpan minUptimeForRestart)
{
    private readonly long _startedTicks = clock.Now.Ticks;
    private long _lastTickTicks = clock.Now.Ticks;
    private long _suspectSinceTick = -1; // the last tick time a frozen check saw; -1 when the last check wasn't frozen
    private volatile bool _stopped;

    /// <summary>Called at the end of every tick that finished, and on waking.</summary>
    public void TickDone() => Interlocked.Exchange(ref _lastTickTicks, clock.Now.Ticks);

    /// <summary>True when no tick has finished for longer than the limit, counting from the start before the first one.</summary>
    public bool Frozen => !_stopped && clock.Now.Ticks - Interlocked.Read(ref _lastTickTicks) > limit.Ticks;

    /// <summary>For the once-a-minute watch: true when this check and the one before it both found the same tick frozen.</summary>
    public bool ShouldRecover()
    {
        var lastTick = Interlocked.Read(ref _lastTickTicks);
        if (!Frozen)
        {
            Interlocked.Exchange(ref _suspectSinceTick, -1);
            return false;
        }
        return Interlocked.Exchange(ref _suspectSinceTick, lastTick) == lastTick;
    }

    /// <summary>True once this process has run long enough that restarting it can't turn into a loop; never after <see cref="Stop"/>.</summary>
    public bool MayRestart => !_stopped && clock.Now.Ticks - _startedTicks >= minUptimeForRestart.Ticks;

    /// <summary>Nightcrew is quitting or the session is ending: no recovery and no restart from now on.</summary>
    public void Stop() => _stopped = true;
}
