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

## ✅ Decisions

**D1 — Pre-2.24-firmware handshake policy. RESOLVED (2026-07-06): keep it strict.**
Per **standing rule #5** ("identity handshake is strict"), Avalonia's rejection of a
below-`MinProtocol` HELLO is **correct and stays**. The bug is only the *silent*
failure — the connection loops in `Identifying` forever with no user-facing reason.
Fix = **surface it**: a clear warning in the UI + a log entry naming the reported vs.
required protocol, and stop presenting it as an in-progress identify with no verdict.
Do **not** adopt WPF's warn-and-connect. This reshapes B1 (below) from a
policy-change into a diagnostics/UX fix, and merges it tightly with Q6.

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
| F5 | **Dead "tray notifications" checkbox** | Setting exists but nothing reads it. Either wire it to real notifications or remove the control. (Pairs with F4.) | S | `MainWindow.axaml.cs:338,395` |

---

## P2 — behavioral fidelity & quality

| # | Item | Why | Effort | Key files |
|---|---|---|---|---|
| Q1 | **Encoder debounce/coalescing/reverse-guard** | Avalonia applies every raw `ENC` immediately. On Linux each write is a `wpctl` **process spawn**, so a bouncy turn spawns a burst of processes. WPF buffers on a 25ms timer + suppresses isolated reversals. | M | `Services/ChannelRuntime.cs:99-136`; cf. WPF `MainWindow.Encoder.cs:63-156` |
| Q2 | **Assignable-target list doesn't auto-refresh** | New app doesn't appear in the picker until manual Refresh; WPF auto-detects in ~2.5s. (Assigned channels already track fine.) | S | `MainWindow.axaml.cs:176-190`; cf. WPF `MainWindow.xaml.cs:1833-1857` |
| Q3 | **Rejected/phantom-port cooldown tracking** | Avalonia re-tries every candidate each reconnect cycle, incl. known-wrong ports — wastes cycles + identify timeouts when a 2nd serial device is present. | M | `Services/SerialConnectionService.cs:217-252`; cf. WPF `:686-697`,`:448-459` |
| Q4 | **Hardware self-test panel** | WPF's per-channel "encoder count X, button seen yes/no" checklist + Reset + Sleep/Wake test buttons lets a user verify all 6 encoders/buttons. Avalonia has only a raw console. | M | new UI on Debug tab; cf. WPF `MainWindow.xaml.cs:109-110`,`:4375-4432` |
| Q5 | **Overlay mute — distinct visual mode** | WPF shows a dedicated mute layout (speaker icon, "Muted" text, bar hidden); Avalonia just relabels the percentage. | S | `VolumeOverlay.cs:104-113`; cf. WPF `VolumeOverlayWindow.xaml.cs:56-74` |
| Q6 | **Diagnostics panel detail + protocol-mismatch warning** | Avalonia shows one summary line vs WPF's 8-field panel with a colour-coded protocol/channel-mismatch warning. The warning pairs with B1 (surface *why* an incompatible controller won't connect). | M | `MainWindow.axaml.cs:159-165`; cf. WPF `MainWindow.Ui.cs:119-169` |

---

## P3 — minor / nice-to-have

| # | Item | Why | Effort | Key files |
|---|---|---|---|---|
| N1 | **`--safe` diagnostic launch flag** | Disable auto-connect/reconnect/audio writes for troubleshooting. | S | `Program.cs`/`App.axaml.cs`; cf. WPF `MainWindow.xaml.cs:106-107,273-298` |
| N2 | **Manual per-port picker in UI** | `Connect(string port)` exists but nothing calls it; auto-detect covers the common case. | S | `Services/SerialConnectionService.cs:129-136` |
| N3 | **Copy/open-log helper buttons** | WPF has Copy-debug-console, Copy-log-folder-path, Open-current-log-file, Save-debug-snapshot. Avalonia's `ExportDiagnostics` zip covers the core need. | S | Debug/Settings tab UI |

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
2. **F3** (log cleanup — trivial), then **F5**/**F4** (tray cluster).
3. **Q1** before broad Linux dogfooding (process-spawn storms are a real-hardware
   footgun), then **Q2/Q3** (serial + discovery quality).
4. **F1** and **F2** — the two large features; schedule as their own PRs.
5. **Q4/Q5**, then the **P3** batch, then confirm **R1** and move to WPF retirement.

Each item is a small, build-green PR per standing rule #1 (`feat/v3.x-…` for features,
`fix/v3.x.y-…` for bugs).
