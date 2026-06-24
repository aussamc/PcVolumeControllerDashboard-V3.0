# v3.3 — Phase 1: Avalonia OLED Setup tab

v3.3 adds the **OLED Setup** tab to the cross-platform Avalonia host (`App.Avalonia`).
The shipping Windows (WPF) dashboard is unchanged; this continues the incremental
Avalonia UI port.

## Avalonia OLED tab

- **OLED display settings** wired to Core `DashboardSettings`: display mode,
  brightness, disconnected sleep timeout, connected idle action + timeout, and
  anti-burn-in pixel shifting.
- **Whole-controller preview**: six 128×64 OLED previews rendered live from the
  platform-agnostic Core `OledRenderer`, driven by the selected display mode and
  each channel's saved name (with sample volumes). All five firmware display
  modes are exercised.
- **New Avalonia OLED bitmap builder** (`OledImage`) — converts the renderer's
  monochrome pixel buffer to an Avalonia `WriteableBitmap`. This is the
  cross-platform counterpart to the WPF host's WPF-only `ToWriteableBitmap`
  extension, so the same Core renderer now drives both hosts.

Live device/channel data and pushing OLED settings to the ESP32 arrive with the
serial layer in a later PR.

## Firmware

No firmware changes. Requires firmware protocol **v2.24** or later (v2.25
current, backward-compatible).
