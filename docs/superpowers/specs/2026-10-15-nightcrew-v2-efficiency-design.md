# Nightcrew v2: Efficiency mode for other apps while you're away

Date: 15 October 2026. Status: approved by the owner on 15 October 2026.

This spec adds one feature to Nightcrew v0.1.1. While an agent works with the
lid closed and nobody is at the laptop, Nightcrew puts the apps the agents
don't need into Windows' Efficiency mode, the same as Task Manager's
right-click Efficiency mode. When you come back, it puts them back exactly as
they were. It replaces section 7 of `2026-10-13-nightcrew-design.md` for v2.
Everything else in v1 (section 0 of that spec, the settings page and v0.1.1)
stays as it is.

**In one line:** when Windows would have put the laptop to sleep, Nightcrew
turns everything except the agents down to a whisper instead.

**Why not freezing.** The first draft of this spec froze apps. On 13 and 15
October a safety classifier stopped the code that suspends other processes,
so on 15 October the owner chose Efficiency mode instead. Apps keep running,
just slowly, which also removed the need for the freeze draft's stall safety
net and installer rule.

## 1. Goals

The owner wants this for three things:

- **Longer battery** on long closed-lid runs.
- **Less heat** in a bag. There is no temperature limit; the owner dropped
  that idea on 15 October.
- **Agents get the machine:** Claude Code, Codex and their jobs get the CPU
  that other apps would use.

Efficiency mode saves less than freezing. A busy background app (a browser
tab running scripts, a sync client) is slowed down a lot; an app that was
already idle changes little.

**Success means:**

- With the lid closed and nobody at the laptop, apps with windows that the
  agents don't need are in Efficiency mode within 10 seconds of the
  conditions in section 2 holding.
- Opening the lid, or touching the keyboard or mouse, puts them back within
  about a second.
- Nothing stays slowed down for long: after a crash or a kill, the watchdog
  puts every app back within 5 minutes, and the next start puts back anything
  left.
- Apps that were already in Efficiency mode, because you or the app set it,
  are left exactly as they were.
- Nightcrew stays light: this adds well under 0.1% of one core while away,
  and nothing at all otherwise.

## 2. When it applies and when it's undone

**Efficiency mode is wanted when all of these are true** (`EfficiencyGate`,
pure):

- the controller is **Armed** (an agent is working)
- the lid is **Closed**
- there has been no keyboard or mouse input for `AwayMinutes` (5)
- the owner's lid action for the current power source is **Sleep**,
  **Hibernate** or **Shut down**. With **Do nothing**, closing the lid
  normally keeps apps running at full speed, so Nightcrew leaves them alone
  too. The lid action is the owner's saved value (`journal.OwnerValues`),
  never the Do nothing that Nightcrew writes while armed.
- **Efficiency mode while away** is on (`EfficiencyWhileAway`, default true)
- Nightcrew isn't paused

**Every app goes back as soon as any of these happens.** Each one calls
`RestoreAll` directly, before anything else and without waiting for the tick:

| Event | Where | Then |
|---|---|---|
| Lid opens | lid-change callback | the usual tick |
| Keyboard or mouse input | 1-second input check, running only while apps are in Efficiency mode | Efficiency mode waits for another 5 minutes without input |
| Work ends (Disarm) | engine, before restoring the power settings | the usual lid action |
| Battery floor or lid-closed limit | engine, before Hibernate | hibernate |
| Windows suspends | `PBT_APMSUSPEND` callback | Windows sleeps or hibernates |
| Restart, shutdown or sign-out | `WM_QUERYENDSESSION`, before restoring the power settings | as today |
| Pause or Quit | panel actions | as today |

**While away, every 10-second tick re-plans** (section 3):

- an app that opened since the last tick goes into Efficiency mode
- an app that has become needed goes back. Examples: a browser that now has a
  connector running, a process an agent has just connected to, a new GPU
  holder.

## 3. Which apps

### 3.1 Candidates

A **candidate** is a process in the current Windows session that owns a
top-level window you could Alt+Tab to:

