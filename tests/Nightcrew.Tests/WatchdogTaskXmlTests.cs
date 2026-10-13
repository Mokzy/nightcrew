using System.Security.Principal;
using System.Xml.Linq;
using Nightcrew.Core;
using Xunit;

namespace Nightcrew.Tests;

public sealed class WatchdogTaskXmlTests
{
    private static readonly XNamespace T = "http://schemas.microsoft.com/windows/2004/02/mit/task";
    private const string Exe = @"C:\Users\Test User\AppData\Local\Nightcrew\nightcrew.exe";

    private static XElement Task(string exe = Exe) => XDocument.Parse(WatchdogTaskXml.Build(exe)).Root!;

    private static XElement Child(XElement parent, string name) =>
        parent.Element(T + name) ?? throw new Xunit.Sdk.XunitException($"missing <{name}> under <{parent.Name.LocalName}>");

    [Fact]
    public void Declares_utf16_and_the_task_namespace()
    {
        var xml = WatchdogTaskXml.Build(Exe);
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"UTF-16\"?>", xml);
        var task = Task();
        Assert.Equal(T + "Task", task.Name);
        Assert.Equal("1.2", (string?)task.Attribute("version"));
    }

    [Fact]
    public void Triggers_at_logon_for_the_current_user_and_every_five_minutes()
    {
        var triggers = Child(Task(), "Triggers");
        var logon = Child(triggers, "LogonTrigger");
        Assert.Equal(WindowsIdentity.GetCurrent().Name, Child(logon, "UserId").Value);

        var time = Child(triggers, "TimeTrigger");
        Assert.Equal("PT5M", Child(Child(time, "Repetition"), "Interval").Value);
        Assert.True(DateTime.TryParse(Child(time, "StartBoundary").Value, out _));
    }

    [Fact]
    public void Settings_run_on_battery_briefly_hidden_and_once_at_a_time()
    {
        var settings = Child(Task(), "Settings");
        Assert.Equal("false", Child(settings, "DisallowStartIfOnBatteries").Value);
        Assert.Equal("false", Child(settings, "StopIfGoingOnBatteries").Value);
        Assert.Equal("PT2M", Child(settings, "ExecutionTimeLimit").Value);
        Assert.Equal("IgnoreNew", Child(settings, "MultipleInstancesPolicy").Value);
        Assert.Equal("true", Child(settings, "StartWhenAvailable").Value);
        Assert.Equal("true", Child(settings, "Hidden").Value);
    }

    [Fact]
    public void Runs_with_the_interactive_token_of_the_current_user()
    {
        var principal = Child(Child(Task(), "Principals"), "Principal");
        Assert.Equal("InteractiveToken", Child(principal, "LogonType").Value);
        Assert.Equal(WindowsIdentity.GetCurrent().Name, Child(principal, "UserId").Value);
    }

    [Fact]
    public void Action_runs_the_quoted_exe_in_watchdog_mode()
    {
        var exec = Child(Child(Task(), "Actions"), "Exec");
        Assert.Equal("\"" + Exe + "\"", Child(exec, "Command").Value);
        Assert.Equal("--watchdog", Child(exec, "Arguments").Value);
    }

    [Fact]
    public void Registration_info_names_the_author_and_how_to_uninstall()
    {
        var info = Child(Task(), "RegistrationInfo");
        Assert.Equal("Nightcrew", Child(info, "Author").Value);
        Assert.Contains("nightcrew --uninstall", Child(info, "Description").Value);
    }

    [Fact]
    public void Exe_path_with_xml_special_characters_survives()
    {
        const string odd = @"C:\Tools & <Stuff>\nightcrew.exe";
        var exec = Child(Child(Task(odd), "Actions"), "Exec");
        Assert.Equal("\"" + odd + "\"", Child(exec, "Command").Value);
    }
}
