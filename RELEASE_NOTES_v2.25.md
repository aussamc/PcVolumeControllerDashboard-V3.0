# Release Notes — v2.25

## Changes

### Named profiles

You can now save and switch between named sets of channel assignments without
touching the individual channel controls.

A **profile bar** has been added to the top of the Audio tab (above the channel
mapping list). It shows the currently active profile and four action buttons:

| Button | Action |
|--------|--------|
| **New** | Create a new profile with default channel assignments |
| **Rename** | Rename the active profile |
| **Duplicate** | Copy the active profile's channel assignments to a new profile |
| **Delete** | Delete the active profile (disabled when only one profile exists) |

Switching profiles is immediate — selecting a different profile from the
drop-down applies its channel assignments instantly with no Apply button needed.

#### What a profile stores

Each profile holds the six channel settings:

- Target (assigned app / master)
- Display name
- Short / long / double-press button actions
- Auto-rebind fallback behaviour
- Per-channel OLED display mode

All other settings (OLED brightness, encoder sensitivity, COM port, etc.) are
global and shared across profiles.

#### Settings migration

Existing setups are migrated automatically on first launch: a single **Default**
profile is created from the current channel assignments. No channel settings are
lost.

## Compatibility

- Dashboard: v2.25
- Required firmware protocol: v2.24 (no reflash required)
- Hardware: v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8)
