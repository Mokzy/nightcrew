using Nightcrew.Core;
using Xunit;

namespace Nightcrew.Tests;

public sealed class ControllerTests
{
    private const LidState Open = LidState.Open;
    private const LidState Closed = LidState.Closed;
    private const LidState Unknown = LidState.Unknown;

    // Rule 1: paused

    [Fact]
    public void Pause_while_armed_disarms_once()
    {
        var rig = ArmedRig(Open);
        Assert.Equal("Disarm", rig.Step(In(paused: true)));
        Assert.Equal(Mode.Paused, rig.Mode);
        Assert.Equal("", rig.Step(In(paused: true)));
    }

    [Fact]
    public void Pause_while_idle_or_finishing_emits_nothing()
    {
        var idle = new Rig();
        Assert.Equal("", idle.Step(In(live: false, paused: true)));
        Assert.Equal(Mode.Paused, idle.Mode);

        var finishing = FinishingRig(LidAction.Sleep);
        Assert.Equal("", finishing.Step(In(live: false, lid: Closed, paused: true)));
        Assert.Equal(Mode.Paused, finishing.Mode);
    }

    [Theory]
    [InlineData(true, "Arm", Mode.Armed)]
    [InlineData(false, "", Mode.Idle)]
    public void Unpause_goes_idle_and_continues_in_the_same_step(bool live, string expected, Mode mode)
    {
        var rig = new Rig();
        rig.Step(In(paused: true));
        Assert.Equal(expected, rig.Step(In(live: live)));
        Assert.Equal(mode, rig.Mode);
    }

    // Rule 2: lid open

    [Fact]
    public void Lid_open_clears_spent_and_lid_closed_time()
    {
        var rig = SpentRig();
        Assert.True(rig.Controller.Spent);
        Assert.Equal(TimeSpan.FromMinutes(10), rig.Controller.LidClosedFor);

        rig.Step(In(live: false, lid: Open));
        Assert.False(rig.Controller.Spent);
        Assert.Equal(TimeSpan.Zero, rig.Controller.LidClosedFor);
    }

    [Fact]
    public void Unknown_lid_does_not_clear_spent()
    {
        var rig = SpentRig();
        Assert.Equal("", rig.Step(In(lid: Unknown)));
        Assert.True(rig.Controller.Spent);
        Assert.Equal(Mode.Idle, rig.Mode);
    }

    // Rule 3: idle

    [Theory]
    [InlineData(false, Open, 5, true, true, "", Mode.Idle)]
    [InlineData(false, Closed, 5, true, true, "", Mode.Idle)]
    [InlineData(true, Open, 5, true, true, "Arm", Mode.Armed)]
    [InlineData(true, Unknown, 5, true, true, "Arm", Mode.Armed)]
    [InlineData(true, Closed, 5, true, true, "Arm Lock", Mode.Armed)]
    [InlineData(true, Closed, 4.9, true, true, "Arm", Mode.Armed)]
    [InlineData(true, Closed, 5, true, false, "Arm", Mode.Armed)]
    [InlineData(true, Open, 5, false, true, "", Mode.Idle)]
    [InlineData(true, Closed, 5, false, true, "", Mode.Idle)]
    public void Idle_arms_when_live_and_writable(bool live, LidState lid, double idleMinutes, bool canWrite, bool lockOnLidClose, string expected, Mode mode)
    {
        var rig = new Rig(new Settings { LockOnLidClose = lockOnLidClose });
        Assert.Equal(expected, rig.Step(In(live: live, lid: lid, idleMinutes: idleMinutes, canWrite: canWrite)));
        Assert.Equal(mode, rig.Mode);
    }

    [Fact]
    public void Spent_blocks_arming_until_the_lid_opens()
    {
        var rig = SpentRig();
        Assert.Equal("", rig.Step(In(lid: Closed)));
        Assert.Equal(Mode.Idle, rig.Mode);
        Assert.Equal("Arm", rig.Step(In(lid: Open)));
        Assert.Equal(Mode.Armed, rig.Mode);
    }

