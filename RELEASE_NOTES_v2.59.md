# v2.59 — Audio tab UI improvements

## Features

- **Multi-app channel pool** — a single channel can now watch a list of apps and automatically route to whichever one is currently running. Useful for games: assign KSP, CS:GO, Minecraft etc. to the same channel and the encoder controls whichever is open. Apps can be added via the new **Add** button and removed from the chip strip that appears below the assignment row.

- **Resizable Audio tab splitter** — the divider between the *Channel Mapping* and *Selected Channel* panels is now a draggable `GridSplitter`. Drag left or right to give each panel as much space as needed. The split position is saved and restored across restarts. Default is 50/50 (previously 55/45 biased left).

- **Collapsible Output Devices card** — the Output Devices list now has a ▶/▼ chevron toggle. Collapsed by default to save vertical space; state persists across restarts.

- **Scalable controller preview** — the Whole Controller Preview (OLEDs + encoders) in the OLED tab now scales proportionally to fill the available panel width using a `Viewbox`. Previously the preview was a fixed size requiring horizontal scrolling on narrower windows.

## Bug fixes

- **All audio sessions now appear in the Assign To dropdown** — sessions were being silently deduplicated by process name, so running multiple instances of the same app (e.g. two Chrome windows) would only show one entry. All sessions now appear, with duplicate names disambiguated as *chrome*, *chrome (2)*, *chrome (3)*, etc.

- **Output Devices panel now populates correctly** — devices were never appearing due to a COM apartment threading issue (`Task.Run` + `Dispatcher.InvokeAsync` caused the `MMDeviceEnumerator` to be disposed before the callback ran). Fixed by running enumeration synchronously on the UI thread.

## Other

- Default window size changed to 1300 × 900 (was 1268 × 833).
- Removed a stale hardcoded "v2.15 — Double-press button action…" subtitle that was never updated.

## Firmware

No firmware changes. Requires firmware protocol **v2.24** (unchanged from v2.57).
See PR #67 (`feat/v2.58-controller-pairing`) for the optional **v2.25** firmware update that enables the Paired Controller feature.
