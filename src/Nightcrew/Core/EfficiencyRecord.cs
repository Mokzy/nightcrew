using System.Runtime.Serialization;

namespace Nightcrew.Core;

/// <summary>One process Nightcrew put into Efficiency mode, and the priority to put back.</summary>
[DataContract]
public sealed class EfficiencyEntry
{
    [DataMember(IsRequired = true)] public int Pid { get; set; }
    [DataMember(IsRequired = true)] public long StartUtcTicks { get; set; }
    [DataMember(IsRequired = true)] public uint OriginalPriority { get; set; }
    [DataMember(IsRequired = true)] public int RootPid { get; set; }
    [DataMember(IsRequired = true)] public long RootStartUtcTicks { get; set; }
    [DataMember] public string Name { get; set; } = "";

    /// <summary>When Nightcrew put this process into Efficiency mode (UTC ticks); 0 in a record from v0.2.2 or earlier.</summary>
    [DataMember(EmitDefaultValue = false)] public long AppliedUtcTicks { get; set; }

    /// <summary>
    /// Started by a slowed process while it was slowed, so Windows gave it the idle priority: Nightcrew didn't slow it,
    /// but puts its priority back with the rest of the family.
    /// </summary>
    [DataMember(EmitDefaultValue = false)] public bool Inherited { get; set; }

    public ProcessRef Process => new(Pid, new DateTime(StartUtcTicks, DateTimeKind.Utc));

    public ProcessRef Root => new(RootPid, new DateTime(RootStartUtcTicks, DateTimeKind.Utc));
}

/// <summary>Everything in Efficiency mode, saved before each change so a crash can be undone.</summary>
[DataContract]
public sealed class EfficiencyRecord
{
    [DataMember(IsRequired = true)] public List<EfficiencyEntry> Entries { get; set; } = [];
}
