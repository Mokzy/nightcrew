# Nightcrew v2: freezing apps while you're away

Date: 15 October 2026. Status: draft for the owner's review.

This spec adds one feature to Nightcrew v0.1.1: while an agent works with the
lid closed and nobody is at the laptop, Nightcrew freezes the apps the agents
don't need, the way Windows would have paused them in sleep. It replaces
section 7 of `2026-10-13-nightcrew-design.md` for v2. Everything else in v1
(section 0 of that spec, plus the settings page and v0.1.1) stays as it is.

**In one line:** when Windows would have put the laptop to sleep, Nightcrew
puts everything except the agents to sleep instead.

## 1. Goals

The owner wants freezing for three things:

- **Longer battery** on long closed-lid runs.
- **Less heat** in a bag. There is no temperature limit; the owner dropped
  that idea on 15 October.
- **Agents get the machine:** Claude Code, Codex and their jobs get the CPU
  and GPU that idle apps would use.

Quiet (no sounds or notifications) is a side effect, not a goal.

**Success means:**

- With the lid closed and nobody at the laptop, apps with windows that the
  agents don't need are frozen within 10 seconds of the conditions in
  section 2 holding.
- Opening the lid, or touching the keyboard or mouse, thaws everything within
  about a second.
- No app ever stays frozen: not after a crash, a kill, a restart, a sleep or a
  hibernate.
- An agent never fails because something it needed was frozen; at worst it
  stalls for about 3 minutes (section 5).
- Nightcrew stays light: freezing adds well under 0.1% of one core while
  frozen, and nothing at all otherwise.

## 2. When it freezes and when it thaws

**Freezing is wanted when all of these are true** (`FreezeGate`, pure):

- the controller is **Armed** (an agent is working)
- the lid is **Closed**
- there has been no keyboard or mouse input for `AwayMinutes` (5)
- the owner's lid action for the current power source is **Sleep**,
  **Hibernate** or **Shut down**. With **Do nothing**, closing the lid
  normally keeps apps running, so Nightcrew doesn't freeze either. The lid
  action is the owner's saved value (`journal.OwnerValues`), never the
  Do nothing that Nightcrew writes while armed.
- **Freeze apps** is on (`FreezeApps`, default true)
- freezing is supported on this PC (section 6.1)
- Nightcrew isn't paused
- no installer is running under an agent (section 5.2)
- freezing isn't blocked until the lid opens (section 5.1)

**Everything thaws as soon as any of these happens.** Each one calls
`ThawAll` directly, before anything else and without waiting for the tick:

| Event | Where | Then |
|---|---|---|
| Lid opens | lid-change callback | the usual tick |
| Keyboard or mouse input | 1-second input check, running only while something is frozen | freezing waits for another 5 minutes without input |
| Work ends (Disarm) | engine, before restoring the settings | the usual lid action |
| Battery floor or lid-closed limit | engine, before Hibernate | hibernate |
| Windows suspends | `PBT_APMSUSPEND` callback | Windows sleeps or hibernates |
| Restart, shutdown or sign-out | `WM_QUERYENDSESSION`, before restoring the settings | Windows closes apps normally |
| Pause or Quit | panel actions | as today |
| Installer starts under an agent | tick | freezing stays off until it exits |
| Stall safety net | tick | section 5.1 |

**While frozen, every 10-second tick re-plans** (section 3):

- an app that opened since the last tick gets frozen
- a frozen app that has become needed gets thawed. Examples: a browser that
  now has a connector running, a process an agent has just connected to, a
  new GPU holder.

## 3. What gets frozen

### 3.1 Candidates

A **candidate** is a process in the current Windows session that owns a top-level
window you could Alt+Tab to:

- visible (minimized counts), with a non-empty title
- not owned by another window, unless it has `WS_EX_APPWINDOW`
- not a tool window (`WS_EX_TOOLWINDOW`), unless it has `WS_EX_APPWINDOW`
- not cloaked by the app itself (`DWMWA_CLOAKED` = `DWM_CLOAKED_APP`). A
  window cloaked by the shell because it sits on another virtual desktop still
  counts.

Nightcrew only reads windows; it never sends them messages. Apps that live
only in the tray, with no window open, aren't candidates and keep running.
Real sleep would pause them; this is the one place v2 falls short of "like
sleep", by choice, because a tray app can't be told apart from a background
helper an agent might need.

