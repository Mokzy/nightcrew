using Nightcrew.Core;
using Nightcrew.Platform;
using Xunit;

namespace Nightcrew.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nightcrew-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Missing_file_creates_defaults()
    {
        var path = Path.Combine(_dir, "settings.json");
        var settings = SettingsStore.Load(path);
        Assert.True(File.Exists(path));
        Assert.Equal(5, settings.GraceMinutes);
        Assert.Equal(20, settings.BatteryFloorPercent);
        Assert.Contains("claude", settings.AgentProcessNames);
    }

    [Fact]
    public void Missing_members_keep_defaults_when_loading_old_file()
    {
        var path = Path.Combine(_dir, "settings.json");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(path, "{\"BatteryFloorPercent\":35}");
        var settings = SettingsStore.Load(path);
        Assert.Equal(35, settings.BatteryFloorPercent);
        Assert.Equal(8, settings.LidClosedCapHours);
        Assert.NotEmpty(settings.TranscriptRoots);
    }

    [Fact]
    public void Corrupt_file_is_set_aside_and_defaults_are_used()
    {
        var path = Path.Combine(_dir, "settings.json");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(path, "{ this is not json");
        var settings = SettingsStore.Load(path);
        Assert.Equal(5, settings.GraceMinutes);
        Assert.True(File.Exists(path + ".corrupt"));
        Assert.Equal(5, SettingsStore.Load(path).GraceMinutes);
    }

    [Theory]
    [InlineData("\0\0\0\0")]
    [InlineData("null")]
    public void Unreadable_file_of_any_kind_is_set_aside_and_defaults_are_used(string text)
    {
        var path = WriteSettings(text);
        var settings = SettingsStore.Load(path);
        Assert.Equal(5, settings.GraceMinutes);
        Assert.True(File.Exists(path + ".corrupt"));
    }

    [Fact]
    public void Null_lists_become_defaults()
    {
        var path = WriteSettings("{\"TranscriptRoots\":null,\"AgentProcessNames\":null,\"IgnorePathContains\":null,\"SelfCpuAgentNames\":null," +
            "\"LeaveAlone\":null,\"LeaveAloneLabels\":null,\"ConnectorHostNames\":null,\"Mark\":null,\"ClaudeSessionsDir\":null}");
        var settings = SettingsStore.Load(path);
        var defaults = new Settings();
        Assert.Equal(defaults.TranscriptRoots, settings.TranscriptRoots);
        Assert.Equal(defaults.AgentProcessNames, settings.AgentProcessNames);
        Assert.Equal(defaults.IgnorePathContains, settings.IgnorePathContains);
        Assert.Equal(defaults.SelfCpuAgentNames, settings.SelfCpuAgentNames);
        Assert.Equal(defaults.LeaveAlone, settings.LeaveAlone);
        Assert.Empty(settings.LeaveAloneLabels);
        Assert.Equal(defaults.ConnectorHostNames, settings.ConnectorHostNames);
        Assert.Equal("lantern", settings.Mark);
        Assert.Equal(@"%USERPROFILE%\.claude\sessions", settings.ClaudeSessionsDir);
    }

    [Theory]
    [InlineData("GraceMinutes", 0, 1)]
    [InlineData("GraceMinutes", 500, 60)]
    [InlineData("AwayMinutes", -3, 1)]
    [InlineData("SampleSeconds", 0, 1)]
    [InlineData("SampleSeconds", 999, 60)]
    [InlineData("GpuPercent", 0, 5)]
    [InlineData("ToolCpuPercent", 0, 1)]
    [InlineData("SelfCpuPercent", 0, 0.5)]
    [InlineData("BatteryFloorPercent", 99, 90)]
    [InlineData("BatteryFloorPercent", -1, 0)]
    [InlineData("LidClosedCapHours", 100, 48)]
    [InlineData("LidClosedCapHours", -2, 0)]
    public void Numbers_are_clamped_into_range(string key, double value, double expected)
    {
        var path = WriteSettings($"{{\"{key}\":{value.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}");
        var settings = SettingsStore.Load(path);
        Assert.Equal(expected, Convert.ToDouble(typeof(Settings).GetProperty(key)!.GetValue(settings)));
    }

    [Fact]
    public void Unknown_mark_becomes_the_lantern()
    {
        Assert.Equal("lantern", SettingsStore.Load(WriteSettings("{\"Mark\":\"comet\"}")).Mark);
    }

    [Fact]
    public void New_keys_have_their_defaults()
    {
        var settings = SettingsStore.Load(WriteSettings("{}"));
        Assert.True(settings.StayAwakeForApprovals);
        Assert.Equal(@"%USERPROFILE%\.claude\sessions", settings.ClaudeSessionsDir);
    }

    [Fact]
    public void Load_reports_whether_the_file_was_created()
    {
        var path = Path.Combine(_dir, "settings.json");
        SettingsStore.Load(path, out var first);
        SettingsStore.Load(path, out var second);
        Assert.True(first);
        Assert.False(second);
    }

    private string WriteSettings(string text)
    {
        var path = Path.Combine(_dir, "settings.json");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public void Save_then_load_round_trips()
    {
        var path = Path.Combine(_dir, "settings.json");
        var settings = new Settings
        {
            GraceMinutes = 7, LockOnLidClose = false, LeaveAlone = ["olk"], LeaveAloneLabels = new() { ["olk"] = "Microsoft Outlook" },
        };
        SettingsStore.Save(path, settings);
        var loaded = SettingsStore.Load(path);
        Assert.Equal(7, loaded.GraceMinutes);
        Assert.False(loaded.LockOnLidClose);
        Assert.Equal(["olk"], loaded.LeaveAlone);
        Assert.Equal("Microsoft Outlook", loaded.LeaveAloneLabels["olk"]);
        Assert.Contains("\"LeaveAloneLabels\":{\"olk\":\"Microsoft Outlook\"}", File.ReadAllText(path));
        Assert.False(File.Exists(path + ".tmp"));
    }
}
