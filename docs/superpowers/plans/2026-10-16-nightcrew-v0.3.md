# Nightcrew v0.3.0 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship v0.3.0. It fixes the audit's safety holes in "your settings always come back". It also reads Claude Code's status files, so quiet commands and lid-closed approval waits keep the laptop awake.

**Architecture:**
- Each fix stays in the class that owns the behaviour:
  - `RestoreJournal` and `IPowerPolicy`: plan, lost record, DC value
  - `Controller`: lock, per-episode cap
  - `EfficiencyMode`: inherited priority
  - `TrayApp` and `Program`: freeze, first and second start, exit
- The status files become one more `IBusySignal`, read through a thin Platform reader.
- Pure logic stays in Core, tested with fakes. The Windows calls stay in Platform.

**Tech Stack:** C# 14 on .NET Framework 4.8, WinForms and WPF, xUnit on Microsoft.Testing.Platform, DataContractJsonSerializer.

**Spec:** `docs/superpowers/specs/2026-10-16-nightcrew-v0.3-design.md`. It was approved by the owner on 16 October 2026.

## Global Constraints

These come from CONTRIBUTING.md and the spec. Every task follows them.

- Target `net48` with `LangVersion` 14. No default interface methods. Use no `Span`, `ValueTask` or `System.Text.Json`, and no array range slicing.
- One exe. No runtime NuGet packages. JSON only through `Nightcrew.Core.Json`. Persisted classes use `[DataContract]` and `[DataMember]`.
- Warnings are errors. The build must have 0 warnings. Fix nullable warnings; don't suppress them.
- Tests never change the machine. Use fakes for power writes, power requests, sleep, hibernate, monitor off, lock, the Run key and scheduled tasks. Tests may read real power values, the process list and files.
- `Nightcrew.Core` has no P/Invoke.
- The words follow the house style: sentence case, short, contractions, no "please", no "successfully". The settings page has no explanation text.
- Never open a file under `~\.claude` except `<digits>.json` in the sessions folder.
- Never keep, log or show `cwd`, `name` or `sessionId`.
- No network code. No hooks. No new words on the main panel.
- New setting label: exactly `Stay awake for approvals`. Its key is `StayAwakeForApprovals`, default `true`. It goes directly after `Lock when lid closes`.
- New settings key `ClaudeSessionsDir`, default `%USERPROFILE%\.claude\sessions`.
- The version is `0.3.0`, and the startup log line reads `Nightcrew 0.3.0 started`. In a dry run it reads `Nightcrew 0.3.0 started (dry run)`.

**Environment** (Git Bash, from the worktree root `C:\Users\Minex\Documents\nightcrew-v03`):

```bash
export DOTNET_ROOT="$LOCALAPPDATA/Microsoft/dotnet-sdk"; export PATH="$DOTNET_ROOT:$PATH"; export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
```

- One test class: `dotnet test -c Release -- --filter-class Nightcrew.Tests.<Class>`
- Everything: `dotnet build -c Release && dotnet test -c Release && powershell -NoProfile -File build/check-single-file.ps1`

Baseline before Task 1: 393 tests, all passing.

## Review Focus

1. **An old `restore.json` from v0.2.2** (no `Scheme` field) is left by a crash before the upgrade. The startup restore must still put the values back on the active plan. Test: `Old_record_without_a_scheme_restores_the_active_plan` (Task 2).
2. **The plan switches during work, and the old plan can't be written** (a policy lock, for example). The record must not be overwritten before the old plan has been restored. Test: `Plan_switch_keeps_the_old_record_when_the_old_plan_cannot_be_restored` (Task 2).
3. **A status file is mid-write, so it is empty or truncated**, or its `pid` now belongs to a new process. It must be skipped silently, and it must never count. Tests: `Empty_or_truncated_file_is_skipped` and `Reused_pid_does_not_count` (Task 5).
4. **A docked user whose real settings are Do nothing and Never** arms many times. Their settings must stay theirs every time. Test: `Docked_owner_with_do_nothing_and_never_keeps_their_values_across_episodes` (Task 3).
5. **Windows sleeps for hours** while armed. Waking must not look like a freeze. The guard uses the clock that excludes sleep. Test: `Sleep_does_not_count_as_a_freeze` (Task 7).

---

### Task 1: Damaged files can't stop Nightcrew (spec 4.4, plus new setting keys)

**Files:**
- Modify: `src/Nightcrew/Core/RestoreJournal.cs` (the `OwnerValues` catch, and `Load`)
- Modify: `src/Nightcrew/Core/Settings.cs` (add 2 keys, `[OnDeserialized]` normalise)
- Modify: `src/Nightcrew/Platform/SettingsStore.cs` (catch all, `out bool created`)
- Test: `tests/Nightcrew.Tests/RestoreJournalTests.cs`, `tests/Nightcrew.Tests/SettingsStoreTests.cs`

