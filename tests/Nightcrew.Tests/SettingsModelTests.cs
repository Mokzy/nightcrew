using Nightcrew.Core;
using Xunit;

namespace Nightcrew.Tests;

public sealed class SettingsModelTests
{
    [Fact]
    public void Efficiency_mode_is_on_by_default_and_the_leave_alone_list_is_sorted()
    {
        Assert.True(SettingsModel.From(new Settings()).EfficiencyWhileAway);
        Assert.Empty(SettingsModel.From(new Settings()).LeaveAlone!);

        var page = SettingsModel.From(new Settings { EfficiencyWhileAway = false, LeaveAlone = ["whatsapp.root", "Olk", "discord"] });

        Assert.False(page.EfficiencyWhileAway);
        Assert.Equal(["discord", "Olk", "whatsapp.root"], page.LeaveAlone);
    }

    [Fact]
    public void Defaults_select_20_percent_8_hours_and_the_lantern()
    {
        var page = SettingsModel.From(new Settings());

        Assert.Equal(["10%", "15%", "20%", "30%", "Off"], page.BatteryFloor.Select(choice => choice.Label));
        Assert.Equal([10, 15, 20, 30, 0], page.BatteryFloor.Select(choice => choice.Value));
        Assert.Equal("20%", page.BatteryFloor.Single(choice => choice.Selected).Label);

        Assert.Equal(["2 h", "4 h", "8 h", "12 h", "Off"], page.LidClosedLimit.Select(choice => choice.Label));
        Assert.Equal("8 h", page.LidClosedLimit.Single(choice => choice.Selected).Label);

        Assert.Equal(["Lantern", "Moon and star"], page.Mark.Select(choice => choice.Label));
        Assert.Equal(MarkKind.Lantern, page.Mark.Single(choice => choice.Selected).Value);

        Assert.True(page.LockOnLidClose);
        Assert.True(page.StartAtLogin);
    }

    [Fact]
    public void The_page_follows_the_settings()
    {
        var page = SettingsModel.From(new Settings
        {
            BatteryFloorPercent = 0, LidClosedCapHours = 12, LockOnLidClose = false, StartAtLogin = false, Mark = "moon",
        });

        Assert.Equal("Off", page.BatteryFloor.Single(choice => choice.Selected).Label);
        Assert.Equal("12 h", page.LidClosedLimit.Single(choice => choice.Selected).Label);
        Assert.Equal(MarkKind.MoonAndStar, page.Mark.Single(choice => choice.Selected).Value);
        Assert.False(page.LockOnLidClose);
        Assert.False(page.StartAtLogin);
    }

    [Fact]
    public void A_value_set_by_hand_in_settings_json_shows_as_its_own_choice()
    {
        var page = SettingsModel.From(new Settings { BatteryFloorPercent = 25, LidClosedCapHours = 6 });

        Assert.Equal(["10%", "15%", "20%", "25%", "30%", "Off"], page.BatteryFloor.Select(choice => choice.Label));
        Assert.Equal("25%", page.BatteryFloor.Single(choice => choice.Selected).Label);
        Assert.Equal(["2 h", "4 h", "6 h", "8 h", "12 h", "Off"], page.LidClosedLimit.Select(choice => choice.Label));
        Assert.Equal("6 h", page.LidClosedLimit.Single(choice => choice.Selected).Label);
    }

    [Fact]
    public void A_negative_value_set_by_hand_counts_as_off()
    {
        var page = SettingsModel.From(new Settings { BatteryFloorPercent = -5, LidClosedCapHours = -1 });

        Assert.Equal(["10%", "15%", "20%", "30%", "Off"], page.BatteryFloor.Select(choice => choice.Label));
        Assert.Equal("Off", page.BatteryFloor.Single(choice => choice.Selected).Label);
        Assert.Equal("Off", page.LidClosedLimit.Single(choice => choice.Selected).Label);
    }
}
