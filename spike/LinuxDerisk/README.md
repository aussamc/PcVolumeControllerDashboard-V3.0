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

- A distro with **PipeWire** (Fedora, Ubuntu 24.04, Linux Mint, CachyOS/Arch, etc.).
- **.NET 10 SDK**.
  - Debian/Ubuntu/Fedora: distro packages or Microsoft's `dotnet-install.sh`.
  - **Arch / CachyOS:** `sudo pacman -S dotnet-sdk` (or the AUR / `dotnet-install.sh`
    if the repo version lags).
- `wpctl` (ships with `wireplumber`) and/or `pactl`. On CachyOS both are present
  out of the box (PipeWire + pipewire-pulse are the default).
- For the serial probe: the ESP32 controller plugged in, and your user in the
  serial device's group. **The group name differs by distro:**
  ```sh
  # Debian/Ubuntu/Fedora:
  sudo usermod -aG dialout $USER
  # Arch / CachyOS (ttyACM*/ttyUSB* are owned by group 'uucp'):
  sudo usermod -aG uucp $USER
  ```
  Then **log out/in (or reboot)** for the group to take effect. If Connect still
  fails, check the actual owner with `ls -l /dev/ttyACM0`.
  In a VM, give the guest **USB passthrough** to the controller first.

### Wayland note (CachyOS often defaults to Wayland)
Avalonia 11.2 renders through **XWayland** automatically, so no action is needed.
Native Wayland is experimental and not required for this spike.

## Run

GUI (watch the window yourself):
```sh
dotnet run --project spike/LinuxDerisk
```

Headless (no display needed — prints `PASS`/`FAIL`; ideal for letting Claude Code
run and self-verify the spike):
```sh
# serial only, read for 8s (plug in the ESP32 first)
dotnet run --project spike/LinuxDerisk -- --headless --no-audio --seconds 8

# list audio nodes, then set+verify a per-app volume
dotnet run --project spike/LinuxDerisk -- --headless --no-serial
dotnet run --project spike/LinuxDerisk -- --headless --no-serial --node <ID> --volume 30
```
Options: `--port <name>`, `--seconds <n>`, `--node <id>`, `--volume <0-100>`,
`--no-serial`, `--no-audio`.

**Running this with Claude Code on Linux?** See `CLAUDE_HANDOFF.md` for a paste-in
brief that drives the whole spike headlessly.

## What success looks like

- **Serial:** pick the port (typically `/dev/ttyACM0`), click **Connect**, and
  see `RX HELLO,PC_VOLUME_CONTROLLER,...` and live `ENC`/`BTN` lines in the log.
  - `OPEN FAILED: ... permission denied` ⇒ the serial-group step above wasn't
    applied (`uucp` on Arch/CachyOS, `dialout` on Debian/Ubuntu/Fedora).
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
