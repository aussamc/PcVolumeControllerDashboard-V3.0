# Release Notes — v2.44

## Solution file and xUnit test suite

Introduces a solution file (`PcVolumeControllerDashboard.slnx`) and a `tests/` subdirectory containing the first automated test project for the dashboard.

### What was added

**`PcVolumeControllerDashboard.slnx`** — solution file at the repo root that references both the main project and the test project. Enables `dotnet build` / `dotnet test` across the whole repo from one command.

**`tests/PcVolumeControllerDashboard.Tests.csproj`** — xUnit + FluentAssertions test project targeting `net10.0-windows` with 38 tests across three suites:

- **`UpdateCheckerTests`** (12 tests) — version comparison: `IsVersionNewer` (newer/older/equal, numeric vs string ordering) and `TryParseVersion` (valid dotted inputs, single-part padding, invalid inputs).
- **`SettingsRepositoryTests`** (18 tests) — `Normalize` schema migrations v0→v5, value clamping (sensitivity, OLED timeouts), null-guard for channels and profiles, wrong-size channel arrays in profiles, and a Save/Load roundtrip that checks the active profile name is preserved.
- **`SerialServiceTests`** (8 tests) — lifecycle invariants that need no real COM port: initial `IsConnected`/`PortName` state, `Close` idempotency, `Dispose` safety, `SendLine` no-op when disconnected, `GetPortNames` never throwing, and `Open` propagating errors for a non-existent port.

### Supporting changes

- `AssemblyInfo.cs` — added `[assembly: InternalsVisibleTo("PcVolumeControllerDashboard.Tests")]` so the test project can access `internal` members.
- `UpdateChecker.cs` — `IsVersionNewer` and `TryParseVersion` promoted from `private` to `internal` so tests can call them directly without reflection.
- `PcVolumeControllerDashboard.csproj` — added `tests\**` to the SDK glob exclusion list so the main project does not accidentally compile test source files.

### Running the tests

```
dotnet test -c Release
```

or via the solution:

```
dotnet test PcVolumeControllerDashboard.slnx -c Release
```

## Compatibility

- Dashboard: v2.44
- Required firmware protocol: v2.24 (no reflash required)
- Hardware: v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8)
