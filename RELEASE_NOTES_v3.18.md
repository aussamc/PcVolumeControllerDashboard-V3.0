# v3.18 — First-run wizard overhaul

The setup wizard is rebuilt around **two streams** so new users pick how much they want
to configure up front. No settings-file migration, protocol, or firmware changes — the
controller does **not** need a reflash. Existing users keep everything; this changes the
first-run experience and adds a couple of new preferences.

## Two-stream setup

- **Quick vs Advanced chooser** — right after Welcome the wizard asks how you'd like to
  set up. **Quick** (preselected) keeps it short: connect, check the displays, assign your
  knobs, and pick a couple of preferences — everything else uses recommended defaults.
  **Advanced** adds pages for the audio backend, theme, and software-update preferences.

- **Quick stream (7 steps):** Welcome → Choose setup → Connect → Check displays → Assign
  channels → a condensed Application-Setup page (Start at login / Start minimized to tray)
  → Done.

- **Advanced stream (10 steps):** the above plus an **Audio backend** page (WASAPI /
  VoiceMeeter), the full **Application Setup** checkbox page, a **Theme** page
  (Follow-system / Light / Dark, applied live), and a **Software updates** page.

- The **anti-burn-in pixel-shift** toggle (on by default, with a display-lifetime note)
  now lives on the **Check displays** page, so it appears in both streams.

## New wizard pages

- **Application Setup** — mirrors the Setup tab's app toggles. "Advanced debug logging"
  stays on the Debug tab, not in the wizard.
- **Theme** — pick Follow-system / Light / Dark during setup; applied immediately.
- **Audio backend** (Advanced only) — choose WASAPI or VoiceMeeter, switched live, with a
  status line showing the active backend and target count.
- **Software updates** — "Automatically check for updates" (on) and "Automatically
  download & install updates" (off) preferences, plus a **Check now** button. The
  auto-download/install engine arrives in a later release; for now the page checks and
  remembers your choice.

## New settings

- `AutoCheckForUpdates` (default **on**) and `AutoApplyUpdates` (default **off**) —
  independent toggles used by the new wizard page; both fresh-install and existing users
  get these defaults.

## Compatibility

- Required controller firmware protocol: **v2.24**.
- Hardware: v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8).
