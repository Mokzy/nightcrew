# Contributing

Nightcrew is deliberately small. It keeps a laptop awake while agents work,
puts the settings back and follows the lid action when they stop. Please keep
changes to that job.

## Build and test

You need the .NET 10 SDK; `global.json` pins the version. In Git Bash, from
the repo root:

```bash
dotnet build -c Release
dotnet test -c Release
pwsh -File build/check-single-file.ps1
```

If the SDK is installed per user in `%LOCALAPPDATA%\Microsoft\dotnet-sdk`,
run this first:

```bash
export DOTNET_ROOT="$LOCALAPPDATA/Microsoft/dotnet-sdk"; export PATH="$DOTNET_ROOT:$PATH"; export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
```

To run one test class:

```bash
dotnet test -c Release -- --filter-class Nightcrew.Tests.<ClassName>
```

Without PowerShell 7, use `powershell -NoProfile -File` for the
single-file check.

The build must finish with 0 warnings, because warnings are errors.

## One exe

The release is a single `nightcrew.exe` and nothing else.
`build/check-single-file.ps1` fails if `src/Nightcrew/bin/Release/net48`
holds any other file.

- Don't add NuGet runtime packages. PolySharp is allowed because it only
  generates source at build time.
- Don't add files next to the exe. The fonts and the app icon are compiled
  into it, and the tray icons are drawn in code from `MarkGeometry`.
- JSON goes through `Nightcrew.Core.Json` (DataContractJsonSerializer).
  Persisted classes need `[DataContract]` and `[DataMember]`.

## C# on .NET Framework 4.8

The app targets `net48` with C# 14.

- **Allowed:** records, `init`, `required`, primary constructors, collection
  expressions and file-scoped namespaces.
- **Not allowed:**
  - default interface methods, static abstract members and ref fields
  - array range slicing (`arr[1..]`)
  - `Span`, `ValueTask`, `System.Text.Json` and `IAsyncEnumerable`
- Fix nullable warnings properly. Don't suppress them.

## Where code goes

| Namespace | Contents |
|---|---|
| `Nightcrew.Core` | Pure logic with no P/Invoke, unit-tested with fakes |
| `Nightcrew.Platform` | Thin Windows wrappers (P/Invoke and WinForms) |
| `Nightcrew.App` | Tray icon, menu and wiring |
| `Nightcrew.App.Ui` | The WPF panel, the marks, the theme and the asset renderer |

## Words and looks

Everything Nightcrew shows, from the panel and its settings page to the tooltip, uses
sentence case, short plain sentences and contractions. No exclamation marks,
no "please", no "successfully". Say what happens, not how. The panel's words
all come from `PanelModel`, so they're unit-tested.

Nightcrew is dark only. Colours and fonts live in `App/Ui/Theme.cs`, and the
README pages in `docs/assets/src/style.css` use the same values.

## Icon and README images

The app icon (`src/Nightcrew/nightcrew.ico`) and the images in `docs/assets`
are made from the real code. After changing the marks, the panel or the pages
in `docs/assets/src`, build and render them again:

```bash
dotnet build -c Release
powershell -ExecutionPolicy Bypass -File build/render-assets.ps1
```

The script loads `nightcrew.exe` as a library and calls `AssetRenderer`, so
the tray app never starts. It needs Windows PowerShell 5.1 and Google Chrome.
GitHub's social preview, `docs/assets/social.png`, is uploaded by hand in the
repository settings.

## Tests

Every change to behaviour needs a test in `tests/Nightcrew.Tests`. Write the
test first and watch it fail.

Tests must never change the machine they run on. Use fakes for anything that:

- writes power settings
- holds a power request
- sleeps, hibernates, turns off the monitor or locks the workstation
- writes the `HKCU\...\Run` key
- registers a scheduled task

Tests may call read-only APIs on the real machine, such as reading power
values, the access check, battery status, the process list, performance
counters and file scans.

Changes to lid, sleep or hibernate behaviour also need the relevant steps of
[docs/hardware-checklist.md](docs/hardware-checklist.md) on a real laptop.

## Releases

Push a tag such as `v1.0.0`. The release workflow tests and builds the exe,
checks that it's a single file, writes `nightcrew.exe.sha256`, attests the
build and publishes the GitHub release. Pin the workflow's actions to full
commit SHAs before the first public release.
