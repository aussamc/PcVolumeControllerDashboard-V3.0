# Release Notes — v2.43

## Codebase cleanup: Tray, Hotkeys, and Ui partial classes

Splits three further domains out of `MainWindow.xaml.cs` into dedicated partial class files. No behaviour changes — pure code organisation.

### What moved

**`MainWindow.Tray.cs`** (10 methods):
`LoadAppIcon`, `SetupTrayIcon`, `BuildTrayProfileMenu`, `DispatchUi`, `ShowTrayNotification`, `OnStateChanged`, `HideToTray`, `RestoreFromTray`, `ExitApplication`, `OnClosing`

**`MainWindow.Hotkeys.cs`** (8 methods):
`OnSourceInitialized`, `WndProc`, `RegisterAllHotkeys`, `RegisterHotkeyIfAssigned`, `UnregisterAllHotkeys`, `HandleHotkeyEvent`, `UpdateHotkeyLabels`, `SetHotkeyBinding`

**`MainWindow.Ui.cs`** (14 methods):
`ShowVolumeOverlay`, `PositionOverlayWindow`, `UpdateDiagnostics`, `ApplyTheme`, `ApplyWindowChromeTheme`, `IsWindowsUsingLightTheme`, `IsAdvancedDebugLoggingEnabled`, `AppendDebugConsole`, `UpdateVersionHeader`, `LogStartupHeader`, `CleanupOldLogs`, `Log`, `GetLogDirectory`, `GetLogPath`

### After this version

`MainWindow.xaml.cs` is now ~4 590 lines (down from the original ~7 700+ before v2.41), containing the constructor, fields/constants, audio management, channel management, profile management, settings UI, firmware flasher, and import/export.

## Compatibility

- Dashboard: v2.43
- Required firmware protocol: v2.24 (no reflash required)
- Hardware: v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8)
