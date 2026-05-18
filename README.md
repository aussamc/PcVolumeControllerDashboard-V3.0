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
- **Per-channel sensitivity** — tune how fast each encoder changes volume.
- **Per-channel short-button action** — select next channel, toggle mute, or no action.
- **Per-channel OLED display** — each encoder has its own SSD1315 OLED showing channel name and volume.
- **Auto sleep/wake** — sends a sleep command to the controller when the PC is idle (10 min), locked, or suspended; wakes it on activity.
- **Auto-reconnect** — detects controller disconnects and reconnects automatically.
- **Firmware flasher** — built-in UI to flash new ESP32 firmware via `tools/esptool.exe`.
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

The ESP32 Arduino source is in `Computer_Volume_Controller_v2.15/`.

Flash the firmware using the dashboard's built-in flasher, or manually via Arduino IDE / Arduino CLI with the ESP32-S3 Arduino core installed.

The dashboard's built-in flasher requires `tools/esptool.exe`. See `tools/esptool_setup_instructions.txt` for how to obtain it.

---

## Project structure

```
PcVolumeControllerDashboard.csproj      — .NET 10 WPF project
MainWindow.xaml / .cs                   — main application window
App.xaml / .cs                          — WPF app entry point
AssemblyInfo.cs                         — assembly attributes
Computer_Volume_Controller_v2.10/       — ESP32 Arduino firmware source (v2.10, 6-channel)
firmware_bin/                           — firmware build output + instructions
tools/                                  — esptool.exe for firmware flashing
previous_version_notes/                 — release notes for older versions
```

---

## Version compatibility

| Dashboard | Required firmware protocol | Hardware       |
|-----------|---------------------------|----------------|
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