Store (UWP) apps own no window themselves; `ApplicationFrameHost` does, and
it's a system process, so those keep running. Packaged desktop apps such as
WhatsApp and the new Outlook are ordinary candidates.

### 3.2 Families

Nightcrew freezes whole apps, never part of one. A **family** is:

- **the root:** the candidate's topmost ancestor in the same session, stopping
  below any system or shell process (Explorer, `svchost`, `sihost` and the
  rest of the system list). Every parent link is checked with start times: a
  parent that started after its child is a reused pid, not the real parent,
  as `AgentTreeSignal` already does.
- **every descendant** of the root
- **orphans with the root's image path:** helpers whose parent has exited,
  such as browser tab processes. This never applies to shared runtimes
  (`msedgewebview2`, `node`, `bun`, `deno`, `python`, `pythonw`, `java`,
  `javaw`, `cmd`, `powershell`, `pwsh`).

**If any member of a family is kept, the whole family keeps running.**

### 3.3 What always keeps running

Each rule has a name, which the log uses when it explains a decision.

1. **Agent** (`agent`): the family of every agent process (`AgentProcessNames`:
   `claude`, `codex`) and every descendant of one. This keeps the Claude app,
   ChatGPT (Codex's host), and any terminal or editor running an agent.
2. **Keeping it awake** (`holder`): the family of every process listed under
   "Keeping it awake", which in practice means the GPU holders. `GpuSignal`
   starts reporting the pids of its busy processes, not just their names.
3. **Connector browser** (`connector`): for each running connector helper
   (`ConnectorHostNames`, default `chrome-native-host` for Claude and
   `extension-host` for Codex), walk up through `cmd` to the browser and keep
   that browser's family.
4. **Agent connection** (`connection`): the family of every process that an
   agent process, or one of its descendants, has an established loopback TCP
   connection to. These are found with `GetExtendedTcpTable` for IPv4 and
   IPv6, by matching the connection's remote port to the other process's
   local port. This covers dev servers, Ollama, LM Studio, a browser driven over
   DevTools, local databases, and tools that don't exist yet. Named pipes
   (Docker Desktop, SSH agents) aren't visible this way, so rule 5 covers them.
5. **Built-in categories** (`category`), matched by lower-case process name
   or prefix, in one list in `FreezeRules`:
   - terminals and editors: Windows Terminal, conhost, OpenConsole, VS Code,
     Cursor, Visual Studio, JetBrains IDEs, WezTerm, Alacritty
   - Docker Desktop, WSL and VMs: Docker Desktop, `com.docker.*`, `wsl`,
     `wslhost`, `vmmem*`, VirtualBox, VMware
   - VPN clients: NordVPN, OpenVPN, WireGuard, Tailscale, ProtonVPN, Mullvad
   - password managers and SSH agents: 1Password, Bitwarden, NordPass,
     KeePassXC, Dashlane, Proton Pass, `ssh-agent`, Pageant
   - local model apps: Ollama, LM Studio
   - remote desktop: `mstsc`, AnyDesk, TeamViewer, Parsec, RustDesk,
     Chrome Remote Desktop
6. **Never opened** (`never-open`): game launchers and everything under them,
   plus anti-cheat. That means Steam, Epic, Riot Client, EA app, Battle.net,
   GOG Galaxy, Ubisoft Connect, Easy Anti-Cheat, BattlEye, Vanguard and
   FACEIT. Freezing these can get an account banned, so Nightcrew doesn't even
   open the process.
7. **Windows** (`system`): system and shell processes. That covers csrss, dwm,
   winlogon, Explorer, sihost, the shell and start-menu hosts, SearchHost,
   TextInputHost, ctfmon, LockApp, ApplicationFrameHost, RuntimeBroker,
   svchost, taskhostw, dllhost, Settings, Task Manager, Process Explorer,
   System Informer, PowerToys, Widgets, Phone Link, screen readers, IME
   hosts, and Nightcrew itself.
8. **Your list** (`yours`): `NeverFreeze` in `settings.json`, by process name.
   The panel adds to it with one click (section 7).
9. **Not allowed** (`denied`): a process Windows won't let Nightcrew open with
   the rights in section 6.2, such as apps running as admin or protected
   processes. The log notes this once per image per run.

## 4. Planning

`FreezePlanner` is pure: a snapshot in, a plan out.

- **Snapshot:**
  - processes: pid, parent pid, name, start time, session
  - window owners: the candidate pids from 3.1
  - agent pids
  - holder pids
  - connector helper pids
  - loopback connections: owner pid, local and remote endpoints, state
  - the user's list
- **Plan:**
  - the families to freeze, each with its display name and pids
  - the families kept, each with the rule that kept it

The display name is the image's `FileDescription` ("Microsoft Outlook"),
falling back to the process name. It's read once per image and cached.

**One process list per tick.** `AgentTreeSignal` already lists every process
each tick, and that list is most of a tick's cost (20–37 ms). The freeze
planner reuses the same list through a per-tick cache in front of
`IProcessTable`, so freezing never lists processes twice. Window enumeration
(about 0.6 ms) and the TCP tables (about 0.4 ms) run only while freezing is
wanted.

## 5. Safety nets

### 5.1 Stall safety net

If an agent needs an app that is frozen, it waits on it and goes quiet.
`StallWatch` (pure) catches this:

1. **Trigger.** While anything is frozen, if a busy signal fired since the
   freeze began (or since the stall watch last fired) and then no signal
   fires for 2 minutes,
   Nightcrew calls `ThawAll` and watches for 1 minute. Hold awake doesn't
   count as a busy signal here.
2. **Work picks up within that minute:** a frozen app was almost certainly the
   problem. Freezing is blocked until the lid next opens, and the log says
   "Freezing off until the lid opens: work picked up right after thawing; one
   of these was probably needed: Outlook, WhatsApp".
3. **Work stays quiet:** it was a normal pause, such as an agent thinking for
   a while or work that has finished. Freezing carries on. The trigger needs a
   new busy signal before it can fire again, so a long pause doesn't cause
   repeated thaws.

Work counts as live for 5 minutes after the last busy signal (`GraceMinutes`),
so the safety net always acts before Nightcrew decides the work has ended.

### 5.2 Installers

If any process in an agent's tree is an installer (`msiexec`, `winget`,
`AppInstaller`, or a name containing `setup` or `install`), Nightcrew calls
`ThawAll` and keeps freezing off until no such process remains. Windows'
installer may close apps that hold files it needs, and a frozen app can't
answer.