**Interfaces:**
- Produces:
  - `Settings.StayAwakeForApprovals : bool` (default `true`)
  - `Settings.ClaudeSessionsDir : string` (default `@"%USERPROFILE%\.claude\sessions"`)
  - `static Settings SettingsStore.Load(string path, out bool created)`. `created` is true when the file didn't exist before this call. The existing `Load(string path)` stays and calls the new one.

- [ ] **Step 1: Write the failing tests.**

  In `RestoreJournalTests`, extend the `Corrupt_record_is_set_aside_and_restore_fails` theory with two more cases:
  - `[InlineData("\0\0\0\0\0\0\0\0")]`, which a power cut leaves
  - `[InlineData("{\"OwnerLidAc\":\"x\"}")]`

  The existing assertions stay: OwnerValues is null, Restore returns false, the record is moved to `.corrupt`, there is one warning and there are no writes.

  In `SettingsStoreTests`, add:

  ```csharp
  [Theory]
  [InlineData("\0\0\0\0")]
  [InlineData("[1,2]")]
  public void Unreadable_file_of_any_kind_is_set_aside_and_defaults_are_used(string text)
  // write text to settings.json; Load(path) returns GraceMinutes 5 and doesn't throw; settings.json.corrupt exists

  [Fact]
  public void Null_lists_become_defaults()
  // {"TranscriptRoots":null,"AgentProcessNames":null,"IgnorePathContains":null,"SelfCpuAgentNames":null,
  //  "LeaveAlone":null,"LeaveAloneLabels":null,"ConnectorHostNames":null,"Mark":null,"ClaudeSessionsDir":null}
  // → every list equals new Settings()'s default; Mark == "lantern"; ClaudeSessionsDir == @"%USERPROFILE%\.claude\sessions"

  [Theory]
  [InlineData("GraceMinutes", 0, 1)] [InlineData("GraceMinutes", 500, 60)]
  [InlineData("AwayMinutes", -3, 1)] [InlineData("SampleSeconds", 0, 1)] [InlineData("SampleSeconds", 999, 60)]
  [InlineData("GpuPercent", 0, 5)] [InlineData("ToolCpuPercent", 0, 1)] [InlineData("SelfCpuPercent", 0, 0.5)]
  [InlineData("BatteryFloorPercent", 99, 90)] [InlineData("BatteryFloorPercent", -1, 0)]
  [InlineData("LidClosedCapHours", 100, 48)] [InlineData("LidClosedCapHours", -2, 0)]
  public void Numbers_are_clamped_into_range(string key, double value, double expected)

  [Fact]
  public void Unknown_mark_becomes_the_lantern()           // "Mark":"comet" → "lantern"

  [Fact]
  public void New_keys_have_their_defaults()                // {} → StayAwakeForApprovals true, ClaudeSessionsDir default

  [Fact]
  public void Load_reports_whether_the_file_was_created()  // first Load(path, out created) → true; second → false
  ```

- [ ] **Step 2: Run the tests and check that they fail.**

  `dotnet test -c Release -- --filter-class Nightcrew.Tests.SettingsStoreTests` and `--filter-class Nightcrew.Tests.RestoreJournalTests`.

  Expected: FAIL. The zero-filled record throws `XmlException`. The new members don't exist.

- [ ] **Step 3: Implement.**
  - **`RestoreJournal`:**
    - `Load` treats any exception from parsing the text as corrupt. Use `catch (Exception ex) when (ex is not IOException and not UnauthorizedAccessException)`.
    - Reading the file stays outside that catch, so a locked file is not moved aside.
    - `OwnerValues` returns null for any exception.
  - **`Settings`:**
    - Add the 2 keys.
    - Add `[OnDeserialized] private void Normalize(StreamingContext _)`. It replaces a null list, dictionary, `Mark` or `ClaudeSessionsDir` with its default. It clamps the numbers to the spec 4.4 ranges: Grace 1–60, Away 1–60, SampleSeconds 1–60, GpuPercent 5–100, ToolCpuPercent 1–100, SelfCpuPercent 0.5–100, BatteryFloor 0–90, LidClosedCap 0–48.
    - A `Mark` that isn't a known name becomes `MarkKinds.LanternName`.
  - **`SettingsStore`:**
    - `Load(path, out created)` catches every exception from deserialising.
    - The copy to `.corrupt` is in its own try. A failing copy is ignored.
    - It uses defaults and saves them.

- [ ] **Step 4: Run both classes, then the full suite.** Expected: PASS, with 393 + new tests.

- [ ] **Step 5: Commit.** `fix: damaged restore and settings files can't stop Nightcrew`

---

### Task 2: The record knows its power plan (spec 4.7)

**Files:**
- Modify: `src/Nightcrew/Core/Contracts.cs` (the `IPowerPolicy` shape)
- Modify: `src/Nightcrew/Platform/PowrProfPolicy.cs`, `src/Nightcrew/Platform/DryRun.cs` (`DryRunPolicy`)
- Modify: `src/Nightcrew/Core/RestoreJournal.cs`, `src/Nightcrew/Core/Engine.cs`
- Create: `tests/Nightcrew.Tests/FakePolicy.cs` (a shared multi-plan fake)
- Modify: `tests/Nightcrew.Tests/RestoreJournalTests.cs`, `EngineTests.cs` and `EngineSnapshotTests.cs`, so they use the shared fake and drop their private ones
- Test: `RestoreJournalTests`, `EngineTests`, `PowrProfPolicyTests`

