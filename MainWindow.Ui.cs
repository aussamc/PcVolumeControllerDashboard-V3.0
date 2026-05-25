// MainWindow.Ui.cs — Theme application, logging, diagnostics panel, and volume overlay.
// Extracted from MainWindow.xaml.cs in v2.43. All fields remain in MainWindow.xaml.cs.

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;
using WpfBrush = System.Windows.Media.Brush;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfColor = System.Windows.Media.Color;
using WpfSolidColorBrush = System.Windows.Media.SolidColorBrush;
using WpfSystemColors = System.Windows.SystemColors;
using WpfDispatcherTimer = System.Windows.Threading.DispatcherTimer;

namespace PcVolumeControllerDashboard;

public partial class MainWindow
{

    // ── End update checker ───────────────────────────────────────────────────────

    private void ShowVolumeOverlay(int channelIndex, int volumePercent)
    {
        if (!_settings.OverlayEnabled) return;
        if (channelIndex < 0 || channelIndex >= _channels.Count) return;

        string channelName = _channels[channelIndex].DisplayLabel;
        double timeout = _settings.OverlayTimeoutSeconds;
        bool isDark = _settings.ThemeMode == ThemeModes.Dark ||
                      (_settings.ThemeMode == ThemeModes.FollowSystem && !IsWindowsUsingLightTheme());

        Dispatcher.InvokeAsync(() =>
        {
            if (_overlayWindow == null || !_overlayWindow.IsLoaded)
            {
                _overlayWindow = new VolumeOverlayWindow();
            }

            PositionOverlayWindow();
            _overlayWindow.ShowOverlay(channelName, volumePercent, timeout, isDark);
        });
    }


    /// <summary>
    /// Shows (or refreshes) the overlay in mute mode — speaker icon + Muted / Unmuted text.
    /// No-op when the overlay is disabled in settings.
    /// </summary>
    private void ShowMuteOverlay(int channelIndex, bool isMuted)
    {
        if (!_settings.OverlayEnabled) return;
        if (channelIndex < 0 || channelIndex >= _channels.Count) return;

        string channelName = _channels[channelIndex].DisplayLabel;
        double timeout = _settings.OverlayTimeoutSeconds;
        bool isDark = _settings.ThemeMode == ThemeModes.Dark ||
                      (_settings.ThemeMode == ThemeModes.FollowSystem && !IsWindowsUsingLightTheme());

        Dispatcher.InvokeAsync(() =>
        {
            if (_overlayWindow == null || !_overlayWindow.IsLoaded)
            {
                _overlayWindow = new VolumeOverlayWindow();
            }

            PositionOverlayWindow();
            _overlayWindow.ShowMuteOverlay(channelName, isMuted, timeout, isDark);
        });
    }

    private void PositionOverlayWindow()
    {
        if (_overlayWindow == null) return;

        // Use the work area (screen minus taskbar)
        System.Windows.Rect work = SystemParameters.WorkArea;
        const double margin = 24;
        const double overlayW = 280;
        const double overlayH = 80; // approximate

        double x, y;
        switch (_settings.OverlayPosition)
        {
            case "TopLeft":
                x = work.Left + margin;
                y = work.Top + margin;
                break;
            case "TopCenter":
                x = work.Left + (work.Width - overlayW) / 2;
                y = work.Top + margin;
                break;
            case "TopRight":
                x = work.Right - overlayW - margin;
                y = work.Top + margin;
                break;
            case "BottomLeft":
                x = work.Left + margin;
                y = work.Bottom - overlayH - margin;
                break;
            case "BottomRight":
                x = work.Right - overlayW - margin;
                y = work.Bottom - overlayH - margin;
                break;
            case "BottomCenter":
            default:
                x = work.Left + (work.Width - overlayW) / 2;
                y = work.Bottom - overlayH - margin;
                break;
        }

        _overlayWindow.Left = x;
        _overlayWindow.Top = y;
    }


