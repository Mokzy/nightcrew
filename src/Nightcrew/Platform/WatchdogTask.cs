using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Nightcrew.Core;

namespace Nightcrew.Platform;

/// <summary>Registers the watchdog with schtasks.exe; no admin rights needed for the current user.</summary>
public static class WatchdogTask
{
    public const string Name = "Nightcrew Watchdog";

    public static bool Register(string exePath, ILog log)
    {
        var xmlPath = Path.Combine(Path.GetTempPath(), "nightcrew-watchdog-" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            File.WriteAllText(xmlPath, WatchdogTaskXml.Build(exePath), Encoding.Unicode);
            return Schtasks($"/Create /TN \"{Name}\" /XML \"{xmlPath}\" /F", log);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Warn($"Watchdog task: could not write {xmlPath}: {ex.Message}");
            return false;
        }
        finally
        {
            try { File.Delete(xmlPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    public static bool Unregister(ILog log) => Schtasks($"/Delete /TN \"{Name}\" /F", log);

    private static bool Schtasks(string arguments, ILog log)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        try
        {
            using var process = Process.Start(start)!;
            var error = process.StandardError.ReadToEnd().Trim();
            process.WaitForExit();
            if (process.ExitCode == 0)
            {
                log.Info($"Watchdog task: schtasks {arguments} succeeded");
                return true;
            }
            log.Warn($"Watchdog task: schtasks {arguments} failed ({process.ExitCode}): {error}");
            return false;
        }
        catch (Win32Exception ex)
        {
            log.Warn($"Watchdog task: could not run schtasks.exe: {ex.Message}");
            return false;
        }
    }
}