**Interfaces:**
- Produces:

  ```csharp
  public interface IPowerPolicy
  {
      Guid ActiveScheme();
      PowerValues Read(Guid scheme);
      /// Writes all four values to the scheme; calls PowerSetActiveScheme only when it is the active scheme; verifies the read-back. Throws on failure.
      void Write(Guid scheme, PowerValues values);
      /// Windows' default values for the scheme (PowerReadACDefaultIndex / PowerReadDCDefaultIndex). Throws on failure.
      PowerValues ReadDefaults(Guid scheme);
      bool CanWrite(out string reason);
  }
  public static class PowerPolicyExtensions { public static PowerValues ReadActive(this IPowerPolicy policy) => policy.Read(policy.ActiveScheme()); }
  ```

  - `RestoreRecord.Scheme : string?`, as `[DataMember(EmitDefaultValue = false)]`. It holds the GUID as `"D"` text, and a null value means a v0.2.2 record.
  - `bool RestoreJournal.SchemeChanged`: true when a record with a scheme exists and the active scheme differs. It never throws.
  - `RestoreJournal.Arm()`. If `SchemeChanged`, it first restores the old plan and deletes the record. If that restore fails, it throws and leaves the record as it is. Then it captures the active plan and writes the arm values.
  - `RestoreJournal.Restore()` works on `record.Scheme` when it is set, and on the active plan otherwise.
  - The `FakePolicy` (tests):
    - `FakePolicy(PowerValues initial, List<string>? calls = null)` starts with one active plan.
    - `Guid AddScheme(PowerValues)` and `void Activate(Guid)` add and switch plans.
    - `PowerValues ValuesOf(Guid)` reads a plan; `Values` reads the active one.
    - The test switches are `FailWrites`, `FailWritesTo(Guid)`, `FailReads`, `CanWriteResult` and `Defaults`.
    - The counters are `Writes` and `CanWriteChecks`.
    - A write adds "Write" to `calls`.

- [ ] **Step 1: Write the failing tests** in `RestoreJournalTests`:

  ```csharp
  [Fact] public void Record_names_the_plan_it_armed()            // record file contains the active scheme GUID text
  [Fact] public void Restore_writes_back_to_the_recorded_plan_even_when_another_is_active()
  // arm on A; Activate(B) (B has its own values); Restore() → ValuesOf(A) == Owner, ValuesOf(B) unchanged, record gone
  [Fact] public void Arm_after_a_plan_switch_restores_the_old_plan_and_arms_the_new_one()
  // arm on A; Activate(B); Assert.True(journal.SchemeChanged); Arm() → ValuesOf(A)==Owner, ValuesOf(B)==ArmValues,
  // OwnerValues == B's original values, SchemeChanged false
  [Fact] public void Plan_switch_keeps_the_old_record_when_the_old_plan_cannot_be_restored()
  // arm on A; Activate(B); FailWritesTo(A); Assert.Throws<InvalidOperationException>(journal.Arm); record still names A with A's owner values
  [Fact] public void Old_record_without_a_scheme_restores_the_active_plan()
  // write v0.2.2 JSON (8 uint fields, no Scheme) by hand; Restore() → active plan == owner values
  ```

  In `EngineTests`:

  ```csharp
  [Fact] public void A_plan_switch_while_armed_moves_to_the_new_plan()
  // ArmWithLidOpen on A; _policy.Activate(B); Tick() → ValuesOf(A)==Owner, ValuesOf(B)==ArmValues, log has a line containing "Power plan changed"
  ```

- [ ] **Step 2: Run them and check that they fail.**

  `--filter-class Nightcrew.Tests.RestoreJournalTests`, then `EngineTests`. Expected: compile errors or FAIL.

- [ ] **Step 3: Implement.**
  - **`PowrProfPolicy`:**
    - `ActiveScheme()` is public.
    - `Read(scheme)` and `Write(scheme, values)`. Write activates the scheme only if it is the active one.
    - `ReadDefaults(scheme)` uses `PowerReadACDefaultIndex` and `PowerReadDCDefaultIndex`.
  - **`DryRunPolicy`** passes the reads through and logs the writes.
  - **`RestoreJournal`:**
    - Use the scheme everywhere.
    - `Pick` stays per field.
  - **`Engine.Tick`:**
    - After the controller commands, if `controller.Mode == Mode.Armed && journal.SchemeChanged`:
      1. Log `Power plan changed while armed; moving to the new plan`.
      2. Call `journal.Arm()` inside a try. On failure, log a warning and leave the record.
    - `ownerLid` falls back to `policy.ReadActive()`.
  - **Callers:** update every caller of `Read()`/`Write(values)`. These are Program, Watchdog and Uninstall through the journal, and `PowrProfPolicyTests`, which reads the active scheme.

