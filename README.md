# PC Volume Controller Dashboard

A Windows desktop app that lets a physical rotary-encoder controller (ESP32-S3) control per-app audio volume on your PC.

Each encoder knob on the hardware maps to a Windows audio session (an app, a browser tab, system sounds, or master volume). Turning the knob changes that app's volume; pressing it can mute it or jump to the next channel. The dashboard runs in the system tray and communicates with the controller over USB.

---

## Requirements

- Windows 10 or 11 (x64)
- PC Volume Controller hardware v1.4 (ESP32-S3-DevKitC-1-N16R8 based, with firmware v2.0 or later)
- USB cable to connect the controller

To build from source:
- .NET 10 SDK
- Visual Studio 2022 or Rider (optional — `dotnet build` works standalone)

---

## Getting started

1. Connect the controller via USB.
2. Run `PcVolumeControllerDashboard.exe`.
3. The app detects the controller's COM port automatically.
4. Use the **Channel Mappings** panel to assign each encoder to a Windows audio session.

Settings are saved to `%APPDATA%\PcVolumeController\settings.json` and restored on next launch.

---

## Features

- **6-channel control** — six independent encoder knobs, each mapped to any Windows audio session.
- **Per-channel audio targets** — assign any encoder to any running audio session, or to master volume.
- **Per-channel sensitivity** — each encoder has its own sensitivity override, or inherits the global setting.
- **Microphone input control** — assign any encoder to the default capture device to control microphone volume.
- **Global hotkeys** — system-wide keyboard shortcuts for master volume, mute, profile cycling, and show dashboard; all unassigned by default.
- **Per-channel short-button action** — select next channel, toggle mute, or no action.
- **Per-channel OLED display** — each encoder has its own SSD1315 OLED showing channel name and volume.
- **Named profiles** — save and switch between named sets of channel assignments instantly from the Audio tab.
- **Auto sleep/wake** — sends a sleep command to the controller when the PC is idle (10 min), locked, or suspended; wakes it on activity.
- **Auto-reconnect** — detects controller disconnects and reconnects automatically.
- **Diagnostics export** — one-click zip of logs and settings for bug reports.
- **Debug console** — live serial traffic view for development.

---

## Building

The app is the Avalonia host, which multi-targets — build the TFM for your OS:

```
# Windows
dotnet build App.Avalonia/App.Avalonia.csproj -c Release -f net10.0-windows10.0.17763.0
# Linux / macOS
dotnet build App.Avalonia/App.Avalonia.csproj -c Release -f net10.0
```

To publish a standalone Windows `.exe` for distribution:

```
dotnet publish App.Avalonia/App.Avalonia.csproj -c Release -f net10.0-windows10.0.17763.0 -r win-x64 --self-contained true -p:PublishSingleFile=true
```

Output is `PcVolumeControllerDashboard.Avalonia.exe` under
`App.Avalonia\bin\Release\net10.0-windows10.0.17763.0\win-x64\publish\`.

---

## Firmware

The ESP32 Arduino source is in `Computer_Volume_Controller_v2.26/` (current; v2.26 redesigns the Large Volume Number OLED layout — display-only, the wire protocol is unchanged from v2.25's chip-ID pairing). The previous `Computer_Volume_Controller_v2.25/` and `Computer_Volume_Controller_v2.24/` are retained for reference.

Flash the firmware via Arduino IDE / Arduino CLI with the ESP32-S3 Arduino core installed, or using esptool directly.

---

## Project structure

```
PcVolumeControllerDashboard.slnx        — solution file
App.Avalonia/                           — cross-platform Avalonia host (the app), net10.0 + net10.0-windows10.0.17763.0
Core/                                   — platform-agnostic domain (serial, settings, OLED renderer, seams), net10.0
Platform.Windows/                       — Windows audio backends (WASAPI + VoiceMeeter) behind the Core seam
Platform.Linux/                         — Linux audio backend (PipeWire via pw-dump/wpctl) behind the same seam
tests/                                  — xUnit + FluentAssertions test project
Computer_Volume_Controller_v2.26/       — ESP32 Arduino firmware source (v2.26, current — Large Volume OLED redesign)
Computer_Volume_Controller_v2.25/       — previous firmware source (v2.25, retained for reference)
Computer_Volume_Controller_v2.24/       — older firmware source (v2.24, retained for reference)
firmware_bin/                           — firmware build output
```

> The original Windows-only **WPF host** was retired in v3.x once the Avalonia host
> reached feature parity; the Avalonia host is now the single UI on Windows, Linux, and macOS.

---

## Version compatibility

Five most recent releases shown below. For the complete history, see
[VERSION_COMPATIBILITY.md](VERSION_COMPATIBILITY.md).

| Dashboard | Required firmware protocol | Hardware       |
|-----------|---------------------------|----------------|
| v3.15     | v2.24                     | v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8) |
| v3.14.2   | v2.24                     | v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8) |
| v3.14.1   | v2.24                     | v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8) |
| v3.14     | v2.24                     | v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8) |
| v3.13     | v2.24                     | v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8) |

---

## Logs

Runtime logs are written to `%APPDATA%\PcVolumeController\logs\` as `dashboard-YYYYMMDD-HHmmss.log`.
