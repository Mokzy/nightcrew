using Nightcrew.Core;
using Xunit;

namespace Nightcrew.Tests;

public sealed class FreezeGuardTests
{
    private readonly FakeClock _clock = new();

    private FreezeGuard NewGuard() => new(_clock, TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(10));

    [Fact]
    public void Not_frozen_while_ticks_finish()
    {
        var guard = NewGuard();
        for (var tick = 0; tick < 60; tick++)
        {
            _clock.Advance(TimeSpan.FromSeconds(10));
            guard.TickDone();
            Assert.False(guard.Frozen);
        }
    }

    [Fact]
    public void Frozen_after_three_minutes_without_a_finished_tick()
    {
        var guard = NewGuard();
        guard.TickDone();

        _clock.Advance(TimeSpan.FromMinutes(3));
        Assert.False(guard.Frozen);
        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(guard.Frozen);

        guard.TickDone();
        Assert.False(guard.Frozen);
    }

    [Fact]
    public void A_first_tick_that_never_finishes_counts_from_the_start()
    {
        var guard = NewGuard();

        _clock.Advance(TimeSpan.FromMinutes(3.5));

        Assert.True(guard.Frozen);
    }

    [Fact]
    public void Sleep_does_not_count_as_a_freeze()
    {
        var guard = NewGuard();
        guard.TickDone();

        _clock.UtcNow += TimeSpan.FromHours(2); // asleep: wall time moves, IClock.Now doesn't

        Assert.False(guard.Frozen);
    }

    [Fact]
    public void Restart_is_allowed_only_after_ten_minutes()
    {
        var guard = NewGuard();

        _clock.Advance(TimeSpan.FromMinutes(10) - TimeSpan.FromSeconds(1));
        Assert.False(guard.MayRestart);
        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(guard.MayRestart);
    }

    [Fact]
    public void Two_frozen_checks_in_a_row_recover()
    {
        var guard = NewGuard();
        guard.TickDone();

        _clock.Advance(TimeSpan.FromMinutes(3) + TimeSpan.FromSeconds(1));
        Assert.False(guard.ShouldRecover());
        _clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True(guard.ShouldRecover());
    }

    [Fact]
    public void One_late_check_after_waking_does_not_recover()
    {
        var guard = NewGuard();
        guard.TickDone();

        _clock.Advance(TimeSpan.FromMinutes(4)); // the clock moved while asleep, and the watch ran first
        Assert.False(guard.ShouldRecover());
        guard.TickDone();                         // then the tick ran
        _clock.Advance(TimeSpan.FromMinutes(1));

        Assert.False(guard.ShouldRecover());
    }

    [Fact]
    public void After_stop_it_is_never_frozen_and_never_restarts()
    {
        var guard = NewGuard();
        _clock.Advance(TimeSpan.FromMinutes(30));

        guard.Stop();

        Assert.False(guard.Frozen);
        Assert.False(guard.ShouldRecover());
        Assert.False(guard.ShouldRecover());
        Assert.False(guard.MayRestart);
    }
}
