# Nightcrew UI design

Date: 13 October 2026. Status: approved by the owner on 14 October 2026;
section 10 records what changed while building it. This builds on
[the v1 design](2026-10-13-nightcrew-design.md) and the code on `feat/v1`. It
changes how Nightcrew looks and reads, not what it does.

## 1. Goal

Give Nightcrew the design language of Insomnia and Wispr Flow:
- **Insomnia:** a single line mark that changes with state, a dark night
  palette, and short, plain, honest wording.
- **Wispr Flow:** a warm cream ink, a serif headline with an italic turn, and
  lavender accents.

Three surfaces change:
- **the tray mark**
- **a new click panel**
- **the README and GitHub page**

## 2. Owner decisions (13 October 2026)

- **Direction:** "Moonlit". A dark night panel in the Insomnia style, warmed
  with Wispr's serif headline and lavender accent. **Dark only**, with no light
  mode in the app or the README.
- **Marks:** two are built, **lantern** and **moon-and-star**. A `Mark`
  setting switches between them (default `lantern`) until the owner picks one.
- **Panel:** left-click opens it. It has three states (all quiet, working,
  paused) plus a warning row. There is no finishing state: it happens with the
  lid closed and nobody watching.
- **Agents:** Claude Code and Codex are detected out of the box. Other agents
  are covered by the GPU signal and Hold awake, and power users can add them
  through the settings lists (`AgentProcessNames`, `TranscriptRoots`). Built-in
  presets for other agents are a v2 item.
- **Tech:** WPF, which is part of .NET Framework 4.8. The single-file property
  is kept because WPF needs nothing extra, though it isn't a hard requirement.
- **Images:** GPT Image may be used for the app icon art and one optional
  atmosphere image, never for images that contain text.

## 3. Tokens

