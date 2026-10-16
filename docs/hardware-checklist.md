# Hardware checklist

Manual tests on a real laptop, run in order before a release. Each step says
what to do, what to look at and what counts as a pass. Stop at the first
failure and fix it before going on.

## Before you start

- Put `nightcrew.exe` in the folder where it will stay.
- Set your lid action to **Sleep**, both plugged in and on battery. Step 2
  can't tell Nightcrew's change apart from Do nothing.
- Run `powercfg /a` and check that Hibernate is listed. Steps 6 and 7 need it.
- Only one Nightcrew runs at a time. Unless a step says otherwise, quit any
  running copy from the tray before you start the step.

## Tools used below

**Settings check.** Run in PowerShell:

```powershell
powercfg /qh SCHEME_CURRENT SUB_BUTTONS LIDACTION | Select-String Current
powercfg /qh SCHEME_CURRENT SUB_SLEEP STANDBYIDLE | Select-String Current
```

LIDACTION is 0 for Do nothing, 1 for Sleep, 2 for Hibernate and 3 for Shut
down. STANDBYIDLE is "Sleep after" in seconds, in hex; 0 means Never. Run it
once now and write down the four values. Below they are called **your
values**.

**Log.** `%LOCALAPPDATA%\Nightcrew\nightcrew.log`, or tray > Open log.

**Power events.** Run in PowerShell:

```powershell
Get-WinEvent -FilterHashtable @{ LogName = 'System'; ProviderName = 'Microsoft-Windows-Kernel-Power'; Id = 42, 506, 507; StartTime = (Get-Date).AddHours(-3) } | Format-Table TimeCreated, Id, Message -Wrap
```

- 506 means the laptop entered Modern Standby (sleep).
- 507 means it left Modern Standby.
- 42 means it entered sleep or hibernate. In Event Viewer (Windows Logs >
  System), the Details tab of a 42 shows `TargetState`: 5 is hibernate, 4 is
  S3 sleep on laptops without Modern Standby.

**Heartbeat.** In a separate PowerShell window, delete
`$env:TEMP\nightcrew-heartbeat.txt` if it exists, then run:

```powershell
while ($true) { Get-Date -Format o | Add-Content $env:TEMP\nightcrew-heartbeat.txt; Start-Sleep -Seconds 30 }
```

Afterwards, list the gaps longer than 90 seconds. A gap means the laptop was
asleep.

```powershell
$t = @(Get-Content $env:TEMP\nightcrew-heartbeat.txt | ForEach-Object { [datetime]$_ })
for ($i = 1; $i -lt $t.Count; $i++) { if (($t[$i] - $t[$i - 1]).TotalSeconds -gt 90) { "{0:t} -> {1:t}" -f $t[$i - 1], $t[$i] } }
```

**Long agent task.** Ask Claude Code:

> Run this command N times, one after another, each with a 10-minute
> timeout, then reply "done":
> `powershell -NoProfile -Command '$e = (Get-Date).AddMinutes(9); while ((Get-Date) -lt $e) {}'`

Each run keeps one CPU core busy for 9 minutes. Use N = 4 for about 36
minutes and N = 10 for about 90 minutes. A real task that you expect to take
as long also works.

## 1. Dry run

**Do.** Run `nightcrew.exe --dry-run`. Use your agents normally for an hour,
with some quiet stretches in between, such as reading, browsing or a video.

**Look at.** The log. Run the settings check a few times while an agent is
working.

**Pass.**

- An arm line appears within about 30 seconds of an agent starting real work.
- A disarm line appears about 5 minutes after the agent goes quiet.
- There's no arm while no agent is working. If there is one, note the reason
  the log gives.
- The settings check shows your values the whole time, because a dry run
  writes nothing.

## 2. Settings write

**Do.** Run `nightcrew.exe` without `--dry-run`, with `DryRun` false in
`settings.json`. Keep the lid open and the laptop plugged in. Start an agent
task that runs for a few minutes. When it finishes, wait 6 minutes.

**Look at.** The settings check, while the agent works and again after the
6 minutes. Also check whether `%LOCALAPPDATA%\Nightcrew\restore.json` exists.
Optionally, in an admin terminal, run `powercfg /requests` while the agent
works.

