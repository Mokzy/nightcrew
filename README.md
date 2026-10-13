# Vigil

Keeps a Windows laptop working with the lid closed while Claude Code or Codex is
busy, then lets it sleep normally once the work is done.

- Nothing running: closing the lid sleeps as usual.
- An agent mid-task, or a busy job it started: closing the lid keeps the laptop
  running, freezes the apps the agents aren't using, and sleeps once everything
  finishes.
- Battery floor, a maximum lid-closed time, and a phone-hotspot fallback if
  Wi-Fi drops.

Status: in design. See [docs/superpowers/specs](docs/superpowers/specs).

Inspired by Insomnia for macOS by Krish Garg.

## License

MIT
