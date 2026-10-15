using Nightcrew.Core;
using Xunit;

namespace Nightcrew.Tests;

public sealed class EfficiencyGateTests
{
    private static readonly TimeSpan Away = TimeSpan.FromMinutes(5);

    private static bool Wanted(Mode mode = Mode.Armed, LidState lid = LidState.Closed, TimeSpan? idle = null,
        LidAction owner = LidAction.Sleep, bool enabled = true) =>
        EfficiencyGate.Wanted(mode, lid, idle ?? Away, owner, new Settings { EfficiencyWhileAway = enabled });

    [Fact]
    public void Wanted_when_an_agent_works_with_the_lid_closed_and_nobody_there() => Assert.True(Wanted());

    [Theory]
    [InlineData(LidAction.Sleep)]
    [InlineData(LidAction.Hibernate)]
    [InlineData(LidAction.ShutDown)]
    public void Wanted_for_every_lid_action_that_would_stop_the_laptop(LidAction owner) => Assert.True(Wanted(owner: owner));

    [Fact]
    public void Not_wanted_when_the_lid_action_is_do_nothing() => Assert.False(Wanted(owner: LidAction.DoNothing));

    [Theory]
    [InlineData(Mode.Idle)]
    [InlineData(Mode.Finishing)]
    [InlineData(Mode.Paused)]
    public void Not_wanted_unless_armed(Mode mode) => Assert.False(Wanted(mode: mode));

    [Theory]
    [InlineData(LidState.Open)]
    [InlineData(LidState.Unknown)]
    public void Not_wanted_unless_the_lid_is_closed(LidState lid) => Assert.False(Wanted(lid: lid));

    [Fact]
    public void Not_wanted_until_five_minutes_without_input() =>
        Assert.False(Wanted(idle: Away - TimeSpan.FromSeconds(1)));

    [Fact]
    public void Not_wanted_when_switched_off() => Assert.False(Wanted(enabled: false));

    [Fact]
    public void Away_minutes_comes_from_the_settings()
    {
        var settings = new Settings { AwayMinutes = 10 };
        Assert.False(EfficiencyGate.Wanted(Mode.Armed, LidState.Closed, TimeSpan.FromMinutes(9), LidAction.Sleep, settings));
        Assert.True(EfficiencyGate.Wanted(Mode.Armed, LidState.Closed, TimeSpan.FromMinutes(10), LidAction.Sleep, settings));
    }
}
