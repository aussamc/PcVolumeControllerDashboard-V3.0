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
was a faithful code-behind port first, not an MVVM rewrite. The original WPF host has
been **retired** (removed in the v3.x WPF-retirement PR) now that the Avalonia host
reached feature parity — the Avalonia host is the single UI going forward.

## Repository layout

| Path | TFM | Purpose |
|---|---|---|
| `Core/` (`PcVolumeControllerDashboard.Core.csproj`) | `net10.0` | Platform-agnostic domain: serial layer + protocol parser, settings repository/POCOs + migrations, OLED renderer, encoder math, audio & hotkey seams. **No WPF/Windows/Avalonia refs.** |
| `App.Avalonia/` (`App.Avalonia.csproj`) | `net10.0;net10.0-windows10.0.17763.0` | The cross-platform Avalonia host — **the single UI**. References Core; references `Platform.Windows` only on the `-windows` TFM, `Platform.Linux` only on the plain `net10.0` TFM. The Windows TFM carries an OS version (Win10 1809) so the host can call the WinRT toast API for F6 desktop notifications; nothing else in the solution needs it. |
| `Platform.Windows/` | `net10.0-windows` | Windows-only implementations behind Core seams: WASAPI + VoiceMeeter audio backends. |
| `Platform.Linux/` | `net10.0` | Linux audio backend behind the same seam: `PipeWireAudioBackend` (shells out to `pw-dump`/`wpctl`). Also referenced on macOS builds (shared TFM), but `AudioBackendFactory` only instantiates it at runtime on Linux. |
| `tests/` (`PcVolumeControllerDashboard.Tests.csproj`) | `net10.0-windows` | xUnit + FluentAssertions. Tests Core (pure logic) + Windows platform pieces. |
| `Computer_Volume_Controller_v2.31/` | Arduino | Current ESP32 firmware (`.ino`) — **the only firmware kept in the repo**. Older versions (v2.24–v2.30) were removed; recover one from git history if ever needed. On a firmware bump, delete the superseded folder in the same PR. |
| `PcVolumeControllerDashboard.slnx` | | Solution file (Core + App.Avalonia + Platform.Windows/Linux + tests). |

Namespaces: Core = `PcVolumeControllerDashboard.Core`; Avalonia host =
`PcVolumeControllerDashboard.App`. (The retired WPF host used the bare
`PcVolumeControllerDashboard` namespace.)

## Build, run, test

Work from the repo root. The Avalonia app **multi-targets**, so build the TFM for the
current OS explicitly:

```bash
# Build the Avalonia app
dotnet build App.Avalonia/App.Avalonia.csproj -f net10.0-windows10.0.17763.0   # Windows
dotnet build App.Avalonia/App.Avalonia.csproj -f net10.0                       # Linux/macOS

# Run it
dotnet run --project App.Avalonia/App.Avalonia.csproj -f net10.0-windows10.0.17763.0   # Windows
dotnet run --project App.Avalonia/App.Avalonia.csproj -f net10.0                       # Linux/macOS

# Tests (target the test project directly to avoid rebuilding the running app)
dotnet test tests/PcVolumeControllerDashboard.Tests.csproj
```

Verification bar for any change:

- **Both** Avalonia TFMs (`net10.0` and `net10.0-windows10.0.17763.0`) build at **0 warnings / 0 errors**.
- The full test suite passes.
- The Windows build launches.

Notes:

- The audio backend is chosen by `AudioBackendFactory`: per-TFM via `#if WINDOWS`
  (WASAPI/VoiceMeeter on Windows), then a runtime `RuntimeInformation.IsOSPlatform`
  check on the shared non-Windows TFM (PipeWire on Linux; Core's `NullAudioBackend`
  on macOS, which has no TFM suffix of its own to branch on at compile time). Global
  hotkeys use the simpler per-TFM pattern (`WindowsGlobalHotkeyService` vs
  `NullGlobalHotkeyService`) since they're Windows-only for now.
- Multi-target incremental builds can leave a **stale `net10.0-windows10.0.17763.0`
  output** — if a `--no-build` run seems to execute old code, rebuild that TFM
  explicitly (or clean `App.Avalonia/bin` + `obj`). Building the `.csproj` directly
  emits to `bin/Debug/<tfm>/`; solution builds emit to `bin/x64/Debug/<tfm>/`.
- The test suite **never touches the live user config** (v3.23.4): a module initializer
  in `tests/TestConfigDirectory.cs` points `SettingsRepository.ConfigDirectoryOverride`
  at a temp dir before any test runs. Before that fix, a `dotnet test` run overwrote the
  developer's own `settings.json` with a fixture — which looked like an update losing the
  user's settings. Any new test that calls the real `SettingsRepository.Save`/`Load` is
  covered automatically; don't remove that initializer.
