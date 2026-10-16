using System.Diagnostics;
using Nightcrew.Core;
using Xunit;

namespace Nightcrew.Tests;

public sealed class DeadlineTests
{
    [Fact]
    public void Steps_run_in_order_and_a_failing_step_does_not_stop_the_next()
    {
        var done = new List<int>();

        var finished = Deadline.Run(TimeSpan.FromSeconds(10), () => done.Add(1), () => throw new InvalidOperationException("stuck"), () => done.Add(3));

        Assert.True(finished);
        Assert.Equal([1, 3], done);
    }

    [Fact]
    public void A_step_that_never_returns_gives_up_at_the_deadline()
    {
        using var never = new ManualResetEventSlim(false);
        var clock = Stopwatch.StartNew();

        var token = TestContext.Current.CancellationToken;
        var finished = Deadline.Run(TimeSpan.FromMilliseconds(200), () => never.Wait(token));

        Assert.False(finished);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5));
        never.Set();
    }

    [Fact]
    public void Under_runs_the_action_while_holding_a_free_gate()
    {
        var gate = new object();
        var held = false;

        Assert.True(Deadline.Under(gate, TimeSpan.FromSeconds(1), () => held = Monitor.IsEntered(gate)));

        Assert.True(held);
        Assert.False(Monitor.IsEntered(gate));
    }

    [Fact]
    public void Under_gives_up_when_the_gate_stays_held()
    {
        var gate = new object();
        using var taken = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var token = TestContext.Current.CancellationToken;
        var holder = new Thread(() =>
        {
            lock (gate)
            {
                taken.Set();
                release.Wait(token);
            }
        });
        holder.Start();
        taken.Wait(token);
        var ran = false;

        var entered = Deadline.Under(gate, TimeSpan.FromMilliseconds(200), () => ran = true);

        release.Set();
        holder.Join();
        Assert.False(entered);
        Assert.False(ran);
    }
}
