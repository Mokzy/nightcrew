# Nightcrew v0.3.0: safety patch and Claude Code status

Date: 16 October 2026. Status: draft for the owner's review; builder check done.

Sources:
- The overnight research of 16 October 2026, stored outside this repo in the owner's notes:
  - the code audit (findings C1, C2, I1, I2, I3, I5, I7, I8, I9, N7, N10, N13, N16)
  - the adversarial critique that re-scoped it
- The owner's decisions in chat on 16 October 2026.

## 1. Intent

v0.3.0 is the release that Nightcrew launches on. It has two jobs.

1. **Keep the promise "your settings always come back".** The audit found ways
   that promise breaks today. Each one is fixed here.
2. **Stop runs from ending in the middle of a quiet command.** Claude Code
   sometimes runs a long command that uses almost no CPU, for example
   `docker build`, a download, `ssh`, or a wait for a remote job. Its
   transcript stops growing. Five minutes later Nightcrew thinks the work has
   ended, and the laptop sleeps. Claude Code writes its own status
   (busy, idle, waiting) to a small file per session. Nightcrew will read it.

**Why now:** Nightcrew will be launched publicly, and v0.3.0 is the first
release strangers will run. A failure that a launch thread notices ("it left
my plan on Do nothing") cannot be taken back.

## 2. Goals

- Every fix in section 4 works and has a test.
- Claude Code runs no longer stop during quiet commands.
- With the new setting on (the default) and the lid closed, the laptop stays
  awake while Claude Code waits for an approval or an answer.
- The exe says which version it is.
- No new words on the main panel. Settings gets one new switch.

## 3. Non-goals and anti-goals

**Not in v0.3** (owner, 16 October 2026):

- The "stuck tool" rule. A tool that stays busy for hours keeps counting.
- Speed improvements to Nightcrew itself. These are for later.
- Code signing. This comes after the launch.

**Also not in v0.3** (audit items not chosen):

- detecting an external display
- S3 laptops and laptops with hibernate off (I4)
- the policy-locked "degraded arm" (I6)
- Claude desktop app processes counted as agents (I7)
- WSL
- notifications

**Anti-goals.** v0.3 fails, even if it works, if it does any of these:

- Opens any file in `~\.claude` other than `<digits>.json` in the sessions
  folder. The `.key` files hold login tokens.
- Reads or stores transcript content.
- Writes a session name, a folder path or a session ID to the log, the panel
  or any file.
- Adds network code.
- Requires hooks or a plugin.
- Changes the main panel's words.

## 4. What changes

Each subsection says who decided it.

### 4.1 Claude Code status files (owner: option A)

**Where.** Claude Code keeps one small file per running session at
`%USERPROFILE%\.claude\sessions\<pid>.json`. The files from the Claude Code
CLI and from the Claude desktop app's Code sessions are both there.

Fields Nightcrew uses:
- `pid` (number)
- `procStart`: a string holding the process creation time as a Windows
  FILETIME, the same value that `GetProcessTimes` returns. This was checked on
  the owner's laptop on 16 October.
- `status`: `busy`, `idle` or `waiting`

- `cwd`: only to compare against `IgnorePathContains`, the same filter the
  transcript signal uses. It is never kept, logged or shown. This matters
  because claude-mem's background "observer" sessions write status files
  too. On 16 October one of them was `busy` on the owner's laptop. Their
  `cwd` contains `claude-mem`, and the default `IgnorePathContains`
  (`claude-mem-observer`) must exclude them. Check the exact match on a real
  file during the build. (My call.)

The other fields are not read into anything that is kept. Those are
`name`, `sessionId`, `entrypoint`, `version` and timestamps.
Headless sessions (`sdk-cli`) count like any other session, because the
owner's own overnight jobs run that way.

**The format is undocumented.** Anthropic documents the folder but not the
fields. The Claude Code code shows how `status` is set:
- `busy`: a turn is running. This includes tool calls and subagents.
- `waiting`: the turn needs the user (an approval, a question, a dialog).
- `idle`: neither. This includes a usage-limit wait, because that wait is not
  a running turn.

The usage-limit case comes from reading the Claude Code code and is not
tested. The hardware check in section 6 covers it.

**Rules (my calls, except where marked):**

- Open only files whose names match `^\d+\.json$`. Skip any file bigger than
  64 KB. Look at no more than 64 files per check.
- A file counts only if a process with that `pid` exists and its creation
  time equals `procStart`. A file left by a dead session never counts.
- `busy` counts as work.
- `waiting` counts as work while the new setting is on (owner). This reverses
  the 13 October rule that an approval wait is not work.
- `idle`, or any other value, or a file that can't be read or parsed, does not
  count. An unreadable file is skipped silently. Log one warning per run at
  most, and never the file's content.
- **The signal sits next to the existing signals.** It adds to them and
  replaces none. The transcript, tool CPU and GPU signals stay as they are.
  If the folder is missing or the format changes, Nightcrew behaves as it
  does today.
- **Panel holder:** "Claude Code" · "N sessions", the same words the transcript
  signal uses. When both report, the first one wins, as today. There are no
  new words on the main panel (my call).
- **Folder:** the default is `%USERPROFILE%\.claude\sessions`. Add it to
  settings.json as `ClaudeSessionsDir`, so that a user with a moved config
  folder can change it. `CLAUDE_CONFIG_DIR` support stays out of scope.
- **How often:** once per tick, like the other signals. Whether to use a
  folder watcher or to re-read each tick is the builder's choice.

**Limits.** This works for Claude Code only. Codex writes no status file, and
it does not save approval requests.

### 4.2 New setting: Stay awake for approvals (owner)

- **Label:** "Stay awake for approvals".
- **Default:** on.
- **Where:** on the settings page, directly after "Lock when lid closes".
- **settings.json key:** `StayAwakeForApprovals`, boolean, default `true`.
- **When it's off:** `waiting` does not count, as before v0.3.
- It works only for Claude Code. The README says so. The settings page has no
  explanation text, as the owner prefers.
- **Only with the lid closed** (owner, 16 October). `waiting` counts as work
  only while the lid is closed. With the lid open, or the lid state unknown
  (a desktop), a wait does not count. Windows' own sleep timer then applies,
  as before v0.3. If the lid opens during a wait, the wait stops counting, and
  the usual 5-minute grace period runs.
- **A prompt nobody answers** with the lid closed keeps the laptop awake until
  the existing limits stop it: 20% battery, or 8 untouched hours with the lid
  closed. The owner accepted this on 16 October.

### 4.3 A frozen check no longer leaves the laptop awake (audit I5; my design)

**Today.** If one tick hangs, every later tick is skipped. The power settings
stay on Do nothing and Never. The battery floor and the lid-closed limit are
never checked again. The watchdog task sees a live process and does nothing.

**Fix.**

- Record the time each tick finishes.
- A separate timer checks once a minute. It must not need the tick lock. If no
  tick has finished for 3 minutes, Nightcrew does the following:
  1. Logs it.
  2. Takes all apps out of Efficiency mode.
  3. Releases the keep-awake request.
  4. Puts the power settings back from the record.
  5. Starts a new copy of nightcrew.exe.
  6. Exits.
- **No restart loop:** it restarts only if this process has been running for
  at least 10 minutes. Otherwise it exits without restarting. The watchdog
  task and the next sign-in then take over.
- **Crashes:** add handlers for unhandled exceptions on any thread
  (`AppDomain.UnhandledException` and `Application.ThreadException`). They do
  steps 1 to 6, with the same 10-minute rule.
- **Overlap:** the recovery may run while the frozen tick is still inside a
  power write. The process exits right after, so the builder only has to make
  sure the recovery itself can't hang.

### 4.4 Damaged files no longer stop Nightcrew (audit I1; my design)

- **restore.json.** Any exception while reading it (not only
  `SerializationException`; a zero-filled file throws `XmlException`) means
  the record is corrupt. It is moved aside to `.corrupt`, as today. Reading
  the owner's values never throws.
- **settings.json.**
  - Any exception while loading sets the file aside as `.corrupt` and uses
    the defaults.
  - After loading, every `null` list or dictionary becomes its default.
  - Numbers are clamped:

| Key | Range |
|---|---|
| `GraceMinutes` | 1–60 |
| `AwayMinutes` | 1–60 |
| `SampleSeconds` | 1–60 |
| `GpuPercent` | 5–100 |
| `ToolCpuPercent` | 1–100 |
| `SelfCpuPercent` | 0.5–100 |
| `BatteryFloorPercent` | 0–90 |
| `LidClosedCapHours` | 0–48 |

  - An unknown `Mark` becomes the lantern.

### 4.5 A lost record can't make "Do nothing" your setting (audit I2; my design)

**Today.** If restore.json disappears while Nightcrew's values are still in
place, the next arm saves those values as the owner's own. Every later restore
then "restores" Do nothing and Never.

**Fix.** Every normal arm starts with no record, because each restore deletes
it. So the check must fire only when there are signs that a record was lost.
It is never a plain "the values look like ours" test, because a docked user
can really have lid Do nothing and Never.

Nightcrew treats the current values as its own leftovers only when one of
these holds:
- **A corrupt record was just set aside.** Arm's own load found the record
  unreadable and moved it to `.corrupt`.
- **The values match what Nightcrew writes exactly.** That means lid Do
  nothing on AC and DC, AC sleep-after Never, and DC sleep-after equal to
  the distinctive "limit plus 1 hour" value from 4.6. No owner sets that
  value by hand. When the limit is off, this check can't tell the values
  apart, and only the corrupt-record sign applies.

In either case, Nightcrew saves Windows' default values for that plan as the
owner's instead, and logs a warning.
- Read the defaults with `PowerReadACDefaultIndex` and
  `PowerReadDCDefaultIndex`.
- If that fails, use lid action Sleep, sleep after 30 minutes on AC and 15
  minutes on DC.

The existing test `Arm_over_a_corrupt_record_captures_fresh_owner_values`
changes to match. (My call, after the builder check.)

### 4.6 Crash plus restart no longer leaves "never sleep" (audit I9; my design)

**Today.** While armed, sleep-after on DC is Never. Suppose Nightcrew crashes,
and Windows restarts within the watchdog's 5 minutes. The logon screen then
keeps lid Do nothing and DC sleep-after Never until someone signs in.

**Fix.**
- While armed, write DC sleep-after as the lid-closed limit plus 1 hour. With
  the default limit of 8 hours, that is 9 hours.
- If the limit is off (0), keep Never.
- AC sleep-after stays Never.

**Side effect.** With the lid open, on battery and untouched for 9 hours, the
laptop may now sleep during work. The 20% floor normally comes first. On
Modern Standby, Windows ends power requests 5 minutes after the sleep timeout,
so hardware step 4 has to be run again to prove nothing changes under 9
hours.

### 4.7 Power plan changes are handled (audit C1; my design)

**Today.** The record doesn't say which power plan it belongs to. Every read,
write and restore uses whichever plan is active at that moment.

**Fix.**

- The record stores the plan's GUID. This is a new optional field. An old
  record without it keeps today's behaviour.
- A restore writes the owner's values back to the recorded plan, even if
  another plan is active now. `PowerWrite*ValueIndex` works on an inactive
  plan. Call `PowerSetActiveScheme` only when the recorded plan is the active
  one.
- On every armed tick, if the active plan is not the recorded plan:
  1. Put the old plan's values back.
  2. Save the new plan's own values.
  3. Arm the new plan.
  4. Log it.
- The watchdog and the startup restore use the recorded plan too.

### 4.8 Apps started by a slowed app come back (audit C2; my design)

**Today.** Windows gives a child the IDLE priority of an IDLE parent.
Nightcrew treats any IDLE process as "its own choice" and never records it, so
a helper, or an updater's relaunch, started while away stays slow forever.

**Fix.**

- Each Efficiency mode entry records when it was applied. This is a new
  optional field.
- **Record inherited processes while away.** On every away tick, a member of
  a slowed family is recorded as "inherited" if:
  - it is at IDLE priority,
  - it started after that family was applied, and
  - its parent chain reaches a recorded process.

  The record stores the recorded ancestor's original priority. So the
  watchdog and the startup restore can put it back later, even after the
  parent has exited.
- **Sweep when putting apps back.** When putting apps back (and also in the
  watchdog and at startup), Nightcrew sweeps every process at IDLE priority
  once more. It restores a process if:
  - it started after an entry was applied, and
  - its parent chain reaches a recorded or inherited process.
- The parent check uses the pid plus the start time while the parent is
  alive. If the parent has exited, a pid match is accepted when the child
  started after the parent's recorded start.
- Chains count: if the updater was recorded or restored this way, the app it
  relaunched is restored too.
- **Known gap, accepted:** a middle process that starts and exits between two
  ticks (under 10 seconds) is never seen, so the chain through it breaks.
  The test uses a grandchild whose middle process lives longer than one tick.
  (My call, after the builder check.)
- **What "restored" means:** Nightcrew sets the priority back to the recorded
  ancestor's original priority, and only if the priority is still IDLE.
- **Throttling:** the test checks whether power throttling is inherited too.
  If it is, clear it as well.

### 4.9 Docked use no longer locks the PC (audit I3, partly; my design)

- **Locking.** Don't lock on lid close when the owner's lid action for the
  current power source is Do nothing. In that case Windows itself wouldn't
  lock or sleep, because the user is using the laptop closed with a screen.
- **The lid-closed limit counts per episode.** The time resets each time
  Nightcrew arms. A lid that stays closed for days no longer adds up across
  nights. "Spent" still clears only when the lid opens, as today.
- External display detection stays out of scope.

### 4.10 First start, second start, quitting (audit N10, N13, N7; my design)

- **First start.** If settings.json didn't exist before this start, open the
  panel once, next to the tray icon. Windows 11 puts a new tray icon in the
  overflow, so otherwise nothing visible happens. Placing the panel is the
  builder's choice: `Shell_NotifyIconGetRect`, or a fallback to the corner
  above the taskbar.
- **Second start.** Starting nightcrew.exe while it already runs makes the
  running copy open its panel, through a named event. Today the second start
  does nothing.
- **`--exit`.** A new command-line option tells the running copy to quit
  normally, with the full restore. It waits up to 10 seconds for that copy to
  exit. It does nothing if Nightcrew isn't running. The README explains how to
  use it to update.
- **Quit race.** Set `_closed` before disposing the timers. Catch
  `ObjectDisposedException` around `Timer.Change`.

### 4.11 Version and a small speed fix (audit N16, I8; my design)

- **Version.**
  - The project's version is `0.3.0`.
  - The release workflow builds with the tag's version (`v0.3.0` gives
    `0.3.0`).
  - The startup log line says "Nightcrew 0.3.0 started".
  - File properties show the version.