- To point a *running* dashboard at a throwaway config dir, set
  `PCVOLUMECONTROLLER_CONFIG_DIR` — settings, backups, logs, and diagnostics all follow it.

## Standing rules

1. **PRs only** — never commit directly to `main`. Branch `feat/v3.x-...` (features)
   or `fix/v3.x.y-...` (bug fixes). Work in small, build-green PRs.
2. **Versioning** — always **three components, `MAJOR.FEATURE.FIX`**, never two.
   - `MAJOR` (the leading `3`) — reserved for a whole-product generation.
   - `FEATURE` (middle) — a user-facing feature or change batch. Bumping it **resets
     `FIX` to 0**: `3.23.4 → 3.24.0`.
   - `FIX` (last) — a bug fix or other minor change that isn't its own release:
     `3.23.3 → 3.23.4`.

   Never write a bare `3.24` — it is `3.24.0` everywhere (code, csproj, tags, docs,
   release-notes filenames). On a bump, update **all** of: `AppInfo.Version` in the
   Avalonia host, `<Version>/<AssemblyVersion>/<FileVersion>` in the Avalonia csproj,
   the README + `VERSION_COMPATIBILITY.md` tables, and `RELEASE_NOTES_vX.Y.Z.md`
   (remove the superseded notes file). Infra-only PRs don't bump.
3. **The WPF host has been retired** (removed in the v3.x WPF-retirement PR). The
   Avalonia host is the single UI — there is no second host to keep in sync.
4. **Serial channel indices are always 0-based (0–5) on the wire; always 1-based
   (1–6) in the UI.** Never change this.
5. **Identity handshake is strict** — `HELLO` must match both the `PC_VOLUME_CONTROLLER`
   name and the exact protocol string.
6. **Firmware version** — only bump the firmware version / rename its folder when the
   ESP32 code actually changes and a reflash is required. On a bump, **delete the
   superseded firmware folder** (git history is the archive) and update the firmware
   ladder + matrix in `VERSION_COMPATIBILITY.md` and the README table: the
   *minimum protocol* column only moves if the wire protocol itself changes, but the
   *matching firmware* column must always point at the new firmware for the dashboard
   version shipping alongside it.
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

## Serial protocol (firmware v2.31; wire format unchanged since v2.24)

- Handshake: ESP32 → `HELLO,PC_VOLUME_CONTROLLER,<protocol>,<channels>,<chipId>`
  (5th field = chip ID for controller pairing; absent = pre-v2.25, still accepted).
- PC → ESP32: `STATE` / `CHSTATE` (per-channel OLED data) / `OLEDCFG` / `DISPMODE` /
  `PING` / `SLEEP` / `WAKE`.
- ESP32 → PC: `ENC,<ch>,<delta>` / `BTN_SHORT|BTN_LONG|BTN_DOUBLE,<ch>` / `PONG`.
- The connection owns a 1s keepalive (`PING`) + inbound-liveness drop + auto-reconnect;
  opening the port resets the ESP32 (it reboots before `HELLO`).

## Key constants

- Dashboard version: **3.24.1** (Avalonia host). Expected channels: **6**.
- **Two firmware numbers, don't conflate them** (same split as the compatibility
  tables — see `VERSION_COMPATIBILITY.md`):
  - *Minimum protocol* — **2.24** (`SerialConnectionService.MinProtocol`). The
    handshake floor: anything below is rejected outright. Only moves if the wire
    format itself changes, which it hasn't since v2.24.
  - *Matching firmware* — **v2.31** (`Computer_Volume_Controller_v2.31/`). What
    3.24.x was built and tested against. Older-but-accepted firmware connects fine
    and loses features silently, so this is the number to quote to a user.
- **v2.31 anti-burn-in is mirrored in Core**, so it's the one firmware detail that
  constrains host code: the drawing origin walks a 3×3 grid of 0–2px x/y offsets, one
  adjacent step every 30s, **clipped never wrapped** (v2.28–v2.30 used a vertical-only,
  wrapping hardware `SETDISPLAYOFFSET` shift). Every screen keeps base content within
  x0..125/y0..61 so a full jitter never clips a lit pixel, and `OledRenderer` reproduces
  it pixel-for-pixel (`SetAntiBurnJitter`/`AntiBurnJitterForStep`) — change one side and
  the preview stops matching the panel. Per-firmware history for everything else lives
  in the `VERSION_COMPATIBILITY.md` ladder, not here.
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
WPF-host build break and a crash-on-close stack overflow). Since then the **Q4/Q6/N3
Debug-tab batch shipped (v3.13)**, **F6 (the cross-platform desktop-notification layer)
shipped (v3.14)**, **Q2 (target auto-refresh) shipped (v3.14.1)**, and **N1 (`--safe`) +
N2 (per-port picker) shipped (v3.14.2)** — with B1/B2/F3/F4 already on `main`, **the full
parity backlog (P0–P3) is cleared**; only WPF retirement (roadmap item 3) remains. Named
profiles (F2) were **descoped**. `PARITY_FIX_BACKLOG.md` is the live tracker.