**Pass.**

- While the agent works, LIDACTION is `0x00000000` on AC and DC, STANDBYIDLE
  is `0x00000000` on AC and `0x00007e97` (9 hours and 7 seconds: the lid-closed limit plus
  an hour) on DC, and `restore.json` exists.
- `powercfg /requests` lists `nightcrew.exe` under SYSTEM and EXECUTION.
- After the 6 minutes, the settings check shows your values again, and
  `restore.json` is gone.
- The laptop didn't sleep.

## 3. Lid closed on AC, 30 minutes

**Do.** Plug the laptop in. In the tray, turn **Lock when lid closes** off for
this step. Start the heartbeat and a long agent task with N = 4. When the log
shows the arm line, close the lid and leave it closed. Come back after about
50 minutes.

**Look at.** The heartbeat gaps, the agent's reply, the log and the power
events.

**Pass.**

- The agent finished every run and replied "done".
- There's no heartbeat gap between closing the lid and the agent finishing.
- About 5 minutes after the agent finished, the log shows the disarm line.
- After that, the laptop followed your lid action. With Sleep, there's a 506,
  or a 42 with TargetState 5, within about 10 minutes of the agent finishing,
  and the heartbeat stops there.
- When you open the lid, the settings check shows your values.

## 4. Lid closed on battery, 90 minutes

This proves the battery "Sleep after" override. Without it, Windows would
sleep after your DC STANDBYIDLE value. Run it again for v0.3.0: the override
on battery is now 9 hours instead of Never, and on Modern Standby Windows ends
keep-awake requests 5 minutes after that timer, so this checks nothing ends
early.

**Do.** Charge to at least 80% so that the 20% battery floor doesn't trigger,
then unplug. Leave **Lock when lid closes** off. Start the heartbeat and a
long agent task with N = 10. When the log shows the arm line, close the lid.
Come back after about 110 minutes.

**Look at.** The same as step 3.

**Pass.** The same as step 3, over the full 90 minutes on battery.

## 5. Locked

**Do.** Plug the laptop in. In the tray, turn **Lock when lid closes** on.
Repeat step 3. About 5 minutes after closing the lid, open it briefly, check
the screen, then close it again.

**Look at.** The same as step 3, and the screen when you open the lid
briefly.

**Pass.**

- The same as step 3.
- When you open the lid briefly, the lock screen is showing.
- There's no heartbeat gap while the session is locked.

## 6. Finish chain

**Do.**

1. Set your lid action to **Sleep**. Plug the laptop in. Start the heartbeat
   and a long agent task with N = 1. When the log shows the arm line, close
   the lid. Don't touch the laptop for 30 minutes.
2. Set your lid action to **Hibernate** and repeat.
3. Set your lid action back to **Sleep**.

**Look at.** The time of the disarm line in the log, the power events and
the heartbeat.

**Pass.**

- With Sleep, there's a 506 within about 5 minutes of the disarm line. If
  Windows doesn't sleep by itself, Nightcrew's hibernate fallback runs
  instead: a 42 with TargetState 5 about 5 minutes after the disarm line.
  Either passes; note which one happened.
- With Hibernate, there's a 42 with TargetState 5 within about a minute of
  the disarm line.
- After waking, the settings check shows your values.
- With Sleep, if Windows went into standby (the 506), leave it closed for
  another 30 minutes, then open the lid and use the laptop. No 42 with
  TargetState 5 follows within a minute of opening it: Nightcrew must not
  hibernate a laptop you've just come back to.

## 7. Battery floor and lid-closed limit

**Do.**

1. **Floor.** Charge as full as the laptop allows, then unplug. Quit
   Nightcrew. In `%LOCALAPPDATA%\Nightcrew\settings.json`, set
   `BatteryFloorPercent` to 5 below the current charge, for example
   `"BatteryFloorPercent": 95` at 100%. Start Nightcrew. Start the heartbeat
   and a long agent task with N = 4. When the log shows the arm line, close
   the lid.
