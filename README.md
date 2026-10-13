# Nightcrew

Keeps a closed Windows laptop working for as long as Claude Code or Codex is
busy, freezes everything else, then sleeps.

- Nothing running: closing the lid does whatever Windows is set to do.
- An agent mid-task, or a busy job it started: closing the lid keeps the laptop
  running, freezes the apps the agents aren't using, and carries out your lid
  action once everything finishes.
- Battery floor, a maximum lid-closed time, and a phone-hotspot fallback if
  Wi-Fi drops.
- Tray app, no hooks or plugins, single `.exe` on .NET Framework 4.8.

Status: in design. See the
[design spec](docs/superpowers/specs/2026-10-13-nightcrew-design.md).

Inspired by [Insomnia](https://github.com/krishhgg/Insomnia) for macOS by Krish
Garg.

## License

MIT