- **GPU holder names.** Take them from the tick's existing process list,
  instead of `Process.GetProcessById`.

## 5. Constraints

The rules in CONTRIBUTING.md:
- `net48` with C# 14
- one exe
- no runtime NuGet packages
- JSON through `Nightcrew.Core.Json`
- warnings are errors
- tests never change the machine

Also:
- The words follow the house style: sentence case, short, no explanations
  under settings.
- Core stays free of P/Invoke.

## 6. Testing

- **Unit tests.** Every item in section 4 has a unit test, written first.
  Item 4.8 runs against real child processes, as EfficiencyModeTests do. The
  rest use fakes.
- **New hardware-checklist steps.** The owner runs these:
  1. Switch the power plan while armed. Check that both plans end up with the
     owner's values.
  2. Use the laptop docked, with the lid action set to Do nothing. Close the
     lid while an agent works. Check that there is no lock.
  3. In Claude Code, run a quiet command (a sleep of 10 minutes) with the lid
     closed. Check that the laptop stays awake until it ends.
  4. Leave a permission prompt open with the lid closed and the setting on.
     Check that the laptop stays awake. Then check two more cases, where the
     laptop must sleep after the 5-minute grace period and Windows' own
     timer, as before v0.3:
     - the lid open with the setting on
     - the lid closed with the setting off
  5. Optional: reach a usage limit. Check that the status file shows `idle`.
  6. Run `--exit` and a second start.
  7. Run step 4 again (90 minutes on battery), because of item 4.6.
