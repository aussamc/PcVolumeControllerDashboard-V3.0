# v3.1 — Phase 0: Core library extraction

v3.1 completes **Phase 0** of the cross-platform rewrite: a platform-agnostic
`PcVolumeControllerDashboard.Core` library has been carved out of the WPF
monolith. This is an internal restructure — **no user-facing behaviour changes**.
The Windows dashboard works exactly as in v3.0; the groundwork is now in place
for the Avalonia UI port and the Linux/macOS platform layers.

## What moved into Core (`net10.0`, zero WPF/Windows references)

- **Serial layer** — `SerialService`.
- **Protocol & domain constants** — `ProtocolCommands` plus the display/encoder/
  audio/theme constant classes.
- **OLED renderer** — `OledRenderer` now produces a raw pixel buffer; the WPF
  bitmap conversion stays in the host as an extension method.
- **Settings** — `SettingsRepository` (load/save/backup + all v1→v8 migrations)
  and the settings model (`DashboardSettings`, `ChannelSettings`, `ProfileEntry`,
  `VolumePreset`, `HotkeySettings`, `HotkeyBinding`).
- **Encoder feel math** — `EncoderMath` (acceleration presets + EMA smoothing),
  now covered by unit tests.
- **Keystroke seam** — `IKeystrokeSender`, the cross-platform interface for the
  planned `SendHotkey` action (design only; per-OS implementations land later).

Windows-specific code (WPF UI, WASAPI/NAudio audio, VoiceMeeter, Win32 P/Invokes)
remains in the host. The `IAudioBackend` abstraction is intentionally **deferred
to the Linux platform phase**, where a second real backend will validate a clean
neutral contract.

## Tests

The Core extraction is covered by unit tests (serial lifecycle, settings
migration, encoder math). The 3 long-standing stale `SettingsRepository` tests
(asserting the pre-v8 schema version) were also fixed. Full suite is green.

## Firmware

No firmware changes. Requires firmware protocol **v2.24** or later (v2.25 current,
backward-compatible).
