# Phase 0.5 — Linux derisk spike (THROWAWAY)

A tiny Avalonia app whose only job is to answer two questions on **real Linux**,
before committing weeks to the Avalonia port (Phase 1) and Linux audio backend
(Phase 2):

1. **Serial** — does the extracted Core `SerialService` open the ESP32 CDC port
   and read lines on Linux? (Reuses the real `Core` project, so this also proves
   Core is consumable on a non-Windows target.)
2. **PipeWire** — can we change a per-app (sink-input) volume from .NET? The
   pragmatic approach tested here is shelling out to `wpctl`.

This is a probe, not product code. **Delete `spike/` and the `spike/linux-derisk`
branch once you have your answers** — the learning is the deliverable.

## Prerequisites (Linux)

- A distro with **PipeWire** (Fedora, Ubuntu 24.04, Linux Mint, etc.).
- **.NET 10 SDK**.
- `wpctl` (ships with `wireplumber`) and/or `pactl` (`pulseaudio-utils`).
- For the serial probe: the ESP32 controller plugged in, and your user in the
  **`dialout`** group:
  ```sh
  sudo usermod -aG dialout $USER   # then log out/in (or reboot)
  ```
  In a VM, give the guest **USB passthrough** to the controller first.

## Run

```sh
dotnet run --project spike/LinuxDerisk
```

## What success looks like

- **Serial:** pick the port (typically `/dev/ttyACM0`), click **Connect**, and
  see `RX HELLO,PC_VOLUME_CONTROLLER,...` and live `ENC`/`BTN` lines in the log.
  - `OPEN FAILED: ... permission denied` ⇒ the `dialout` step above wasn't applied.
- **PipeWire:** start an app playing audio (browser, Spotify), click
  **List audio apps**, copy a stream/sink-input **id** into the box, move the
  slider, click **Set volume** — the app's volume should change in the system mixer.

If both work, **Linux is derisked.** Record which approach worked (e.g. `wpctl`
shell-out vs. needing a native binding) in project memory, then delete the spike.

## Notes

- UI is built in code (no AXAML) to keep the scaffold minimal.
- Not added to `PcVolumeControllerDashboard.slnx` — build/run it directly.
- The host `.csproj` excludes `spike/**` from its compile glob so the spike never
  pollutes the main build.
