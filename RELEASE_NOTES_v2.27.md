# Release Notes — v2.27

## Changes

### Auto-Rebind tab removed — channel rebind moved to Per-Channel Controls
The separate **Auto-Rebind** tab has been removed. Its functionality (selecting which audio
session to assign each encoder to) is now available directly in the **Per-Channel Controls**
section of the Audio tab via a single **Channel Rebind** combo box. Selecting a different
channel from the channel selector immediately updates the combo box to reflect that
channel's current assignment.

### Persistent status bar
A status bar is now docked at the bottom of the main window and remains visible regardless
of which tab is active. It shows:
- A coloured dot indicating connection state (green = connected, amber = connecting, red = disconnected).
- The active COM port and connection status.
- The active profile name.
- The connected firmware version.

### Primary button accent style
A `PrimaryButton` named WPF style (`#0066CC` blue, white text) is applied to the four
main action buttons: **Connect**, **Assign Channel**, **Flash Firmware**, and **New Profile**.
All other buttons retain the default window chrome style for clear visual hierarchy.

### Auto-save — Setup tab "Save Setup" button removed
Settings are saved automatically whenever they change. The now-redundant **Save Setup**
button has been removed and replaced with an italicised note confirming that settings
are saved automatically.

### First Run Wizard tab hidden after first use
The **First Run Wizard** tab is hidden once the wizard has been completed. A **"Show setup
guide"** link in the Setup tab allows the user to bring it back at any time.

### Firmware tab: dynamic firmware source string
The firmware source description in the Firmware tab is now set dynamically from code
(via `UpdateVersionHeader()`) so it accurately reflects the current build's asset paths
rather than being a hardcoded literal that could go stale.

### Mini volume bars in channel list
Each row of the **Channel Mappings** list view now shows a narrow (`3 px`) horizontal
progress bar beneath the volume percentage, coloured `#0066CC`, giving an at-a-glance
visual indication of each channel's current volume level.

### Profile submenu in system tray
The system-tray context menu now includes a **Profiles** sub-menu. Each named profile
appears as a menu item; clicking one switches to that profile immediately without opening
the dashboard window.

### Application icon
The dashboard now ships with a custom placeholder `.ico` icon (a blue circle with a white
chevron). It is applied as the Win32 application icon (visible in the taskbar, Alt+Tab
switcher, and Windows Explorer), the WPF window title-bar icon, and the system-tray
notification icon.

## Compatibility

- Dashboard: v2.27
- Required firmware protocol: v2.24 (no reflash required)
- Hardware: v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8)