    private void UpdateDiagnostics()
    {
        bool portOpen = _serialService.IsConnected;
        bool connected = portOpen && _esp32HelloReceived;
        string comPort = portOpen ? (_serialService.PortName ?? "--") : "--";

        DiagConnectionTextBlock.Text = connected ? "ESP32 connection: Connected" : portOpen ? "ESP32 connection: Identifying" : "ESP32 connection: Disconnected";
        DiagComPortTextBlock.Text = $"COM port: {comPort}";

        if (_lastEspMessageTime.HasValue)
        {
            TimeSpan age = DateTime.Now - _lastEspMessageTime.Value;
            DiagLastHeartbeatTextBlock.Text = $"Last message: {age.TotalSeconds:0.0}s ago";
        }
        else
        {
            DiagLastHeartbeatTextBlock.Text = "Last message: --";
        }

        DiagFirmwareTextBlock.Text = $"Firmware: {_espFirmwareName}";
        DiagProtocolTextBlock.Text = $"Protocol: {_espProtocolVersion} / Required {RequiredProtocolVersion}";
        DiagLastMessageTextBlock.Text = $"Last ESP32 message: {_lastEspMessage}";
        DiagLastStateSentTextBlock.Text = $"Last state sent: {_lastStateSent}";

        bool protocolOk =
            IsEspProtocolCompatible(_espProtocolVersion) &&
            _espChannelCount.Equals(ExpectedChannelCount.ToString(), StringComparison.OrdinalIgnoreCase);

        if (!portOpen)
        {
            DiagProtocolStatusTextBlock.Text = "Protocol status: Waiting for ESP32";
            DiagProtocolStatusTextBlock.Foreground = Resources["SecondaryForeground"] as WpfBrush;
        }
        else if (!connected)
        {
            DiagProtocolStatusTextBlock.Text = "Protocol status: Identifying controller";
            DiagProtocolStatusTextBlock.Foreground = Resources["WarningForeground"] as WpfBrush;
        }
        else if (protocolOk)
        {
            DiagProtocolStatusTextBlock.Text = "Protocol status: OK";
            DiagProtocolStatusTextBlock.Foreground = Resources["ConnectionGoodForeground"] as WpfBrush;
        }
        else
        {
            DiagProtocolStatusTextBlock.Text = $"Protocol status: Warning - ESP32 reports protocol {_espProtocolVersion}, channels {_espChannelCount}; requires v{RequiredProtocolVersion}+ and {ExpectedChannelCount} channels";
            DiagProtocolStatusTextBlock.Foreground = Resources["WarningForeground"] as WpfBrush;
        }

        UpdateFirstRunWizardStatus();
    }