## 6. How it freezes

### 6.1 The mechanism

Freezing uses Windows 11's process state-change objects
(`NtCreateProcessStateChange` and `NtChangeProcessState` in `ntdll`).

- **To freeze,** Nightcrew creates a state-change object for the process and
  asks it to suspend.
- **To thaw,** it closes that object.
- **If Nightcrew crashes or is killed,** Windows closes its handles and every
  process thaws by itself. Nothing can stay frozen, even without the watchdog.
- **Supported** means both entry points exist in `ntdll` and Windows is build
  22000 or later. If not (Windows 10, or an older Windows 11), freezing is
  off, the setting is hidden, and the log says so once.
- The API is undocumented. The freezer's own tests (section 10) prove each
  property on real processes before anything else is built on top, and they
  run in CI on `windows-2025` too.

There's no fallback: `NtSuspendProcess` would leave apps frozen if Nightcrew
died, so it is never used.

### 6.2 Access

Nightcrew asks only for `PROCESS_QUERY_LIMITED_INFORMATION |
PROCESS_SUSPEND_RESUME | PROCESS_SET_INFORMATION`. Handles are never
inheritable. Before freezing, it checks the process start time against the
snapshot, so a reused pid is never frozen by mistake.

### 6.3 Concurrency

`ProcessFreezer` (Platform) owns one lock, a generation counter, and the list
of state-change handles with their family.

- **`ThawAll`** can be called from any thread and calling it twice is harmless.
  Under the lock it increments the generation, closes every handle and clears
  the list. The lid, input, suspend and end-session callbacks call it
  directly, so a thaw never waits for a tick that's in progress.
- **`Freeze(plan)`** runs on the tick thread.
  - For each process, under the lock, it checks that the generation still
    matches the one it started with. On a mismatch it closes any handle it
    just made and abandons the pass.
  - It freezes a family back to back, root first.
  - It then re-checks that family up to 3 times for children that started
    during the freeze.
  - If any member can't be frozen, it thaws that family again and records the
    whole family as kept (`denied`).
