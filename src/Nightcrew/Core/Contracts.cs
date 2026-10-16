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

/// <summary>The lid action and sleep-after values of a power plan (scheme). Every method throws on failure.</summary>
public interface IPowerPolicy
{
    Guid ActiveScheme();
    PowerValues Read(Guid scheme);
    /// <summary>
    /// Writes all four values to the scheme, re-activates it only when it is the active scheme, and verifies the
    /// read-back. A scheme that isn't active can be written too; Windows applies it when it becomes active.
    /// </summary>
    void Write(Guid scheme, PowerValues values);
    /// <summary>Windows' default values for the scheme (PowerReadACDefaultIndex and PowerReadDCDefaultIndex).</summary>
    PowerValues ReadDefaults(Guid scheme);
    /// <summary>False when the plan has been deleted (or Windows' default plans were restored).</summary>
    bool SchemeExists(Guid scheme);
    bool CanWrite(out string reason);
}

public static class PowerPolicyExtensions
{
    public static PowerValues ReadActive(this IPowerPolicy policy) => policy.Read(policy.ActiveScheme());
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
