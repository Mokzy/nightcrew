using System.Security.Principal;
using System.Xml.Linq;

namespace Nightcrew.Core;

/// <summary>Task Scheduler definition for the watchdog that restores power settings if the app dies.</summary>
public static class WatchdogTaskXml
{
    private static readonly XNamespace T = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    public static string Build(string exePath)
    {
        var user = WindowsIdentity.GetCurrent().Name;
        var task = new XElement(T + "Task", new XAttribute("version", "1.2"),
            new XElement(T + "RegistrationInfo",
                new XElement(T + "Author", "Nightcrew"),
                new XElement(T + "Description",
                    "Restores your power settings if Nightcrew stops while keeping the PC awake. Remove with: nightcrew --uninstall")),
            new XElement(T + "Triggers",
                new XElement(T + "LogonTrigger",
                    new XElement(T + "Enabled", "true"),
                    new XElement(T + "UserId", user)),
                new XElement(T + "TimeTrigger",
                    new XElement(T + "Repetition", new XElement(T + "Interval", "PT5M")),
                    new XElement(T + "StartBoundary", "2026-01-01T00:00:00"),
                    new XElement(T + "Enabled", "true"))),
            new XElement(T + "Principals",
                new XElement(T + "Principal", new XAttribute("id", "Author"),
                    new XElement(T + "UserId", user),
                    new XElement(T + "LogonType", "InteractiveToken"))),
            new XElement(T + "Settings",
                new XElement(T + "MultipleInstancesPolicy", "IgnoreNew"),
                new XElement(T + "DisallowStartIfOnBatteries", "false"),
                new XElement(T + "StopIfGoingOnBatteries", "false"),
                new XElement(T + "StartWhenAvailable", "true"),
                new XElement(T + "Hidden", "true"),
                new XElement(T + "ExecutionTimeLimit", "PT2M")),
            new XElement(T + "Actions", new XAttribute("Context", "Author"),
                new XElement(T + "Exec",
                    new XElement(T + "Command", "\"" + exePath + "\""),
                    new XElement(T + "Arguments", "--watchdog"))));

        return "<?xml version=\"1.0\" encoding=\"UTF-16\"?>" + Environment.NewLine + task;
    }
}