- **`Thaw(family)`** thaws one family that has become needed.

### 6.4 Restart and shutdown

At startup Nightcrew calls `SetProcessShutdownParameters(0x3FF, 0)`, so it
hears about a restart or shutdown before other apps do. On
`WM_QUERYENDSESSION` it calls `ThawAll` before restoring the settings, and it
never vetoes the end of the session.

### 6.5 Dry run

`DryRunFreezer` logs the same plan with "would freeze" and freezes nothing.
The first run on the owner's laptop is a dry run.

## 7. What you see

**Settings page:**

- **Freeze apps:** a switch, on by default. On Windows 10 it doesn't appear.
- **Always keep running:** the apps on the user's list, each with a × to take
  it off. When the list is empty, the row doesn't appear.

**Panel, after the lid opens:**

- A short section, **"Frozen while you were away"**, lists the apps from the
  most recent freeze by display name. Each has a **Keep running** switch that
  adds the process name of the app's family root to `NeverFreeze`; turning
  the switch off again takes it back off.
- The section appears only while the lid is open and that freeze has ended.
  It clears when the next freeze starts.
- When freezing is blocked until the lid opens, the section's line reads "One
  of these was probably needed while you were away".
- As on the rest of the settings page, there are no explanations under the
  new settings.

**Log:**

- `Froze Outlook (olk, 4 processes), WhatsApp (whatsapp.root, 6 processes)`
- `Kept running: Brave (connector), Windows Terminal (category), Ollama (connection)`
- `Thawed 2 apps: lid opened`. The reason is one of: lid opened, input, work
  ended, battery floor, lid-closed limit, suspend, session end, pause, quit,
  installer, stall.
- `Can't freeze <image>: access denied`, once per image per run
- In dry run, `Would freeze …`

**README:** the "How it works" list gains one step after "You close the lid":
"Five minutes later, apps the agents don't need are frozen, the way sleep
would. They're back the moment you open the lid."

## 8. Settings

New keys in `settings.json`; every key you leave out keeps its default:

| Key | Default | Meaning |
|---|---|---|
| `FreezeApps` | true | Freeze apps the agents don't need while you're away |
| `NeverFreeze` | `[]` | Process names that are never frozen |
| `ConnectorHostNames` | `chrome-native-host`, `extension-host` | Browser connector helpers; their browser keeps running |

The stall timings (2 minutes quiet, 1 minute watch) and the built-in lists are
constants in code, not settings.

## 9. Architecture

**New units**, each small and tested on its own:

| Unit | Kind | Job |
|---|---|---|
| `Core/FreezeGate` | pure | Section 2's conditions in, freezing wanted or not out |
| `Core/FreezeRules` | data | The built-in lists from 3.3, and the installer names |
| `Core/FreezePlanner` | pure | Snapshot in, plan out (sections 3–4) |
| `Core/StallWatch` | pure | Section 5.1: busy times and freeze state in, "thaw" or "block" out |
| `Core/FreezeCoordinator` | logic | Each tick, builds the snapshot, asks the gate, planner and stall watch, and drives the freezer. It also keeps the episode the panel shows. |
| `Platform/ProcessFreezer` | Windows | Section 6: `IsSupported`, `Freeze`, `Thaw`, `ThawAll` |
| `Platform/DryRunFreezer` | Windows | Section 6.5 |
| `Platform/WindowOwners` | Windows | The candidate pids from 3.1 |
| `Platform/LoopbackConnections` | Windows | The TCP rows from 3.3 rule 4 |
| `Platform/TickProcessTable` | Windows | One process list per tick, shared by the agent signal and the planner |

**Changes to existing code:**

- `Engine.Tick` calls the coordinator after the controller step. In `Run`,
  Disarm and Hibernate call `ThawAll` first.
- `EngineSnapshot` adds the freeze state (off, unsupported, ready, frozen with
  a count, or blocked until the lid opens) and the last episode's apps.
- In `TrayApp`, the lid, suspend and end-session callbacks call `ThawAll`
  first; Pause and Quit do too. A 1-second input timer runs only while
  something is frozen.
- `Program` creates the freezer (or the dry-run one), calls
  `SetProcessShutdownParameters`, and wires in the per-tick process table.