- **Dry run.** The session-file signal runs in a dry run too. The log shows
  when it alone kept things awake.

## 7. Release

1. All work goes on branch `fix/v0.3.0`, with a pull request.
2. The owner tests a build on the laptop and runs the hardware steps.
3. The owner decides when to merge and tag `v0.3.0`.
4. The README changes in the same pull request:
   - what Nightcrew reads in `~\.claude\sessions`
   - the new setting and settings.json keys
   - the `--exit` update steps
   - the version

## 8. Calls I made

The owner should check these:

1. The panel shows sessions found through the status files with the same
   words as today ("Claude Code · N sessions").
2. A frozen check gets 3 minutes before recovery. A restart only happens
   after at least 10 minutes of uptime.
3. The settings ranges in 4.4.
4. A lost record falls back to Windows' default values (4.5).
5. DC sleep-after while armed is the lid-closed limit plus 1 hour (4.6).
6. On a plan switch, Nightcrew moves to the new plan rather than stopping
   (4.7).
7. Docked means "your lid action is Do nothing". No display detection.
8. `--exit` waits up to 10 seconds.
9. claude-mem background sessions are excluded by matching `cwd` against
   `IgnorePathContains`. `cwd` is read only for that comparison. Headless
   sessions count (4.1).
10. A lost record is detected only from a corrupt file just set aside, or
    from an exact match with Nightcrew's distinctive values (4.5).
11. Inherited slow processes are recorded on every away tick. Middle
    processes that live under 10 seconds can't be followed (4.8).
