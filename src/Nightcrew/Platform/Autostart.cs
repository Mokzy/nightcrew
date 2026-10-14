using Microsoft.Win32;

namespace Nightcrew.Platform;

/// <summary>Start at login through HKCU\Software\Microsoft\Windows\CurrentVersion\Run.</summary>
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Nightcrew";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
        return key?.GetValue(ValueName) is string value && value.Length > 0;
    }

    public static void Set(bool enabled, string exePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
            key.SetValue(ValueName, "\"" + exePath + "\"", RegistryValueKind.String);
        else
            key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
