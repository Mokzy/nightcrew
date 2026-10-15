using System.Runtime.InteropServices;

namespace Nightcrew.Platform;

/// <summary>
/// The processes that own a window you could Alt+Tab to. Only reads windows; never sends them messages. A window on
/// another virtual desktop counts; one the app has cloaked itself doesn't.
/// </summary>
public static class WindowOwners
{
    private const int GWL_EXSTYLE = -20;
    private const uint GW_OWNER = 4;
    private const int WS_EX_TOOLWINDOW = 0x80;
    private const int WS_EX_APPWINDOW = 0x40000;
    private const int DWMWA_CLOAKED = 14;
    private const int DWM_CLOAKED_APP = 1;

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr window, uint command);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr window, int index);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out int processId);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);

    public static IReadOnlyCollection<int> List()
    {
        var owners = new HashSet<int>();
        EnumWindows((window, _) =>
        {
            if (Eligible(window) && GetWindowThreadProcessId(window, out var pid) != 0) owners.Add(pid);
            return true;
        }, IntPtr.Zero);
        return owners;
    }

    private static bool Eligible(IntPtr window)
    {
        if (!IsWindowVisible(window) || GetWindowTextLength(window) == 0) return false;
        var exStyle = GetWindowLong(window, GWL_EXSTYLE);
        var appWindow = (exStyle & WS_EX_APPWINDOW) != 0;
        if (!appWindow && ((exStyle & WS_EX_TOOLWINDOW) != 0 || GetWindow(window, GW_OWNER) != IntPtr.Zero)) return false;
        return DwmGetWindowAttribute(window, DWMWA_CLOAKED, out var cloaked, sizeof(int)) != 0 || cloaked != DWM_CLOAKED_APP;
    }
}
