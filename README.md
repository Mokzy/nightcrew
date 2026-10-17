<p align="center">
  <img src="docs/assets/banner.png" alt="Nightcrew. Awake while your agents work. Asleep when they're done." width="100%">
</p>

<p align="center">
  <img src="https://img.shields.io/badge/Windows-10%20%C2%B7%2011-F0D7FF?style=flat-square&labelColor=26282B" alt="Windows 10 and 11">
  <img src="https://img.shields.io/badge/license-MIT-F0D7FF?style=flat-square&labelColor=26282B" alt="MIT licence">
  <img src="https://img.shields.io/badge/status-experimental-F5C27A?style=flat-square&labelColor=26282B" alt="Status: experimental">
</p>

Close a Windows laptop and it goes to sleep, and whatever Claude Code or Codex
was doing stops with it. Nightcrew keeps the laptop working with the lid shut
for as long as an agent is busy, then lets it sleep the way it normally
would.

https://github.com/user-attachments/assets/200be945-4714-42fb-981e-1815e68ddf0e

- **Automatic.** It notices when an agent is working. No hooks, no plugin,
  nothing to configure.
- **Only when needed.** With no agent working, closing the lid does what it
  always did.
- **Leaves your settings alone.** It changes power settings only while an
  agent works and puts them back afterwards, even after a crash.
- **Agents get the machine.** While you're away, apps they don't need go into
  Efficiency mode, the way Task Manager does it.
- **Light.** One 1.7 MB exe in the tray, using well under 1% of a CPU core.

## Install

1. Download `nightcrew.exe` from [Releases](../../releases/latest).
2. Put it somewhere it can stay, such as `%LOCALAPPDATA%\Programs\Nightcrew`,
   and run it. A lantern appears in the tray, the panel opens once so you can
   find it, and Nightcrew starts with Windows from then on.

The exe isn't code-signed, so the first run shows "Windows protected your PC".
Click **More info**, then **Run anyway**. With Smart App Control on, Windows
may block it outright. To check the download, compare its hash with the
`nightcrew.exe.sha256` file on the release, or verify its GitHub build
attestation:

```powershell
Get-FileHash .\nightcrew.exe
gh attestation verify .\nightcrew.exe -R leikauskas/nightcrew
```

## How it works

<img src="docs/assets/night.png" alt="One night in four steps: an agent starts, you close the lid, the work ends, it sleeps" width="100%">

1. **An agent starts working.** Every 10 seconds Nightcrew looks for a Claude
   Code or Codex session that's growing, a build or script one of them
   started, or heavy GPU use. For Claude Code it also reads the status in
   `~\.claude\sessions\<pid>.json` (busy, idle or waiting), so a long, quiet
   command still counts. That's the only file it opens in `~\.claude`:
   session transcripts are watched for size only, and the login `.key` files
   are never touched. Nightcrew has no network code. The lantern lights up.
2. **You close the lid.** The laptop keeps running and locks itself, unless
   your lid action is Do nothing or **Lock when lid closes** is off. When the
   work started, Nightcrew saved your power settings, then set the lid to Do
   nothing and the sleep timer to Never. On battery the timer is your
   lid-closed limit plus an hour instead, or Never if the limit is off.
3. **Other apps ease off.** Five minutes later, apps the agents don't need go
   into Efficiency mode. Agents, what they're connected to, terminals,
   editors, browsers with a Claude or Codex connector and games are left
   alone. Everything's back to normal the moment you open the lid.
4. **The work ends.** Five minutes after the last sign of work, your settings
   go back.
5. **It sleeps.** Once nobody has touched the laptop for 5 minutes, it follows
   your lid action: sleep, hibernate or nothing. If Windows hasn't gone to
   sleep 5 minutes later, Nightcrew hibernates it.

**Safety limits.** With the lid closed, Nightcrew stops early and hibernates at
20% battery or after 8 untouched hours, even mid-task. You can change both.

**Your settings always come back:** when the work ends, when you pause or quit,
when you sign out, and on the next start after a crash. If Nightcrew is killed,
a watchdog task restores them within 5 minutes. If your organization locks the
power settings, Nightcrew stays idle and tells you so.

## The panel

<img src="docs/assets/panel-working.png" alt="The Nightcrew panel while Claude Code and Codex are working" width="352" align="right">

Click the lantern to see what's keeping the laptop awake, your battery, and
what happens when the work is done.

- **Hold awake** keeps the laptop awake until you turn it off, for anything
  Nightcrew can't detect on its own.
- **Pause** hands the lid back to Windows until you resume.
- **Settings**, or right-click the lantern: battery floor, lid-closed limit,
  lock on lid close, stay awake for approvals, start at login, Efficiency mode
  while away, and the tray mark.