2. **Limit.** Quit Nightcrew. Set `"BatteryFloorPercent": 20` and
   `"LidClosedCapHours": 1`. Start Nightcrew. Plug the laptop in. Start the
   heartbeat and a long agent task with N = 8. When the log shows the arm
   line, close the lid.
3. Quit Nightcrew, set `"LidClosedCapHours": 8` and start it again.

**Look at.** The log, the power events and the heartbeat.

**Pass.**

- **Floor.** When the battery reaches the floor, the log shows the floor stop
  and a 42 with TargetState 5 follows within a minute, even though the agent
  is still working.
- **Limit.** About 65 minutes after you closed the lid, the log shows the
  limit stop and a 42 with TargetState 5 follows within a minute. The limit
  counts only time without keyboard or mouse input, which starts 5 minutes
  after you last touched the laptop.
- In both cases the heartbeat gap starts at the 42, and after waking the
  settings check shows your values.

## 8. Watchdog

**Do.** Start Nightcrew normally. Check that the watchdog task exists with
`schtasks /Query /TN "Nightcrew Watchdog"`. Keep the lid open and the laptop
plugged in. Start an agent task and wait until the settings check shows
LIDACTION `0x00000000`. In Task Manager > Details, end `nightcrew.exe` and note the
time. Afterwards, start Nightcrew again.

**Look at.** The settings check once a minute, `restore.json`, and
`schtasks /Query /TN "Nightcrew Watchdog" /V /FO LIST` for Last Run Time and
Last Result.

**Pass.**

- Within 5 minutes of ending the process, the settings check shows your
  values.
- `restore.json` is gone.
- The task's Last Result is 0.

## Efficiency mode tools

**Priority log.** Open Notepad and leave it open. In a terminal (terminals are
left alone, so this keeps running at full speed), delete
`$env:TEMP\nightcrew-priority.txt` if it exists, then run:

```powershell
while ($true) { "{0:T} {1}" -f (Get-Date), (Get-Process notepad | Select-Object -First 1).PriorityClass | Add-Content $env:TEMP\nightcrew-priority.txt; Start-Sleep -Seconds 30 }
```

`Idle` means Notepad is in Efficiency mode; `Normal` means it's back.

## 9. Efficiency mode on and off

**Do.** Start Nightcrew normally and start the priority log. Plug the laptop
in, start a long agent task with N = 2 and close the lid. After 15 minutes,
open the lid and open the panel.

**Look at.** The priority log, the log and the settings page.

**Pass.**

- About 5 minutes after you closed the lid, the priority log turns `Idle` and
  the log shows `Efficiency mode on:` with `Notepad.exe (notepad, 1 process)`
  among the apps.
- The log shows `Efficiency mode off for` some apps with `lid opened` at the
  moment you opened the lid, and the next priority log line says `Normal`.
- The main panel shows no list of apps. In Settings, **+ Add an app** under
  **Keep at full speed** lists `Notepad.exe` among the apps Nightcrew would
  slow; clicking it moves it onto the list, and its × takes it off again.
- `%LOCALAPPDATA%\Nightcrew\efficiency.json` is gone.

## 10. Efficiency mode after a crash

**Do.** Start Nightcrew normally and start the priority log. In a second
terminal, run `Start-Sleep -Seconds 600; Stop-Process -Name nightcrew`, which
ends Nightcrew in 10 minutes. Plug the laptop in, start a long agent task with
N = 4 and close the lid. After 20 minutes, open the lid. Start Nightcrew again
afterwards.

**Look at.** The priority log, the log and `efficiency.json`.

**Pass.**

- The priority log turns `Idle` about 5 minutes after you closed the lid, and
  back to `Normal` within 5 minutes of Nightcrew ending, before you open the
  lid.
- The log shows `Efficiency mode off for 1 app: watchdog` (or more apps, if
  more were open).
- `efficiency.json` is gone.

## 11. Uninstall

**Do.** Start Nightcrew normally. Start an agent task and wait until the
settings check shows LIDACTION `0x00000000`. In Task Manager, end `nightcrew.exe`.
Right away, before the watchdog's next run, run `.\nightcrew.exe --uninstall`
from the exe's folder.

**Look at.**

