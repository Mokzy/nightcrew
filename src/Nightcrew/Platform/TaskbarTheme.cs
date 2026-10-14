using Microsoft.Win32;

namespace Nightcrew.Platform;

public static class TaskbarTheme
{
    /// <summary>True when Windows draws a light taskbar. Read each time, so a theme switch shows on the next tick.</summary>
    public static bool IsLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int value && value == 1;
        }
        catch (Exception)
        {
            return false; // the default taskbar is dark
        }
    }
}
