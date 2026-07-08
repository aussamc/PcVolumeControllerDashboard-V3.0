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
| `App.Avalonia/` (`App.Avalonia.csproj`) | `net10.0;net10.0-windows` | The cross-platform Avalonia host (the future single UI). References Core; references `Platform.Windows` only on the `-windows` TFM, `Platform.Linux` only on the plain `net10.0` TFM. |
| `Platform.Windows/` | `net10.0-windows` | Windows-only implementations behind Core seams: WASAPI + VoiceMeeter audio backends. |
| `Platform.Linux/` | `net10.0` | Linux audio backend behind the same seam: `PipeWireAudioBackend` (shells out to `pw-dump`/`wpctl`). Also referenced on macOS builds (shared TFM), but `AudioBackendFactory` only instantiates it at runtime on Linux. |
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

- The audio backend is chosen by `AudioBackendFactory`: per-TFM via `#if WINDOWS`
  (WASAPI/VoiceMeeter on Windows), then a runtime `RuntimeInformation.IsOSPlatform`
  check on the shared non-Windows TFM (PipeWire on Linux; Core's `NullAudioBackend`
  on macOS, which has no TFM suffix of its own to branch on at compile time). Global
  hotkeys use the simpler per-TFM pattern (`WindowsGlobalHotkeyService` vs
  `NullGlobalHotkeyService`) since they're Windows-only for now.
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
  VoiceMeeter, wrapped in a runtime-switchable backend. Linux: `PipeWireAudioBackend`
  (`Platform.Linux/`) — reads come from a single periodically-refreshed `pw-dump`
  JSON graph snapshot (no shell-out on the UI's 20Hz poll path); writes shell out to
  `wpctl set-volume`/`set-mute` (stream IDs are ephemeral — always resolved from the
  current snapshot, never cached across refreshes). macOS (planned, v1 scope): master
  volume only.
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

- Dashboard version: **3.12** (both hosts). Required controller protocol: **2.24**
  (firmware v2.25 is backward-compatible). Expected channels: **6**.
- Hardware: v1.4 PCB, ESP32-S3-DevKitC-1-N16R8, SSD1315 OLEDs behind a TCA9548A I2C
  mux. GPIO/OLED layout is final — see the firmware source.

## Current status & roadmap

**v3.10 shipped** — the Avalonia host is at **Windows feature-parity for the intended
scope**: full audio/channel runtime, live OLED state push, encoder feel + button
actions, per-channel detail, link groups + multi-app pools, volume overlay,
settings import/export, audio-backend switch, first-run wizard, global hotkeys, and a
software update check.

**v3.11 adds the Linux audio backend** — `PipeWireAudioBackend` (`Platform.Linux/`)
closes the previous "no assignable targets on Linux" gap: master/mic/per-app volume
and mute now work end-to-end via `pw-dump` (reads) + `wpctl` (writes). Global hotkeys
remain Windows-only for now (Wayland has no cross-desktop-environment global-shortcut
API — a separate, harder problem than audio).

**v3.12 closes most of the parity backlog** (verified on Windows with real hardware,
2026-07-08). New/fixed on the Avalonia host: **auto sleep/wake** (PC idle/lock/suspend
→ controller SLEEP/WAKE, suspend-flush confirmed), encoder **debounce/coalescing/
reverse-guard**, a distinct **overlay mute mode**, a **channel-count-mismatch** warning,
and rejected/phantom-port **reconnect cooldowns** (which also fixed an incompatible-
controller status *flicker* and two Windows-only defects caught during verification: a
WPF-host build break and a crash-on-close stack overflow). With B1/B2/F3/F4 already on
`main`, the **only remaining parity blocker is the cross-platform desktop-notification
layer (F6)**; the rest is P2/P3 polish. Named profiles (F2) were **descoped**.
`PARITY_FIX_BACKLOG.md` is the live tracker.

Remaining to finish the port:

1. **Linux launch re-check** (CachyOS / PipeWire) — **done 2026-07-04**, including a
   real-hardware pass. Builds clean (`net10.0`, 0 warnings/errors) and launches on
   CachyOS (KDE/Wayland). With a physical controller on `/dev/ttyACM0`: auto-detect,
   port open, ESP32 reboot, and identity handshake all worked (protocol 2.25, 6
   channels); OLED config + display-mode init sent to all 6 channels; wizard
   completed to the dashboard; physical encoder input confirmed live (`ENC ch0
   delta ±1` in both directions). One transient reconnect during the wizard
   (`port is closed`) self-healed within ~17s via the existing auto-reconnect —
   no manual fix needed. The "no assignable audio targets on Linux" gap noted here
   originally is now **fixed as of v3.11** (`PipeWireAudioBackend`, see above);
   `NullGlobalHotkeyService` still means global hotkeys are inert on Linux for now.
   `dotnet test` cannot run on Linux at all — the test project targets
   `net10.0-windows` only.
2. **Parity audit** — Avalonia vs WPF, feature by feature. **First pass done
   2026-07-04** (confirmed at parity: per-channel sensitivity/limits/presets,
   settings import/export, diagnostics export, About dialog, update checker —
   encoder acceleration/volume smoothing were also flagged parity in that pass,
   but a 2026-07-05 deeper audit found two real gaps there, see below). Known
   deliberate gaps: per-channel mute hotkeys, output-device cycling, SteelSeries
   Sonar backend (all deferred/post-port). Named **profiles (F2) were descoped** from
   the port — not a gap. **Status as of v3.12 (2026-07-08):** most of the gaps
   catalogued below are now **resolved and merged** — B1, B2, F3, F4, plus the v3.12
   batch (Q1 encoder debounce, Q3 port cooldowns + incompatible-controller flicker
   fix, Q5 overlay mute mode, Q6 channel-mismatch warning, F1 auto sleep/wake); R1 is
   confirmed (Avalonia correct). **Still open:** F6 (cross-platform notification layer
   — the one P1 blocker), Q2 (target auto-refresh), Q4 (hardware self-test panel), the
   fuller Q6 diagnostics panel, and P3 items N1/N2/N3. See `PARITY_FIX_BACKLOG.md` for
   the live tracker. The detailed findings below are retained as the audit record:
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
     nothing reads it to actually show a notification. **Resolution (2026-07-06):**
     rather than remove the control, a cross-platform desktop-notification layer is
     greenlit to make it real (Avalonia's `TrayIcon` has no balloon API) — tracked
     as **F6** in `PARITY_FIX_BACKLOG.md`.
   - **Serial reconnect audit done 2026-07-05**: WPF's `MainWindow.Serial.cs`
     (1666 lines) vs. Avalonia's `Services/SerialConnectionService.cs` (398 lines).
     Most of the gap is justified (WM_DEVICECHANGE-driven refresh and WPF
     `ComboBox` binding gymnastics have no cross-platform equivalent needed), but
     found real gaps, not yet fixed:
     - **Bug: pre-2.24-firmware controllers can never connect on Avalonia** —
       `Core/SerialProtocol.cs:107-112` (`IsValidIdentity`) hard-rejects the HELLO
       handshake if the reported protocol is below `MinProtocol`, so the
       connection sits in `Identifying` until timeout and retries forever (no
       error surfaced). WPF's `HandleHelloMessage`
       (`MainWindow.Serial.cs:1313-1391`) only checks the device identity name and
       connects on any protocol version (with just a UI compatibility-banner
       warning via `IsEspProtocolCompatible`, `:1618-1664`). User impact: an
       old-firmware controller silently never connects on Linux/Avalonia, with no
       indication why.
     - **No rejected/phantom-port cooldown tracking** — WPF tracks
       wrong-identity ports (`_rejectedComPorts`/`MarkPortRejected`, `:686-697`)
       and unopenable ports (`_phantomComPorts`/`MarkPortPhantom`, `:448-459`)
       with separate cooldowns so it stops retrying them for a while. Avalonia's
       `TryNextCandidate` (`SerialConnectionService.cs:217-252`) has no such
       memory — every ~3-7s reconnect cycle re-tries every candidate including
       ones already known to be wrong, wasting cycles (and the identify timeout)
       if a second serial device is also plugged in.
     - **No PC idle/lock/suspend → controller SLEEP/WAKE on Avalonia** — WPF's
       `OnSessionSwitch`/`OnPowerModeChanged`/`UpdateControllerPowerStateFromPcActivity`
       (`MainWindow.Serial.cs:930-1078`) implement the README's documented
       "Auto sleep/wake" feature; zero equivalent exists anywhere under
       `App.Avalonia/`. This is a whole missing feature, same shape as the
       profile-system/tray-menu gaps above, not just a serial-layer nuance.
     - **No manual per-port picker in the Avalonia UI** — `Connect(string port)`
       exists (`SerialConnectionService.cs:129-136`, doc comment says "for a
       future UI") but nothing calls it; only Reconnect/Disconnect are wired.
       Minor — auto-detect covers the common case.
   - **Encoder/volume-feel audit done 2026-07-05**: WPF's `MainWindow.Encoder.cs`
     vs. Avalonia's `Services/ChannelRuntime.cs`. Both hosts share the exact same
     `Core/EncoderMath.cs` acceleration/EMA-smoothing formulas — genuinely at
     parity there. Two real gaps beyond that:
     - **No debounce/coalescing/reverse-guard on Avalonia's encoder path** — WPF's
       `QueueSmoothedEncoderDelta` (`MainWindow.Encoder.cs:63-156`) buffers rapid
       raw deltas and applies them on a 25ms timer (`EncoderApplyIntervalMs`,
       `MainWindow.xaml.cs:47`), suppressing isolated direction reversals within
       140ms unless confirmed twice. Avalonia's `HandleEncoder`
       (`ChannelRuntime.cs:99-136`) applies every raw `ENC` message immediately,
       no buffering. On Linux this matters more than it would on Windows: each
       write is a `wpctl set-volume` **process spawn**
       (`Platform.Linux/PipeWireAudioBackend.cs`), so a fast/bouncy encoder turn
       could spawn far more processes in a burst than WPF's batched COM writes
       ever would.
     - **Link groups gang unconditionally on Avalonia but only conditionally on
       WPF** — WPF's `ApplySmoothedEncoderDelta` (`:223-331`) only propagates a
       delta to linked channels inside the smoothing-enabled branch — with
       Volume Smoothing off, linked channels silently stop moving together.
       Avalonia's `HandleEncoder` (`:124-135`) always gangs linked channels
       regardless of the smoothing setting. Same settings file, different
       behavior across hosts — Avalonia's behavior looks like the intended one;
       WPF's looks like a pre-existing bug this port didn't reproduce.
   - **Volume overlay audit done 2026-07-05**: WPF's `VolumeOverlayWindow` vs.
     Avalonia's `VolumeOverlay`/`VolumeOverlayController`. Trigger completeness,
     the 6-way position setting, timeout/fade behavior all confirmed at parity.
     One real gap:
     - **Mute toggle has no distinct visual mode on Avalonia** — WPF shows a
       dedicated mute layout (large animated speaker icon, 18pt "Muted"/"Unmuted"
       text, volume bar hidden — `VolumeOverlayWindow.xaml.cs:56-74`). Avalonia
       reuses the normal volume-bar view and just relabels the percentage as
       "Muted" (`VolumeOverlay.cs:104-113`) — no icon, no mode switch, much less
       visually distinct at a glance.
     - Bonus finding (not a gap — Avalonia is more correct): Avalonia's overlay
       sets `ShowActivated = false` (`VolumeOverlay.cs:47`), never stealing
       keyboard focus. WPF sets no such flag, so every overlay pop-up likely
       steals focus from whatever the user is typing into — a pre-existing WPF
       bug this port already fixed, not something to backport.
   - **App startup/single-instance audit done 2026-07-05**: WPF's `App.xaml.cs`
     vs. Avalonia's `App.axaml.cs`/`Program.cs`/`Platform/WindowsGlue.cs`.
     Single-instance mutex, bring-to-front, and run-on-login (HKCU Run key) are
     all faithfully ported and confirmed at parity (correctly Windows-only on
     both hosts). Two real gaps:
     - **No global crash handler on Avalonia** — WPF wires
       `DispatcherUnhandledException`/`AppDomain.UnhandledException`/
       `TaskScheduler.UnobservedTaskException` (`App.xaml.cs:41-59`) to write a
       crash log (`WriteCrashLog`, `:71-101`) and show a friendly error dialog
       with a "copy details" option (`ShowCrashDialog`, `:103-136`). Zero
       equivalent anywhere under `App.Avalonia/` — an unhandled exception on
       Linux just kills the process with no crash log and no dialog, especially
       bad when launched from a desktop icon with no attached console.
     - **No `--safe` diagnostic launch flag on Avalonia** — WPF parses `--safe`
       (`MainWindow.xaml.cs:106-107,273-298`) to disable auto-connect/reconnect/
       audio writes for troubleshooting. No equivalent flag exists in
       `App.Avalonia/Program.cs` or `App.axaml.cs`.
     - Correction for the record: WPF is not wizard-less as earlier assumed —
       it has a lightweight in-window first-run tab (`MainWindow.xaml:2731`,
       `MainWindow.xaml.cs:279-288`); Avalonia's dedicated `FirstRunWizard`
       window is a more built-out evolution of the same idea, not an
       unprecedented new feature. Not a gap either direction.
   - **OLED/state-push audit done 2026-07-06**: WPF's `SendStateToDevice`/
     `SendAllChannelStatesToDevice`/`SendOledSettingsToDevice`/
     `SendAllChannelOledModesToDevice` (`MainWindow.Serial.cs:1410-1571`) vs.
     Avalonia's `Services/DeviceStateService.cs`. STATE/CHSTATE/OLEDCFG/DISPMODE
     framing, OLEDCFG-on-connect, per-channel DISPMODE, and the `MakeProtocolSafeLabel`
     handling are all at parity — Avalonia's per-line change-detection
     (`_lastChState`/`_lastState`) is if anything cleaner than WPF's
     send-all-on-`SendStateIfChanged` + `QueueFullStateSend` coalescing. The one
     WPF-only guard (`_controllerSleepRequested` suppresses pushes while the
     controller is asleep, `:1414`,`:1451`) is subsumed by the already-documented
     missing SLEEP/WAKE feature — nothing to add on the push layer itself.
   - **Logging/diagnostics audit done 2026-07-06**: WPF's `MainWindow.Ui.cs`
     (`Log`/`CleanupOldLogs`/`UpdateDiagnostics`) vs. Avalonia's
     `Services/LogService.cs` + `MainWindow.Debug.cs`. Theme handling
     (`ApplyTheme`, follow-system/light/dark) confirmed at parity. Real gaps:
     - **No log cleanup/rotation on Avalonia** — WPF's `CleanupOldLogs`
       (`MainWindow.Ui.cs:586-628`) deletes `dashboard-*.log` older than
       `LogRetentionDays` on every startup. Avalonia's `LogService`
       (`LogService.cs:16-22`) writes one `avalonia-{timestamp}.log` per launch
       and **never prunes** — logs accumulate unbounded, one file per app start.
       WPF's cleanup only globs `dashboard-*.log`, so it won't sweep Avalonia's
       files either; on a Linux/Avalonia-only box the logs dir grows forever.
     - **Diagnostics panel is much thinner on Avalonia** (minor) — WPF's
       `UpdateDiagnostics` (`MainWindow.Ui.cs:119-169`) drives an 8-field panel
       (connection state, COM port, last-heartbeat age, firmware, protocol vs.
       required, last ESP32 message, last state sent, and a colour-coded
       protocol-status line that warns on version/channel mismatch). Avalonia
       collapses this to a single `ConnectionStatusText` summary
       (`MainWindow.axaml.cs:159-165`); the rest of the detail is only in the
       debug console/log. The missing colour-coded protocol-mismatch warning
       overlaps the pre-2.24-firmware bug above (no user-facing indication when a
       controller is protocol-incompatible).
   - **Audio-session/target discovery audit done 2026-07-06**: WPF's
     `AutoRefreshAudioSessionsIfChanged` on a 2.5s timer
     (`AudioSessionRefreshCheckMs`, `MainWindow.xaml.cs:59`,`:1833-1857`) vs.
     Avalonia's `RefreshTargets` (`MainWindow.axaml.cs:176-190`). Gap:
     - **Assignable-target list doesn't auto-refresh on Avalonia** — Avalonia's
       50ms poll only calls `RefreshChannelStates` (already-assigned channels),
       never `RefreshTargets`, and there's no session-change timer. A
       newly-launched app doesn't appear in the target dropdown until the user
       clicks **Refresh**; WPF auto-detects it within ~2.5s. Already-assigned
       channels still track fine (`ChannelRuntime` resolves keys fresh) — the gap
       is purely new-app *discovery* in the picker.
   - **Debug/hardware-test audit done 2026-07-06**: WPF's hardware self-test
     panel vs. Avalonia's `MainWindow.Debug.cs`. Avalonia's Debug tab (live TX/RX
     console + Ping/Show-ident/Scan-I2C/Test-display quick buttons + raw send) is
     at parity with WPF's raw console. Gap:
     - **No hardware self-test / verification panel on Avalonia** — WPF tracks
       `_hardwareEncoderCounts`/`_hardwareButtonSeen` (`MainWindow.xaml.cs:109-110`)
       and renders a per-channel "Channel N: encoder count X, button seen yes/no"
       checklist via `UpdateHardwareTestSummary` (`:4387-4400`), with a Reset
       button (`:4403-4407`) and dedicated Sleep/Wake test buttons + status
       readout (`:4421-4432`, `MainWindow.Serial.cs:1262-1272`). This lets a user
       confirm all six encoders and buttons physically register. Avalonia has no
       structured self-test — SLEEP/WAKE/TEST_DISPLAY are reachable only by typing
       raw commands in the debug send box, with no per-channel pass/fail readout.
       Same shape as the other feature gaps, not a serial-nuance.
     - Minor, not itemised above: WPF also has Copy-debug-console,
       Copy-log-folder-path, Open-current-log-file, and Save-debug-snapshot
       buttons; Avalonia covers the diagnostics-export need with its
       `ExportDiagnostics` zip but lacks the individual copy/open-log helpers.
3. **Retire WPF** — remove the WPF host and its Windows-only helpers, collapse the
   solution, then add a signed installer (Windows: Inno Setup; Linux: `.deb`/AppImage;
   macOS: notarized `.dmg`) and a CI build matrix.
