# Nightcrew

Close your laptop and walk away while Claude Code or Codex keeps working.

- While an agent is working, Nightcrew keeps a Windows laptop awake with the
  lid closed.
- When the work ends, it puts your power settings back and does what your lid
  action says: sleep, hibernate or nothing.
- It lives in the tray. There's nothing to set up.

## Install

1. Download `nightcrew.exe` from [Releases](../../releases/latest).
2. Move it to a folder where it can stay, for example
   `%LOCALAPPDATA%\Programs\Nightcrew`. Start at login and the watchdog task
   remember this path.
3. Run it. Its icon appears in the tray.

Nightcrew needs Windows 10 (1903 or later) or Windows 11. It uses
.NET Framework 4.8, which is already part of Windows.

Nightcrew isn't code-signed. The first time you run it, SmartScreen shows
"Windows protected your PC". Click **More info**, then **Run anyway**. If
Smart App Control is on, it blocks unsigned apps it doesn't already trust and
offers no Run anyway, so Nightcrew may not start.

Each release includes `nightcrew.exe.sha256` and a GitHub build attestation.
To check your download, run `Get-FileHash .\nightcrew.exe` and compare the
hash with the one in that file.

## How it decides an agent is busy

Every 10 seconds Nightcrew looks for any of these:

- **Transcripts.** A Claude Code or Codex session file (`*.jsonl` under
  `%USERPROFILE%\.claude\projects` or `%USERPROFILE%\.codex\sessions`) grew,
  or a new one appeared.
- **Agent tool processes.** A program started by `claude` or `codex`, such as
  a build, a test run or a script, used at least 10% of one CPU core. Codex
  itself counts at 2%.
- **GPU.** Any process kept a GPU engine at least 30% busy. A game or a video
  counts too.
- **Hold awake.** You turned it on in the tray menu. It counts as busy until
  you turn it off.

Work counts as live until 5 minutes after the last busy check. The status
line at the top of the tray menu shows what Nightcrew is doing.

## Lid closed, and when work ends

While work is live, Nightcrew:

- sets the lid-close action to Do nothing and "Sleep after" to Never, on mains
  power and on battery
- asks Windows to keep the system running
- locks the PC when you close the lid during work, or when work starts while
  the lid is already closed and there has been no keyboard or mouse input for
  5 minutes (turn off **Lock when lid closes** if you don't want that)

It saves your original values in `%LOCALAPPDATA%\Nightcrew\restore.json`
before changing anything. If you change one of these settings yourself while
Nightcrew is working, your change is kept.

When the work ends, Nightcrew puts your settings back. If the lid is open,
that's all. If the lid is closed, it waits until there has been no keyboard or
mouse input for 5 minutes, then follows your Windows lid action for the
current power source:

- **Sleep.** It turns the screen off and lets Windows sleep. If the laptop is
  still awake 5 minutes later, it hibernates.
- **Hibernate** or **Shut down.** It hibernates.
- **Do nothing.** The laptop stays on, as your setting says.

If work starts again first, Nightcrew keeps the laptop awake again. If you open
the lid first, it does nothing.

Your lid action is in Settings > System > Power & battery > Lid & power button
controls, or in Control Panel > Power Options > Choose what closing the lid
does.

If Nightcrew crashes or is killed, a scheduled task called Nightcrew Watchdog
puts your settings back within 5 minutes.

## Battery floor and lid-closed limit

With the lid closed, Nightcrew stops early and hibernates, even if an agent is
still working, when:

- it's on battery and the charge falls to the battery floor (default 20%), or
- the lid has been closed for the lid-closed limit (default 8 hours).

It puts your settings back first. After that it won't keep the laptop awake
again until you open the lid. If hibernation is turned off on your PC
(`powercfg /a` lists what's available), Windows' own sleep timer takes over
instead.

The battery floor applies whenever the lid is closed on battery, even while
you work on a docked laptop. The lid-closed limit counts only time without
keyboard or mouse input, which starts 5 minutes after your last input, so
time spent working on a docked laptop with its lid closed doesn't count
toward it.

## Limits

- Don't switch Windows power plans while the status shows Live. Nightcrew
  puts your settings back into the plan that is active at that moment, so
  the plan you switched away from keeps Do nothing and Never.
- A game or video that keeps a GPU engine at least 30% busy counts as busy,
  so the laptop stays awake while it runs.

## Tray menu

- **Status line.** What Nightcrew is doing right now.
- **Hold awake.** Treat the PC as busy until you turn this off.
- **Pause.** Put your settings back and stop keeping the laptop awake.
- **Battery floor.** 10, 15, 20 or 30%, or Off.
- **Lid-closed limit.** 2, 4, 8 or 12 hours, or Off.
- **Lock when lid closes.**
- **Start at login.**
- **Open log.**
- **Quit.** Puts your settings back, then exits.

## Settings

Settings live in `%LOCALAPPDATA%\Nightcrew\settings.json`, created on first
run. Quit Nightcrew before editing the file, then start it again. A key you
leave out keeps its default. The log is `nightcrew.log` in the same folder.

| Key | Default | Meaning |
|---|---|---|
| `GraceMinutes` | 5 | How long work stays live after the last busy check |
| `BatteryFloorPercent` | 20 | Hibernate at this charge with the lid closed and on battery; 0 = off |
| `LidClosedCapHours` | 8 | Hibernate after this long with the lid closed and no input; 0 = off |
| `AwayMinutes` | 5 | Minutes without input before the lid action runs, before lid-closed time counts toward the limit, and before the lock when work starts with the lid closed |
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

To watch what Nightcrew would do without letting it change anything, run
`nightcrew --dry-run` and read the log.

If your organization's policy locks the power settings, Nightcrew can't change
them and stays idle.

## Tips

**Phone hotspot.** If the laptop leaves your Wi-Fi, for example in a bag,
the agent loses its connection. Open Settings > Network & internet > Wi-Fi >
Manage known networks, select your phone's hotspot and turn on **Connect
automatically when in range**. With the hotspot on, Windows then switches to
it by itself.

**Heat.** A closed laptop in a bag has almost no airflow. Don't run hard jobs,
such as long builds or GPU work, in a closed bag. Nightcrew doesn't watch
temperatures.

## Uninstall

1. Quit Nightcrew from the tray.
2. Open a terminal in the folder that holds the exe and run
   `.\nightcrew.exe --uninstall`. It puts your power settings back, removes
   Start at login and deletes the Nightcrew Watchdog task.
3. Delete `nightcrew.exe`. To remove settings and logs too, delete
   `%LOCALAPPDATA%\Nightcrew`.

## Credits

Inspired by [Insomnia](https://github.com/krishhgg/Insomnia) by Krish Garg.

## License

MIT
