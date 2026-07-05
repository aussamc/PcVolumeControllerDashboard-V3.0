# CLAUDE.md

Project guidance for working in this repository. Applies on any machine/OS (loaded
automatically from the repo root).

## What this is

Cross-platform dashboard for the **PC Volume Controller** — a physical rotary-encoder
+ OLED per-channel volume controller (ESP32-S3) connected over USB CDC serial. It
maps six knobs/buttons to per-app / master / device audio and mirrors live state back
to the controller's OLEDs.

This repo is the **v3 cross-platform rewrite** (Windows + Linux + macOS via
**Avalonia UI**), ported from a Windows-only **WPF/.NET 10** app (v2.61.1). The port
is a faithful code-behind port first, not an MVVM rewrite. The WPF host still lives in
the repo and builds, but is **being retired** once the Avalonia build reaches parity.

## Repository layout

| Path | TFM | Purpose |
|---|---|---|
| `Core/` (`PcVolumeControllerDashboard.Core.csproj`) | `net10.0` | Platform-agnostic domain: serial layer + protocol parser, settings repository/POCOs + migrations, OLED renderer, encoder math, audio & hotkey seams. **No WPF/Windows/Avalonia refs.** |
| `App.Avalonia/` (`App.Avalonia.csproj`) | `net10.0;net10.0-windows` | The cross-platform Avalonia host (the future single UI). References Core; references `Platform.Windows` only on the `-windows` TFM. |
| `Platform.Windows/` | `net10.0-windows` | Windows-only implementations behind Core seams: WASAPI + VoiceMeeter audio backends. |
| `PcVolumeControllerDashboard.csproj` (root) | `net10.0-windows` | The legacy **WPF host** (being retired). Keep it building but functionally untouched until convergence. |
| `tests/` (`PcVolumeControllerDashboard.Tests.csproj`) | `net10.0-windows` | xUnit + FluentAssertions. Tests Core (pure logic) + Windows platform pieces. |
| `Computer_Volume_Controller_v2.25/` | Arduino | Current ESP32 firmware (`.ino`). `v2.24/` kept for reference. |
| `Assets/`, `PcVolumeControllerDashboard.slnx` | | Icons; solution file. |

Namespaces: Core = `PcVolumeControllerDashboard.Core`; Avalonia host =
`PcVolumeControllerDashboard.App`; WPF host = `PcVolumeControllerDashboard`.

## Build, run, test

Work from the repo root. The Avalonia app **multi-targets**, so build the TFM for the
current OS explicitly:

```bash
# Build the Avalonia app
dotnet build App.Avalonia/App.Avalonia.csproj -f net10.0-windows   # Windows
dotnet build App.Avalonia/App.Avalonia.csproj -f net10.0           # Linux/macOS

# Run it
dotnet run --project App.Avalonia/App.Avalonia.csproj -f net10.0-windows   # Windows
dotnet run --project App.Avalonia/App.Avalonia.csproj -f net10.0           # Linux/macOS

# Tests (target the test project directly to avoid rebuilding the running app)
dotnet test tests/PcVolumeControllerDashboard.Tests.csproj
```

Verification bar for any change:

- **Both** Avalonia TFMs (`net10.0` and `net10.0-windows`) build at **0 warnings / 0 errors**.
- The full test suite passes.
- The Windows build launches.
- The WPF host (`PcVolumeControllerDashboard.csproj`) still builds.

Notes:

