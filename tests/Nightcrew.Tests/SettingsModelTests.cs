using Nightcrew.Core;
using Xunit;

namespace Nightcrew.Tests;

public sealed class SettingsModelTests
{
    [Fact]
    public void Stay_awake_for_approvals_is_on_by_default_and_follows_the_setting()
    {
        Assert.True(SettingsModel.From(new Settings()).StayAwakeForApprovals);
        Assert.False(SettingsModel.From(new Settings { StayAwakeForApprovals = false }).StayAwakeForApprovals);
    }

    [Fact]
    public void Efficiency_mode_is_on_by_default_with_nothing_kept_at_full_speed()
    {
        var page = SettingsModel.From(new Settings());

        Assert.True(page.EfficiencyWhileAway);
        Assert.Empty(page.FullSpeed!);
        Assert.Null(page.Addable);
    }

    [Fact]
    public void Apps_kept_at_full_speed_show_by_their_label_sorted_and_fall_back_to_the_process_name()
    {
        var page = SettingsModel.From(new Settings
        {
            EfficiencyWhileAway = false,
            LeaveAlone = ["whatsapp.root", "Olk", "discord"],
            LeaveAloneLabels = new() { ["olk"] = "Microsoft Outlook", ["whatsapp.root"] = "WhatsApp" },
        });

        Assert.False(page.EfficiencyWhileAway);
        Assert.Equal([new FullSpeedApp("discord", "discord"), new FullSpeedApp("Microsoft Outlook", "Olk"), new FullSpeedApp("WhatsApp", "whatsapp.root")],
            page.FullSpeed);
    }

    [Fact]
    public void The_apps_to_add_are_sorted_and_skip_any_already_kept_at_full_speed()
    {
        var page = SettingsModel.From(new Settings { LeaveAlone = ["olk"] },
            [new NamedApp("Spotify", "spotify"), new NamedApp("Microsoft Outlook", "OLK"), new NamedApp("Discord", "discord")]);

        Assert.Equal([new NamedApp("Discord", "discord"), new NamedApp("Spotify", "spotify")], page.Addable);
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
