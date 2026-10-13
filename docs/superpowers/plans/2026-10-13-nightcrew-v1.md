# Nightcrew v1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build Nightcrew v1, a single-exe Windows tray app. While Claude Code
or Codex is busy, it keeps the laptop working with the lid closed. When the
work ends, it restores the user's power settings and carries out their lid
action.

**Architecture:**
- **Core** (`Nightcrew.Core`): pure logic, unit-tested with fakes. This covers
  the activity signals, the controller state machine, the restore journal and
  the engine.
- **Platform** (`Nightcrew.Platform`): thin Windows wrappers for powrprof,
  power requests, lid notifications, Toolhelp, PDH and Task Scheduler.
- **App** (`Nightcrew.App`): the tray UI and the wiring.

Every 10 seconds the engine samples activity, the controller decides, and the
engine carries out the decision. Every power-setting change is written to
`restore.json` before it is applied.

**Tech Stack:**
- C# 14 on .NET Framework 4.8 (SDK-style projects, .NET 10 SDK 10.0.401)
- WinForms NotifyIcon
- PolySharp
- xUnit v3 on Microsoft Testing Platform
- GitHub Actions

**Spec:** `docs/superpowers/specs/2026-10-13-nightcrew-design.md`. **Section 0
(v1 scope) governs.** Sections 1–16 are background and a v2 roadmap. Where
they disagree with section 0, section 0 wins.

## Global Constraints

**Build and language:**
- Target `net48`, with `LangVersion 14.0`, `Nullable enable`,
  `ImplicitUsings enable` and `TreatWarningsAsErrors true`.
- The app's only package is PolySharp (`PrivateAssets=all`). The Release
  output must be exactly `nightcrew.exe`: `build/check-single-file.ps1` passes.
- Not allowed: default interface methods, static abstract members, ref
  fields, array range slicing, Span, ValueTask, System.Text.Json,
  IAsyncEnumerable.
- JSON goes only through `Nightcrew.Core.Json` (DataContractJsonSerializer).

**Runtime behaviour:**
- Data lives in `%LOCALAPPDATA%\Nightcrew`: `settings.json`, `restore.json`,
  `nightcrew.log`.
- Every power-setting write is preceded by an atomic `restore.json` write.
- Restore writes back only the fields that still equal what Nightcrew wrote.
- No admin rights; the manifest is `asInvoker`.

**Testing:**
- Tests never call real power-changing APIs: writing settings, power
  requests, hibernate, monitor off, lock, the Run key, or scheduled tasks.
- Tests on the real machine are read-only.

**Shell:** Git Bash. Before any `dotnet` command, run the env line:
`export DOTNET_ROOT="$LOCALAPPDATA/Microsoft/dotnet-sdk"; export PATH="$DOTNET_ROOT:$PATH"; export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1`.
Skip it if a machine-wide .NET 10 SDK is on PATH.

**Single test class:** `dotnet test -c Release -- --filter-class Nightcrew.Tests.<Class>`.

## Review Focus

These are the inputs most likely to break Nightcrew in real use that the
spec doesn't spell out. Each is pinned by a test in the task that owns the
code.

1. **The lid is never reported** (lid state stays Unknown). Expected:
   Nightcrew still arms while agents work, but never locks, finishes,
   hibernates or applies the floor or cap. Test: Task 8, `Unknown_lid_never_locks_or_finishes`.
2. **The user changes the lid setting in Control Panel while Nightcrew holds
   it.** Expected: on restore, the user's new value is kept and the other
   fields are restored. Test: Task 6, `Restore_keeps_fields_the_user_changed`.
3. **A process exits or denies access** between listing and reading its
   times. Expected: it is skipped and the sample still completes. Test: Task 4,
   `Vanished_or_denied_processes_are_skipped`.
4. **The laptop resumes from sleep in the middle of finishing.** Expected:
   Nightcrew returns to Idle and doesn't hibernate later. Test: Task 8,
   `Resume_during_finishing_goes_idle_and_never_hibernates_later`.
5. **A transcript file shrinks or is deleted** (a session compacts or is
   cleaned up). Expected: this never counts as activity, and a later growth
   of that file does. Test: Task 3,
   `Shrink_or_delete_is_not_busy_but_later_growth_is`.

## File Structure

```
global.json, Directory.Build.props, Nightcrew.slnx      build (Task 1)
build/check-single-file.ps1                             single-exe gate (Task 1)
.github/workflows/ci.yml, release.yml                   CI (Task 1), release (Task 12)
src/Nightcrew/
  Nightcrew.csproj, app.manifest, Program.cs            app project; Program wired in Task 11
  Core/                                                 pure logic
    Contracts.cs, Settings.cs, Json.cs                  shared types (Task 2)
    TranscriptSignal.cs                                 transcript growth signal (Task 3)
    AgentTreeSignal.cs                                  agent tool-process CPU signal (Task 4)
    GpuSignal.cs                                        GPU signal (Task 5)
    RestoreJournal.cs                                   journaled power-setting changes (Task 6)
    Controller.cs                                       state machine (Task 8)
    WatchdogTaskXml.cs                                  watchdog task definition (Task 10)
    ActivityMonitor.cs, Engine.cs                       sampling and command execution (Task 9)
  Platform/                                             Windows wrappers
    AtomicFile.cs, AppPaths.cs, SettingsStore.cs,
    SystemClock.cs, FileLog.cs                          (Task 2)
    JsonlFileScanner.cs                                 (Task 3)
    ToolhelpProcessTable.cs                             (Task 4)
    PdhGpuUsage.cs                                      (Task 5)
    PowrProfPolicy.cs                                   (Task 6)
    KeepAwake.cs, SystemPower.cs,
    PowerEventsWindow.cs, PowerBroadcast.cs             (Task 7)
    DryRun.cs                                           (Task 9)
    Autostart.cs, WatchdogTask.cs                       (Task 10)
  App/
    Watchdog.cs                                         --watchdog verb (Task 10)
    TrayApp.cs, TrayIcons.cs, CommandLine.cs            tray and verbs (Task 11)
tests/Nightcrew.Tests/                                  one test file per unit
README.md, CONTRIBUTING.md, docs/hardware-checklist.md  (Task 12)
```

---

### Task 1: Toolchain, solution skeleton and CI

**Files:**
- Create: `global.json`
- Create: `Directory.Build.props`
- Create: `Nightcrew.slnx` (generated by `dotnet new sln`)
- Create: `src/Nightcrew/Nightcrew.csproj`
- Create: `src/Nightcrew/app.manifest`
- Create: `src/Nightcrew/Program.cs`
- Create: `tests/Nightcrew.Tests/Nightcrew.Tests.csproj`
- Create: `build/check-single-file.ps1`
- Create: `.github/workflows/ci.yml`
- Test: `tests/Nightcrew.Tests/ToolchainTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - the build: `dotnet build -c Release` writes `src/Nightcrew/bin/Release/net48/nightcrew.exe`
  - the test runner: `dotnet test -c Release`
  - the gate: `build/check-single-file.ps1`
  - `Nightcrew.Program` (stub `Main`, replaced in Task 11)

- [ ] **Step 0: Install the .NET 10 SDK (per user, no admin)**

Skip this step if `dotnet --list-sdks` already shows `10.0.4xx`.

```powershell
$dl = Join-Path $env:TEMP "dotnet-install"; New-Item -ItemType Directory -Force $dl | Out-Null
Invoke-WebRequest -UseBasicParsing https://dot.net/v1/dotnet-install.ps1 -OutFile "$dl\dotnet-install.ps1"
(Get-AuthenticodeSignature "$dl\dotnet-install.ps1").Status   # expect: Valid (Microsoft Corporation)
& "$dl\dotnet-install.ps1" -Channel 10.0 -InstallDir "$env:LOCALAPPDATA\Microsoft\dotnet-sdk" -NoPath
```

From now on, run this line in Git Bash before any `dotnet` command (the
Global Constraints call it **the env line**):

```bash
export DOTNET_ROOT="$LOCALAPPDATA/Microsoft/dotnet-sdk"; export PATH="$DOTNET_ROOT:$PATH"; export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
```

- [ ] **Step 1: Write the build files**

`global.json`:
```json
{
  "sdk": { "version": "10.0.401", "rollForward": "latestFeature" },
  "test": { "runner": "Microsoft.Testing.Platform" }
}
```

`Directory.Build.props`:
```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net48</TargetFramework>
    <LangVersion>14.0</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <DebugType>embedded</DebugType>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="PolySharp" Version="1.16.0" PrivateAssets="all" IncludeAssets="runtime; build; native; contentfiles; analyzers; buildtransitive" />
  </ItemGroup>
</Project>
```

`src/Nightcrew/Nightcrew.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <UseWindowsForms>true</UseWindowsForms>
    <AssemblyName>nightcrew</AssemblyName>
    <RootNamespace>Nightcrew</RootNamespace>
    <ApplicationManifest>app.manifest</ApplicationManifest>
    <GenerateSupportedRuntime>false</GenerateSupportedRuntime>
    <AutoGenerateBindingRedirects>false</AutoGenerateBindingRedirects>
  </PropertyGroup>
  <ItemGroup>
    <InternalsVisibleTo Include="Nightcrew.Tests" />
  </ItemGroup>
</Project>
```

`src/Nightcrew/app.manifest`:
```xml
<?xml version="1.0" encoding="utf-8"?>
<assembly manifestVersion="1.0" xmlns="urn:schemas-microsoft-com:asm.v1">
  <assemblyIdentity version="1.0.0.0" name="Nightcrew.app" />
  <trustInfo xmlns="urn:schemas-microsoft-com:asm.v2">
    <security>
      <requestedPrivileges xmlns="urn:schemas-microsoft-com:asm.v3">
        <requestedExecutionLevel level="asInvoker" uiAccess="false" />
      </requestedPrivileges>
    </security>
  </trustInfo>
  <compatibility xmlns="urn:schemas-microsoft-com:compatibility.v1">
    <application>
      <supportedOS Id="{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}" />
    </application>
  </compatibility>
  <application xmlns="urn:schemas-microsoft-com:asm.v3">
    <windowsSettings>
      <dpiAware xmlns="http://schemas.microsoft.com/SMI/2005/WindowsSettings">true/pm</dpiAware>
      <dpiAwareness xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">PerMonitorV2</dpiAwareness>
    </windowsSettings>
  </application>
</assembly>
```

`src/Nightcrew/Program.cs` (stub; Task 11 replaces it):
```csharp
namespace Nightcrew;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args) => 0;
}
```

`tests/Nightcrew.Tests/Nightcrew.Tests.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="xunit.v3" Version="4.0.2" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\Nightcrew\Nightcrew.csproj" />
  </ItemGroup>
</Project>
```

`build/check-single-file.ps1`:
```powershell
param([string]$Dir = "src/Nightcrew/bin/Release/net48")
$files = @(Get-ChildItem -Path $Dir -File)
if ($files.Count -ne 1 -or $files[0].Name -ne "nightcrew.exe") {
  Write-Error ("Expected only nightcrew.exe in $Dir, found: " + (($files | ForEach-Object Name) -join ", "))
  exit 1
}
"Single-file gate passed: $Dir\nightcrew.exe"
```

`.github/workflows/ci.yml`:
```yaml
name: ci

on:
  push:
    branches: [main]
  pull_request:

jobs:
  build:
    runs-on: windows-2025
    steps:
      - uses: actions/checkout@v7
      - uses: actions/setup-dotnet@v6
        with:
          global-json-file: global.json
      - run: dotnet build -c Release
      - run: dotnet test -c Release --no-build
      - run: pwsh -File build/check-single-file.ps1
```

Create the solution (run the env line first):
```bash
dotnet new sln -n Nightcrew -o .
dotnet sln add src/Nightcrew/Nightcrew.csproj tests/Nightcrew.Tests/Nightcrew.Tests.csproj
```
Expected: `Nightcrew.slnx` exists, and the output says both projects were added.

- [ ] **Step 2: Write the toolchain test**

`tests/Nightcrew.Tests/ToolchainTests.cs`:
```csharp
using Xunit;

namespace Nightcrew.Tests;

public class ToolchainTests
{
    private sealed record Sample(int A, string B)
    {
        public required int C { get; init; }
    }

    [Fact]
    public void Modern_csharp_features_work_on_net48()
    {
        var sample = new Sample(1, "b") { C = 3 };
        int[] numbers = [1, 2, 3];
        Assert.Equal(3, sample.C);
        Assert.Equal(sample with { C = 3 }, sample);
        Assert.Equal(6, numbers.Sum());
    }
}
```

- [ ] **Step 3: Build, test, gate**

Run:
```bash
dotnet build -c Release
dotnet test -c Release
powershell -NoProfile -File build/check-single-file.ps1
```
Expected:
- `Build succeeded.` with 0 warnings
- `Test run summary: Passed!` with total 1
- `Single-file gate passed`

- [ ] **Step 4: Commit**

```bash
git add global.json Directory.Build.props Nightcrew.slnx src/Nightcrew build/check-single-file.ps1 tests/Nightcrew.Tests .github/workflows/ci.yml
git commit -m "build: net48 skeleton with modern C#, xUnit v3, CI and single-exe gate"
```

### Task 2: Core contracts, settings, JSON, atomic files and logging

**Files:**
- Create: `src/Nightcrew/Core/Contracts.cs`
- Create: `src/Nightcrew/Core/Settings.cs`
- Create: `src/Nightcrew/Core/Json.cs`
- Create: `src/Nightcrew/Platform/AtomicFile.cs`
- Create: `src/Nightcrew/Platform/AppPaths.cs`
- Create: `src/Nightcrew/Platform/SettingsStore.cs`
- Create: `src/Nightcrew/Platform/SystemClock.cs`
- Create: `src/Nightcrew/Platform/FileLog.cs`
- Test: `tests/Nightcrew.Tests/FakeClock.cs`
- Test: `tests/Nightcrew.Tests/SettingsStoreTests.cs`
- Test: `tests/Nightcrew.Tests/FileLogTests.cs`

**Interfaces:**
- Consumes: Task 1 build.
- Produces (binding for every later task):
  - **Contract types:**
    - `Nightcrew.Core.PowerSource { AC, DC }`
    - `LidAction : uint { DoNothing = 0, Sleep = 1, Hibernate = 2, ShutDown = 3 }`
    - `LidState { Unknown, Open, Closed }`
    - `readonly record struct PowerValues(uint LidAc, uint LidDc, uint StandbyAc, uint StandbyDc)` with `LidAction LidFor(PowerSource)`
  - **Interfaces:**
    - `IClock { TimeSpan Now; DateTime UtcNow; }`
    - `IPowerPolicy { PowerValues Read(); void Write(PowerValues); bool CanWrite(out string reason); }`
    - `IKeepAwake { bool IsHeld; void Hold(); void Release(); }`
    - `ISystemPower { PowerSource Source; int? BatteryPercent; TimeSpan IdleTime; bool HibernateAvailable; bool Hibernate(); void MonitorOff(); void LockWorkstation(); }`
    - `ILog { void Info(string); void Warn(string); }`
    - `IBusySignal { string Name; bool Sample(TimeSpan now); }`
  - **Settings and helpers:**
    - `Settings`: its defaults are in the code below
    - `Json.Serialize<T>` and `Json.Deserialize<T>`
    - `AtomicFile.WriteAllText(path, text)`
    - `AppPaths.DataDir`, `SettingsFile`, `RestoreFile`, `LogFile`
    - `SettingsStore.Load(path)` and `SettingsStore.Save(path, settings)`
    - `SystemClock`, `FileLog(path, maxBytes)` (never throws on an I/O or access error: an `IOException` or `UnauthorizedAccessException` drops the line)
    - test helper `FakeClock { Now; UtcNow; Advance(TimeSpan) }`

- [ ] **Step 1: Write the failing tests**

`tests/Nightcrew.Tests/FakeClock.cs`:
```csharp
using Nightcrew.Core;

namespace Nightcrew.Tests;

public sealed class FakeClock : IClock
{
    public TimeSpan Now { get; set; } = TimeSpan.FromHours(1);
    public DateTime UtcNow { get; set; } = new(2026, 10, 13, 22, 0, 0, DateTimeKind.Utc);

    public void Advance(TimeSpan by)
    {
        Now += by;
        UtcNow += by;
    }
}
```

`tests/Nightcrew.Tests/SettingsStoreTests.cs`:
```csharp
using Nightcrew.Core;
using Nightcrew.Platform;
using Xunit;

namespace Nightcrew.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nightcrew-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Missing_file_creates_defaults()
    {
        var path = Path.Combine(_dir, "settings.json");
        var settings = SettingsStore.Load(path);
        Assert.True(File.Exists(path));
        Assert.Equal(5, settings.GraceMinutes);
        Assert.Equal(20, settings.BatteryFloorPercent);
        Assert.Contains("claude", settings.AgentProcessNames);
    }

    [Fact]
    public void Missing_members_keep_defaults_when_loading_old_file()
    {
        var path = Path.Combine(_dir, "settings.json");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(path, "{\"BatteryFloorPercent\":35}");
        var settings = SettingsStore.Load(path);
        Assert.Equal(35, settings.BatteryFloorPercent);
        Assert.Equal(8, settings.LidClosedCapHours);
        Assert.NotEmpty(settings.TranscriptRoots);
    }

    [Fact]
    public void Corrupt_file_is_set_aside_and_defaults_are_used()
    {
        var path = Path.Combine(_dir, "settings.json");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(path, "{ this is not json");
        var settings = SettingsStore.Load(path);
        Assert.Equal(5, settings.GraceMinutes);
        Assert.True(File.Exists(path + ".corrupt"));
        Assert.Equal(5, SettingsStore.Load(path).GraceMinutes);
    }

    [Fact]
    public void Save_then_load_round_trips()
    {
        var path = Path.Combine(_dir, "settings.json");
        var settings = new Settings { GraceMinutes = 7, LockOnLidClose = false };
        SettingsStore.Save(path, settings);
        var loaded = SettingsStore.Load(path);
        Assert.Equal(7, loaded.GraceMinutes);
        Assert.False(loaded.LockOnLidClose);
        Assert.False(File.Exists(path + ".tmp"));
    }
}
```

`tests/Nightcrew.Tests/FileLogTests.cs`:
```csharp
using Nightcrew.Platform;
using Xunit;

namespace Nightcrew.Tests;

public sealed class FileLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nightcrew-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Writes_a_line_that_can_be_read_back()
    {
        var path = Path.Combine(_dir, "nightcrew.log");
        var log = new FileLog(path);

        log.Info("hello");
        log.Warn("careful");

        var lines = File.ReadAllLines(path);
        Assert.Equal(2, lines.Length);
        Assert.EndsWith("[INFO] hello", lines[0]);
        Assert.EndsWith("[WARN] careful", lines[1]);
    }

    [Fact]
    public void Logging_where_the_folder_is_a_file_does_not_throw()
    {
        Directory.CreateDirectory(_dir);
        var notAFolder = Path.Combine(_dir, "not-a-folder");
        File.WriteAllText(notAFolder, "");
        var log = new FileLog(Path.Combine(notAFolder, "nightcrew.log"));

        log.Info("dropped");
        log.Warn("dropped");

        Assert.Equal("", File.ReadAllText(notAFolder));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test -c Release -- --filter-class Nightcrew.Tests.SettingsStoreTests --filter-class Nightcrew.Tests.FileLogTests`
Expected: build error, ``The type or namespace name 'Settings' could not be found`` (and the same for `FileLog`).

- [ ] **Step 3: Write the implementation**

`src/Nightcrew/Core/Contracts.cs`:
```csharp
namespace Nightcrew.Core;

public enum PowerSource { AC, DC }

/// <summary>Windows lid-close action indices as stored in the power scheme.</summary>
public enum LidAction : uint { DoNothing = 0, Sleep = 1, Hibernate = 2, ShutDown = 3 }

public enum LidState { Unknown, Open, Closed }

/// <summary>Lid action indices and sleep-after seconds (0 = never) of the active power scheme.</summary>
public readonly record struct PowerValues(uint LidAc, uint LidDc, uint StandbyAc, uint StandbyDc)
{
    public LidAction LidFor(PowerSource source) => (LidAction)(source == PowerSource.AC ? LidAc : LidDc);
}

public interface IClock
{
    /// <summary>Monotonic time that excludes sleep (QueryUnbiasedInterruptTime).</summary>
    TimeSpan Now { get; }
    DateTime UtcNow { get; }
}

public interface IPowerPolicy
{
    PowerValues Read();
    /// <summary>Writes all four values to the active scheme, re-activates it and verifies the read-back. Throws on failure.</summary>
    void Write(PowerValues values);
    bool CanWrite(out string reason);
}

public interface IKeepAwake
{
    bool IsHeld { get; }
    void Hold();
    void Release();
}

public interface ISystemPower
{
    PowerSource Source { get; }
    /// <summary>Null when there is no battery or the level is unknown.</summary>
    int? BatteryPercent { get; }
    /// <summary>Time since the last keyboard or mouse input.</summary>
    TimeSpan IdleTime { get; }
    bool HibernateAvailable { get; }
    /// <summary>Returns false if hibernation failed or is unavailable.</summary>
    bool Hibernate();
    void MonitorOff();
    void LockWorkstation();
}

public interface ILog
{
    void Info(string message);
    void Warn(string message);
}

public interface IBusySignal
{
    string Name { get; }
    /// <summary>Takes one sample; returns true if the source was busy during it.</summary>
    bool Sample(TimeSpan now);
}
```

`src/Nightcrew/Core/Settings.cs`:
```csharp
using System.Runtime.Serialization;

namespace Nightcrew.Core;

[DataContract]
public sealed class Settings
{
    public Settings() => ApplyDefaults();

    [DataMember] public int GraceMinutes { get; set; }
    [DataMember] public int BatteryFloorPercent { get; set; }
    [DataMember] public int LidClosedCapHours { get; set; }
    [DataMember] public int AwayMinutes { get; set; }
    [DataMember] public bool LockOnLidClose { get; set; }
    [DataMember] public bool StartAtLogin { get; set; }
    [DataMember] public bool DryRun { get; set; }
    [DataMember] public string[] TranscriptRoots { get; set; } = [];
    [DataMember] public string[] IgnorePathContains { get; set; } = [];
    [DataMember] public string[] AgentProcessNames { get; set; } = [];
    [DataMember] public string[] SelfCpuAgentNames { get; set; } = [];
    [DataMember] public double SelfCpuPercent { get; set; }
    [DataMember] public double ToolCpuPercent { get; set; }
    [DataMember] public double GpuPercent { get; set; }
    [DataMember] public int SampleSeconds { get; set; }

    [OnDeserializing]
    private void OnDeserializing(StreamingContext context) => ApplyDefaults();

    private void ApplyDefaults()
    {
        GraceMinutes = 5;
        BatteryFloorPercent = 20;
        LidClosedCapHours = 8;
        AwayMinutes = 5;
        LockOnLidClose = true;
        StartAtLogin = true;
        DryRun = false;
        TranscriptRoots = [@"%USERPROFILE%\.claude\projects", @"%USERPROFILE%\.codex\sessions"];
        IgnorePathContains = ["claude-mem-observer"];
        AgentProcessNames = ["claude", "codex"];
        SelfCpuAgentNames = ["codex"];
        SelfCpuPercent = 2;
        ToolCpuPercent = 10;
        GpuPercent = 30;
        SampleSeconds = 10;
    }
}
```

`src/Nightcrew/Core/Json.cs`:
```csharp
using System.Runtime.Serialization.Json;
using System.Text;

namespace Nightcrew.Core;

public static class Json
{
    private static readonly DataContractJsonSerializerSettings Options = new() { UseSimpleDictionaryFormat = true };

    public static string Serialize<T>(T value)
    {
        using var stream = new MemoryStream();
        new DataContractJsonSerializer(typeof(T), Options).WriteObject(stream, value);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static T Deserialize<T>(string text)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
        return (T)new DataContractJsonSerializer(typeof(T), Options).ReadObject(stream)!;
    }
}
```

`src/Nightcrew/Platform/AtomicFile.cs`:
```csharp
using System.Runtime.InteropServices;
using System.Text;

namespace Nightcrew.Platform;

public static class AtomicFile
{
    private const uint MOVEFILE_REPLACE_EXISTING = 0x1;
    private const uint MOVEFILE_WRITE_THROUGH = 0x8;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool MoveFileEx(string existing, string replacement, uint flags);

    /// <summary>Writes to a temp file, flushes it to disk, then swaps it in.</summary>
    public static void WriteAllText(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var bytes = new UTF8Encoding(false).GetBytes(text);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(flushToDisk: true);
        }
        if (!MoveFileEx(temp, path, MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH))
            throw new IOException($"MoveFileEx failed for {path}", Marshal.GetLastWin32Error());
    }
}
```

`src/Nightcrew/Platform/AppPaths.cs`:
```csharp
namespace Nightcrew.Platform;

public static class AppPaths
{
    public static string DataDir { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nightcrew");

    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string RestoreFile => Path.Combine(DataDir, "restore.json");
    public static string LogFile => Path.Combine(DataDir, "nightcrew.log");
}
```

`src/Nightcrew/Platform/SettingsStore.cs`:
```csharp
using System.Runtime.Serialization;
using Nightcrew.Core;

namespace Nightcrew.Platform;

public static class SettingsStore
{
    /// <summary>Loads settings; a missing file is created with defaults, a corrupt one is set aside and replaced.</summary>
    public static Settings Load(string path)
    {
        if (File.Exists(path))
        {
            try
            {
                return Json.Deserialize<Settings>(File.ReadAllText(path));
            }
            catch (SerializationException)
            {
                File.Copy(path, path + ".corrupt", overwrite: true);
            }
        }
        var defaults = new Settings();
        Save(path, defaults);
        return defaults;
    }

    public static void Save(string path, Settings settings) => AtomicFile.WriteAllText(path, Json.Serialize(settings));
}
```

`src/Nightcrew/Platform/SystemClock.cs`:
```csharp
using System.Runtime.InteropServices;
using Nightcrew.Core;

namespace Nightcrew.Platform;

public sealed class SystemClock : IClock
{
    [DllImport("kernel32.dll")]
    private static extern bool QueryUnbiasedInterruptTime(out ulong unbiasedTime);