### Post-parity features

With parity cleared and WPF retired, new feature work is tracked in
**`FEATURE_BACKLOG.md`** (same conventions as the parity tracker: PRs only, small and
build-green; a user-facing batch bumps `3.x`).

- **v3.15 — Volume overlay enhancements** *(planned, one PR
  `feat/v3.15-overlay-enhancements`, bump 3.14.2 → 3.15):* **O1** transparency slider
  (`OverlayOpacity`), **O2** size/scale slider (`OverlayScale`, whole-overlay Viewbox
  scale), **O4** show-on-all-screens (`OverlayAllScreens`, multi-window mirror). **O3**
  screen position (the 6 Top/Bottom × Left/Center/Right corners the user asked for) is
  **already shipped** — no code change, just fix the stale "ported in a later PR" helper
  text on the overlay card. See `FEATURE_BACKLOG.md` for per-item design + the
  version-bump chore list.

- **v3.16–v3.19 — UX & lifecycle batch** *(shipped)*: an 18-item request spanning
  first-run defaults (ch2–ch6 start **unassigned**, only ch1 = Master; ch4 label
  "Voice Chat"; no pool-by-default), OLED-tab polish + anti-burn-in preview/clip fixes,
  Setup-tab declutter + Debug-tab move, a **two-stream (Quick/Advanced) wizard overhaul**,
  and a **download + one-click auto-updater**. Shipped as four bumps (3.16→3.19). The
  18th item — an opt-in **Share Diagnostics** crash reporter, originally slated as v3.20 —
  has been **deferred out of the timeline** (unscheduled) pending the **upload-endpoint
  decision**; the Windows installer also remains **unsigned** (SmartScreen friction for
  auto-apply). Full per-item design + the deferred Share Diagnostics plan in
  `FEATURE_BACKLOG.md`.

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
   confirmed (Avalonia correct). **Since v3.12:** the **Q4/Q6/N3 Debug-tab batch** shipped
   (v3.13), **F6** (cross-platform notification layer) shipped (v3.14), **Q2** (target
   auto-refresh) shipped (v3.14.1), and **N1 (`--safe`) + N2 (per-port picker)** shipped
   (v3.14.2) — clearing the last P1 blocker and the entire P2/P3 backlog. **Nothing open;
   only WPF retirement (roadmap item 3) remains.** See `PARITY_FIX_BACKLOG.md` for the
   live tracker. The detailed findings below are retained as the audit record:
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
     - **No global crash handler on Avalonia** — **RESOLVED (parity B2):** the
       Avalonia host now has `App.Avalonia/CrashHandler.cs` (wired at
       `App.axaml.cs`), which hooks `Dispatcher.UIThread.UnhandledException` /
       `AppDomain.UnhandledException` / `TaskScheduler.UnobservedTaskException`,
       writes a `crash-<timestamp>.log`, and shows a friendly error dialog with a
       "copy details" option — parity with WPF's `App.xaml.cs` handler. (Original
       finding: WPF wired these and Avalonia had zero equivalent, so an unhandled
       exception just killed the process with no crash log and no dialog.)
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
3. **Retire WPF** — **removal done** (the root WPF host project + its source and the
   WPF-only `UpdateCheckerTests` were deleted, the solution collapsed to Core +
   App.Avalonia + Platform.Windows/Linux + tests, and the release workflow repointed to
   publish the Avalonia host's self-contained win-x64 exe). **Installers:** a **Windows
   Inno Setup installer** is now built by `.github/workflows/build-installer.yml`
   (`installers/windows/PcVolumeController.iss`) — validated as a workflow artifact on
   PRs and attached to the `v<version>` release on push to `main`, alongside the portable
   single-file exe. It's currently **unsigned**. **Linux packaging** is built by
   `.github/workflows/build-linux-packages.yml` via `installers/linux/build.sh` — a
   Debian **`.deb`** and a universal **AppImage** from the self-contained linux-x64
   publish (PR artifact + release attach, same pattern as Windows). A **multi-OS CI
   matrix** (`.github/workflows/ci.yml`) builds the Avalonia host on Windows/Linux/macOS
   on every push and PR, and runs the test suite on the Windows leg (the test project is
   `net10.0-windows`-only). **Still to do:** code-sign the Windows installer and a macOS
   notarized `.dmg` (both need signing credentials — a Windows cert / an Apple Developer
   ID). (Linux runtime install/launch is best verified on the CachyOS box; CI only
   validates that the packages build.)
