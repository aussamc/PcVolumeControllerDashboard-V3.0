# Avalonia parity — prioritized fix backlog

Consolidated from the WPF-vs-Avalonia parity audit (five passes, 2026-07-04 →
2026-07-06; findings recorded inline in `CLAUDE.md` roadmap item 2). This is the
work remaining to reach functional parity before retiring the WPF host
(roadmap item 3).

Priority tiers:

- **P0** — real bugs / silent failures affecting core function. Fix first.
- **P1** — documented/expected features missing; parity blockers for retiring WPF.
- **P2** — behavioral fidelity & quality gaps.
- **P3** — minor / nice-to-have.

Effort: **S** ≈ <½ day, **M** ≈ ½–2 days, **L** ≈ multi-day.

---

## Progress (updated 2026-07-13 — shipped as v3.14)

- **Merged to `main` (v3.12):** B1 (+Q6 protocol-mismatch warning), B2, F3, F4, and the
  v3.12 batch — **Q1** (encoder debounce/coalescing/reverse-guard), **Q3** (rejected/phantom
  port cooldowns; the same PR also fixed an incompatible-controller status *flicker* —
  the too-old-firmware port was re-opened/reset every ~3s), **Q5** (overlay mute mode),
  **Q6** (connected-but-channel-count-mismatch warning), **F1** (auto sleep/wake,
  incl. suspend/resume verified on Windows). **R1** confirmed — Avalonia's link-gang
  behavior is the correct one; nothing to port.
- **Merged to `main` (v3.13, PR #59):** the **Debug-tab batch per D2** — **Q4** (hardware
  self-test section), the fuller **Q6** diagnostics readout, and **N3** (copy-console /
  copy-log-path / open-log-file helpers) — folded into the existing Debug tab, gated by a
  new `AdvancedDebugFeatures` setting (default off) with a `--debug` startup flag that
  force-shows it. The diagnostics-export entry point stays on the always-visible Setup
  tab; the v3.12 mismatch warning stays on the main status line.