- [ ] **Step 4: Run the full suite.** Expected: PASS.

- [ ] **Step 5: Commit.** `fix: the restore record knows its power plan and follows a plan switch`

---

### Task 3: Lost record, DC sleep-after, docked lock, per-episode cap (spec 4.5, 4.6, 4.9)

**Files:**
- Modify: `src/Nightcrew/Core/RestoreJournal.cs`, `src/Nightcrew/Core/Controller.cs`, `src/Nightcrew/Program.cs`
- Test: `RestoreJournalTests`, `ControllerTests`, `EngineTests`

**Interfaces:**
- Consumes: `IPowerPolicy.ReadDefaults(Guid)` (Task 2) and `Settings.LidClosedCapHours`.
- Produces:
  - `static PowerValues RestoreJournal.ArmValuesFor(int lidClosedCapHours)` returns `(0, 0, 0, cap > 0 ? (uint)(cap + 1) * 3600 : 0)`.
  - The constructor becomes `RestoreJournal(IPowerPolicy policy, string path, ILog log, Func<PowerValues>? armValues = null)`. The default is `() => ArmValuesFor(8)`.
  - `RestoreJournal.ArmValues` (static) is removed. Tests use `ArmValuesFor(8)`.
  - `static readonly PowerValues RestoreJournal.FallbackDefaults = new(1, 1, 1800, 900)`.

- [ ] **Step 1: Write the failing tests.**

  ```csharp
  // RestoreJournalTests
  [Fact] public void Arm_writes_dc_sleep_after_as_the_limit_plus_an_hour()
  // journal with armValues = () => ArmValuesFor(8) → Values == (0,0,0,32400)
  [Fact] public void Limit_off_keeps_never_on_dc()               // ArmValuesFor(0) == (0,0,0,0)
  [Fact] public void Arm_after_a_corrupt_record_saves_windows_defaults_not_our_values()
  // policy values = ArmValuesFor(8) (our leftovers), Defaults = (1,1,1200,600); record file "{ not json" →
  // Arm() → OwnerValues == (1,1,1200,600); one warning about the corrupt record plus one about using defaults
  // (replaces Arm_over_a_corrupt_record_captures_fresh_owner_values)
  [Fact] public void Arm_with_our_exact_values_and_no_record_saves_windows_defaults()
  // values == ArmValuesFor(8), no record → OwnerValues == Defaults
  [Fact] public void Docked_owner_with_do_nothing_and_never_keeps_their_values_across_episodes()
  // owner values (0,0,0,0), limit 8; Arm/Restore three times → after each Restore, Values == (0,0,0,0) and OwnerValues was (0,0,0,0)
  [Fact] public void Defaults_that_cannot_be_read_fall_back_to_sleep_30_and_15_minutes()
  // ReadDefaults throws → OwnerValues == FallbackDefaults

  // ControllerTests
  [Theory] [InlineData(LidAction.DoNothing, "Arm")] [InlineData(LidAction.Sleep, "Arm Lock")]
  public void Arming_with_the_lid_closed_locks_only_when_your_lid_action_is_not_do_nothing(LidAction owner, string expected)
  // Rig().Step(Away(owner: owner)) == expected
  [Fact] public void Closing_the_lid_while_armed_does_not_lock_when_your_lid_action_is_do_nothing()
  // ArmedRig(Open).Step(In(lid: Closed, owner: LidAction.DoNothing)) == ""
  [Fact] public void Lid_closed_time_counts_per_episode()
  // ArmedRig(Closed); Step(Away(), 30); work ends (Done(Sleep, 5)) → Finishing; Step(Away(), 1) re-arms →
  // LidClosedFor == 0 right after re-arming; Step(Away(), 10) → 10 min
  ```

- [ ] **Step 2: Run them and check that they fail.**

- [ ] **Step 3: Implement.**
  - **`Arm()`, when `Load` returns null:**
    1. Read the current values of the active scheme.
    2. If the load just set a record aside as corrupt, or `current == armValues()` and `armValues().StandbyDc != 0`: log the warning `No restore record, and the power settings look like Nightcrew's own; saving Windows' defaults as yours`. Then the owner values are `ReadDefaults(scheme)`. If that throws, they are `FallbackDefaults`.
    3. Otherwise the owner values are `current`.
    4. Write `armValues()`, and store it as `Written`.
  - **`Controller`:**
    - Lock only when `settings.LockOnLidClose && inputs.OwnerLid != LidAction.DoNothing`, in both `StepIdle` and `StepArmed`.
    - Set `LidClosedFor = TimeSpan.Zero` wherever the controller emits `Command.Arm`, in `StepIdle` and `StepFinishing`.
    - `Reset()` stays as it is.
  - **`Program`** passes `() => RestoreJournal.ArmValuesFor(settings.LidClosedCapHours)` to the journal.

- [ ] **Step 4: Run the full suite.** Expected: PASS.

  Update existing tests that pinned the old behaviour:
  - Arm-values assertions become `ArmValuesFor(8)`.
  - `Arm Lock` cases keep owner Sleep.