    public TimeSpan Now
    {
        get
        {
            QueryUnbiasedInterruptTime(out var ticks);
            return TimeSpan.FromTicks((long)ticks);
        }
    }

    public DateTime UtcNow => DateTime.UtcNow;
}
```

`src/Nightcrew/Platform/FileLog.cs`:
```csharp
using Nightcrew.Core;

namespace Nightcrew.Platform;

/// <summary>Appends to a text file, rolling it to .1 past maxBytes. Never throws on I/O or access errors: the line is dropped.</summary>
public sealed class FileLog(string path, long maxBytes = 5 * 1024 * 1024) : ILog
{
    private readonly object _gate = new();

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);

    private void Write(string level, string message)
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (File.Exists(path) && new FileInfo(path).Length > maxBytes)
                {
                    var old = path + ".1";
                    if (File.Exists(old)) File.Delete(old);
                    File.Move(path, old);
                }
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Logging must never take the app down: a locked, full or unwritable log loses the line.
            }
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test -c Release -- --filter-class Nightcrew.Tests.SettingsStoreTests --filter-class Nightcrew.Tests.FileLogTests`
Expected: PASS, 6 tests (4 in `SettingsStoreTests`, 2 in `FileLogTests`).

- [ ] **Step 5: Commit**

```bash
git add src/Nightcrew/Core src/Nightcrew/Platform tests/Nightcrew.Tests/FakeClock.cs tests/Nightcrew.Tests/SettingsStoreTests.cs tests/Nightcrew.Tests/FileLogTests.cs
git commit -m "feat: core contracts, settings store, atomic file writes and log"
```

### Task 3: Transcript activity signal

**Files:**
- Create: `src/Nightcrew/Core/TranscriptSignal.cs`
- Create: `src/Nightcrew/Platform/JsonlFileScanner.cs`
- Test: `tests/Nightcrew.Tests/TranscriptSignalTests.cs`
- Test: `tests/Nightcrew.Tests/JsonlFileScannerTests.cs`

**Interfaces:**
- Consumes: `Nightcrew.Core.IBusySignal { string Name { get; } bool Sample(TimeSpan now); }` (skeleton, `Core/Contracts.cs`)
- Produces:
  - `public interface IFileScanner { IReadOnlyDictionary<string, long> Scan(IReadOnlyList<string> roots); }` (namespace `Nightcrew.Core`; full path -> length of every `*.jsonl` under the roots, recursive)
  - `public sealed class TranscriptSignal(IFileScanner scanner, IReadOnlyList<string> roots, IReadOnlyList<string> ignorePathContains) : IBusySignal` with `string Name => "transcripts"`, `string? LastChangedPath { get; }`, `bool Sample(TimeSpan now)` (namespace `Nightcrew.Core`)
  - `public sealed class JsonlFileScanner : IFileScanner` (namespace `Nightcrew.Platform`; parameterless)
  - Wiring for Task 9/11: `new TranscriptSignal(new JsonlFileScanner(), settings.TranscriptRoots, settings.IgnorePathContains)`

Behaviour: the first `Sample` records a baseline and returns false. Each later sample compares the new scan with the previous one and returns true if any non-ignored file grew or newly appeared; shrinking or deleted files never count. Ignore matching is a case-insensitive substring match on the path; a blank ignore entry ignores nothing. `LastChangedPath` keeps the path that made the most recent busy sample busy (null until then). The scanner expands environment variables in each root, skips roots that do not exist, and skips a root on `IOException` or `UnauthorizedAccessException`. Because `EnumerateFiles` is lazy and cannot resume after an error, a failure part-way through a root keeps the files already found and drops the rest of that root; `Length` comes from the find data, so reading it does not open the file. Measured on the owner's machine: 3,603 transcript files, about 90 ms per warm scan.

- [ ] **Step 1: Write the failing tests**

`tests/Nightcrew.Tests/TranscriptSignalTests.cs`:

```csharp
using Nightcrew.Core;
using Xunit;

namespace Nightcrew.Tests;

public sealed class TranscriptSignalTests
{
    private const string Root = @"C:\transcripts";
    private const string A = @"C:\transcripts\proj\a.jsonl";
    private const string B = @"C:\transcripts\proj\b.jsonl";
    private const string Observer = @"C:\transcripts\Claude-Mem-Observer\o.jsonl";
    private static readonly TimeSpan T = TimeSpan.FromHours(1);

    private sealed class FakeScanner : IFileScanner
    {
        public Dictionary<string, long> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        public IReadOnlyList<string>? LastRoots { get; private set; }

        public IReadOnlyDictionary<string, long> Scan(IReadOnlyList<string> roots)
        {
            LastRoots = roots;
            return new Dictionary<string, long>(Files, StringComparer.OrdinalIgnoreCase);
        }
    }

    private static TranscriptSignal Create(FakeScanner scanner, params string[] ignore) =>
        new(scanner, [Root], ignore);

    [Fact]
    public void Name_is_transcripts()
    {
        Assert.Equal("transcripts", Create(new FakeScanner()).Name);
    }

    [Fact]
    public void First_sample_records_baseline_and_is_not_busy()
    {
        var scanner = new FakeScanner();
        scanner.Files[A] = 100;
        var signal = Create(scanner);

        Assert.False(signal.Sample(T));
        Assert.Null(signal.LastChangedPath);
        Assert.Equal([Root], scanner.LastRoots);
    }

