# Parity batch — verify & merge checklist

Five Avalonia parity branches produced in one parallel pass (2026-07-07). Each is
**build-green on `net10.0`** and code-reviewed clean, but **none can merge until the
Windows gate below is cleared** — `net10.0-windows` and `dotnet test` cannot run on
Linux (the test project targets `net10.0-windows` only).

This doc is the handoff for running that verification on a Windows box.

## Branches in this batch

| Branch | Item | Kind | Files touched |
|---|---|---|---|
| `fix/v3.11.1-encoder-debounce` | Q1 — encoder debounce/coalescing/reverse-guard | patch | `Services/ChannelRuntime.cs` |
| `fix/v3.11.x-pre224-firmware-diagnostics` | Q6 — connected-but-channel-mismatch warning | patch | `Services/SerialConnectionService.cs`, `MainWindow.axaml.cs` |
| `fix/v3.11.x-overlay-mute-mode` | Q5 — distinct overlay mute visual mode | patch | `VolumeOverlay.cs`, `Services/ChannelRuntime.cs`, `Services/GlobalHotkeyManager.cs` |
| `fix/v3.11.x-port-cooldown-tracking` | Q3 — rejected/phantom-port reconnect cooldowns | patch | `Services/SerialConnectionService.cs` |
| `feat/v3.x-auto-sleep-wake` | F1 — PC idle/lock/suspend → controller SLEEP/WAKE | feature | Core (`IPcActivityMonitor`, `NullPcActivityMonitor`), `Platform.Windows/WindowsPcActivityMonitor.cs`, `App.Avalonia/Services/{SleepWakeService,DeviceStateService}.cs`, `App.axaml.cs` |

> There is also a small `fix/v3.11.x-pre224-firmware-diagnostics` note: B1 + the Q6
> warning line were already merged on `main` (PR #43) before this pass; that branch
> only adds the remaining channel-count-mismatch diagnostic.

## Pull the batch onto the Windows box

```bash
git fetch origin
# verify each branch in turn, e.g.:
git checkout fix/v3.11.1-encoder-debounce
```

## The universal gate — run per branch, on Windows

```bash
dotnet build App.Avalonia/App.Avalonia.csproj -f net10.0-windows   # MUST be 0 warnings / 0 errors
dotnet build App.Avalonia/App.Avalonia.csproj -f net10.0           # re-confirm the Linux TFM
dotnet test  tests/PcVolumeControllerDashboard.Tests.csproj        # full suite must pass
dotnet build PcVolumeControllerDashboard.csproj                    # WPF host still builds
```

## Per-branch behavior checks (beyond the build)

- **Q1 — `fix/v3.11.1-encoder-debounce`**
  - Fast/bouncy encoder turn no longer spawns a burst of `wpctl` processes (Linux); single steps still feel responsive (25 ms coalescing is imperceptible).
  - Linked channels still gang **with Volume Smoothing OFF** — this is the R1 invariant; do not let it regress toward WPF's conditional behavior.
- **Q6 — `fix/v3.11.x-pre224-firmware-diagnostics`**
  - A controller reporting ≠ 6 channels surfaces the orange channel-count warning line.
- **Q5 — `fix/v3.11.x-overlay-mute-mode`**
  - Mute toggle (button action **and** master-mute hotkey) shows the speaker-glyph / "Muted" layout with the volume bar hidden.
  - A plain encoder turn on an already-muted channel still shows the **normal** volume bar (mute layout is keyed on the mute *action*, not the muted *state*).
- **Q3 — `fix/v3.11.x-port-cooldown-tracking`**
  - With a second serial device plugged in, the wrong port stops being re-tried every reconnect cycle.
  - Unplug/replug of the real controller reconnects promptly (the cooldown must not lock out the real device).
- **F1 — `feat/v3.x-auto-sleep-wake`**
  - Lock / 10-min idle / suspend → controller sleeps (OLEDs blank, state pushes suppressed); unlock / resume → wakes and OLEDs repaint from live state.
  - **Watch specifically:** does the `SLEEP` line flush over serial before the machine actually suspends?

## Merge order & conflict notes

Two file-overlap pairs — whichever of each pair merges second needs a quick rebase
(edits are in different regions, so conflicts should be trivial):

- `Services/ChannelRuntime.cs` — **Q1** (encoder path) ∩ **Q5** (`VolumeOverlayInfo` record + mute site)
- `Services/SerialConnectionService.cs` — **Q6** (`ConnectedChannelCount`) ∩ **Q3** (cooldown maps)

Suggested order: **Q1 → Q5 → Q6 → Q3 → F1** (F1 last — largest surface).

## Version bump — applied once, at merge time (NOT in the feature branches)

The batch is one feature (**F1**) + four patches, so it lands as a single
**`3.11 → 3.12`** feature release. Per standing rule #2, at merge update **all** of:

- `DashboardVersion` in both hosts' `MainWindow` code
- `<Version>` / `<AssemblyVersion>` / `<FileVersion>` in the Avalonia and WPF csprojs
- the README compatibility table
- a new `RELEASE_NOTES_v3.12.md` (remove the superseded notes file)

Keeping the bump out of the feature branches is deliberate — it avoids four-way
collisions in those shared version files.