- [ ] **Step 5: Commit.** `fix: lost records, DC sleep-after, docked lock and a per-episode limit`

---

### Task 4: Apps started by a slowed app come back (spec 4.8)

**Files:**
- Modify: `src/Nightcrew/Core/EfficiencyRecord.cs`. Add optional `[DataMember(EmitDefaultValue = false)] long AppliedUtcTicks` and `bool Inherited`.
- Modify: `src/Nightcrew/Platform/EfficiencyMode.cs`. Add the inherited recording in `Apply`, and a sweep in `RestoreAll` and `RestoreFamily`.
- Test: `tests/Nightcrew.Tests/EfficiencyModeTests.cs`

**Interfaces:**
- Produces:
  - `EfficiencyMode(string path, ILog log, IProcessTable? table = null)`. The default is `new ToolhelpProcessTable()`, and the sweep uses its `List()` and `TryGetTimes`.
  - The public members are unchanged.

- [ ] **Step 1: Write the failing tests.** They use real processes, like the other tests in this class.

  ```csharp
  [Fact] public void A_child_started_while_its_parent_is_slowed_is_recorded_and_put_back()
  // parent = cmd /c "ping -n 3 127.0.0.1 >nul & ping -n 30 127.0.0.1 >nul" at Normal; Apply(Family(parent));
  // wait (≤10 s, poll) for parent's ping child; Assert child is Idle (inherited);
  // Apply(Family(parent, child)) → record has the child with Inherited=true;
  // RestoreAll → child Normal, parent Normal
  [Fact] public void RestoreAll_sweeps_an_idle_child_that_was_never_in_a_family()
  // same parent; Apply(Family(parent)) only; wait for child Idle; RestoreAll → child Normal
  [Fact] public void A_process_that_was_idle_before_efficiency_mode_is_left_alone_by_the_sweep()
  // start an unrelated child at Idle before Apply; Apply+RestoreAll of another family → it stays Idle
  [Fact] public void The_watchdog_sweep_works_from_the_record_alone()
  // Apply(Family(parent)); wait for child Idle; new EfficiencyMode(RecordPath, _log).RestoreAll("watchdog") → child Normal
  ```

  Each test also asserts the child's throttling after the restore with `ThrottledOf(child) == false`. If throttling turns out to be inherited, Step 3 clears it too. Ledger which way it went.

  Find the child with `new ToolhelpProcessTable().List()`: ParentPid equals the parent pid, and the name is `ping`.

- [ ] **Step 2: Run them and check that they fail.** `--filter-class Nightcrew.Tests.EfficiencyModeTests`. Expected: the children stay Idle.

- [ ] **Step 3: Implement.**
  - **`Apply`:** a process at IDLE that started after the family's earliest `AppliedUtcTicks` is recorded with `Inherited = true` and the root entry's `OriginalPriority`. Throttling is not changed for it. Other IDLE or BELOW_NORMAL processes are still "its own choice". New entries get `AppliedUtcTicks = DateTime.UtcNow.Ticks`.
  - **The sweep, inside `RestoreAll` and `RestoreFamily`, before entries are dropped:**
    - The start set is the recorded entries.
    - It repeats until nothing is added. Any listed process counts if all of these hold:
      - its parent pid is in the set,
      - it started after that entry's `AppliedUtcTicks`, or after its start when there are no ticks, and
      - when the parent is alive, the parent's start equals the recorded start.
    - Each match joins the set with the ancestor's original priority.
    - Then `Put` each one: priority goes back only if it is still IDLE, and throttling is cleared if it is set.
  - Never touch Nightcrew's own pid.

- [ ] **Step 4: Run `EfficiencyModeTests` 3 times, then the full suite.** Expected: PASS every time.

- [ ] **Step 5: Commit.** `fix: apps started by a slowed app come back from Efficiency mode`

---

### Task 5: Claude Code status files (spec 4.1)

**Files:**
- Create: `src/Nightcrew/Core/ClaudeSessionSignal.cs`
- Create: `src/Nightcrew/Platform/SessionFileReader.cs`
- Modify: `src/Nightcrew/Program.cs`. Add the signal after `TranscriptSignal` in `signals`.
- Test: create `tests/Nightcrew.Tests/ClaudeSessionSignalTests.cs` and `tests/Nightcrew.Tests/SessionFileReaderTests.cs`

**Interfaces:**
- Consumes: `Settings.StayAwakeForApprovals`, `Settings.ClaudeSessionsDir`, `Settings.IgnorePathContains`, and `IProcessTable.TryGetTimes`.
- Produces:

  ```csharp
  public interface ISessionFiles { /// Contents of up to 64 files named <digits>.json, each ≤ 64 KB, in the folder; empty if it is missing.
      IReadOnlyList<string> Read(string folder); }
  public sealed class ClaudeSessionSignal(ISessionFiles files, IProcessTable processes, Settings settings,
      Func<LidState> lid, ILog log) : IBusySignal, IHolderSource   // Name => "claude-status"
  ```

  - `[DataContract] internal sealed class SessionStatus` has `pid` (int), `procStart` (string), `status` (string) and `cwd` (string), using `[DataMember(Name = "...")]`.
  - `Platform.SessionFileReader : ISessionFiles` does the following:
    - expands environment variables in the folder
    - matches file names against `^\d+\.json$`, case-insensitively
    - skips files over 65,536 bytes and stops after 64 files
    - reads each file with `FileShare.ReadWrite | FileShare.Delete`
    - skips a file that throws `IOException` or `UnauthorizedAccessException`