- The audio backend is chosen per-TFM by `AudioBackendFactory` via `#if WINDOWS`
  (WASAPI/VoiceMeeter on Windows; Core's `NullAudioBackend` elsewhere). Same pattern
  for global hotkeys (`WindowsGlobalHotkeyService` vs `NullGlobalHotkeyService`).
- Multi-target incremental builds can leave a **stale `net10.0-windows` output** — if
  a `--no-build` run seems to execute old code, rebuild that TFM explicitly (or clean
  `App.Avalonia/bin` + `obj`). Building the `.csproj` directly emits to
  `bin/Debug/<tfm>/`; solution builds emit to `bin/x64/Debug/<tfm>/`.
- Some settings tests touch the real user config path — run tests with `APPDATA`
  (Windows) / `XDG_CONFIG_HOME`-equivalent redirected to a temp dir if you don't want
  to read the live `settings.json`.

## Standing rules

1. **PRs only** — never commit directly to `main`. Branch `feat/v3.x-...` (features)
   or `fix/v3.x.y-...` (bug fixes). Work in small, build-green PRs.
2. **Versioning** — major/user-facing change bumps `3.x` (e.g. `3.9 → 3.10`); minor
   fix bumps `3.x.y`. On a bump, update **all** of: `DashboardVersion` in both hosts'
   `MainWindow` code, `<Version>/<AssemblyVersion>/<FileVersion>` in the Avalonia and
   WPF csprojs, the README compatibility table, and `RELEASE_NOTES_vX.Y.md` (remove
   the superseded notes file). Infra-only PRs don't bump.
3. **Keep the WPF host building and functionally untouched** until the convergence
   step (only version-string changes are made to it during the port).
4. **Serial channel indices are always 0-based (0–5) on the wire; always 1-based
   (1–6) in the UI.** Never change this.
5. **Identity handshake is strict** — `HELLO` must match both the `PC_VOLUME_CONTROLLER`
   name and the exact protocol string.
6. **Firmware version** — only bump the firmware version / rename its folder when the
   ESP32 code actually changes and a reflash is required.
7. **Logs / settings** live in the per-OS user config dir
   (`%APPDATA%\PcVolumeController\` on Windows, `~/.config/PcVolumeController/` on
   Linux, `~/Library/Application Support/PcVolumeController/` on macOS).

## Architecture & platform seams

- **`IAudioBackend`** (Core) — key-addressed audio: `MASTER`, `MIC_INPUT`,
  `PROC:<name>`, `VM_STRIP:n`, `VM_BUS:n`. Windows: WASAPI (`AudioService` port) +
  VoiceMeeter, wrapped in a runtime-switchable backend. Linux (planned): PipeWire via
  `wpctl` shell-out (stream IDs are ephemeral — enumerate dynamically). macOS (planned,
  v1 scope): master volume only.
- **`IGlobalHotkeyService`** (Core) — Windows impl uses `RegisterHotKey` on a
  dedicated message-only window / message loop (Avalonia has no `WndProc`). Null
  elsewhere (X11/Wayland, macOS deferred).
- **WASAPI COM affinity** — audio reads/writes are marshalled to the Avalonia UI
  thread (`Dispatcher.UIThread`); device events arrive on background threads.
- **Settings-wipe guard** — control-change handlers early-return while an
  `_initializing`/`_detailLoading` flag is set (they'd otherwise persist control
  defaults over just-loaded settings during construction).

## Serial protocol (firmware v2.25)

- Handshake: ESP32 → `HELLO,PC_VOLUME_CONTROLLER,<protocol>,<channels>,<chipId>`
  (5th field = chip ID for controller pairing; absent = pre-v2.25, still accepted).
- PC → ESP32: `STATE` / `CHSTATE` (per-channel OLED data) / `OLEDCFG` / `DISPMODE` /
  `PING` / `SLEEP` / `WAKE`.
- ESP32 → PC: `ENC,<ch>,<delta>` / `BTN_SHORT|BTN_LONG|BTN_DOUBLE,<ch>` / `PONG`.
- The connection owns a 1s keepalive (`PING`) + inbound-liveness drop + auto-reconnect;
  opening the port resets the ESP32 (it reboots before `HELLO`).

## Key constants

- Dashboard version: **3.10** (both hosts). Required controller protocol: **2.24**
  (firmware v2.25 is backward-compatible). Expected channels: **6**.
- Hardware: v1.4 PCB, ESP32-S3-DevKitC-1-N16R8, SSD1315 OLEDs behind a TCA9548A I2C
  mux. GPIO/OLED layout is final — see the firmware source.

## Current status & roadmap

**v3.10 shipped** — the Avalonia host is at **Windows feature-parity for the intended
scope**: full audio/channel runtime, live OLED state push, encoder feel + button
actions, per-channel detail, link groups + multi-app pools, volume overlay,
settings import/export, audio-backend switch, first-run wizard, global hotkeys, and a
software update check.

Remaining to finish the port:

1. **Linux launch re-check** (CachyOS / PipeWire) — **done 2026-07-04**, including a
   real-hardware pass. Builds clean (`net10.0`, 0 warnings/errors) and launches on
   CachyOS (KDE/Wayland). With a physical controller on `/dev/ttyACM0`: auto-detect,
   port open, ESP32 reboot, and identity handshake all worked (protocol 2.25, 6
   channels); OLED config + display-mode init sent to all 6 channels; wizard
   completed to the dashboard; physical encoder input confirmed live (`ENC ch0
   delta ±1` in both directions). One transient reconnect during the wizard
   (`port is closed`) self-healed within ~17s via the existing auto-reconnect —
   no manual fix needed. **Expected, not a bug:** the dashboard shows no
   assignable audio targets on Linux — `NullAudioBackend` (`Core/Audio/
   NullAudioBackend.cs:20`) always returns an empty target list until the planned
   PipeWire backend lands; `NullGlobalHotkeyService` means global hotkeys are
   likewise inert on Linux for now. `dotnet test` cannot run on Linux at all — the
   test project targets `net10.0-windows` only.
2. **Parity audit** — Avalonia vs WPF, feature by feature. **First pass done
   2026-07-04** (confirmed at parity: encoder acceleration, volume smoothing,
   per-channel sensitivity/limits/presets, settings import/export, diagnostics
   export, About dialog, update checker). Known deliberate gaps: per-channel mute
   hotkeys, output-device cycling, SteelSeries Sonar backend (all
   deferred/post-port). Newly found gaps, not yet fixed:
   - **Profile system missing from Avalonia UI** — WPF has full multi-profile
     support (create/rename/duplicate/delete/switch/cycle-next, tray submenu,
     global hotkey — `MainWindow.xaml.cs:611-932`, `MainWindow.Tray.cs:70-99`,
     `MainWindow.Hotkeys.cs:60,117-118`). Backed by cross-platform
     `ProfileEntry`/`Profiles`/`ActiveProfileName` in Core, so it's implementable
     on Avalonia, just never built — Avalonia's own `MainWindow.Hotkeys.cs` doc
     comment notes "CycleNextProfile descoped."
   - **Avalonia tray menu is missing most WPF actions** — WPF has Open Dashboard,
     Connect, Disconnect, Reconnect, Open Log Folder, Exit, double-click-to-restore
     (`MainWindow.Tray.cs:39-63`). Avalonia's tray menu (`App.axaml:22-28`) only has
     Show Dashboard and Exit.
   - **Avalonia's "tray notifications" setting is a dead no-op** — the
     `TrayNotificationsEnabled` checkbox exists (`MainWindow.axaml.cs:338,395`) but
     nothing reads it to actually show a notification.
   - **Not yet audited**: WPF's `MainWindow.Serial.cs` (1666 lines) vs. Avalonia's
     `Services/SerialConnectionService.cs` (398 lines) — the 4x size gap could hide
     reconnect edge-case differences; needs a focused follow-up pass.
3. **Retire WPF** — remove the WPF host and its Windows-only helpers, collapse the
   solution, then add a signed installer (Windows: Inno Setup; Linux: `.deb`/AppImage;
   macOS: notarized `.dmg`) and a CI build matrix.