    [Fact]
    public void Wake_with_the_lid_closed_arms_and_locks()
    {
        var rig = new Rig();
        Assert.Equal("", rig.Step(Away(live: false)));
        Assert.Equal("Arm Lock", rig.Step(Away()));
        Assert.Equal(Mode.Armed, rig.Mode);
    }

    // Rule 4: armed

    [Theory]
    [InlineData(Open, true, "Lock")]
    [InlineData(Unknown, true, "Lock")]
    [InlineData(Open, false, "")]
    public void Armed_locks_only_on_the_lid_closing_edge(LidState armedWith, bool lockOnLidClose, string expected)
    {
        var rig = ArmedRig(armedWith, new Settings { LockOnLidClose = lockOnLidClose });
        Assert.Equal(expected, rig.Step(In(lid: Closed)));
        Assert.Equal("", rig.Step(In(lid: Closed)));
        Assert.Equal("", rig.Step(In(lid: Open)));
        Assert.Equal(expected, rig.Step(In(lid: Closed)));
    }

    [Fact]
    public void Lid_closed_time_counts_only_while_armed_and_unattended()
    {
        var rig = ArmedRig(Closed);
        Assert.Equal(TimeSpan.Zero, rig.Controller.LidClosedFor);    // the arming step adds nothing

        rig.Step(Away(), afterMinutes: 10);
        rig.Step(Away(), afterMinutes: 5);
        Assert.Equal(TimeSpan.FromMinutes(15), rig.Controller.LidClosedFor);

        rig.Step(In(lid: Closed, idleMinutes: 1), afterMinutes: 20); // in use with the lid closed: not counted
        Assert.Equal(TimeSpan.FromMinutes(15), rig.Controller.LidClosedFor);

        rig.Step(Away(live: false), afterMinutes: 1);                // still Armed during this step
        rig.Step(Away(live: false), afterMinutes: 30);               // Finishing: not counted
        Assert.Equal(Mode.Finishing, rig.Mode);
        Assert.Equal(TimeSpan.FromMinutes(16), rig.Controller.LidClosedFor);

        rig.Step(Away(), afterMinutes: 1);                           // re-arms: a new episode starts at zero
        rig.Step(Away(), afterMinutes: 2);
        Assert.Equal(TimeSpan.FromMinutes(2), rig.Controller.LidClosedFor);

        rig.Step(In(lid: Open), afterMinutes: 1);
        Assert.Equal(TimeSpan.Zero, rig.Controller.LidClosedFor);
        Assert.Equal(Mode.Armed, rig.Mode);
    }

    [Fact]
    public void Lid_closed_time_counts_per_episode()
    {
        var rig = ArmedRig(Closed);
        rig.Step(Away(), afterMinutes: 30);
        Assert.Equal("Disarm", rig.Step(Done(LidAction.Sleep, idleMinutes: 5)));
        Assert.Equal(Mode.Finishing, rig.Mode);

        Assert.Equal("Arm", rig.Step(Away(), afterMinutes: 1));
        Assert.Equal(TimeSpan.Zero, rig.Controller.LidClosedFor);
        rig.Step(Away(), afterMinutes: 10);
        Assert.Equal(TimeSpan.FromMinutes(10), rig.Controller.LidClosedFor);
    }

    [Theory]
    [InlineData(LidAction.DoNothing, "Arm")]
    [InlineData(LidAction.Sleep, "Arm Lock")]
    public void Arming_with_the_lid_closed_locks_only_when_your_lid_action_is_not_do_nothing(LidAction owner, string expected)
    {
        Assert.Equal(expected, new Rig().Step(Away(owner: owner)));
    }

    [Fact]
    public void Closing_the_lid_while_armed_does_not_lock_when_your_lid_action_is_do_nothing()
    {
        var rig = ArmedRig(Open);
        Assert.Equal("", rig.Step(In(lid: Closed, owner: LidAction.DoNothing)));
    }