- [ ] **Step 1: Write the failing tests.** Use a fake `ISessionFiles` and a fake `IProcessTable`. Pid 100 started at `T0`. The file's `procStart` is `T0.ToFileTimeUtc().ToString()`.

  ```csharp
  [Fact] public void Busy_session_counts()                         // status busy → Sample true; Holders == [("Claude Code","1 session")]
  [Fact] public void Idle_session_does_not_count()
  [Theory] [InlineData(true, LidState.Closed, true)] [InlineData(true, LidState.Open, false)]
           [InlineData(true, LidState.Unknown, false)] [InlineData(false, LidState.Closed, false)]
  public void Waiting_counts_only_with_the_setting_on_and_the_lid_closed(bool setting, LidState lid, bool expected)
  [Fact] public void Dead_process_does_not_count()                 // TryGetTimes false
  [Fact] public void Reused_pid_does_not_count()                   // TryGetTimes start = T0 + 1 s
  [Fact] public void Empty_or_truncated_file_is_skipped()          // "", "{\"pid\":10", "\0\0" → false, one warning for the run, no exception
  [Fact] public void Observer_sessions_are_ignored()               // cwd contains "claude-mem-observer" (default IgnorePathContains) → false
  [Fact] public void Two_busy_sessions_show_as_two_sessions()      // ("Claude Code","2 sessions")
  [Fact] public void Nothing_from_the_file_reaches_the_log_or_holders()
  // cwd "C:\\secret-project", busy → no log line contains "secret"; holder text doesn't either
  ```

  `SessionFileReaderTests` work on a temp folder:

  ```csharp
  [Fact] public void Reads_only_digit_named_json_files()          // 123.json read; 123.key, abc.json, 123.json.tmp not read
  [Fact] public void Skips_files_over_64_KB()
  [Fact] public void Reads_at_most_64_files()                      // 70 files → 64 results
  [Fact] public void Missing_folder_gives_nothing()
  ```

  Also check the real `cwd` format once, without printing it. On the owner's laptop, a claude-mem observer session's `cwd` must contain the default `IgnorePathContains` value `claude-mem-observer`. If it contains only `claude-mem`, ledger a Ruling and match what the transcript filter matches on the transcript path.

- [ ] **Step 2: Run them and check that they fail.** Expected: compile errors.

- [ ] **Step 3: Implement both classes, then wire them up in `Program`.**
  - `new ClaudeSessionSignal(new SessionFileReader(), processes, settings, () => events.Lid, log)` goes after the `TranscriptSignal` and before the `AgentTreeSignal`.
  - A parse failure is caught per file; log at most one warning per run, with no content.
  - `procStart` is parsed with `long.TryParse` and `DateTime.FromFileTimeUtc`.

- [ ] **Step 4: Run both classes, then the full suite.** Expected: PASS.

- [ ] **Step 5: Commit.** `feat: read Claude Code's session status so quiet commands and approvals keep it awake`

---

### Task 6: The Stay awake for approvals switch (spec 4.2)

**Files:**
- Modify: `src/Nightcrew/Core/SettingsModel.cs`. Add `bool StayAwakeForApprovals = true` as the last parameter of `SettingsPage`, and fill it in `From`.
- Modify: `src/Nightcrew/App/Ui/PanelVisual.cs`. Add `SwitchRow("Stay awake for approvals", page.StayAwakeForApprovals, actions?.StayAwakeForApprovals, bottom: 12)` directly after the Lock row. Add the parameter `Action<bool>? StayAwakeForApprovals = null` as the last parameter of `SettingsActions`.
- Modify: `src/Nightcrew/App/TrayApp.cs`. Wire it to `ChangeSetting("Stay awake for approvals", () => _settings.StayAwakeForApprovals = on)`.
- Test: `SettingsModelTests`, `SettingsPageTests`

- [ ] **Step 1: Write the failing tests.**
  - `SettingsPageTests.The_page_shows_every_setting_in_order`: the expected list gains `"Stay awake for approvals"` directly after `"Lock when lid closes"`.
  - In `Each_control_reports_the_new_value`, clicking the new switch reports `approvals False` from the defaults.
  - In `The_chosen_value_and_switches_that_are_on_fill_lavender`, the new switch is Lavender with the defaults.
  - `SettingsModelTests`: `From(new Settings { StayAwakeForApprovals = false }).StayAwakeForApprovals == false`.

- [ ] **Step 2: Run them and check that they fail.**

- [ ] **Step 3: Implement as listed under Files.** The automation name of the new switch follows the existing switches.