    [Fact]
    public void Growing_file_is_busy_and_unchanged_files_are_not()
    {
        var scanner = new FakeScanner();
        scanner.Files[A] = 100;
        scanner.Files[B] = 50;
        var signal = Create(scanner);
        signal.Sample(T);

        Assert.False(signal.Sample(T + TimeSpan.FromSeconds(10)));

        scanner.Files[B] = 60;
        Assert.True(signal.Sample(T + TimeSpan.FromSeconds(20)));
        Assert.Equal(B, signal.LastChangedPath);

        Assert.False(signal.Sample(T + TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void New_file_is_busy()
    {
        var scanner = new FakeScanner();
        scanner.Files[A] = 100;
        var signal = Create(scanner);
        signal.Sample(T);

        scanner.Files[B] = 1;
        Assert.True(signal.Sample(T + TimeSpan.FromSeconds(10)));
        Assert.Equal(B, signal.LastChangedPath);
    }

    [Fact]
    public void Shrink_or_delete_is_not_busy_but_later_growth_is()
    {
        var scanner = new FakeScanner();
        scanner.Files[A] = 100;
        scanner.Files[B] = 50;
        var signal = Create(scanner);
        signal.Sample(T);

        scanner.Files[A] = 10;
        Assert.False(signal.Sample(T + TimeSpan.FromSeconds(10)));  // shrink

        scanner.Files.Remove(B);
        Assert.False(signal.Sample(T + TimeSpan.FromSeconds(20)));  // delete
        Assert.Null(signal.LastChangedPath);

        scanner.Files[A] = 20;
        Assert.True(signal.Sample(T + TimeSpan.FromSeconds(30)));   // the surviving file grows
        Assert.Equal(A, signal.LastChangedPath);

        scanner.Files[B] = 1;
        Assert.True(signal.Sample(T + TimeSpan.FromSeconds(40)));   // the deleted file is created again
        Assert.Equal(B, signal.LastChangedPath);
    }

    [Fact]
    public void Ignored_paths_never_count_and_matching_ignores_case()
    {
        var scanner = new FakeScanner();
        scanner.Files[Observer] = 100;
        var signal = Create(scanner, "claude-mem-observer");
        signal.Sample(T);

        scanner.Files[Observer] = 200;
        scanner.Files[@"C:\transcripts\CLAUDE-MEM-OBSERVER\new.jsonl"] = 1;
        Assert.False(signal.Sample(T + TimeSpan.FromSeconds(10)));
        Assert.Null(signal.LastChangedPath);
    }

    [Fact]
    public void Blank_ignore_entry_ignores_nothing()
    {
        var scanner = new FakeScanner();
        scanner.Files[A] = 100;
        var signal = Create(scanner, "");
        signal.Sample(T);

        scanner.Files[A] = 101;
        Assert.True(signal.Sample(T + TimeSpan.FromSeconds(10)));
    }
}
```

`tests/Nightcrew.Tests/JsonlFileScannerTests.cs`:

```csharp
using Nightcrew.Platform;
using Xunit;

namespace Nightcrew.Tests;

public sealed class JsonlFileScannerTests : IDisposable
{
    private const string EnvVar = "NIGHTCREW_SCANNER_TEST_ROOT";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nightcrew-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(EnvVar, null);
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private string Touch(string relativePath, int bytes)
    {
        var path = Path.Combine(_dir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    [Fact]
    public void Finds_jsonl_files_recursively_with_their_lengths()
    {
        var top = Touch(@"claude\a.jsonl", 3);
        var deep = Touch(@"claude\proj\sub\b.jsonl", 5);
        Touch(@"claude\notes.txt", 7);
        Touch(@"claude\proj\c.json", 9);

        var files = new JsonlFileScanner().Scan([Path.Combine(_dir, "claude")]);

        Assert.Equal(2, files.Count);
        Assert.Equal(3, files[top]);
        Assert.Equal(5, files[deep]);
    }

    [Fact]
    public void Expands_environment_variables_and_skips_missing_roots()
    {
        var claude = Touch(@"claude\a.jsonl", 4);
        var codex = Touch(@"codex\2026\10\13\rollout.jsonl", 6);
        Environment.SetEnvironmentVariable(EnvVar, _dir);

        var files = new JsonlFileScanner().Scan(
            [@"%" + EnvVar + @"%\claude", Path.Combine(_dir, "missing"), Path.Combine(_dir, "codex")]);

        Assert.Equal(2, files.Count);
        Assert.Equal(4, files[claude]);
        Assert.Equal(6, files[codex]);
    }

    [Fact]
    public void No_roots_gives_an_empty_result()
    {
        Assert.Empty(new JsonlFileScanner().Scan([]));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test -c Release -- --filter-class Nightcrew.Tests.TranscriptSignalTests` (after the env line from the plan's Global Constraints)

Expected: build error `CS0246: The type or namespace name 'TranscriptSignal' could not be found` (also `IFileScanner` and `JsonlFileScanner`)

- [ ] **Step 3: Write the implementation**

`src/Nightcrew/Core/TranscriptSignal.cs`:

```csharp
namespace Nightcrew.Core;

public interface IFileScanner
{
    /// <summary>Full path -> length in bytes of every *.jsonl under the roots, recursive.</summary>
    IReadOnlyDictionary<string, long> Scan(IReadOnlyList<string> roots);
}

/// <summary>Busy when an agent transcript grew or appeared since the previous sample.</summary>
public sealed class TranscriptSignal(IFileScanner scanner, IReadOnlyList<string> roots, IReadOnlyList<string> ignorePathContains) : IBusySignal
{
    private IReadOnlyDictionary<string, long>? _previous;

    public string Name => "transcripts";

    /// <summary>The file that made the most recent busy sample busy.</summary>
    public string? LastChangedPath { get; private set; }

    public bool Sample(TimeSpan now)
    {
        var previous = _previous;
        var current = scanner.Scan(roots);
        _previous = current;
        if (previous is null) return false;

        foreach (var file in current)
        {
            if (IsIgnored(file.Key)) continue;
            if (!previous.TryGetValue(file.Key, out var before) || file.Value > before)
            {
                LastChangedPath = file.Key;
                return true;
            }
        }
        return false;
    }

    private bool IsIgnored(string path) =>
        ignorePathContains.Any(part => part.Length > 0 && path.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0);
}
```

`src/Nightcrew/Platform/JsonlFileScanner.cs`:

```csharp
using Nightcrew.Core;

namespace Nightcrew.Platform;

/// <summary>Lists *.jsonl files with their lengths; missing or unreadable roots are skipped.</summary>
public sealed class JsonlFileScanner : IFileScanner
{
    public IReadOnlyDictionary<string, long> Scan(IReadOnlyList<string> roots)
    {
        var files = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            try
            {
                var dir = new DirectoryInfo(Environment.ExpandEnvironmentVariables(root));
                if (!dir.Exists) continue;
                // Length comes from the find data, so no file is opened.
                foreach (var file in dir.EnumerateFiles("*.jsonl", SearchOption.AllDirectories))
                    files[file.FullName] = file.Length;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return files;
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test -c Release -- --filter-class Nightcrew.Tests.TranscriptSignalTests`  Expected: PASS (7 tests)

Run: `dotnet test -c Release -- --filter-class Nightcrew.Tests.JsonlFileScannerTests`  Expected: PASS (3 tests)

Then `dotnet build -c Release` (0 warnings, 0 errors) and `dotnet test -c Release` (all pass; 17 tests: 7 from Tasks 1 and 2 plus the 10 from this task).

- [ ] **Step 5: Commit**

```bash
git add src/Nightcrew/Core/TranscriptSignal.cs src/Nightcrew/Platform/JsonlFileScanner.cs tests/Nightcrew.Tests/TranscriptSignalTests.cs tests/Nightcrew.Tests/JsonlFileScannerTests.cs
git commit -m "feat: transcript activity signal and jsonl scanner"
```

### Task 4: Agent tool-process activity signal

**Files:**
- Create: `src/Nightcrew/Core/AgentTreeSignal.cs`
- Create: `src/Nightcrew/Platform/ToolhelpProcessTable.cs`
- Test: `tests/Nightcrew.Tests/AgentTreeSignalTests.cs`

**Interfaces:**
- Consumes: `Nightcrew.Core.IBusySignal { string Name { get; } bool Sample(TimeSpan now); }`; `Nightcrew.Core.Settings` (`string[] AgentProcessNames`, `string[] SelfCpuAgentNames`, `double SelfCpuPercent`, `double ToolCpuPercent`).
- Produces:
  - `public readonly record struct ProcessEntry(int Pid, int ParentPid, string Name)` (Name lowercased, no ".exe")
  - `public interface IProcessTable { IReadOnlyList<ProcessEntry> List(); bool TryGetTimes(int pid, out DateTime startUtc, out TimeSpan cpu); }`
  - `public sealed class AgentTreeSignal(IProcessTable table, Settings settings) : IBusySignal` with `string Name => "agent-tools"`, `string? LastBusyProcess { get; }` (e.g. "python (pid 1234)"; keeps the last busy value), `bool Sample(TimeSpan now)`
  - `public sealed class ToolhelpProcessTable : IProcessTable` (namespace `Nightcrew.Platform`, parameterless constructor)

CPU is measured as percent of one core, so `processorCount` is **dropped** from the `AgentTreeSignal` constructor (an unread primary-constructor parameter is warning CS9113, which fails the build). Wiring in later tasks constructs it as `new AgentTreeSignal(new ToolhelpProcessTable(), settings)`.

- [ ] **Step 1: Write the failing tests**

The fake process table lets each test build a process tree and add CPU time. The last test is the only real-machine test and is read-only: it lists processes and reads the current process's times.

`tests/Nightcrew.Tests/AgentTreeSignalTests.cs`:

```csharp
using System.Diagnostics;
using Nightcrew.Core;
using Nightcrew.Platform;
using Xunit;

namespace Nightcrew.Tests;

public sealed class AgentTreeSignalTests
{
    private readonly FakeProcessTable _table = new();
    private TimeSpan _now = TimeSpan.FromHours(1);

    private AgentTreeSignal Signal(Settings? settings = null) => new(_table, settings ?? new Settings());

    private bool SampleAfter(AgentTreeSignal signal, int seconds)
    {
        _now += TimeSpan.FromSeconds(seconds);
        return signal.Sample(_now);
    }

    // claude (10) -> bash (11) -> python (12), all started at the same time.
    private void ClaudeTree()
    {
        _table.Add(10, 1, "claude");
        _table.Add(11, 10, "bash");
        _table.Add(12, 11, "python");
    }

    [Fact]
    public void First_sighting_only_records_a_baseline()
    {
        ClaudeTree();
        _table.Burn(12, 100);
        var signal = Signal();
        Assert.False(signal.Sample(_now));
        Assert.Null(signal.LastBusyProcess);
    }

    [Fact]
    public void Busy_tool_below_an_agent_is_busy()
    {
        ClaudeTree();
        var signal = Signal();
        signal.Sample(_now);
        _table.Burn(12, 2); // 2 s of CPU in 10 s = 20% of one core
        Assert.True(SampleAfter(signal, 10));
        Assert.Equal("python (pid 12)", signal.LastBusyProcess);
        Assert.Equal("agent-tools", signal.Name);
    }

    [Fact]
    public void Tool_below_its_threshold_is_not_busy()
    {
        ClaudeTree();
        var signal = Signal();
        signal.Sample(_now);
        _table.Burn(12, 2); // 2 s of CPU in 30 s = 6.7%
        Assert.False(SampleAfter(signal, 30));
    }

    [Fact]
    public void Busy_process_outside_agent_trees_is_ignored()
    {
        _table.Add(10, 1, "claude");
        _table.Add(20, 1, "python");
        var signal = Signal();
        signal.Sample(_now);
        _table.Burn(20, 10);
        Assert.False(SampleAfter(signal, 10));
    }

    [Fact]
    public void Agent_and_conhost_cpu_is_not_tool_activity()
    {
        _table.Add(10, 1, "claude");
        _table.Add(11, 10, "conhost");
        _table.Add(12, 10, "claude");
        var signal = Signal();
        signal.Sample(_now);
        _table.Burn(10, 5);
        _table.Burn(11, 5);
        _table.Burn(12, 5);
        Assert.False(SampleAfter(signal, 10));
    }

    [Fact]
    public void Self_cpu_agent_over_its_own_threshold_is_busy()
    {
        _table.Add(30, 1, "codex");
        var signal = Signal();
        signal.Sample(_now);
        _table.Burn(30, 0.3); // 3% of one core, default SelfCpuPercent is 2
        Assert.True(SampleAfter(signal, 10));
        Assert.Equal("codex (pid 30)", signal.LastBusyProcess);
    }

    [Fact]
    public void Agent_names_match_case_insensitively()
    {
        ClaudeTree();
        var signal = Signal(new Settings { AgentProcessNames = ["Claude"] });
        signal.Sample(_now);
        _table.Burn(12, 2);
        Assert.True(SampleAfter(signal, 10));
    }

    [Fact]
    public void Parent_that_started_after_the_child_is_not_followed()
    {
        _table.Add(10, 1, "claude", startMinute: 5);
        _table.Add(12, 10, "python", startMinute: 0); // its real parent exited and pid 10 was reused
        var signal = Signal();
        signal.Sample(_now);
        _table.Burn(12, 5);
        Assert.False(SampleAfter(signal, 10));
    }

    [Fact]
    public void Cycle_in_parent_links_terminates()
    {
        _table.Add(10, 12, "claude");
        _table.Add(11, 10, "bash");
        _table.Add(12, 11, "python");
        var signal = Signal();
        signal.Sample(_now);
        _table.Burn(12, 5);
        Assert.True(SampleAfter(signal, 10));
    }

    [Fact]
    public void New_process_reusing_a_pid_gets_a_fresh_baseline()
    {
        ClaudeTree();
        var signal = Signal();
        signal.Sample(_now);
        _table.Remove(12);
        _table.Add(12, 11, "python", startMinute: 1);
        _table.Burn(12, 5);
        Assert.False(SampleAfter(signal, 10));
    }

    [Fact]
    public void Vanished_or_denied_processes_are_skipped()
    {
        ClaudeTree();
        _table.Add(13, 11, "node");
        _table.Add(14, 11, "go");
        var signal = Signal();
        signal.Sample(_now);
        _table.Deny(12);    // access denied
        _table.Vanish(13);  // still listed, but it exited before its times were read
        _table.Burn(12, 5);
        _table.Burn(13, 5);
        Assert.False(SampleAfter(signal, 10));

        _table.Burn(14, 2); // the sample still completes for the others
        Assert.True(SampleAfter(signal, 10));
        Assert.Equal("go (pid 14)", signal.LastBusyProcess);
    }

    [Fact]
    public void Exited_process_is_dropped_from_the_cache()
    {
        ClaudeTree();
        var signal = Signal();
        signal.Sample(_now);
        _table.Remove(12);
        Assert.False(SampleAfter(signal, 10));
        _table.Add(12, 11, "python");
        _table.Burn(12, 5);
        Assert.False(SampleAfter(signal, 10)); // baseline again, not a delta from before it exited
        _table.Burn(12, 2);
        Assert.True(SampleAfter(signal, 10));
    }

    [Fact]
    public void Toolhelp_table_lists_the_current_process_and_reads_its_times()
    {
        using var current = Process.GetCurrentProcess();
        var table = new ToolhelpProcessTable();
        var entry = Assert.Single(table.List(), p => p.Pid == current.Id);
        Assert.Equal(current.ProcessName.ToLowerInvariant(), entry.Name);
        Assert.NotEqual(0, entry.ParentPid);
        Assert.True(table.TryGetTimes(current.Id, out var start, out var cpu));
        Assert.True(start <= DateTime.UtcNow);
        Assert.True(cpu > TimeSpan.Zero);
    }

    private sealed class FakeProcessTable : IProcessTable
    {
        private static readonly DateTime T0 = new(2026, 10, 13, 20, 0, 0, DateTimeKind.Utc);
        private readonly Dictionary<int, Row> _rows = [];

        public void Add(int pid, int parentPid, string name, int startMinute = 0) =>
            _rows[pid] = new Row { Entry = new ProcessEntry(pid, parentPid, name), Start = T0.AddMinutes(startMinute) };

        public void Burn(int pid, double seconds) => _rows[pid].Cpu += TimeSpan.FromSeconds(seconds);

        public void Remove(int pid) => _rows.Remove(pid);

        public void Deny(int pid) => _rows[pid].Denied = true;

        public void Vanish(int pid) => _rows[pid].Vanished = true;

        public IReadOnlyList<ProcessEntry> List() => _rows.Values.Select(r => r.Entry).ToList();

        public bool TryGetTimes(int pid, out DateTime startUtc, out TimeSpan cpu)
        {
            startUtc = default;
            cpu = default;
            if (!_rows.TryGetValue(pid, out var row) || row.Denied || row.Vanished) return false;
            startUtc = row.Start;
            cpu = row.Cpu;
            return true;
        }

        private sealed class Row
        {
            public ProcessEntry Entry { get; set; }
            public DateTime Start { get; set; }
            public TimeSpan Cpu { get; set; }
            public bool Denied { get; set; }
            public bool Vanished { get; set; }
        }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test -c Release -- --filter-class Nightcrew.Tests.AgentTreeSignalTests` (after the env line from the plan's Global Constraints)
Expected: build error CS0246 naming the missing types `AgentTreeSignal`, `IProcessTable` and `ProcessEntry`.

- [ ] **Step 3: Write the implementation**

Each sample lists processes, walks down from every agent through `ParentPid` links (a link counts only if the parent did not start after the child; a visited set stops cycles), and watches tool descendants (not an agent name, not `conhost`) at `ToolCpuPercent` and any `SelfCpuAgentNames` process at `SelfCpuPercent`. CPU baselines are keyed by (pid, start time) and replaced every sample, so a first sighting is only a baseline and exited processes drop out.

`src/Nightcrew/Core/AgentTreeSignal.cs`:

```csharp
namespace Nightcrew.Core;

/// <summary>One process. Name is lowercased, without ".exe".</summary>
public readonly record struct ProcessEntry(int Pid, int ParentPid, string Name);

public interface IProcessTable
{
    IReadOnlyList<ProcessEntry> List();
    /// <summary>Returns false if the process has exited or access is denied.</summary>
    bool TryGetTimes(int pid, out DateTime startUtc, out TimeSpan cpu);
}

/// <summary>
/// Busy while a tool process started by an agent, or a self-CPU agent itself, uses CPU.
/// CPU is a percent of one core; it is not divided by the processor count.
/// </summary>
public sealed class AgentTreeSignal(IProcessTable table, Settings settings) : IBusySignal
{
    private Dictionary<(int Pid, DateTime Start), (TimeSpan Cpu, TimeSpan At)> _baselines = [];

    public string Name => "agent-tools";

    /// <summary>The process that made the most recent busy sample, e.g. "python (pid 1234)".</summary>
    public string? LastBusyProcess { get; private set; }

    public bool Sample(TimeSpan now)
    {
        var processes = table.List();
        var agents = new HashSet<string>(settings.AgentProcessNames, StringComparer.OrdinalIgnoreCase);
        var selfCpuAgents = new HashSet<string>(settings.SelfCpuAgentNames, StringComparer.OrdinalIgnoreCase);

        var times = new Dictionary<int, (DateTime Start, TimeSpan Cpu)?>();
        (DateTime Start, TimeSpan Cpu)? TimesOf(int pid)
        {
            if (!times.TryGetValue(pid, out var found))
                times[pid] = found = table.TryGetTimes(pid, out var start, out var cpu) ? (start, cpu) : null;
            return found;
        }

        var watched = new Dictionary<int, (ProcessEntry Process, double Threshold)>();
        foreach (var process in processes)
            if (selfCpuAgents.Contains(process.Name)) watched[process.Pid] = (process, settings.SelfCpuPercent);

        var children = processes.ToLookup(p => p.ParentPid);
        var queue = new Queue<ProcessEntry>(processes.Where(p => agents.Contains(p.Name)));
        var visited = new HashSet<int>(queue.Select(p => p.Pid));
        while (queue.Count > 0)
        {
            var parent = queue.Dequeue();
            if (TimesOf(parent.Pid) is not { } parentTimes) continue;
            foreach (var child in children[parent.Pid])
            {
                // A parent that started after its child is a reused pid, not the real parent.
                if (visited.Contains(child.Pid) || TimesOf(child.Pid) is not { } childTimes || parentTimes.Start > childTimes.Start) continue;
                visited.Add(child.Pid);
                queue.Enqueue(child);
                var isTool = !agents.Contains(child.Name) && !string.Equals(child.Name, "conhost", StringComparison.OrdinalIgnoreCase);
                if (isTool && !watched.ContainsKey(child.Pid)) watched[child.Pid] = (child, settings.ToolCpuPercent);
            }
        }

        var busy = false;
        var next = new Dictionary<(int Pid, DateTime Start), (TimeSpan Cpu, TimeSpan At)>();
        foreach (var (process, threshold) in watched.Values)
        {
            if (TimesOf(process.Pid) is not { } current) continue;
            var key = (process.Pid, current.Start);
            next[key] = (current.Cpu, now);
            if (!_baselines.TryGetValue(key, out var last) || now <= last.At) continue;
            var percent = (current.Cpu - last.Cpu).TotalMilliseconds / (now - last.At).TotalMilliseconds * 100;
            if (busy || percent < threshold) continue;
            busy = true;
            LastBusyProcess = $"{process.Name} (pid {process.Pid})";
        }

        _baselines = next; // processes not seen this sample (exited or no longer watched) are dropped
        return busy;
    }
}
```

`src/Nightcrew/Platform/ToolhelpProcessTable.cs`:

```csharp
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Nightcrew.Core;

namespace Nightcrew.Platform;

public sealed class ToolhelpProcessTable : IProcessTable
{
    private const uint TH32CS_SNAPPROCESS = 0x2;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref PROCESSENTRY32W entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32NextW(IntPtr snapshot, ref PROCESSENTRY32W entry);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    public IReadOnlyList<ProcessEntry> List()
    {
        var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == InvalidHandleValue) return [];
        try
        {
            var result = new List<ProcessEntry>();
            var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            for (var ok = Process32FirstW(snapshot, ref entry); ok; ok = Process32NextW(snapshot, ref entry))
                result.Add(new ProcessEntry((int)entry.th32ProcessID, (int)entry.th32ParentProcessID, NameOf(entry.szExeFile)));
            return result;
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    public bool TryGetTimes(int pid, out DateTime startUtc, out TimeSpan cpu)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            startUtc = process.StartTime.ToUniversalTime();
            cpu = process.TotalProcessorTime;
            return true;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception)
        {
            startUtc = default;
            cpu = default;
            return false;
        }
    }

    private static string NameOf(string exeFile)
    {
        var name = exeFile.ToLowerInvariant();
        return name.EndsWith(".exe", StringComparison.Ordinal) ? name.Substring(0, name.Length - 4) : name;
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test -c Release -- --filter-class Nightcrew.Tests.AgentTreeSignalTests`  Expected: PASS (13 tests)

- [ ] **Step 5: Commit**

```bash
git add src/Nightcrew/Core/AgentTreeSignal.cs src/Nightcrew/Platform/ToolhelpProcessTable.cs tests/Nightcrew.Tests/AgentTreeSignalTests.cs
git commit -m "feat: agent tool-process activity signal"
```

### Task 5: GPU activity signal

**Files:**
- Create: `src/Nightcrew/Core/GpuSignal.cs`
- Create: `src/Nightcrew/Platform/PdhGpuUsage.cs`
- Test: `tests/Nightcrew.Tests/GpuSignalTests.cs`

**Interfaces:**
- Consumes: `Nightcrew.Core.IBusySignal { string Name { get; } bool Sample(TimeSpan now); }` (skeleton, `Core/Contracts.cs`)
- Produces:
  - `namespace Nightcrew.Core; public interface IGpuUsage { IReadOnlyDictionary<int, double> PercentByPid(); }` (pid -> busiest engine utilization %, empty when unavailable)
  - `public sealed class GpuSignal(IGpuUsage gpu, double thresholdPercent) : IBusySignal` with `string Name => "gpu"`, `int? LastBusyPid { get; }` (busiest pid of the last busy sample; kept after the GPU goes quiet) and `bool Sample(TimeSpan now)` (true if any pid >= threshold)
  - `namespace Nightcrew.Platform; public sealed class PdhGpuUsage : IGpuUsage, IDisposable` (parameterless constructor; first call primes and returns empty; never throws; empty forever if the counter can't be added or after `Dispose`)
  - Internal, for tests only: `internal static bool PdhGpuUsage.TryParsePid(string instance, out int pid)` and `internal static Dictionary<int, double> PdhGpuUsage.MaxByPid(IEnumerable<(string Instance, double Percent)> items)`

- [ ] **Step 1: Write the failing tests**

`tests/Nightcrew.Tests/GpuSignalTests.cs`:
```csharp
using Nightcrew.Core;
using Nightcrew.Platform;
using Xunit;

namespace Nightcrew.Tests;

public sealed class GpuSignalTests
{
    private static readonly TimeSpan Now = TimeSpan.FromHours(1);

    private sealed class FakeGpu : IGpuUsage
    {
        public Dictionary<int, double> Values { get; set; } = [];
        public IReadOnlyDictionary<int, double> PercentByPid() => Values;
    }

    [Fact]
    public void Name_is_gpu() => Assert.Equal("gpu", new GpuSignal(new FakeGpu(), 30).Name);

    [Fact]
    public void No_gpu_processes_is_not_busy()
    {
        var signal = new GpuSignal(new FakeGpu(), 30);
        Assert.False(signal.Sample(Now));
        Assert.Null(signal.LastBusyPid);
    }

    [Fact]
    public void Below_threshold_is_not_busy()
    {
        var gpu = new FakeGpu { Values = new() { [10] = 29.9, [20] = 5 } };
        var signal = new GpuSignal(gpu, 30);
        Assert.False(signal.Sample(Now));
        Assert.Null(signal.LastBusyPid);
    }

    [Fact]
    public void At_threshold_is_busy_and_records_the_pid()
    {
        var gpu = new FakeGpu { Values = new() { [10] = 2, [42] = 30 } };
        var signal = new GpuSignal(gpu, 30);
        Assert.True(signal.Sample(Now));
        Assert.Equal(42, signal.LastBusyPid);
    }

    [Fact]
    public void Busiest_pid_is_recorded_when_several_are_busy()
    {
        var gpu = new FakeGpu { Values = new() { [10] = 50, [20] = 90, [30] = 35 } };
        var signal = new GpuSignal(gpu, 30);
        Assert.True(signal.Sample(Now));
        Assert.Equal(20, signal.LastBusyPid);
    }

    [Fact]
    public void Last_busy_pid_is_kept_after_the_gpu_goes_quiet()
    {
        var gpu = new FakeGpu { Values = new() { [7] = 80 } };
        var signal = new GpuSignal(gpu, 30);
        Assert.True(signal.Sample(Now));
        gpu.Values = new() { [7] = 1 };
        Assert.False(signal.Sample(Now + TimeSpan.FromSeconds(10)));
        Assert.Equal(7, signal.LastBusyPid);
    }

    [Theory]
    [InlineData("pid_1234_luid_0x00000000_0x0000C2A1_phys_0_eng_0_engtype_3D", 1234)]
    [InlineData("pid_8_luid_0x00000000_0x0000C2A1_phys_0_eng_3_engtype_VideoDecode", 8)]
    [InlineData("pid_77", 77)]
    public void Parses_the_pid_from_an_instance_name(string instance, int expected)
    {
        Assert.True(PdhGpuUsage.TryParsePid(instance, out var pid));
        Assert.Equal(expected, pid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("_Total")]
    [InlineData("pid__luid_0x0")]
    [InlineData("pid_abc_luid_0x0")]
    [InlineData("pid_-5_luid_0x0")]
    [InlineData("luid_0x0_pid_12")]
    public void Rejects_instance_names_without_a_pid(string instance)
    {
        Assert.False(PdhGpuUsage.TryParsePid(instance, out _));
    }

    [Fact]
    public void Each_pid_gets_its_busiest_engine()
    {
        var result = PdhGpuUsage.MaxByPid(
        [
            ("pid_1_luid_0x0_0x1_phys_0_eng_0_engtype_3D", 12.5),
            ("pid_1_luid_0x0_0x1_phys_0_eng_1_engtype_Copy", 40),
            ("pid_1_luid_0x0_0x1_phys_0_eng_2_engtype_VideoDecode", 7),
            ("pid_2_luid_0x0_0x1_phys_0_eng_0_engtype_3D", 3),
            ("not_a_pid_instance", 99),
        ]);
        Assert.Equal(2, result.Count);
        Assert.Equal(40, result[1]);
        Assert.Equal(3, result[2]);
    }

    [Fact]
    public void Real_counters_prime_first_and_never_throw()
    {
        using var gpu = new PdhGpuUsage();
        Assert.Empty(gpu.PercentByPid());
        Thread.Sleep(250);
        var second = gpu.PercentByPid();
        Assert.All(second.Values, value => Assert.InRange(value, 0.0, 100.0));
        gpu.Dispose();
        Assert.Empty(gpu.PercentByPid());
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test -c Release -- --filter-class Nightcrew.Tests.GpuSignalTests` (after the env line from the plan's Global Constraints)
Expected: build error CS0246, `The type or namespace name 'IGpuUsage' could not be found`

- [ ] **Step 3: Write the implementation**

`src/Nightcrew/Core/GpuSignal.cs`:
```csharp
namespace Nightcrew.Core;

public interface IGpuUsage
{
    /// <summary>Process id to the utilization percent of its busiest GPU engine. Empty when unavailable.</summary>
    IReadOnlyDictionary<int, double> PercentByPid();
}

public sealed class GpuSignal(IGpuUsage gpu, double thresholdPercent) : IBusySignal
{
    public string Name => "gpu";

    /// <summary>The busiest pid of the last busy sample; kept after the GPU goes quiet.</summary>
    public int? LastBusyPid { get; private set; }

    public bool Sample(TimeSpan now)
    {
        int? busiest = null;
        double busiestPercent = 0;
        foreach (var pair in gpu.PercentByPid())
        {
            if (pair.Value >= thresholdPercent && (busiest is null || pair.Value > busiestPercent))
            {
                busiest = pair.Key;
                busiestPercent = pair.Value;
            }
        }
        if (busiest is null) return false;
        LastBusyPid = busiest;
        return true;
    }
}
```

`src/Nightcrew/Platform/PdhGpuUsage.cs`:
```csharp
using System.Globalization;
using System.Runtime.InteropServices;
using Nightcrew.Core;

namespace Nightcrew.Platform;

/// <summary>Per-process GPU utilization from the "GPU Engine" performance counters.</summary>
public sealed class PdhGpuUsage : IGpuUsage, IDisposable
{
    private const string CounterPath = @"\GPU Engine(*)\Utilization Percentage";
    private const uint PDH_FMT_DOUBLE = 0x00000200;
    private const uint PDH_MORE_DATA = 0x800007D2;
    private const uint PDH_CSTATUS_NEW_DATA = 1;

    private static readonly IReadOnlyDictionary<int, double> Empty = new Dictionary<int, double>();

    private IntPtr _query;
    private IntPtr _counter;
    private bool _disabled;

    [StructLayout(LayoutKind.Sequential)]
    private struct PdhFmtCounterValue
    {
        public uint CStatus;
        public double DoubleValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PdhFmtCounterValueItem
    {
        public IntPtr Name;
        public PdhFmtCounterValue FmtValue;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhOpenQueryW")]
    private static extern uint PdhOpenQuery(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhAddEnglishCounterW")]
    private static extern uint PdhAddEnglishCounter(IntPtr query, string counterPath, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhGetFormattedCounterArrayW")]
    private static extern uint PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr itemBuffer);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);

    /// <summary>The first call primes the query and returns nothing. Never throws.</summary>
    public IReadOnlyDictionary<int, double> PercentByPid()
    {
        if (_disabled) return Empty;
        try
        {
            if (_query == IntPtr.Zero)
            {
                _disabled = !Open();
                if (!_disabled) PdhCollectQueryData(_query);
                return Empty;
            }
            return PdhCollectQueryData(_query) == 0 ? MaxByPid(ReadItems()) : Empty;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            _disabled = true;
            return Empty;
        }
    }

    public void Dispose()
    {
        _disabled = true;
        if (_query == IntPtr.Zero) return;
        PdhCloseQuery(_query);
        _query = IntPtr.Zero;
    }

    private bool Open()
    {
        if (PdhOpenQuery(null, IntPtr.Zero, out _query) != 0)
        {
            _query = IntPtr.Zero;
            return false;
        }
        if (PdhAddEnglishCounter(_query, CounterPath, IntPtr.Zero, out _counter) == 0) return true;
        PdhCloseQuery(_query);
        _query = IntPtr.Zero;
        return false;
    }

    private List<(string Instance, double Percent)> ReadItems()
    {
        var items = new List<(string Instance, double Percent)>();
        uint size = 0;
        var status = PdhGetFormattedCounterArray(_counter, PDH_FMT_DOUBLE, ref size, out _, IntPtr.Zero);
        // Instances can appear between the size call and the read, so retry a few times.
        for (var attempt = 0; attempt < 3 && status == PDH_MORE_DATA; attempt++)
        {
            var buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                status = PdhGetFormattedCounterArray(_counter, PDH_FMT_DOUBLE, ref size, out var count, buffer);
                if (status != 0) continue;
                var itemSize = Marshal.SizeOf<PdhFmtCounterValueItem>();
                for (var i = 0; i < count; i++)
                {
                    var item = Marshal.PtrToStructure<PdhFmtCounterValueItem>(buffer + i * itemSize);
                    if (item.FmtValue.CStatus > PDH_CSTATUS_NEW_DATA || item.Name == IntPtr.Zero) continue;
                    items.Add((Marshal.PtrToStringUni(item.Name), item.FmtValue.DoubleValue));
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        return items;
    }

    /// <summary>Keeps, for each pid, the maximum value across its engine instances.</summary>
    internal static Dictionary<int, double> MaxByPid(IEnumerable<(string Instance, double Percent)> items)
    {
        var result = new Dictionary<int, double>();
        foreach (var (instance, percent) in items)
        {
            if (!TryParsePid(instance, out var pid)) continue;
            if (!result.TryGetValue(pid, out var max) || percent > max) result[pid] = percent;
        }
        return result;
    }

    /// <summary>Parses 1234 from instance names like "pid_1234_luid_0x..._engtype_3D".</summary>
    internal static bool TryParsePid(string instance, out int pid)
    {
        pid = 0;
        if (!instance.StartsWith("pid_", StringComparison.Ordinal)) return false;
        var end = instance.IndexOf('_', 4);
        var digits = end < 0 ? instance.Substring(4) : instance.Substring(4, end - 4);
        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out pid);
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test -c Release -- --filter-class Nightcrew.Tests.GpuSignalTests`  Expected: PASS (17 tests)

- [ ] **Step 5: Commit**

```bash
git add src/Nightcrew/Core/GpuSignal.cs src/Nightcrew/Platform/PdhGpuUsage.cs tests/Nightcrew.Tests/GpuSignalTests.cs
git commit -m "feat: GPU activity signal from per-process GPU engine counters"
```

### Task 6: Power policy and restore journal

**Files:**
- Create: `src/Nightcrew/Platform/PowrProfPolicy.cs`
- Create: `src/Nightcrew/Core/RestoreJournal.cs`
- Test: `tests/Nightcrew.Tests/RestoreJournalTests.cs`
- Test: `tests/Nightcrew.Tests/PowrProfPolicyTests.cs`

**Interfaces:**
- Consumes: `PowerValues(uint LidAc, uint LidDc, uint StandbyAc, uint StandbyDc)`, `IPowerPolicy { PowerValues Read(); void Write(PowerValues values); bool CanWrite(out string reason); }`, `ILog { void Info(string message); void Warn(string message); }`, `LidAction`, `Json.Serialize<T>(T)` / `Json.Deserialize<T>(string)`, `AtomicFile.WriteAllText(string path, string text)` (all from the skeleton).
- Produces:
  - `public sealed class PowrProfPolicy : IPowerPolicy` (namespace `Nightcrew.Platform`, parameterless). `Read()` and `Write()` throw `InvalidOperationException("<call> failed (<code>)")` on a non-zero return code; `Write()` also throws on a read-back mismatch. `CanWrite` returns `false` with `reason = "policy blocks lid action AC (<code>)"` (or `lid action DC`, `sleep after AC`, `sleep after DC`), else `true` with `reason = ""`.
  - `[DataContract] public sealed class RestoreRecord` (namespace `Nightcrew.Core`): `uint OwnerLidAc, OwnerLidDc, OwnerStandbyAc, OwnerStandbyDc, WrittenLidAc, WrittenLidDc, WrittenStandbyAc, WrittenStandbyDc` (required members), `DateTime CreatedUtc`, plus non-persisted helpers `PowerValues Owner { get; set; }` and `PowerValues Written { get; set; }`.
  - `public sealed class RestoreJournal(IPowerPolicy policy, string path, ILog log)` (namespace `Nightcrew.Core`): `public static readonly PowerValues ArmValues`, `public bool HasRecord { get; }`, `public PowerValues? OwnerValues { get; }` (null when missing or corrupt; never renames), `public void Arm()` (throws if the policy read or write fails; the record stays so `Restore` can undo a partial write), `public bool Restore()` (never throws).

- [ ] **Step 1: Write the failing tests**

`tests/Nightcrew.Tests/RestoreJournalTests.cs`:
```csharp
using Nightcrew.Core;
using Xunit;

namespace Nightcrew.Tests;

public sealed class RestoreJournalTests : IDisposable
{
    private static readonly PowerValues Owner = new(1, 1, 900, 600);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nightcrew-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakePolicy _policy = new(Owner);
    private readonly ListLog _log = new();

    private string RecordPath => Path.Combine(_dir, "restore.json");

    private RestoreJournal NewJournal() => new(_policy, RecordPath, _log);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Arm_captures_owner_values_then_writes_arm_values()
    {
        var journal = NewJournal();

        journal.Arm();

        Assert.True(journal.HasRecord);
        Assert.Equal(Owner, journal.OwnerValues);
        Assert.Equal(RestoreJournal.ArmValues, _policy.Values);
        Assert.Equal(1, _policy.Writes);
        Assert.False(File.Exists(RecordPath + ".tmp"));
    }

    [Fact]
    public void Rearm_keeps_the_first_owner_values()
    {
        var journal = NewJournal();
        journal.Arm();
        _policy.Values = new PowerValues(2, 2, 60, 60);

        journal.Arm();

        Assert.Equal(Owner, journal.OwnerValues);
        Assert.Equal(RestoreJournal.ArmValues, _policy.Values);
        Assert.Equal(2, _policy.Writes);
    }

    [Fact]
    public void Restore_puts_owner_values_back_and_deletes_the_record()
    {
        NewJournal().Arm();
        var journal = NewJournal();

        Assert.True(journal.Restore());

        Assert.Equal(Owner, _policy.Values);
        Assert.False(journal.HasRecord);
        Assert.Null(journal.OwnerValues);
    }

    [Fact]
    public void Restore_keeps_fields_the_user_changed()
    {
        var journal = NewJournal();
        journal.Arm();
        _policy.Values = new PowerValues(0, 2, 300, 0);

        Assert.True(journal.Restore());

        Assert.Equal(new PowerValues(Owner.LidAc, 2, 300, Owner.StandbyDc), _policy.Values);
    }

    [Fact]
    public void Restore_skips_the_write_when_nothing_is_ours()
    {
        var journal = NewJournal();
        journal.Arm();
        var changed = new PowerValues(3, 3, 120, 120);
        _policy.Values = changed;

        Assert.True(journal.Restore());

        Assert.Equal(changed, _policy.Values);
        Assert.Equal(1, _policy.Writes);
        Assert.False(journal.HasRecord);
    }

    [Fact]
    public void Failed_write_keeps_the_record_for_a_retry()
    {
        var journal = NewJournal();
        journal.Arm();
        _policy.FailWrites = true;

        Assert.False(journal.Restore());

        Assert.True(journal.HasRecord);
        Assert.Equal(Owner, journal.OwnerValues);
        Assert.Single(_log.Warnings);

        _policy.FailWrites = false;
        Assert.True(journal.Restore());
        Assert.Equal(Owner, _policy.Values);
        Assert.False(journal.HasRecord);
    }

    [Fact]
    public void Restore_without_a_record_does_nothing()
    {
        var journal = NewJournal();

        Assert.True(journal.Restore());

        Assert.False(journal.HasRecord);
        Assert.Null(journal.OwnerValues);
        Assert.Equal(0, _policy.Writes);
        Assert.Equal(Owner, _policy.Values);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("")]
    [InlineData("{}")]
    public void Corrupt_record_is_set_aside_and_restore_fails(string text)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(RecordPath, text);
        var journal = NewJournal();

        Assert.Null(journal.OwnerValues);
        Assert.False(journal.Restore());

        Assert.False(journal.HasRecord);
        Assert.True(File.Exists(RecordPath + ".corrupt"));
        Assert.Single(_log.Warnings);
        Assert.Equal(0, _policy.Writes);
    }

    [Fact]
    public void Arm_over_a_corrupt_record_captures_fresh_owner_values()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(RecordPath, "{ not json");
        var journal = NewJournal();

        journal.Arm();

        Assert.True(File.Exists(RecordPath + ".corrupt"));
        Assert.Equal(Owner, journal.OwnerValues);
        Assert.Equal(RestoreJournal.ArmValues, _policy.Values);
        Assert.Single(_log.Warnings);
    }

    private sealed class FakePolicy(PowerValues values) : IPowerPolicy
    {
        public PowerValues Values { get; set; } = values;
        public int Writes { get; private set; }
        public bool FailWrites { get; set; }

        public PowerValues Read() => Values;

        public void Write(PowerValues values)
        {
            if (FailWrites) throw new InvalidOperationException("PowerWriteACValueIndex failed (5)");
            Values = values;
            Writes++;
        }

        public bool CanWrite(out string reason)
        {
            reason = "";
            return true;
        }
    }

    private sealed class ListLog : ILog
    {
        public List<string> Warnings { get; } = [];

        public void Info(string message) { }

        public void Warn(string message) => Warnings.Add(message);
    }
}
```

`tests/Nightcrew.Tests/PowrProfPolicyTests.cs`:
```csharp
using Nightcrew.Core;
using Nightcrew.Platform;
using Xunit;

namespace Nightcrew.Tests;

// Read-only checks against the real machine. Never call Write() here.
public sealed class PowrProfPolicyTests
{
    [Fact]
    public void Read_returns_lid_actions_in_range()
    {
        var values = new PowrProfPolicy().Read();

        Assert.InRange(values.LidAc, (uint)LidAction.DoNothing, (uint)LidAction.ShutDown);
        Assert.InRange(values.LidDc, (uint)LidAction.DoNothing, (uint)LidAction.ShutDown);
    }

    [Fact]
    public void CanWrite_returns_without_throwing()
    {
        var allowed = new PowrProfPolicy().CanWrite(out var reason);

        if (allowed) Assert.Equal("", reason);
        else Assert.StartsWith("policy blocks ", reason);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test -c Release -- --filter-class Nightcrew.Tests.RestoreJournalTests --filter-class Nightcrew.Tests.PowrProfPolicyTests` (after the env line from the plan's Global Constraints)
Expected: build error CS0246 naming the missing type `RestoreJournal` (and `PowrProfPolicy`)

- [ ] **Step 3: Write the implementation**

`src/Nightcrew/Core/RestoreJournal.cs`:
```csharp
using System.Runtime.Serialization;
using Nightcrew.Platform;

namespace Nightcrew.Core;

/// <summary>What the owner had before we armed, and what we wrote over it.</summary>
[DataContract]
public sealed class RestoreRecord
{
    [DataMember(IsRequired = true)] public uint OwnerLidAc { get; set; }
    [DataMember(IsRequired = true)] public uint OwnerLidDc { get; set; }
    [DataMember(IsRequired = true)] public uint OwnerStandbyAc { get; set; }
    [DataMember(IsRequired = true)] public uint OwnerStandbyDc { get; set; }
    [DataMember(IsRequired = true)] public uint WrittenLidAc { get; set; }
    [DataMember(IsRequired = true)] public uint WrittenLidDc { get; set; }
    [DataMember(IsRequired = true)] public uint WrittenStandbyAc { get; set; }
    [DataMember(IsRequired = true)] public uint WrittenStandbyDc { get; set; }
    [DataMember] public DateTime CreatedUtc { get; set; }

    public PowerValues Owner
    {
        get => new(OwnerLidAc, OwnerLidDc, OwnerStandbyAc, OwnerStandbyDc);
        set => (OwnerLidAc, OwnerLidDc, OwnerStandbyAc, OwnerStandbyDc) = (value.LidAc, value.LidDc, value.StandbyAc, value.StandbyDc);
    }

    public PowerValues Written
    {
        get => new(WrittenLidAc, WrittenLidDc, WrittenStandbyAc, WrittenStandbyDc);
        set => (WrittenLidAc, WrittenLidDc, WrittenStandbyAc, WrittenStandbyDc) = (value.LidAc, value.LidDc, value.StandbyAc, value.StandbyDc);
    }
}

public sealed class RestoreJournal(IPowerPolicy policy, string path, ILog log)
{
    /// <summary>Lid action Do nothing and sleep-after Never, on AC and DC.</summary>
    public static readonly PowerValues ArmValues = new(0, 0, 0, 0);

    public bool HasRecord => File.Exists(path);

    /// <summary>The owner's values from the record; null when there is no readable record.</summary>
    public PowerValues? OwnerValues
    {
        get
        {
            try
            {
                return File.Exists(path) ? Parse(File.ReadAllText(path)).Owner : null;
            }
            catch (Exception ex) when (ex is SerializationException or IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    /// <summary>Saves the owner's values once, then writes ArmValues. Throws if reading or writing the policy fails.</summary>
    public void Arm()
    {
        if (Load(out _) is null)
        {
            var owner = policy.Read();
            var record = new RestoreRecord { Owner = owner, Written = ArmValues, CreatedUtc = DateTime.UtcNow };
            AtomicFile.WriteAllText(path, Json.Serialize(record));
            log.Info($"Saved owner power settings {owner}");
        }
        policy.Write(ArmValues);
    }

    /// <summary>Puts back each field we still own, then deletes the record. False keeps the record for a retry.</summary>
    public bool Restore()
    {
        try
        {
            var record = Load(out var corrupt);
            if (record is null) return !corrupt;
            var current = policy.Read();
            var owner = record.Owner;
            var written = record.Written;
            var result = new PowerValues(
                Pick(current.LidAc, written.LidAc, owner.LidAc),
                Pick(current.LidDc, written.LidDc, owner.LidDc),
                Pick(current.StandbyAc, written.StandbyAc, owner.StandbyAc),
                Pick(current.StandbyDc, written.StandbyDc, owner.StandbyDc));
            if (result != current) policy.Write(result);
            File.Delete(path);
            log.Info($"Restored power settings {result}");
            return true;
        }
        catch (Exception ex)
        {
            log.Warn($"Restore failed, record kept: {ex.Message}");
            return false;
        }
    }

    // A field the user changed since we armed is theirs; keep it.
    private static uint Pick(uint current, uint written, uint owner) => current == written ? owner : current;

    private static RestoreRecord Parse(string text) =>
        Json.Deserialize<RestoreRecord?>(text) ?? throw new SerializationException("Restore record is empty.");

    /// <summary>Null when there is no record; a corrupt record is renamed to .corrupt and also gives null.</summary>
    private RestoreRecord? Load(out bool corrupt)
    {
        corrupt = false;
        if (!File.Exists(path)) return null;
        try
        {
            return Parse(File.ReadAllText(path));
        }
        catch (SerializationException ex)
        {
            corrupt = true;
            var aside = path + ".corrupt";
            File.Delete(aside);
            File.Move(path, aside);
            log.Warn($"Restore record was corrupt, moved to {aside}: {ex.Message}");
            return null;
        }
    }
}
```

`src/Nightcrew/Platform/PowrProfPolicy.cs`:
```csharp
using System.Runtime.InteropServices;
using Nightcrew.Core;

namespace Nightcrew.Platform;

/// <summary>Reads and writes the lid action and sleep-after values of the active power scheme.</summary>
public sealed class PowrProfPolicy : IPowerPolicy
{
    private const uint ACCESS_AC_POWER_SETTING_INDEX = 0;
    private const uint ACCESS_DC_POWER_SETTING_INDEX = 1;
    private const uint KEY_WRITE = 0x20006;

    private sealed record Setting(string Name, Guid Subgroup, Guid Id);

    private static readonly Setting Lid = new("lid action",
        new Guid("4f971e89-eebd-4455-a8de-9e59040e7347"), new Guid("5ca83367-6e45-459f-a27b-476b1d01c936"));

    private static readonly Setting Standby = new("sleep after",
        new Guid("238c9fa8-0aad-41ed-83f4-97be242c8f20"), new Guid("29f6c1db-86da-48c5-9fdb-f2b67b1f44da"));

    public PowerValues Read() => ReadValues(ActiveScheme());

    public void Write(PowerValues values)
    {
        var scheme = ActiveScheme();
        WriteIndex(scheme, Lid, ac: true, values.LidAc);
        WriteIndex(scheme, Lid, ac: false, values.LidDc);
        WriteIndex(scheme, Standby, ac: true, values.StandbyAc);
        WriteIndex(scheme, Standby, ac: false, values.StandbyDc);
        Check(PowerSetActiveScheme(IntPtr.Zero, ref scheme), "PowerSetActiveScheme");
        var readBack = ReadValues(scheme);
        if (readBack != values)
            throw new InvalidOperationException($"Power settings read-back mismatch: wrote {values}, read {readBack}");
    }

    public bool CanWrite(out string reason)
    {
        foreach (var setting in new[] { Lid, Standby })
        {
            foreach (var (access, side) in new[] { (ACCESS_AC_POWER_SETTING_INDEX, "AC"), (ACCESS_DC_POWER_SETTING_INDEX, "DC") })
            {
                var id = setting.Id;
                var code = PowerSettingAccessCheckEx(access, ref id, KEY_WRITE);
                if (code != 0)
                {
                    reason = $"policy blocks {setting.Name} {side} ({code})";
                    return false;
                }
            }
        }
        reason = "";
        return true;
    }

    private static Guid ActiveScheme()
    {
        Check(PowerGetActiveScheme(IntPtr.Zero, out var pointer), "PowerGetActiveScheme");
        try
        {
            return Marshal.PtrToStructure<Guid>(pointer);
        }
        finally
        {
            LocalFree(pointer);
        }
    }

    private static PowerValues ReadValues(Guid scheme) => new(
        ReadIndex(scheme, Lid, ac: true),
        ReadIndex(scheme, Lid, ac: false),
        ReadIndex(scheme, Standby, ac: true),
        ReadIndex(scheme, Standby, ac: false));

    private static uint ReadIndex(Guid scheme, Setting setting, bool ac)
    {
        var subgroup = setting.Subgroup;
        var id = setting.Id;
        uint value;
        var code = ac
            ? PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref id, out value)
            : PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref id, out value);
        Check(code, ac ? "PowerReadACValueIndex" : "PowerReadDCValueIndex");
        return value;
    }

    private static void WriteIndex(Guid scheme, Setting setting, bool ac, uint value)
    {
        var subgroup = setting.Subgroup;
        var id = setting.Id;
        var code = ac
            ? PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref id, value)
            : PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref id, value);
        Check(code, ac ? "PowerWriteACValueIndex" : "PowerWriteDCValueIndex");
    }

    private static void Check(uint code, string call)
    {
        if (code != 0) throw new InvalidOperationException($"{call} failed ({code})");
    }

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerSetActiveScheme(IntPtr userRootPowerKey, ref Guid schemeGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadACValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subgroupGuid, ref Guid settingGuid, out uint value);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadDCValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subgroupGuid, ref Guid settingGuid, out uint value);

    [DllImport("powrprof.dll")]
    private static extern uint PowerWriteACValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subgroupGuid, ref Guid settingGuid, uint value);

    [DllImport("powrprof.dll")]
    private static extern uint PowerWriteDCValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subgroupGuid, ref Guid settingGuid, uint value);

    [DllImport("powrprof.dll")]
    private static extern uint PowerSettingAccessCheckEx(uint accessFlags, ref Guid powerGuid, uint accessType);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test -c Release -- --filter-class Nightcrew.Tests.RestoreJournalTests --filter-class Nightcrew.Tests.PowrProfPolicyTests`  Expected: PASS (13 tests)

- [ ] **Step 5: Commit**

```bash
git add src/Nightcrew/Platform/PowrProfPolicy.cs src/Nightcrew/Core/RestoreJournal.cs tests/Nightcrew.Tests/RestoreJournalTests.cs tests/Nightcrew.Tests/PowrProfPolicyTests.cs
git commit -m "feat: powrprof power policy and crash-safe restore journal"
```

### Task 7: Keep-awake request, system power and lid events

**Files:**
- Create: `src/Nightcrew/Platform/PowerBroadcast.cs`
- Create: `src/Nightcrew/Platform/KeepAwake.cs`
- Create: `src/Nightcrew/Platform/SystemPower.cs`
- Create: `src/Nightcrew/Platform/PowerEventsWindow.cs`
- Test: `tests/Nightcrew.Tests/PowerBroadcastTests.cs`

**Interfaces:**
- Consumes: `Nightcrew.Core.IKeepAwake { bool IsHeld { get; } void Hold(); void Release(); }`, `Nightcrew.Core.ISystemPower { PowerSource Source { get; } int? BatteryPercent { get; } TimeSpan IdleTime { get; } bool HibernateAvailable { get; } bool Hibernate(); void MonitorOff(); void LockWorkstation(); }`, `enum PowerSource { AC, DC }`, `enum LidState { Unknown, Open, Closed }` (all from `Core/Contracts.cs`, Task 1).
- Produces:
  - `public sealed class KeepAwake : IKeepAwake, IDisposable` (parameterless; the power request is created on the first `Hold()`)
  - `public sealed class SystemPower(Func<IntPtr> ownWindow) : ISystemPower` (`ownWindow` supplies the hWnd for `MonitorOff`)
  - `public sealed class PowerEventsWindow : NativeWindow, IDisposable { public PowerEventsWindow(); public LidState Lid { get; } public event Action<LidState>? LidChanged; public event Action? Suspending; public event Action? Resumed; public event Action? SessionEnding; public event Action? SessionEndCancelled; public event Action? SessionEnded; }`
  - `public static class PowerBroadcast { public static LidState ParseLid(IntPtr powerBroadcastSetting); }`
  - Notes for wiring (Task 11): `LidChanged` fires only for a parsed Open/Closed (never Unknown); `Lid` stays `Unknown` until Windows delivers the initial lid notification through the message loop, so create the window on the UI thread before `Application.Run`. `SessionEnding` fires on `WM_QUERYENDSESSION` (0x11, answered TRUE). `SessionEndCancelled` fires on `WM_ENDSESSION` (0x16) with wParam FALSE, which means the session end announced by `SessionEnding` was cancelled. `SessionEnded` fires on `WM_ENDSESSION` with wParam TRUE: the session is really ending, and Windows may end the process any time after the handler returns. `WM_ENDSESSION` is answered 0. All events fire on the window's thread.

Safety: the tests only parse an `AllocHGlobal` buffer, check the `SYSTEM_POWER_CAPABILITIES` marshalling layout, and read `Source`, `BatteryPercent`, `IdleTime` and `HibernateAvailable`. They never call `KeepAwake.Hold`, `Hibernate`, `MonitorOff`, `LockWorkstation`, and never create a `PowerEventsWindow`.

- [ ] **Step 1: Write the failing tests**

`tests/Nightcrew.Tests/PowerBroadcastTests.cs`:

```csharp
using System.Runtime.InteropServices;
using Nightcrew.Core;
using Nightcrew.Platform;
using Xunit;

namespace Nightcrew.Tests;

public sealed class PowerBroadcastTests
{
    private static readonly Guid LidSwitch = new("BA3E0F4D-B817-4094-A2D1-D56379E6A0F3");

    // POWERBROADCAST_SETTING: Guid @0, DataLength @16, Data @20.
    private static LidState Parse(Guid setting, int dataLength, int data)
    {
        var buffer = Marshal.AllocHGlobal(24);
        try
        {
            Marshal.Copy(setting.ToByteArray(), 0, buffer, 16);
            Marshal.WriteInt32(buffer, 16, dataLength);
            Marshal.WriteInt32(buffer, 20, data);
            return PowerBroadcast.ParseLid(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [Theory]
    [InlineData(0, LidState.Closed)]
    [InlineData(1, LidState.Open)]
    [InlineData(2, LidState.Unknown)]
    public void Lid_switch_data_maps_to_lid_state(int data, LidState expected)
    {
        Assert.Equal(expected, Parse(LidSwitch, 4, data));
    }

    [Fact]
    public void Other_power_setting_is_unknown()
    {
        Assert.Equal(LidState.Unknown, Parse(Guid.NewGuid(), 4, 0));
    }

    [Fact]
    public void Short_data_is_unknown()
    {
        Assert.Equal(LidState.Unknown, Parse(LidSwitch, 0, 0));
    }

    [Fact]
    public void Null_pointer_is_unknown()
    {
        Assert.Equal(LidState.Unknown, PowerBroadcast.ParseLid(IntPtr.Zero));
    }

    [Fact]
    public void Power_capabilities_layout_matches_winnt()
    {
        Assert.Equal(76, Marshal.SizeOf<SystemPower.SystemPowerCapabilities>());
        Assert.Equal(6, Marshal.OffsetOf<SystemPower.SystemPowerCapabilities>("SystemS4").ToInt32());
        Assert.Equal(8, Marshal.OffsetOf<SystemPower.SystemPowerCapabilities>("HiberFilePresent").ToInt32());
        Assert.Equal(32, Marshal.OffsetOf<SystemPower.SystemPowerCapabilities>("BatteryScale").ToInt32());
        Assert.Equal(72, Marshal.OffsetOf<SystemPower.SystemPowerCapabilities>("DefaultLowLatencyWake").ToInt32());
    }

    [Fact]
    public void System_power_read_only_properties_do_not_throw()
    {
        var power = new SystemPower(() => IntPtr.Zero);
        Assert.True(power.Source is PowerSource.AC or PowerSource.DC);
        Assert.True(power.BatteryPercent is null or (>= 0 and <= 100));
        Assert.True(power.IdleTime >= TimeSpan.Zero);
        _ = power.HibernateAvailable;
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test -c Release -- --filter-class Nightcrew.Tests.PowerBroadcastTests` (after the env line from the plan's Global Constraints)

Expected: build error CS0103 `The name 'PowerBroadcast' does not exist in the current context` and CS0246 for `SystemPower`.

- [ ] **Step 3: Write the implementation**

`src/Nightcrew/Platform/PowerBroadcast.cs`:

```csharp
using System.Runtime.InteropServices;
using Nightcrew.Core;

namespace Nightcrew.Platform;

public static class PowerBroadcast
{
    internal static readonly Guid LidSwitchStateChange = new("BA3E0F4D-B817-4094-A2D1-D56379E6A0F3");

    /// <summary>Reads a POWERBROADCAST_SETTING (Guid @0, DataLength @16, Data @20) for the lid switch.</summary>
    public static LidState ParseLid(IntPtr powerBroadcastSetting)
    {
        if (powerBroadcastSetting == IntPtr.Zero) return LidState.Unknown;
        if (Marshal.PtrToStructure<Guid>(powerBroadcastSetting) != LidSwitchStateChange) return LidState.Unknown;
        if (Marshal.ReadInt32(powerBroadcastSetting, 16) < 4) return LidState.Unknown;
        return Marshal.ReadInt32(powerBroadcastSetting, 20) switch
        {
            0 => LidState.Closed,
            1 => LidState.Open,
            _ => LidState.Unknown,
        };
    }
}
```

`src/Nightcrew/Platform/KeepAwake.cs`:

```csharp
using System.ComponentModel;
using System.Runtime.InteropServices;
using Nightcrew.Core;

namespace Nightcrew.Platform;

public sealed class KeepAwake : IKeepAwake, IDisposable
{
    private const uint POWER_REQUEST_CONTEXT_VERSION = 0;
    private const uint POWER_REQUEST_CONTEXT_SIMPLE_STRING = 0x1;
    private const int PowerRequestSystemRequired = 1;
    private const int PowerRequestExecutionRequired = 3;
    private static readonly IntPtr InvalidHandle = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct REASON_CONTEXT
    {
        public uint Version;
        public uint Flags;
        public string SimpleReasonString;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr PowerCreateRequest(ref REASON_CONTEXT context);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool PowerSetRequest(IntPtr request, int requestType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool PowerClearRequest(IntPtr request, int requestType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    private IntPtr _request = IntPtr.Zero;

    public bool IsHeld { get; private set; }

    public void Hold()
    {
        if (IsHeld) return;
        if (_request == IntPtr.Zero)
        {
            var context = new REASON_CONTEXT
            {
                Version = POWER_REQUEST_CONTEXT_VERSION,
                Flags = POWER_REQUEST_CONTEXT_SIMPLE_STRING,
                SimpleReasonString = "Nightcrew: agents working",
            };
            var request = PowerCreateRequest(ref context);
            if (request == IntPtr.Zero || request == InvalidHandle) throw new Win32Exception();
            _request = request;
        }
        if (!PowerSetRequest(_request, PowerRequestSystemRequired)) throw new Win32Exception();
        if (!PowerSetRequest(_request, PowerRequestExecutionRequired))
        {
            var error = new Win32Exception();
            PowerClearRequest(_request, PowerRequestSystemRequired);
            throw error;
        }
        IsHeld = true;
    }

    public void Release()
    {
        if (!IsHeld) return;
        PowerClearRequest(_request, PowerRequestExecutionRequired);
        PowerClearRequest(_request, PowerRequestSystemRequired);
        IsHeld = false;
    }

    public void Dispose()
    {
        Release();
        if (_request == IntPtr.Zero) return;
        CloseHandle(_request);
        _request = IntPtr.Zero;
    }
}
```

`src/Nightcrew/Platform/SystemPower.cs`:

```csharp
using System.Runtime.InteropServices;
using Nightcrew.Core;

namespace Nightcrew.Platform;

public sealed class SystemPower(Func<IntPtr> ownWindow) : ISystemPower
{
    private const uint WM_SYSCOMMAND = 0x112;
    private const int SC_MONITORPOWER = 0xF170;
    private const int MONITOR_OFF = 2;
    private const uint TOKEN_ADJUST_PRIVILEGES = 0x20;
    private const uint TOKEN_QUERY = 0x8;
    private const uint SE_PRIVILEGE_ENABLED = 0x2;

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    /// <summary>SYSTEM_POWER_CAPABILITIES from winnt.h (76 bytes); BOOLEAN fields are single bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct SystemPowerCapabilities
    {
        public byte PowerButtonPresent, SleepButtonPresent, LidPresent, SystemS1, SystemS2, SystemS3, SystemS4, SystemS5;
        public byte HiberFilePresent, FullWake, VideoDimPresent, ApmPresent, UpsPresent;
        public byte ThermalControl, ProcessorThrottle, ProcessorMinThrottle, ProcessorMaxThrottle;
        public byte FastSystemS4, Hiberboot, WakeAlarmPresent, AoAc, DiskSpinDown, HiberFileType, AoAcConnectivitySupported;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)] public byte[] Spare3;
        public byte SystemBatteriesPresent, BatteriesAreShortTerm;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)] public uint[] BatteryScale; // 3 x {Granularity, Capacity}
        public int AcOnLineWake, SoftLidWake, RtcWake, MinDeviceWakeState, DefaultLowLatencyWake;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES
    {
        public uint PrivilegeCount;
        public LUID Luid;
        public uint Attributes;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

    [DllImport("powrprof.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool GetPwrCapabilities(out SystemPowerCapabilities capabilities);

    [DllImport("powrprof.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool SetSuspendState(
        [MarshalAs(UnmanagedType.U1)] bool hibernate,
        [MarshalAs(UnmanagedType.U1)] bool force,
        [MarshalAs(UnmanagedType.U1)] bool wakeupEventsDisabled);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint desiredAccess, out IntPtr token);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool LookupPrivilegeValue(string? systemName, string name, out LUID luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TOKEN_PRIVILEGES newState,
        uint bufferLength, IntPtr previousState, IntPtr returnLength);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool LockWorkStation();

    public PowerSource Source =>
        GetSystemPowerStatus(out var status) && status.ACLineStatus == 0 ? PowerSource.DC : PowerSource.AC;

    public int? BatteryPercent
    {
        get
        {
            if (!GetSystemPowerStatus(out var status)) return null;
            if (status.BatteryFlag == 128 || status.BatteryLifePercent == 255) return null;
            return status.BatteryLifePercent;
        }
    }

    public TimeSpan IdleTime
    {
        get
        {
            var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
            if (!GetLastInputInfo(ref info)) return TimeSpan.Zero;
            var elapsed = unchecked((uint)Environment.TickCount - info.dwTime);
            return TimeSpan.FromMilliseconds(elapsed);
        }
    }

    public bool HibernateAvailable =>
        GetPwrCapabilities(out var caps) && caps.SystemS4 != 0 && caps.HiberFilePresent != 0;

    public bool Hibernate()
    {
        if (!HibernateAvailable || !EnableShutdownPrivilege()) return false;
        return SetSuspendState(hibernate: true, force: false, wakeupEventsDisabled: false);
    }

    public void MonitorOff() => PostMessage(ownWindow(), WM_SYSCOMMAND, new IntPtr(SC_MONITORPOWER), new IntPtr(MONITOR_OFF));

    public void LockWorkstation() => LockWorkStation();

    private static bool EnableShutdownPrivilege()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out var token)) return false;
        try
        {
            if (!LookupPrivilegeValue(null, "SeShutdownPrivilege", out var luid)) return false;
            var privileges = new TOKEN_PRIVILEGES { PrivilegeCount = 1, Luid = luid, Attributes = SE_PRIVILEGE_ENABLED };
            if (!AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero)) return false;
            return Marshal.GetLastWin32Error() == 0; // ERROR_NOT_ALL_ASSIGNED otherwise
        }
        finally
        {
            CloseHandle(token);
        }
    }
}
```

`src/Nightcrew/Platform/PowerEventsWindow.cs`:

```csharp
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Nightcrew.Core;

namespace Nightcrew.Platform;

/// <summary>Hidden top-level window (not message-only, so it gets broadcasts) for lid, suspend/resume and session-end messages.</summary>
public sealed class PowerEventsWindow : NativeWindow, IDisposable
{
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WM_QUERYENDSESSION = 0x11;
    private const int WM_ENDSESSION = 0x16;
    private const int WM_POWERBROADCAST = 0x218;
    private const long PBT_APMSUSPEND = 0x4;
    private const long PBT_APMRESUMEAUTOMATIC = 0x12;
    private const long PBT_POWERSETTINGCHANGE = 0x8013;
    private const int DEVICE_NOTIFY_WINDOW_HANDLE = 0;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr RegisterPowerSettingNotification(IntPtr recipient, ref Guid powerSettingGuid, int flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterPowerSettingNotification(IntPtr handle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr RegisterSuspendResumeNotification(IntPtr recipient, int flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterSuspendResumeNotification(IntPtr handle);

    private IntPtr _lidNotification;
    private IntPtr _suspendNotification;

    public PowerEventsWindow()
    {
        CreateHandle(new CreateParams { Caption = "Nightcrew power events", Style = WS_POPUP, Parent = IntPtr.Zero });
        var lidSwitch = PowerBroadcast.LidSwitchStateChange;
        _lidNotification = RegisterPowerSettingNotification(Handle, ref lidSwitch, DEVICE_NOTIFY_WINDOW_HANDLE);
        _suspendNotification = RegisterSuspendResumeNotification(Handle, DEVICE_NOTIFY_WINDOW_HANDLE);
    }

    public LidState Lid { get; private set; }

    public event Action<LidState>? LidChanged;
    public event Action? Suspending;
    public event Action? Resumed;
    public event Action? SessionEnding;

    /// <summary>The session end announced by SessionEnding was cancelled (WM_ENDSESSION with wParam FALSE).</summary>
    public event Action? SessionEndCancelled;

    /// <summary>The session is ending for real (WM_ENDSESSION with wParam TRUE); the process may be ended any time after this returns.</summary>
    public event Action? SessionEnded;

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_QUERYENDSESSION)
        {
            SessionEnding?.Invoke();
            m.Result = new IntPtr(1);
            return;
        }
        if (m.Msg == WM_ENDSESSION)
        {
            if (m.WParam == IntPtr.Zero) SessionEndCancelled?.Invoke();
            else SessionEnded?.Invoke();
            m.Result = IntPtr.Zero;
            return;
        }
        if (m.Msg == WM_POWERBROADCAST)
        {
            switch ((long)m.WParam)
            {
                case PBT_POWERSETTINGCHANGE:
                    var lid = PowerBroadcast.ParseLid(m.LParam);
                    if (lid != LidState.Unknown)
                    {
                        Lid = lid;
                        LidChanged?.Invoke(lid);
                    }
                    break;
                case PBT_APMSUSPEND:
                    Suspending?.Invoke();
                    break;
                case PBT_APMRESUMEAUTOMATIC:
                    Resumed?.Invoke();
                    break;
            }
            m.Result = new IntPtr(1);
            return;
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (_lidNotification != IntPtr.Zero)
        {
            UnregisterPowerSettingNotification(_lidNotification);
            _lidNotification = IntPtr.Zero;
        }
        if (_suspendNotification != IntPtr.Zero)
        {
            UnregisterSuspendResumeNotification(_suspendNotification);
            _suspendNotification = IntPtr.Zero;
        }
        DestroyHandle();
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test -c Release -- --filter-class Nightcrew.Tests.PowerBroadcastTests`  Expected: PASS (8 tests). Then `dotnet build -c Release` reports 0 warnings and 0 errors.

- [ ] **Step 5: Commit**

```bash
git add src/Nightcrew/Platform/PowerBroadcast.cs src/Nightcrew/Platform/KeepAwake.cs src/Nightcrew/Platform/SystemPower.cs src/Nightcrew/Platform/PowerEventsWindow.cs tests/Nightcrew.Tests/PowerBroadcastTests.cs
git commit -m "feat: keep-awake request, system power and lid events"
```

### Task 8: Controller state machine

**Files:**
- Create: `src/Nightcrew/Core/Controller.cs`
- Test: `tests/Nightcrew.Tests/ControllerTests.cs`

**Interfaces:**
- Consumes: `Nightcrew.Core.Settings` (`LockOnLidClose`, `BatteryFloorPercent`, `LidClosedCapHours`, `AwayMinutes`); `enum LidState { Unknown, Open, Closed }`; `enum PowerSource { AC, DC }`; `enum LidAction : uint { DoNothing = 0, Sleep = 1, Hibernate = 2, ShutDown = 3 }`; test helper `FakeClock` (`TimeSpan Now`, `void Advance(TimeSpan by)`).
- Produces:
  - `public sealed record Inputs(bool Live, LidState Lid, PowerSource Source, int? BatteryPercent, TimeSpan IdleTime, bool Paused, bool CanWrite, LidAction OwnerLid);`
  - `public enum Mode { Idle, Armed, Finishing, Paused }`
  - `public enum Command { Arm, Disarm, Lock, MonitorOff, Hibernate }`
  - `public sealed class Controller(Settings settings)` with `Mode Mode { get; }`, `bool Spent { get; }`, `TimeSpan LidClosedFor { get; }`, `string Status { get; }`, `IReadOnlyList<Command> Step(Inputs inputs, TimeSpan now)`, `void OnResume()`, `void Reset()` (Mode becomes Idle; `Spent` and `LidClosedFor` are kept; no commands. Task 9 calls it after a failed Arm, Task 11 at session end, so a later `Step` can arm again).
  - Commands are emitted in order and the engine runs them in that order (for example `Lock Disarm`, `Disarm Hibernate`). `Status` strings: `Paused`; `Live`; `Live — lid closed 1h02, when done: sleep|hibernate|do nothing` while unattended (ShutDown reads "hibernate", because that is what the controller does); `Live — lid closed, when done: sleep|hibernate|do nothing` while the lid is closed but not yet unattended (there was input within the away time); `Done — waiting until you're away`; `Done — screen off`; `Idle — limit reached, open the lid to reset`; `Idle — can't change power settings`; `Idle`.

The controller is pure: no clock, no I/O. `now` is the unbiased monotonic time the engine passes in. Each `Step` applies rule 1 (pause), then rule 2 (lid open clears `Spent` and `LidClosedFor`), then exactly one of rules 3/4/5, chosen by the mode at that point, so a step that changes mode does not also run the next mode's rule. **Unattended** means `Lid == Closed && IdleTime >= AwayMinutes`: the lid is closed and there has been no keyboard or mouse input for the away time. A docked laptop with its lid closed that someone is using is not unattended. Two things happen only while unattended: the `Lock` emitted with `Arm` when arming from Idle with the lid already Closed (the `Lock` on the lid-closing edge while Armed does not depend on it), and the lid-closed cap with its `LidClosedFor` count. The battery floor does not depend on input: it fires on any Armed step with the lid Closed, `Source == DC`, a known `BatteryPercent`, `BatteryFloorPercent > 0` and the battery at or below the floor, so a docked laptop that loses mains power while in use still hibernates at the floor. `LidClosedFor` grows by `now - previous Step's now` on every Armed step that is unattended (the arming step itself adds nothing) and is still reset to zero whenever the lid is Open. An Unknown lid is never Closed, so it never locks, finishes, or hits the floor or cap. The Finishing stage (`WaitAway` or `MonitorOffSent`) is private.

- [ ] **Step 1: Write the failing tests**

`tests/Nightcrew.Tests/ControllerTests.cs`:
```csharp
using Nightcrew.Core;
using Xunit;

namespace Nightcrew.Tests;

public sealed class ControllerTests
{
    private const LidState Open = LidState.Open;
    private const LidState Closed = LidState.Closed;
    private const LidState Unknown = LidState.Unknown;

    // Rule 1: paused

    [Fact]
    public void Pause_while_armed_disarms_once()
    {
        var rig = ArmedRig(Open);
        Assert.Equal("Disarm", rig.Step(In(paused: true)));
        Assert.Equal(Mode.Paused, rig.Mode);
        Assert.Equal("", rig.Step(In(paused: true)));
    }

    [Fact]
    public void Pause_while_idle_or_finishing_emits_nothing()
    {
        var idle = new Rig();
        Assert.Equal("", idle.Step(In(live: false, paused: true)));
        Assert.Equal(Mode.Paused, idle.Mode);

        var finishing = FinishingRig(LidAction.Sleep);
        Assert.Equal("", finishing.Step(In(live: false, lid: Closed, paused: true)));
        Assert.Equal(Mode.Paused, finishing.Mode);
    }

    [Theory]
    [InlineData(true, "Arm", Mode.Armed)]
    [InlineData(false, "", Mode.Idle)]
    public void Unpause_goes_idle_and_continues_in_the_same_step(bool live, string expected, Mode mode)
    {
        var rig = new Rig();
        rig.Step(In(paused: true));
        Assert.Equal(expected, rig.Step(In(live: live)));
        Assert.Equal(mode, rig.Mode);
    }

    // Rule 2: lid open

    [Fact]
    public void Lid_open_clears_spent_and_lid_closed_time()
    {
        var rig = SpentRig();
        Assert.True(rig.Controller.Spent);
        Assert.Equal(TimeSpan.FromMinutes(10), rig.Controller.LidClosedFor);

        rig.Step(In(live: false, lid: Open));
        Assert.False(rig.Controller.Spent);
        Assert.Equal(TimeSpan.Zero, rig.Controller.LidClosedFor);
    }

    [Fact]
    public void Unknown_lid_does_not_clear_spent()
    {
        var rig = SpentRig();
        Assert.Equal("", rig.Step(In(lid: Unknown)));
        Assert.True(rig.Controller.Spent);
        Assert.Equal(Mode.Idle, rig.Mode);
    }

    // Rule 3: idle

    [Theory]
    [InlineData(false, Open, 5, true, true, "", Mode.Idle)]
    [InlineData(false, Closed, 5, true, true, "", Mode.Idle)]
    [InlineData(true, Open, 5, true, true, "Arm", Mode.Armed)]
    [InlineData(true, Unknown, 5, true, true, "Arm", Mode.Armed)]
    [InlineData(true, Closed, 5, true, true, "Arm Lock", Mode.Armed)]
    [InlineData(true, Closed, 4.9, true, true, "Arm", Mode.Armed)]
    [InlineData(true, Closed, 5, true, false, "Arm", Mode.Armed)]
    [InlineData(true, Open, 5, false, true, "", Mode.Idle)]
    [InlineData(true, Closed, 5, false, true, "", Mode.Idle)]
    public void Idle_arms_when_live_and_writable(bool live, LidState lid, double idleMinutes, bool canWrite, bool lockOnLidClose, string expected, Mode mode)
    {
        var rig = new Rig(new Settings { LockOnLidClose = lockOnLidClose });
        Assert.Equal(expected, rig.Step(In(live: live, lid: lid, idleMinutes: idleMinutes, canWrite: canWrite)));
        Assert.Equal(mode, rig.Mode);
    }

    [Fact]
    public void Spent_blocks_arming_until_the_lid_opens()
    {
        var rig = SpentRig();
        Assert.Equal("", rig.Step(In(lid: Closed)));
        Assert.Equal(Mode.Idle, rig.Mode);
        Assert.Equal("Arm", rig.Step(In(lid: Open)));
        Assert.Equal(Mode.Armed, rig.Mode);
    }

    [Fact]
    public void Wake_with_the_lid_closed_arms_and_locks()
    {
        var rig = new Rig();
        Assert.Equal("", rig.Step(Away(live: false)));
        Assert.Equal("Arm Lock", rig.Step(Away()));
        Assert.Equal(Mode.Armed, rig.Mode);
    }

    // Rule 4: armed

    [Theory]
    [InlineData(Open, true, "Lock")]
    [InlineData(Unknown, true, "Lock")]
    [InlineData(Open, false, "")]
    public void Armed_locks_only_on_the_lid_closing_edge(LidState armedWith, bool lockOnLidClose, string expected)
    {
        var rig = ArmedRig(armedWith, new Settings { LockOnLidClose = lockOnLidClose });
        Assert.Equal(expected, rig.Step(In(lid: Closed)));
        Assert.Equal("", rig.Step(In(lid: Closed)));
        Assert.Equal("", rig.Step(In(lid: Open)));
        Assert.Equal(expected, rig.Step(In(lid: Closed)));
    }

    [Fact]
    public void Lid_closed_time_counts_only_while_armed_and_unattended()
    {
        var rig = ArmedRig(Closed);
        Assert.Equal(TimeSpan.Zero, rig.Controller.LidClosedFor);    // the arming step adds nothing

        rig.Step(Away(), afterMinutes: 10);
        rig.Step(Away(), afterMinutes: 5);
        Assert.Equal(TimeSpan.FromMinutes(15), rig.Controller.LidClosedFor);

        rig.Step(In(lid: Closed, idleMinutes: 1), afterMinutes: 20); // in use with the lid closed: not counted
        Assert.Equal(TimeSpan.FromMinutes(15), rig.Controller.LidClosedFor);

        rig.Step(Away(live: false), afterMinutes: 1);                // still Armed during this step
        rig.Step(Away(live: false), afterMinutes: 30);               // Finishing: not counted
        Assert.Equal(Mode.Finishing, rig.Mode);
        Assert.Equal(TimeSpan.FromMinutes(16), rig.Controller.LidClosedFor);

        rig.Step(Away(), afterMinutes: 1);                           // re-arms: not counted
        rig.Step(Away(), afterMinutes: 2);
        Assert.Equal(TimeSpan.FromMinutes(18), rig.Controller.LidClosedFor);

        rig.Step(In(lid: Open), afterMinutes: 1);
        Assert.Equal(TimeSpan.Zero, rig.Controller.LidClosedFor);
        Assert.Equal(Mode.Armed, rig.Mode);
    }

    [Theory]
    [InlineData(PowerSource.DC, 20, 20, Closed, 5, true)]
    [InlineData(PowerSource.DC, 5, 20, Closed, 5, true)]
    [InlineData(PowerSource.DC, 21, 20, Closed, 5, false)]
    [InlineData(PowerSource.AC, 5, 20, Closed, 5, false)]
    [InlineData(PowerSource.DC, null, 20, Closed, 5, false)]
    [InlineData(PowerSource.DC, 5, 0, Closed, 5, false)]
    [InlineData(PowerSource.DC, 5, 20, Closed, 0, true)]
    [InlineData(PowerSource.DC, 5, 20, Open, 5, false)]
    [InlineData(PowerSource.DC, 5, 20, Unknown, 5, false)]
    public void Battery_floor_hibernates_when_the_lid_is_closed_on_battery(PowerSource source, int? battery, int floor, LidState lid, double idleMinutes, bool hibernates)
    {
        var rig = ArmedRig(lid, new Settings { BatteryFloorPercent = floor });
        Assert.Equal(hibernates ? "Disarm Hibernate" : "", rig.Step(In(lid: lid, source: source, battery: battery, idleMinutes: idleMinutes)));
        Assert.Equal(hibernates, rig.Controller.Spent);
        Assert.Equal(hibernates ? Mode.Idle : Mode.Armed, rig.Mode);
    }

    [Fact]
    public void Battery_floor_applies_while_the_closed_laptop_is_in_use()
    {
        var rig = new Rig();
        Assert.Equal("Arm", rig.Step(In(lid: Closed, idleMinutes: 0.1)));
        Assert.Equal("", rig.Step(In(lid: Closed, source: PowerSource.DC, battery: 25, idleMinutes: 0.1)));
        Assert.Equal("Disarm Hibernate", rig.Step(In(lid: Closed, source: PowerSource.DC, battery: 15, idleMinutes: 0.1)));
        Assert.True(rig.Controller.Spent);
        Assert.Equal(Mode.Idle, rig.Mode);
    }

    [Fact]
    public void Lid_closed_cap_hibernates_at_the_limit()
    {
        var rig = ArmedRig(Closed, new Settings { LidClosedCapHours = 2 });
        Assert.Equal("", rig.Step(Away(), afterMinutes: 119));
        Assert.Equal("Disarm Hibernate", rig.Step(Away(), afterMinutes: 1));
        Assert.True(rig.Controller.Spent);
        Assert.Equal(Mode.Idle, rig.Mode);
    }

    [Fact]
    public void Lid_closed_cap_of_zero_is_off()
    {
        var rig = ArmedRig(Closed, new Settings { LidClosedCapHours = 0 });
        Assert.Equal("", rig.Step(Away(), afterMinutes: 48 * 60));
        Assert.Equal(Mode.Armed, rig.Mode);
    }

    [Fact]
    public void Lid_closed_cap_ignores_time_with_the_lid_open()
    {
        var rig = ArmedRig(Open, new Settings { LidClosedCapHours = 2 });
        Assert.Equal("", rig.Step(In(lid: Open, idleMinutes: 5), afterMinutes: 3 * 60)); // one long step without input
        for (var minute = 0; minute < 180; minute++)
            Assert.Equal("", rig.Step(In(lid: Open, idleMinutes: 6 + minute), afterMinutes: 1));
        Assert.Equal(TimeSpan.Zero, rig.Controller.LidClosedFor);
        Assert.False(rig.Controller.Spent);
        Assert.Equal(Mode.Armed, rig.Mode);
    }

    [Fact]
    public void Docked_and_in_use_gets_no_lock_cap_or_lid_closed_time()
    {
        var rig = new Rig(new Settings { LidClosedCapHours = 2 });
        Assert.Equal("Arm", rig.Step(In(lid: Closed, idleMinutes: 1)));
        for (var minute = 0; minute < 180; minute++)
            Assert.Equal("", rig.Step(In(lid: Closed, idleMinutes: 4.9), afterMinutes: 1));
        Assert.Equal(TimeSpan.Zero, rig.Controller.LidClosedFor);
        Assert.False(rig.Controller.Spent);
        Assert.Equal(Mode.Armed, rig.Mode);

        // Walking away makes it unattended: lid-closed time counts from here, and the cap applies.
        for (var minute = 1; minute < 120; minute++)
            Assert.Equal("", rig.Step(Away(), afterMinutes: 1));
        Assert.Equal("Disarm Hibernate", rig.Step(Away(), afterMinutes: 1));
        Assert.True(rig.Controller.Spent);
    }

    [Theory]
    [InlineData(Open, Mode.Idle)]
    [InlineData(Unknown, Mode.Idle)]
    [InlineData(Closed, Mode.Finishing)]
    public void Armed_disarms_when_work_stops(LidState lid, Mode mode)
    {
        var rig = ArmedRig(lid);
        Assert.Equal("Disarm", rig.Step(In(live: false, lid: lid)));
        Assert.Equal(mode, rig.Mode);
    }

    [Fact]
    public void Floor_wins_over_work_stopping()
    {
        var rig = ArmedRig(Closed);
        Assert.Equal("Disarm Hibernate", rig.Step(Away(live: false, source: PowerSource.DC, battery: 10)));
        Assert.Equal(Mode.Idle, rig.Mode);
    }

    [Fact]
    public void Lid_closing_as_work_stops_locks_then_disarms()
    {
        var rig = ArmedRig(Open);
        Assert.Equal("Lock Disarm", rig.Step(In(live: false, lid: Closed)));
        Assert.Equal(Mode.Finishing, rig.Mode);
    }

    [Fact]
    public void Unknown_lid_never_locks_or_finishes()
    {
        var rig = new Rig(new Settings { LidClosedCapHours = 2 });
        Assert.Equal("Arm", rig.Step(In(lid: Unknown, idleMinutes: 30)));
        Assert.Equal(Mode.Armed, rig.Mode);

        for (var minute = 0; minute < 180; minute++)
            Assert.Equal("", rig.Step(In(lid: Unknown, source: PowerSource.DC, battery: 5, idleMinutes: 30), afterMinutes: 1));
        Assert.Equal(TimeSpan.Zero, rig.Controller.LidClosedFor);
        Assert.False(rig.Controller.Spent);

        Assert.Equal("Disarm", rig.Step(In(live: false, lid: Unknown, idleMinutes: 30)));
        Assert.Equal(Mode.Idle, rig.Mode);
        for (var minute = 0; minute < 30; minute++)
            Assert.Equal("", rig.Step(In(live: false, lid: Unknown, idleMinutes: 31 + minute), afterMinutes: 1));
    }

    // Rule 5: finishing

    [Theory]
    [InlineData(Open)]
    [InlineData(Unknown)]
    public void Finishing_goes_idle_when_the_lid_is_not_closed(LidState lid)
    {
        var rig = FinishingRig(LidAction.Sleep);
        Assert.Equal("", rig.Step(In(live: false, lid: lid, idleMinutes: 30)));
        Assert.Equal(Mode.Idle, rig.Mode);
    }

    [Fact]
    public void Finishing_rearms_when_work_resumes()
    {
        var rig = FinishingRig(LidAction.Sleep);
        Assert.Equal("Arm", rig.Step(In(lid: Closed, idleMinutes: 30)));
        Assert.Equal(Mode.Armed, rig.Mode);
    }

    [Fact]
    public void Finishing_does_not_rearm_without_write_access()
    {
        var rig = FinishingRig(LidAction.DoNothing);
        Assert.Equal("", rig.Step(In(lid: Closed, canWrite: false, idleMinutes: 1)));
        Assert.Equal(Mode.Finishing, rig.Mode);
    }

    [Theory]
    [InlineData(LidAction.Sleep, "MonitorOff", Mode.Finishing)]
    [InlineData(LidAction.Hibernate, "Hibernate", Mode.Idle)]
    [InlineData(LidAction.ShutDown, "Hibernate", Mode.Idle)]
    [InlineData(LidAction.DoNothing, "", Mode.Idle)]
    public void Finishing_acts_on_the_owner_lid_action_once_away(LidAction owner, string expected, Mode mode)
    {
        var rig = FinishingRig(owner);
        Assert.Equal("", rig.Step(Done(owner, idleMinutes: 4.9)));
        Assert.Equal(Mode.Finishing, rig.Mode);
        Assert.Equal(expected, rig.Step(Done(owner, idleMinutes: 5)));
        Assert.Equal(mode, rig.Mode);
    }

    [Fact]
    public void Monitor_off_falls_back_to_hibernate_after_five_minutes()
    {
        var rig = FinishingRig(LidAction.Sleep);
        Assert.Equal("MonitorOff", rig.Step(Done(LidAction.Sleep, idleMinutes: 5)));
        Assert.Equal("", rig.Step(Done(LidAction.Sleep, idleMinutes: 9), afterMinutes: 4));
        Assert.Equal("Hibernate", rig.Step(Done(LidAction.Sleep, idleMinutes: 10), afterMinutes: 1));
        Assert.Equal(Mode.Idle, rig.Mode);
    }

    [Fact]
    public void Input_after_monitor_off_goes_back_to_waiting()
    {
        var rig = FinishingRig(LidAction.Sleep);
        Assert.Equal("MonitorOff", rig.Step(Done(LidAction.Sleep, idleMinutes: 5)));
        Assert.Equal("", rig.Step(Done(LidAction.Sleep, idleMinutes: 0.5), afterMinutes: 4));  // user input
        Assert.Equal("", rig.Step(Done(LidAction.Sleep, idleMinutes: 2), afterMinutes: 2));    // 6 min after: no hibernate
        Assert.Equal(Mode.Finishing, rig.Mode);
        Assert.Equal("MonitorOff", rig.Step(Done(LidAction.Sleep, idleMinutes: 5), afterMinutes: 3));
    }

    [Fact]
    public void Do_nothing_owner_never_hibernates_when_work_is_done()
    {
        var rig = FinishingRig(LidAction.DoNothing);
        for (var minute = 0; minute < 120; minute++)
            Assert.DoesNotContain("Hibernate", rig.Step(Done(LidAction.DoNothing, idleMinutes: minute), afterMinutes: 1));
        Assert.Equal(Mode.Idle, rig.Mode);
    }

    // Rule 6: resume

    [Fact]
    public void Resume_during_finishing_goes_idle_and_never_hibernates_later()
    {
        var rig = FinishingRig(LidAction.Sleep);
        Assert.Equal("MonitorOff", rig.Step(Done(LidAction.Sleep, idleMinutes: 5)));
        rig.Controller.OnResume();
        Assert.Equal(Mode.Idle, rig.Mode);

        for (var minute = 0; minute < 60; minute++)
            Assert.Equal("", rig.Step(Done(LidAction.Sleep, idleMinutes: 6 + minute), afterMinutes: 1));
        Assert.Equal(Mode.Idle, rig.Mode);
    }

    [Fact]
    public void Resume_leaves_other_modes_alone()
    {
        var rig = ArmedRig(Closed);
        rig.Controller.OnResume();
        Assert.Equal(Mode.Armed, rig.Mode);
    }

    // Reset

    [Fact]
    public void Reset_goes_idle_keeps_spent_and_lid_closed_time_and_the_next_step_rearms()
    {
        var rig = ArmedRig(Closed);
        rig.Step(Away(), afterMinutes: 10);
        rig.Controller.Reset();
        Assert.Equal(Mode.Idle, rig.Mode);
        Assert.Equal(TimeSpan.FromMinutes(10), rig.Controller.LidClosedFor);
        Assert.Equal("Arm Lock", rig.Step(Away()));
        Assert.Equal(Mode.Armed, rig.Mode);

        var spent = SpentRig();
        spent.Controller.Reset();
        Assert.Equal(Mode.Idle, spent.Mode);
        Assert.True(spent.Controller.Spent);
        Assert.Equal(TimeSpan.FromMinutes(10), spent.Controller.LidClosedFor);
    }

    // Status

    [Fact]
    public void Status_follows_the_mode()
    {
        var rig = new Rig();
        Assert.Equal("Idle", rig.Controller.Status);

        rig.Step(In(lid: Open));
        Assert.Equal("Live", rig.Controller.Status);

        rig.Step(Away(), afterMinutes: 62);
        rig.Step(Away(), afterMinutes: 62);
        Assert.Equal("Live — lid closed 2h04, when done: sleep", rig.Controller.Status);

        rig.Step(In(lid: Closed, idleMinutes: 1));
        Assert.Equal("Live — lid closed, when done: sleep", rig.Controller.Status);

        rig.Step(Done(LidAction.Sleep, idleMinutes: 0));
        Assert.Equal("Done — waiting until you're away", rig.Controller.Status);

        rig.Step(Done(LidAction.Sleep, idleMinutes: 5));
        Assert.Equal("Done — screen off", rig.Controller.Status);

        rig.Step(In(paused: true));
        Assert.Equal("Paused", rig.Controller.Status);
    }

    [Theory]
    [InlineData(LidAction.Sleep, "sleep")]
    [InlineData(LidAction.Hibernate, "hibernate")]
    [InlineData(LidAction.ShutDown, "hibernate")]
    [InlineData(LidAction.DoNothing, "do nothing")]
    public void Status_names_what_happens_when_done(LidAction owner, string words)
    {
        var rig = ArmedRig(Closed);
        rig.Step(Away(owner: owner), afterMinutes: 5);
        Assert.Equal($"Live — lid closed 0h05, when done: {words}", rig.Controller.Status);
    }

    [Fact]
    public void Status_explains_why_idle_will_not_arm()
    {
        var spent = SpentRig();
        Assert.Equal("Idle — limit reached, open the lid to reset", spent.Controller.Status);

        var blocked = new Rig();
        blocked.Step(In(canWrite: false));
        Assert.Equal("Idle — can't change power settings", blocked.Controller.Status);
    }

    // Helpers

    private static Inputs In(bool live = true, LidState lid = Open, PowerSource source = PowerSource.AC, int? battery = null,
        double idleMinutes = 0, bool paused = false, bool canWrite = true, LidAction owner = LidAction.Sleep) =>
        new(live, lid, source, battery, TimeSpan.FromMinutes(idleMinutes), paused, canWrite, owner);

    /// <summary>Unattended: the lid is closed and there has been no input for the default 5 away minutes.</summary>
    private static Inputs Away(bool live = true, PowerSource source = PowerSource.AC, int? battery = null, LidAction owner = LidAction.Sleep) =>
        In(live: live, lid: Closed, source: source, battery: battery, idleMinutes: 5, owner: owner);

    /// <summary>Work is done and the lid is still closed.</summary>
    private static Inputs Done(LidAction owner, double idleMinutes) =>
        In(live: false, lid: Closed, idleMinutes: idleMinutes, owner: owner);

    private static Rig ArmedRig(LidState lid, Settings? settings = null)
    {
        var rig = new Rig(settings);
        rig.Step(In(lid: lid));
        Assert.Equal(Mode.Armed, rig.Mode);
        return rig;
    }

    private static Rig FinishingRig(LidAction owner)
    {
        var rig = ArmedRig(Closed);
        Assert.Equal("Disarm", rig.Step(Done(owner, idleMinutes: 0)));
        Assert.Equal(Mode.Finishing, rig.Mode);
        return rig;
    }

    /// <summary>Armed with the lid closed, unattended for 10 minutes, then the battery floor hibernated.</summary>
    private static Rig SpentRig()
    {
        var rig = ArmedRig(Closed);
        Assert.Equal("Disarm Hibernate", rig.Step(Away(source: PowerSource.DC, battery: 15), afterMinutes: 10));
        return rig;
    }

    private sealed class Rig(Settings? settings = null)
    {
        private readonly FakeClock _clock = new();

        public Controller Controller { get; } = new(settings ?? new Settings());

        public Mode Mode => Controller.Mode;

        /// <summary>Advances the clock, steps the controller and returns the commands joined by spaces.</summary>
        public string Step(Inputs inputs, double afterMinutes = 0.5)
        {
            _clock.Advance(TimeSpan.FromMinutes(afterMinutes));
            return string.Join(" ", Controller.Step(inputs, _clock.Now));
        }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test -c Release -- --filter-class Nightcrew.Tests.ControllerTests` (after the env line from the plan's Global Constraints: `export DOTNET_ROOT="$LOCALAPPDATA/Microsoft/dotnet-sdk"; export PATH="$DOTNET_ROOT:$PATH"; export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1`)
Expected: build error `CS0246: The type or namespace name 'Mode' could not be found` (also `Inputs`, `Controller`)

- [ ] **Step 3: Write the implementation**

`src/Nightcrew/Core/Controller.cs`:
```csharp
namespace Nightcrew.Core;

/// <summary>Everything the controller needs to know for one step.</summary>
public sealed record Inputs(bool Live, LidState Lid, PowerSource Source, int? BatteryPercent, TimeSpan IdleTime, bool Paused, bool CanWrite, LidAction OwnerLid);

public enum Mode { Idle, Armed, Finishing, Paused }

public enum Command { Arm, Disarm, Lock, MonitorOff, Hibernate }

/// <summary>Pure state machine: turns inputs into commands. The engine carries the commands out.</summary>
public sealed class Controller(Settings settings)
{
    private enum Stage { WaitAway, MonitorOffSent }

    private Stage _stage;
    private TimeSpan _sentAt;
    private TimeSpan _lastNow;
    private LidState _lastLid;
    private Inputs? _last;

    public Mode Mode { get; private set; }

    /// <summary>Set by a floor or cap hibernate; cleared when the lid is seen Open.</summary>
    public bool Spent { get; private set; }

    /// <summary>Accumulated while Armed and unattended; reset when the lid is Open.</summary>
    public TimeSpan LidClosedFor { get; private set; }

    public string Status => Describe();

    public IReadOnlyList<Command> Step(Inputs inputs, TimeSpan now)
    {
        var commands = new List<Command>();
        var previousNow = _lastNow;
        var previousLid = _lastLid;
        _lastNow = now;
        _lastLid = inputs.Lid;
        _last = inputs;

        if (inputs.Paused)
        {
            if (Mode == Mode.Armed) commands.Add(Command.Disarm);
            Mode = Mode.Paused;
            return commands;
        }
        if (Mode == Mode.Paused) Mode = Mode.Idle;

        if (inputs.Lid == LidState.Open)
        {
            Spent = false;
            LidClosedFor = TimeSpan.Zero;
        }

        switch (Mode)
        {
            case Mode.Idle:
                StepIdle(inputs, commands);
                break;
            case Mode.Armed:
                StepArmed(inputs, now - previousNow, previousLid, commands);
                break;
            case Mode.Finishing:
                StepFinishing(inputs, now, commands);
                break;
        }
        return commands;
    }

    /// <summary>The machine woke up: whatever we were finishing is over.</summary>
    public void OnResume()
    {
        if (Mode == Mode.Finishing) Mode = Mode.Idle;
    }

    /// <summary>Back to Idle, keeping Spent and LidClosedFor, so the next step can arm again (a failed arm, or session end).</summary>
    public void Reset() => Mode = Mode.Idle;

    /// <summary>Lid closed and no keyboard or mouse input for AwayMinutes. A docked laptop in use is not unattended.</summary>
    private bool Unattended(Inputs inputs) =>
        inputs.Lid == LidState.Closed && inputs.IdleTime >= TimeSpan.FromMinutes(settings.AwayMinutes);

    private void StepIdle(Inputs inputs, List<Command> commands)
    {
        if (!inputs.Live || Spent || !inputs.CanWrite) return;
        commands.Add(Command.Arm);
        if (Unattended(inputs) && settings.LockOnLidClose) commands.Add(Command.Lock);
        Mode = Mode.Armed;
    }

    private void StepArmed(Inputs inputs, TimeSpan sinceLastStep, LidState previousLid, List<Command> commands)
    {
        var closed = inputs.Lid == LidState.Closed;
        var unattended = Unattended(inputs);
        if (closed && previousLid != LidState.Closed && settings.LockOnLidClose) commands.Add(Command.Lock);
        if (unattended) LidClosedFor += sinceLastStep;

        // The floor protects the battery whenever the lid is closed, in use or not; the cap counts only unattended time.
        var floorHit = closed && inputs.Source == PowerSource.DC && inputs.BatteryPercent is { } battery
            && settings.BatteryFloorPercent > 0 && battery <= settings.BatteryFloorPercent;
        var capHit = unattended && settings.LidClosedCapHours > 0 && LidClosedFor >= TimeSpan.FromHours(settings.LidClosedCapHours);

        if (floorHit || capHit)
        {
            commands.Add(Command.Disarm);
            commands.Add(Command.Hibernate);
            Spent = true;
            Mode = Mode.Idle;
        }
        else if (!inputs.Live)
        {
            commands.Add(Command.Disarm);
            if (closed)
            {
                Mode = Mode.Finishing;
                _stage = Stage.WaitAway;
            }
            else
            {
                Mode = Mode.Idle;
            }
        }
    }

    private void StepFinishing(Inputs inputs, TimeSpan now, List<Command> commands)
    {
        if (inputs.Lid != LidState.Closed)
        {
            Mode = Mode.Idle;
        }
        else if (inputs.Live && !Spent && inputs.CanWrite)
        {
            commands.Add(Command.Arm);
            Mode = Mode.Armed;
        }
        else if (_stage == Stage.WaitAway)
        {
            if (inputs.IdleTime < TimeSpan.FromMinutes(settings.AwayMinutes)) return;
            switch (inputs.OwnerLid)
            {
                case LidAction.Sleep:
                    commands.Add(Command.MonitorOff);
                    _stage = Stage.MonitorOffSent;
                    _sentAt = now;
                    break;
                case LidAction.Hibernate:
                case LidAction.ShutDown:
                    commands.Add(Command.Hibernate);
                    Mode = Mode.Idle;
                    break;
                default:
                    Mode = Mode.Idle;
                    break;
            }
        }
        else if (inputs.IdleTime < TimeSpan.FromMinutes(1))
        {
            _stage = Stage.WaitAway;
        }
        else if (now - _sentAt >= TimeSpan.FromMinutes(5))
        {
            commands.Add(Command.Hibernate);
            Mode = Mode.Idle;
        }
    }

    private string Describe()
    {
        switch (Mode)
        {
            case Mode.Paused:
                return "Paused";
            case Mode.Armed when _last is { Lid: LidState.Closed } last && !Unattended(last):
                return $"Live \u2014 lid closed, when done: {WhenDone(last.OwnerLid)}";
            case Mode.Armed when _last is { Lid: LidState.Closed } last:
                return $"Live \u2014 lid closed {(int)LidClosedFor.TotalHours}h{LidClosedFor.Minutes:00}, when done: {WhenDone(last.OwnerLid)}";
            case Mode.Armed:
                return "Live";
            case Mode.Finishing:
                return _stage == Stage.WaitAway ? "Done \u2014 waiting until you're away" : "Done \u2014 screen off";
            default:
                if (Spent) return "Idle \u2014 limit reached, open the lid to reset";
                if (_last is { Live: true, CanWrite: false }) return "Idle \u2014 can't change power settings";
                return "Idle";
        }
    }

    private static string WhenDone(LidAction owner) => owner switch
    {
        LidAction.Sleep => "sleep",
        LidAction.Hibernate or LidAction.ShutDown => "hibernate",
        _ => "do nothing",
    };
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test -c Release -- --filter-class Nightcrew.Tests.ControllerTests`  Expected: PASS (61 tests)

Then `dotnet build -c Release` (0 warnings, 0 errors) and `dotnet test -c Release` (everything passes).

- [ ] **Step 5: Commit**

```bash
git add src/Nightcrew/Core/Controller.cs tests/Nightcrew.Tests/ControllerTests.cs
git commit -m "feat: controller state machine (arm, lock, floor/cap hibernate, finish on owner lid action)"
```

### Task 9: Activity monitor, engine and dry-run wrappers

**Files:**
- Create: `src/Nightcrew/Core/ActivityMonitor.cs`
- Create: `src/Nightcrew/Core/Engine.cs`
- Create: `src/Nightcrew/Platform/DryRun.cs`
- Test: `tests/Nightcrew.Tests/ActivityMonitorTests.cs`
- Test: `tests/Nightcrew.Tests/EngineTests.cs`

**Interfaces:**
- Consumes:
  - Task 2: `IBusySignal` (`string Name`, `bool Sample(TimeSpan now)`); `IClock.Now`; `IPowerPolicy` (`PowerValues Read()`, `void Write(PowerValues values)`, `bool CanWrite(out string reason)`); `IKeepAwake` (`bool IsHeld`, `void Hold()`, `void Release()`); `ISystemPower` (`PowerSource Source`, `int? BatteryPercent`, `TimeSpan IdleTime`, `bool HibernateAvailable`, `bool Hibernate()`, `void MonitorOff()`, `void LockWorkstation()`); `ILog` (`Info`, `Warn`); `PowerValues.LidFor(PowerSource source)`; `Settings` defaults; test helper `FakeClock` (`TimeSpan Now`, `void Advance(TimeSpan by)`).
  - Task 6: `RestoreJournal(IPowerPolicy policy, string path, ILog log)` with `static readonly PowerValues ArmValues`, `bool HasRecord`, `PowerValues? OwnerValues`, `void Arm()`, `bool Restore()`.
  - Task 8: `Controller(Settings settings)` with `Mode Mode`, `string Status`, `IReadOnlyList<Command> Step(Inputs inputs, TimeSpan now)`, `void Reset()`; `enum Mode { Idle, Armed, Finishing, Paused }`; `record Inputs(bool Live, LidState Lid, PowerSource Source, int? BatteryPercent, TimeSpan IdleTime, bool Paused, bool CanWrite, LidAction OwnerLid)`; `enum Command { Arm, Disarm, Lock, MonitorOff, Hibernate }`.
- Produces:
  - `Nightcrew.Core`: `public sealed class ActivityMonitor(IReadOnlyList<IBusySignal> signals, TimeSpan grace)` with `bool Hold { get; set; }`, `bool Live { get; }`, `string? Reason { get; }`, `void Sample(TimeSpan now)`.
  - `Nightcrew.Core`: `public sealed class Engine(ActivityMonitor activity, Controller controller, RestoreJournal journal, IPowerPolicy policy, IKeepAwake keepAwake, ISystemPower power, ILog log, IClock clock)` with `LidState Lid { get; set; }`, `bool Paused { get; set; }`, `string Status { get; }`, `void Tick()`, `void RestoreNow()`. `Tick` also undoes a failed Arm, waits 5 minutes before arming again, and retries a restore that failed (see Engine.Tick below). **Deviation from the contract:** there is no `Settings` parameter. The engine never reads settings, and an unread primary-constructor parameter is warning CS9113, which fails the build.
  - `Nightcrew.Platform`: `public sealed class DryRunPolicy(IPowerPolicy real, ILog log) : IPowerPolicy`, `public sealed class DryRunKeepAwake(ILog log) : IKeepAwake`, `public sealed class DryRunSystemPower(ISystemPower real, ILog log) : ISystemPower`.

**ActivityMonitor.** `Sample(now)` samples every signal, in list order, every time (also while held, so baselines stay current) and stamps a signal busy at `now` when it returns true. `Live` is true while `Hold` is set or any signal was busy at most `grace` ago (`now - lastBusy <= grace`). `Reason` is `"hold"` when held, otherwise the `Name` of the first signal in list order that is within grace, otherwise null. `Hold` takes effect at once, without a new sample. Task 11 passes `TimeSpan.FromMinutes(settings.GraceMinutes)`.

**Engine.Tick.** It samples `activity` at `clock.Now`, reads `power.Source` once, and takes the owner lid action from `(journal.OwnerValues ?? policy.Read()).LidFor(source)`. While armed, the record holds the owner's values and the policy holds Do nothing. `CanWrite` is checked at most every 5 minutes and cached in between. A block is logged once per distinct reason (`Can't change power settings: <reason>`), and the logged reason is cleared once writing is allowed again. Then it builds `Inputs` from `activity.Live`, `Lid`, the power readings, `Paused`, the cached `CanWrite` (passed as false while a failed Arm's 5-minute block runs) and the owner lid action, steps the controller, and runs each command in order:
- **Arm:** `journal.Arm()`, then `keepAwake.Hold()`. If either throws, the failure is logged, then the half-done arm is undone (`keepAwake.Release()`, `journal.Restore()`) and `controller.Reset()` puts the controller back to Idle. Arming is then blocked for 5 minutes: until `clock.Now` reaches the block end, `Inputs.CanWrite` is false, so a persistent failure does not write and undo the settings on every tick. The start of the block is logged once, as `Not arming again for 5 minutes`. The commands after the Arm still run.
- **Disarm:** `keepAwake.Release()`, then `journal.Restore()`.
- **Lock:** `power.LockWorkstation()`.
- **MonitorOff:** `power.MonitorOff()`.
- **Hibernate:** `power.Hibernate()`. If it returns false, warn and call `power.MonitorOff()`.

**Restore retry.** After the commands, if `controller.Mode` is not Armed and `journal.HasRecord` is still true (a restore failed and kept its record), and at least 60 seconds of `clock.Now` have passed since the last restore attempt (a Disarm, a failed-Arm undo, `RestoreNow()` or an earlier retry; the engine's construction counts as one, so the first retry comes at least 60 seconds after startup), it logs `Retrying the restore of the power settings` and runs `keepAwake.Release()` and `journal.Restore()` again. While Armed the record is in use and is never retried.

Each command is logged as `<Command> (activity: <reason or none>)` before it runs. A command that throws is logged as `<Command> failed: <message>`, and the rest still run. Any other failure (for example a policy read) is logged as `Tick failed: <message>`, so `Tick` never throws on the timer thread. `Status` is `controller.Status`, followed by ` (<reason>)` when `activity.Reason` is set; for example, `Live — lid closed 1h02, when done: sleep (transcripts)`. Before the first tick it is `Idle`. `RestoreNow()` is `keepAwake.Release()` and then `journal.Restore()`, and it counts as a restore attempt for the retry timer.

**Dry run.** The wrappers pass every read through to the real object. `DryRunPolicy.Write`, `DryRunKeepAwake.Hold`/`Release` and `DryRunSystemPower.Hibernate`/`MonitorOff`/`LockWorkstation` only log a `[dry run] would <action>` line. `DryRunKeepAwake` tracks `IsHeld`, and `DryRunSystemPower.Hibernate` returns true. Task 11 must pass the same policy object (real or `DryRunPolicy`) to both the `RestoreJournal` and the `Engine`. In dry run the journal still writes `restore.json`. This is harmless: the real values never equal the written Do-nothing/Never values unless the owner already had them, so a restore, including the watchdog's, changes nothing and deletes the record.

- [ ] **Step 1: Write the failing tests**

`tests/Nightcrew.Tests/ActivityMonitorTests.cs`:
```csharp
using Nightcrew.Core;
using Xunit;

namespace Nightcrew.Tests;

public sealed class ActivityMonitorTests
{
    private static readonly TimeSpan Grace = TimeSpan.FromMinutes(5);

    private readonly FakeClock _clock = new();
    private readonly FakeSignal _transcripts = new("transcripts");
    private readonly FakeSignal _gpu = new("gpu");

    private ActivityMonitor NewMonitor() => new([_transcripts, _gpu], Grace);

    [Fact]
    public void Not_live_before_anything_is_busy()
    {
        var monitor = NewMonitor();
        Assert.False(monitor.Live);
        Assert.Null(monitor.Reason);

        monitor.Sample(_clock.Now);
        Assert.False(monitor.Live);
        Assert.Null(monitor.Reason);
    }

    [Fact]
    public void A_busy_signal_makes_it_live_with_its_name()
    {
        var monitor = NewMonitor();
        _gpu.Busy = true;

        monitor.Sample(_clock.Now);

        Assert.True(monitor.Live);
        Assert.Equal("gpu", monitor.Reason);
    }

    [Fact]
    public void Stays_live_through_the_grace_window_then_drops()
    {
        var monitor = NewMonitor();
        _transcripts.Busy = true;
        monitor.Sample(_clock.Now);
        _transcripts.Busy = false;

        _clock.Advance(Grace);
        monitor.Sample(_clock.Now);
        Assert.True(monitor.Live);
        Assert.Equal("transcripts", monitor.Reason);

        _clock.Advance(TimeSpan.FromSeconds(1));
        monitor.Sample(_clock.Now);
        Assert.False(monitor.Live);
        Assert.Null(monitor.Reason);
    }

    [Fact]
    public void Busy_again_restarts_the_grace_window()
    {
        var monitor = NewMonitor();
        _transcripts.Busy = true;
        monitor.Sample(_clock.Now);
        _clock.Advance(TimeSpan.FromMinutes(4));
        monitor.Sample(_clock.Now);
        _transcripts.Busy = false;

        _clock.Advance(TimeSpan.FromMinutes(4));
        monitor.Sample(_clock.Now);

        Assert.True(monitor.Live);
    }

    [Fact]
    public void Hold_is_live_without_any_signal_and_wins_the_reason()
    {
        var monitor = NewMonitor();
        monitor.Hold = true;
        Assert.True(monitor.Live);
        Assert.Equal("hold", monitor.Reason);

        _gpu.Busy = true;
        monitor.Sample(_clock.Now);
        Assert.Equal("hold", monitor.Reason);

        monitor.Hold = false;
        Assert.Equal("gpu", monitor.Reason);
    }

    [Fact]
    public void The_first_busy_signal_in_order_is_the_reason()
    {
        var monitor = NewMonitor();
        _transcripts.Busy = true;
        _gpu.Busy = true;

        monitor.Sample(_clock.Now);

        Assert.Equal("transcripts", monitor.Reason);
    }

    [Fact]
    public void Every_signal_is_sampled_every_time_with_the_given_time()
    {
        var monitor = NewMonitor();
        monitor.Hold = true;
        _transcripts.Busy = true;

        monitor.Sample(_clock.Now);
        _clock.Advance(TimeSpan.FromSeconds(10));
        monitor.Sample(_clock.Now);

        Assert.Equal(2, _transcripts.Samples);
        Assert.Equal(2, _gpu.Samples);
        Assert.Equal(_clock.Now, _gpu.LastNow);
    }

    [Fact]
    public void No_signals_means_live_only_on_hold()
    {
        var monitor = new ActivityMonitor([], Grace);
        monitor.Sample(_clock.Now);
        Assert.False(monitor.Live);

        monitor.Hold = true;
        Assert.True(monitor.Live);
    }

    private sealed class FakeSignal(string name) : IBusySignal
    {
        public string Name => name;
        public bool Busy { get; set; }
        public int Samples { get; private set; }
        public TimeSpan LastNow { get; private set; }

        public bool Sample(TimeSpan now)
        {
            Samples++;
            LastNow = now;
            return Busy;
        }
    }
}
```

`tests/Nightcrew.Tests/EngineTests.cs`:
```csharp
using Nightcrew.Core;
using Nightcrew.Platform;
using Xunit;

namespace Nightcrew.Tests;

public sealed class EngineTests : IDisposable
{
    // Lid action: Sleep on AC, Hibernate on DC. Sleep after 15 min on AC, 10 min on DC.
    private static readonly PowerValues Owner = new(1, 2, 900, 600);
    private static readonly TimeSpan Grace = TimeSpan.FromMinutes(5);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nightcrew-tests-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _calls = [];
    private readonly Settings _settings = new();
    private readonly FakeClock _clock = new();
    private readonly FakeSignal _signal = new();
    private readonly FakeLog _log = new();
    private readonly FakePolicy _policy;
    private readonly FakeKeepAwake _keepAwake;
    private readonly FakeSystemPower _power;
    private readonly RestoreJournal _journal;
    private readonly Controller _controller;
    private readonly Engine _engine;

    public EngineTests()
    {
        _policy = new FakePolicy(Owner, _calls);
        _keepAwake = new FakeKeepAwake(_calls);
        _power = new FakeSystemPower(_calls);
        _journal = new RestoreJournal(_policy, RecordPath, _log);
        _controller = new Controller(_settings);
        _engine = new Engine(new ActivityMonitor([_signal], Grace), _controller, _journal,
            _policy, _keepAwake, _power, _log, _clock);
    }

    private string RecordPath => Path.Combine(_dir, "restore.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private void Tick(double afterMinutes = 0)
    {
        _clock.Advance(TimeSpan.FromMinutes(afterMinutes));
        _engine.Tick();
    }

    private void TickAfterSeconds(int seconds)
    {
        _clock.Advance(TimeSpan.FromSeconds(seconds));
        _engine.Tick();
    }

    private void ArmWithLidOpen()
    {
        _signal.Busy = true;
        _engine.Lid = LidState.Open;
        Tick();
    }

    [Fact]
    public void Arm_writes_the_restore_record_then_holds()
    {
        ArmWithLidOpen();

        Assert.True(_journal.HasRecord);
        Assert.Equal(Owner, _journal.OwnerValues);
        Assert.Equal(RestoreJournal.ArmValues, _policy.Values);
        Assert.True(_keepAwake.IsHeld);
        Assert.Equal(["Write", "Hold"], _calls);
        Assert.Contains(_log.Infos, line => line.StartsWith("Arm") && line.Contains("transcripts"));
    }

    [Fact]
    public void Disarm_releases_then_restores_the_owner_values()
    {
        ArmWithLidOpen();
        _signal.Busy = false;

        Tick(afterMinutes: 6);

        Assert.Equal(Owner, _policy.Values);
        Assert.False(_journal.HasRecord);
        Assert.False(_keepAwake.IsHeld);
        Assert.Equal(["Write", "Hold", "Release", "Write"], _calls);
    }

    [Fact]
    public void Hibernate_failure_falls_back_to_monitor_off()
    {
        ArmWithLidOpen();
        _engine.Lid = LidState.Closed;
        _power.Source = PowerSource.DC;
        _power.BatteryPercent = 10;
        _power.IdleTime = TimeSpan.FromMinutes(10);
        _power.HibernateResult = false;

        Tick(afterMinutes: 1);

        Assert.Equal(["Write", "Hold", "Lock", "Release", "Write", "Hibernate", "MonitorOff"], _calls);
        Assert.Contains(_log.Warnings, line => line.Contains("Hibernate"));
        Assert.Equal(Owner, _policy.Values);
    }

    [Fact]
    public void A_throwing_command_is_logged_and_the_next_command_still_runs()
    {
        _signal.Busy = true;
        _engine.Lid = LidState.Closed;
        _power.IdleTime = TimeSpan.FromMinutes(10);
        _policy.FailWrites = true;

        Tick();

        Assert.Contains(_log.Warnings, line => line.StartsWith("Arm failed"));
        Assert.False(_keepAwake.IsHeld);
        Assert.False(_journal.HasRecord);
        Assert.Equal(["Release", "Lock"], _calls);
    }

    [Fact]
    public void A_failed_arm_is_undone_and_retried_after_five_minutes()
    {
        _keepAwake.FailHolds = true;
        ArmWithLidOpen();

        Assert.Contains(_log.Warnings, line => line.StartsWith("Arm failed"));
        Assert.Equal(["Write", "Release", "Write"], _calls);
        Assert.Equal(Owner, _policy.Values);
        Assert.False(_journal.HasRecord);
        Assert.Equal(Mode.Idle, _controller.Mode);

        _keepAwake.FailHolds = false;
        for (var i = 0; i < 29; i++) TickAfterSeconds(10);   // up to 4 min 50 s after the failure: blocked

        Assert.Equal(Mode.Idle, _controller.Mode);
        Assert.Equal(["Write", "Release", "Write"], _calls);
        Assert.Single(_log.Infos, line => line.StartsWith("Not arming again"));

        TickAfterSeconds(10);                                // 5 minutes after the failure

        Assert.Equal(Mode.Armed, _controller.Mode);
        Assert.True(_keepAwake.IsHeld);
        Assert.Equal(["Write", "Release", "Write", "Write", "Hold"], _calls);
        Assert.Single(_log.Infos, line => line.StartsWith("Not arming again"));
    }

    [Fact]
    public void A_failed_restore_is_retried_when_not_armed_at_most_once_a_minute()
    {
        ArmWithLidOpen();
        Tick(afterMinutes: 2);                         // armed, with a record: never retried
        Assert.DoesNotContain(_log.Infos, line => line.StartsWith("Retrying"));

        _policy.FailWrites = true;
        _signal.Busy = false;
        Tick(afterMinutes: 6);                         // Disarm: the restore fails and the record is kept
        Assert.True(_journal.HasRecord);
        Assert.Equal(Mode.Idle, _controller.Mode);
        Assert.Single(_log.Warnings, line => line.StartsWith("Restore failed"));

        for (var i = 0; i < 5; i++) TickAfterSeconds(10);
        Assert.Single(_log.Warnings, line => line.StartsWith("Restore failed"));

        TickAfterSeconds(10);                          // 60 s after the failed restore
        Assert.Equal(2, _log.Warnings.Count(line => line.StartsWith("Restore failed")));
        Assert.Single(_log.Infos, line => line.StartsWith("Retrying"));

        _policy.FailWrites = false;
        TickAfterSeconds(30);
        Assert.True(_journal.HasRecord);               // 30 s after the last try: too soon
        TickAfterSeconds(30);
        Assert.False(_journal.HasRecord);
        Assert.Equal(Owner, _policy.Values);
        Assert.False(_keepAwake.IsHeld);
    }

    [Fact]
    public void A_record_left_at_startup_is_first_retried_a_minute_after_the_engine_starts()
    {
        _journal.Arm();                                // a record left behind, as if a restore had failed
        _calls.Clear();

        Tick();
        TickAfterSeconds(50);
        Assert.True(_journal.HasRecord);
        Assert.DoesNotContain(_log.Infos, line => line.StartsWith("Retrying"));

        TickAfterSeconds(10);                          // 60 s after the engine was created
        Assert.Single(_log.Infos, line => line.StartsWith("Retrying"));
        Assert.False(_journal.HasRecord);
        Assert.Equal(Owner, _policy.Values);
        Assert.Equal(["Release", "Write"], _calls);
    }

    [Fact]
    public void Finishing_carries_out_the_restored_owner_lid_action()
    {
        _signal.Busy = true;
        _engine.Lid = LidState.Closed;
        _power.IdleTime = TimeSpan.FromMinutes(10);
        Tick();
        _signal.Busy = false;

        Tick(afterMinutes: 6);
        Tick(afterMinutes: 1);

        Assert.Equal(["Write", "Hold", "Lock", "Release", "Write", "MonitorOff"], _calls);
    }

    [Fact]
    public void Status_combines_the_controller_status_and_the_reason()
    {
        Assert.Equal("Idle", _engine.Status);

        _signal.Busy = true;
        _engine.Lid = LidState.Closed;
        _power.Source = PowerSource.DC;
        _power.IdleTime = TimeSpan.FromMinutes(10);
        Tick();
        Tick(afterMinutes: 1);

        // While armed, the owner's DC lid action comes from the record, not from the policy (now Do nothing).
        Assert.Equal("Live \u2014 lid closed 0h01, when done: hibernate (transcripts)", _engine.Status);
    }

    [Fact]
    public void Can_write_is_checked_every_five_minutes_and_a_block_is_logged_once()
    {
        _policy.CanWriteResult = false;
        _signal.Busy = true;
        _engine.Lid = LidState.Open;

        Tick();
        Tick(afterMinutes: 1);
        Tick(afterMinutes: 1);

        Assert.Equal(1, _policy.CanWriteChecks);
        Assert.Empty(_calls);
        Assert.Equal("Idle \u2014 can't change power settings (transcripts)", _engine.Status);

        Tick(afterMinutes: 3);
        Assert.Equal(2, _policy.CanWriteChecks);
        Assert.Single(_log.Warnings, line => line.Contains(FakePolicy.BlockedReason));

        _policy.CanWriteResult = true;
        Tick(afterMinutes: 5);
        Assert.Equal(3, _policy.CanWriteChecks);
        Assert.Equal(["Write", "Hold"], _calls);
    }

    [Fact]
    public void RestoreNow_releases_and_restores()
    {
        ArmWithLidOpen();

        _engine.RestoreNow();

        Assert.Equal(Owner, _policy.Values);
        Assert.False(_journal.HasRecord);
        Assert.False(_keepAwake.IsHeld);
    }

    [Fact]
    public void A_failing_read_is_logged_and_tick_does_not_throw()
    {
        _policy.FailReads = true;
        _signal.Busy = true;

        Tick();

        Assert.Contains(_log.Warnings, line => line.StartsWith("Tick failed"));
        Assert.Empty(_calls);
    }

    [Fact]
    public void Dry_run_wrappers_read_the_real_values_but_only_log_changes()
    {
        var policy = new DryRunPolicy(_policy, _log);
        var keepAwake = new DryRunKeepAwake(_log);
        var power = new DryRunSystemPower(_power, _log);
        var journal = new RestoreJournal(policy, RecordPath, _log);
        var engine = new Engine(new ActivityMonitor([_signal], Grace), new Controller(_settings), journal,
            policy, keepAwake, power, _log, _clock);
        _signal.Busy = true;
        engine.Lid = LidState.Closed;
        _power.Source = PowerSource.DC;
        _power.BatteryPercent = 50;
        _power.IdleTime = TimeSpan.FromMinutes(10);

        engine.Tick();
        Assert.True(keepAwake.IsHeld);

        _power.BatteryPercent = 10;
        _clock.Advance(TimeSpan.FromMinutes(1));
        engine.Tick();

        Assert.False(keepAwake.IsHeld);
        Assert.False(journal.HasRecord);
        Assert.Equal(Owner, _policy.Values);
        Assert.Equal(1, _policy.CanWriteChecks);
        Assert.Empty(_calls);
        Assert.Contains(_log.Infos, line => line.StartsWith("[dry run]") && line.Contains("hibernate"));
        Assert.Contains(_log.Infos, line => line.StartsWith("[dry run]") && line.Contains("lock"));
        Assert.Contains(_log.Infos, line => line.StartsWith("[dry run]") && line.Contains("write power settings"));
    }

    private sealed class FakeSignal : IBusySignal
    {
        public string Name => "transcripts";
        public bool Busy { get; set; }

        public bool Sample(TimeSpan now) => Busy;
    }

    private sealed class FakePolicy(PowerValues values, List<string> calls) : IPowerPolicy
    {
        public const string BlockedReason = "policy blocks lid action AC (5)";

        public PowerValues Values { get; private set; } = values;
        public bool FailWrites { get; set; }
        public bool FailReads { get; set; }
        public bool CanWriteResult { get; set; } = true;
        public int CanWriteChecks { get; private set; }

        public PowerValues Read() => FailReads ? throw new InvalidOperationException("PowerGetActiveScheme failed (5)") : Values;

        public void Write(PowerValues values)
        {
            if (FailWrites) throw new InvalidOperationException("PowerWriteACValueIndex failed (5)");
            Values = values;
            calls.Add("Write");
        }

        public bool CanWrite(out string reason)
        {
            CanWriteChecks++;
            reason = CanWriteResult ? "" : BlockedReason;
            return CanWriteResult;
        }
    }

    private sealed class FakeKeepAwake(List<string> calls) : IKeepAwake
    {
        public bool IsHeld { get; private set; }
        public bool FailHolds { get; set; }

        public void Hold()
        {
            if (FailHolds) throw new InvalidOperationException("PowerSetRequest failed (5)");
            IsHeld = true;
            calls.Add("Hold");
        }

        public void Release()
        {
            IsHeld = false;
            calls.Add("Release");
        }
    }

    private sealed class FakeSystemPower(List<string> calls) : ISystemPower
    {
        public PowerSource Source { get; set; } = PowerSource.AC;
        public int? BatteryPercent { get; set; } = 100;
        public TimeSpan IdleTime { get; set; }
        public bool HibernateAvailable => true;
        public bool HibernateResult { get; set; } = true;

        public bool Hibernate()
        {
            calls.Add("Hibernate");
            return HibernateResult;
        }

        public void MonitorOff() => calls.Add("MonitorOff");

        public void LockWorkstation() => calls.Add("Lock");
    }

    private sealed class FakeLog : ILog
    {
        public List<string> Infos { get; } = [];
        public List<string> Warnings { get; } = [];

        public void Info(string message) => Infos.Add(message);

        public void Warn(string message) => Warnings.Add(message);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run (after the env line from the plan's Global Constraints, `export DOTNET_ROOT="$LOCALAPPDATA/Microsoft/dotnet-sdk"; export PATH="$DOTNET_ROOT:$PATH"; export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1`):
`dotnet test -c Release -- --filter-class Nightcrew.Tests.ActivityMonitorTests`
and
`dotnet test -c Release -- --filter-class Nightcrew.Tests.EngineTests`
Expected: build error CS0246, `The type or namespace name 'ActivityMonitor' could not be found` (and the same for `Engine`).

- [ ] **Step 3: Write the implementation**

`src/Nightcrew/Core/ActivityMonitor.cs`:
```csharp
namespace Nightcrew.Core;

/// <summary>Live while held, or while any signal was busy within the grace window.</summary>
public sealed class ActivityMonitor(IReadOnlyList<IBusySignal> signals, TimeSpan grace)
{
    private readonly Dictionary<IBusySignal, TimeSpan> _lastBusy = [];
    private string? _signalReason;

    public bool Hold { get; set; }

    public bool Live => Reason is not null;

    /// <summary>"hold", or the name of the first signal busy within grace; null when not live.</summary>
    public string? Reason => Hold ? "hold" : _signalReason;

    /// <summary>Samples every signal, even when held, so their baselines stay current.</summary>
    public void Sample(TimeSpan now)
    {
        _signalReason = null;
        foreach (var signal in signals)
        {
            if (signal.Sample(now)) _lastBusy[signal] = now;
            if (_signalReason is null && _lastBusy.TryGetValue(signal, out var at) && now - at <= grace)
                _signalReason = signal.Name;
        }
    }
}
```

`src/Nightcrew/Core/Engine.cs`:
```csharp
namespace Nightcrew.Core;

/// <summary>One tick: sample activity, ask the controller, carry out its commands.</summary>
public sealed class Engine(ActivityMonitor activity, Controller controller, RestoreJournal journal,
    IPowerPolicy policy, IKeepAwake keepAwake, ISystemPower power, ILog log, IClock clock)
{
    private static readonly TimeSpan CanWriteCheckEvery = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RestoreRetryEvery = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ArmRetryAfter = TimeSpan.FromMinutes(5);

    private TimeSpan? _canWriteCheckedAt;
    private bool _canWrite;
    private string? _loggedBlock;
    private TimeSpan _restoreTriedAt = clock.Now; // so the first retry comes at least a minute after startup
    private TimeSpan? _armBlockedUntil;

    public LidState Lid { get; set; }

    public bool Paused { get; set; }

    public string Status => activity.Reason is { } reason ? $"{controller.Status} ({reason})" : controller.Status;

    /// <summary>Never throws: failures are logged, and a failing command does not stop the ones after it.</summary>
    public void Tick()
    {
        try
        {
            var now = clock.Now;
            activity.Sample(now);
            var source = power.Source;
            var ownerLid = (journal.OwnerValues ?? policy.Read()).LidFor(source);
            var canWrite = CanWrite(now) && !(_armBlockedUntil is { } until && now < until);
            var inputs = new Inputs(activity.Live, Lid, source, power.BatteryPercent, power.IdleTime, Paused, canWrite, ownerLid);
            foreach (var command in controller.Step(inputs, now)) Run(command, now);
            RetryRestore(now);
        }
        catch (Exception ex)
        {
            log.Warn($"Tick failed: {ex.Message}");
        }
    }

    /// <summary>Releases the keep-awake request and restores the owner's settings (quit, session end, startup).</summary>
    public void RestoreNow() => ReleaseAndRestore(clock.Now);

    private void ReleaseAndRestore(TimeSpan now)
    {
        _restoreTriedAt = now;
        keepAwake.Release();
        journal.Restore();
    }

    /// <summary>A restore that failed left its record behind: try again, at most once a minute, whenever we are not armed.</summary>
    private void RetryRestore(TimeSpan now)
    {
        if (controller.Mode == Mode.Armed || !journal.HasRecord) return;
        if (now - _restoreTriedAt < RestoreRetryEvery) return;
        log.Info("Retrying the restore of the power settings");
        ReleaseAndRestore(now);
    }

    private void Run(Command command, TimeSpan now)
    {
        log.Info($"{command} (activity: {activity.Reason ?? "none"})");
        try
        {
            switch (command)
            {
                case Command.Arm:
                    journal.Arm();
                    keepAwake.Hold();
                    break;
                case Command.Disarm:
                    ReleaseAndRestore(now);
                    break;
                case Command.Lock:
                    power.LockWorkstation();
                    break;
                case Command.MonitorOff:
                    power.MonitorOff();
                    break;
                case Command.Hibernate:
                    if (!power.Hibernate())
                    {
                        log.Warn("Hibernate failed; turning the screen off instead");
                        power.MonitorOff();
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            log.Warn($"{command} failed: {ex.Message}");
            if (command != Command.Arm) return;
            // Undo a half-done arm and go back to Idle. Arming waits 5 minutes, so a persistent failure doesn't churn every tick.
            ReleaseAndRestore(now);
            controller.Reset();
            _armBlockedUntil = now + ArmRetryAfter;
            log.Info("Not arming again for 5 minutes");
        }
    }

    /// <summary>Checks at most every five minutes; logs a block once per reason.</summary>
    private bool CanWrite(TimeSpan now)
    {
        if (_canWriteCheckedAt is { } at && now - at < CanWriteCheckEvery) return _canWrite;
        _canWriteCheckedAt = now;
        _canWrite = policy.CanWrite(out var reason);
        if (_canWrite)
        {
            _loggedBlock = null;
        }
        else if (reason != _loggedBlock)
        {
            log.Warn($"Can't change power settings: {reason}");
            _loggedBlock = reason;
        }
        return _canWrite;
    }
}
```

`src/Nightcrew/Platform/DryRun.cs`:
```csharp
using Nightcrew.Core;

namespace Nightcrew.Platform;

/// <summary>Reads the real power settings; logs writes instead of making them.</summary>
public sealed class DryRunPolicy(IPowerPolicy real, ILog log) : IPowerPolicy
{
    public PowerValues Read() => real.Read();

    public void Write(PowerValues values) => log.Info($"[dry run] would write power settings {values}");

    public bool CanWrite(out string reason) => real.CanWrite(out reason);
}

/// <summary>Logs instead of holding a power request.</summary>
public sealed class DryRunKeepAwake(ILog log) : IKeepAwake
{
    public bool IsHeld { get; private set; }

    public void Hold()
    {
        IsHeld = true;
        log.Info("[dry run] would hold the keep-awake request");
    }

    public void Release()
    {
        IsHeld = false;
        log.Info("[dry run] would release the keep-awake request");
    }
}

/// <summary>Reads the real power state; logs instead of hibernating, turning the screen off or locking.</summary>
public sealed class DryRunSystemPower(ISystemPower real, ILog log) : ISystemPower
{
    public PowerSource Source => real.Source;

    public int? BatteryPercent => real.BatteryPercent;

    public TimeSpan IdleTime => real.IdleTime;

    public bool HibernateAvailable => real.HibernateAvailable;

    public bool Hibernate()
    {
        log.Info("[dry run] would hibernate");
        return true;
    }

    public void MonitorOff() => log.Info("[dry run] would turn the monitor off");

    public void LockWorkstation() => log.Info("[dry run] would lock the workstation");
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test -c Release -- --filter-class Nightcrew.Tests.ActivityMonitorTests`  Expected: PASS (8 tests)
Run: `dotnet test -c Release -- --filter-class Nightcrew.Tests.EngineTests`  Expected: PASS (13 tests)

Then run `dotnet build -c Release`, which should give 0 warnings and 0 errors, and `dotnet test -c Release`, where every test should pass.

- [ ] **Step 5: Commit**

```bash
git add src/Nightcrew/Core/ActivityMonitor.cs src/Nightcrew/Core/Engine.cs src/Nightcrew/Platform/DryRun.cs tests/Nightcrew.Tests/ActivityMonitorTests.cs tests/Nightcrew.Tests/EngineTests.cs
git commit -m "feat: activity monitor, engine and dry-run wrappers"
```

### Task 10: Autostart and watchdog

**Files:**
- Create: `src/Nightcrew/Core/WatchdogTaskXml.cs`
- Create: `src/Nightcrew/Platform/Autostart.cs`
- Create: `src/Nightcrew/Platform/WatchdogTask.cs`
- Create: `src/Nightcrew/App/Watchdog.cs`
- Test: `tests/Nightcrew.Tests/WatchdogTaskXmlTests.cs`

**Interfaces:**
- Consumes: `IPowerPolicy`, `ILog` (Core/Contracts.cs); `AppPaths.RestoreFile` (skeleton); from Task 6: `new PowrProfPolicy()`, `new RestoreJournal(IPowerPolicy policy, string path, ILog log)`, `bool RestoreJournal.HasRecord { get; }`, `bool RestoreJournal.Restore()`. `App/Watchdog.cs` does not compile until Task 6 is in the tree.
- Produces: `public static class Autostart { public static bool IsEnabled(); public static void Set(bool enabled, string exePath); }` (namespace Nightcrew.Platform; HKCU\Software\Microsoft\Windows\CurrentVersion\Run, value `Nightcrew` = `"<exe>"`); `public static class WatchdogTaskXml { public static string Build(string exePath); }` (namespace Nightcrew.Core; user taken from `WindowsIdentity.GetCurrent().Name`); `public static class WatchdogTask { public const string Name = "Nightcrew Watchdog"; public static bool Register(string exePath, ILog log); public static bool Unregister(ILog log); }` (namespace Nightcrew.Platform; false plus a log warning on any failure, never throws for schtasks errors); `public static class Watchdog { public static int Run(ILog log); }` (namespace Nightcrew.App; always returns 0). Note for Task 11: `Watchdog.Run` holds `Local\Nightcrew.Instance` for the moment it checks and restores, and its logon trigger fires alongside the Run key, so the app and `--uninstall` should wait briefly for the mutex (for example `WaitOne(TimeSpan.FromSeconds(5))`) rather than `WaitOne(0)`.

- [ ] **Step 1: Write the failing tests**

Only the XML is unit-tested. The tests parse it with `XDocument`; they never register a task, run schtasks, write the Run key or call `Watchdog.Run`.

`tests/Nightcrew.Tests/WatchdogTaskXmlTests.cs`:
```csharp
using System.Security.Principal;
using System.Xml.Linq;
using Nightcrew.Core;
using Xunit;

namespace Nightcrew.Tests;

public sealed class WatchdogTaskXmlTests
{
    private static readonly XNamespace T = "http://schemas.microsoft.com/windows/2004/02/mit/task";
    private const string Exe = @"C:\Users\Test User\AppData\Local\Nightcrew\nightcrew.exe";

    private static XElement Task(string exe = Exe) => XDocument.Parse(WatchdogTaskXml.Build(exe)).Root!;

    private static XElement Child(XElement parent, string name) =>
        parent.Element(T + name) ?? throw new Xunit.Sdk.XunitException($"missing <{name}> under <{parent.Name.LocalName}>");

    [Fact]
    public void Declares_utf16_and_the_task_namespace()
    {
        var xml = WatchdogTaskXml.Build(Exe);
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"UTF-16\"?>", xml);
        var task = Task();
        Assert.Equal(T + "Task", task.Name);
        Assert.Equal("1.2", (string?)task.Attribute("version"));
    }

    [Fact]
    public void Triggers_at_logon_for_the_current_user_and_every_five_minutes()
    {
        var triggers = Child(Task(), "Triggers");
        var logon = Child(triggers, "LogonTrigger");
        Assert.Equal(WindowsIdentity.GetCurrent().Name, Child(logon, "UserId").Value);

        var time = Child(triggers, "TimeTrigger");
        Assert.Equal("PT5M", Child(Child(time, "Repetition"), "Interval").Value);
        Assert.True(DateTime.TryParse(Child(time, "StartBoundary").Value, out _));
    }

    [Fact]
    public void Settings_run_on_battery_briefly_hidden_and_once_at_a_time()
    {
        var settings = Child(Task(), "Settings");
        Assert.Equal("false", Child(settings, "DisallowStartIfOnBatteries").Value);
        Assert.Equal("false", Child(settings, "StopIfGoingOnBatteries").Value);
        Assert.Equal("PT2M", Child(settings, "ExecutionTimeLimit").Value);
        Assert.Equal("IgnoreNew", Child(settings, "MultipleInstancesPolicy").Value);
        Assert.Equal("true", Child(settings, "StartWhenAvailable").Value);
        Assert.Equal("true", Child(settings, "Hidden").Value);
    }

    [Fact]
    public void Runs_with_the_interactive_token_of_the_current_user()
    {
        var principal = Child(Child(Task(), "Principals"), "Principal");
        Assert.Equal("InteractiveToken", Child(principal, "LogonType").Value);
        Assert.Equal(WindowsIdentity.GetCurrent().Name, Child(principal, "UserId").Value);
    }

    [Fact]
    public void Action_runs_the_quoted_exe_in_watchdog_mode()
    {
        var exec = Child(Child(Task(), "Actions"), "Exec");
        Assert.Equal("\"" + Exe + "\"", Child(exec, "Command").Value);
        Assert.Equal("--watchdog", Child(exec, "Arguments").Value);
    }

    [Fact]
    public void Registration_info_names_the_author_and_how_to_uninstall()
    {
        var info = Child(Task(), "RegistrationInfo");
        Assert.Equal("Nightcrew", Child(info, "Author").Value);
        Assert.Contains("nightcrew --uninstall", Child(info, "Description").Value);
    }

    [Fact]
    public void Exe_path_with_xml_special_characters_survives()
    {
        const string odd = @"C:\Tools & <Stuff>\nightcrew.exe";
        var exec = Child(Child(Task(odd), "Actions"), "Exec");
        Assert.Equal("\"" + odd + "\"", Child(exec, "Command").Value);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test -c Release -- --filter-class Nightcrew.Tests.WatchdogTaskXmlTests` (after the env line from the plan's Global Constraints)
Expected: build error `CS0103: The name 'WatchdogTaskXml' does not exist in the current context`

- [ ] **Step 3: Write the implementation**

Element order follows what Task Scheduler itself exports; the definition was checked read-only by loading it into an unregistered `Schedule.Service` `NewTask(0).XmlText`. `WatchdogTask.Register` writes the XML as UTF-16 with a BOM (`Encoding.Unicode`) so it matches the declaration that schtasks requires.

`src/Nightcrew/Core/WatchdogTaskXml.cs`:
```csharp
using System.Security.Principal;
using System.Xml.Linq;

namespace Nightcrew.Core;

/// <summary>Task Scheduler definition for the watchdog that restores power settings if the app dies.</summary>
public static class WatchdogTaskXml
{
    private static readonly XNamespace T = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    public static string Build(string exePath)
    {
        var user = WindowsIdentity.GetCurrent().Name;
        var task = new XElement(T + "Task", new XAttribute("version", "1.2"),
            new XElement(T + "RegistrationInfo",
                new XElement(T + "Author", "Nightcrew"),
                new XElement(T + "Description",
                    "Restores your power settings if Nightcrew stops while keeping the PC awake. Remove with: nightcrew --uninstall")),
            new XElement(T + "Triggers",
                new XElement(T + "LogonTrigger",
                    new XElement(T + "Enabled", "true"),
                    new XElement(T + "UserId", user)),
                new XElement(T + "TimeTrigger",
                    new XElement(T + "Repetition", new XElement(T + "Interval", "PT5M")),
                    new XElement(T + "StartBoundary", "2026-01-01T00:00:00"),
                    new XElement(T + "Enabled", "true"))),
            new XElement(T + "Principals",
                new XElement(T + "Principal", new XAttribute("id", "Author"),
                    new XElement(T + "UserId", user),
                    new XElement(T + "LogonType", "InteractiveToken"))),
            new XElement(T + "Settings",
                new XElement(T + "MultipleInstancesPolicy", "IgnoreNew"),
                new XElement(T + "DisallowStartIfOnBatteries", "false"),
                new XElement(T + "StopIfGoingOnBatteries", "false"),
                new XElement(T + "StartWhenAvailable", "true"),
                new XElement(T + "Hidden", "true"),
                new XElement(T + "ExecutionTimeLimit", "PT2M")),
            new XElement(T + "Actions", new XAttribute("Context", "Author"),
                new XElement(T + "Exec",
                    new XElement(T + "Command", "\"" + exePath + "\""),
                    new XElement(T + "Arguments", "--watchdog"))));

        return "<?xml version=\"1.0\" encoding=\"UTF-16\"?>" + Environment.NewLine + task;
    }
}
```

`src/Nightcrew/Platform/Autostart.cs`:
```csharp
using Microsoft.Win32;

namespace Nightcrew.Platform;

/// <summary>Start at login through HKCU\Software\Microsoft\Windows\CurrentVersion\Run.</summary>
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Nightcrew";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
        return key?.GetValue(ValueName) is string value && value.Length > 0;
    }

    public static void Set(bool enabled, string exePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
            key.SetValue(ValueName, "\"" + exePath + "\"", RegistryValueKind.String);
        else
            key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
```

`src/Nightcrew/Platform/WatchdogTask.cs`:
```csharp
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Nightcrew.Core;

namespace Nightcrew.Platform;

/// <summary>Registers the watchdog with schtasks.exe; no admin rights needed for the current user.</summary>
public static class WatchdogTask
{
    public const string Name = "Nightcrew Watchdog";

    public static bool Register(string exePath, ILog log)
    {
        var xmlPath = Path.Combine(Path.GetTempPath(), "nightcrew-watchdog-" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            File.WriteAllText(xmlPath, WatchdogTaskXml.Build(exePath), Encoding.Unicode);
            return Schtasks($"/Create /TN \"{Name}\" /XML \"{xmlPath}\" /F", log);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Warn($"Watchdog task: could not write {xmlPath}: {ex.Message}");
            return false;
        }
        finally
        {
            try { File.Delete(xmlPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    public static bool Unregister(ILog log) => Schtasks($"/Delete /TN \"{Name}\" /F", log);

    private static bool Schtasks(string arguments, ILog log)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        try
        {
            using var process = Process.Start(start)!;
            var error = process.StandardError.ReadToEnd().Trim();
            process.WaitForExit();
            if (process.ExitCode == 0)
            {
                log.Info($"Watchdog task: schtasks {arguments} succeeded");
                return true;
            }
            log.Warn($"Watchdog task: schtasks {arguments} failed ({process.ExitCode}): {error}");
            return false;
        }
        catch (Win32Exception ex)
        {
            log.Warn($"Watchdog task: could not run schtasks.exe: {ex.Message}");
            return false;
        }
    }
}
```

`src/Nightcrew/App/Watchdog.cs`:
```csharp
using Nightcrew.Core;
using Nightcrew.Platform;

namespace Nightcrew.App;

/// <summary>Runs from the scheduled task: restores the owner's power settings if the app is gone but left a record.</summary>
public static class Watchdog
{
    public static int Run(ILog log)
    {
        using var instance = new Mutex(initiallyOwned: false, @"Local\Nightcrew.Instance");
        bool owned;
        try { owned = instance.WaitOne(0); }
        catch (AbandonedMutexException) { owned = true; }
        if (!owned) return 0;

        try
        {
            var journal = new RestoreJournal(new PowrProfPolicy(), AppPaths.RestoreFile, log);
            if (journal.HasRecord)
            {
                log.Info("Watchdog: Nightcrew is not running but left a restore record; restoring power settings");
                journal.Restore();
            }
            return 0;
        }
        finally
        {
            instance.ReleaseMutex();
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test -c Release -- --filter-class Nightcrew.Tests.WatchdogTaskXmlTests`  Expected: PASS (7 tests)
Then `dotnet build -c Release` must report 0 warnings and 0 errors, and `dotnet test -c Release` must pass.

- [ ] **Step 5: Commit**

```bash
git add src/Nightcrew/Core/WatchdogTaskXml.cs src/Nightcrew/Platform/Autostart.cs src/Nightcrew/Platform/WatchdogTask.cs src/Nightcrew/App/Watchdog.cs tests/Nightcrew.Tests/WatchdogTaskXmlTests.cs
git commit -m "feat: autostart Run key and scheduled-task watchdog that restores power settings"
```

### Task 11: Tray app and Program

**Files:**
- Create: `src/Nightcrew/App/CommandLine.cs`
- Create: `src/Nightcrew/App/TrayIcons.cs`
- Create: `src/Nightcrew/App/TrayApp.cs`
- Modify: `src/Nightcrew/Program.cs` (replaces the Task 1 stub)
- Test: `tests/Nightcrew.Tests/CommandLineTests.cs`

**Interfaces:**
- Consumes:
  - Task 2: `Settings` (`GraceMinutes`, `BatteryFloorPercent`, `LidClosedCapHours`, `LockOnLidClose`, `StartAtLogin`, `DryRun`, `TranscriptRoots`, `IgnorePathContains`, `GpuPercent`, `SampleSeconds`); `IBusySignal`, `IPowerPolicy`, `IKeepAwake`, `ISystemPower`, `ILog`, `LidState`; `AppPaths.SettingsFile`, `AppPaths.RestoreFile`, `AppPaths.LogFile`; `SettingsStore.Load(string path)`, `SettingsStore.Save(string path, Settings settings)`; `new FileLog(string path)`; `new SystemClock()`.
  - Task 3: `new TranscriptSignal(IFileScanner scanner, IReadOnlyList<string> roots, IReadOnlyList<string> ignorePathContains)`, `new JsonlFileScanner()`.
  - Task 4: `new AgentTreeSignal(IProcessTable table, Settings settings)` (Task 4 dropped `processorCount`), `new ToolhelpProcessTable()`.
  - Task 5: `new GpuSignal(IGpuUsage gpu, double thresholdPercent)`, `new PdhGpuUsage()` (IDisposable).
  - Task 6: `new PowrProfPolicy()`; `new RestoreJournal(IPowerPolicy policy, string path, ILog log)` with `bool HasRecord`, `bool Restore()`.
  - Task 7: `new KeepAwake()` (IDisposable); `new SystemPower(Func<IntPtr> ownWindow)`; `new PowerEventsWindow()` with `LidState Lid`, `event Action<LidState>? LidChanged`, `event Action? Resumed`, `event Action? SessionEnding`, `event Action? SessionEndCancelled`, `event Action? SessionEnded`, `IntPtr Handle`, `Dispose()`.
  - Task 8: `Controller(Settings settings)` with `Mode Mode`, `void OnResume()`, `void Reset()`; `enum Mode { Idle, Armed, Finishing, Paused }`.
  - Task 9: `ActivityMonitor(IReadOnlyList<IBusySignal> signals, TimeSpan grace)` with `bool Hold { get; set; }`; `Engine(ActivityMonitor activity, Controller controller, RestoreJournal journal, IPowerPolicy policy, IKeepAwake keepAwake, ISystemPower power, ILog log, IClock clock)` (no `Settings` parameter, per Task 9) with `LidState Lid { get; set; }`, `bool Paused { get; set; }`, `string Status`, `void Tick()`, `void RestoreNow()`; `DryRunPolicy(IPowerPolicy real, ILog log)`, `DryRunKeepAwake(ILog log)`, `DryRunSystemPower(ISystemPower real, ILog log)`.
  - Task 10: `Autostart.Set(bool enabled, string exePath)`; `WatchdogTask.Register(string exePath, ILog log)`, `WatchdogTask.Unregister(ILog log)`; `Watchdog.Run(ILog log)`.
- Produces (namespace `Nightcrew.App`; nothing later depends on these):
  - `public sealed record ParsedArgs(bool Watchdog, bool Uninstall, bool DryRun);`
  - `public static class CommandLine { public static ParsedArgs Parse(string[] args); }`: matches `--watchdog`, `--uninstall` and `--dry-run` exactly, ignoring case; anything else is ignored.
  - `public sealed class TrayIcons : IDisposable { public Icon Idle { get; } public Icon Live { get; } public Icon LidClosed { get; } }`: grey, green and blue 16x16 dots drawn with `Bitmap.GetHicon` and `Icon.FromHandle`; `Dispose` calls `DestroyIcon` on each handle.
  - `public sealed class TrayApp : ApplicationContext`, constructor `TrayApp(Settings settings, bool dryRun, string exePath, PowerEventsWindow events, ActivityMonitor activity, Controller controller, Engine engine, ILog log)`. It subscribes to `events` but does not dispose it; `Program` owns the window's lifetime because `SystemPower` needs its handle before the engine exists.
  - `Program.Main(string[] args)` (`[STAThread]`, returns the exit code).

**Wiring decisions:**
- **One lock.** Every call into the engine, controller, activity monitor or settings goes through one lock: the tick, menu changes, lid changes, resume, session end and its cancellation, and quit. A `System.Threading.Timer` ticks every `SampleSeconds` (at least 1). If the lock is busy (the previous tick or a menu action is still running), the tick is skipped. That is the re-entrancy guard. After each tick, the status and icon are captured under the lock and posted to the UI thread through the `SynchronizationContext`.
- **Immediate tick.** Hold awake, Pause, a lid change, resume, a cancelled session end and any settings change apply under the lock, then restart the timer at zero. A paused or held state, or the lock on lid close, takes effect at once instead of up to 10 seconds later.
- **Icon.** Green when the controller is Armed, blue when Armed with the lid closed, grey otherwise (Idle, Finishing, Paused). The tooltip is the status, cut to 63 characters (the NotifyIcon limit). In a dry run the status starts with `[dry run]`.
- **Dry run.** `--dry-run` or `Settings.DryRun` swaps in the Task 9 dry-run wrappers. Start at login, the watchdog task and the Start-at-login menu's Run-key write are all skipped. The flag is held in a local and never written into `settings.json`, so a later menu change cannot save `DryRun=true` for good.
- **Startup restore.** A leftover record is restored with the real `PowrProfPolicy`, even in a dry run. A record left by a crashed real run must really be written back. With `DryRunPolicy`, the write would only be logged and the record deleted, losing the owner's values. A record left by a dry run is safe either way: restoring it writes nothing, because nothing was changed. If that startup restore fails and Nightcrew was started as a dry run, it refuses to start: it shows a message asking the user to start it normally, so the dry-run journal never deletes a real record.
- **Single instance.** The mutex is `Local\Nightcrew.Instance`, and the app waits up to 5 seconds for it, per Task 10's note: the watchdog holds it briefly at logon. If it's already running, the app exits with 0.
- **Quit.** Quit and dispose stop the timer, then run `engine.RestoreNow()` once under the lock, then hide the icon. `ExitThread` ends `Application.Run`. `Program`'s `using` declarations then dispose the tray before the GPU query, the keep-awake handle and the power-events window.
- **Session end.** On `SessionEnding` (`WM_QUERYENDSESSION`, answered TRUE by Task 7) the app, under the lock, sets a flag that makes every timer tick return without ticking, runs `engine.RestoreNow()`, then `controller.Reset()`. No tick can re-arm while Windows ends the session. If the session end is cancelled (`SessionEndCancelled`), the flag is cleared under the lock and the timer restarts at zero, so ticks resume and re-arm if work is still live. When the session really ends (`SessionEnded`), the app, under the lock, disposes the timer and calls `ExitThread`; the settings were already restored on `SessionEnding`. `Application.Run` returns and `Program` disposes everything as on Quit.
- **Uninstall.** `--uninstall` first waits up to 5 seconds for the instance mutex `Local\Nightcrew.Instance` (`WaitOne(TimeSpan.FromSeconds(5))`), because the watchdog holds it briefly while it checks (Task 10). If it still can't take it (the tray app is running), it shows "Quit Nightcrew from the tray first, then run nightcrew --uninstall again." and returns 1. Otherwise it restores the power settings, removes the Run value, unregisters the watchdog task, shows the result, returns 0 and releases the mutex.
- **Open log.** `Process.Start(AppPaths.LogFile)` opens the log in the shell's default app.

- [ ] **Step 1: Write the failing tests**

Only `CommandLine` is unit-tested. The tray, the timer and `Program` touch the real power APIs and the Run key, so they are checked by the build and by the hardware checklist (Task 12), never run from a test.

`tests/Nightcrew.Tests/CommandLineTests.cs`:
```csharp
using Nightcrew.App;
using Xunit;

namespace Nightcrew.Tests;

public sealed class CommandLineTests
{
    [Fact]
    public void No_arguments_starts_the_tray()
    {
        Assert.Equal(new ParsedArgs(Watchdog: false, Uninstall: false, DryRun: false), CommandLine.Parse([]));
    }

    [Theory]
    [InlineData("--watchdog", true, false, false)]
    [InlineData("--uninstall", false, true, false)]
    [InlineData("--dry-run", false, false, true)]
    public void Each_verb_sets_its_flag(string arg, bool watchdog, bool uninstall, bool dryRun)
    {
        Assert.Equal(new ParsedArgs(watchdog, uninstall, dryRun), CommandLine.Parse([arg]));
    }

    [Fact]
    public void Verbs_ignore_case()
    {
        Assert.Equal(new ParsedArgs(true, false, true), CommandLine.Parse(["--WATCHDOG", "--Dry-Run"]));
    }

    [Fact]
    public void Unknown_arguments_are_ignored()
    {
        Assert.Equal(new ParsedArgs(false, false, true), CommandLine.Parse(["--verbose", "dry-run", "--dry-run", "x"]));
        Assert.Equal(new ParsedArgs(false, false, false), CommandLine.Parse(["/watchdog", "--uninstall=yes", ""]));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test -c Release -- --filter-class Nightcrew.Tests.CommandLineTests` (after the env line from the plan's Global Constraints)
Expected: build error `CS0246: The type or namespace name 'ParsedArgs' could not be found`

- [ ] **Step 3: Write the implementation**

`src/Nightcrew/App/CommandLine.cs`:
```csharp
namespace Nightcrew.App;

public sealed record ParsedArgs(bool Watchdog, bool Uninstall, bool DryRun);

/// <summary>The three verbs; anything else on the command line is ignored.</summary>
public static class CommandLine
{
    public static ParsedArgs Parse(string[] args)
    {
        bool Has(string verb) => args.Any(arg => string.Equals(arg, verb, StringComparison.OrdinalIgnoreCase));
        return new ParsedArgs(Has("--watchdog"), Has("--uninstall"), Has("--dry-run"));
    }
}
```

`src/Nightcrew/App/TrayIcons.cs`:
```csharp
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace Nightcrew.App;

/// <summary>Three 16x16 dots drawn at runtime, so the exe needs no .ico files.</summary>
public sealed class TrayIcons : IDisposable
{
    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    private bool _disposed;

    public Icon Idle { get; } = Dot(Color.FromArgb(150, 150, 150));
    public Icon Live { get; } = Dot(Color.FromArgb(46, 160, 67));
    public Icon LidClosed { get; } = Dot(Color.FromArgb(47, 129, 247));

    private static Icon Dot(Color color)
    {
        using var bitmap = new Bitmap(16, 16);
        using (var graphics = Graphics.FromImage(bitmap))
        using (var brush = new SolidBrush(color))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.FillEllipse(brush, 2, 2, 12, 12);
        }
        return Icon.FromHandle(bitmap.GetHicon());
    }

    /// <summary>Icon.FromHandle does not own its handle, so each one is destroyed here.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var icon in new[] { Idle, Live, LidClosed })
        {
            var handle = icon.Handle;
            icon.Dispose();
            DestroyIcon(handle);
        }
    }
}
```

`src/Nightcrew/App/TrayApp.cs`:
```csharp
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using Nightcrew.Core;
using Nightcrew.Platform;

namespace Nightcrew.App;

/// <summary>The tray icon and its menu. A background timer ticks the engine; every engine call holds one lock.</summary>
public sealed class TrayApp : ApplicationContext
{
    private readonly object _gate = new();
    private readonly Settings _settings;
    private readonly bool _dryRun;
    private readonly Controller _controller;
    private readonly Engine _engine;
    private readonly ILog _log;
    private readonly TrayIcons _icons = new();
    private readonly ContextMenuStrip _menu = new();
    private readonly ToolStripMenuItem _status = new() { Enabled = false };
    private readonly NotifyIcon _tray = new();
    private readonly SynchronizationContext _ui;
    private readonly TimeSpan _period;
    private readonly System.Threading.Timer _timer;
    private bool _closed;
    private bool _sessionEnding;

    /// <summary>Subscribes to <paramref name="events"/> but does not own it; the caller disposes it after this.</summary>
    public TrayApp(Settings settings, bool dryRun, string exePath, PowerEventsWindow events,
        ActivityMonitor activity, Controller controller, Engine engine, ILog log)
    {
        _settings = settings;
        _dryRun = dryRun;
        _controller = controller;
        _engine = engine;
        _log = log;
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _period = TimeSpan.FromSeconds(Math.Max(1, settings.SampleSeconds));
        _timer = new System.Threading.Timer(_ => OnTimer(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        _menu.Items.Add(_status);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(Toggle("Hold awake", false, on => Apply("Hold awake", () => activity.Hold = on)));
        _menu.Items.Add(Toggle("Pause", false, on => Apply("Pause", () => engine.Paused = on)));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(Choice("Battery floor", [10, 15, 20, 30, 0], "%", settings.BatteryFloorPercent,
            value => settings.BatteryFloorPercent = value));
        _menu.Items.Add(Choice("Lid-closed limit", [2, 4, 8, 12, 0], " h", settings.LidClosedCapHours,
            value => settings.LidClosedCapHours = value));
        _menu.Items.Add(Toggle("Lock when lid closes", settings.LockOnLidClose,
            on => Apply("Lock when lid closes", () => { settings.LockOnLidClose = on; Save(); })));
        _menu.Items.Add(Toggle("Start at login", settings.StartAtLogin, on => Apply("Start at login", () =>
        {
            settings.StartAtLogin = on;
            Save();
            if (!dryRun) Autostart.Set(on, exePath);
        })));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Open log", null, (_, _) => Guarded("Open log", () => Process.Start(AppPaths.LogFile)?.Dispose()));
        _menu.Items.Add("Quit", null, (_, _) => Quit());

        if (!dryRun)
        {
            if (settings.StartAtLogin) Guarded("Start at login", () => Autostart.Set(true, exePath));
            Guarded("Watchdog task", () => WatchdogTask.Register(exePath, log));
        }

        _tray.ContextMenuStrip = _menu;
        var (status, icon) = Snapshot();
        ShowStatus(status, icon);
        _tray.Visible = true;

        engine.Lid = events.Lid;
        events.LidChanged += lid => Apply("Lid change", () => engine.Lid = lid);
        events.Resumed += () => Apply("Resume", controller.OnResume);
        events.SessionEnding += () => Guarded("Restore at session end", () =>
        {
            _sessionEnding = true; // no tick may re-arm while Windows ends the session
            engine.RestoreNow();
            controller.Reset();
        });
        events.SessionEndCancelled += () => Apply("Session end cancelled", () => _sessionEnding = false);
        events.SessionEnded += () => Guarded("Exit at session end", () =>
        {
            _timer.Dispose(); // the settings were already restored on SessionEnding
            ExitThread();
        });
        _timer.Change(TimeSpan.Zero, _period);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Shutdown();
            _tray.Dispose();
            _menu.Dispose();
            _icons.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>Timer thread. Skips the tick if the previous one, or a menu action, still holds the lock.</summary>
    private void OnTimer()
    {
        if (!Monitor.TryEnter(_gate)) return;
        try
        {
            if (_closed || _sessionEnding) return;
            _engine.Tick();
            var (status, icon) = Snapshot();
            _ui.Post(_ => ShowStatus(status, icon), null);
        }
        catch (Exception ex)
        {
            _log.Warn($"Tick failed: {ex.Message}");
        }
        finally
        {
            Monitor.Exit(_gate);
        }
    }

    /// <summary>Call under the lock.</summary>
    private (string Status, Icon Icon) Snapshot()
    {
        var status = (_dryRun ? "[dry run] " : "") + _engine.Status;
        var icon = _controller.Mode != Mode.Armed ? _icons.Idle
            : _engine.Lid == LidState.Closed ? _icons.LidClosed
            : _icons.Live;
        return (status, icon);
    }

    /// <summary>UI thread.</summary>
    private void ShowStatus(string status, Icon icon)
    {
        if (_closed) return;
        _status.Text = status;
        _tray.Text = status.Length > 63 ? status.Substring(0, 63) : status;
        _tray.Icon = icon;
    }

    private void Guarded(string what, Action action)
    {
        lock (_gate)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                _log.Warn($"{what} failed: {ex.Message}");
            }
        }
    }

    /// <summary>Applies a change under the lock, then ticks at once so it takes effect and shows.</summary>
    private void Apply(string what, Action change)
    {
        Guarded(what, change);
        if (!_closed) _timer.Change(TimeSpan.Zero, _period);
    }

    private void Save() => SettingsStore.Save(AppPaths.SettingsFile, _settings);

    private static ToolStripMenuItem Toggle(string text, bool initial, Action<bool> apply)
    {
        var item = new ToolStripMenuItem(text) { Checked = initial };
        item.Click += (_, _) =>
        {
            item.Checked = !item.Checked;
            apply(item.Checked);
        };
        return item;
    }

    /// <summary>A submenu of values with one checked; 0 is shown as Off.</summary>
    private ToolStripMenuItem Choice(string text, int[] values, string unit, int current, Action<int> set)
    {
        var parent = new ToolStripMenuItem(text);
        foreach (var value in values)
        {
            var item = new ToolStripMenuItem(value == 0 ? "Off" : value + unit) { Checked = value == current };
            item.Click += (_, _) =>
            {
                foreach (ToolStripMenuItem sibling in parent.DropDownItems) sibling.Checked = sibling == item;
                Apply(text, () => { set(value); Save(); });
            };
            parent.DropDownItems.Add(item);
        }
        return parent;
    }

    private void Quit()
    {
        Shutdown();
        ExitThread();
    }

    /// <summary>Stops ticking and puts the owner's power settings back, once.</summary>
    private void Shutdown()
    {
        _timer.Dispose();
        Guarded("Restore on exit", () =>
        {
            if (_closed) return;
            _closed = true;
            _engine.RestoreNow();
        });
        _tray.Visible = false;
    }
}
```

`src/Nightcrew/Program.cs`:
```csharp
using System.Windows.Forms;
using Nightcrew.App;
using Nightcrew.Core;
using Nightcrew.Platform;

namespace Nightcrew;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var parsed = CommandLine.Parse(args);
        var log = new FileLog(AppPaths.LogFile);
        var exePath = Application.ExecutablePath;
        if (parsed.Watchdog) return Watchdog.Run(log);
        if (parsed.Uninstall) return Uninstall(exePath, log);

        using var instance = new Mutex(initiallyOwned: false, @"Local\Nightcrew.Instance");
        if (!TakeInstance(instance)) return 0;
        try
        {
            RunTray(parsed.DryRun, exePath, log);
            return 0;
        }
        finally
        {
            instance.ReleaseMutex();
        }
    }

    /// <summary>False if Nightcrew is already running. Waits briefly because the watchdog holds the mutex while it checks.</summary>
    private static bool TakeInstance(Mutex instance)
    {
        try
        {
            return instance.WaitOne(TimeSpan.FromSeconds(5));
        }
        catch (AbandonedMutexException)
        {
            return true;
        }
    }

    /// <summary>
    /// Refuses while the tray app runs, because it could change the power settings again after the restore.
    /// Waits up to 5 seconds first, because the watchdog holds the mutex briefly while it checks.
    /// </summary>
    private static int Uninstall(string exePath, ILog log)
    {
        using var instance = new Mutex(initiallyOwned: false, @"Local\Nightcrew.Instance");
        bool owned;
        try { owned = instance.WaitOne(TimeSpan.FromSeconds(5)); }
        catch (AbandonedMutexException) { owned = true; }
        if (!owned)
        {
            MessageBox.Show("Quit Nightcrew from the tray first, then run nightcrew --uninstall again.", "Nightcrew");
            return 1;
        }

        try
        {
            var restored = new RestoreJournal(new PowrProfPolicy(), AppPaths.RestoreFile, log).Restore();
            Autostart.Set(false, exePath);
            WatchdogTask.Unregister(log);
            log.Info("Uninstalled: start at login and the watchdog task removed");
            MessageBox.Show(
                restored ? "Nightcrew removed." : "Nightcrew removed, but your power settings could not be restored. See the log in %LOCALAPPDATA%\\Nightcrew.",
                "Nightcrew");
            return 0;
        }
        finally
        {
            instance.ReleaseMutex();
        }
    }

    private static void RunTray(bool dryRunFlag, string exePath, ILog log)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        var settings = SettingsStore.Load(AppPaths.SettingsFile);
        var dryRun = dryRunFlag || settings.DryRun; // the flag is not saved into settings
        log.Info(dryRun ? "Nightcrew started (dry run)" : "Nightcrew started");

        // A record left by a crash or power cut: put the owner's settings back first. This uses the real
        // policy even in a dry run, so a dry run never deletes a real record without restoring it.
        var realPolicy = new PowrProfPolicy();
        var leftover = new RestoreJournal(realPolicy, AppPaths.RestoreFile, log);
        if (leftover.HasRecord) leftover.Restore();
        if (dryRun && leftover.HasRecord)
        {
            // The dry-run journal only logs writes, so it must never touch a real record it could not restore.
            log.Warn("Dry run refused: a real restore record could not be restored");
            MessageBox.Show("Nightcrew couldn't put back power settings left by an earlier run. Start it normally (without --dry-run) so it can restore them.", "Nightcrew");
            return;
        }

        using var events = new PowerEventsWindow();
        using var realKeepAwake = new KeepAwake();
        using var gpu = new PdhGpuUsage();
        var realPower = new SystemPower(() => events.Handle);
        IPowerPolicy policy = dryRun ? new DryRunPolicy(realPolicy, log) : realPolicy;
        ISystemPower power = dryRun ? new DryRunSystemPower(realPower, log) : realPower;
        IKeepAwake keepAwake = dryRun ? new DryRunKeepAwake(log) : realKeepAwake;
        var journal = new RestoreJournal(policy, AppPaths.RestoreFile, log);

        IBusySignal[] signals =
        [
            new TranscriptSignal(new JsonlFileScanner(), settings.TranscriptRoots, settings.IgnorePathContains),
            new AgentTreeSignal(new ToolhelpProcessTable(), settings),
            new GpuSignal(gpu, settings.GpuPercent),
        ];
        var activity = new ActivityMonitor(signals, TimeSpan.FromMinutes(settings.GraceMinutes));
        var controller = new Controller(settings);
        var engine = new Engine(activity, controller, journal, policy, keepAwake, power, log, new SystemClock());

        using var tray = new TrayApp(settings, dryRun, exePath, events, activity, controller, engine, log);
        Application.Run(tray);
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test -c Release -- --filter-class Nightcrew.Tests.CommandLineTests`  Expected: PASS (6 tests)
Then `dotnet build -c Release` must report 0 warnings and 0 errors, `dotnet test -c Release` must pass, and `powershell -NoProfile -ExecutionPolicy Bypass -File build/check-single-file.ps1` must print `Single-file gate passed`. Do not run `nightcrew.exe`: it changes power settings and registers the watchdog task.

- [ ] **Step 5: Commit**

```bash
git add src/Nightcrew/Program.cs src/Nightcrew/App/CommandLine.cs src/Nightcrew/App/TrayIcons.cs src/Nightcrew/App/TrayApp.cs tests/Nightcrew.Tests/CommandLineTests.cs
git commit -m "feat: tray app, command-line verbs and startup wiring"
```

### Task 12: Release workflow, README and hardware checklist

**Files:**
- Create: `.github/workflows/release.yml`
- Create: `README.md`
- Create: `docs/hardware-checklist.md`
- Create: `CONTRIBUTING.md`
- Test: none. This task has no C#. Step 2 parses the workflow YAML and runs its build commands locally.

**Interfaces:**
- Consumes:
  - `global.json`, `build/check-single-file.ps1` and the Release output `src/Nightcrew/bin/Release/net48/nightcrew.exe` (Task 1)
  - `Settings` keys and defaults, and the `AppPaths` files `settings.json`, `restore.json` and `nightcrew.log` (skeleton)
  - the controller rules: grace, away wait, monitor-off then hibernate, floor, cap, Spent, and the unattended rule (the cap's lid-closed time and the lock at arm only with the lid closed and 5 minutes without input; the floor whenever the lid is closed on battery) (Tasks 8 and 9)
  - the scheduled task name `Nightcrew Watchdog` and the Run value `Nightcrew` (Task 10)
  - the verbs `--dry-run` and `--uninstall` (which refuses while the tray app runs), and the tray menu items (Task 11)
- Produces: no code. For each `v*` tag, a GitHub release with the assets `nightcrew.exe` and `nightcrew.exe.sha256` and a build attestation for `nightcrew.exe`. User docs (`README.md`), the owner's manual test plan (`docs/hardware-checklist.md`) and contributor rules (`CONTRIBUTING.md`).

- [ ] **Step 1: Write the release workflow**

The workflow runs on a `v*` tag. It tests, builds, runs the single-file gate and writes `nightcrew.exe.sha256` as `<hash>  nightcrew.exe` with an LF ending, so `sha256sum -c` accepts it. It then attests the exe and creates a draft release, uploads both files and publishes. Publishing only after the upload means the release never appears without its assets. The actions use version tags, matching `ci.yml`. Pin them to full commit SHAs when the repo is first published; the comment at the top of the file says so.

`.github/workflows/release.yml`:
```yaml
# Builds nightcrew.exe from a v* tag and publishes it as a GitHub release
# with a SHA-256 file and a build attestation.
# Before the first public release, pin each action below to its full commit
# SHA, for example `actions/checkout@<40-char sha> # v7`.
name: release

on:
  push:
    tags: ['v*']

permissions:
  contents: write
  id-token: write
  attestations: write
  artifact-metadata: write

jobs:
  release:
    runs-on: windows-2025
    env:
      DOTNET_CLI_TELEMETRY_OPTOUT: 1
      DOTNET_NOLOGO: 1
    steps:
      - uses: actions/checkout@v7
        with:
          persist-credentials: false
      - uses: actions/setup-dotnet@v6
        with:
          global-json-file: global.json
      - run: dotnet test -c Release
      - run: dotnet build -c Release
      - run: pwsh -File build/check-single-file.ps1
      - name: Compute SHA-256
        run: |
          $hash = (Get-FileHash src/Nightcrew/bin/Release/net48/nightcrew.exe -Algorithm SHA256).Hash.ToLowerInvariant()
          Set-Content -Path nightcrew.exe.sha256 -Value "$hash  nightcrew.exe`n" -Encoding ascii -NoNewline
          Get-Content nightcrew.exe.sha256
      - uses: actions/attest@v4
        with:
          subject-path: src/Nightcrew/bin/Release/net48/nightcrew.exe
      - name: Publish release
        shell: bash
        env:
          GH_TOKEN: ${{ github.token }}
          GH_REPO: ${{ github.repository }}
        run: |
          gh release create "$GITHUB_REF_NAME" --draft --title "Nightcrew $GITHUB_REF_NAME" --generate-notes
          gh release upload "$GITHUB_REF_NAME" src/Nightcrew/bin/Release/net48/nightcrew.exe nightcrew.exe.sha256
          gh release edit "$GITHUB_REF_NAME" --draft=false
```

- [ ] **Step 2: Validate the workflow**

Run (after the env line from the plan's Global Constraints):
```bash
python -c "import yaml; d = yaml.safe_load(open('.github/workflows/release.yml')); print(d[True]); print(d['permissions']); print([s.get('uses') or s.get('name') or s.get('run') for s in d['jobs']['release']['steps']])"
dotnet test -c Release
dotnet build -c Release
powershell -NoProfile -File build/check-single-file.ps1
```
Expected:
- The YAML parses. PyYAML reads the `on` key as `True`.
  ```
  {'push': {'tags': ['v*']}}
  {'contents': 'write', 'id-token': 'write', 'attestations': 'write', 'artifact-metadata': 'write'}
  ['actions/checkout@v7', 'actions/setup-dotnet@v6', 'dotnet test -c Release', 'dotnet build -c Release', 'pwsh -File build/check-single-file.ps1', 'Compute SHA-256', 'actions/attest@v4', 'Publish release']
  ```
- `Test run summary: Passed!`
- `Build succeeded.` with 0 warnings
- `Single-file gate passed`

If PyYAML isn't installed, read the file back and check that every step is a list item under `steps:` with consistent two-space indentation.

- [ ] **Step 3: Write the README**

`README.md`:
```markdown
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
```

- [ ] **Step 4: Write the hardware checklist**

`docs/hardware-checklist.md`:
````markdown
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
````

- [ ] **Step 5: Write the contributing guide**

`CONTRIBUTING.md`:
````markdown
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
- Don't add `.ico` or other resource files next to the exe. Icons are drawn
  in code.
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
| `Nightcrew.App` | Tray UI and wiring |

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
````

- [ ] **Step 6: Commit**

```bash
git add .github/workflows/release.yml README.md docs/hardware-checklist.md CONTRIBUTING.md
git commit -m "feat: release workflow, README, hardware checklist and contributing guide"
```