    private void ApplyTheme()
    {
        string mode = _settings.ThemeMode;

        if (mode == ThemeModes.FollowSystem)
        {
            mode = IsWindowsUsingLightTheme() ? ThemeModes.Light : ThemeModes.Dark;
        }

        bool dark = mode == ThemeModes.Dark;

        WpfBrush appBackground = dark
            ? new WpfSolidColorBrush(WpfColor.FromRgb(13, 15, 17))       // #0D0F11
            : new WpfSolidColorBrush(WpfColor.FromRgb(255, 255, 255));

        WpfBrush cardBackground = dark
            ? new WpfSolidColorBrush(WpfColor.FromRgb(26, 29, 32))       // #1A1D20
            : new WpfSolidColorBrush(WpfColor.FromRgb(255, 255, 255));

        WpfBrush appForeground = dark
            ? new WpfSolidColorBrush(WpfColor.FromRgb(242, 242, 242))    // #F2F2F2
            : new WpfSolidColorBrush(WpfColor.FromRgb(17, 17, 17));

        WpfBrush secondaryForeground = dark
            ? new WpfSolidColorBrush(WpfColor.FromRgb(122, 127, 136))    // #7A7F88
            : new WpfSolidColorBrush(WpfColor.FromRgb(100, 100, 100));

        WpfBrush cardBorder = dark
            ? new WpfSolidColorBrush(WpfColor.FromRgb(37, 40, 48))       // #252830
            : new WpfSolidColorBrush(WpfColor.FromRgb(221, 221, 221));

        WpfBrush buttonBackground = dark
            ? new WpfSolidColorBrush(WpfColor.FromRgb(26, 29, 32))       // #1A1D20
            : new WpfSolidColorBrush(WpfColor.FromRgb(238, 238, 238));

        WpfBrush buttonHoverBackground = dark
            ? new WpfSolidColorBrush(WpfColor.FromRgb(37, 40, 48))       // #252830
            : new WpfSolidColorBrush(WpfColor.FromRgb(226, 226, 226));

        WpfBrush buttonPressedBackground = dark
            ? new WpfSolidColorBrush(WpfColor.FromRgb(17, 18, 20))       // #111214
            : new WpfSolidColorBrush(WpfColor.FromRgb(214, 214, 214));

        WpfBrush buttonForeground = dark
            ? new WpfSolidColorBrush(WpfColor.FromRgb(242, 242, 242))    // #F2F2F2
            : new WpfSolidColorBrush(WpfColor.FromRgb(17, 17, 17));

        WpfBrush buttonBorder = dark
            ? new WpfSolidColorBrush(WpfColor.FromRgb(48, 53, 64))       // #303540
            : new WpfSolidColorBrush(WpfColor.FromRgb(136, 136, 136));

        WpfBrush listBackground = dark
            ? new WpfSolidColorBrush(WpfColor.FromRgb(17, 18, 20))       // #111214
            : new WpfSolidColorBrush(WpfColor.FromRgb(255, 255, 255));

        WpfBrush headerBackground = dark
            ? new WpfSolidColorBrush(WpfColor.FromRgb(17, 18, 20))       // #111214
            : new WpfSolidColorBrush(WpfColor.FromRgb(242, 242, 242));

        WpfBrush selectedBackground = dark
            ? new WpfSolidColorBrush(WpfColor.FromRgb(13, 46, 34))       // #0D2E22
            : new WpfSolidColorBrush(WpfColor.FromRgb(221, 238, 255));

        WpfBrush selectedForeground = dark
            ? new WpfSolidColorBrush(WpfColor.FromRgb(93, 202, 165))     // #5DCAA5
            : new WpfSolidColorBrush(WpfColor.FromRgb(17, 17, 17));

        WpfBrush inputBackground = dark
            ? new WpfSolidColorBrush(WpfColor.FromRgb(17, 18, 20))       // #111214
            : new WpfSolidColorBrush(WpfColor.FromRgb(255, 255, 255));

        WpfBrush popupBackground = dark
            ? new WpfSolidColorBrush(WpfColor.FromRgb(26, 29, 32))       // #1A1D20
            : new WpfSolidColorBrush(WpfColor.FromRgb(255, 255, 255));

        WpfBrush tabSelectedBackground = dark
            ? new WpfSolidColorBrush(WpfColor.FromRgb(26, 29, 32))       // #1A1D20
            : new WpfSolidColorBrush(WpfColor.FromRgb(255, 255, 255));

        WpfBrush tabSelectedForeground = dark
            ? new WpfSolidColorBrush(WpfColor.FromRgb(242, 242, 242))    // #F2F2F2
            : new WpfSolidColorBrush(WpfColor.FromRgb(17, 17, 17));

        WpfBrush tabUnselectedBackground = dark
            ? new WpfSolidColorBrush(WpfColor.FromRgb(17, 18, 20))       // #111214
            : new WpfSolidColorBrush(WpfColor.FromRgb(233, 233, 233));

        WpfBrush tabUnselectedForeground = dark
            ? new WpfSolidColorBrush(WpfColor.FromRgb(122, 127, 136))    // #7A7F88
            : new WpfSolidColorBrush(WpfColor.FromRgb(17, 17, 17));

        WpfBrush connectionGood = dark
            ? new WpfSolidColorBrush(WpfColor.FromRgb(29, 158, 117))     // #1D9E75
            : new WpfSolidColorBrush(WpfColor.FromRgb(0, 120, 20));

        WpfBrush previewSurfaceBackground = dark
            ? new WpfSolidColorBrush(WpfColor.FromRgb(17, 18, 20))       // #111214
            : new WpfSolidColorBrush(WpfColor.FromRgb(247, 247, 247));

        WpfBrush previewSurfaceBorder = dark
            ? new WpfSolidColorBrush(WpfColor.FromRgb(37, 40, 48))       // #252830
            : new WpfSolidColorBrush(WpfColor.FromRgb(200, 200, 200));

        WpfBrush previewEncoderOuterFill = dark
            ? new WpfSolidColorBrush(WpfColor.FromRgb(37, 40, 48))       // #252830
            : new WpfSolidColorBrush(WpfColor.FromRgb(239, 239, 239));

        WpfBrush previewEncoderInnerFill = dark
            ? new WpfSolidColorBrush(WpfColor.FromRgb(26, 29, 32))       // #1A1D20
            : new WpfSolidColorBrush(WpfColor.FromRgb(250, 250, 250));

        WpfBrush previewEncoderStroke = dark
            ? new WpfSolidColorBrush(WpfColor.FromRgb(93, 202, 165))     // #5DCAA5
            : new WpfSolidColorBrush(WpfColor.FromRgb(68, 68, 68));

        WpfBrush previewEncoderActiveBackground = dark
            ? new WpfSolidColorBrush(WpfColor.FromRgb(13, 46, 34))       // #0D2E22
            : new WpfSolidColorBrush(WpfColor.FromRgb(221, 238, 255));

        WpfBrush connectionBad = dark
            ? new WpfSolidColorBrush(WpfColor.FromRgb(224, 90, 90))      // #E05A5A
            : new WpfSolidColorBrush(WpfColor.FromRgb(170, 31, 31));

        WpfBrush warning = dark
            ? new WpfSolidColorBrush(WpfColor.FromRgb(239, 159, 39))     // #EF9F27
            : new WpfSolidColorBrush(WpfColor.FromRgb(176, 96, 0));

        WpfBrush warningBannerBackground = dark
            ? new WpfSolidColorBrush(WpfColor.FromRgb(34, 24, 0))        // #221800
            : new WpfSolidColorBrush(WpfColor.FromRgb(0xFF, 0xF3, 0xCD));

        WpfBrush warningBannerBorder = dark
            ? new WpfSolidColorBrush(WpfColor.FromRgb(61, 46, 0))        // #3D2E00
            : new WpfSolidColorBrush(WpfColor.FromRgb(0xFF, 0xEE, 0xBA));

        WpfBrush warningBannerForeground = dark
            ? new WpfSolidColorBrush(WpfColor.FromRgb(250, 199, 117))    // #FAC775
            : new WpfSolidColorBrush(WpfColor.FromRgb(0x85, 0x64, 0x04));

        // Update banner — teal info style
        WpfBrush updateBannerBackground = dark
            ? new WpfSolidColorBrush(WpfColor.FromRgb(13, 30, 26))       // #0D1E1A
            : new WpfSolidColorBrush(WpfColor.FromRgb(0xE8, 0xF4, 0xFD));

        WpfBrush updateBannerBorder = dark
            ? new WpfSolidColorBrush(WpfColor.FromRgb(20, 61, 46))       // #143D2E
            : new WpfSolidColorBrush(WpfColor.FromRgb(0xBE, 0xE3, 0xF8));

        WpfBrush updateBannerForeground = dark
            ? new WpfSolidColorBrush(WpfColor.FromRgb(93, 202, 165))     // #5DCAA5
            : new WpfSolidColorBrush(WpfColor.FromRgb(0x1A, 0x3B, 0x5D));

        Resources["AppBackground"] = appBackground;
        Resources["CardBackground"] = cardBackground;
        Resources["AppForeground"] = appForeground;
        Resources["SecondaryForeground"] = secondaryForeground;
        Resources["CardBorder"] = cardBorder;
        Resources["PreviewSurfaceBackground"] = previewSurfaceBackground;
        Resources["PreviewSurfaceBorder"] = previewSurfaceBorder;
        Resources["PreviewEncoderOuterFill"] = previewEncoderOuterFill;
        Resources["PreviewEncoderInnerFill"] = previewEncoderInnerFill;
        Resources["PreviewEncoderStroke"] = previewEncoderStroke;
        Resources["PreviewEncoderActiveBackground"] = previewEncoderActiveBackground;

        Resources["ButtonBackground"] = buttonBackground;
        Resources["ButtonHoverBackground"] = buttonHoverBackground;
        Resources["ButtonPressedBackground"] = buttonPressedBackground;
        Resources["ButtonForeground"] = buttonForeground;
        Resources["ButtonBorder"] = buttonBorder;

        Resources["ListBackground"] = listBackground;
        Resources["HeaderBackground"] = headerBackground;
        Resources["HeaderForeground"] = appForeground;
        Resources["SelectedBackground"] = selectedBackground;
        Resources["SelectedForeground"] = selectedForeground;
        Resources["InputBackground"] = inputBackground;
        Resources["InputForeground"] = appForeground;
        Resources["PopupBackground"] = popupBackground;
        Resources["TabSelectedBackground"] = tabSelectedBackground;
        Resources["TabSelectedForeground"] = tabSelectedForeground;
        Resources["TabUnselectedBackground"] = tabUnselectedBackground;
        Resources["TabUnselectedForeground"] = tabUnselectedForeground;
        Resources["TabBorderBrush"] = cardBorder;
        Resources["ConnectionGoodForeground"] = connectionGood;
        Resources["ConnectionBadForeground"] = connectionBad;
        Resources["WarningForeground"] = warning;
        Resources["WarningBannerBackground"] = warningBannerBackground;
        Resources["WarningBannerBorder"] = warningBannerBorder;
        Resources["WarningBannerForeground"] = warningBannerForeground;
        Resources["UpdateBannerBackground"] = updateBannerBackground;
        Resources["UpdateBannerBorder"] = updateBannerBorder;
        Resources["UpdateBannerForeground"] = updateBannerForeground;

        Background = appBackground;
        Foreground = appForeground;

        Resources[WpfSystemColors.WindowBrushKey] = appBackground;
        Resources[WpfSystemColors.ControlBrushKey] = cardBackground;
        Resources[WpfSystemColors.ControlTextBrushKey] = appForeground;
        Resources[WpfSystemColors.WindowTextBrushKey] = appForeground;
        Resources[WpfSystemColors.MenuTextBrushKey] = appForeground;
        Resources[WpfSystemColors.ControlLightBrushKey] = cardBorder;
        Resources[WpfSystemColors.ControlDarkBrushKey] = cardBorder;
        Resources[WpfSystemColors.HighlightBrushKey] = selectedBackground;
        Resources[WpfSystemColors.HighlightTextBrushKey] = selectedForeground;

        if (ConnectionStatusTextBlock != null)
        {
            if (ConnectionStatusTextBlock.Text.StartsWith("Connected", StringComparison.OrdinalIgnoreCase))
            {
                ConnectionStatusTextBlock.Foreground = connectionGood;
            }
            else
            {
                ConnectionStatusTextBlock.Foreground = connectionBad;
            }
        }

        UpdateDiagnostics();
        ApplyWindowChromeTheme(dark);
    }


