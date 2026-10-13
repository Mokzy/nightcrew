# Nightcrew design

Date: 13 October 2026. Status: draft v2 for owner review. v2 was revised after
an adversarial five-lens review the same day.

Nightcrew is a Windows tray app. It keeps a laptop working with the lid closed
for exactly as long as Claude Code or Codex is busy, and freezes the apps the
agents are not using. When the work is done, it carries out the Windows lid
action. When nothing is running, it changes nothing.

The technical claims come from a research pass on 13 October 2026, which
covered seven topics, each checked by an independent verifier. Anything that
only hardware can settle is a spike in section 13.

## 1. Goals and success criteria

The owner runs coding agents and GPU jobs on a Lenovo laptop (Windows 11 build
26300, Modern Standby only, hibernate available) and wants to close the lid and
walk away.

Nightcrew succeeds when:

1. **Nothing live.** Closing the lid does exactly what the Windows lid setting
   says. On wakes Nightcrew did not cause (Windows Update, other apps' wake
   timers), it changes nothing.
2. **Something live.** Provided the capability gate passes (6.2), closing the
   lid never stops the work, on AC or on battery. That holds until the
   lid-closed cap (default 8 h) expires or, on battery, the floor (default
   20%) is reached.
3. **Finishing.** When work finishes with the lid closed, Nightcrew starts the
   Windows lid action for the current power source within G + 30 s of the last
   busy-signal sighting (section 5), which is about 5.5 minutes by default.
   That sighting can come after the last visible activity, because signals are
   averaged over 60 s and a tool child holds for 10 minutes.
   - **Sleep:** the laptop reaches Sleep or hibernate within about 5 more
     minutes.
   - **Do nothing:** Windows' own idle timers apply. With the owner's
     13 October settings (AC lid Do nothing, AC Sleep after Never), the laptop
     stays on until the lid opens.
4. **No stranded state.** A crash, kill, sign-out or power loss never leaves
   apps frozen. Settings Nightcrew changed come back as follows:
   - **power, mute and Wi-Fi:** within 5 minutes while the owner is signed in
     (the watchdog relaunches or restores), or otherwise at the next sign-in
   - **Do nothing at the logon screen:** a stranded DC sleep timeout is
     bounded (6.3), so the laptop still sleeps eventually.
5. **Simple install.** One .exe, with no runtime, no admin rights, no hooks or
   plugins, and no edits to agent configuration.

## 2. Owner decisions (13 October 2026)

- **Automatic.** There are no timed sessions to start; the app runs from login.
- **Live means any of:**
  - an agent mid-task
  - a busy background job that an agent started, such as a training run
  - a manual tray hold
- **Not live:** an agent waiting for the owner's approval.
- **Freeze, don't close.** Apps are frozen, never closed, and only when the
  lid closes while something is live.
- **Extras:**
  - a lid-closed cap (default 8 h)
  - a battery floor (default 20%)
  - lid open unfreezes everything
  - a phone-hotspot fallback
- **No plugin or skill in v1.** Detection uses process and file signals.
- **Build.** Modern C# targeting .NET Framework 4.8, shipped as a single small
  .exe, under the MIT licence, with releases built by GitHub Actions.
- **Idle lid behaviour stays with Windows.** Nightcrew restores the owner's
  values and never imposes its own. On 13 October the owner deliberately set
  AC and DC to Do nothing. The consequence is spelled out in criterion 3.
- **Settings changes are run by the owner.** Anything that changes power
  settings is run by the owner; Claude tests in dry-run mode.

## 3. Terms

| Term | Meaning |
|---|---|
| **G** | Grace period, default 5 min. A session or job is live while any busy signal (section 5) was seen within the last G. This is the only hysteresis. |
| **N** | Sampling window for busy signals; provisional 30 s, tuned by S7. |
| **F** | Cycle-delta floor meaning "a process executed"; set by S7. |
| **Live** | Any session or job is busy within G, or Hold awake is on. |
| **Shut** | A level that is true while all three hold: the lid reads Closed; no external display is active (6.7); and this session is not remote-connected (`WTSClientProtocolType` ≠ 0, for as long as the connection lasts). |
| **Away** | Shut holds and no user-back signal has arrived in the last D (default 60 s). User-back signals after lid close: keyboard or mouse input newer than the close (GetLastInputInfo); the session display turning on, unless it is an AC/DC display burst within 10 s of a power-source change; or user presence changing to present. A presence reading already in place at the moment of closing is ignored, because it lags input by up to 240 s. |
| **Episode** | Open while the restore record holds at least one LIDACTION or STANDBYIDLE entry in `settings[]`. A record that holds only `finishTemp`, a deferred Wi-Fi restore or mute entries is *restore pending*, not an episode. The record is always written before the first write. |
| **Owner values** | The lid and sleep-after values captured when an episode starts. While the episode's record exists, they are reused and never re-captured. The one exception is drift adoption (6.8), which journals an outside change as the new owner value. |
| **Spent** | A latch set when a floor or cap finish completes. It clears when the lid is seen open or an external display becomes active. While spent, Nightcrew starts no episode and Hold awake is off. |
| **Clock** | Every interval uses QueryUnbiasedInterruptTime, which excludes sleep and is unaffected by DST or clock changes. This covers G, N, D, the cap, the tool-child limit, the exited-process cache, chain timers and watchdog timing. Persisted timestamps are UTC, except `instance.json`'s last resume time, which is stored as unbiased time together with the boot ID (9.7). |

## 4. Controller

The controller is level-triggered. On every tick and every event, it computes
the target state from the current levels. Events only bring the next
evaluation forward.

### 4.1 Target state, in priority order

1. Paused is on → **Paused**.
2. A Finishing run is in progress and no exit in 4.4 applies → **Finishing**.
3. Nightcrew holds the keep-awake request (Armed or Working), Shut holds, and
   any of: (on DC, battery ≤ floor), (lid-closed time ≥ cap), (the hotspot
   usage limit is reached) → **Finishing**, with reason *cap* for the cap and
   *floor* otherwise.
4. Live, Away, the gate passes and not spent → **Working**.
5. Live and not spent → **Armed** (shown as *Live*). If the gate fails, Armed
   runs degraded (6.2).
6. Not live, an episode is open, and either Shut holds or (the lid is Unknown
   after a resume and no user-back signal has arrived since it) →
   **Finishing** with reason *work done*. A user-back signal while Shut holds
   doesn't block the finish; it only postpones the chain and the suspend call
   (4.4).
7. Otherwise → **Idle**.

| State | Lid action | Sleep-after | Keep-awake request | Frozen apps | Mute / lock |
|---|---|---|---|---|---|
| Idle, Paused | owner's | owner's | none | none | owner's |
| Armed | Do nothing (AC and DC) | bounded (6.3) | held | none | owner's |
| Working | Do nothing | bounded | held | yes | muted (6.9), locked (6.5) |
| Finishing | owner's (restored first) | owner's; 60 s temporarily in chain step 2 | released | none | restored |

### 4.2 Entry and exit actions

- **Into Armed from Idle, Paused or Finishing.**
  1. If a `finishTemp`-only record exists, restore it first (9.2).
  2. Take the ownership lock (9.5).
  3. Write the record, capturing owner values afresh unless an episode record
     exists.
  4. Write the settings.
  5. Take the request.

  If Away already holds, for example when a wake timer woke the laptop with
  the lid closed and the overnight runner then went live, continue straight to
  Working in the same evaluation.
- **Armed → Working.** Freeze (section 7) unless computer use is active (7.4),
  mute (6.9), lock (6.5).
- **Working → Armed (user back):**
  1. If lock-on-return is active (6.5) and the lid reads Open or an external
     display is active, call `LockWorkStation`.
  2. `ThawAll`, called directly from the callback thread. Other user-back
     signals while Shut still holds thaw without locking.
  3. Restore mute.
