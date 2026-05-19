# Release Notes — v2.30

## Bug fix

### Per-channel sensitivity "Use global" checkbox could not be unticked
The checkbox in Per-Channel Controls that enables a custom encoder sensitivity per
channel always snapped back to checked after being unchecked. Root cause: `SaveChannelsToSettings()`
reconstructed `_settings.Channels` from the runtime `ChannelMappingItem` collection,
which has no `SensitivityPercent` field — so the per-channel sensitivity was silently
reset to `-1` (use global) on every settings flush. Fixed by preserving all
`ChannelSettings`-only fields (currently `SensitivityPercent`) from the previous array
when rebuilding.

## Stability fixes

### Real-time default audio device changes
Subscribed to Windows' `IMMNotificationClient.OnDefaultDeviceChanged` via NAudio's
`MMDeviceEnumerator.RegisterEndpointNotificationCallback`. When the user switches the
default playback or capture device in Windows Sound settings, the dashboard now
automatically refreshes its device handles and session list without requiring a restart.
The notification client is unregistered cleanly on close.

### Typed COMException handling for dead audio sessions
When an audio session ends between poll cycles, WASAPI raises a `COMException`.
Previously this was either swallowed silently or caught as a generic `Exception`.
Now `COMException` is caught separately; when one is detected during a volume
read/write or mute operation, a deferred session refresh is scheduled via
`Dispatcher.InvokeAsync` so the target list stays accurate without re-entrant calls.

### Silent exception swallowing removed
All bare `catch { }` blocks that discarded exceptions without logging have been
replaced with at minimum `catch (Exception ex) { Log(...); }`. Failures in the
smoothing timer and other background paths now appear in the log.

### COM port enumeration moved off the UI thread
`SerialPort.GetPortNames()` — which reads the Windows registry — was called on the
UI dispatcher thread every second. It now runs inside `Task.Run()` and marshals
results back with `Dispatcher.InvokeAsync`, eliminating the periodic UI stutter
on machines with many virtual COM ports.

### Audio session snapshot comparison replaced with HashSet
The 2.5-second audio session auto-refresh diff was performed by serialising the
entire session list to a JSON string and comparing strings — allocating on every
tick. Replaced with a `HashSet<string>` keyed on target keys and `SetEquals()`
comparison: O(n) time, zero allocations after initial construction.

### COM object disposal on device refresh
`RefreshDefaultAudioDevice()` now explicitly disposes the previous render and
capture `MMDevice` handles before replacing them, preventing COM handle accumulation
across repeated device refresh calls.

## Small improvements

- Window title is now set from `DashboardVersion` in code rather than a hardcoded
  XAML string, so it can never fall out of sync with the version constant.
- Stale placeholder text (`v2.15`) removed from version TextBlocks in the Setup
  tab; they are now populated entirely at runtime by `UpdateVersionHeader()`.
- `ApplySettingsToChannels()` now calls `UpdateSelectedChannelUi()` so the
  Per-Channel Controls panel (including the sensitivity slider) refreshes correctly
  when settings are applied or a profile is switched.
- `ButtonAction` fallback in `ApplySettingsToChannels()` corrected from `NoAction`
  to `ToggleAssignedMute` to match the intended default.
- Settings load now logs: `"Settings loaded (version N, N profile(s))."`.
- Encoder and button action handlers guard against `_channels` being empty (e.g.
  a very early serial message arriving before initialisation completes).
- `EncoderDebounceDisabled` state is now always logged at startup (previously only
  logged when `true`).

## Compatibility

- Dashboard: v2.30
- Required firmware protocol: v2.24 (no reflash required)
- Hardware: v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8)