    [Theory]
    [InlineData(PowerSource.DC, 20, 20, Closed, 5, true)]
    [InlineData(PowerSource.DC, 5, 20, Closed, 5, true)]
    [InlineData(PowerSource.DC, 21, 20, Closed, 5, false)]
    [InlineData(PowerSource.AC, 5, 20, Closed, 5, false)]
    [InlineData(PowerSource.DC, null, 20, Closed, 5, false)]
    [InlineData(PowerSource.DC, 5, 0, Closed, 5, false)]
    [InlineData(PowerSource.DC, 5, 20, Closed, 0, true)]
    [InlineData(PowerSource.DC, 5, 20, Open, 5, false)]
    [InlineData(PowerSource.DC, 5, 20, Unknown, 5, false)]
    public void Battery_floor_hibernates_when_the_lid_is_closed_on_battery(PowerSource source, int? battery, int floor, LidState lid, double idleMinutes, bool hibernates)
    {
        var rig = ArmedRig(lid, new Settings { BatteryFloorPercent = floor });
        Assert.Equal(hibernates ? "Disarm Hibernate" : "", rig.Step(In(lid: lid, source: source, battery: battery, idleMinutes: idleMinutes)));
        Assert.Equal(hibernates, rig.Controller.Spent);
        Assert.Equal(hibernates ? Mode.Idle : Mode.Armed, rig.Mode);
    }

    [Fact]
    public void Battery_floor_applies_while_the_closed_laptop_is_in_use()
    {
        var rig = new Rig();
        Assert.Equal("Arm", rig.Step(In(lid: Closed, idleMinutes: 0.1)));
        Assert.Equal("", rig.Step(In(lid: Closed, source: PowerSource.DC, battery: 25, idleMinutes: 0.1)));
        Assert.Equal("Disarm Hibernate", rig.Step(In(lid: Closed, source: PowerSource.DC, battery: 15, idleMinutes: 0.1)));
        Assert.True(rig.Controller.Spent);
        Assert.Equal(Mode.Idle, rig.Mode);
    }

    [Fact]
    public void Lid_closed_cap_hibernates_at_the_limit()
    {
        var rig = ArmedRig(Closed, new Settings { LidClosedCapHours = 2 });
        Assert.Equal("", rig.Step(Away(), afterMinutes: 119));
        Assert.Equal("Disarm Hibernate", rig.Step(Away(), afterMinutes: 1));
        Assert.True(rig.Controller.Spent);
        Assert.Equal(Mode.Idle, rig.Mode);
    }

    [Fact]
    public void Lid_closed_cap_of_zero_is_off()
    {
        var rig = ArmedRig(Closed, new Settings { LidClosedCapHours = 0 });
        Assert.Equal("", rig.Step(Away(), afterMinutes: 48 * 60));
        Assert.Equal(Mode.Armed, rig.Mode);
    }

    [Fact]
    public void Lid_closed_cap_ignores_time_with_the_lid_open()
    {
        var rig = ArmedRig(Open, new Settings { LidClosedCapHours = 2 });
        Assert.Equal("", rig.Step(In(lid: Open, idleMinutes: 5), afterMinutes: 3 * 60)); // one long step without input
        for (var minute = 0; minute < 180; minute++)
            Assert.Equal("", rig.Step(In(lid: Open, idleMinutes: 6 + minute), afterMinutes: 1));
        Assert.Equal(TimeSpan.Zero, rig.Controller.LidClosedFor);
        Assert.False(rig.Controller.Spent);
        Assert.Equal(Mode.Armed, rig.Mode);
    }

    [Fact]
    public void Docked_and_in_use_gets_no_lock_cap_or_lid_closed_time()
    {
        var rig = new Rig(new Settings { LidClosedCapHours = 2 });
        Assert.Equal("Arm", rig.Step(In(lid: Closed, idleMinutes: 1)));
        for (var minute = 0; minute < 180; minute++)
            Assert.Equal("", rig.Step(In(lid: Closed, idleMinutes: 4.9), afterMinutes: 1));
        Assert.Equal(TimeSpan.Zero, rig.Controller.LidClosedFor);
        Assert.False(rig.Controller.Spent);
        Assert.Equal(Mode.Armed, rig.Mode);

        // Walking away makes it unattended: lid-closed time counts from here, and the cap applies.
        for (var minute = 1; minute < 120; minute++)
            Assert.Equal("", rig.Step(Away(), afterMinutes: 1));
        Assert.Equal("Disarm Hibernate", rig.Step(Away(), afterMinutes: 1));
        Assert.True(rig.Controller.Spent);
    }

