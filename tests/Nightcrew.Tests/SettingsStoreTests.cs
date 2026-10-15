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
