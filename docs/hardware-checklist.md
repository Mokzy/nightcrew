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

- While the agent works, LIDACTION and STANDBYIDLE are `0x00000000` on AC and
  DC, and `restore.json` exists.
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
sleep after your DC STANDBYIDLE value.

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
`0x00000000`. In Task Manager > Details, end `nightcrew.exe` and note the
time. Afterwards, start Nightcrew again.

**Look at.** The settings check once a minute, `restore.json`, and
`schtasks /Query /TN "Nightcrew Watchdog" /V /FO LIST` for Last Run Time and
Last Result.

**Pass.**

- Within 5 minutes of ending the process, the settings check shows your
  values.
- `restore.json` is gone.
- The task's Last Result is 0.

## 9. Uninstall

**Do.** Start Nightcrew normally. Start an agent task and wait until the
settings check shows `0x00000000`. In Task Manager, end `nightcrew.exe`.
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
- `restore.json` is gone.

When you're done, set your lid action back to what you normally use. To keep
using Nightcrew, run `nightcrew.exe` again.
