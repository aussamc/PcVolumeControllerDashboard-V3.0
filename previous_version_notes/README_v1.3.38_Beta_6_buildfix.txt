PC Volume Controller Dashboard v1.3.38 Beta 6 buildfix

Buildfix changes:
- Fixed CS0104 MouseEventArgs ambiguity caused by System.Windows.Forms and WPF namespace overlap.
- Fully qualified the slider drag handlers to use System.Windows.Input.MouseEventArgs.

Version status:
- Dashboard: v1.3.38 Beta 6 buildfix
- Required ESP32 protocol: v1.3.38 Beta 6
- Firmware folder: Computer_Volume_Controller_v1.3.38_Beta_6

Notes:
- This is a dashboard buildfix only.
- No ESP32 firmware reflash is required if Beta 6 firmware is already installed.
