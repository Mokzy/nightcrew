namespace Nightcrew.Platform;

public static class AppPaths
{
    public static string DataDir { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nightcrew");

    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string RestoreFile => Path.Combine(DataDir, "restore.json");
    public static string LogFile => Path.Combine(DataDir, "nightcrew.log");
}
