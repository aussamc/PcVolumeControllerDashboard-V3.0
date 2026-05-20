# Release Notes — v2.38

## Architecture: AudioService extraction

NAudio device management and WASAPI get/set operations have been extracted from
`MainWindow.xaml.cs` into a dedicated `AudioService` class.

`AudioService` owns:
- `MMDeviceEnumerator` and default render/capture device handles
- `IMMNotificationClient` listener for device-change notifications
- `SetVolume`, `GetVolume`, `SetMute`, `GetMute` — operate on `AudioTargetItem` objects
- `GetActiveSessions` — enumerates raw audio sessions
- `RefreshDefaultDevice` — re-queries default endpoints

`MainWindow` retains channel→session mapping, the `_audioTargets` collection,
UI updates, and all business logic. `AudioService` has no knowledge of channels or UI.

## Compatibility

- Dashboard: v2.38
- Required firmware protocol: v2.24 (no reflash required)
- Hardware: v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8)
