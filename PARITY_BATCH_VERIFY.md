# Parity batch — verify & merge checklist

Five Avalonia parity branches produced in one parallel pass (2026-07-07). Each is
**build-green on `net10.0`** and code-reviewed clean, but **none can merge until the
Windows testing regime below is cleared** — `net10.0-windows` and `dotnet test` cannot
run on Linux (the test project targets `net10.0-windows` only).

This doc is the handoff for running that verification on a Windows box.

## Branches / PRs in this batch

| PR | Item | Branch | Kind | Files touched |
|---|---|---|---|---|
| #48 | Q1 — encoder debounce/coalescing/reverse-guard | `fix/v3.11.1-encoder-debounce` | patch | `Services/ChannelRuntime.cs` |
| #49 | Q5 — distinct overlay mute visual mode | `fix/v3.11.x-overlay-mute-mode` | patch | `VolumeOverlay.cs`, `Services/ChannelRuntime.cs`, `Services/GlobalHotkeyManager.cs` |
| #50 | Q6 — connected-but-channel-mismatch warning | `fix/v3.11.x-pre224-firmware-diagnostics` | patch | `Services/SerialConnectionService.cs`, `MainWindow.axaml.cs` |
| #51 | Q3 — rejected/phantom-port reconnect cooldowns | `fix/v3.11.x-port-cooldown-tracking` | patch | `Services/SerialConnectionService.cs` |
| #52 | F1 — PC idle/lock/suspend → controller SLEEP/WAKE | `feat/v3.x-auto-sleep-wake` | feature | Core (`IPcActivityMonitor`, `NullPcActivityMonitor`), `Platform.Windows/WindowsPcActivityMonitor.cs`, `App.Avalonia/Services/{SleepWakeService,DeviceStateService}.cs`, `App.axaml.cs` |