- [ ] **Step 4: Run both classes, then the full suite.** Expected: PASS.

- [ ] **Step 5: Commit.** `feat: Stay awake for approvals switch in Settings`

---

### Task 7: A frozen check or a crash puts settings back (spec 4.3)

**Files:**
- Create: `src/Nightcrew/Core/FreezeGuard.cs`
- Create: `src/Nightcrew/App/Recovery.cs`
- Modify: `src/Nightcrew/App/TrayApp.cs`. Call `guard.TickDone()` at the end of each completed tick. Add a 60-second watch timer that doesn't take `_gate`.
- Modify: `src/Nightcrew/Program.cs`. Add the exception handlers and pass the recovery to `TrayApp`.
- Test: create `tests/Nightcrew.Tests/FreezeGuardTests.cs`

**Interfaces:**
- Produces:

  ```csharp
  public sealed class FreezeGuard(IClock clock, TimeSpan limit, TimeSpan minUptimeForRestart)
  {
      public void TickDone();
      /// True when no tick has finished for longer than the limit; uses IClock.Now, which excludes sleep.
      public bool Frozen { get; }
      /// True once the process has run at least minUptimeForRestart since the guard was created.
      public bool MayRestart { get; }
  }
  // App: public static class Recovery { public static void Run(string why, bool restart, IEfficiencyMode efficiency, Engine engine, string exePath, ILog log); }
  ```

  - `Recovery.Run` does the steps in order, each one guarded so that a failure doesn't stop the next:
    1. Log `Recovering: {why}`.
    2. `efficiency.RestoreAll("recovery")`.
    3. `engine.RestoreNow()`.
    4. If `restart`, `Process.Start(exePath)`.
    5. `Environment.Exit(3)`.
  - The values used are a limit of 3 minutes and a minimum uptime of 10 minutes.

- [ ] **Step 1: Write the failing tests** with `FakeClock`:

  ```csharp
  [Fact] public void Not_frozen_while_ticks_finish()
  [Fact] public void Frozen_after_three_minutes_without_a_finished_tick()   // TickDone at 0; advance 3:01 → Frozen
  [Fact] public void Sleep_does_not_count_as_a_freeze()
  // FakeClock.Now does not advance during sleep (IClock contract) → advancing only UtcNow by 2 h keeps Frozen false
  [Fact] public void Restart_is_allowed_only_after_ten_minutes()            // at 9:59 false, at 10:00 true
  ```

- [ ] **Step 2: Run them and check that they fail.**

- [ ] **Step 3: Implement `FreezeGuard` and `Recovery`, then wire them up.**
  - **`TrayApp`:**
    - `OnTimer` calls `TickDone` after `_engine.Tick()` returns.
    - The watch timer runs every 60 s. It skips when `_closed || _sessionEnding`. If `Frozen`, it calls `Recovery.Run("no tick finished for 3 minutes", guard.MayRestart, ...)`.
  - **`Program`:**
    - Before `Application.Run`, call `Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException)`.
    - Hook `Application.ThreadException` and `AppDomain.CurrentDomain.UnhandledException`. Both call `Recovery.Run("unhandled exception: " + message, guard.MayRestart, ...)`.
    - The new copy gets the instance mutex because the old one's mutex is abandoned. `TakeInstance` already treats `AbandonedMutexException` as owned.

- [ ] **Step 4: Run the full suite. Then check by hand in a dry run** that a tick held for over 3 minutes logs `Recovering`. To do that, temporarily make a debug-only setting slow the tick. Ledger the check and remove the code. If that can't be done cleanly, ledger "not exercised by hand". Expected: suite PASS.

- [ ] **Step 5: Commit.** `fix: a frozen check or a crash puts the settings back and restarts Nightcrew`

---

### Task 8: First start, second start, --exit, quit race (spec 4.10)

**Files:**
- Modify: `src/Nightcrew/App/CommandLine.cs`. Add `ParsedArgs(... bool Exit = false)` and parse `--exit`.
- Modify: `src/Nightcrew/Program.cs`:
  - the second start signals `Local\Nightcrew.ShowPanel`
  - `--exit` signals `Local\Nightcrew.Exit`, then waits up to 10 s to take the instance mutex
  - pass `firstRun` (from `SettingsStore.Load(path, out created)`) to `TrayApp`
- Modify: `src/Nightcrew/App/TrayApp.cs`:
  - wait on both named events with `ThreadPool.RegisterWaitForSingleObject`, and post to the UI thread to open the panel or quit
  - on the first run, open the panel once at the bottom-right of the primary work area
  - add a `SafeChange` helper so every `Timer.Change` ignores `ObjectDisposedException`
  - set a closing flag before disposing the timers, in `Shutdown` and in `SessionEnded`
- Test: `CommandLineTests`

**Interfaces:**
- Produces:
  - `ParsedArgs(bool Watchdog, bool Uninstall, bool DryRun, bool Exit = false)`
  - `int Program.ExitRunning()`, which returns 0 once Nightcrew has exited or was not running, and 1 after a 10 s timeout

