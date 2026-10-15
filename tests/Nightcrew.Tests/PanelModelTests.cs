using Nightcrew.Core;
using Xunit;

namespace Nightcrew.Tests;

public sealed class PanelModelTests
{
    private static EngineSnapshot Snap(Mode mode = Mode.Idle, bool live = false, LidState lid = LidState.Open,
        PowerSource source = PowerSource.AC, int? battery = 81, LidAction owner = LidAction.Sleep, bool hold = false,
        bool paused = false, EngineWarning warning = EngineWarning.None, params Holder[] holders) =>
        new(mode, lid, live, holders, source, battery, owner, hold, paused, warning);

    [Fact]
    public void Quiet_says_all_quiet_and_what_the_lid_does()
    {
        var view = PanelModel.From(Snap(owner: LidAction.DoNothing), 20);

        Assert.Equal(PanelState.Quiet, view.State);
        Assert.Equal("All quiet.", view.Headline);
        Assert.Null(view.HeadlineItalic);
        Assert.Equal("No agent is working. Close the lid and your laptop does what Windows says.", view.SubLine);
        Assert.Empty(view.Holders);
        Assert.Equal([new PanelFact("Battery", "81%"), new PanelFact("When the lid closes", "Do nothing")], view.Facts);
        Assert.Equal("Pause", view.PauseLabel);
        Assert.Equal(MarkState.Idle, view.Mark);
        Assert.Equal("Nightcrew · all quiet", view.Tooltip);
        Assert.Null(view.Warning);
    }

    [Fact]
    public void Working_shows_holders_battery_floor_and_what_happens_when_done()
    {
        var holders = new[] { new Holder("Claude Code", "2 sessions"), new Holder("brush_app", "GPU 94%") };
        var view = PanelModel.From(Snap(Mode.Armed, live: true, source: PowerSource.DC, battery: 64, holders: holders), 20);

        Assert.Equal(PanelState.Working, view.State);
        Assert.Equal("Working ", view.Headline);
        Assert.Equal("through the night.", view.HeadlineItalic);
        Assert.Equal("Close the lid. Your laptop keeps working, then does what your lid normally does.", view.SubLine);
        Assert.Equal(holders, view.Holders);
        Assert.Equal([new PanelFact("Battery", "64% · stops at 20%"), new PanelFact("When done", "Sleeps")], view.Facts);
        Assert.Equal(MarkState.Live, view.Mark);
        Assert.Equal("Nightcrew · working (Claude Code)", view.Tooltip);
    }

    [Fact]
    public void Working_with_the_lid_closed_uses_the_same_working_mark()
    {
        var view = PanelModel.From(Snap(Mode.Armed, live: true, lid: LidState.Closed), 20);
        Assert.Equal(MarkState.Live, view.Mark);
    }

    [Fact]
    public void Live_but_not_armed_says_to_keep_the_lid_open_and_leaves_the_mark_unlit()
    {
        var holders = new[] { new Holder("Claude Code", "1 session") };
        var view = PanelModel.From(Snap(Mode.Idle, live: true, warning: EngineWarning.PolicyBlocks, holders: holders), 20);

        Assert.Equal(PanelState.Working, view.State);
        Assert.Equal("Working ", view.Headline);
        Assert.Equal("with the lid open.", view.HeadlineItalic);
        Assert.Equal("Nightcrew can't keep the laptop awake with the lid closed right now.", view.SubLine);
        Assert.Equal(holders, view.Holders);
        Assert.Equal([new PanelFact("Battery", "81%"), new PanelFact("When the lid closes", "Sleep")], view.Facts);
        Assert.Equal(MarkState.Idle, view.Mark);
        Assert.Equal("Nightcrew · working, keep the lid open", view.Tooltip);
        Assert.Equal("Windows policy blocks lid changes here. Keep the lid open.", view.Warning);
    }

    [Fact]
    public void Live_but_not_armed_with_the_lid_closed_still_leaves_the_mark_unlit()
    {
        Assert.Equal(MarkState.Idle, PanelModel.From(Snap(Mode.Idle, live: true, lid: LidState.Closed), 20).Mark);
    }

    [Fact]
    public void Paused_offers_resume_and_shows_only_the_battery()
    {
        var view = PanelModel.From(Snap(Mode.Paused, live: true, paused: true, hold: true), 20);

        Assert.Equal(PanelState.Paused, view.State);
        Assert.Equal("Paused.", view.Headline);
        Assert.Equal("Windows handles the lid until you resume.", view.SubLine);
        Assert.Equal("Resume", view.PauseLabel);
        Assert.True(view.HoldOn);
        Assert.Equal([new PanelFact("Battery", "81%")], view.Facts);
        Assert.Equal(MarkState.Idle, view.Mark);
        Assert.Equal("Nightcrew · paused", view.Tooltip);
    }

    [Theory]
    [InlineData(LidAction.Sleep, "Sleeps", "Sleep")]
    [InlineData(LidAction.Hibernate, "Hibernates", "Hibernate")]
    [InlineData(LidAction.ShutDown, "Hibernates", "Shut down")]
    [InlineData(LidAction.DoNothing, "Stays on", "Do nothing")]
    public void Lid_actions_read_plainly(LidAction action, string whenDone, string lidCloses)
    {
        Assert.Equal(whenDone, PanelModel.From(Snap(Mode.Armed, live: true, owner: action), 20).Facts[1].Value);
        Assert.Equal(lidCloses, PanelModel.From(Snap(owner: action), 20).Facts[1].Value);
    }

    [Theory]
    [InlineData(PowerSource.AC, 81, 20, "81%")]
    [InlineData(PowerSource.DC, 64, 20, "64% · stops at 20%")]
    [InlineData(PowerSource.DC, 64, 0, "64%")]
    public void Battery_shows_the_floor_only_on_battery(PowerSource source, int percent, int floor, string expected)
    {
        Assert.Equal(expected, PanelModel.From(Snap(source: source, battery: percent), floor).Facts[0].Value);
    }

    [Fact]
    public void No_battery_is_said_plainly()
    {
        Assert.Equal("No battery", PanelModel.From(Snap(battery: null), 20).Facts[0].Value);
    }

    [Theory]
    [InlineData(EngineWarning.PolicyBlocks, false, "Windows policy blocks lid changes here. Keep the lid open.")]
    [InlineData(EngineWarning.ArmBackoff, true, "Couldn't change power settings. Trying again in a few minutes.")]
    [InlineData(EngineWarning.RestorePending, true, "Your power settings aren't back yet. Retrying.")]
    [InlineData(EngineWarning.Stopped, true, "Stopped early at the battery floor or lid-closed limit. Open the lid to start again.")]
    [InlineData(EngineWarning.None, true, "The safety net isn't installed. Settings may stay changed if Nightcrew crashes.")]
    public void One_warning_shows_most_important_first(EngineWarning warning, bool watchdogMissing, string expected)
    {
        Assert.Equal(expected, PanelModel.From(Snap(warning: warning), 20, watchdogMissing).Warning);
    }

    [Fact]
    public void The_tooltip_never_exceeds_the_tray_limit()
    {
        var view = PanelModel.From(Snap(Mode.Armed, live: true, holders: new Holder(new string('x', 80), "busy")), 20);
        Assert.Equal(PanelModel.TooltipLimit, view.Tooltip.Length);
    }
}
