namespace Nightcrew.Core;

/// <summary>
/// Notices a tick that never finishes: a hung tick keeps the power settings armed and skips every check after it, the
/// battery floor and the lid-closed limit included. Uses <see cref="IClock.Now"/>, which stops while Windows sleeps, so
/// waking up is never mistaken for a freeze. Safe to use from any thread.
/// </summary>
public sealed class FreezeGuard(IClock clock, TimeSpan limit, TimeSpan minUptimeForRestart)
{
    private readonly long _startedTicks = clock.Now.Ticks;
    private long _lastTickTicks = clock.Now.Ticks;

    /// <summary>Called at the end of every tick that finished.</summary>
    public void TickDone() => Interlocked.Exchange(ref _lastTickTicks, clock.Now.Ticks);

    /// <summary>True when no tick has finished for longer than the limit, counting from the start before the first one.</summary>
    public bool Frozen => clock.Now.Ticks - Interlocked.Read(ref _lastTickTicks) > limit.Ticks;

    /// <summary>True once this process has run long enough that restarting it can't turn into a loop.</summary>
    public bool MayRestart => clock.Now.Ticks - _startedTicks >= minUptimeForRestart.Ticks;
}
