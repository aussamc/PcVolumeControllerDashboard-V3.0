# Release Notes — v2.55

## Feature: Multiple-controller guard

Builds on the hardware identity pairing introduced in v2.54 to provide a visible
in-app warning when a different physical controller is connected.

### What's new

- **Chip-ID mismatch banner** — when the connected controller's chip ID does not
  match the stored paired ID, a yellow warning banner appears directly below the
  ESP32 status line.  The banner offers two actions:
  - **Forget & re-pair** — clears the stored chip ID and immediately re-pairs to
    the newly connected controller; normal operation resumes without a reconnect.
  - **Dismiss** — hides the banner for the current session without changing the
    stored pairing.

- **Controller Pairing section** (Settings → Application Setup) — shows the
  currently paired chip ID (`LastDeviceChipId`) and provides a **Forget
  controller** button to clear the pairing at any time.  The label updates live
  whenever settings are applied to the UI.

- **Banner auto-hide on disconnect** — the mismatch banner is hidden whenever the
  serial connection is torn down (both the connection-phase path and the explicit
  `DisconnectSerial` path), so stale warnings never persist across reconnects.

### Implementation notes

- `ShowChipIdMismatchBanner(string message)` / `HideChipIdMismatchBanner()` are
  UI helpers that must be called on the UI thread; callers on the serial thread
  use `Dispatcher.InvokeAsync`.
- `UpdatePairedControllerIdLabel()` refreshes the Settings label from
  `_settings.LastDeviceChipId`; called from `ApplySettingsToUi`.
- `ForgetControllerButton_Click` calls `SaveSettings` after clearing the stored ID.

---

## Compatibility

- Dashboard: v2.55
- Required firmware protocol: v2.24 (no reflash required)
- Hardware: v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8)