- **Stay awake for approvals**: with the lid closed, Claude Code waiting for
  your approval or answer counts as work, so you can answer from your phone.
  Claude Code only. With the lid open, Windows' own sleep timer applies.
- Starting `nightcrew.exe` again opens the panel.
- If an app keeps misbehaving after you've been away, add it under **Keep at
  full speed** in Settings.

<br clear="right">

## Good to know

- A game or video that keeps the GPU busy counts as work.

<details>
<summary><b>Other agents and advanced settings</b></summary>

Nightcrew knows Claude Code and Codex. Any other agent is covered when it uses
the GPU, or with **Hold awake**. To teach Nightcrew about another agent, quit
Nightcrew, edit `%LOCALAPPDATA%\Nightcrew\settings.json`, and start it again:

- add its process name to `AgentProcessNames`, so programs it starts count as
  agent tools
- add it to `SelfCpuAgentNames` too if the agent works in its own process
- add the folder where it writes `*.jsonl` session files to `TranscriptRoots`

A key you leave out keeps its default. The log, `nightcrew.log`, is in the
same folder. To see what Nightcrew would do without letting it change
anything, quit Nightcrew, run `.\nightcrew.exe --dry-run` in its folder and
read the log.

| Key | Default | Meaning |
|---|---|---|
| `GraceMinutes` | 5 | How long work stays live after the last sign of work |
| `BatteryFloorPercent` | 20 | Hibernate at this charge with the lid closed on battery; 0 = off |
| `LidClosedCapHours` | 8 | Hibernate after this long with the lid closed and no input. While working, the battery sleep timer is this plus an hour. 0 = off (timer Never) |
| `AwayMinutes` | 5 | Minutes without input before the lid action runs, before lid-closed time counts toward the limit, before the lock when work starts with the lid closed, and before apps go into Efficiency mode |
| `LockOnLidClose` | true | Lock the PC when the lid closes |
| `StartAtLogin` | true | Start Nightcrew when you sign in |
| `DryRun` | false | Log what Nightcrew would do without doing it |
| `TranscriptRoots` | Claude Code and Codex folders | Where session files are watched |
| `IgnorePathContains` | `claude-mem-observer` | Session files whose path contains this are ignored |
| `AgentProcessNames` | `claude`, `codex` | Agent processes; programs they start count as agent tools |
| `SelfCpuAgentNames` | `codex` | Agents whose own CPU use counts |
| `SelfCpuPercent` | 2 | CPU threshold for those agents, % of one core |
| `ToolCpuPercent` | 10 | CPU threshold for agent tools, % of one core |
| `GpuPercent` | 30 | GPU engine threshold, % |
| `SampleSeconds` | 10 | Seconds between checks |
| `Mark` | `lantern` | The tray mark: `lantern` or `moon` |
| `EfficiencyWhileAway` | true | Put apps the agents don't need into Efficiency mode while you're away |
| `LeaveAlone` | none | Process names never put into Efficiency mode (Settings > Keep at full speed) |
| `LeaveAloneLabels` | none | The names Settings shows for `LeaveAlone` entries |
| `ConnectorHostNames` | `chrome-native-host`, `extension-host` | Browser connector helpers; their browser is left alone |
| `StayAwakeForApprovals` | true | With the lid closed, a Claude Code session waiting for you counts as work |
| `ClaudeSessionsDir` | `%USERPROFILE%\.claude\sessions` | Where Claude Code keeps its session status files |

</details>

## Update

Quit Nightcrew: right-click the lantern, then **Quit Nightcrew**. That puts
your settings back. Replace the exe with the new one, then start it.

From a script, run `Start-Process .\nightcrew.exe -ArgumentList '--exit' -Wait`
in its folder. PowerShell doesn't wait for `.\nightcrew.exe --exit` on its own.

## Uninstall

Quit Nightcrew (right-click the lantern, then **Quit Nightcrew**), then run
`.\nightcrew.exe --uninstall` in its folder. That puts your settings back,
takes any app out of Efficiency mode, and removes the startup entry and the
watchdog task. Then delete the exe, and `%LOCALAPPDATA%\Nightcrew` if you want
the settings and log gone too. Don't only delete the exe: the watchdog task
would stay behind.

## Credits

- The cream ink, serif headline and lavender come from Wispr Flow's design
  language.
- Type is [EB Garamond](https://github.com/octaviopardo/EBGaramond12) and
  [Figtree](https://github.com/erikdkennedy/figtree), both under the SIL Open
  Font License. See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## License

[MIT](LICENSE)
