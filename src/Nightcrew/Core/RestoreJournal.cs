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

public sealed class RestoreJournal(IPowerPolicy policy, string path, ILog log)
{
    /// <summary>Lid action Do nothing and sleep-after Never, on AC and DC.</summary>
    public static readonly PowerValues ArmValues = new(0, 0, 0, 0);

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
        var record = Load(out _);
        if (record is not null && RecordedScheme(record) is { } recorded && recorded != active)
        {
            var restored = PutBack(record, recorded);
            File.Delete(path);
            log.Info($"Restored power settings {restored} of the previous plan");
            record = null;
        }
        if (record is null)
        {
            var owner = policy.Read(active);
            var saved = new RestoreRecord { Owner = owner, Written = ArmValues, Scheme = active.ToString("D"), CreatedUtc = DateTime.UtcNow };
            AtomicFile.WriteAllText(path, Json.Serialize(saved));
            log.Info($"Saved owner power settings {owner}");
        }
        policy.Write(active, ArmValues);
    }

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
