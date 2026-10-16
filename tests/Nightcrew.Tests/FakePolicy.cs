using Nightcrew.Core;

namespace Nightcrew.Tests;

/// <summary>Power plans in memory: one active plan to begin with, more with <see cref="AddScheme"/>. Never touches Windows.</summary>
internal sealed class FakePolicy : IPowerPolicy
{
    public const string BlockedReason = "policy blocks lid action AC (5)";

    private readonly Dictionary<Guid, PowerValues> _schemes = [];
    private readonly HashSet<Guid> _failWritesTo = [];
    private readonly List<string>? _calls;

    public FakePolicy(PowerValues initial, List<string>? calls = null)
    {
        _calls = calls;
        Active = Guid.NewGuid();
        _schemes[Active] = initial;
    }

    public Guid Active { get; private set; }

    /// <summary>The active plan's values.</summary>
    public PowerValues Values
    {
        get => _schemes[Active];
        set => _schemes[Active] = value;
    }

    public int Writes { get; private set; }
    public bool FailWrites { get; set; }
    public bool FailReads { get; set; }
    public bool CanWriteResult { get; set; } = true;
    public int CanWriteChecks { get; private set; }

    /// <summary>What ReadDefaults returns; null makes it throw.</summary>
    public PowerValues? Defaults { get; set; } = new PowerValues(1, 1, 1800, 900);

    public Guid AddScheme(PowerValues values)
    {
        var scheme = Guid.NewGuid();
        _schemes[scheme] = values;
        return scheme;
    }

    public void Activate(Guid scheme) => Active = scheme;

    /// <summary>The plan is deleted: reading or writing it throws, as Windows does.</summary>
    public void RemoveScheme(Guid scheme) => _schemes.Remove(scheme);

    public bool SchemeExists(Guid scheme) => _schemes.ContainsKey(scheme);

    public PowerValues ValuesOf(Guid scheme) => _schemes[scheme];

    public void FailWritesTo(Guid scheme) => _failWritesTo.Add(scheme);

    public Guid ActiveScheme() => Active;

    public PowerValues Read(Guid scheme) =>
        FailReads ? throw new InvalidOperationException("PowerReadACValueIndex failed (5)") : _schemes[scheme];

    public void Write(Guid scheme, PowerValues values)
    {
        if (FailWrites || _failWritesTo.Contains(scheme)) throw new InvalidOperationException("PowerWriteACValueIndex failed (5)");
        _schemes[scheme] = values;
        Writes++;
        _calls?.Add("Write");
    }

    public PowerValues ReadDefaults(Guid scheme) =>
        Defaults ?? throw new InvalidOperationException("PowerReadACDefaultIndex failed (2)");

    public bool CanWrite(out string reason)
    {
        CanWriteChecks++;
        reason = CanWriteResult ? "" : BlockedReason;
        return CanWriteResult;
    }
}
