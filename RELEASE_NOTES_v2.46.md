# Release Notes — v2.46

## Studio Black theme

Replaced the default light/grey palette with the Studio Black colour scheme across the entire dashboard. The theme is a deep charcoal dark mode with electric teal accents.

### Colour palette

| Role | Hex |
|------|-----|
| App background | `#0D0F11` |
| Card background | `#1A1D20` |
| Surface | `#111214` |
| Elevated surface | `#252830` |
| Border | `#303540` |
| Accent (teal) | `#1D9E75` |
| Accent high | `#5DCAA5` |
| Accent deep | `#0F6E56` |
| Selection tint | `#0D2E22` |
| Text primary | `#F2F2F2` |
| Text secondary | `#7A7F88` |

### Changes

**`MainWindow.xaml`**
- Replaced all 38 `<SolidColorBrush>` declarations in `<Window.Resources>` with Studio Black values.
- Updated `PrimaryButton` style: background `#0066CC` → `#1D9E75`, border `#0055AA` → `#0F6E56`, hover `#0055AA` → `#0F6E56`, pressed `#004488` → `#085041`.

**`MainWindow.Ui.cs`**
- Updated all 35 dark-branch `WpfColor.FromRgb(...)` values in `ApplyTheme()` to the Studio Black palette.
- Light mode (`dark == false`) branch is untouched.

No layout, sizing, logic, or structural changes were made.

---

## Compatibility

- Dashboard: v2.46
- Required firmware protocol: v2.24 (no reflash required)
- Hardware: v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8)
