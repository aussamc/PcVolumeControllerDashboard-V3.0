# Post-parity feature backlog

The parity backlog (`PARITY_FIX_BACKLOG.md`) is fully cleared and WPF is retired — the
Avalonia host is the single UI. This file tracks **new feature work** beyond parity.
Same conventions as the parity tracker: PRs only, small and build-green, per standing
rule #1 (`feat/v3.x-…`); a user-facing batch bumps `3.x`.

Effort: **S** ≈ <½ day, **M** ≈ ½–2 days, **L** ≈ multi-day.

---

## v3.15 — Volume overlay enhancements  *(planned)*

Four requested improvements to the on-screen volume overlay
(`App.Avalonia/VolumeOverlay.cs` + `Services/VolumeOverlayController.cs`, settings in
`Core/Settings.cs:115-118`). Shipping as **one PR** (`feat/v3.15-overlay-enhancements`)
with a single version bump **3.14.2 → 3.15**.

| # | Item | Status | Effort | Key files |
|---|---|---|---|---|
| O1 | **Transparency slider** — user-set overlay opacity | planned | S | `Core/Settings.cs` (`OverlayOpacity`); `VolumeOverlay.cs` (base opacity + fade); `VolumeOverlayController.cs`; `MainWindow.axaml`/`.axaml.cs` (slider) |
| O2 | **Size / scale slider** — scale the whole overlay proportionally | planned | S–M | `Core/Settings.cs` (`OverlayScale`); `VolumeOverlay.cs` (Viewbox + scaled dims + positioning); `VolumeOverlayController.cs`; `MainWindow.axaml`/`.axaml.cs` (slider) |
| O3 | **Screen position** — Top/Bottom × Left/Center/Right (6) | ✅ already shipped | — | `VolumeOverlay.cs:199-218` (`PositionOnScreen`); combo in `MainWindow.axaml:419-426` |
| O4 | **Show on all screens** — mirror the overlay on every monitor | planned | M | `Core/Settings.cs` (`OverlayAllScreens`); `VolumeOverlayController.cs` (multi-window); `VolumeOverlay.cs` (per-`Screen` positioning); `MainWindow.axaml`/`.axaml.cs` (checkbox) |

### O1 — Transparency slider

- **Setting:** `double OverlayOpacity` in `Core/Settings.cs`, default `1.0`, clamp
  `0.30–1.00` (a floor keeps the popup from becoming invisible/unclickable-through in a
  confusing way).
- **Overlay:** the window already animates `Opacity` for the fade-out (`OnFadeTick`,
  `ShowVolume` sets `Opacity = 1`). Introduce a `_baseOpacity` field: `ShowVolume`
  restores `Opacity = _baseOpacity` (not `1`) on each show, the fade steps down toward
  `0`, and post-hide resets to `_baseOpacity`. Pass the value into `ShowVolume`.
- **UI:** a % slider (30–100) in the Volume Overlay card with a live value label,
  following the existing timeout-slider pattern (subscribe in the observer block,
  `_initializing` guard, `Save()`).

### O2 — Size / scale slider

- **Setting:** `double OverlayScale` in `Core/Settings.cs`, default `1.0`, clamp
  `0.75–1.50`.
