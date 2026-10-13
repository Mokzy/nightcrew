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

    [OnDeserializing]
    private void OnDeserializing(StreamingContext context) => ApplyDefaults();

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
    }
}
