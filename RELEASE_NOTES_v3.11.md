# v3.11 — Avalonia: Linux audio backend (PipeWire)

The dashboard now controls real audio on Linux instead of showing every channel as
"Unavailable"/"App offline". A new `PipeWireAudioBackend` (`Platform.Linux/`)
implements the same neutral `IAudioBackend` contract the Windows WASAPI backend uses,
so master volume, microphone input, and per-application volume/mute all work
end-to-end against PipeWire (via WirePlumber's `wpctl`).

## Features
- **Linux per-app audio control** — assign an encoder to master, microphone, or any
  running app's audio stream, exactly like on Windows. Multi-app pools (a channel
  that follows whichever pool member is actively playing) work the same way too.
- Reads (volume, mute, live target list) come from a single periodically-refreshed
  `pw-dump` graph snapshot, so the UI's 20Hz channel poll never shells out per call;
  only user-driven volume/mute changes shell out to `wpctl`.

## Notes
- Requires PipeWire + WirePlumber (`pw-dump`, `wpctl`) — the default modern Linux
  audio stack. Without them, Linux falls back to the previous no-op behavior instead
  of crashing.
- macOS still uses the no-op backend; its CoreAudio implementation hasn't landed.
- The shipping Windows (WPF) dashboard is functionally unchanged (version string only).

## Compatibility
- Required controller firmware protocol: **v2.24** (firmware v2.25 is
  backward-compatible).
- Hardware: v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8).