    [Theory]
    [InlineData(Open, Mode.Idle)]
    [InlineData(Unknown, Mode.Idle)]
    [InlineData(Closed, Mode.Finishing)]
    public void Armed_disarms_when_work_stops(LidState lid, Mode mode)
    {
        var rig = ArmedRig(lid);
        Assert.Equal("Disarm", rig.Step(In(live: false, lid: lid)));
        Assert.Equal(mode, rig.Mode);
    }

    [Fact]
    public void Floor_wins_over_work_stopping()
    {
        var rig = ArmedRig(Closed);
        Assert.Equal("Disarm Hibernate", rig.Step(Away(live: false, source: PowerSource.DC, battery: 10)));
        Assert.Equal(Mode.Idle, rig.Mode);
    }

    [Fact]
    public void Lid_closing_as_work_stops_locks_then_disarms()
    {
        var rig = ArmedRig(Open);
        Assert.Equal("Lock Disarm", rig.Step(In(live: false, lid: Closed)));
        Assert.Equal(Mode.Finishing, rig.Mode);
    }

    [Fact]
    public void Unknown_lid_never_locks_or_finishes()
    {
        var rig = new Rig(new Settings { LidClosedCapHours = 2 });
        Assert.Equal("Arm", rig.Step(In(lid: Unknown, idleMinutes: 30)));
        Assert.Equal(Mode.Armed, rig.Mode);

        for (var minute = 0; minute < 180; minute++)
            Assert.Equal("", rig.Step(In(lid: Unknown, source: PowerSource.DC, battery: 5, idleMinutes: 30), afterMinutes: 1));
        Assert.Equal(TimeSpan.Zero, rig.Controller.LidClosedFor);
        Assert.False(rig.Controller.Spent);

        Assert.Equal("Disarm", rig.Step(In(live: false, lid: Unknown, idleMinutes: 30)));
        Assert.Equal(Mode.Idle, rig.Mode);
        for (var minute = 0; minute < 30; minute++)
            Assert.Equal("", rig.Step(In(live: false, lid: Unknown, idleMinutes: 31 + minute), afterMinutes: 1));
    }

    // Rule 5: finishing

    [Theory]
    [InlineData(Open)]
    [InlineData(Unknown)]
    public void Finishing_goes_idle_when_the_lid_is_not_closed(LidState lid)
    {
        var rig = FinishingRig(LidAction.Sleep);
        Assert.Equal("", rig.Step(In(live: false, lid: lid, idleMinutes: 30)));
        Assert.Equal(Mode.Idle, rig.Mode);
    }

    [Fact]
    public void Finishing_rearms_when_work_resumes()
    {
        var rig = FinishingRig(LidAction.Sleep);
        Assert.Equal("Arm", rig.Step(In(lid: Closed, idleMinutes: 30)));
        Assert.Equal(Mode.Armed, rig.Mode);
    }

    [Fact]
    public void Finishing_does_not_rearm_without_write_access()
    {
        var rig = FinishingRig(LidAction.DoNothing);
        Assert.Equal("", rig.Step(In(lid: Closed, canWrite: false, idleMinutes: 1)));
        Assert.Equal(Mode.Finishing, rig.Mode);
    }

    [Theory]
    [InlineData(LidAction.Sleep, "MonitorOff", Mode.Finishing)]
    [InlineData(LidAction.Hibernate, "Hibernate", Mode.Idle)]
    [InlineData(LidAction.ShutDown, "Hibernate", Mode.Idle)]
    [InlineData(LidAction.DoNothing, "", Mode.Idle)]
    public void Finishing_acts_on_the_owner_lid_action_once_away(LidAction owner, string expected, Mode mode)
    {
        var rig = FinishingRig(owner);
        Assert.Equal("", rig.Step(Done(owner, idleMinutes: 4.9)));
        Assert.Equal(Mode.Finishing, rig.Mode);
        Assert.Equal(expected, rig.Step(Done(owner, idleMinutes: 5)));
        Assert.Equal(mode, rig.Mode);
    }

