# Release Notes — v2.37

## Architecture: SerialService extraction

The raw serial port lifecycle (open, close, send, receive) has been extracted from
`MainWindow.xaml.cs` into a dedicated `SerialService` class.

`SerialService` owns the `SerialPort` instance and exposes two events:
- `LineReceived` — fires for every complete text line from the device
- `ErrorOccurred` — fires on read/write failure, prompting disconnect

`MainWindow` still owns all higher-level orchestration: reconnect logic, port
scanning, protocol parsing, and UI state. This separation makes the serial I/O
layer independently testable and removes ~120 lines from `MainWindow.xaml.cs`.

## Compatibility

- Dashboard: v2.37
- Required firmware protocol: v2.24 (no reflash required)
- Hardware: v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8)
