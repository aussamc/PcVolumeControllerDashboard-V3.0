# Release Notes — v2.41

## Codebase cleanup: encoder partial class

Extracts all encoder-domain logic from `MainWindow.xaml.cs` into a new `MainWindow.Encoder.cs` partial class file. No behaviour changes — this is a pure code organisation improvement.

### What moved

The following 21 methods were extracted into `MainWindow.Encoder.cs`:

| Group | Methods |
|---|---|
| Entry point | `HandleEncoderMessage`, `RemapEncoderChannel` |
| Debounce / coalescing | `QueueSmoothedEncoderDelta`, `GetEncoderApplyDelayMsLocked`, `TakePendingEncoderDeltaLocked`, `ScheduleEncoderCoalesceTimerLocked` |
| Apply delta | `BeginApplySmoothedEncoderDelta`, `ApplySmoothedEncoderDelta` |
| Acceleration | `GetAcceleratedStep`, `ComputeCustomAccelMultiplier` |
| Smoothing (EMA) | `GetSmoothingAlpha`, `EnsureSmoothingTimerRunning`, `StopSmoothingTimer`, `SmoothingTick` |
| Volume read / write | `GetChannelCurrentVolumeNormalized`, `SetChannelVolumeAbsolute` |
| Sensitivity | `GetVolumeStepPercent`, `GetVolumeStepPercentForChannel`, `GetVolumeStepPercentFromSensitivity` |
| Hardware diagnostics | `RegisterHardwareEncoderEvent`, `HighlightEncoderPreview` |

All fields remain in `MainWindow.xaml.cs` (WPF partial-class rules require a single definition point for fields).

### Why

`MainWindow.xaml.cs` had grown to ~6 600 lines. Splitting by domain makes each area easier to navigate, review, and reason about in isolation. v2.42 will continue with the serial-communication methods.

## Compatibility

- Dashboard: v2.41
- Required firmware protocol: v2.24 (no reflash required)
- Hardware: v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8)
