# Release Notes — v2.50

## Fix: UpdateChecker user agent version

The HTTP `User-Agent` header sent to the GitHub Releases API was hardcoded to
`PcVolumeControllerDashboard/2.44`, regardless of the running dashboard version.

### What changed

- User-Agent is now built per-request as `PcVolumeControllerDashboard/<currentVersion>`,
  so it always reflects the version that actually performed the check.
- The Accept header is also set per-request rather than on the shared `HttpClient`
  default headers — making `CheckAsync` safe for hypothetical concurrent calls.

---

## Compatibility

- Dashboard: v2.50
- Required firmware protocol: v2.24 (no reflash required)
- Hardware: v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8)
