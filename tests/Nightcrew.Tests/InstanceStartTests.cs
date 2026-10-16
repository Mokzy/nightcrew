using Nightcrew.App;
using Xunit;

namespace Nightcrew.Tests;

public sealed class InstanceStartTests
{
    [Fact]
    public void A_running_copy_that_answers_opens_its_panel_without_this_start_waiting_for_the_lock()
    {
        var waited = false;

        var run = InstanceStart.ShouldRun(showRunningPanel: () => true, takeInstance: () => waited = true);

        Assert.False(run);
        Assert.False(waited);
    }

    [Fact]
    public void With_nothing_answering_and_the_lock_free_this_start_runs()
    {
        Assert.True(InstanceStart.ShouldRun(showRunningPanel: () => false, takeInstance: () => true));
    }

    [Fact]
    public void With_nothing_answering_and_the_lock_held_this_start_asks_again_and_ends()
    {
        var asked = 0;

        var run = InstanceStart.ShouldRun(showRunningPanel: () => { asked++; return false; }, takeInstance: () => false);

        Assert.False(run);
        Assert.Equal(2, asked);
    }
}