- visible (minimized counts), with a non-empty title
- not owned by another window, unless it has `WS_EX_APPWINDOW`
- not a tool window (`WS_EX_TOOLWINDOW`), unless it has `WS_EX_APPWINDOW`
- not cloaked by the app itself (`DWMWA_CLOAKED` = `DWM_CLOAKED_APP`). A
  window cloaked by the shell because it sits on another virtual desktop still
  counts.

Nightcrew only reads windows; it never sends them messages. Apps that live
only in the tray, with no window open, aren't candidates and keep running at
full speed: a tray app can't be told apart from a background helper an agent
might need.

Store (UWP) apps own no window themselves; `ApplicationFrameHost` does, and
it's a system process, so those are left alone. Packaged desktop apps such as
WhatsApp and the new Outlook are ordinary candidates.

### 3.2 Families

Nightcrew treats whole apps, never part of one. A **family** is:

- **the root:** the candidate's topmost ancestor in the same session, stopping
  below any system or shell process (Explorer, `svchost`, `sihost` and the
  rest of the system list). Every parent link is checked with start times: a
  parent that started after its child is a reused pid, not the real parent,
  as `AgentTreeSignal` already does.
- **every descendant** of the root
- **orphans with the root's image name:** helpers whose parent has exited,
  such as browser tab processes. This never applies to shared runtimes
  (`msedgewebview2`, `node`, `bun`, `deno`, `python`, `pythonw`, `java`,
  `javaw`, `cmd`, `powershell`, `pwsh`).

**If any member of a family is left alone, the whole family is left alone.**

### 3.3 What is always left alone

Each rule has a name, which the log uses when it explains a decision.