> Note: B1 + the Q6 protocol-mismatch warning line were already merged on `main`
> (PR #43). PR #50 only adds the remaining channel-count-mismatch diagnostic.

---

# Windows testing regime

A repeatable procedure for the Windows box. Run it **once per branch/PR**, in the
merge order at the bottom. Record results in the PR using the template in §4.

## 1. Environment prerequisites

Confirm all of these before starting — a missing prerequisite invalidates the run:

- Windows 10 or 11.
- **.NET 10 SDK** installed (`dotnet --version` reports a 10.x SDK).
- The physical **PC Volume Controller** (ESP32-S3) connected over USB, firmware **≥ v2.25**, enumerating as a COM port.
- At least one running app producing an **audio session** (e.g. a browser or media player actively playing), plus a working **master** device and a **mic** input — so channels have assignable targets.
- **For Q3 (#51) only:** a **second USB serial device** (any — an Arduino, a USB-TTL adapter, another dev board) plugged into a different COM port, to prove the wrong port gets cooled down.
- **For F1 (#52):** the ability to **lock the session** (Win+L), let the machine **idle**, and ideally **sleep/suspend + resume** the machine.
- Optional but ideal for Q6 (#50): an **old-firmware controller** (protocol < 2.24) to confirm the merged B1 path + the new channel-count path both surface correctly and neither regressed.

## 2. Baseline (run once, before any branch)

Establish that `main` is healthy on this box so later failures are attributable to a branch:

```bash
git fetch origin
git checkout main
dotnet build App.Avalonia/App.Avalonia.csproj -f net10.0-windows   # expect 0/0
dotnet test  tests/PcVolumeControllerDashboard.Tests.csproj        # expect all pass
dotnet build PcVolumeControllerDashboard.csproj                    # WPF host builds
dotnet run   --project App.Avalonia/App.Avalonia.csproj -f net10.0-windows
```

Confirm the app launches, auto-detects the controller, handshakes (protocol shown),
and pushes OLED state. This is your "known good" reference. **If baseline fails, stop
and fix the box/environment before verifying any branch.**

## 3. Per-branch procedure

For each PR branch, in merge order:

1. **Checkout:** `git fetch origin && git checkout <branch>`
2. **Build gate (all four must pass):**
   ```bash
   dotnet build App.Avalonia/App.Avalonia.csproj -f net10.0-windows   # MUST be 0 warnings / 0 errors
   dotnet build App.Avalonia/App.Avalonia.csproj -f net10.0           # re-confirm the Linux TFM
   dotnet test  tests/PcVolumeControllerDashboard.Tests.csproj        # full suite must pass
   dotnet build PcVolumeControllerDashboard.csproj                    # WPF host still builds
   ```
3. **Launch:** `dotnet run --project App.Avalonia/App.Avalonia.csproj -f net10.0-windows`
   > If a `--no-build` run seems to execute old code, rebuild the `net10.0-windows` TFM explicitly or clean `App.Avalonia/bin` + `obj` (stale multi-target output is a known gotcha).
4. **Functional checks** — run the branch-specific script in §5.
5. **Regression smoke** (every branch — nothing below should have regressed):
   - Controller auto-connects and handshakes.
   - Assign a target to a channel; turn its encoder → volume changes and the overlay pops.
   - Short-press to mute/unmute → audio + OLED reflect it.
   - Disconnect + Reconnect from the tray/UI → re-establishes cleanly.
6. **Record** the result in the PR (template §4). On any failure, attach the log
   (`%APPDATA%\PcVolumeController\`) and **do not merge** — flag it.

## 4. Results template (paste as a PR comment)

```
### Windows verification — <branch> (<PR#>)
Environment: Windows <ver>, .NET SDK <x>, firmware <ver>
- [ ] net10.0-windows build: 0 warnings / 0 errors
- [ ] net10.0 build: 0 warnings / 0 errors
- [ ] dotnet test: <N> passed / 0 failed
- [ ] WPF host builds
- [ ] App launches (net10.0-windows) + controller handshakes
- [ ] Branch functional checks (§5) pass
- [ ] Regression smoke (§3.5) pass
Result: PASS / FAIL
Notes:
```

## 5. Per-branch functional scripts

### Q1 — #48 — encoder debounce/coalescing/reverse-guard
1. Turn one channel's encoder **slowly** one step at a time → each step registers, feels responsive (25 ms coalescing is imperceptible).
2. Turn it **fast** through a wide range → volume tracks smoothly; **on Linux specifically** confirm this no longer spawns a burst of `wpctl` processes (watch `pgrep -fc wpctl` or a process monitor during the turn). On Windows just confirm no missed/laggy steps.
3. Wiggle the encoder back-and-forth quickly (simulate a bouncy detent) → isolated single-step reversals are ignored; a genuine sustained reversal still registers.
4. **R1 invariant:** put two channels in a link group, turn **Volume Smoothing OFF** in settings, turn one encoder → the linked channel still moves together. (This must NOT regress to WPF's smoothing-only ganging.)

### Q5 — #49 — distinct overlay mute visual mode
1. Short-press a channel button to **mute** → overlay shows the dedicated mute layout: speaker glyph, "Muted" text, **volume bar hidden**.
2. Short-press again to **unmute** → overlay shows "Unmuted" with the glyph.
3. Trigger the **master-mute global hotkey** → same dedicated mute layout for Master.
4. **Negative check:** mute a channel, then **turn its encoder** → overlay shows the **normal volume bar** (not the mute layout), because it's a volume action, not a mute action.
5. Confirm overlay position, fade/timeout, and no-focus-steal (typing in another window isn't interrupted) are unchanged.

### Q6 — #50 — connected-but-channel-mismatch warning
1. With the normal 6-channel controller → status line is green/normal (no false warning).
2. If a controller/firmware reporting a channel count ≠ 6 is available (or simulate) → the status line shows the **orange channel-count-mismatch warning** naming the reported vs expected count.
3. Confirm the already-merged incompatible-firmware (protocol < 2.24) warning still works and the two warnings are distinguishable.

### Q3 — #51 — rejected/phantom-port reconnect cooldowns
1. With the real controller **and** the second serial device both plugged in, start the app → it connects to the real controller; the wrong port is tried at most once then **stops being re-tried each cycle** (watch the log — it should note the wrong port is on cooldown, not re-attempted every ~3 s).
2. **Unplug and replug the real controller** → it reconnects **promptly** (the cooldown must not lock out the real device).
3. Press **Reconnect** in the UI → clears all cooldowns and rescans immediately.
4. Let the controller reboot slowly (or unplug/replug quickly) → the remembered port is only briefly (≤15 s) skipped, never for the long 5-min window.

### F1 — #52 — auto sleep/wake
1. With the controller connected, **lock the session (Win+L)** → controller receives `SLEEP`; OLEDs blank; state pushes are suppressed (no OLED repaint while locked). Unlock → `WAKE`; OLEDs repaint from live state on the next poll.
2. Leave the machine **idle for 10 minutes** (no input) → `SLEEP`/blank; move the mouse → `WAKE`/repaint.
3. **Suspend/resume** the machine → on resume the controller wakes and repaints. **Watch specifically:** does the `SLEEP` line actually flush over serial *before* the machine suspends? (If the OLEDs don't blank going into suspend, note it — it's the known open question on this feature.)
4. Confirm that on Linux (Null monitor) none of this fires and normal operation is unaffected — the `net10.0` build already exercises that path, but sanity-check no `SLEEP`/`WAKE` is emitted on Linux.
5. Confirm a controller **drop while asleep** (unplug during lock) → on replug it reconnects awake and gets a **full** state resend (no stuck-suppressed OLEDs).

## 6. Final integration pass (after all five are individually green)

Merge the five into a single integration branch (or verify sequentially on `main`
after each merge) and do one end-to-end run to catch cross-feature interactions the
per-branch tests can't:

- With the controller connected: turn encoders (Q1) + mute (Q5) + lock/idle/wake
  (F1) in sequence → confirm the F1 push-suppression and the Q1 encoder timing don't
  interfere (e.g. no queued encoder delta wakes the OLEDs while asleep; OLEDs repaint
  correctly on wake).
- Plug in the second serial device (Q3) and lock/unlock (F1) → reconnect cooldowns
  and sleep/wake coexist without either wedging the connection.

---

# Merge order & conflict notes

Two file-overlap pairs — whichever of each pair merges second needs a quick rebase
(edits are in different regions, so conflicts should be trivial):

- `Services/ChannelRuntime.cs` — **Q1 (#48)** ∩ **Q5 (#49)**
- `Services/SerialConnectionService.cs` — **Q6 (#50)** ∩ **Q3 (#51)**

Recommended order: **#48 → #49 → #50 → #51 → #52** (F1 last — largest surface).

# Version bump — applied once, at merge time (NOT in the feature branches)

The batch is one feature (**F1**) + four patches, so it lands as a single
**`3.11 → 3.12`** feature release. Per standing rule #2, at merge update **all** of:

- `DashboardVersion` in both hosts' `MainWindow` code
- `<Version>` / `<AssemblyVersion>` / `<FileVersion>` in the Avalonia and WPF csprojs
- the README compatibility table
- a new `RELEASE_NOTES_v3.12.md` (remove the superseded notes file)

Keeping the bump out of the feature branches is deliberate — it avoids four-way
collisions in those shared version files.