- **Armed or Working → Idle.** RestoreAll (9.3).
- **Into Finishing.** See 6.6.
- **Into Paused from any state.** Cancel any Finishing run: stop the chain
  timers and restore `finishTemp`. Then RestoreAll. Leaving Paused triggers a
  fresh evaluation.

### 4.3 Resume

On `PBT_APMRESUMEAUTOMATIC`:

1. Set the lid to Unknown and re-register the lid callback, whose initial
   value is the current state. Wait up to 5 s for it.
2. While the lid is Unknown, never freeze.
3. If no episode was open at suspend, restore whatever the record holds
   (`finishTemp`, a deferred Wi-Fi restore, mute) and delete it. Do nothing
   else: Windows handles the wake, even with the lid closed.
4. If an episode was open, evaluate normally. Rule 6 covers a lid that is
   still Unknown.

Austerity wake blips (5–10 s, lid closed) need nothing beyond this.

### 4.4 Finishing exits

These are evaluated on every tick and event (4.1 rule 2), and again just
before each chain step and any `SetSuspendState` call.

- **Shut ends** (lid open, external display, remote connection). Cancel the
  chain timers and restore `finishTemp` if it was written. Then, if live, arm
  as described in 4.2; otherwise go to Idle.
- **User-back signal while Shut still holds.** Postpone the next chain step
  and the suspend call until Away holds again. The run stays in Finishing.
- **Live returns** (only when the reason is *work done*). Cancel, restore
  `finishTemp`, and arm as described in 4.2. The arm captures owner values
  afresh, or uses `finishTemp.ownerValue` if that restore failed. Evaluation
  continues to Working if Away holds.
- **Reason floor or cap.** New live signals are ignored. Completion sets spent
  and turns Hold awake off.
- **Before the suspend call.** Re-check each of these:
  - Away holds, or the lid is still Unknown with no user-back signal since the
    resume
  - nothing is live (for *work done*)
  - no input has arrived since Finishing began

  If any check fails, never call `SetSuspendState`. Take the matching exit
  above instead.
- **Reason work done, lid action Do nothing.** Go to Idle right after
  RestoreAll. Floor and cap finishes always carry out the 6.6 hibernate, or
  its fallback, whatever the lid action.
- **Paused.** Cancel as in 4.2. The run does not resume.
- **After a suspend.** On resume, follow 4.3.

### 4.5 Cap and Hold

- **Lid-closed time** is unbiased time during which Nightcrew holds the
  keep-awake request and Shut holds. It counts from when the lid was last seen
  open or an external display was last active. It is persisted in
  `state.json`, so it survives Armed↔Working blips, user-back signals, sleeps
  and restarts.
- **A cap finish** sets spent. A later lid-closed wake does not re-arm until
  the lid has been opened.
- **Hold awake** counts as live until it is switched off. It is persisted in
  `state.json`. Floor and cap finishes switch it off.

## 5. Live detection

### 5.1 Classifying processes

Every classification is by full image path and arguments, never by file name.
Three different kinds of process are all called `claude.exe`.

| Kind | Recognized by | Role |
|---|---|---|
| Claude desktop host | `%LOCALAPPDATA%\AnthropicClaude\app-*\claude.exe`, plus Electron helpers with `--type=`; or MSIX `WindowsApps\Claude_*` | host app: never live, never frozen |
| Claude Code session, desktop | `%APPDATA%\Claude\claude-code\**\claude.exe` whose validated parent is the desktop host | agent (interactive) |
| Claude Code session, CLI or headless | `~\.local\bin\claude.exe`, or the path above with a parent that is not the desktop host | agent; headless rules below |
| Codex host | `WindowsApps\OpenAI.Codex_*\ChatGPT.exe` (MSIX) | host app |
| Codex agent | `%LOCALAPPDATA%\OpenAI\Codex\bin\**\codex.exe` (`app-server`, `exec`), or the standalone `codex` CLI | agent |
| Codex sandbox work | `codex-command-runner.exe`, and processes owned by `CodexSandbox*` users | Codex work; never opened and never frozen |

**Headless runs.** A `-p` or `stream-json` run that the desktop host did not
spawn is classified by ancestry:

- Launched by Task Scheduler, a `python` runner or an interactive shell: it
  counts. This covers the owner's overnight runner.
- Descended from claude-mem's `bun … worker-service.cjs --daemon`: ignored by
  default.
- Anything else: it counts, and appears in "Why awake" with an Ignore option.

**User-defined agents** are configured through `settings.json` (11.4) as kind
`generic`.

### 5.2 Claude Code busy signals

A session is busy within the window N if any of these holds:

