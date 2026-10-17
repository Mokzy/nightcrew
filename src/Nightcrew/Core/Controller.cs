namespace Nightcrew.Core;

/// <summary>Everything the controller needs to know for one step.</summary>
public sealed record Inputs(bool Live, LidState Lid, PowerSource Source, int? BatteryPercent, TimeSpan IdleTime, bool Paused, bool CanWrite, LidAction OwnerLid);

public enum Mode { Idle, Armed, Finishing, Paused }

public enum Command { Arm, Disarm, Lock, MonitorOff, Hibernate }

/// <summary>Pure state machine: turns inputs into commands. The engine carries the commands out.</summary>
public sealed class Controller(Settings settings)
{
    private enum Stage { WaitAway, MonitorOffSent }

    private Stage _stage;
    private TimeSpan _sentAt;
    private TimeSpan _lastNow;
    private LidState _lastLid;
    private Inputs? _last;

    public Mode Mode { get; private set; }

    /// <summary>Set by a floor or cap hibernate; cleared when the lid is seen Open.</summary>
    public bool Spent { get; private set; }

    /// <summary>Accumulated while Armed and unattended; reset at each arm (per episode) and when the lid is Open.</summary>
    public TimeSpan LidClosedFor { get; private set; }

    public string Status => Describe();

    public IReadOnlyList<Command> Step(Inputs inputs, TimeSpan now)
    {
        var commands = new List<Command>();
        var previousNow = _lastNow;
        var previousLid = _lastLid;
        _lastNow = now;
        _lastLid = inputs.Lid;
        _last = inputs;

        if (inputs.Paused)
        {
            if (Mode == Mode.Armed) commands.Add(Command.Disarm);
            Mode = Mode.Paused;
            return commands;
        }
        if (Mode == Mode.Paused) Mode = Mode.Idle;

        if (inputs.Lid == LidState.Open)
        {
            Spent = false;
            LidClosedFor = TimeSpan.Zero;
        }

        switch (Mode)
        {
            case Mode.Idle:
                StepIdle(inputs, commands);
                break;
            case Mode.Armed:
                StepArmed(inputs, now - previousNow, previousLid, commands);
                break;
            case Mode.Finishing:
                StepFinishing(inputs, now, commands);
                break;
        }
        return commands;
    }

    /// <summary>The machine woke up: whatever we were finishing is over.</summary>
    public void OnResume()
    {
        if (Mode == Mode.Finishing) Mode = Mode.Idle;
    }

    /// <summary>Back to Idle, keeping Spent and LidClosedFor, so the next step can arm again (a failed arm, or session end).</summary>
    public void Reset() => Mode = Mode.Idle;

    /// <summary>Lid closed and no keyboard or mouse input for AwayMinutes. A docked laptop in use is not unattended.</summary>
    private bool Unattended(Inputs inputs) =>
        inputs.Lid == LidState.Closed && inputs.IdleTime >= TimeSpan.FromMinutes(settings.AwayMinutes);

    /// <summary>
    /// Lock on lid close, unless the user's own lid action is Do nothing: then Windows wouldn't lock or sleep either,
    /// because they use the laptop closed, with a screen.
    /// </summary>
    private bool LocksOnLidClose(Inputs inputs) => settings.LockOnLidClose && inputs.OwnerLid != LidAction.DoNothing;

    /// <summary>Arms, starting a new episode: the lid-closed limit counts per episode, not across nights.</summary>
    private void AddArm(List<Command> commands)
    {
        commands.Add(Command.Arm);
        LidClosedFor = TimeSpan.Zero;
        Mode = Mode.Armed;
    }

    private void StepIdle(Inputs inputs, List<Command> commands)
    {
        if (!inputs.Live || Spent || !inputs.CanWrite) return;
        AddArm(commands);
        if (Unattended(inputs) && LocksOnLidClose(inputs)) commands.Add(Command.Lock);
    }

    private void StepArmed(Inputs inputs, TimeSpan sinceLastStep, LidState previousLid, List<Command> commands)
    {
        var closed = inputs.Lid == LidState.Closed;
        var unattended = Unattended(inputs);
        if (closed && previousLid != LidState.Closed && LocksOnLidClose(inputs)) commands.Add(Command.Lock);
        if (unattended) LidClosedFor += sinceLastStep;

        // The floor protects the battery whenever the lid is closed, in use or not; the cap counts only unattended time.
        var floorHit = closed && inputs.Source == PowerSource.DC && inputs.BatteryPercent is { } battery
            && settings.BatteryFloorPercent > 0 && battery <= settings.BatteryFloorPercent;
        var capHit = unattended && settings.LidClosedCapHours > 0 && LidClosedFor >= TimeSpan.FromHours(settings.LidClosedCapHours);

        if (floorHit || capHit)
        {
            commands.Add(Command.Disarm);
            commands.Add(Command.Hibernate);
            Spent = true;
            Mode = Mode.Idle;
        }
        else if (!inputs.Live)
        {
            commands.Add(Command.Disarm);
            if (closed)
            {
                Mode = Mode.Finishing;
                _stage = Stage.WaitAway;
            }
            else
            {
                Mode = Mode.Idle;
            }
        }
    }

    private void StepFinishing(Inputs inputs, TimeSpan now, List<Command> commands)
    {
        if (inputs.Lid != LidState.Closed)
        {
            Mode = Mode.Idle;
        }
        else if (inputs.Live && !Spent && inputs.CanWrite)
        {
            AddArm(commands);
        }
        else if (_stage == Stage.WaitAway)
        {
            if (inputs.IdleTime < TimeSpan.FromMinutes(settings.AwayMinutes)) return;
            switch (inputs.OwnerLid)
            {
                case LidAction.Sleep:
                    commands.Add(Command.MonitorOff);
                    _stage = Stage.MonitorOffSent;
                    _sentAt = now;
                    break;
                case LidAction.Hibernate:
                case LidAction.ShutDown:
                    commands.Add(Command.Hibernate);
                    Mode = Mode.Idle;
                    break;
                default:
                    Mode = Mode.Idle;
                    break;
            }
        }
        else if (inputs.IdleTime < TimeSpan.FromMinutes(1))
        {
            _stage = Stage.WaitAway;
        }
        else if (now - _sentAt >= TimeSpan.FromMinutes(5))
        {
            commands.Add(Command.Hibernate);
            Mode = Mode.Idle;
        }
    }

    private string Describe()
    {
        switch (Mode)
        {
            case Mode.Paused:
                return "Paused";
            case Mode.Armed when _last is { Lid: LidState.Closed } last && !Unattended(last):
                return $"Live \u2014 lid closed, when done: {WhenDone(last.OwnerLid)}";
            case Mode.Armed when _last is { Lid: LidState.Closed } last:
                return $"Live \u2014 lid closed {(int)LidClosedFor.TotalHours}h{LidClosedFor.Minutes:00}, when done: {WhenDone(last.OwnerLid)}";
            case Mode.Armed:
                return "Live";
            case Mode.Finishing:
                return _stage == Stage.WaitAway ? "Done \u2014 waiting until you're away" : "Done \u2014 screen off";
            default:
                if (Spent) return "Idle \u2014 limit reached, open the lid to reset";
                if (_last is { Live: true, CanWrite: false }) return "Idle \u2014 can't change power settings";
                return "Idle";
        }
    }

    private static string WhenDone(LidAction owner) => owner switch
    {
        LidAction.Sleep => "sleep",
        LidAction.Hibernate or LidAction.ShutDown => "hibernate",
        _ => "do nothing",
    };
}