- `GpuSignal` reports its busy pids. `ActivityMonitor` reports when a signal
  was last busy, for the stall watch.
- `Settings`, `SettingsModel`, `PanelModel` and `PanelVisual` gain the switch,
  the list and the "Frozen while you were away" section.

**Threading:**

- The tick and all planning run on the timer thread under `TrayApp._gate`, as
  today.
- `ThawAll` takes only the freezer's own lock, so it can run from any thread
  at any time.

## 10. Testing

**In this order:**

1. **The freezer core**, on processes the tests start themselves:
   - a child that writes to a file every 50 ms stops writing when frozen and
     carries on when thawed
   - `ThawAll` thaws several processes at once
   - a freeze started in a separate helper process is undone when that helper
     is killed (the auto-thaw property). The helper is a small test-only
     console project, `tests/Nightcrew.FreezeHolder`, that references
     `nightcrew.exe`. It isn't shipped.
   - a pid that's been reused (the start time doesn't match) is refused
   - a process it can't open is reported as denied
   - `IsSupported` is true on Windows 11 and on the CI runner
2. **The pure units:**
   - `FreezeGate`: one table row per condition in section 2
   - `FreezePlanner`, on made-up snapshots:
     - each rule from 3.3
     - candidate rules from 3.1
     - family integrity (one kept member keeps the whole family)
     - orphans grouped by image, but never for shared runtimes
     - reused pids rejected by start times
   - `StallWatch`:
     - quiet after work, then work picks up: blocked
     - quiet after work, then still quiet: carries on
     - no new busy signal: never fires twice
   - the installer rule
   - the strings in `PanelModel` and `SettingsModel`
3. **The engine with a fake freezer:**
   - `ThawAll` happens before Disarm restores, and before Hibernate
   - an installer thaws everything
   - the episode reaches the snapshot
4. **On the owner's laptop:**
   - a dry run first, reading the log's "Would freeze" and "Kept running"
     lines
   - then three new steps in `docs/hardware-checklist.md`:
     - **Freeze:** close the lid with apps open while an agent works. After 5
       minutes, Task Manager shows them as Suspended.
     - **Thaw:** open the lid; they respond within a second.
     - **Crash:** kill Nightcrew while apps are frozen; they thaw at once.
5. **Resources:** the same before-and-after measurement as v0.1.1, with
   freezing on, idle and frozen.

## 11. Build order and the classifier

On 13 October a safety classifier stopped the code that acts on other
programs, so the build order puts that code first, where a block is cheap:

1. **`ProcessFreezer` and its tests, on throwaway processes only.** If the
   classifier stops this step, the build stops. The owner gets the exact step
   and a decision: try another approach (Windows' Efficiency mode, which only
   slows apps down) or drop freezing.
2. The pure units (gate, rules, planner, stall watch).
3. The Windows readers (window owners, loopback connections, per-tick process
   table).
4. The coordinator and the engine wiring.
5. Settings, panel and log.
6. README and hardware checklist.

## 12. Release

v0.2.0 is tagged after the owner's dry run and the three new checklist steps
pass. The release notes say freezing is new, on by default, Windows 11 only,
and can be switched off in Settings.

## 13. Out of scope for v2

- **Tray-only apps** (section 3.1).
- **Store (UWP) apps.**
- **Keeping apps that are playing audio or using the microphone running.**
  Sleep would stop them too.
- **Detecting computer use.** With the lid closed and the PC locked, computer
  use can't drive the desktop anyway.
- **Windows 10 freezing.**
- **The optional Claude Code and Codex plugin,** saved as an idea on 15
  October. It would make the stall safety net exact.
- **Temperature limits,** dropped on 15 October.

## 14. Risks

- **The API is undocumented.** A Windows update could change it. Feature
  detection and the CI tests catch that; the fallback is "freezing off", never
  "apps stuck".
- **Antivirus.** An unsigned app that suspends other processes may look
  suspicious. Mitigations: minimal access rights, open source, builds made by
  GitHub with an attestation. If it becomes a problem, code signing is the
  answer.
- **Apps after a long freeze** may reconnect or refresh when they thaw, the
  same as after sleep.
- **A dependency that none of the three layers sees** costs at most about 3
  minutes of stalled work per lid-close, then freezing stops until the lid
  opens.