| Token | Value | Use |
|---|---|---|
| `Night` | `#1E1F21` | panel background, icon tile |
| `NightRaised` | `#26282B` | the "keeping it awake" box, hover |
| `Moon` | `#FFFFEB` | primary text and mark strokes (Wispr's cream, used as ink) |
| `MoonMuted` | `#C9C7B6` | secondary text |
| `MoonFaint` | `#8C8A7C` | the Settings link, captions |
| `Hairline` | `#FFFFEB` at 10% | dividers |
| `Lavender` | `#F0D7FF` | the lit flame or star, Hold awake when on, badges |
| `Ink` | `#1A1A1A` | text on lavender |
| `Warn` | `#F5C27A` | warning row text |
| `LavenderDeep` | `#8E5CC4` | the flame or star on a light taskbar |

**Type:**
- **Headline:** EB Garamond, regular and italic, 26 px. It is used for the
  single headline only.
- **Everything else:** Figtree, regular and medium, 11–13 px.
- **Embedding:** both fonts are embedded in the exe as WPF resources. Both are
  under the SIL Open Font License, which allows embedding; the licence text
  ships in `THIRD-PARTY-NOTICES.md`.
- **Fallback:** if a font fails to load, Georgia replaces EB Garamond and
  Segoe UI replaces Figtree.

**Shape:** the panel has 14 px corners, controls and rows 8 px. Spacing is on
a 4 px grid. There are no shadows inside the panel; the window itself gets the
standard Windows 11 shadow.

## 4. Marks

Both marks are drawn on a 24-unit grid, like Insomnia's eye, and kept as path
data in one place (`MarkGeometry`).

| State | Lantern | Moon and star |
|---|---|---|
| Idle | outline only, unlit | crescent outline |
| Live, lid open | lavender flame inside | small lavender star beside the moon |
| Live, lid closed | body filled lavender | moon filled lavender, star in cream |
| Paused | same as idle | same as idle |

**Stroke width:** 2 units at 16–20 px, so the mark stays legible in the tray,
and 1.5 units at 24 px and above.

**Tray icons:**
- **Rendering:** at runtime, for the size Windows asks for
  (`SystemInformation.SmallIconSize`, so it follows the display scaling).
- **Caching:** one icon per mark, per state, per size. These replace the
  coloured dots in `TrayIcons`.
- **Colours:** a cream stroke with lavender fills, which reads on both dark and
  light taskbars.

**App icon:**
- **What it is:** the lantern on a `Night` tile with 22% corners.
- **How it's made:** generated as a multi-size `.ico` (16 to 256 px) from the
  same geometry by a small build-time tool, and set as `ApplicationIcon`.
- **GPT Image:** may be used to explore a richer 256 px tile. The owner picks.
  The shipped `.ico` always keeps the vector mark at 16–48 px.

## 5. Click panel

### 5.1 Behaviour

- **Opening and closing:** a left-click on the tray icon toggles the panel. It
  closes when you click elsewhere, press Esc, or click the icon again.
- **Size and position:** 320 px wide, with the height set by its content. It
  sits beside the tray, against the taskbar edge, whichever side the taskbar
  is on.
- **Animation:** it fades and slides 8 px over 120 ms.
- **Right-click** still opens the existing settings menu. The panel's
  "Settings" link opens that same menu.
- **Updates:** the panel refreshes after every engine tick while it's open.

### 5.2 Content

| Part | All quiet | Working | Paused |
|---|---|---|---|
| Header | mark, "Nightcrew", Settings | same | same |
| Headline (serif) | All quiet. | Working *through the night.* | Paused. |
| Sub line | No agent is working. Close the lid and your laptop does what Windows says. | Close the lid. Your laptop keeps working, then does what your lid normally does. | Windows handles the lid until you resume. |
| Keeping it awake | hidden | one row per holder (see 5.3) | hidden |
| Facts | Battery · When the lid closes | Battery and floor · When done | Battery |
| Buttons | Hold awake · Pause | Hold awake · Pause | Hold awake · Resume |
| Warning row | when there is one (see 5.4) | same | same |

**Fact rows:**
- **Battery:** `81%` on AC. On battery: `64% · stops at 20%`, or `64%` alone
  when the floor is off.
- **When the lid closes** (all quiet): the owner's Windows lid action for the
  current power source, as `Sleep`, `Hibernate`, `Do nothing` or `Shut down`.
- **When done** (working): what happens after the work ends: `Sleeps`,
  `Hibernates`, `Stays on` (Do nothing), or `Hibernates` (Shut down).

**Hold awake** fills lavender while it's on.

### 5.3 Holders

Each signal reports who is holding the laptop awake, as label and detail
pairs:

| Source | Label | Detail |
|---|---|---|
| Claude transcripts | `Claude Code` | `2 sessions` (files under the Claude root that grew within the grace period) |
| Codex transcripts or codex CPU | `Codex` | `working` |
| A tool process under an agent | the process name, e.g. `python` | `busy` |
| GPU | the process name | `GPU 41%` |
| Hold awake | `Hold awake` | `on` |

`ActivityMonitor` keeps the holders seen within the grace period, so the rows
match "Live". Holders from user-added agents use the process name, or the
transcript root's folder name.

### 5.4 Warnings

One row, in `Warn` colour, shows the most important current problem:

| Condition | Text |
|---|---|
| The gate fails (policy or access) | Windows policy blocks lid changes here. Keep the lid open. |
| A failed arm is backing off | Couldn't change power settings. Trying again in a few minutes. |
| A restore is pending | Your power settings aren't back yet. Retrying. |
| The watchdog failed to register | The safety net isn't installed. Settings may stay changed if Nightcrew crashes. |

### 5.5 Wording rules

These apply everywhere, including the tooltip, menu and notifications:
- sentence case
- short, plain sentences
- contractions
- no exclamation marks, no "please", no "successfully"
- say what happens, not how

**Tooltip,** at most 63 characters:
- `Nightcrew · all quiet`
- `Nightcrew · working (Claude Code)`
- `Nightcrew · paused`

## 6. Architecture

| Unit | Where | Does |
|---|---|---|
| `MarkGeometry` | `App/Ui/MarkGeometry.cs` | Path data for both marks; builds a WPF `Drawing` for (mark, state, size); renders tray bitmaps |
| `Theme` | `App/Ui/Theme.cs` | Colour and brush constants; loads the embedded fonts, with fallback |
| `PanelModel` | `Core/PanelModel.cs` (pure) | Maps an engine snapshot to the strings and rows in section 5. Unit-tested |
| `PanelWindow` | `App/Ui/PanelWindow.xaml` + code-behind | The borderless WPF window; binds to `PanelModel`; positions itself; fade animation |
| `Holder` reporting | `Core/*Signal.cs`, `ActivityMonitor` | Signals expose `IReadOnlyList<Holder> Holders` from their last busy sample, and the monitor aggregates them within the grace period |
| `EngineSnapshot` | `Core/Engine.cs` | A record carrying mode, lid, holders, battery, floor, owner lid action, hold, paused and warning; built at the end of each tick |
| Tray wiring | `App/TrayApp.cs` | Left-click toggles the panel. After each tick, posts the snapshot to the UI thread, refreshes the open panel, and swaps the tray icon |

**Threading:**
- **UI thread:** the panel lives on the existing STA UI thread. WinForms and WPF
  share it, and `ElementHost.EnableModelessKeyboardInterop` gives the window
  keyboard input.
- **Engine:** ticks still run on the timer thread. Only the immutable snapshot
  crosses over to the UI thread.

**Build:**
- **Project:** `UseWPF` is added next to `UseWindowsForms`. The fonts are
  `<Resource>` items.
- **Single-file gate:** still passes, because WPF assemblies come with .NET
  Framework.
- **Exe size:** grows by about 0.6 MB of fonts.

## 7. README and GitHub

Everything is in the dark style. The assets live in `docs/assets/`:

| Asset | Made with | Shows |
|---|---|---|
| `banner.png` | HTML with the real fonts, rendered by headless Chrome at 2× | lantern tile, "Nightcrew", the serif tagline |
| `night.png` | same | the four-step night timeline: an agent starts, you close the lid, the work ends, it sleeps |
| `changes.png` | same | what Nightcrew changes and what it puts back |
| `panel.png` | the real WPF panel, rendered off-screen | the click panel while working |
| `social.png` | from the banner, 1280×640 | GitHub's social preview |
| `atmosphere.png` (optional) | GPT Image, no text | a closed laptop on a desk at night with the lantern glowing |

**`panel.png`:** comes from a hidden dev verb, `nightcrew --render-panel <dir>`.
It renders the three panel states from fixed sample snapshots to PNG, so the
README always shows the real panel.

**Tagline:** "Awake while your agents work. *Asleep when they're done.*"

**README sections,** in Insomnia's plain voice:
1. heat warning
2. install
3. using it
4. what happens when the lid closes
5. how it puts things back
6. limits
7. other agents
8. uninstall
9. credits

**Badges** use the token colours: Windows 10 and 11, MIT, status experimental.

**GPT Image:** model `gpt-image-1`, with the key from the user environment
variable `OPENAI_API_KEY`. Generated images are reviewed by the owner before
they are committed.

## 8. Testing

**Unit tests:**
- **`PanelModel`:** every row and string in section 5 for each state, the
  holder aggregation, the warning priority and the tooltip length.
- **`MarkGeometry`:** both marks parse, the three states differ, and the tray
  bitmaps come out at the requested size. Rendering runs off-screen on an STA
  test thread.
- **`--render-panel`:** writes three PNGs of the expected size.

**Owner check:** run `nightcrew --dry-run` and look at the panel in each
state, then switch `Mark` between `lantern` and `moon`.

## 9. Out of scope

- light mode
- built-in presets for other agents
- animations beyond the panel fade
- a finishing panel state
- a settings window (settings stay in the right-click menu and `settings.json`)

## 10. Changes while building (14 October 2026)

- **Code-only WPF.** The panel is built in C# (`App/Ui/PanelVisual.cs`) and
  hosted by `App/Ui/PanelWindow.cs`, with no XAML. The same tree is shown in
  the window and rendered for the README.
- **No hidden exe verb.** `build/render-assets.ps1` loads the built exe as a
  library and calls `AssetRenderer`, so rendering never starts the tray app.
  It writes `panel-quiet.png`, `panel-working.png` and `panel-paused.png`;
  the README shows `panel-working.png`. The same script writes the app icon
  and screenshots the README pages with headless Chrome.
- **App icon.** `src/Nightcrew/nightcrew.ico` is committed and set as
  `ApplicationIcon`. A test checks its sizes against `MarkGeometry`.
- **Light taskbars.** The tray mark uses `Night` ink and `LavenderDeep` when
  Windows' taskbar is light, read from the registry on each update.
- **Menu.** The `Mark` setting also has a **Tray mark** submenu.
- **Exe size.** About 1.5 MB, because EB Garamond's two files are about
  0.6 MB each.
- **Header mark.** 20 px with the 1.5-unit stroke, because the panel is never
  shown smaller than that.