- **Overlay:** `OverlayWidth`/`OverlayHeight` become scale-derived instance values
  (base `320×84 × scale`). Wrap the panel `Border` in a `Viewbox` so text, bar, and
  mute glyph scale together; set the window `Width`/`Height` to the scaled base.
  `PositionOnScreen` must use the scaled dimensions (it already multiplies by the
  screen's DPI `Scaling` — combine the two).
- **UI:** a % slider (75–150) with a live value label.

### O3 — Screen position  *(already implemented — no code change)*

The 6 corners the user listed (Top/Bottom × Left/Center/Right) are exactly what
`PositionOnScreen` and the `OverlayPositionComboBox` already support, audited at parity
2026-07-05. **Only cleanup:** the card's helper text in `MainWindow.axaml:413-414`
still says the overlay is "ported in a later PR; the preference is saved now" — stale
since the overlay shipped. Fix that copy as part of this PR.

### O4 — Show on all screens

- **Setting:** `bool OverlayAllScreens` in `Core/Settings.cs`, default `false`.
- **Controller:** `VolumeOverlayController` currently holds a single lazy `_overlay`.
  Generalize to a small pool: when `OverlayAllScreens` is on, ensure one
  `VolumeOverlay` per `Screens.All`, position each on its own screen, and drive all of
  them from `OnVolumeChanged`; when off, keep the single-overlay/primary behavior.
  Rebuild the pool when the monitor set changes (compare current `Screens.All` count).
- **Overlay:** add a `PositionOnScreen(Screen screen, string position, double scale)`
  path so each instance lands on its assigned screen instead of always `Screens.Primary`.
- **UI:** a "Show on all screens" checkbox in the card.
- **Note:** Avalonia exposes `Screens` per top-level; the controller can enumerate via
  an existing overlay's `Screens`. Each mirrored window keeps `ShowActivated = false`
  (never steals focus) and its own hide/fade timer.

### Verification bar (per CLAUDE.md)

- Both Avalonia TFMs (`net10.0`, `net10.0-windows10.0.17763.0`) build **0 warnings /
  0 errors**.
- Full test suite passes. Add unit coverage where logic is extractable (position/scale
  math is the testable seam; the window plumbing is UI-bound).
- Windows build launches; smoke-test each new control end-to-end.

### Version-bump chores (3.14.2 → 3.15, per standing rule #2)

- `DashboardVersion` in the Avalonia host's `MainWindow`.
- `<Version>`/`<AssemblyVersion>`/`<FileVersion>` in `App.Avalonia/App.Avalonia.csproj`.
- README compatibility table.
- Add `RELEASE_NOTES_v3.15.md`; remove the superseded `RELEASE_NOTES_v3.14.2.md`.
- Update `## Key constants` in CLAUDE.md (Dashboard version line).

---

## v3.16–v3.20 — UX & lifecycle batch  *(planned, 2026-07-14)*

An 18-item batch of defaults, UX polish, wizard rework, and two new lifecycle
subsystems (auto-update, crash reporting) requested 2026-07-14. **Nothing here is
built yet** — this is the plan of record. Grouped into five sequenced version bumps so
each ships as a small, build-green batch (standing rule #1/#2). Ordering is a proposal;
items can be re-sliced. The dependency worth respecting: the wizard's update page (v3.18)
is thin until the updater backend (v3.19) exists — either ship v3.19 first or land the
wizard page as "check only" and enrich it when v3.19 lands.

**Decisions captured from the 2026-07-14 clarification pass:**

- **Channel defaults (items 1–3):** a fresh install has **only ch1 = Master** actually
  bound. ch2–ch6 start **Unassigned** (empty `TargetKey`). The old app names become
  **suggested friendly-name placeholders only** (not bindings), and ch4's suggestion is
  **"Voice Chat"**, not "Discord". No channel starts in pool/multi-app mode — a channel
  is a single target unless the user explicitly makes it a pool.
- **Auto-updater (items 4/14):** scope = **download + one-click apply** (download the
  installer/package in-app and launch it / stage self-replace on restart), with toggles
  to disable the checker and the auto-apply independently. Not full silent self-update.
- **Share Diagnostics / crash logger (item 18):** design it now with a **pluggable
  upload endpoint (TBD)**, **opt-in / default OFF**, explicit consent, path/username
  redaction. Server choice is an **open dependency** — flagged below.
- **Wizard streams (item 16):** a startup chooser with **"Quick setup" preselected**
  and "Advanced setup" opt-in. Quick = fewer pages + opinionated defaults; Advanced =
  the full page set (audio backend, encoder feel, update prefs, etc.).

### Item → batch map

| # | Item | Batch | Effort |
|---|---|---|---|
| 1 | Targets start as single targets, never pool mode | v3.16 | S |
| 2 | ch4 default label "Voice Chat" (not Discord) | v3.16 | S |
| 3a | Nothing assigned except Master on first start (unassigned + suggested labels) | v3.16 | S |
| 8 | OLED sliders sit under their heading, moved right | v3.16 | S |
| 9 | Default OLED display mode → **Large Volume Number** | v3.16 | S |
| 9b | LargeVolume layout: bigger name + number, drop mute line, "MUTE" when muted (⚠ firmware reflash v2.25→v2.26) | v3.16 (own firmware PR) | M |
| — | Tray/login default flip: `MinimizeToTray`/`StartMinimizedToTray`/`StartWithWindows` → default on | v3.16 | S |
| — | Encoder acceleration default-on (Medium preset) | v3.16 | S |
| 10 | Anti-burn-in pixel shift not reflected in controller preview | v3.16 | S–M |
| 11 | Anti-burn-in shift clips text off the bottom of the screen | v3.16 | M |
| 15 | "Encoder sensitivity" slider left-aligned | v3.16 | S |
| 6 | Move "Advanced Debug Logging" toggle to the Debug tab | v3.17 | S |
| 3b | Remove stale "Coming in later PRs" cards from the Setup/Channels pages | v3.17 | S |
| 7 | Declutter the large Setup tab (UX investigation) | v3.17 | M |
| 5 | Wizard gains the Application-Setup checkboxes (+ anti-burn default-on w/ disclaimer) | v3.18 | M |
| 12 | Wizard theme page (Style Settings) | v3.18 | S–M |
| 13 | Wizard Encoder-Feel page with live "try it" dummy overlay | v3.18 | M |
| 14 | Wizard auto-update-checker/updater page | v3.18 | S |
| 16 | Two-stream wizard (Quick vs Advanced) chosen at startup | v3.18 | L |
| 17 | Audio backend selection in the Advanced wizard stream | v3.18 | S |
| 4 | Auto-update checker + one-click updater (+ disable toggles) | v3.19 | L |
| 18 | Share Diagnostics — crash logger with server upload | v3.20 | L |

---

### v3.16 — First-run defaults & OLED-tab polish

Small, cohesive defaults + layout fixes. Bump **3.15 → 3.16**.

**Item 1/2/3a — channel defaults.** `Core/Settings.cs:146` `CreateDefaultChannels()`:
keep `ch1 = MASTER / "Master"`; set ch2–ch6 `TargetKey = ""` (unassigned). Provide the
old names only as *suggested placeholders* surfaced by the wizard/assign UI — ch2
"Browser", ch3 "Music", **ch4 "Voice Chat"**, ch5 "Game", ch6 "Microphone" — without
writing them as bindings. Confirm no code path auto-promotes an unassigned channel into a multi-app
pool (audit `App.Avalonia/Audio/ChannelTargets.cs` + `MainWindow.ChannelDetail.cs` pool
entry points). Update `SettingsRepositoryTests`/`SettingsImportExportTests` expectations
for the new default array. Verify migration: existing users' saved channels are
untouched — this only changes the *fresh-install* default.

**Item 9 — default OLED mode.** `Core/Settings.cs:82` and `SettingsRepository.cs:331-332`
default `OledDisplayMode` from `DisplayModes.AppNameAndVolume` → `DisplayModes.LargeVolume`
(`"LargeVolume"`, already a valid mode). Fresh-install default only; don't rewrite
existing settings. Update any test asserting the old default.

**Item 9b — LargeVolume layout: bigger name + number, drop the mute line (added
2026-07-14).** The user wants the "Large Volume Number" mode to feel genuinely *large*:
enlarge the top **channel-name** text, enlarge the **%** number, and remove the
"Muted/Unmuted" strip at the bottom that currently eats the space. Current layout (128×64)
is identical on both sides — label size 1 @ y0, rule @ y12, number size 3 @ y24, mute text
size 1 @ y56:

- **Core preview** — `Core/OledRenderer.cs:335` `RenderLargeVolume` (custom 5×7 font,
  `DrawString(..., size)` integer multiplier).
- **ESP32 firmware** — `Computer_Volume_Controller_v2.25.ino:337-347`, the
  `LARGE_VOLUME` branch (Adafruit GFX 6×8 font, `setTextSize()`).

Proposed new layout (both must match, or preview diverges from the device): label
**size 2** centred at y0 (≈16px tall), rule at y18, number **size 4** centred around
y26 (≈32px tall, fills to ~y58), **no bottom mute line**. Verify the widest case (e.g.
"100%" at GFX size 4 = 24px/char × 4 = 96px ≤ 128, fits; a 7-char channel name at size 2
= 12px × 7 = 84px, fits — longer names already truncate/scroll via existing label
handling, confirm).

- **Mute indication (decided 2026-07-14 — option b).** When muted, **replace the %
  number with the word "MUTE"** instead of drawing a separate line. Keep the channel-name
  header as-is above it. Size it to fill like the number does — GFX size 4 "MUTE" =
  24px × 4 chars = 96px ≤ 128 (fits centred); the Core preview mirrors it at its
  equivalent size. When unmuted, the number returns. No bottom mute strip in either
  state. (Rejected: corner speaker-slash glyph; full-panel invert.)
- **⚠ Firmware reflash + bump (standing rule #6).** Because the on-device `LARGE_VOLUME`
  branch changes, this needs a firmware version bump (v2.25 → v2.26), folder rename
  (`Computer_Volume_Controller_v2.26/`), and a reflash — plus the CLAUDE.md firmware-
  version line + README. **Sequencing note:** this makes 9b heavier than the rest of
  v3.16 (which is app-only). Options: keep it in v3.16 but as its own firmware-touching
  PR, or split the firmware change into a dedicated `fix/…-oled-largevolume` PR that lands
  alongside. The **Core-preview** half can ship app-only; the two just must not diverge on
  `main` — land them together or gate the preview change behind the firmware release.
- **Anti-burn interaction:** the taller size-4 number leaves less vertical slack, so
  coordinate with items 10/11 — the anti-burn y-shift safe margin must account for the
  number now reaching ~y58.

**Item 8 — OLED slider layout.** `MainWindow.axaml` OLED Display Settings card: today the
value label sits above and the slider spans below/left. Restack so each slider sits
directly **under its heading** and is right-aligned/indented consistently (match the
overlay-card slider pattern). Pure XAML layout; no logic change.

**Item 15 — encoder sensitivity slider left-aligned.** `MainWindow.axaml` Encoder Feel
card — left-align the "Encoder sensitivity" slider (remove the centering/right margin so
it starts at the card's left gutter like the others). Pure XAML.

**Item 10 — anti-burn-in not reflected in preview.** Root cause found: `Core/OledRenderer.cs`
has **no pixel-shift logic at all**, and `RenderOledPreviews()`
(`MainWindow.axaml.cs:1089`) never applies a shift — so toggling
`OledAntiBurnInEnabled` (`Settings.cs:87`, already default `true`) changes nothing in the
"Whole Controller Preview". Plan: add an anti-burn shift to the renderer (a small
periodic x/y offset, e.g. ±1–2px on a slow cycle) gated on the setting, and have the
preview apply the same offset the firmware uses so preview == device. **First
investigate where the shift is actually applied on the real device** — check the ESP32
firmware (`Computer_Volume_Controller_v2.25.ino`) and `DeviceStateService`; the PC may
only send content while the ESP32 does the shifting. The preview must mirror whichever
side owns it.

**Item 11 — anti-burn shift clips bottom text.** Almost certainly the same investigation:
the shift (wherever it lives) offsets the drawing origin without shrinking the usable
draw area, so a downward shift pushes the last text row past 63px. Fix = reserve a safe
margin (draw into a `width × (height − shiftRange)` inner region, or clamp/wrap so a
+y shift can't overflow). Confirm against the SSD1315 64px height. Depends on the item-10
finding (same shift code path).

**Tray/login default flip (added 2026-07-14).** Change the fresh-install defaults of
three Application-Setup toggles in `Core/Settings.cs` from `false` → **`true`**:
`MinimizeToTray` (48→ line 51), `StartMinimizedToTray` (52), `StartWithWindows` (53).
Rationale: this is a background tray utility — new users almost always want it tucked
into the tray and launching with the OS. Implications for the implementer:
- `StartWithWindows = true` must actually **register the HKCU Run entry on first run**
  (Windows-only; the existing run-on-login glue) — a default of `true` that doesn't
  write the key would be a lie. On Linux/macOS this default is inert (no equivalent
  wired), same as today.
- Fresh-install only — `NormalizeSettings` must **not** retroactively flip these for
  existing users' saved `settings.json`.
- The wizard's Application-Setup toggles (Quick condensed page + Advanced full page)
  therefore render **pre-checked**.
- Update `CreateDefault`/settings tests asserting the old `false` values.

**Encoder acceleration default-on (added 2026-07-14).** Change `AccelerationEnabled`
in `Core/Settings.cs:67` from `false` → **`true`**, keeping `AccelerationPreset =
Medium` (line 68, already the default). Rationale: Medium acceleration gives a better
out-of-box encoder feel (fast spins cover range quickly, slow turns stay precise).
Fresh-install only — don't retroactively flip existing users. `VolumeSmoothingEnabled`
stays **off**. Update `CreateDefault`/settings tests asserting the old `false`. Note the
`ChannelRuntime`/`EncoderMath` path already honours the flag, so this is a pure default
change, not new behaviour.

**Verification:** both TFMs 0/0; tests updated + green; Windows launch smoke-tests the
new OLED default, slider layout, the anti-burn toggle visibly shifting the preview
without clipping, the tray/login toggles defaulting on (incl. the Run key being
written), and acceleration feeling active on a fresh profile. Bump chores per standing
rule #2.

---

### v3.17 — Setup-tab restructure & Debug-tab move  ✅ shipped

Bump **3.16 → 3.17** (done). Branch `feat/v3.17-setup-restructure`.

**Shipped:** items 6, 3b, and 7 all landed. The Setup tab's cards are now collapsible
`Expander` sections (Application Setup expanded by default, the rest collapsed);
"Advanced debug logging" moved to the Debug tab's Logs row (same `AdvancedDebugLogging`
setting + `AppSetupCheckBox_Changed` handler, so no logic change); both stale "Coming in
later PRs" cards removed, plus the stale "(serial sync ports in a later PR)" copy on the
OLED tab. Verified: both TFMs 0/0, 233 tests green, Windows build launches.

**Item 6 — move Advanced Debug Logging to Debug tab.** Relocate the "Advanced debug
logging" checkbox out of Application Setup into the Debug tab (it's a diagnostics
control, not a setup step). Setting itself (`AdvancedDebugLogging` in `Settings.cs`)
stays; only its host control + wiring move. Ensure the wizard's Application-Setup page
(item 5) deliberately **omits** this one.

**Item 3b — remove stale "Coming in later PRs" cards.** Delete the two stale cards at
`MainWindow.axaml:210-216` ("link groups, multi-app pools, named profiles, output-device
cycling, connect/disconnect… follow-up PRs") and `:485-491` ("global hotkey capture,
software updates, first-run wizard…"). Both describe subsystems that **already shipped**;
they're the "future updates" sections the user flagged. Move any genuinely-still-deferred
note (named profiles, output-device cycling are descoped) into this backlog only — not
the user-facing UI.

**Item 7 — declutter the Setup tab (UX investigation).** The Setup tab is large and
dense. Investigate + propose (no functionality removed). Candidate approaches to weigh:

- **Collapsible/expander sections** — group Application Setup, OLED, Overlay, Encoder
  Feel, Update, etc. into `Expander`s, collapsed by default except the most-used.
- **Sub-navigation** — split Setup into left-rail sub-pages (Application / Display /
  Overlay / Encoder / Updates) instead of one long scroll.
- **Advanced disclosure** — push rarely-touched controls behind a per-section "Advanced"
  toggle (mirrors the wizard's Quick/Advanced split, item 16, for consistency).
- **Move diagnostics-y controls to Debug** (dovetails with item 6).

Deliverable for this item is a short options write-up + a recommendation, then implement
the chosen one. Recommend the collapsible-sections approach as lowest-risk first cut.

**Verification:** both TFMs 0/0; tests green; Windows launch confirms every control
still reachable + functional after the reorg. Bump chores.

---

### v3.18 — First-run wizard overhaul

The biggest batch. Current wizard is 5 steps (Welcome → Connect → Identify → Assign →
Done, `FirstRunWizard.axaml.cs:26-41`). Bump **3.17 → 3.18**. Ships as its own
`feat/v3.18-wizard-overhaul` branch; may internally be >1 PR given size, but one
user-facing bump.

> **Status (in progress on `feat/v3.18-wizard-overhaul`):** the stream backbone (item 16)
> plus items **5, 12, 14, 17** shipped — the wizard now has a Quick/Advanced chooser and a
> dynamic per-stream step sequence, with self-contained `IWizardPage` UserControls for
> Application-Setup (condensed + full), Theme, Audio-backend, and Update-prefs. New Core
> settings `AutoCheckForUpdates`/`AutoApplyUpdates` landed here per the settings-ownership
> split. **Deferred to a follow-up:** item **13** (Encoder-Feel page with the live "try it"
> dummy overlay) — the Advanced sequence has a placeholder gap where it will slot in
> (after Theme, before Update-prefs). Both TFMs 0/0; 234 tests green.

**Item 16 — two streams (Quick vs Advanced).** Add a **stream-chooser page** after
Welcome: two big choices, **"Quick setup" preselected**, "Advanced setup" opt-in.
Implement as a `_stream` enum gating which panels are inserted into the step sequence
(the wizard already indexes panels by `_step`; generalize `StepTitles`/`_panels` to be
stream-dependent).

**Finalized page split (decided 2026-07-14):**

*Quick stream (7 pages)* — minimal, opinionated defaults for everything cosmetic/tuning:

1. Welcome
2. Stream chooser
3. Connect / pair controller
4. Check displays
5. **Assign channels — full per-channel dropdowns** (no auto-apply magic; user assigns
   each channel, ch1 pre-filled Master, ch2–ch6 start unassigned per item 3a)
6. **Application Setup — condensed:** only **Start at login** + **Start minimized to
   tray** (the two personal-preference toggles). All other app-setup checkboxes stay at
   their defaults, unshown.
7. Done

*Defaults applied silently in Quick* (not shown as pages): theme = Follow-system,
encoder feel = Normal, anti-burn-in = on, audio backend = auto-selected, updates =
check-on / auto-apply-off, and the un-shown app-setup checkboxes (auto-connect,
scan-all-ports, minimize-to-tray, show tray notifications) = their defaults.

*Advanced stream (11 pages)* — full control:

1. Welcome → 2. Stream chooser → 3. Connect → 4. Check displays
5. **Audio backend** (Advanced-only, item 17)
6. Assign channels (full dropdowns)
7. **Application Setup** — full checkbox page (item 5)
8. **Theme / Style Settings** (item 12)
9. **Encoder Feel + "try it" overlay** (item 13)
10. **Auto-update prefs** (item 14)
11. Done

So Advanced adds 5 pages Quick hides (audio backend, full app-setup, theme, encoder
feel, update prefs), and Quick's app-setup page is a 2-toggle subset of Advanced's.

**Quick Defaults set (finalized 2026-07-14).** Quick applies the standard app defaults
(including the items 1–3 / 9 / tray-login changes from v3.16) — there is **no
Quick-specific divergence** to maintain. The two condensed toggles render pre-checked
(their new `true` defaults) and the user can uncheck them. Concrete values Quick lands:

| Setting | Quick value | Source |
|---|---|---|
| `StartWithWindows` ("Start at login") | **on** (user can uncheck on condensed page) | v3.16 default flip |
| `StartMinimizedToTray` | **on** (user can uncheck on condensed page) | v3.16 default flip |
| `MinimizeToTray` | **on** | v3.16 default flip |
| `AutoConnectOnLaunch` | on | app default |
| `ScanAllComPortsIfRememberedMissing` | on | app default |
| `TrayNotificationsEnabled` | on | app default |
| `AdvancedDebugLogging` | off | app default (now under Debug tab) |
| `ThemeMode` | FollowSystem | app default |
| `OledDisplayMode` | LargeVolume | v3.16 (item 9) |
| `OledAntiBurnInEnabled` | on | app default |
| Brightness / sleep / idle action / idle timeout | 100% / 2 min / DimTo30 / 10 min | app defaults |
| `EncoderSensitivityPercent` | 50 | app default |
| `AccelerationEnabled` (Medium preset) | **on** | v3.16 default flip |
| `VolumeSmoothingEnabled` (Normal speed) | off | app default |
| Overlay (enabled / pos / timeout / opacity / scale / all-screens) | on / BottomCenter / 2.5 s / 1.0 / 1.0 / off | app defaults |
| `AudioBackendMode` | WASAPI (auto) | app default |
| Channels | ch1 Master bound; ch2–6 assigned by the user on the full-dropdown Quick assign page | v3.16 (items 1–3) |
| `AutoCheckForUpdates` / `AutoApplyUpdates` | on / off | v3.19 defaults |

Because Quick == app defaults, the only thing the Quick stream truly *decides* is
channel assignment (page 5) and the two login/tray toggles (page 6); everything else is
the install-wide default the user can revisit in Setup later.

**Item 5 — Application-Setup checkboxes in the wizard.** Add a page mirroring the Setup
tab's Application Setup checkboxes: Auto-connect on launch, Scan all COM ports if
remembered controller missing, Minimize to tray, Start minimized to tray, Start program
at login, Show tray notifications — **excluding "Advanced debug logging"** (item 6 moves
it to Debug). Also surface **"Enable anti-burn-in pixel shifting" enabled by default**
with a disclaimer ("For best display lifetime, leave this enabled"). All bind to existing
settings.

**Item 12 — theme page (Style Settings).** A wizard page exposing the existing Style
Settings (Follow-system / Light / Dark, + any accent options) so the user picks a theme
during setup. Reuse the Setup tab's theme controls/logic (`ApplyTheme`).

**Item 13 — Encoder-Feel page with live "try it".** A wizard page with the Encoder-Feel
controls (sensitivity, acceleration, smoothing) **plus a "Try it" affordance that shows a
dummy volume overlay** the user can drive to feel each setting live. Reuse
`VolumeOverlay`/`VolumeOverlayController` in a sandboxed "demo" mode fed synthetic
deltas (no real audio write) so the user sees the overlay respond as they turn a
slider/scroll. This is the fiddliest sub-item (wiring a live overlay preview into the
wizard).

**Item 14 — auto-update page.** A wizard page for the update prefs from item 4/19:
"Automatically check for updates" + "Automatically download & apply updates" toggles,
with a "Check now" button. Thin until v3.19 lands the backend — ship as check-only and
enrich, or sequence v3.19 first.

> **Settings-ownership split (decided 2026-07-14).** The two bool settings
> **`AutoCheckForUpdates` (default on)** and **`AutoApplyUpdates` (default off)** are
> **introduced here in v3.18** — the wizard page and the Quick Defaults table both write
> them, so they must exist as of v3.18 even though nothing acts on them yet. **v3.19 adds
> only the *engine*** (the checker timer + download/apply) that reads these settings; it
> does **not** define them. This keeps v3.18 self-contained (the wizard doesn't reference
> settings that don't exist) and lets the two bumps ship in either order. If v3.19 somehow
> lands first, it must not re-declare the settings.

**Item 17 — audio backend in the Advanced stream.** Surface the audio-backend selector
(WASAPI / VoiceMeeter on Windows; the backend switch already exists in Setup) as an
**Advanced-stream-only** wizard page. Quick stream keeps the auto-selected default.

**Verification:** both TFMs 0/0; tests green (extract stream/step-sequence logic as a
testable seam where possible); Windows launch runs both streams end-to-end, and the
Encoder-Feel demo overlay actually animates. Bump chores.

---

### v3.19 — Auto-update checker + one-click updater  *(items 4 & 14)*

Builds on the existing manual `UpdateCheckService` (GitHub Releases API,
`App.Avalonia/Services/UpdateCheckService.cs`) + pure `Core/UpdateCheck.IsNewer`
comparator. Bump **3.18 → 3.19**.

**Scope (per decision): download + one-click apply.**

- **Settings:** `AutoCheckForUpdates` (default on) and `AutoApplyUpdates` (default
  **off**) are **already introduced in v3.18** (see the settings-ownership split under
  item 14) — v3.19 **reads** them, doesn't declare them. What v3.19 *adds* to
  `Core/Settings.cs`: persist a "last checked" timestamp + a "skipped version" so the
  user can dismiss a release. Both toggles are independently disableable (item 4
  requirement).
- **Checker:** a background timer (e.g. on launch + every N hours) calls the existing
  `UpdateCheckService.CheckAsync`. On `UpdateAvailable`, show a non-modal prompt
  ("v3.x available — Download & install / Skip / Remind me"). Gate on
  `AutoCheckForUpdates`.
- **Downloader/applier:** pick the right release asset for the platform (the release
  workflow publishes a Windows Inno installer + portable exe, and a Linux `.deb` +
  AppImage — see CLAUDE.md roadmap item 3). Download to a temp dir with a SHA/size check,
  then:
  - **Windows:** launch the downloaded installer (optionally `/SILENT`) and exit so it
    can replace the running exe; or stage a self-replace + relaunch. Note: installer is
    **unsigned** today → SmartScreen friction; flag that code-signing (already a
    CLAUDE.md TODO) materially improves this UX.
  - **Linux:** `.deb` → hand off to the system package tool / open it; AppImage →
    download the new AppImage beside the old and relaunch. `wpctl`-style shell-out is
    fine; no elevation baked in.
  - **macOS:** out of scope (no signed `.dmg` yet).
- **`--safe` interaction:** suppress auto-check/auto-apply under the existing `--safe`
  flag.
- **UI:** the Setup "Software update" card + the wizard page (item 14) share these
  toggles and the "Check now" / "Download & install" buttons.

**Open dependencies:** unsigned Windows installer (SmartScreen); no macOS signing; the
Linux `.deb` apply path needs a package manager present. Effort **L**; consider splitting
checker-automation (S, ships first) from the download/apply engine (M–L).

---

### v3.20 — Share Diagnostics (crash logger + server upload)  *(item 18)*

An opt-in crash/diagnostics reporter surfaced in the Setup tab as **"Share Diagnostics"**.
Bump **3.19 → 3.20**.

- **Prereq — global crash handler:** the Avalonia host currently has **no** unhandled-
  exception handler (parity-audit gap in CLAUDE.md). First wire
  `AppDomain.UnhandledException` / `TaskScheduler.UnobservedTaskException` / the Avalonia
  dispatcher-exception hook to write a crash log (reuse `LogService`) and show a friendly
  dialog. This is the capture layer the reporter needs — worth doing regardless.
- **Setting:** `bool ShareDiagnostics` in `Core/Settings.cs`, **default OFF (opt-in)**.
  First-run/Setup shows a clear consent explanation of what's sent and where.
- **What's collected:** the crash log + recent app log + environment (OS, app version,
  firmware/protocol) — **redacted**: strip usernames from paths, drop the COM/serial
  device serial, no audio-session app names beyond what's needed. Redaction is a testable
  Core seam.
- **Transport:** a **pluggable upload endpoint** behind a Core interface
  (`IDiagnosticsUploader` + a `NullDiagnosticsUploader`), so the actual server URL is
  configurable and swappable. Upload is a plain HTTPS POST of the redacted bundle;
  fire-and-forget with a size cap; never blocks shutdown.
- **Manual path:** even with upload off, keep an "Export diagnostics zip" (the existing
  `ExportDiagnostics`) so users can attach logs to a GitHub issue manually.

**OPEN DEPENDENCY (blocks the upload half):** **no server/endpoint exists yet.** Options
to decide before the upload path is built: (a) stand up a minimal endpoint I specify
later; (b) a hosted service (Sentry-style) — faster to real data, adds a third-party dep
+ privacy-policy surface; (c) ship the capture + redaction + manual-zip now and leave
`IDiagnosticsUploader` null until an endpoint is chosen. Recommend building (c) first so
the crash-handler + redaction land immediately, then wire transport when the endpoint is
picked. Effort **L**.

---

## SendHotkey — synthesized key combos  *(unscheduled, seam already exists)*

A `SendHotkey` channel-button action: pressing a knob button synthesizes a
user-configured key combination. Fire-and-forget and app-agnostic — the motivating use
case is **Discord mic-mute / deafen** (send the combo bound to Discord's own
mute/deafen shortcut), but the same action covers OBS scene switch, Teams mute, push-to-
talk, etc. Distinct from the existing `ToggleAssignedMute`, which only mutes an app's
**playback volume** via the audio backend and cannot touch Discord's in-app mic/deafen
state.

The Core seam was laid down in Phase 0 and is **unimplemented**:

- `Core/IKeystrokeSender.cs` — interface + `NullKeystrokeSender` (always returns
  `false`) exist; its doc comment already names "Discord push-to-talk, OBS scene switch,
  Teams mute." `HotkeyBinding` (VK code + `KeyModifiers` bitmask) is the persisted combo.
- **No concrete implementation** — planned as Windows `SendInput`, Linux `uinput`/XTEST,
  macOS `CGEventPost`.
- **Not selectable** — there is no `SendHotkey` value in `ChannelButtonActions`
  (`Core/DomainConstants.cs:10`), so the action can't be chosen in the channel-detail UI.

Rough scope to make it real:

- Add `SendHotkey` to `ChannelButtonActions` (+ `IsValid`/long-press/double-press lists)
  and the channel-detail action dropdown (`MainWindow.ChannelDetail.cs`).
- Add a per-channel `HotkeyBinding SendHotkeyBinding` to `ChannelSettings`
  (`Core/Settings.cs`) + a hotkey-capture picker in the channel detail UI.
- Implement `IKeystrokeSender` per platform (Windows first: `SendInput`); wire it into
  the button-action dispatch in `ChannelRuntime`. Respect the `--safe` write-suppression
  gate like other output actions.
- Effort: **M** (Windows-only first cut), **L** with Linux/macOS synthesis.

---

## Backlog (unscheduled)

Deferred/descoped items that could become future feature work, from the parity audit:

- Named **profiles** (F2) — descoped from the port; Core still backs it
  (`ProfileEntry`/`Profiles`/`ActiveProfileName`).
- **Output-device cycling** — descoped.
- **Per-channel mute hotkeys**, **SteelSeries Sonar backend** — deferred/post-port.
- **Linux/Wayland global hotkeys** — no cross-DE API; harder than audio was.
- **macOS**: notarized `.dmg`, master-volume audio backend, notifications (no-op today).
- **Code-signing** the Windows installer.
- **Software-jitter anti-burn-in** *(future firmware; follow-up to v3.16 items 10/11)* —
  today the firmware anti-burn uses hardware `SETDISPLAYOFFSET` (a vertical 0–3px shift
  that **wraps**); v3.16 item 11 worked around the wrap by reserving a 3-row bottom margin
  in every display mode (`OledRenderer.AntiBurnMaxOffset`). The cleaner long-term fix is to
  replace the hardware offset with a small **2-D software jitter** (±1–2px x/y walk within a
  reserved 1–2px border) applied in the firmware drawing helpers, so content is **clipped,
  never wrapped**, and no per-mode margin bookkeeping is needed. Better burn-in coverage
  (horizontal + vertical drift) too. Cost: threads the offset through ~30 draw calls in all
  modes on both the firmware and the Core `OledRenderer` preview, kept pixel-synchronized;
  needs a reflash + hardware look. Deferred from v3.16 in favour of the margin fix (option
  1) 2026-07-14.