1. **Transcript growth.** Nightcrew saw
   `~\.claude\projects\<slug>\<session-id>.jsonl` grow, or a subagent
   transcript under `<session-id>\subagents\` grow. Transcript age is measured
   as the unbiased time since Nightcrew last saw the file grow, not by file
   timestamps.

   Triggers come from a recursive `FileSystemWatcher`. As a backstop, the
   length of every mapped file is checked on each tick, because watcher events
   can lag or overflow. Directories on the ignore list (by default
   `*claude-mem-observer-sessions*`) are skipped.

   A session is mapped to its file, in order, by:
   1. `CLAUDE_CODE_SESSION_ID` from a child process's environment (marker-only
      read, 5.7)
   2. `--resume=<id>`
   3. the newest non-ignored `.jsonl` in the slug directory derived from the
      session's working directory

   A session that can't be mapped is shown as "unmapped" in "Why awake" and is
   judged on rules 2–4.
2. **Tool child.** A non-baseline descendant holds the session while its
   subtree's cycle delta over the last 60 s exceeds F, or for 10 minutes after
   it spawns, whichever is longer. Baseline children are conhost and processes
   with `CLAUDECODE=1` but no `CLAUDE_CODE_CHILD_SESSION` (MCP servers). If the
   environment read fails for a session descendant, it is treated as a tool
   child.
3. **MCP call in progress.** In the last 64 KB of the transcript, the newest
   `tool_use` id has no matching `tool_result`, and an MCP child's cycle delta
   exceeds F. S7 calibrates F so that an approval wait with a browser MCP open
   falls below it.
4. **Tiebreaker.** The session's own CPU, averaged over 60 s, exceeds the
   higher of 8% of one core and its learned idle p90 plus a margin.

### 5.3 Codex busy signals

Codex is busy if any app-server, exec or CLI process, or any sandbox runner,
shows a cycle delta above F within N. Sandbox runners' CPU is read from class 5
(5.7), without a handle. If S7 finds a reliable file signal (rollouts under
`~\.codex\sessions`, or the sqlite state), it becomes the primary signal.

### 5.4 Generic agents

A generic agent is busy on rule 2 of 5.2 (tool child) plus its own cycle
delta above F over 60 s.

### 5.5 Background jobs

1. **Exclude first:**
   - the claude-mem worker tree
   - baseline MCP servers and their subtrees, while their session is alive

   These never become jobs; MCP activity counts only through 5.2 rule 3.
2. **Tag** a process as agent-started, checking in order:
   1. Its validated parent chain reaches an agent session.
   2. Its environment has `CLAUDE_PID` or `CLAUDE_CODE_CHILD_SESSION`. This is
      read only when rule 1 can't decide because the chain is broken, and only
      for processes created after an agent session was first seen.
   3. A tagged parent sits in the exited-process cache (10 min).

   The tag persists after the parent exits.
3. **Busy** means at least 15% of one core averaged over 60 s, or GPU engine
   use of 10% or more. Only CPU and GPU count in v1. S7 confirms the
   thresholds against job baselines.

### 5.6 Hold

The tray's **Hold awake** toggle (4.5).

### 5.7 Process plumbing

**Never-open list.** Before any `OpenProcess`, for any purpose, each process
is matched by image name (class 252) and ancestry from the snapshot. A name
match alone is enough to never open it. The list covers:

- game-launcher and anti-cheat families (7.3)
- password managers
- `CodexSandbox*` processes and `codex-command-runner.exe`

This list holds whatever the freeze-category toggles say.

**Environment reads.** These use `PROCESS_QUERY_INFORMATION | PROCESS_VM_READ`
on same-user processes outside the never-open list, and only in the cases
named in 5.2 and 5.5. The block is parsed in place, and only `CLAUDECODE`,
`CLAUDE_PID`, `CLAUDE_CODE_CHILD_SESSION` and `CLAUDE_CODE_SESSION_ID` are
kept. Everything else is discarded at once and never cached or logged. A unit
test checks this.

**Field sources:**

| Field | Source | If unavailable |
|---|---|---|
| PID, parent PID, image name, SequenceNumber | `NtQuerySystemInformation` class 252, every 1–2 s while Idle or Armed and every 15 s while Working | class 5 on older builds |
| CreateTime | `GetProcessTimes` on handles Nightcrew already holds; otherwise class 5 on demand, cached per (PID, sequence) | link not used for the on-disk record |
| SessionId | one class-5 call at freeze planning time | not frozen |
| Full path | `QueryFullProcessImageName` with QLI | never classified as an agent |
| Full path without a handle (used only to narrow never-open name matches) | class 88 (`SystemProcessIdInformation`) by PID, if S7 confirms it works for a standard user | name match only |
| Command line | `NtQueryInformationProcess(ProcessCommandLineInformation)` with QLI | treated as unknown headless (counts) |
| Owner SID (Codex sandbox) | `WTSEnumerateProcessesEx`, no handle needed | descent from `codex-command-runner.exe` |
| CPU of unopenable processes | class 5 | n/a |

**Identity.** At runtime, a parent link is valid when the parent's sequence
number is lower than the child's. Every walk guards against cycles. On disk,
processes are identified by (PID, CreateTime).

**Counters.** Only per-process deltas are used; tree totals are never
differenced. Nightcrew holds a `PROCESS_QUERY_LIMITED_INFORMATION` handle on
each tracked process. The handle keeps the PID reserved, signals exit, and
still allows reading the final counters. CPU comes from
`QueryProcessCycleTime` and `GetProcessTimes`.

**GPU.** One long-lived raw PDH query on
`\GPU Engine(*)\Utilization Percentage`, collected only while a tagged job
exists. The busiest engine per `pid_` is used.

**Not used:**
- network byte counters (none exist without admin)
- I/O counts (idle sessions churn them)
- other apps' power requests (reading them needs admin)

### 5.8 Why awake

Every tick, Nightcrew records the decision with its reasons, using the tick
schema in 11.6. The tray lists the current holders, each with an Ignore
option. Ignore entries are stable keys: kind, image path and an argument
pattern, or a transcript directory. They can be undone under **Ignored ▸**.

### 5.9 Quiet agents

An approval wait, a usage-limit retry wait and "done" all go quiet, so live
ends after G in every case. For approval waits, that matches the owner's
decision. For retry waits it is a v1 limitation, raised as owner question 14.1.

## 6. Power control

### 6.1 Writes

All reads and writes go through powrprof. Nightcrew never parses `powercfg`,
never writes the registry, and launches no child processes during an episode.

1. `PowerGetActiveScheme`
2. `PowerWriteACValueIndex` and `PowerWriteDCValueIndex` for each setting:
   - LIDACTION: `4f971e89…` / `5ca83367…`; indices 0 Do nothing, 1 Sleep,
     2 Hibernate, 3 Shut down
   - STANDBYIDLE: `238c9fa8…` / `29f6c1db…`
3. `PowerSetActiveScheme(sameGuid)`
4. Read back.

Each write's return code is checked. The record's `lastWritten` is updated
before every write (9.2). By default no admin rights are needed; on the dev
machine BUILTIN\Users has KRKW. S1 checks whether re-activating the scheme
resets the Power-mode overlay. If it does, drop step 3, provided the writes
still take effect without it.

### 6.2 Capability gate

**Write check.** Before each arm, Nightcrew runs
`PowerSettingAccessCheckEx(KEY_WRITE)` for LIDACTION and STANDBYIDLE on both AC
and DC, and reads the policy keys under
`HKLM\SOFTWARE\Policies\Microsoft\Power\PowerSettings`.

- **Error 1640** means a remote session. Keep the last good result rather than
  reporting a policy block.
- **Gate failure** (override, denial or read-back mismatch) puts Nightcrew in
  degraded mode:
  - no LIDACTION writes and no freezing
  - live, including Hold, still enters Armed with the request held
  - sleep-after is changed only if its own check passed
  - Working never fires
  - status: "Lid control unavailable (policy). Keep the lid open."

**Startup checks.** These are reported in `--diagnose` and the dry-run log.
Only the first can affect behaviour.

- Hibernate available? (`GetPwrCapabilities`: SystemS4 and HiberFilePresent).
  If not, see 6.6.
- `SYSTEMREQUIRED` AC/DC policy, any `PowerRequestOverride` entry for
  `nightcrew.exe`, and `AoAc`. These produce a tray warning only.

### 6.3 Sleep-after during an episode

**Why it has to change.** On Modern Standby with DC power, Windows ends
system and execution requests 5 minutes after the sleep timeout expires. The
timeout counts from the last user input. With the owner's 60-minute DC value,
lid-closed work on battery would stop about 65 minutes after the last input.

**Bounded value.** During an episode, Nightcrew writes a bounded value, not
Never:

> (now − last input) + remaining cap + 30 min

- **When it is written:** on arm and at lid close, and again whenever headroom
  drops below 30 minutes. Every write is journaled.
- **Which sources:** DC always. AC only if the owner's AC value is not
  already Never; AC requests block idle sleep indefinitely, so AC needs this
  only as a backstop.
- **If stranded:** a value left behind after a crash still ends in sleep,
  even at the logon screen.

**Undocumented.** Whether a large DC value lifts the 5-minute request limit
isn't documented, and one Microsoft page words the limit with no timeout
qualifier. S2 settles it. If the DC run sleeps, Nightcrew limits lid-closed
time on DC to the measured hold minus a margin, hibernates before that point,
and shows "Battery hold limited".

**Timing.** The lid action must already be Do nothing before the lid closes,
because requests don't survive a user-initiated sleep.

### 6.4 Keep-awake request

While Armed or Working, Nightcrew holds one `PowerCreateRequest` handle (reason
"Nightcrew: agents working") set to SystemRequired and ExecutionRequired.

- **Verification:** by the API return codes only.
  `CallNtPowerInformation(SystemExecutionState)` is a system-wide aggregate and
  is logged as a diagnostic.
- **Re-assert** (clear, then set) on resume, AC/DC change, unlock, and every 5
  minutes.
- **DisplayRequired** is never used.

Screen Off is acceptable; Sleep is the failure. If `PBT_APMSUSPEND` arrives
while Working, Nightcrew logs "hold failed" with AC/DC, timers, lock state and
holders. The owner's elevated `powercfg /requests` snapshot in S2 is the only
attribution check.

### 6.5 Lock

**Lock when the lid closes** is on by default. Entering Working calls
`LockWorkStation`, unless computer use is active (7.4).

Microsoft's PowerToys documentation says user-mode apps can't hold power
requests at the lock screen, so S2 tests the locked case. If a locked session
loses the hold, Nightcrew switches to **lock on return**:

- the session stays unlocked while Working
- when the lid reads Open or an external display becomes active,
  `LockWorkStation` runs first, then `ThawAll`; both take well under 1 s
- other user-back signals while the lid still reads Closed thaw without
  locking
- Finishing locks first (6.6)

That way an unattended laptop is never handed over unlocked.

### 6.6 Finishing

**Order:**
1. If lock-on-return is active and the session is unlocked, call
   `LockWorkStation`. The hold is about to be released, so this costs nothing.
2. Stop hotspot work and probes.
3. `ThawAll`.
4. RestoreAll, in the 9.3 order: mute, Wi-Fi, power values, release the
   request.
   - **Work-done finishes** wait at most 10 s for the Wi-Fi reconnect, which
     then carries on in the background.
   - **Floor and cap finishes** defer Wi-Fi to resume (4.3).
5. Carry out the action. Floor and cap finishes always hibernate, or use the
   fallback, whatever the lid action.

| Reason, and the owner's lid action for the current source | Action |
|---|---|
| work done, Do nothing | none; go to Idle |
| work done, Sleep | sleep chain |
| work done, Hibernate | hibernate |
| work done, Shut down | hibernate (avoids unsaved-work loss; logged) |
| floor or cap | hibernate |

**Hibernate** means enabling SeShutdownPrivilege and then calling
`SetSuspendState(TRUE, FALSE, FALSE)`, with the return value and
`GetLastError` checked. `SetSuspendState(FALSE)` is never called.

If hibernate is unavailable or fails, Nightcrew runs chain steps 1–2 and
repeats them. The tray shows "Hibernate off: floor and cap use sleep".

**Sleep chain.** S5 verifies and orders the steps. Exits in 4.4 are checked
before each step.

1. Send `SC_MONITORPOWER 2` to Nightcrew's own window. Never send it via
   `HWND_BROADCAST`, and only send it after the thaw. Sysinternals PsShutdown
   `-x` documents monitor-off as a route into Modern Standby.
2. If no `PBT_APMSUSPEND` has arrived after 2 minutes, set the current
   source's STANDBYIDLE to 60 s temporarily, then wait 3 more minutes. The
   episode's record is already deleted by now, so this step first creates a
   record holding only `finishTemp` (9.2).
3. Still no suspend: hibernate.

Other apps' idle requests, such as the Claude desktop app's, can block step 1
on AC. Hibernate is the backstop.

### 6.7 External display

An external display counts as active if any active path (`QDC_ONLY_ACTIVE_PATHS`)
has a target whose `outputTechnology` is none of:

- `INTERNAL`, `DISPLAYPORT_EMBEDDED`, `UDI_EMBEDDED`
- `INDIRECT_VIRTUAL` or `MIRACAST`, unless the device path is on the
  settings allow-list

This is re-evaluated on `WM_DISPLAYCHANGE` and on every tick. An active
external display makes Shut false. Unplugging it lets Shut, and then Away,
hold again.

### 6.8 Drift

Every tick during an episode, Nightcrew re-reads the lid and sleep-after
values.

- **If a value differs from `lastWritten`:**
  1. journal the new value as the owner value
  2. rewrite Nightcrew's own value and verify it
  3. log the change
- **If the same setting drifts more than 3 times in 10 minutes** (for example
  Lenovo Vantage fighting back):
  1. stop rewriting it
  2. mark the gate failed for the rest of this episode, so the controller
     runs degraded Armed (6.2): ThawAll, restore mute, keep the request
  3. show "Lid control unavailable (drift: <setting>)" and log it

### 6.9 Mute

1. **On entering Working,** record the default render endpoint's ID and
   whether it was already muted. Mute it only if it was unmuted.
2. **If the default endpoint changes while Working**
   (`IMMNotificationClient.OnDefaultDeviceChanged`), mute the new default the
   same way. The record keeps a list of endpoint entries.
3. **On restore,** each recorded endpoint goes back to its prior state.
   Nightcrew never unmutes an endpoint the user had muted.

## 7. Freezing

### 7.1 Mechanism

**Primary mechanism.** Windows 11 process state-change objects
(`NtCreateProcessStateChange` and `NtChangeProcessState`). Nightcrew holds one
handle per frozen process and thaws by closing the handles.

- **Auto-thaw.** If the handle owner dies, the kernel thaws the process. These
  objects freeze threads created later too, and other tools can't resume them.
- **Detection.** The API is undocumented. Nightcrew feature-detects it at
  startup, and S6 verifies it.
- **No fallback.** Where the API is missing (Windows 10, older Windows 11
  builds), freezing is off. NtSuspendProcess is not used as a fallback.

**Access.** Nightcrew requests only
`PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_SUSPEND_RESUME | PROCESS_SET_INFORMATION`.
Handles are never inheritable. `ERROR_ACCESS_DENIED` (elevated, UIAccess or
protected processes) means: leave the process running and log it once.

**Concurrency contract.** The Freezer owns a lock, a generation counter and a
"thawed" latch.

- **ThawAll** is idempotent and callable from any thread. Under the lock it:
  1. increments the generation
  2. sets the latch
  3. closes every handle

  The lid and presence callbacks call it directly, then post the state change
  to the UI thread.
- **The freeze worker,** for each process, takes the lock and checks two
  things: the generation still matches its pass, and the latch is clear. Then
  it creates and applies the state-change object.
  - **On a mismatch:** close any handle it just created, and abandon the pass,
    including the re-snapshot loop.
  - **Starting a new pass:** only the controller entering Working starts a new
    generation and clears the latch.
- **Safety net:** on entering Armed or Idle, the controller asserts that no
  handles remain. If any do, it calls ThawAll.

### 7.2 What gets frozen

**Candidates** are processes in the interactive session that own a top-level,
Alt+Tab-eligible window.

- **Eligibility:** the owner and last-active-popup rule, plus the
  WS_EX_TOOLWINDOW and WS_EX_APPWINDOW flags.
- **Still counted:** minimized windows, and windows cloaked only because they
  sit on another virtual desktop (`IVirtualDesktopManager`).
- **Not counted:** windows the app itself cloaks, and shell surfaces.
- **No messages.** Nightcrew only reads foreign windows with `GetWindowText`;
  it never sends them messages.

**Family.** The freeze unit is a family:

- the topmost same-session ancestor below the shell, with every link validated
- all of that ancestor's descendants
- orphans with the same image path as the family root (processes with no live
  validated parent)

Same-image grouping never applies to shared runtimes: msedgewebview2, node,
bun, deno, python, pythonw, java, javaw, cmd, powershell, pwsh.

**Freezing a family:**
1. Members are frozen back to back, root first.
2. If any member is excluded or access is denied, the whole family stays
   running.
3. Afterwards, Nightcrew re-snapshots at most 3 times to catch processes
   spawned during the freeze.

**Packaged and UWP apps** are left running in v1.

### 7.3 Never frozen

Built-in, always enforced:

- **Live holders**, each with its whole family, checked at freeze time and
  before every re-snapshot pass:
  - agent families: the Claude desktop host, ChatGPT/Codex, and any terminal
    or editor hosting an agent (VS Code and Cursor renderers, Windows Terminal,
    conhost)
  - tagged jobs
  - every process listed in "Why awake"
- **Agent-connected browsers.** For each Claude `chrome-native-host.exe` or
  Codex `extension-host.exe`, walk up through `cmd.exe` to the topmost process
  of the browser image and keep that browser family running.
- **System and shell processes:** csrss, dwm, winlogon, fontdrvhost, explorer,
  sihost, ShellExperienceHost, ShellHost, StartMenuExperienceHost, SearchHost,
  TextInputHost, ctfmon, TabTip, LockApp, ApplicationFrameHost, RuntimeBroker,
  svchost, taskhostw, dllhost, conhost, OpenConsole, WindowsTerminal,
  SystemSettings, SecurityHealthSystray, Widgets*, PhoneExperienceHost,
  CrossDevice*, Taskmgr, procexp*, SystemInformer, PowerToys*, screen
  readers, IME hosts, and Nightcrew itself.
- **The never-open list (5.7).** These processes aren't even opened.

Default categories, each switchable under **Never freeze ▸** and all on by
default:

- browsers with a Claude or Codex native-messaging host registered (agents may
  start browser automation later)
- meeting, dictation and recording apps: Wispr Flow, Granola, Jamie, Teams,
  Zoom, Webex, OBS, Loom
- password managers and SSH agents: 1Password, Bitwarden, NordPass, KeePassXC,
  Dashlane, Proton Pass
- VPN and proxy clients
- virtualization and containers: Docker Desktop, WSL, VirtualBox, VMware
- local model runtimes: Ollama, LM Studio
- editors, IDEs and terminals
- remote-desktop and screen-sharing tools
- game launchers and everything under them, plus anti-cheat clients. These
  stay on the never-open list even if their freeze category is switched off.

Dynamic checks, run at freeze time and on every Working tick (7.4):

- processes with an active audio session
- owners of loopback listeners that an agent family is connected to, plus the
  configured proxy port's owner (`GetExtendedTcpTable`)
- full-screen or presentation state (`SHQueryUserNotificationState`), which
  skips freezing entirely
- computer use active (7.4), which also skips freezing entirely

The user's own never-freeze list lives in `settings.json`. **Would freeze now ▸**
previews every family and the rule that decided it.

### 7.4 While Working

Every tick, Nightcrew:

- **Re-runs the dynamic checks** and also looks for new native-host processes.
  Any family that becomes needed is thawed and pinned as never-freeze until the
  lid opens.
- **Watches for computer use.** Checked at freeze time and on every tick.
  While it is active, Nightcrew calls ThawAll if anything is frozen, freezes
  nothing, and logs it. It also doesn't lock while computer use is active. If the session is already
  locked, it stays locked; computer use can't drive a locked desktop.
  - **Claude:** the transcript has a pending `tool_use` whose tool name matches
    a configurable prefix list.
  - **Codex:** `codex-computer-use-swift.exe` (configurable) shows a cycle
    delta above F.
  - **Fallback:** if S7 finds neither signal reliable, the README says
    computer-use tasks need **Lock when the lid closes** off.
- **Watches for installers.** If an agent tree runs an installer-class process
  (`msiexec`, `winget`, an image with setup, install or update in its name, or
  an elevation-requesting exe), Nightcrew calls ThawAll and keeps freezing off
  until it exits. Restart Manager otherwise kills frozen apps that hold files.

### 7.5 Thaw and shutdown

- **Lid-open path:** the callback calls ThawAll before anything else; target
  under 1 s. Windows may still show frozen apps as "Not responding" until
  they catch up.
- **Shutdown order:** at startup, call `SetProcessShutdownParameters(0x3FF, 0)`.
- **`WM_QUERYENDSESSION`:**
  1. ThawAll and RestoreAll synchronously, in well under 1 s
  2. log the state, the live holders and the lParam flags
  3. return TRUE; never veto

  The next launch shows "Interrupted by restart at hh:mm" once.
- **`WM_ENDSESSION(FALSE)`:** re-evaluate.
- **`ShutdownBlockReasonCreate`:** used while Working, only to get the longer
  end-session allowance.
- **`PBT_APMSUSPEND`:** ThawAll, then flush the log and the record within the
  2 s budget. After a lid-closed resume with work live, the level-triggered
  evaluation re-freezes. S6 checks how freezes behave across sleep and
  hibernate.
- **Unhandled exception:** ThawAll.
- **UI thread:** does only quick work, so end-session messages always get
  pumped.

## 8. Hotspot fallback

Everything goes through `wlanapi.dll` via P/Invoke. Nothing calls `netsh`, and
no Location permission is needed.

**Calls used:**
- `WlanGetProfileList` and `WlanGetProfile`
- `WlanConnect` (profile mode)
- `WlanQueryInterface` for interface state and radio state
- ACM notifications, subscribed from startup

**Never called:** `WlanScan`, `WlanGetAvailableNetworkList`,
`WlanGetNetworkBssList` or `current_connection`.

**Knowing the original network.** In order of preference:
1. the last profile seen through ACM
2. NLM `INetwork::GetName` of the connected Wi-Fi network, matched
   case-sensitively against saved profile names
3. "unknown"

**Detecting an outage.** Probing runs only while Working.
- **Probe:** an HTTP GET of `www.msftconnecttest.com/connecttest.txt` with a
  content check, plus HTTPS HEAD requests to `api.anthropic.com` and
  `api.openai.com`. Any completed TLS status counts as reachable.
- **Cadence:** every 20–30 s, every 10 s after a failure.
- **Down:** every target has failed for at least 45 s. A WLAN disconnect,
  NLM or ACM event only brings the next probe forward.
- **Skip switching** if NLM reports internet on any non-WLAN interface (USB
  tether, Ethernet), or if the Wi-Fi radio is off.

**Switching:**
1. Journal the original network (or "unknown") and the profile order.
2. Connect to the first hotspot, waiting up to 45 s for
   `connection_complete`.
3. Wait about 10 s for DHCP and NCSI, then probe.
4. On failure, try the next hotspot, then back off at 2, 5 and 10 minutes.

**Staying and leaving.** Nightcrew stays on the hotspot until Finishing or lid
open. On leaving, it restores the profile order (where it has the rights) and
reconnects the original network. If the original is unknown, it tries the
saved auto-connect, non-hotspot profiles in profile-list order. It never stays
on the hotspot and never issues a bare `WlanDisconnect`.

**Usage limit.** Hotspot bytes (`GetIfEntry2`) and time are logged for each
episode. There is an optional limit: off, 500 MB, 1 GB or 5 GB, or 1, 2 or
4 h. When it is reached, the controller finishes with reason *floor* (4.1
rule 3), and Wi-Fi is restored as in 6.6. The default is owner question 14.2.

**Tray:** profiles are ordered by the order in which they were ticked, with a
**Clear** item. **Test hotspot** works only with the lid open.

**Setup warnings:**
- the profile is set to auto-connect
- Nightcrew lacks execute access
- the NLM cost shows the network as unmetered

**README:**
- join the hotspot once with the lid open
- on iPhone, USB tethering is the reliable path
- on Android, turn off hotspot auto-off and Data Saver
- mark the hotspot as metered

## 9. Crash safety, restore and lifecycle

### 9.1 Files

All files live in `%LOCALAPPDATA%\Nightcrew`:

- `settings.json`
- `state.json` (lid-closed time, spent, Hold awake, pre-suspend state)
- `restore.json`
- `instance.json` (pid, CreateTime, image path, boot ID, last resume time as
  unbiased time)
- `logs\`

Writes use a temporary file, a flush, then
`MoveFileEx(REPLACE_EXISTING | WRITE_THROUGH)`.

### 9.2 Restore record

**Schema v1:**
- `version`, `bootId`, `sessionId`, `scheme`
- `settings[]`: `{subgroup, setting, source: AC|DC, ownerValue, lastWritten}`
- `finishTemp?`: `{source: AC|DC, ownerValue, lastWritten}`
- `wifi`: `{original | "unknown", profileOrder[], hotspotUsed, switched}`
- `mute[]`: `{endpointId, priorMuted, mutedByUs}`
- `frozen[]` (diagnostic only)
- `startedUtc`

**Write rules:**
- `lastWritten` is journaled before every write, including the chain's 60 s
  value.
- **Arming while an episode record exists** (one with `settings[]` entries)
  reuses `ownerValue` and the scheme. Nothing is re-captured except through
  drift adoption (6.8).
- **The chain's 60 s value.** Chain step 2 creates a record holding only
  `finishTemp`. Restoring it writes `ownerValue` only if the current value
  equals `lastWritten`, then deletes the record.
- **Arming while a `finishTemp` record exists** restores it first. If that
  restore fails, the arm uses `finishTemp.ownerValue` as that setting's owner
  value.
- **Deletion.** The record is deleted once each entry is restored and
  verified, or skipped because drift adopted a new value.

**Failures:**
- **A restore write or read-back fails:** keep the record, retry every tick and
  every watchdog run, and show "Restore pending".
- **The record is unreadable:** rename it to `restore.bad-<ts>.json` and start
  no episode until the owner resolves it. The tray shows **Restore record
  unreadable ▸ Open folder / Discard**; `--discard-record` does the same.

### 9.3 RestoreAll

RestoreAll is idempotent, lock-guarded and callable from any thread. It runs
in this order:

1. ThawAll.
2. Mute entries back to their prior state.
3. Wi-Fi restore: deferred to resume on floor and cap paths; work-done
   finishes wait at most 10 s (6.6).
4. Power values: write each owner value only if the current value equals
   `lastWritten`, then read it back.
5. Release the request.
6. Delete the record once everything is complete.

### 9.4 Startup

1. Take the single-instance mutex.
2. Start with the lid Unknown until the callback's initial value arrives. Take
   the lid-close time to be the startup time. Restore Hold awake from
   `state.json`.
3. If a record exists, check it:
   - **Boot ID matches and the values are still Nightcrew's:** adopt the
     record, so the episode continues. Start the controller in Armed, take the
     ownership lock (9.5) and the request (6.4), and treat Live as true until
     G has passed since startup. A fresh process has no busy history yet. Then
     evaluate:
     - a target of Idle runs RestoreAll
     - a target of Working runs the Armed → Working actions

     The watchdog's relaunch path depends on this.
   - **Otherwise:** RestoreAll.

### 9.5 One owner per machine

Power policy is machine-wide.

- **Ownership lock.** An episode needs `%ProgramData%\Nightcrew\owner.json`,
  held with an exclusive lock for the episode's duration. The folder is
  created with a DACL that grants BUILTIN\Users modify rights.
  - The file also stores `{accountSid, sessionId, startedUtc}`.
  - Only the owning account's RestoreAll clears it, after that account's
    record is deleted.
  - Another account doesn't arm while `owner.json` names a different account,
    even if the file lock is free (for example after a crash).
- **Active session only.** An instance arms only while its session is the
  active console session, or is itself remote-connected. Otherwise it behaves
  as Paused.
- **Contention.** A second instance that finds the lock held, or another
  account named in the file, doesn't arm. It shows "Lid control in use by
  another account".

### 9.6 Session events

| Event | Action |
|---|---|
| SessionLock (including Nightcrew's own lock) | nothing |
| SessionUnlock | re-assert the request, re-evaluate |
| SessionLogoff | RestoreAll |
| ConsoleDisconnect, another session takes the console, and own session is not remote-connected within 10 s | RestoreAll, then behave as Paused |
| RemoteConnect to own session | Shut is false while connected, so the state stays Armed: no freeze, no mute, no lock (lock-on-return included); keep the arm |
| ConsoleConnect back to own session | re-evaluate |

### 9.7 Watchdog

**Task.** A per-user task, `\Nightcrew\Watchdog`, registered through COM
`RegisterTask` XML. It needs no admin rights and doesn't spawn schtasks.exe.

- **Author and Description** name Nightcrew and `nightcrew --uninstall`.
- **Triggers:** logon, and every 5 minutes.
- **Settings:**
  - interactive token
  - `DisallowStartIfOnBatteries=false`, `StopIfGoingOnBatteries=false`
  - `ExecutionTimeLimit=PT2M`
  - `MultipleInstancesPolicy=IgnoreNew`
  - `StartWhenAvailable`
  - hidden

**Action:** `nightcrew.exe --watchdog`, which runs this logic:

1. **Serialize.** Take the `Local\Nightcrew.Record` mutex before touching the
   record. The instance takes the same mutex.
2. **Instance alive (another process holds the single-instance mutex from
   9.4):**
   1. Ping it on a named event; its UI thread must answer within 30 s.
   2. Declare it hung only after two consecutive failed pings at least one
      trigger apart, and never within 5 minutes of a resume, measured on the
      unbiased clock from the last wake time.
   3. Before terminating, confirm that the PID's CreateTime and image path
      match `instance.json`.
3. **Instance dead or terminated:**
   - **A record exists:** relaunch `nightcrew.exe` detached, with breakaway
     from the task's job. The new instance adopts the record (9.4), so work
     that is still live keeps its hold.
   - **No relaunch:** after 3 relaunches in an hour, or if a "quit by user"
     marker exists, RestoreAll instead.
4. **No instance, no record:** relaunch if Start at login is on and there's no
   "quit by user" marker, at most 3 times an hour.

**Quit marker.** Quit and `--uninstall` write the "quit by user" marker; a
manual start clears it.

**Logon-screen gap.** If no one is signed in, power values stay changed until
the next sign-in; the bounded DC value (6.3) limits this. S15 checks whether a
standard user can register an S4U task to close the gap.

### 9.8 Command-line verbs

| Verb | Does |
|---|---|
| `--dry-run` | evaluate and log everything, change nothing |
| `--watchdog` | 9.7 |
| `--exit` | the running instance thaws, restores and quits (for upgrades) |
| `--uninstall` | RestoreAll, remove the Run key and task, write the quit marker, optionally delete the data folder |
| `--diagnose` | current signals, the startup checks from 6.2, and a Kernel-Power 506/507/566/172/42 summary of recent lid-closed episodes |
| `--discard-record` | 9.2 |

**Autostart** uses the HKCU Run key pointing at the current exe path, which
needs no admin rights. If the path changes, the Run key and the task are
registered again.

## 10. UI

The tray menu is built in code and shows only what the owner acts on.

**Status line**, one of:
- Idle
- Live — 2 sessions, 1 job
- Working, lid closed — 3h12 left, 64%, when done: sleeps / stays on /
  hibernates
- Finishing
- Paused

Warnings appear when they apply:
- Lid control unavailable (policy)
- Lid control in use by another account
- Battery hold limited
- Hibernate off: floor and cap use sleep
- Restore pending
- Restore record unreadable ▸
- Interrupted by restart at hh:mm (shown once)
- Lid control unavailable (drift: <setting>)
- Power policy may block the hold (SYSTEMREQUIRED / override / AoAc)
- Hotspot setup: auto-connect / no execute access / not metered

**Menu:**
- **Why awake ▸** each holder, with Ignore; **Ignored ▸** lists them so they
  can be undone
- **Hold awake**
- **Pause Nightcrew**
- **Freeze apps when the lid closes** (on), **Would freeze now ▸**, **Never
  freeze ▸** (category checkboxes)
- **Lock when the lid closes** (on)
- **Battery floor ▸** 10 / 15 / 20 / 30% / off
- **Lid-closed limit ▸** 2 / 4 / 8 / 12 h
- **Hotspot ▸** profiles in tick order, Clear, Test hotspot (lid open only),
  Usage limit ▸
- **Start at login** (on)
- **Open log**
- **Quit:** restores first

**Icon states:** idle, live, working. **Balloon notifications** are
best-effort; nothing depends on them.

## 11. Architecture

### 11.1 Platform layer

Thin P/Invoke wrappers, each behind an interface:

- **Power:** PowerPolicy, PowerRequest, PowerEvents (described below),
  ISuspend (hibernate, monitor-off)
- **Processes and windows:** ProcessSnapshot (NtQSI 252/5), ProcessHandles,
  GpuCounters (PDH), Freezer, WindowEnumerator
- **Audio:** AudioSessions, AudioEndpoints (mute, default-device
  notifications)
- **Network:** Wlan, ConnectivityProbe, Nlm
- **System and session:** TaskScheduler, Autostart, Displays
  (QueryDisplayConfig), ISessionControl (lock, lock state, session events),
  IEventLogReader
- **Infrastructure:** IClock / ITimerFactory, ITranscriptSource,
  IInstanceChannel (mutex, ping, `--exit`), IOwnershipLock

PowerEvents is one hidden, never-shown WS_POPUP top-level window on the UI
thread. It is not message-only, because message-only windows don't receive
broadcasts. It handles:

- lid, AC/DC and battery through `PowerSettingRegisterNotification`
  callbacks, which deliver initial values
- session display status and user presence through
  `RegisterPowerSettingNotification` on the window
- `RegisterSuspendResumeNotification`
- end-session messages
- `WM_DISPLAYCHANGE`

### 11.2 Core logic

Pure classes, unit-tested with fakes:

- `AgentClassifier`
- `ProcessTracker`
- `ActivityEvaluator`
- `FreezePlanner`
- `Controller` (section 4)
- `RestoreJournal`
- `HotspotStateMachine`
- `WatchdogDecision`
- `Settings`

### 11.3 Threads

- **UI (STA) thread:** the message pump, the tray and quick state changes.
- **One worker thread:** polling, enumeration and freezing.
- **Async I/O:** WLAN connects and probes run asynchronously, so the worker
  never blocks.
- **ThawAll** is called directly from the callback threads (7.1).

### 11.4 Settings

`settings.json` is read and written with DataContractJsonSerializer and holds:

- `agents[]`: `{kind: claude|codex|generic, imagePathGlob (supports %ENV% and **), argsInclude[], argsExclude[]}`
- `ignore[]`: `{kind: session|job|headless, imagePath, argsContains, transcriptDir}`
- `neverFreeze[]` (image paths)
- `categories{}` (all true by default)
- `virtualDisplayAllow[]`
- `hotspots[]` (in order)
- `hotspotLimit`
- `proxyPort` (default none)
- `computerUseTools[]`
- `installerPatterns[]`
- the floor, cap, lock, freeze and start-at-login settings

### 11.5 Defaults

All values are provisional.

| Value | Default | Tuned by |
|---|---|---|
| G (grace) | 5 min | S7 (longest silent turn) |
| N (sample window) | 30 s | S7 |
| F (cycle floor) | from idle baselines | S7 |
| Job busy | 15% of one core over 60 s, or GPU ≥ 10% | S7, S10 |
| D (user-back debounce) | 60 s | S4 |
| Exited-process cache | 10 min | S13 |
| Poll cadence | 1–2 s Idle and Armed / 15 s Working | S7, battery check in S2 |
| Chain step delays | 2 min, then 3 min | S5 |
| Cap / floor | 8 h / 20% | owner |

### 11.6 Logs and fixtures

**One JSONL tick schema** (`ts, state, lid, display, presence, source,
battery, sessions[] {pid, seq, transcriptAgeMs, cpu, toolChildren[]}, jobs[]
{pid, cpu, gpu}, liveRule, decision`) is shared by:

- the dry-run log
- the S7/S10 captures
- the unit-test fixtures

**One snapshot schema** covers window and process-tree dumps.

**Log files:** daily files in `logs\`, kept for 7 days or up to 50 MB. Outside
dry-run, only state changes are logged.

## 12. Build and distribution

**Project:**
- SDK-style, `net48`, `UseWindowsForms`
- `LangVersion 14.0`, `Nullable` and `ImplicitUsings` on
- `GenerateSupportedRuntime=false`, `DebugType=embedded`
- PolySharp (`PrivateAssets=all`) as the only package
- `global.json` pins SDK 10.0.4xx and the test runner
- `app.manifest` with the Windows 10 supportedOS entry and PerMonitorV2
  (manifest-only)

**C# rules** (recorded in CONTRIBUTING):
- none of: default interface methods, static abstract members, covariant
  returns, ref fields, inline arrays, generic attributes
- no array range slicing
- none of: Span, ValueTask, IAsyncEnumerable, System.Text.Json
- no image `.resx`; icons are raw `EmbeddedResource`

**Gate:** CI fails if `bin/Release` contains anything except `nightcrew.exe`.

**Tests:** xUnit v3 on net48 using Microsoft Testing Platform, against the exe
project through InternalsVisibleTo.

**CI** (`ci.yml`, runs on a pinned `windows-2025`): checkout v7, then
setup-dotnet v6 from `global.json`, then build, test and the gate. Actions are
pinned by SHA.

**Release** (`release.yml`, on a `v*` tag):
1. Build, test, compute the SHA256.
2. `actions/attest@v4`.
3. Create a draft with `gh release create --draft`.
4. Upload `nightcrew.exe` and `nightcrew.exe.sha256`, then publish.

Permissions: `contents`, `id-token`, `attestations` and `artifact-metadata`
write. Immutable releases are on.

**v1 ships through GitHub Releases only, unsigned.** The README covers
SmartScreen ("More info, then Run anyway") and Smart App Control, which blocks
the exe outright. Settings live in `%LOCALAPPDATA%\Nightcrew`; the README
recommends a stable install path.

## 13. Testing

### 13.1 Unit tests

**Controller:** every rule in 4.1–4.5, including:
- presence still present at lid close, then clearing
- a display burst when AC is unplugged with the lid closed
- a bump in a bag (display on, then off again)
- live starting with the lid closed and no resume
- live ending in Armed with the lid closed
- startup with the lid closed and something live
- Idle, then a lid-closed resume with nothing live: no action
- WakeToRun, then the overnight runner
- every Finishing exit, at every chain step
- cap, then hibernate, then a wake, then live: stays spent
- floor and cap during a user-back sub-state
- live ending within D of a bump in a bag: Finishing, with the action carried
  out once Away returns
- a bump in a bag, then quiet, on DC below the floor: hibernates
- a laptop left lid-closed for longer than the cap before anything is live:
  the cap timer starts only when the request is held
- an adopted record after a crash relaunch, with the lid Closed and work live:
  stays Working, no premature finish
- Finishing → Armed: owner values captured afresh, or taken from
  `finishTemp.ownerValue` if its restore failed
- Pause during a sleep chain: the chain is cancelled and never suspends
- hotspot usage limit reached: finishes with reason floor
- drift limit reached: degraded Armed
- Pause
- degraded gate
- the hibernate-unavailable fallback
- RDP connected with the lid closed and no input for longer than D: stays
  Armed, nothing frozen or locked
- RDP into the owner's own session (ConsoleDisconnect, then RemoteConnect):
  the arm is kept
- lock-on-return mode: work done with Do nothing leaves the session locked;
  lid opened during a chain finds it locked
- account A crashes mid-episode and account B takes the console while A's
  record exists: B does not arm

**Other components:**
- **ActivityEvaluator,** against recorded traces: approval wait with an MCP
  browser open; claude-mem only; a detached tagged job; an unmapped session;
  generic agents.
- **FreezePlanner:**
  - WebView2 hosts
  - a user's node app next to MCP node servers
  - a detached tagged python job with a window
  - partial-family refusal
- **Freezer concurrency:** ThawAll injected at every step of a multi-family
  freeze, including re-snapshots; asserts zero open handles afterwards.
- **RestoreJournal:**
  - arming during chain step 2
  - a failed restore, then retry
  - drift during Finishing
  - a partly restored record at startup
  - an unreadable record
  - only-if-still-ours
- **Environment reads:** a block with `ANTHROPIC_API_KEY` and `GITHUB_TOKEN`
  leaves nothing in the cache or the logs.
- **WatchdogDecision:**
  - right after a 2 h sleep
  - DAM-suspended
  - genuinely hung
  - dead with a record
  - a relaunch loop
  - PID identity mismatch
- **Session events:** Nightcrew's own lock, and a lock/unlock cycle, cause no
  restore.
- **HotspotStateMachine:**
  - started while already connected
  - original network unknown
  - a USB tether present
  - radio off
  - ACM failures
  - flapping
  - the usage limit
- **Task XML generation.**

### 13.2 Hardware spikes

Every spike runs `--dry-run` first. Claude runs only dry-run and read-only
checks. Spikes that change power or Wi-Fi settings, register tasks, or freeze
processes are run by the owner. The first build is a throwaway spike harness,
not the app. It has:

- a 1 Hz heartbeat file
- a CPU-bound child that logs iterations/s and its core EfficiencyClass
- logs of lid, display, presence, AC/DC, battery, suspend and
  SystemExecutionState
- an export of the Kernel-Power events

| # | Who | Question | Blocks | If it fails |
|---|---|---|---|---|
| S1 | owner | Non-elevated writes and read-back of LIDACTION and STANDBYIDLE; does `PowerSetActiveScheme` reset the overlay? | PowerPolicy | drop re-activation if the writes take effect without it; otherwise the gate disables lid mode |
| S2 | owner | Lid closed with Do nothing and the request held, unlocked and locked. Runs: AC 30 min; DC 3 h with the bounded value; DC with Never and no request; DC with the owner's 60 min plus ES_SYSTEM_REQUIRED pulses. Also measures panel-lit drain and the elevated `powercfg /requests` snapshot. | Controller hold, 6.3, 6.5 | DC: cap at the measured hold and show "Battery hold limited"; locked: lock-on-return |
| S3 | owner | Control: DC with sleep-after 5 min and the request held | confirms S2 | — |
| S4 | owner | Lid events with Do nothing (callback and HWND); fresh initial value after re-registration on resume; presence at close right after typing; AC unplug burst; RDP into the laptop; QueryDisplayConfig paths with the lid closed (no monitor, docked, virtual display) | Controller inputs, 6.7 | poll GetSystemPowerStatus and display status; lid Unknown until a real reading |
| S5 | owner | Chain steps 1–3, with and without the Claude app's request; SetSuspendState return codes; hibernate when disabled; does WakeToRun wake from hibernate? | 6.6 | reorder the chain; README note on wake timers |
| S6 | owner | State-change freeze on Notepad and a Chromium app: kill the harness (auto-thaw), sleep and hibernate round trips, nesting with PsSuspend, access matrix, freezing a terminal UI while an agent prints | Freezer | ship with freezing off |
| S7 | Claude (read-only) | 10-minute baselines: Claude Code idle, busy, approval wait (incl. MCP browser), usage-limit retry wait, computer-use run; Codex idle and busy, and its file signal; Codex on DC in Screen Off (owner closes the lid); job baselines (GPU training, CPU script, idle dev server, watch build, Playwright Chrome); longest silent turn. Also: in 10 minutes of class-252 snapshots with process churn, does every live parent have a lower SequenceNumber than its child, with no reuse? Does class 88 give full paths to a standard user? | ActivityEvaluator, ProcessTracker, 11.5 | CPU fallback for Codex; README note for computer use; validate parent links by class-5 CreateTime; never-open by name only |
| S8 | owner (VM) | Shutdown and sign-out with apps frozen; an agent-run installer using Restart Manager on a file held by frozen Notepad | 7.4, 7.5 | thaw on every installer |
| S9 | owner | Hotspot: Location-off API matrix; upstream-loss timing; iPhone and Android with the lid closed; USB tether and Ethernet; started while already connected; data counters | section 8 | README guidance |
| S10 | Claude (read-only) | claude-mem only, including the minutes after a user session ends | 5.5 | built-in ignores |
| S11 | owner | 8 h on AC while live: Lenovo Vantage drift | 6.8 | drift limit disables lid mode |
| S12 | owner | Watchdog task on DC, lid closed, Energy Saver on, about 15% battery: fires every 5 min; kill-then-relaunch | 9.7 | document the bound |
| S13 | Claude | A Start-Process grandchild launched through a PowerShell that exits immediately: does CLAUDE_PID tagging work, and which launch methods escape? | 5.5 | creation-time and working-directory heuristic |
| S14 | owner | A wake timer firing with the lid closed, plus an Austerity blip: Nightcrew does nothing | 4.3 | — |
| S15 | Claude (read-only) | Can a standard user register an S4U or boot-trigger task? | 9.7 | document the logon-screen gap |

### 13.3 Owner checklist before v0.1.0

1. Idle lid close follows the Windows setting.
2. A mid-task lid close keeps the work running; when it's done, the lid action
   follows. On AC with Do nothing and Never, confirm that staying on is
   intended.
3. Time from the last busy-signal sighting in the log, to Finishing, to
   Sleep, is within criterion 3.
4. Opening the lid thaws everything in under 1 s.
5. Floor and cap work, tested with low thresholds.
6. Killing Nightcrew mid-task: apps thaw, and the watchdog relaunches it
   within 5 minutes.
7. `--uninstall` leaves nothing behind.

### 13.4 Pre-release check

On a clean Windows 11 VM: download from GitHub in Edge with Smart App Control
off and then in evaluation mode, run one dry-run and one real freeze/restore
cycle, and record every SmartScreen, Smart App Control or Defender prompt.
Upload the exe to VirusTotal.

## 14. Risks and open questions

**Owner questions:**
1. **Usage-limit retry waits** look done, so the laptop sleeps and the retry
   never fires. Accept this for v1, or have S7 capture a real retry wait so v1
   can hold live until the stated reset time?
2. **Hotspot usage limit default:** off, or a value?
3. **Lid action.** Once Nightcrew is in use, consider setting the Windows lid
   action back to Sleep. With Do nothing, a finished lid-closed session on AC
   stays on (criterion 3).

**Risks:**
4. **DC hold (S2).** If the bounded value doesn't lift the 5-minute DC limit,
   battery sessions are capped ("Battery hold limited").
5. **Locked-session hold (S2).** Falls back to lock-on-return.
6. **Finishing on AC (S5).** Other apps' requests block idle entry; hibernate
   is the backstop.
7. **Codex busy signal (S7).** Unmeasured; CPU against a clean baseline is the
   fallback.
8. **Remote approval.** The owner uses Remote Control. An optional "approval
   grace" is a v2 candidate.
9. **No heat cut-off.** Thermal zones read 0 without admin on this laptop. The
   README warns about bags.
10. **Lenovo Vantage drift (S11).**
11. **Build-gated and undocumented APIs.** NtQSI class 252 is documented for
    26100.4770+. State-change objects and PEB offsets are undocumented. All
    are feature-detected and fail soft.
12. **Logon-screen gap after a crash and reboot (9.7, S15).**
13. **Name.** "Nightcrew" is clean in the agent and lid space. Run a
    USPTO/EUIPO Class 9 check before any Store listing.

## 15. Prior art and credit

Inspired by [Insomnia](https://github.com/krishhgg/Insomnia) for macOS by Krish
Garg (MIT). Nightcrew shares no code with it. Insomnia's lessons are adopted
here:

- freeze-all needs protected categories
- the recovery journal comes before any change
- an explicit action is needed at the end (#94)
- external-display clamshell mode (#97)

Other Windows tools:

| Tool | What it does |
|---|---|
| LidGuard | hooks; sleeps after sessions |
| agents-dont-sleep | hooks; Do nothing plus the battery idle timeout; relies on idle sleep at the end |
| ajar | hooks plus a process scan |
| Insomnia-for-Windows | timer; NtSuspendProcess; .NET 10 |
| LidKeeper | process-presence scheduled task |
| PowerToys Awake | no lid handling |

Nightcrew's differences:
- automatic detection without hooks
- freezing that can't get stuck
- the lid action carried out at the end
- background-job tagging
- one .exe with no runtime

## 16. Out of scope for v1

- plugins and hooks
- WinRT toasts
- an approval-grace mode
- a heat cut-off
- HighQoS tagging for agents on DC (S2 measures the cost)
- the Store/MSIX build
- the Scoop bucket and winget
- SignPath signing
- Windows 10 freezing
- freezing packaged/UWP apps
- a settings window
- remote control from a phone
- simultaneous episodes from two accounts