    private void ApplyWindowChromeTheme(bool dark)
    {
        try
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;

            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            int useDark = dark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref useDark, Marshal.SizeOf<int>());
        }
        catch
        {
        }
    }


    private static bool IsWindowsUsingLightTheme()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                writable: false);

            object? value = key?.GetValue("AppsUseLightTheme");
            return value is int intValue ? intValue != 0 : true;
        }
        catch
        {
            return true;
        }
    }



    private bool IsAdvancedDebugLoggingEnabled()
    {
        try
        {
            return _settings.AdvancedDebugLogging || AdvancedDebugLoggingCheckBox?.IsChecked == true;
        }
        catch
        {
            return _settings.AdvancedDebugLogging;
        }
    }


    private void AppendDebugConsole(string direction, string message)
    {
        try
        {
            bool isHeartbeat = message.Equals(ProtocolCommands.Ping, StringComparison.OrdinalIgnoreCase) || message.Equals(ProtocolCommands.Pong, StringComparison.OrdinalIgnoreCase);
            bool showHeartbeat = ShowHeartbeatDebugCheckBox?.IsChecked == true;
            bool advanced = IsAdvancedDebugLoggingEnabled();

            if (isHeartbeat && !showHeartbeat && !advanced)
            {
                return;
            }

            string line = $"{DateTime.Now:HH:mm:ss.fff} {direction} {message}";
            _debugConsoleLines.Add(line);

            if (DebugConsoleTextBox != null)
            {
                if (_debugConsoleLines.Count > DebugConsoleMaxLines)
                {
                    int removeCount = _debugConsoleLines.Count - DebugConsoleMaxLines;
                    _debugConsoleLines.RemoveRange(0, removeCount);
                    DebugConsoleTextBox.Text = string.Join(Environment.NewLine, _debugConsoleLines);
                }
                else
                {
                    if (DebugConsoleTextBox.Text.Length > 0)
                    {
                        DebugConsoleTextBox.AppendText(Environment.NewLine + line);
                    }
                    else
                    {
                        DebugConsoleTextBox.Text = line;
                    }
                }

                DebugConsoleTextBox.ScrollToEnd();
            }
        }
        catch
        {
        }
    }


    private void UpdateVersionHeader()
    {
        try
        {
            if (SettingsDashboardVersionTextBlock != null)
            {
                SettingsDashboardVersionTextBlock.Text = $"Dashboard version: v{DashboardVersion}";
            }

            if (SettingsExpectedProtocolTextBlock != null)
            {
                SettingsExpectedProtocolTextBlock.Text = $"Required ESP32 protocol: v{RequiredProtocolVersion}";
            }

            if (SettingsFirmwareVersionTextBlock != null)
            {
                string firmwareText;

                if (!_serialService.IsConnected)
                {
                    firmwareText = "Connected ESP32 firmware: Not connected";
                }
                else if (_esp32HelloReceived)
                {
                    firmwareText = $"Connected ESP32 firmware: {_espFirmwareName}, protocol v{_espProtocolVersion}";
                }
                else if (_esp32Seen)
                {
                    firmwareText = "Connected ESP32 firmware: Unknown - hello not received";
                }
                else
                {
                    firmwareText = "Connected ESP32 firmware: Waiting for hello";
                }

                SettingsFirmwareVersionTextBlock.Text = firmwareText;
            }

            if (SettingsBuildTypeTextBlock != null)
            {
#if DEBUG
                SettingsBuildTypeTextBlock.Text = "Build type: Debug";
#else
                SettingsBuildTypeTextBlock.Text = "Build type: Release";
#endif
            }

            if (SettingsActiveLogFileTextBlock != null)
            {
                SettingsActiveLogFileTextBlock.Text = $"Active log file: {_logPath}";
            }
            if (SettingsLogFolderButton != null)
            {
                SettingsLogFolderButton.Content = GetLogDirectory();
                SettingsLogFolderButton.ToolTip = "Open log folder";
            }

        }
        catch
        {
        }
    }


    private void LogStartupHeader()
    {
        Log("------------------------------------------------------------");
        Log("PC Volume Controller Dashboard started.");
        Log($"Dashboard version: v{DashboardVersion}");
        Log($"Required ESP32 protocol: v{RequiredProtocolVersion}");
#pragma warning disable CS0162 // Intentional — dead code when EncoderDebounceDisabled=false
        if (EncoderDebounceDisabled)
        {
            Log("WARNING: EncoderDebounceDisabled=true — all software debounce/coalescing/reverse-guard is bypassed. Every raw ENC event is logged with [RAW] prefix. For diagnostic use only.");
        }
        else
        {
            Log("Encoder debounce: enabled (normal operation).");
        }
#pragma warning restore CS0162
        Log($"Last remembered controller port: {(string.IsNullOrWhiteSpace(_settings.LastComPort) ? "none" : _settings.LastComPort)}");
        string[] startupPorts = GetAvailableComPorts();
        Log($"Actual COM ports at startup: {(startupPorts.Length == 0 ? "none" : string.Join(", ", startupPorts))}");
        Log($"Auto-connect on launch: {_settings.AutoConnectOnLaunch}");
        Log($"Scan all COM ports if remembered controller is missing: {_settings.ScanAllComPortsIfRememberedMissing}");
        Log($"Manual disconnect lockout: {_manualDisconnectRequested}");
        Log($"First-run wizard completed: {_settings.FirstRunWizardCompleted}");
        Log($"Safe mode: {_safeMode}");
        Log($"Active log file: {_logPath}");
        Log($"Started at: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Log("------------------------------------------------------------");
    }


    private void CleanupOldLogs()
    {
        try
        {
            string logDirectory = GetLogDirectory();

            if (!Directory.Exists(logDirectory))
            {
                Directory.CreateDirectory(logDirectory);
                return;
            }

            DateTime cutoff = DateTime.Now.Date.AddDays(-LogRetentionDays);
            int deletedCount = 0;

            foreach (string file in Directory.GetFiles(logDirectory, "dashboard-*.log", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    FileInfo info = new(file);

                    if (info.LastWriteTime.Date < cutoff)
                    {
                        info.Delete();
                        deletedCount++;
                    }
                }
                catch (Exception ex)
                {
                    Log($"Log cleanup could not delete {Path.GetFileName(file)}: {ex.Message}");
                }
            }

            Log(deletedCount == 0
                ? $"Log cleanup complete. No logs older than {LogRetentionDays} days found."
                : $"Log cleanup complete. Deleted {deletedCount} log file(s) older than {LogRetentionDays} days.");
        }
        catch (Exception ex)
        {
            Log($"Log cleanup error: {ex.Message}");
        }
    }


    private void Log(string message)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}";

        try
        {
            string? directory = Path.GetDirectoryName(_logPath);

            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            lock (_logFileLock)
            {
                File.AppendAllText(_logPath, line + Environment.NewLine);
            }
        }
        catch
        {
        }

        void UpdateUi()
        {
            if (LogTextBlock != null)
            {
                LogTextBlock.Text = $"{DateTime.Now:HH:mm:ss}  {message}  |  Log: {_logPath}";
            }
        }

        try
        {
            if (Dispatcher.CheckAccess())
            {
                UpdateUi();
            }
            else
            {
                Dispatcher.InvokeAsync(UpdateUi);
            }
        }
        catch
        {
        }
    }


    private static string GetLogDirectory()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "PcVolumeController", "logs");
    }


    private static string GetLogPath()
    {
        string timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        return Path.Combine(GetLogDirectory(), $"dashboard-{timestamp}.log");
    }

}