    [Fact]
    public void Monitor_off_falls_back_to_hibernate_after_five_minutes()
    {
        var rig = FinishingRig(LidAction.Sleep);
        Assert.Equal("MonitorOff", rig.Step(Done(LidAction.Sleep, idleMinutes: 5)));
        Assert.Equal("", rig.Step(Done(LidAction.Sleep, idleMinutes: 9), afterMinutes: 4));
        Assert.Equal("Hibernate", rig.Step(Done(LidAction.Sleep, idleMinutes: 10), afterMinutes: 1));
        Assert.Equal(Mode.Idle, rig.Mode);
    }

    [Fact]
    public void Input_after_monitor_off_goes_back_to_waiting()
    {
        var rig = FinishingRig(LidAction.Sleep);
        Assert.Equal("MonitorOff", rig.Step(Done(LidAction.Sleep, idleMinutes: 5)));
        Assert.Equal("", rig.Step(Done(LidAction.Sleep, idleMinutes: 0.5), afterMinutes: 4));  // user input
        Assert.Equal("", rig.Step(Done(LidAction.Sleep, idleMinutes: 2), afterMinutes: 2));    // 6 min after: no hibernate
        Assert.Equal(Mode.Finishing, rig.Mode);
        Assert.Equal("MonitorOff", rig.Step(Done(LidAction.Sleep, idleMinutes: 5), afterMinutes: 3));
    }

    [Fact]
    public void Do_nothing_owner_never_hibernates_when_work_is_done()
    {
        var rig = FinishingRig(LidAction.DoNothing);
        for (var minute = 0; minute < 120; minute++)
            Assert.DoesNotContain("Hibernate", rig.Step(Done(LidAction.DoNothing, idleMinutes: minute), afterMinutes: 1));
        Assert.Equal(Mode.Idle, rig.Mode);
    }

    // Rule 6: resume

    [Fact]
    public void Resume_during_finishing_goes_idle_and_never_hibernates_later()
    {
        var rig = FinishingRig(LidAction.Sleep);
        Assert.Equal("MonitorOff", rig.Step(Done(LidAction.Sleep, idleMinutes: 5)));
        rig.Controller.OnResume();
        Assert.Equal(Mode.Idle, rig.Mode);

        for (var minute = 0; minute < 60; minute++)
            Assert.Equal("", rig.Step(Done(LidAction.Sleep, idleMinutes: 6 + minute), afterMinutes: 1));
        Assert.Equal(Mode.Idle, rig.Mode);
    }

    [Fact]
    public void Resume_leaves_other_modes_alone()
    {
        var rig = ArmedRig(Closed);
        rig.Controller.OnResume();
        Assert.Equal(Mode.Armed, rig.Mode);
    }

    // Reset

    [Fact]
    public void Reset_goes_idle_keeps_spent_and_lid_closed_time_and_the_next_step_rearms()
    {
        var rig = ArmedRig(Closed);
        rig.Step(Away(), afterMinutes: 10);
        rig.Controller.Reset();
        Assert.Equal(Mode.Idle, rig.Mode);
        Assert.Equal(TimeSpan.FromMinutes(10), rig.Controller.LidClosedFor);
        Assert.Equal("Arm Lock", rig.Step(Away()));
        Assert.Equal(Mode.Armed, rig.Mode);

        var spent = SpentRig();
        spent.Controller.Reset();
        Assert.Equal(Mode.Idle, spent.Mode);
        Assert.True(spent.Controller.Spent);
        Assert.Equal(TimeSpan.FromMinutes(10), spent.Controller.LidClosedFor);
    }

    // Status

