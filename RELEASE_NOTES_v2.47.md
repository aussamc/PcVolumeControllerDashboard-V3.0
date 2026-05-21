# Release Notes — v2.47

## Studio Black shape language — buttons, tabs, progress bars

Continues the Studio Black visual redesign by applying the design direction's shape language to controls throughout the UI. Colour-only changes; no logic or layout changes.

### Button corner radius

All buttons now use `CornerRadius="4"` (both the standard button style and the teal PrimaryButton style), replacing the previous sharp-cornered appearance.

### Tab strip restyled — pill tabs

The main tab control has been redesigned to match the Studio Black preview:

- The classic WPF tab chrome border (the bottom-open raised tab with a connecting line to the content) is replaced with a clean `DockPanel`-based template — just a tab strip over the app background, no surrounding border.
- Each tab is now a rounded pill (`CornerRadius="5"`, transparent background by default).
- The active tab gets the teal selection tint background (`#0D2E22`) with teal text (`#5DCAA5`), matching the design direction.
- Inactive tabs are displayed with secondary foreground colour (`#7A7F88`).
- Hover state mirrors the active state for clear affordance.

### Global ProgressBar style

A new global `ProgressBar` style replaces the WPF default:

- Slim rounded track with dark background (`InputBackground` / `#111214`).
- Teal fill (`ConnectionGoodForeground` / `#1D9E75`), `CornerRadius="2"` on both track and fill.
- The previously hardcoded `Foreground="#0066CC"` (stale blue) on the inline volume bar in the channel mappings list is now `ConnectionGoodForeground`.

### Selected channel volume bar height

`SelectedVolumeProgressBar` height reduced from 24 px to 8 px to match the slim bar aesthetic shown in the design direction.

---

## Compatibility

- Dashboard: v2.47
- Required firmware protocol: v2.24 (no reflash required)
- Hardware: v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8)
