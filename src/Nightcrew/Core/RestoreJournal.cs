using System.Runtime.Serialization;
using Nightcrew.Platform;

namespace Nightcrew.Core;

/// <summary>What the owner had before we armed, and what we wrote over it.</summary>
[DataContract]
public sealed class RestoreRecord
{
    [DataMember(IsRequired = true)] public uint OwnerLidAc { get; set; }
    [DataMember(IsRequired = true)] public uint OwnerLidDc { get; set; }
    [DataMember(IsRequired = true)] public uint OwnerStandbyAc { get; set; }
    [DataMember(IsRequired = true)] public uint OwnerStandbyDc { get; set; }
    [DataMember(IsRequired = true)] public uint WrittenLidAc { get; set; }
    [DataMember(IsRequired = true)] public uint WrittenLidDc { get; set; }
    [DataMember(IsRequired = true)] public uint WrittenStandbyAc { get; set; }
    [DataMember(IsRequired = true)] public uint WrittenStandbyDc { get; set; }
    [DataMember] public DateTime CreatedUtc { get; set; }

    /// <summary>The power plan (scheme GUID, "D" format) the values belong to; null in a record from v0.2.2 or earlier.</summary>
    [DataMember(EmitDefaultValue = false)] public string? Scheme { get; set; }

    public PowerValues Owner
    {
        get => new(OwnerLidAc, OwnerLidDc, OwnerStandbyAc, OwnerStandbyDc);
        set => (OwnerLidAc, OwnerLidDc, OwnerStandbyAc, OwnerStandbyDc) = (value.LidAc, value.LidDc, value.StandbyAc, value.StandbyDc);
    }

    public PowerValues Written
    {
        get => new(WrittenLidAc, WrittenLidDc, WrittenStandbyAc, WrittenStandbyDc);
        set => (WrittenLidAc, WrittenLidDc, WrittenStandbyAc, WrittenStandbyDc) = (value.LidAc, value.LidDc, value.StandbyAc, value.StandbyDc);
    }
}

/// <param name="armValues">What arming writes; <see cref="ArmValuesFor"/> of the lid-closed limit. Defaults to the 8-hour limit.</param>
public sealed class RestoreJournal(IPowerPolicy policy, string path, ILog log, Func<PowerValues>? armValues = null)
{
    /// <summary>Lid action Sleep, sleep after 30 minutes on AC and 15 on DC: used when Windows' own defaults can't be read.</summary>
    public static readonly PowerValues FallbackDefaults = new(1, 1, 1800, 900);

    private readonly Func<PowerValues> _armValues = armValues ?? (() => ArmValuesFor(8));

    /// <summary>
    /// Lid action Do nothing on AC and DC and sleep-after Never on AC. On DC sleep-after is the lid-closed limit plus an
    /// hour, so a crash followed by a restart can't leave the logon screen awake for ever; with no limit it is Never.
    /// </summary>
    public static PowerValues ArmValuesFor(int lidClosedCapHours) =>
        new(0, 0, 0, lidClosedCapHours > 0 ? (uint)(lidClosedCapHours + 1) * 3600 : 0);

    private PowerValues ArmValues => _armValues();

    public bool HasRecord => File.Exists(path);

    /// <summary>The owner's values from the record; null when there is no readable record.</summary>
    public PowerValues? OwnerValues
    {
        get
        {
            try
            {
                return File.Exists(path) ? Parse(File.ReadAllText(path)).Owner : null;
            }
            catch (Exception)
            {
                return null; // read every tick: a damaged or locked record must never stop the tick
            }
        }
    }