- **Merged to `main` (v3.14, PR #62):** **F6** — the cross-platform desktop-notification
  layer that finally makes the `TrayNotificationsEnabled` toggle functional: connect /
  disconnect / started-minimised notifications via a Windows WinRT toast, Linux
  `notify-send`, and a macOS no-op (deferred), behind a Core `INotificationService` seam.
  **This clears the last P1 blocker** — no parity blockers remain. (The WPF host's
  reconnect-failed call site has no distinct event on the Avalonia connection service and
  is not surfaced; connect/disconnect/started-minimised cover the UX.)
- **Descoped (not a gap):** **F2** named profiles — descoped from the port
  (`chore(avalonia): descope named profiles from the port`), along with output-device
  cycling.
- **Still open — quality/polish (no blockers left):** **Q2** (target auto-refresh);
  **P3** — N1 (`--safe`), N2 (per-port picker).
- **Next up:** the remaining P2/P3 polish, then WPF retirement (roadmap item 3).

> The prioritized tables below are the original backlog catalog; the Progress block
> above is the status of record. Most P0/P2 items plus F1/F3/F4 are merged (v3.12), the
> Q4/Q6/N3 Debug-tab batch is merged (v3.13), and F6 is merged (v3.14) — clearing the
> last P1 blocker.

---

## ✅ Decisions

**D1 — Pre-2.24-firmware handshake policy. RESOLVED (2026-07-06): keep it strict.**
Per **standing rule #5** ("identity handshake is strict"), Avalonia's rejection of a
below-`MinProtocol` HELLO is **correct and stays**. The bug is only the *silent*
failure — the connection loops in `Identifying` forever with no user-facing reason.
Fix = **surface it**: a clear warning in the UI + a log entry naming the reported vs.
required protocol, and stop presenting it as an in-progress identify with no verdict.
Do **not** adopt WPF's warn-and-connect. This reshapes B1 (below) from a
policy-change into a diagnostics/UX fix, and merges it tightly with Q6.

**D2 — Q4 + Q6 live in the Debug tab, gated by an `AdvancedDebugFeatures` setting.
DECIDED 2026-07-08.** Neither the hardware self-test (Q4) nor the fuller diagnostics
readout (Q6) warrants a new top-level panel — both are developer/troubleshooting
surfaces. Fold them into the **existing Debug tab** (live console + raw send +
Ping/Scan-I2C/Test-display). A new **`AdvancedDebugFeatures`** setting (**default off**)
shows/hides the **entire** Debug tab; a **`--debug`** startup flag force-shows it for
the session regardless of the setting (sibling to N1's `--safe`). **Constraint — the
actionable diagnostics must stay reachable outside the toggle:** the protocol/channel-
mismatch **warnings already render on the main connection status line** (v3.12), and the
diagnostics-**export** entry point must live somewhere always-visible (Settings/About),
**not** only inside the hideable Debug tab. This shrinks Q4 and Q6 from "new panel" to
"Debug-tab section."

---

## P0 — bugs / silent failures

| # | Item | Why | Effort | Key files |
|---|---|---|---|---|
| B1 | **Pre-2.24 firmware fails silently** (per D1: stay strict, surface it) | `IsValidIdentity` correctly rejects HELLO below `MinProtocol`, but the connection then loops in `Identifying` forever with no error. Keep the rejection; add a distinct rejected state → **UI warning + log line** naming reported vs. required protocol, and stop the silent identify loop. | M | `Core/SerialProtocol.cs:107-112`; `Services/SerialConnectionService.cs` (needs a "rejected/incompatible" state distinct from `Identifying`); log via `LogService` |
| B2 | **No global crash handler** | Unhandled exception just kills the process — no crash log, no dialog. Especially bad launched from a desktop icon with no console. WPF writes a crash log + friendly dialog. | M | new code in `App.axaml.cs`/`Program.cs`; cf. WPF `App.xaml.cs:41-59`,`:71-136` |

*B1 per resolved D1: **keep strict rejection**; the fix is purely to surface the
failure (rejected state + UI warning + log) instead of retrying invisibly. Implement
together with Q6 (the colour-coded protocol-mismatch diagnostics line is the UI half
of this).*

---

## P1 — missing features (parity blockers)

| # | Item | Why | Effort | Key files |
|---|---|---|---|---|
| F1 | **Auto sleep/wake** (PC idle/lock/suspend → controller SLEEP/WAKE) | Whole documented feature ("Auto sleep/wake" in README); zero equivalent in Avalonia. Also unblocks the `_controllerSleepRequested` push-suppression parity. | L | new service under `App.Avalonia/`; cf. WPF `MainWindow.Serial.cs:930-1078` |
| F2 | **Profile system** (create/rename/duplicate/delete/switch/cycle-next) | Full multi-profile support in WPF incl. tray submenu + global hotkey. Core already has `ProfileEntry`/`Profiles`/`ActiveProfileName`, so it's UI-only work. | L | `App.Avalonia/` UI + `MainWindow.Hotkeys.cs` (cycle descoped); Core already backs it; cf. WPF `MainWindow.xaml.cs:611-932`,`MainWindow.Tray.cs:70-99` |
| F3 | **Log cleanup/rotation** | `LogService` writes one `avalonia-{timestamp}.log` per launch and never prunes → unbounded growth. Easy win. | S | `Services/LogService.cs:16-22`; cf. WPF `MainWindow.Ui.cs:586-628` |
| F4 | **Tray menu — missing actions** | Avalonia tray has only Show/Exit; WPF has Connect/Disconnect/Reconnect/Open Log Folder/Exit + double-click-restore. | M | `App.axaml:22-28`; cf. WPF `MainWindow.Tray.cs:39-63` |
| F5 | **Inert "tray notifications" checkbox** | Setting exists but nothing reads it. **Resolution (2026-07-06): not removed — wired by F6** (cross-platform notification layer greenlit). The checkbox stays; F6 makes it real. | — | subsumed by F6 |
| F6 | ✅ **DONE** (v3.14, PR #62) — **Cross-platform desktop notification layer** | Makes the `TrayNotificationsEnabled` toggle functional. Core `INotificationService` seam + `NullNotificationService`; Windows = WinRT **toast** via CommunityToolkit (in the host, `#if WINDOWS`); Linux = `notify-send` (`Platform.Linux`); macOS = no-op. Coordinator gates on the setting and fires connect / disconnect (edge-tracked) / started-minimised. Reconnect-failed not wired (no distinct Avalonia event). Windows TFM bumped to `net10.0-windows10.0.17763.0` for the WinRT API. | M–L | `Core/INotificationService.cs`, `App.Avalonia/Services/NotificationService.cs`, `App.Avalonia/Platform/WindowsToastNotificationService.cs`, `Platform.Linux/LinuxNotificationService.cs`; cf. WPF `MainWindow.Tray.cs:116-135` |

---

## P2 — behavioral fidelity & quality

| # | Item | Why | Effort | Key files |
|---|---|---|---|---|
| Q1 | **Encoder debounce/coalescing/reverse-guard** | Avalonia applies every raw `ENC` immediately. On Linux each write is a `wpctl` **process spawn**, so a bouncy turn spawns a burst of processes. WPF buffers on a 25ms timer + suppresses isolated reversals. | M | `Services/ChannelRuntime.cs:99-136`; cf. WPF `MainWindow.Encoder.cs:63-156` |
| Q2 | **Assignable-target list doesn't auto-refresh** | New app doesn't appear in the picker until manual Refresh; WPF auto-detects in ~2.5s. (Assigned channels already track fine.) | S | `MainWindow.axaml.cs:176-190`; cf. WPF `MainWindow.xaml.cs:1833-1857` |
| Q3 | **Rejected/phantom-port cooldown tracking** | Avalonia re-tries every candidate each reconnect cycle, incl. known-wrong ports — wastes cycles + identify timeouts when a 2nd serial device is present. | M | `Services/SerialConnectionService.cs:217-252`; cf. WPF `:686-697`,`:448-459` |
| Q4 | ✅ **DONE** (PR #59) — **Hardware self-test** Debug-tab section (per D2) | Per-channel "encoder count X, button seen yes/no" checklist + Reset + Sleep/Wake test buttons to verify all 6 encoders/buttons. Lives in the Debug tab, gated by `AdvancedDebugFeatures` (default off; `--debug` force-shows). Tally in Core (`HardwareSelfTest`, unit-tested). | S–M | `MainWindow.Debug.cs`, `Core/HardwareSelfTest.cs`; cf. WPF `MainWindow.xaml.cs:109-110`,`:4375-4432` |
| Q5 | **Overlay mute — distinct visual mode** | WPF shows a dedicated mute layout (speaker icon, "Muted" text, bar hidden); Avalonia just relabels the percentage. | S | `VolumeOverlay.cs:104-113`; cf. WPF `VolumeOverlayWindow.xaml.cs:56-74` |
| Q6 | ✅ **DONE** — warning line (v3.12) + fuller readout (PR #59) | The colour-coded protocol/channel-mismatch **warning shipped in v3.12** on the main connection status line. The fuller readout (connection, COM port, last-heartbeat age, protocol vs required, reported-vs-expected channels, last ESP32 msg, last state sent) now lives in the Debug tab under `AdvancedDebugFeatures`. | S | `MainWindow.Debug.cs`, `SerialConnectionService.PortName`; cf. WPF `MainWindow.Ui.cs:119-169` |

---

## P3 — minor / nice-to-have

| # | Item | Why | Effort | Key files |
|---|---|---|---|---|
| N1 | **`--safe` diagnostic launch flag** | Disable auto-connect/reconnect/audio writes for troubleshooting. | S | `Program.cs`/`App.axaml.cs`; cf. WPF `MainWindow.xaml.cs:106-107,273-298` |
| N2 | **Manual per-port picker in UI** | `Connect(string port)` exists but nothing calls it; auto-detect covers the common case. | S | `Services/SerialConnectionService.cs:129-136` |
| N3 | ✅ **DONE** (PR #59) — **Copy/open-log helper buttons** | Debug tab now has Copy-debug-console, Copy-log-folder-path, Open-current-log-file (cross-platform default handler). Save-debug-snapshot stays covered by the always-visible `ExportDiagnostics` zip on the Setup tab. | S | `MainWindow.Debug.cs` |

---

## Reconcile / verify (not a fill-the-gap item)

- **R1 — Link-group ganging with smoothing OFF.** WPF only propagates a delta to
  linked channels inside the smoothing-enabled branch (`MainWindow.Encoder.cs:223-331`);
  Avalonia always gangs (`ChannelRuntime.cs:124-135`). **Avalonia's behavior looks
  correct**; WPF looks like a pre-existing bug. Action: confirm Avalonia is intended,
  then this is done — nothing to port. (Don't accidentally "fix" Avalonia toward WPF.)
- Overlay focus-steal and single-instance/run-on-login were audited and are already
  correct on Avalonia — no action.

---

## Suggested sequencing

1. **B1 + Q6** together (D1 resolved — stay strict; surface the failure + the
   diagnostics line that explains it). **B2** alongside — both are high-value
   safety nets.
2. **F3** (log cleanup — trivial), then **F4** (tray menu actions). **F6** (the
   notification layer that wires the F5 checkbox) is greenlit — schedule as its
   own PR when convenient; it's independent of the big features below.
3. **Q1** before broad Linux dogfooding (process-spawn storms are a real-hardware
   footgun), then **Q2/Q3** (serial + discovery quality).
4. **F1** and **F2** — the two large features; schedule as their own PRs.
5. **Q4/Q5** (both done — Q5 in v3.12, Q4 in the PR #59 Debug-tab batch alongside Q6/N3),
   then the remaining **P3** items (N1 `--safe`, N2 per-port picker), then confirm **R1**
   (done) and move to WPF retirement.

Each item is a small, build-green PR per standing rule #1 (`feat/v3.x-…` for features,
`fix/v3.x.y-…` for bugs).