```powershell
reg query HKCU\Software\Microsoft\Windows\CurrentVersion\Run /v Nightcrew
schtasks /Query /TN "Nightcrew Watchdog"
```

Also look at the settings check and `restore.json`.

**Pass.**

- Both queries report that the value or task can't be found.
- The settings check shows your values.
- `restore.json` and `efficiency.json` are gone.

## 12. Power plan switch while armed

**Do.** Run `powercfg /list` and note your active plan and one other. Start
Nightcrew normally. Plug the laptop in, keep the lid open and start a long
agent task with N = 1. When the log shows the arm line, switch to the other
plan with `powercfg /setactive <its GUID>`. Wait a minute, then switch back.
When the task ends, wait 6 minutes.

**Look at.** The log, and the settings check for both plans: replace
`SCHEME_CURRENT` with each plan's GUID.

**Pass.**

- After each switch, within about 10 seconds, the log shows `Power plan
  changed while armed; moving to the new plan`.
- While armed, the active plan shows LIDACTION `0x00000000`.
- After the 6 minutes, both plans show your values again, and `restore.json`
  is gone.

## 13. Docked, lid action Do nothing

**Do.** Set your lid action to **Do nothing** while plugged in, and write the
four values down again. Connect an external screen, keyboard and mouse. Start
Nightcrew normally. Start a long agent task with N = 1, close the lid and keep
working on the external screen. When the task ends, wait 6 minutes. Then set
your lid action back to **Sleep**.

**Look at.** The screen, the log and the settings check.

**Pass.**

- The PC doesn't lock when you close the lid. The log has no `Lock` line.
- After the 6 minutes, the settings check shows your Do nothing values.

## 14. A quiet command

**Do.** Start Nightcrew normally and the heartbeat. Plug the laptop in. Ask
Claude Code to run `powershell -NoProfile -Command 'Start-Sleep -Seconds 900'`
with a 20-minute timeout and then reply "done". Close the lid as soon as the
command starts. Come back after 25 minutes.

**Look at.** The heartbeat, the log and Claude Code's reply.

**Pass.**

- There's no heartbeat gap until after Claude Code replied "done". The command
  uses no CPU and the transcript doesn't grow, so only Claude Code's own
  status (`claude-status` in the log) keeps the laptop awake.
- About 5 minutes after the reply, the log shows the disarm line.

## 15. Approvals

**Do.** Start Nightcrew normally and the heartbeat. Plug the laptop in. In
Settings, check that **Stay awake for approvals** is on. Ask Claude Code to do
something that needs your permission, such as writing a file outside the
project, so it waits for you. Then try three cases, each for 15 minutes:

1. Close the lid.
2. Open the lid and don't touch the laptop.
3. Turn **Stay awake for approvals** off and close the lid.

Answer the prompt after case 3 and turn the setting back on.

**Look at.** The heartbeat and the log.

**Pass.**

- Case 1: no heartbeat gap, and the log keeps the arm.
- Case 2: about 5 minutes after you opened the lid, the log shows the disarm
  line, and Windows' own sleep timer applies.
- Case 3: about 5 minutes after the setting went off, the log shows the disarm
  line, and the laptop then follows your lid action.

## 16. Usage limit (optional)

**Do.** Hit a Claude Code usage limit in a session. While it waits for the
reset, look at its file in `%USERPROFILE%\.claude\sessions`.

**Pass.** The `status` field is `idle`, so the wait doesn't count as work.

## 17. Second start and --exit

**Do.** Start Nightcrew normally. Run `nightcrew.exe` again from its folder.
Then run `.\nightcrew.exe --exit` in a terminal and note how long it takes.
Then rename `%LOCALAPPDATA%\Nightcrew\settings.json` to `settings.old.json`
and start Nightcrew.

**Look at.** The screen, the tray and `$LASTEXITCODE`.

**Pass.**

- The second start opens the panel within about a second.
- `--exit` returns within 10 seconds with exit code 0, and the lantern is
  gone from the tray.
- With no settings file, the panel opens once on start.

Afterwards, quit Nightcrew, delete the new settings.json, and rename
`settings.old.json` back.

When you're done, set your lid action back to what you normally use. To keep
using Nightcrew, run `nightcrew.exe` again.
