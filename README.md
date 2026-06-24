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

```
dotnet build -c Release
```

To publish a standalone `.exe` for distribution:

```
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

Output is in `bin\Release\net10.0-windows\win-x64\publish\`.

---

## Firmware

The ESP32 Arduino source is in `Computer_Volume_Controller_v2.25/` (current; protocol v2.25 adds controller pairing via chip ID). The previous `Computer_Volume_Controller_v2.24/` is retained for reference.

Flash the firmware via Arduino IDE / Arduino CLI with the ESP32-S3 Arduino core installed, or using esptool directly.

---

## Project structure

```
PcVolumeControllerDashboard.slnx        — solution file (main project + tests)
PcVolumeControllerDashboard.csproj      — .NET 10 WPF project
MainWindow.xaml / .cs                   — main application window
App.xaml / .cs                          — WPF app entry point
AssemblyInfo.cs                         — assembly attributes
Assets/                                 — application assets (app-icon.ico)
tests/                                  — xUnit + FluentAssertions test project
Computer_Volume_Controller_v2.25/       — ESP32 Arduino firmware source (v2.25, current — adds chip-ID pairing)
Computer_Volume_Controller_v2.24/       — previous firmware source (v2.24, retained for reference)
firmware_bin/                           — firmware build output
```

---

## Version compatibility

| Dashboard | Required firmware protocol | Hardware       |
|-----------|---------------------------|----------------|
| v3.2      | v2.24                     | v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8) |
| v3.1      | v2.24                     | v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8) |
| v3.0      | v2.24                     | v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8) |
| v2.61.1   | v2.24                     | v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8) |
| v2.59     | v2.24                     | v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8) |
| v2.57     | v2.24                     | v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8) |
| v2.56     | v2.24                     | v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8) |
| v2.55     | v2.24                     | v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8) |
| v2.54     | v2.24                     | v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8) |
| v2.53     | v2.24                     | v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8) |
| v2.52     | v2.24                     | v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8) |
| v2.51     | v2.24                     | v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8) |
| v2.50     | v2.24                     | v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8) |
| v2.49     | v2.24                     | v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8) |
| v2.48     | v2.24                     | v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8) |
| v2.47     | v2.24                     | v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8) |
| v2.46     | v2.24                     | v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8) |
| v2.45     | v2.24                     | v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8) |
| v2.44     | v2.24                     | v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8) |
| v2.43     | v2.24                     | v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8) |
| v2.42     | v2.24                     | v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8) |
| v2.41     | v2.24                     | v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8) |
| v2.40     | v2.24                     | v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8) |
| v2.39     | v2.24                     | v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8) |
| v2.38     | v2.24                     | v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8) |
| v2.37     | v2.24                     | v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8) |
| v2.36     | v2.24                     | v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8) |
| v2.35     | v2.24                     | v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8) |
| v2.34     | v2.24                     | v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8) |
| v2.33     | v2.24                     | v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8) |
| v2.32     | v2.24                     | v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8) |
| v2.31     | v2.24                     | v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8) |
| v2.30     | v2.24                     | v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8) |
| v2.29     | v2.24                     | v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8) |
| v2.28     | v2.24                     | v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8) |
| v2.27     | v2.24                     | v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8) |
| v2.26     | v2.24                     | v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8) |
| v2.25     | v2.24                     | v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8) |
| v2.24     | v2.24                     | v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8) |
| v2.23     | v2.21                     | v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8) |
| v2.22     | v2.21                     | v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8) |
| v2.21     | v2.21                     | v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8) |
| v2.20     | v2.15                     | v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8) |
| v2.19     | v2.15                     | v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8) |
| v2.18     | v2.15                     | v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8) |
| v2.17     | v2.15                     | v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8) |
| v2.16     | v2.15                     | v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8) |
| v2.15     | v2.15                     | v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8) |
| v2.14     | v2.12                     | v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8) |
| v2.13     | v2.12                     | v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8) |
| v2.12     | v2.12                     | v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8) |
| v2.11     | v2.10                     | v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8) |
| v2.10     | v2.10                     | v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8) |
| v2.9      | v2.9                      | v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8) |
| v2.5–v2.8 | v2.5                      | v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8) |
| v2.0–v2.4 | v2.0                      | v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8) |
| v1.3.39   | v1.3.38 Beta 7            | Prototype (1-channel) |

---

## Logs

Runtime logs are written to `%APPDATA%\PcVolumeController\logs\` as `dashboard-YYYYMMDD-HHmmss.log`.
