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
