# Release Notes — v2.42

## Codebase cleanup: serial/connection partial class

Extracts all serial and connection-lifecycle logic from `MainWindow.xaml.cs` into a new `MainWindow.Serial.cs` partial class file. No behaviour changes — pure code organisation.

### What moved (63 methods)

| Group | Methods |
|---|---|
| Poll timers & debounce | `QueueStatePollTick`, `QueueComPortRefreshTick`, `QueueDebouncedDeviceChangeRefresh`, `QueueDelayedComPortRefresh` |
| Port button handlers | `RequestManualDisconnect`, `RefreshPortsButton_Click`, `ConnectButton_Click` |
| COM port management | `RefreshComPortsIfChanged`, `UpdateComPortAndConnectionState`, `ForceRefreshComPorts`, `LogComPortRefreshIfChanged`, `GetPreferredVisibleComPort`, `SetDisconnectedStatusForAvailablePorts`, `GetAvailableComPorts`, `GetRawComPorts`, `NormalizeComPortForSort`, `IsPortTemporarilyPhantom`, `PrunePhantomComPorts`, `MarkPortPhantom`, `UpdateRememberedControllerPortPresence`, `IsMissingPortOpenFailure`, `IsAccessDeniedOpenFailure`, `CheckConnectedPortStillExists`, `IsConnectedDeviceTimedOut`, `TryAutoReconnect`, `IsPortTemporarilyRejected`, `PruneRejectedComPorts`, `MarkPortRejected` |
| Connection lifecycle | `ConnectSerial` (×2), `DisconnectSerial`, `DisconnectSerialDueToError`, `BeginDisconnectAfterSerialError`, `SetConnectionStatus`, `UpdateStatusBar` |
| Power / session | `OnSessionSwitch`, `OnPowerModeChanged`, `UpdateControllerPowerStateFromPcActivity`, `SendControllerSleep`, `SendControllerWake`, `GetUserIdleMilliseconds` |
| Device messaging | `DispatchHandleDeviceMessage`, `HandleDeviceMessage`, `MarkEsp32Seen`, `HandleHelloMessage` |
| Device protocol sends | `SendPingToDevice`, `RequestHelloFromDevice`, `SendDisconnectToDevice`, `SendStateIfChanged`, `SendStateToDevice`, `SendAllChannelStatesToDevice`, `SendChannelOledModeToDevice`, `SendAllChannelOledModesToDevice`, `SendOledSettingsToDevice`, `WriteSerialLine`, `QueueFullStateSend` |
| OLED mode helpers | `GetOledDisplayModeProtocolValue`, `GetChannelOledModeProtocolValue`, `GetChannelOledModeIndex`, `GetChannelOledModeFromIndex` |
| Protocol version | `IsEspProtocolCompatible`, `CompareVersionParts`, `ParseVersionParts` |

`MainWindow.xaml.cs` goes from ~7 055 lines to ~5 530 lines after this extraction.

## Compatibility

- Dashboard: v2.42
- Required firmware protocol: v2.24 (no reflash required)
- Hardware: v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8)