    [Fact]
    public void Status_follows_the_mode()
    {
        var rig = new Rig();
        Assert.Equal("Idle", rig.Controller.Status);

        rig.Step(In(lid: Open));
        Assert.Equal("Live", rig.Controller.Status);

        rig.Step(Away(), afterMinutes: 62);
        rig.Step(Away(), afterMinutes: 62);
        Assert.Equal("Live — lid closed 2h04, when done: sleep", rig.Controller.Status);

        rig.Step(In(lid: Closed, idleMinutes: 1));
        Assert.Equal("Live — lid closed, when done: sleep", rig.Controller.Status);

        rig.Step(Done(LidAction.Sleep, idleMinutes: 0));
        Assert.Equal("Done — waiting until you're away", rig.Controller.Status);

        rig.Step(Done(LidAction.Sleep, idleMinutes: 5));
        Assert.Equal("Done — screen off", rig.Controller.Status);

        rig.Step(In(paused: true));
        Assert.Equal("Paused", rig.Controller.Status);
    }

    [Theory]
    [InlineData(LidAction.Sleep, "sleep")]
    [InlineData(LidAction.Hibernate, "hibernate")]
    [InlineData(LidAction.ShutDown, "hibernate")]
    [InlineData(LidAction.DoNothing, "do nothing")]
    public void Status_names_what_happens_when_done(LidAction owner, string words)
    {
        var rig = ArmedRig(Closed);
        rig.Step(Away(owner: owner), afterMinutes: 5);
        Assert.Equal($"Live — lid closed 0h05, when done: {words}", rig.Controller.Status);
    }

    [Fact]
    public void Status_explains_why_idle_will_not_arm()
    {
        var spent = SpentRig();
        Assert.Equal("Idle — limit reached, open the lid to reset", spent.Controller.Status);

        var blocked = new Rig();
        blocked.Step(In(canWrite: false));
        Assert.Equal("Idle — can't change power settings", blocked.Controller.Status);
    }

    // Helpers

    private static Inputs In(bool live = true, LidState lid = Open, PowerSource source = PowerSource.AC, int? battery = null,
        double idleMinutes = 0, bool paused = false, bool canWrite = true, LidAction owner = LidAction.Sleep) =>
        new(live, lid, source, battery, TimeSpan.FromMinutes(idleMinutes), paused, canWrite, owner);

    /// <summary>Unattended: the lid is closed and there has been no input for the default 5 away minutes.</summary>
    private static Inputs Away(bool live = true, PowerSource source = PowerSource.AC, int? battery = null, LidAction owner = LidAction.Sleep) =>
        In(live: live, lid: Closed, source: source, battery: battery, idleMinutes: 5, owner: owner);

    /// <summary>Work is done and the lid is still closed.</summary>
    private static Inputs Done(LidAction owner, double idleMinutes) =>
        In(live: false, lid: Closed, idleMinutes: idleMinutes, owner: owner);

    private static Rig ArmedRig(LidState lid, Settings? settings = null)
    {
        var rig = new Rig(settings);
        rig.Step(In(lid: lid));
        Assert.Equal(Mode.Armed, rig.Mode);
        return rig;
    }

    private static Rig FinishingRig(LidAction owner)
    {
        var rig = ArmedRig(Closed);
        Assert.Equal("Disarm", rig.Step(Done(owner, idleMinutes: 0)));
        Assert.Equal(Mode.Finishing, rig.Mode);
        return rig;
    }

    /// <summary>Armed with the lid closed, unattended for 10 minutes, then the battery floor hibernated.</summary>
    private static Rig SpentRig()
    {
        var rig = ArmedRig(Closed);
        Assert.Equal("Disarm Hibernate", rig.Step(Away(source: PowerSource.DC, battery: 15), afterMinutes: 10));
        return rig;
    }

    private sealed class Rig(Settings? settings = null)
    {
        private readonly FakeClock _clock = new();

        public Controller Controller { get; } = new(settings ?? new Settings());

        public Mode Mode => Controller.Mode;

        /// <summary>Advances the clock, steps the controller and returns the commands joined by spaces.</summary>
        public string Step(Inputs inputs, double afterMinutes = 0.5)
        {
            _clock.Advance(TimeSpan.FromMinutes(afterMinutes));
            return string.Join(" ", Controller.Step(inputs, _clock.Now));
        }
    }
}