- [ ] **Step 1: Write the failing tests.**

  ```csharp
  [Fact] public void Exit_verb_sets_its_flag()      // Parse(["--exit"]) == new ParsedArgs(false,false,false,Exit: true)
  [Fact] public void Exit_ignores_case()            // "--EXIT"
  ```

- [ ] **Step 2: Run them and check that they fail.**

- [ ] **Step 3: Implement.**
  - The event and mutex names are exactly `Local\Nightcrew.ShowPanel`, `Local\Nightcrew.Exit` and the existing `Local\Nightcrew.Instance`.
  - The second start opens the existing `ShowPanel` event with `EventWaitHandle.TryOpenExisting`, sets it and returns 0. If the event doesn't exist, it does nothing.
  - The `Exit` handler calls the existing `Quit()` on the UI thread.
  - Calls to `Change` on timers made after quitting become no-ops.

- [ ] **Step 4: Run the full suite. Then check by hand on the built exe, in a dry run** (`--dry-run` copy only; no real power writes):
  1. A second start opens the panel.
  2. `nightcrew --exit` closes it and returns within 10 s.
  3. Renaming settings.json away opens the panel on the next start.

  Ledger the results. Expected: suite PASS.

- [ ] **Step 5: Commit.** `feat: second start opens the panel, --exit quits, first start shows the panel`

---

### Task 9: Version and GPU holder names (spec 4.11)

**Files:**
- Modify: `Directory.Build.props`. Add `<Version>0.3.0</Version>`.
- Create: `src/Nightcrew/Core/AppVersion.cs`, containing `public static class AppVersion { public static string Text { get; } }`. It returns `Major.Minor.Build` of the Nightcrew assembly.
- Modify: `src/Nightcrew/Program.cs`:
  - the startup line becomes `Nightcrew {AppVersion.Text} started` or `... started (dry run)`
  - `ProcessName` is replaced by a lookup in the tick's `processes.List()`
- Create: `src/Nightcrew/Core/ProcessNames.cs`, containing `public static Func<int, string?> ProcessNames.From(IProcessTable table)`
- Modify: `.github/workflows/release.yml`. Add a step before the build that fails when the tag without `v` differs from `<Version>` in Directory.Build.props.
- Test: create `tests/Nightcrew.Tests/AppVersionTests.cs`, and add to `GpuSignalTests`

- [ ] **Step 1: Write the failing tests.**

  ```csharp
  [Fact] public void Version_is_0_3_0() => Assert.Equal("0.3.0", AppVersion.Text);
  [Fact] public void Holder_names_come_from_the_tick_list()
  // fake IProcessTable listing (42, 1, "ollama"); ProcessNames.From(table)(42) == "ollama"; (43) == null
  ```

- [ ] **Step 2: Run them and check that they fail.**

- [ ] **Step 3: Implement.** The release check is in pwsh:

  ```pwsh
  $want = ([xml](Get-Content Directory.Build.props)).Project.PropertyGroup.Version
  if ("$env:GITHUB_REF_NAME".TrimStart('v') -ne $want) { throw "Tag $env:GITHUB_REF_NAME does not match Version $want" }
  ```

- [ ] **Step 4: Run the full suite.** Check that the built exe's FileVersion starts with `0.3.0`, using `(Get-Item src/Nightcrew/bin/Release/net48/nightcrew.exe).VersionInfo`. Expected: PASS.

- [ ] **Step 5: Commit.** `chore: v0.3.0 version stamp and GPU holder names from the tick list`

---

### Task 10: README and hardware checklist (spec 6, 7)

**Files:**
- Modify: `README.md`
- Modify: `docs/hardware-checklist.md`
- Modify: `CONTRIBUTING.md`, only if a rule changed. Expected: no change.

- [ ] **Step 1: Update the README.**
  - **How it works, step 1:** add one sentence. Nightcrew reads only the status (busy, idle, waiting), process ID and start time from Claude Code's `~\.claude\sessions\<pid>.json`. It never opens anything else in `~\.claude`, and it has no network access.
  - **The panel and settings list:** add **Stay awake for approvals**. With the lid closed, Claude Code waiting for your approval counts as work. This is Claude Code only.
  - **The advanced table:** add `StayAwakeForApprovals` (true) and `ClaudeSessionsDir`.
  - **A new short Update section:** run `nightcrew --exit`, replace the exe, start it.
  - **Install step 2:** say that the panel opens on the first start.

  Everything follows the house style.

- [ ] **Step 2: Add the hardware-checklist steps from spec section 6** as steps 12 to 17, in the checklist's own format (**Do**, **Look at**, **Pass**):
  - plan switch while armed
  - docked with Do nothing, no lock
  - quiet command with the lid closed
  - approval prompt, in three cases: lid closed with the setting on, lid open, and setting off
  - `--exit` and a second start
  - a note in step 4 that it must be re-run for v0.3

- [ ] **Step 3: Build and run the full suite** (a docs-only change; this checks nothing broke). Expected: PASS.

- [ ] **Step 4: Commit.** `docs: v0.3.0 README and hardware checklist steps`
