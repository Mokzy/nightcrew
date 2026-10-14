using Nightcrew.Core;
using Xunit;

namespace Nightcrew.Tests;

public sealed class ActivityMonitorTests
{
    private static readonly TimeSpan Grace = TimeSpan.FromMinutes(5);

    private readonly FakeClock _clock = new();
    private readonly FakeSignal _transcripts = new("transcripts");
    private readonly FakeSignal _gpu = new("gpu");

    private ActivityMonitor NewMonitor() => new([_transcripts, _gpu], Grace);

    [Fact]
    public void Not_live_before_anything_is_busy()
    {
        var monitor = NewMonitor();
        Assert.False(monitor.Live);
        Assert.Null(monitor.Reason);

        monitor.Sample(_clock.Now);
        Assert.False(monitor.Live);
        Assert.Null(monitor.Reason);
    }

    [Fact]
    public void A_busy_signal_makes_it_live_with_its_name()
    {
        var monitor = NewMonitor();
        _gpu.Busy = true;

        monitor.Sample(_clock.Now);

        Assert.True(monitor.Live);
        Assert.Equal("gpu", monitor.Reason);
    }

    [Fact]
    public void Stays_live_through_the_grace_window_then_drops()
    {
        var monitor = NewMonitor();
        _transcripts.Busy = true;
        monitor.Sample(_clock.Now);
        _transcripts.Busy = false;

        _clock.Advance(Grace);
        monitor.Sample(_clock.Now);
        Assert.True(monitor.Live);
        Assert.Equal("transcripts", monitor.Reason);

        _clock.Advance(TimeSpan.FromSeconds(1));
        monitor.Sample(_clock.Now);
        Assert.False(monitor.Live);
        Assert.Null(monitor.Reason);
    }

    [Fact]
    public void Busy_again_restarts_the_grace_window()
    {
        var monitor = NewMonitor();
        _transcripts.Busy = true;
        monitor.Sample(_clock.Now);
        _clock.Advance(TimeSpan.FromMinutes(4));
        monitor.Sample(_clock.Now);
        _transcripts.Busy = false;

        _clock.Advance(TimeSpan.FromMinutes(4));
        monitor.Sample(_clock.Now);

        Assert.True(monitor.Live);
    }

    [Fact]
    public void Hold_is_live_without_any_signal_and_wins_the_reason()
    {
        var monitor = NewMonitor();
        monitor.Hold = true;
        Assert.True(monitor.Live);
        Assert.Equal("hold", monitor.Reason);

        _gpu.Busy = true;
        monitor.Sample(_clock.Now);
        Assert.Equal("hold", monitor.Reason);

        monitor.Hold = false;
        Assert.Equal("gpu", monitor.Reason);
    }

    [Fact]
    public void The_first_busy_signal_in_order_is_the_reason()
    {
        var monitor = NewMonitor();
        _transcripts.Busy = true;
        _gpu.Busy = true;

        monitor.Sample(_clock.Now);

        Assert.Equal("transcripts", monitor.Reason);
    }

    [Fact]
    public void Every_signal_is_sampled_every_time_with_the_given_time()
    {
        var monitor = NewMonitor();
        monitor.Hold = true;
        _transcripts.Busy = true;

        monitor.Sample(_clock.Now);
        _clock.Advance(TimeSpan.FromSeconds(10));
        monitor.Sample(_clock.Now);

        Assert.Equal(2, _transcripts.Samples);
        Assert.Equal(2, _gpu.Samples);
        Assert.Equal(_clock.Now, _gpu.LastNow);
    }

    [Fact]
    public void No_signals_means_live_only_on_hold()
    {
        var monitor = new ActivityMonitor([], Grace);
        monitor.Sample(_clock.Now);
        Assert.False(monitor.Live);

        monitor.Hold = true;
        Assert.True(monitor.Live);
    }

    private sealed class FakeSignal(string name) : IBusySignal
    {
        public string Name => name;
        public bool Busy { get; set; }
        public int Samples { get; private set; }
        public TimeSpan LastNow { get; private set; }

        public bool Sample(TimeSpan now)
        {
            Samples++;
            LastNow = now;
            return Busy;
        }
    }
}
