namespace Nightcrew.Core;

/// <summary>What needs the owner's attention, most important first.</summary>
public enum EngineWarning { None, PolicyBlocks, ArmBackoff, RestorePending, Stopped }

/// <summary>
/// Everything the tray and the panel show, taken at the end of a tick. <paramref name="EfficiencyApps"/> counts the apps in
/// Efficiency mode now.
/// </summary>
public sealed record EngineSnapshot(Mode Mode, LidState Lid, bool Live, IReadOnlyList<Holder> Holders, PowerSource Source,
    int? BatteryPercent, LidAction OwnerLid, bool Hold, bool Paused, EngineWarning Warning, int EfficiencyApps = 0)
{
    public static EngineSnapshot Initial { get; } =
        new(Mode.Idle, LidState.Unknown, false, [], PowerSource.AC, null, LidAction.Sleep, false, false, EngineWarning.None);
}

/// <summary>One tick: sample activity, ask the controller, carry out its commands.</summary>
public sealed class Engine(ActivityMonitor activity, Controller controller, RestoreJournal journal,
    IPowerPolicy policy, IKeepAwake keepAwake, ISystemPower power, ILog log, IClock clock, EfficiencyCoordinator? efficiency = null)
{
    private static readonly TimeSpan CanWriteCheckEvery = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RestoreRetryEvery = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ArmRetryAfter = TimeSpan.FromMinutes(5);

    private TimeSpan? _canWriteCheckedAt;
    private bool _canWrite;
    private string? _loggedBlock;
    private TimeSpan _restoreTriedAt = clock.Now; // so the first retry comes at least a minute after startup
    private TimeSpan? _armBlockedUntil;
    private volatile bool _suspended;
    private string? _loggedHolders;

    public LidState Lid { get; set; }

    public bool Paused { get; set; }

    public string Status => activity.Reason is { } reason ? $"{controller.Status} ({reason})" : controller.Status;

    /// <summary>Taken at the end of every tick; <see cref="EngineSnapshot.Initial"/> before the first one.</summary>
    public EngineSnapshot Snapshot { get; private set; } = EngineSnapshot.Initial;

    /// <summary>Never throws: failures are logged, and a failing command does not stop the ones after it.</summary>
    public void Tick()
    {
        try
        {
            var now = clock.Now;
            if (_suspended)
            {
                _suspended = false;
                controller.OnResume();
            }
            activity.Sample(now);
            LogHoldersWhenTheyChange(activity.Holders);
            var source = power.Source;
            var battery = power.BatteryPercent;
            var ownerLid = (journal.OwnerValues ?? policy.ReadActive()).LidFor(source);
            var policyAllows = CanWrite(now);
            var backingOff = _armBlockedUntil is { } until && now < until;
            var inputs = new Inputs(activity.Live, Lid, source, battery, power.IdleTime, Paused, policyAllows && !backingOff, ownerLid);
            foreach (var command in controller.Step(inputs, now)) Run(command, now);
            FollowPlanSwitch();
            efficiency?.Step(controller.Mode, Lid, inputs.IdleTime, ownerLid);
            RetryRestore(now);

            var warning = !policyAllows ? EngineWarning.PolicyBlocks
                : _armBlockedUntil is { } blockedUntil && now < blockedUntil ? EngineWarning.ArmBackoff
                : controller.Mode != Mode.Armed && journal.HasRecord ? EngineWarning.RestorePending
                : controller.Spent ? EngineWarning.Stopped
                : EngineWarning.None;
            Snapshot = new EngineSnapshot(controller.Mode, Lid, activity.Live, activity.Holders, source, battery,
                ownerLid, activity.Hold, Paused, warning, efficiency?.AppCount ?? 0);
        }
        catch (Exception ex)
        {
            log.Warn($"Tick failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Called on PBT_APMSUSPEND without taking any lock. The next tick ends a Finishing run first, so a tick that
    /// fires late after wake (lid opening, lid event not yet handled) can't hibernate the laptop in front of the user.
    /// </summary>
    public void NoteSuspend() => _suspended = true;

    /// <summary>Releases the keep-awake request and restores the owner's settings (quit, session end, startup).</summary>
    public void RestoreNow() => ReleaseAndRestore(clock.Now);

    /// <summary>
    /// While armed, a switch to another power plan (by the user or an OEM utility) would leave the new plan sleeping on
    /// lid close and the old plan on Do nothing for good: put the old plan back and arm the new one.
    /// </summary>
    private void FollowPlanSwitch()
    {
        if (controller.Mode != Mode.Armed || !journal.SchemeChanged) return;
        log.Info("Power plan changed while armed; moving to the new plan");
        try
        {
            journal.Arm();
        }
        catch (Exception ex)
        {
            log.Warn($"Moving to the new power plan failed, record kept: {ex.Message}");
        }
    }

    /// <summary>One log line whenever the set of holders changes, so the log says why the laptop stays awake.</summary>
    private void LogHoldersWhenTheyChange(IReadOnlyList<Holder> holders)
    {
        var labels = string.Join("|", holders.Select(holder => holder.Label));
        if (labels == _loggedHolders) return;
        _loggedHolders = labels;
        log.Info(holders.Count == 0 ? "Quiet" : "Live: " + string.Join(", ", holders.Select(holder => $"{holder.Label} · {holder.Detail}")));
    }

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
                    // Apps first, so nothing stays slowed down once the laptop goes to sleep.
                    efficiency?.RestoreAll(controller.Spent ? "battery floor or lid-closed limit" : "work ended");
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