    /// <summary>True when the record names a power plan that isn't the active one any more. Never throws.</summary>
    public bool SchemeChanged
    {
        get
        {
            try
            {
                return File.Exists(path) && RecordedScheme(Parse(File.ReadAllText(path))) is { } recorded && recorded != policy.ActiveScheme();
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Saves the owner's values of the active plan once, then writes ArmValues to it. If the record belongs to another
    /// plan (the plan was switched while armed), that plan is put back first; if putting it back fails, this throws and
    /// the record stays as it is. Throws if reading or writing the policy fails.
    /// </summary>
    public void Arm()
    {
        var active = policy.ActiveScheme();
        var arm = ArmValues;
        var record = Load(out var corrupt);
        if (record is not null && RecordedScheme(record) is { } recorded && recorded != active)
        {
            var restored = PutBack(record, recorded);
            File.Delete(path);
            log.Info($"Restored power settings {restored} of the previous plan");
            record = null;
        }
        if (record is null)
        {
            var owner = OwnerValuesOf(active, arm, corrupt);
            Save(new RestoreRecord { Owner = owner, Written = arm, Scheme = active.ToString("D"), CreatedUtc = DateTime.UtcNow });
            log.Info($"Saved owner power settings {owner}");
        }
        else if (record.Written != arm)
        {
            // The lid-closed limit changed: what we write now must be recorded as ours, or a restore would keep it.
            record.Written = arm;
            Save(record);
        }
        policy.Write(active, arm);
    }

    /// <summary>
    /// The plan's current values, unless they are Nightcrew's own leftovers from a record that was lost: then Windows'
    /// defaults, so a lost record can never turn Do nothing into the owner's own setting. Only clear signs count: a
    /// record just set aside as corrupt while the lid actions are Do nothing, or an exact match with what Nightcrew
    /// writes, whose DC value nobody sets by hand. A docked owner's real Do nothing and Never stay theirs.
    /// </summary>
    private PowerValues OwnerValuesOf(Guid scheme, PowerValues arm, bool recordWasCorrupt)
    {
        var current = policy.Read(scheme);
        var looksLikeOurs = current.LidAc == 0 && current.LidDc == 0 && current.StandbyAc == 0;
        var lost = (recordWasCorrupt && looksLikeOurs) || (current == arm && arm.StandbyDc != 0);
        if (!lost) return current;
        log.Warn("No restore record, and the power settings look like Nightcrew's own; saving Windows' defaults as yours");
        try
        {
            return policy.ReadDefaults(scheme);
        }
        catch (Exception ex)
        {
            log.Warn($"Couldn't read Windows' default power settings, using sleep after 30 and 15 minutes: {ex.Message}");
            return FallbackDefaults;
        }
    }

    private void Save(RestoreRecord record) => AtomicFile.WriteAllText(path, Json.Serialize(record));

    /// <summary>
    /// Puts back each field we still own, to the plan the record names (the active plan for an old record), then deletes
    /// the record. False keeps the record for a retry.
    /// </summary>
    public bool Restore()
    {
        try
        {
            var record = Load(out var corrupt);
            if (record is null) return !corrupt;
            var result = PutBack(record, RecordedScheme(record) ?? policy.ActiveScheme());
            File.Delete(path);
            log.Info($"Restored power settings {result}");
            return true;
        }
        catch (Exception ex)
        {
            log.Warn($"Restore failed, record kept: {ex.Message}");
            return false;
        }
    }

    /// <summary>Writes the owner's values back over every field we still own in the scheme; returns what it holds now.</summary>
    private PowerValues PutBack(RestoreRecord record, Guid scheme)
    {
        var current = policy.Read(scheme);
        var owner = record.Owner;
        var written = record.Written;
        var result = new PowerValues(
            Pick(current.LidAc, written.LidAc, owner.LidAc),
            Pick(current.LidDc, written.LidDc, owner.LidDc),
            Pick(current.StandbyAc, written.StandbyAc, owner.StandbyAc),
            Pick(current.StandbyDc, written.StandbyDc, owner.StandbyDc));
        if (result != current) policy.Write(scheme, result);
        return result;
    }

    // A field the user changed since we armed is theirs; keep it.
    private static uint Pick(uint current, uint written, uint owner) => current == written ? owner : current;

    private static Guid? RecordedScheme(RestoreRecord record) => Guid.TryParse(record.Scheme, out var scheme) ? scheme : null;

    private static RestoreRecord Parse(string text) =>
        Json.Deserialize<RestoreRecord?>(text) ?? throw new SerializationException("Restore record is empty.");

    /// <summary>
    /// Null when there is no record; a corrupt record is renamed to .corrupt and also gives null. Any failure to parse
    /// counts as corrupt (a power cut can leave zeros, which throw XmlException); a file that can't be read, such as a
    /// locked one, throws as before and is left where it is.
    /// </summary>
    private RestoreRecord? Load(out bool corrupt)
    {
        corrupt = false;
        if (!File.Exists(path)) return null;
        var text = File.ReadAllText(path);
        try
        {
            return Parse(text);
        }
        catch (Exception ex) when (ex is not IOException and not UnauthorizedAccessException)
        {
            corrupt = true;
            var aside = path + ".corrupt";
            File.Delete(aside);
            File.Move(path, aside);
            log.Warn($"Restore record was corrupt, moved to {aside}: {ex.Message}");
            return null;
        }
    }
}