1. **Agent** (`agent`): the family of every agent process (`AgentProcessNames`:
   `claude`, `codex`) and every descendant of one. This keeps the Claude app,
   ChatGPT (Codex's host), and any terminal or editor running an agent at full
   speed.
2. **Keeping it awake** (`holder`): the family of every process listed under
   "Keeping it awake", which in practice means the GPU holders. `GpuSignal`
   starts reporting the pids of its busy processes, not just their names.
3. **Connector browser** (`connector`): for each running connector helper
   (`ConnectorHostNames`, default `chrome-native-host` for Claude and
   `extension-host` for Codex), walk up through `cmd` to the browser and leave
   that browser's family alone.
4. **Agent connection** (`connection`): the family of every process that an
   agent process, or one of its descendants, has an established loopback TCP
   connection to. These are found with `GetExtendedTcpTable` for IPv4 and
   IPv6, by matching the connection's remote port to the other process's
   local port. This covers dev servers, Ollama, LM Studio, a browser driven
   over DevTools, local databases, and tools that don't exist yet.
5. **Built-in categories** (`category`), matched by lower-case process name
   or prefix, in one list in `LeaveAloneRules`:
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
6. **Games** (`game`): game launchers and everything under them, plus
   anti-cheat: Steam, Epic, Riot Client, EA app, Battle.net, GOG Galaxy,
   Ubisoft Connect, Easy Anti-Cheat, BattlEye, Vanguard, FACEIT. Nightcrew
   never touches these.
7. **Windows** (`system`): system and shell processes. That covers csrss, dwm,
   winlogon, Explorer, sihost, the shell and start-menu hosts, SearchHost,
   TextInputHost, ctfmon, LockApp, ApplicationFrameHost, RuntimeBroker,
   svchost, taskhostw, dllhost, Settings, Task Manager, Process Explorer,
   System Informer, PowerToys, Widgets, Phone Link, screen readers, IME
   hosts, and Nightcrew itself.
8. **Your list** (`yours`): `LeaveAlone` in `settings.json`, by process name.
   Settings adds to it with Keep at full speed (section 6).
9. **Not allowed** (`denied`): a process Windows won't let Nightcrew change,
   such as apps running as admin or protected processes. The log notes this
   once per image per run.

## 4. Planning

`EfficiencyPlanner` is pure: a snapshot in, a plan out.

- **Snapshot:**
  - processes: pid, parent pid, name, start time, session
  - window owners: the candidate pids from 3.1
  - agent pids
  - holder pids
  - connector helper pids
  - loopback connections: owner pid, local and remote endpoints, state
  - the user's list
- **Plan:**
  - the families to put into Efficiency mode, each with its display name and
    pids
  - the families left alone, each with the rule that kept it

The display name is the image's `FileDescription` ("Microsoft Outlook"),
falling back to the process name. It's read once per image and cached.

**One process list per tick.** `AgentTreeSignal` already lists every process
each tick, and that list is most of a tick's cost (20–37 ms). The planner
reuses the same list through a per-tick cache in front of `IProcessTable`, so
Nightcrew never lists processes twice. Window enumeration (about 0.6 ms) and
the TCP tables (about 0.4 ms) run only while Efficiency mode is wanted.

## 5. How it works

### 5.1 The mechanism

For each process in a family, Nightcrew does what Task Manager's Efficiency
mode does:

- **power throttling on:** `SetProcessInformation` with
  `ProcessPowerThrottling`, `ControlMask` and `StateMask` both
  `PROCESS_POWER_THROTTLING_EXECUTION_SPEED`. On Windows 11 with efficiency
  cores, Windows runs the process on them at low clock speeds (EcoQoS).
- **priority** set to `IDLE_PRIORITY_CLASS`.

Both are documented Windows features. They work on Windows 10 1709 and later;
the throttling saves the most on Windows 11 with efficiency cores.

**Access:** Nightcrew asks only for `PROCESS_QUERY_LIMITED_INFORMATION |
PROCESS_SET_INFORMATION`. Handles are never inheritable and are closed right
away. Before changing or restoring a process, it checks the process start time
against what it expects, so a reused pid is never touched.

### 5.2 Leaving existing choices alone

Before changing a process, Nightcrew reads its current priority class and
power-throttling state.

- A process that is **already in Efficiency mode**, or already has power
  throttling or a priority below normal, is left as it is and not recorded.
  Chrome and Edge do this to their own background tabs, and you may have set
  some yourself.
- **On restore,** a value the app (or you) changed since Nightcrew set it
  is kept. Priority is put back only if it's still `IDLE_PRIORITY_CLASS`, and
  throttling only if it's still on. This matches how the power settings
  record already treats your changes.
- If Windows doesn't let Nightcrew read the throttling state, the process is
  treated as not throttled; only the priority check applies.

### 5.3 The record and crash safety

Windows doesn't undo Efficiency mode when the app that set it exits, so
Nightcrew keeps a record, `%LOCALAPPDATA%\Nightcrew\efficiency.json`, next to
`restore.json`. For each process it changed, the record holds:

- the pid and the start time
- the original priority class
- whether it turned power throttling on

The record is written (atomically, like `restore.json`) **before** a batch of
changes and updated after a restore. Every restore path puts back exactly
what the record lists and then deletes it.

- **At startup,** Nightcrew restores anything a previous run left in the
  record.
- **The watchdog task,** which already runs every 5 minutes, does the same
  when Nightcrew isn't running.
- **Worst case after a crash:** an app runs slowly for up to 5 minutes.
  Nothing ever stops.

### 5.4 Concurrency

`EfficiencyMode` (Platform) owns one lock, a generation counter, the list of
changed processes with their family, and the record.

- **`RestoreAll`** can be called from any thread, and calling it twice is
  harmless. Under the lock it increments the generation, restores every
  process, and deletes the record. The lid, input, suspend and end-session
  callbacks call it directly, so a restore never waits for a tick that's in
  progress.
- **`Apply(plan)`** runs on the tick thread. Under the lock, it checks that
  the generation still matches the one it started with; on a mismatch, it
  stops. It writes the record first, then changes each process. A process
  that fails is skipped and its family recorded as left alone (`denied`).
- **`Restore(family)`** puts back one family that has become needed.

### 5.5 Dry run

`DryRunEfficiency` logs the same plan with "would" and changes nothing. A dry
run can't reach Efficiency mode on a laptop, though: it doesn't hold the
laptop awake, so closing the lid sleeps it (and with the lid action set to Do
nothing, the gate rightly stays shut). So the first look on the owner's laptop
is a read-only preview of the plan instead. On 15 October it listed Outlook,
Wispr Flow and WhatsApp, left Brave, Chrome, ChatGPT, Claude and File Explorer
alone, and found no empty families. The real run in checklist step 9 follows.

## 6. What you see

**Settings page:**

- **Efficiency mode while away:** a switch, on by default.
- **Keep at full speed**, under the switch and only while it's on: the apps
  on the user's list by display name, each with a × to take it off, then
  **+ Add an app**.
- **+ Add an app** opens a short list of the apps Nightcrew would slow right
  now (the plan from section 4, minus the user's list), by display name.
  Clicking one adds the process name of the app's family root to
  `LeaveAlone`, remembers its display name in `LeaveAloneLabels`, and closes
  the list. With nothing to add, the list says "Nothing to add right now".
- As on the rest of the settings page, there are no explanations under the
  new settings.

**The main panel doesn't change.** An earlier build listed the apps from the
last time away on the main panel, each with a Leave alone switch; on the
owner's laptop that read as confusing. Most people never need the list, so it
lives in Settings for the few who see an app misbehave after being away.

**Log:**

- `Efficiency mode on: Outlook (olk, 4 processes), WhatsApp (whatsapp.root, 6 processes)`
- `Left alone: Brave (connector), Windows Terminal (category), Ollama (connection)`
- `Efficiency mode off for 2 apps: lid opened`. The reason is one of: lid
  opened, input, work ended, battery floor, lid-closed limit, suspend,
  session end, pause, quit, startup, watchdog.
- `Can't change <image>: access denied`, once per image per run
- In dry run, `Would put into Efficiency mode: …`

**README:** the "How it works" list gains one step after "You close the lid":
"Five minutes later, apps the agents don't need go into Efficiency mode, the
way Task Manager does it. They're back to normal the moment you open the
lid."

## 7. Settings

New keys in `settings.json`; every key you leave out keeps its default:

| Key | Default | Meaning |
|---|---|---|
| `EfficiencyWhileAway` | true | Put apps the agents don't need into Efficiency mode while you're away |
| `LeaveAlone` | `[]` | Process names that are never put into Efficiency mode |
| `LeaveAloneLabels` | `{}` | The name Settings shows for each of them, by process name; without one, Settings shows the process name |
| `ConnectorHostNames` | `chrome-native-host`, `extension-host` | Browser connector helpers; their browser is left alone |

The built-in lists are constants in code, not settings.

## 8. Architecture

**New units**, each small and tested on its own:

| Unit | Kind | Job |
|---|---|---|
| `Core/EfficiencyGate` | pure | Section 2's conditions in, wanted or not out |
| `Core/LeaveAloneRules` | data | The built-in lists from 3.3 |
| `Core/EfficiencyPlanner` | pure | Snapshot in, plan out (sections 3–4) |
| `Core/EfficiencyRecord` | data | The record from 5.3 and its JSON |
| `Core/EfficiencyCoordinator` | logic | Each tick, builds the snapshot, asks the gate and planner, and drives Efficiency mode. It also lists the apps it would slow now, for Settings' Add an app. |
| `Platform/EfficiencyMode` | Windows | Section 5: `Apply`, `Restore`, `RestoreAll`; its constructor loads what a crashed run left in the record, so `RestoreAll` puts that back too |
| `Platform/DryRunEfficiency` | Windows | Section 5.5 |
| `Platform/WindowOwners` | Windows | The candidate pids from 3.1 |
| `Platform/LoopbackConnections` | Windows | The TCP rows from 3.3 rule 4 |
| `Platform/TickProcessTable` | Windows | One process list per tick, shared by the agent signal and the planner |

**Changes to existing code:**

- `Engine.Tick` calls the coordinator after the controller step. In `Run`,
  Disarm and Hibernate call `RestoreAll` first.
- `EngineSnapshot` adds the number of apps in Efficiency mode.
- In `TrayApp`, the lid, suspend and end-session callbacks call `RestoreAll`
  first; Pause and Quit do too. A 1-second input timer runs only while apps
  are in Efficiency mode.
- `Program` creates `EfficiencyMode` (or the dry-run one), restores leftovers
  at startup, and wires in the per-tick process table.
- `Watchdog.Run` also restores leftovers from `efficiency.json`.
- `AppPaths` gains `EfficiencyFile`.
- `GpuSignal` reports its busy pids.
- `Settings`, `SettingsModel` and `PanelVisual` gain the switch and the
  Keep at full speed list. `TrayApp` fills Add an app from the coordinator,
  under the lock.

**Threading:**

- The tick and all planning run on the timer thread under `TrayApp._gate`, as
  today.
- `RestoreAll` takes only `EfficiencyMode`'s own lock, so it can run from any
  thread at any time.

## 9. Testing

**In this order:**

1. **The Efficiency mode core**, on processes the tests start themselves:
   - a child process goes to `IDLE_PRIORITY_CLASS` with throttling on, and
     comes back to its original priority with throttling off
   - the record is written before the change and deleted after `RestoreAll`
   - a new instance (as after a crash) restores leftovers from the record
   - a process that was already at idle priority or throttled is left alone
     and not recorded
   - a process whose priority changed after Nightcrew set it keeps the new
     value
   - a pid that's been reused (the start time doesn't match) is skipped
   - a process it can't change is reported as denied
2. **The pure units:**
   - `EfficiencyGate`: one table row per condition in section 2
   - `EfficiencyPlanner`, on made-up snapshots:
     - each rule from 3.3
     - candidate rules from 3.1
     - family integrity (one left-alone member keeps the whole family)
     - orphans grouped by image, but never for shared runtimes
     - reused pids rejected by start times
   - the strings in `SettingsModel`, and the Keep at full speed list on the
     settings page
3. **The engine with a fake `EfficiencyMode`:**
   - `RestoreAll` happens before Disarm restores the power settings, and
     before Hibernate
4. **On the owner's laptop:**
   - a read-only preview of the plan first (section 5.5)
   - then two new steps in `docs/hardware-checklist.md`:
     - **On and off:** close the lid with apps open while an agent works. After 5
       minutes, Task Manager's Status column shows the leaf icon for them;
       open the lid and the leaf is gone within a second.
     - **Crash:** kill Nightcrew while apps are in Efficiency mode; within 5
       minutes the watchdog puts them back.
5. **Resources:** the same before-and-after measurement as v0.1.1, idle and
   away.

## 10. Build order and the classifier

On 13 and 15 October a safety classifier stopped the code that suspends other
programs. Efficiency mode still changes other programs' settings, so the
build order puts that code first, where a block is cheap:

1. **`EfficiencyMode` and its tests, on throwaway processes only.** If the
   classifier stops this step, the build stops and the feature is dropped.
2. The pure units (gate, rules, planner, record).
3. The Windows readers (window owners, loopback connections, per-tick process
   table).
4. The coordinator, the engine and the watchdog.
5. Settings, panel and log.
6. README and hardware checklist.

## 11. Release

v0.2.0 is tagged after the read-only preview and the two new checklist steps
(9 and 10) pass. The release notes say Efficiency mode while away is new, on by default,
and can be switched off in Settings.

## 12. Out of scope for v2

- **Freezing apps** (see the top of this spec).
- **Tray-only apps** (section 3.1).
- **Store (UWP) apps.**
- **Keeping apps that are playing audio or using the microphone at full
  speed.** Sleep would stop them entirely.
- **The optional Claude Code and Codex plugin,** saved as an idea on 15
  October.
- **Temperature limits,** dropped on 15 October.

## 13. Risks

- **Smaller savings than freezing.** Measured in the resources step; if the
  difference is too small to matter, the owner decides whether to keep it on
  by default.
- **An app stays slow after a crash** for up to 5 minutes, until the watchdog
  runs. Opening it and finding it sluggish is the visible symptom; the next
  watchdog run or start of Nightcrew fixes it.
- **A dependency that none of the rules sees** runs slower while you're away,
  so the agent's work takes longer. It never stops.
- **Antivirus.** An unsigned app that changes other processes' priority may
  look suspicious. Mitigations: minimal access rights, open source, builds
  made by GitHub with an attestation.
