namespace Nightcrew.Core;

public enum PowerSource { AC, DC }

/// <summary>Windows lid-close action indices as stored in the power scheme.</summary>
public enum LidAction : uint { DoNothing = 0, Sleep = 1, Hibernate = 2, ShutDown = 3 }

public enum LidState { Unknown, Open, Closed }

/// <summary>Lid action indices and sleep-after seconds (0 = never) of the active power scheme.</summary>
public readonly record struct PowerValues(uint LidAc, uint LidDc, uint StandbyAc, uint StandbyDc)
{
    public LidAction LidFor(PowerSource source) => (LidAction)(source == PowerSource.AC ? LidAc : LidDc);
}

public interface IClock
{
    /// <summary>Monotonic time that excludes sleep (QueryUnbiasedInterruptTime).</summary>
    TimeSpan Now { get; }
    DateTime UtcNow { get; }
}

public interface IPowerPolicy
{
    PowerValues Read();
    /// <summary>Writes all four values to the active scheme, re-activates it and verifies the read-back. Throws on failure.</summary>
    void Write(PowerValues values);
    bool CanWrite(out string reason);
}

public interface IKeepAwake
{
    bool IsHeld { get; }
    void Hold();
    void Release();
}

public interface ISystemPower
{
    PowerSource Source { get; }
    /// <summary>Null when there is no battery or the level is unknown.</summary>
    int? BatteryPercent { get; }
    /// <summary>Time since the last keyboard or mouse input.</summary>
    TimeSpan IdleTime { get; }
    bool HibernateAvailable { get; }
    /// <summary>Returns false if hibernation failed or is unavailable.</summary>
    bool Hibernate();
    void MonitorOff();
    void LockWorkstation();
}

public interface ILog
{
    void Info(string message);
    void Warn(string message);
}

public interface IBusySignal
{
    string Name { get; }
    /// <summary>Takes one sample; returns true if the source was busy during it.</summary>
    bool Sample(TimeSpan now);
}
