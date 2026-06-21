# v3.0 — Cross-platform baseline

v3.0 is the baseline for the cross-platform rewrite of the PC Volume Controller
Dashboard. The codebase starts as a direct copy of v2.61.1 (WPF / .NET 10,
Windows-only). No behaviour changes ship in this baseline — it establishes the
v3.0 version stamp from which the cross-platform work (Avalonia UI, then Linux
and macOS platform layers) proceeds.

## What's in this baseline

- Version stamped to **3.0** across the dashboard (assembly identity, window
  title, About dialog, diagnostics export).
- Functionally identical to v2.61.1. Same WPF UI, same serial protocol, same
  audio backends.

## Roadmap (not in this release)

The cross-platform work is sequenced as:

1. **Core library extraction** — carve a platform-agnostic `Core` library out of
   the WPF monolith (serial, settings + migrations, audio abstraction, OLED
   rendering, encoder math, domain objects). No behaviour changes.
2. **Avalonia UI port** — faithful re-author of the UI in Avalonia, then an
   information-architecture redesign. WPF is retired at this point.
3. **Linux platform layer** — PipeWire/PulseAudio audio, X11 hotkeys, udev
   hotplug, `.deb` / AppImage packaging.
4. **macOS platform layer** — Core Audio (master volume for v1), CGEventTap
   hotkeys, IOKit hotplug, notarized `.app` / `.dmg`.

## Firmware

No firmware reflash required for the v3.0 baseline. The dashboard requires
firmware protocol **v2.24** or later. Firmware **v2.25** (current) adds the
ESP32 chip ID to the HELLO handshake to enable controller pairing; the dashboard
consumes it when present and remains backward-compatible with v2.24 firmware.
