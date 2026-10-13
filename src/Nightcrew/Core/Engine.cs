namespace Nightcrew.Core;

/// <summary>One tick: sample activity, ask the controller, carry out its commands.</summary>
public sealed class Engine(ActivityMonitor activity, Controller controller, RestoreJournal journal,
    IPowerPolicy policy, IKeepAwake keepAwake, ISystemPower power, ILog log, IClock clock)
{
    private static readonly TimeSpan CanWriteCheckEvery = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RestoreRetryEvery = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ArmRetryAfter = TimeSpan.FromMinutes(5);

    private TimeSpan? _canWriteCheckedAt;
    private bool _canWrite;
    private string? _loggedBlock;
    private TimeSpan _restoreTriedAt = clock.Now; // so the first retry comes at least a minute after startup
    private TimeSpan? _armBlockedUntil;

    public LidState Lid { get; set; }

    public bool Paused { get; set; }

    public string Status => activity.Reason is { } reason ? $"{controller.Status} ({reason})" : controller.Status;

    /// <summary>Never throws: failures are logged, and a failing command does not stop the ones after it.</summary>
    public void Tick()
    {
        try
        {
            var now = clock.Now;
            activity.Sample(now);
            var source = power.Source;
            var ownerLid = (journal.OwnerValues ?? policy.Read()).LidFor(source);
            var canWrite = CanWrite(now) && !(_armBlockedUntil is { } until && now < until);
            var inputs = new Inputs(activity.Live, Lid, source, power.BatteryPercent, power.IdleTime, Paused, canWrite, ownerLid);
            foreach (var command in controller.Step(inputs, now)) Run(command, now);
            RetryRestore(now);
        }
        catch (Exception ex)
        {
            log.Warn($"Tick failed: {ex.Message}");
        }
    }

    /// <summary>Releases the keep-awake request and restores the owner's settings (quit, session end, startup).</summary>
    public void RestoreNow() => ReleaseAndRestore(clock.Now);

    private void ReleaseAndRestore(TimeSpan now)
    {
        _restoreTriedAt = now;
        keepAwake.Release();
        journal.Restore();
    }

    /// <summary>A restore that failed left its record behind: try again, at most once a minute, whenever we are not armed.</summary>
    private void RetryRestore(TimeSpan now)
    {
        if (controller.Mode == Mode.Armed || !journal.HasRecord) return;
        if (now - _restoreTriedAt < RestoreRetryEvery) return;
        log.Info("Retrying the restore of the power settings");
        ReleaseAndRestore(now);
    }

    private void Run(Command command, TimeSpan now)
    {
        log.Info($"{command} (activity: {activity.Reason ?? "none"})");
        try
        {
            switch (command)
            {
                case Command.Arm:
                    journal.Arm();
                    keepAwake.Hold();
                    break;
                case Command.Disarm:
                    ReleaseAndRestore(now);
                    break;
                case Command.Lock:
                    power.LockWorkstation();
                    break;
                case Command.MonitorOff:
                    power.MonitorOff();
                    break;
                case Command.Hibernate:
                    if (!power.Hibernate())
                    {
                        log.Warn("Hibernate failed; turning the screen off instead");
                        power.MonitorOff();
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            log.Warn($"{command} failed: {ex.Message}");
            if (command != Command.Arm) return;
            // Undo a half-done arm and go back to Idle. Arming waits 5 minutes, so a persistent failure doesn't churn every tick.
            ReleaseAndRestore(now);
            controller.Reset();
            _armBlockedUntil = now + ArmRetryAfter;
            log.Info("Not arming again for 5 minutes");
        }
    }

    /// <summary>Checks at most every five minutes; logs a block once per reason.</summary>
    private bool CanWrite(TimeSpan now)
    {
        if (_canWriteCheckedAt is { } at && now - at < CanWriteCheckEvery) return _canWrite;
        _canWriteCheckedAt = now;
        _canWrite = policy.CanWrite(out var reason);
        if (_canWrite)
        {
            _loggedBlock = null;
        }
        else if (reason != _loggedBlock)
        {
            log.Warn($"Can't change power settings: {reason}");
            _loggedBlock = reason;
        }
        return _canWrite;
    }
}
