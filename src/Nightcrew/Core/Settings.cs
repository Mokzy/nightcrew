using System.Runtime.Serialization;

namespace Nightcrew.Core;

[DataContract]
public sealed class Settings
{
    public Settings() => ApplyDefaults();

    [DataMember] public int GraceMinutes { get; set; }
    [DataMember] public int BatteryFloorPercent { get; set; }
    [DataMember] public int LidClosedCapHours { get; set; }
    [DataMember] public int AwayMinutes { get; set; }
    [DataMember] public bool LockOnLidClose { get; set; }
    [DataMember] public bool StartAtLogin { get; set; }
    [DataMember] public bool DryRun { get; set; }
    [DataMember] public string[] TranscriptRoots { get; set; } = [];
    [DataMember] public string[] IgnorePathContains { get; set; } = [];
    [DataMember] public string[] AgentProcessNames { get; set; } = [];
    [DataMember] public string[] SelfCpuAgentNames { get; set; } = [];
    [DataMember] public double SelfCpuPercent { get; set; }
    [DataMember] public double ToolCpuPercent { get; set; }
    [DataMember] public double GpuPercent { get; set; }
    [DataMember] public int SampleSeconds { get; set; }
    [DataMember] public string Mark { get; set; } = MarkKinds.LanternName;
    [DataMember] public bool EfficiencyWhileAway { get; set; }
    [DataMember] public string[] LeaveAlone { get; set; } = [];

    /// <summary>The name Settings shows for each app on <see cref="LeaveAlone"/>, by process name.</summary>
    [DataMember] public Dictionary<string, string> LeaveAloneLabels { get; set; } = [];

    [DataMember] public string[] ConnectorHostNames { get; set; } = [];

    /// <summary>With the lid closed, a Claude Code session waiting for your approval or answer counts as work.</summary>
    [DataMember] public bool StayAwakeForApprovals { get; set; }

    /// <summary>The folder where Claude Code keeps one small status file per running session.</summary>
    [DataMember] public string ClaudeSessionsDir { get; set; } = "";

    [OnDeserializing]
    private void OnDeserializing(StreamingContext context) => ApplyDefaults();

    /// <summary>
    /// A hand-edited file can hold nulls and numbers that would stop every tick: nulls get their defaults and numbers
    /// are clamped into a range that works.
    /// </summary>
    [OnDeserialized]
    private void OnDeserialized(StreamingContext context)
    {
        var defaults = new Settings();
        TranscriptRoots ??= defaults.TranscriptRoots;
        IgnorePathContains ??= defaults.IgnorePathContains;
        AgentProcessNames ??= defaults.AgentProcessNames;
        SelfCpuAgentNames ??= defaults.SelfCpuAgentNames;
        LeaveAlone ??= defaults.LeaveAlone;
        LeaveAloneLabels ??= defaults.LeaveAloneLabels;
        ConnectorHostNames ??= defaults.ConnectorHostNames;
        ClaudeSessionsDir ??= defaults.ClaudeSessionsDir;
        Mark = MarkKinds.Name(MarkKinds.Parse(Mark));

        GraceMinutes = Clamp(GraceMinutes, 1, 60);
        AwayMinutes = Clamp(AwayMinutes, 1, 60);
        SampleSeconds = Clamp(SampleSeconds, 1, 60);
        GpuPercent = Clamp(GpuPercent, 5, 100);
        ToolCpuPercent = Clamp(ToolCpuPercent, 1, 100);
        SelfCpuPercent = Clamp(SelfCpuPercent, 0.5, 100);
        BatteryFloorPercent = Clamp(BatteryFloorPercent, 0, 90);
        LidClosedCapHours = Clamp(LidClosedCapHours, 0, 48);
    }

    private static int Clamp(int value, int low, int high) => Math.Min(Math.Max(value, low), high);

    private static double Clamp(double value, double low, double high) =>
        double.IsNaN(value) ? low : Math.Min(Math.Max(value, low), high);

    private void ApplyDefaults()
    {
        GraceMinutes = 5;
        BatteryFloorPercent = 20;
        LidClosedCapHours = 8;
        AwayMinutes = 5;
        LockOnLidClose = true;
        StartAtLogin = true;
        DryRun = false;
        TranscriptRoots = [@"%USERPROFILE%\.claude\projects", @"%USERPROFILE%\.codex\sessions"];
        IgnorePathContains = ["claude-mem-observer"];
        AgentProcessNames = ["claude", "codex"];
        SelfCpuAgentNames = ["codex"];
        SelfCpuPercent = 2;
        ToolCpuPercent = 10;
        GpuPercent = 30;
        SampleSeconds = 10;
        Mark = MarkKinds.LanternName;
        EfficiencyWhileAway = true;
        LeaveAlone = [];
        LeaveAloneLabels = [];
        ConnectorHostNames = ["chrome-native-host", "extension-host"];
        StayAwakeForApprovals = true;
        ClaudeSessionsDir = @"%USERPROFILE%\.claude\sessions";
    }
}
