using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading.Tasks;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Input;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using Forms = System.Windows.Forms;
using WpfBrush = System.Windows.Media.Brush;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfColor = System.Windows.Media.Color;
using WpfSolidColorBrush = System.Windows.Media.SolidColorBrush;
using WpfSystemColors = System.Windows.SystemColors;
using WpfDispatcherTimer = System.Windows.Threading.DispatcherTimer;

namespace PcVolumeControllerDashboard;

public partial class MainWindow : Window
{
    private const string DashboardVersion = "2.41";
    private const string RequiredProtocolVersion = "2.24";
    private const string ExpectedDeviceIdentity = "PC_VOLUME_CONTROLLER";
    private const int LogRetentionDays = 7;
    private const int ExpectedChannelCount = 6;

    // Software encoder channel remap: maps firmware encoder index (0-based) to dashboard
    // channel index (0-based).  Edit this array to correct for physical encoder wiring order
    // mismatches without reflashing the ESP32.
    // Identity mapping — firmware GPIO assignments match channel order 0-5.
    private static readonly int[] EncoderChannelRemap = { 0, 1, 2, 3, 4, 5 };

    private const int BaudRate = 115200;
    private const int BaseVolumeStepPercent = 2;
    private const int MaxEncoderSensitivityPercent = 500;
    private const int MaxVolumeStepPercent = 25;
    private const int EncoderApplyIntervalMs = 25;
    private const int EncoderReverseGuardMs = 140;
    private const int EncoderReverseConfirmEvents = 2;
    private const int EncoderMaxCoalescedDelta = 5;

    // --- DIAGNOSTIC: set true to bypass ALL software debounce/coalescing/reverse-guard.
    // Every raw ENC event is logged with timestamp, direction and inter-event interval
    // so you can see exactly what the hardware sends with no filtering in the way.
    // Normally false; flip to true temporarily for hardware analysis.
    private const bool EncoderDebounceDisabled = false;
    private const int StatePollMs = 500;
    private const int HeartbeatMs = 1000;
    private const int AudioSessionRefreshCheckMs = 2500;
    private const int ComPortRefreshMs = 1000;
    private const int DeviceMessageTimeoutMs = 5000;
    private const int HelloTimeoutMs = 12000;
    private const int AutoReconnectCooldownMs = 3000;
    private const int RejectedPortCooldownMs = 5 * 60 * 1000;
    private const int PhantomPortOpenFailCooldownMs = 15 * 1000;
    private const int DeviceChangeDebounceMs = 2200;
    private const int RememberedPortUsbSettleMs = 1200;
    private const int RememberedPortMissingBeforeScanAllMs = 30000;
    private const int UserIdleSleepMs = 10 * 60 * 1000;
    private const int DebugConsoleMaxLines = 500;
    private const int ChannelCount = 6;
    private const string StartupRegistryName = "PcVolumeControllerDashboard";
    private const string MicTargetKey = "MIC_INPUT";
    private const int DwmwaUseImmersiveDarkMode = 20;
    private static readonly JsonSerializerOptions JsonWriteOptions = new() { WriteIndented = true };
    private const int WmDeviceChange = 0x0219;
    private const int DbtDeviceArrival = 0x8000;
    private const int DbtDeviceRemoveComplete = 0x8004;
    private const int DbtDevNodesChanged = 0x0007;

    private readonly ObservableCollection<AudioTargetItem> _audioTargets = new();
    private readonly Dictionary<string, AudioTargetItem> _audioTargetCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Collections.ObjectModel.ObservableCollection<OutputDeviceItem> _outputDevices = new();
    private int _audioSessionRefreshInProgress; // used as bool via Interlocked
    private readonly ObservableCollection<ChannelMappingItem> _channels = new();
    private readonly ObservableCollection<string> _availableComPorts = new();
    private readonly object _logFileLock = new();

    private readonly SerialService _serialService = new();
    private readonly AudioService _audioService = new();
    private MMDevice? _defaultRenderDevice;
    private MMDevice? _defaultCaptureDevice;
    private System.Threading.Timer? _statePollTimer;
    private System.Threading.Timer? _heartbeatTimer;
    private System.Threading.Timer? _audioSessionRefreshTimer;
    private System.Threading.Timer? _comPortRefreshTimer;
    private Forms.NotifyIcon? _trayIcon;
    private Forms.ToolStripMenuItem? _trayProfileMenuItem;

    private readonly bool _safeMode = Environment.GetCommandLineArgs()
        .Any(arg => string.Equals(arg, "--safe", StringComparison.OrdinalIgnoreCase));
    private readonly List<string> _debugConsoleLines = new();
    private readonly int[] _hardwareEncoderCounts = new int[ExpectedChannelCount];
    private readonly bool[] _hardwareButtonSeen = new bool[ExpectedChannelCount];
    private readonly object _encoderSmoothingLock = new();
    private readonly int[] _encoderPendingDeltas = new int[ExpectedChannelCount];
    private readonly DateTime[] _encoderLastAppliedAt = new DateTime[ExpectedChannelCount];
    private readonly int[] _encoderLastDirection = new int[ExpectedChannelCount];
    private readonly int[] _encoderReverseCandidateDirection = new int[ExpectedChannelCount];
    private readonly int[] _encoderReverseCandidateCount = new int[ExpectedChannelCount];
    private readonly DateTime[] _encoderReverseCandidateStartedAt = new DateTime[ExpectedChannelCount];
    private readonly System.Threading.Timer?[] _encoderCoalesceTimers = new System.Threading.Timer?[ExpectedChannelCount];
    private readonly WpfDispatcherTimer?[] _encoderHighlightTimers = new WpfDispatcherTimer?[ExpectedChannelCount];

    // Acceleration: tracks when each channel's delta was last applied (on the UI thread) so the
    // inter-event interval can be measured for speed-scaling.
    private readonly DateTime[] _accelPrevApplyAt = new DateTime[ExpectedChannelCount];

    // Diagnostic (EncoderDebounceDisabled): tracks the arrival time of each raw ENC event
    // per channel so the inter-event interval can be logged.
    private readonly DateTime[] _encoderLastRawEventAt = new DateTime[ExpectedChannelCount];

    // Smoothing: all volumes are tracked in normalized float space (0.0–1.0) matching the
    // WASAPI API natively, which eliminates the quantisation artefacts that occur when
    // intermediate steps are rounded to integer percent.  A shared background timer fires
    // every SmoothingTickMs and applies one EMA step per active channel, converging
    // asymptotically toward the target with no fixed tick count.
    private const int SmoothingTickMs = 16;          // ~60 Hz; reliable with timeBeginPeriod(1)
    private const float SmoothingSnapThreshold = 0.002f; // 0.2 % — snap when this close
    private readonly float[] _smoothingTargetVolumes = new float[ExpectedChannelCount];
    private readonly float[] _smoothingCurrentVolumes = new float[ExpectedChannelCount];
    private readonly bool[] _smoothingActive = new bool[ExpectedChannelCount];
    private System.Threading.Timer? _smoothingTimer;

    private DashboardSettings _settings = new();
    private int _selectedChannelIndex = 0;
    private bool _perChannelSensitivitySuppressEvents;
    private bool _suppressPresetCallbacks;
    private int _lastSentVolume = -1;
    private bool _lastSentMute;
    private string _lastSentLabel = string.Empty;
    private HashSet<string> _lastAudioSessionSnapshot = new(StringComparer.OrdinalIgnoreCase);
    private string _lastStateSent = "--";
    private string _lastEspMessage = "--";
    private DateTime? _lastEspMessageTime;
    private string _espFirmwareName = "Unknown";
    private string _espProtocolVersion = "Unknown";
    private string _espChannelCount = "Unknown";
    private bool _esp32Seen;
    private bool _esp32HelloReceived;
    private DateTime? _serialConnectedAt;
    private DateTime _lastAutoReconnectAttempt = DateTime.MinValue;
    private string _activeConnectionState = "Disconnected";
    private string[] _lastKnownComPorts = Array.Empty<string>();
    private string _lastLoggedComPortSnapshot = string.Empty;
    private string _lastLoggedComPortSelection = string.Empty;
    private string _lastLoggedRememberedControllerPort = string.Empty;
    private string _lastLoggedConnectionState = string.Empty;
    private bool _reallyClose;
    private int _statePollBusy;
    private int _audioRefreshBusy;
    private int _comRefreshBusy;
    private int _deviceChangeRefreshQueued;
    private bool _manualDisconnectRequested;
    private DateTime _lastManualDisconnectAt = DateTime.MinValue;
    private bool _manualAutoReconnectSuppressionLogged;
    private bool _serialControlLinesDisabledLogged;
    private bool _controllerSleepRequested;
    private string _controllerSleepReason = string.Empty;
    private bool _sessionLocked;
    private bool _systemSuspending;
    private bool _lastUserIdleSleepState;
    private readonly Dictionary<string, DateTime> _rejectedComPorts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _phantomComPorts = new(StringComparer.OrdinalIgnoreCase);
    private DateTime? _rememberedPortMissingSince;
    private DateTime _rememberedPortReadyAfter = DateTime.MinValue;
    private DateTime _lastRememberedPortScanDelayLog = DateTime.MinValue;
    private readonly string _logPath = GetLogPath();
    private string _selectedFirmwareBinPath = string.Empty;
    private System.Threading.Timer? _fullStateSendCoalesceTimer;
    private int _fullStateSendQueued;
    private DateTime _lastSleepWakeTestCommandAt = DateTime.MinValue;
    private Slider? _activeSliderDrag;
    private bool _profileComboBoxSuppressEvents;

    // Set by LoadSettings when settings.json exists but cannot be parsed.
    // The corruption dialog is shown after the window is fully initialised
    // (via ShowPendingSettingsCorruptionDialogIfNeeded) so it has a valid owner.
    private bool _settingsWereCorrupt;
    private string? _settingsCorruptBackupRestored;

    // Volume overlay window (created lazily, reused)
    private VolumeOverlayWindow? _overlayWindow;

    // Update checker state
    private string _updateCheckerStatus = "Never checked";
    private string _updateCheckerLastChecked = "—";
    private bool _updateBannerDismissed;


    public MainWindow()
    {
        // Raise Windows multimedia timer resolution to 1 ms for reliable smooth-volume ticks.
        timeBeginPeriod(1);

        InitializeComponent();
        Title = $"PC Volume Controller Dashboard v{DashboardVersion}";

        AudioSessionsListView.ItemsSource = _audioTargets;
        ChannelTargetComboBox.ItemsSource = _audioTargets;
        ChannelMappingsListView.ItemsSource = _channels;
        OutputDevicesListView.ItemsSource = _outputDevices;

        LoadSettings();
        SetupTrayIcon();
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        _audioService.DefaultDeviceChanged += () => Dispatcher.InvokeAsync(() =>
        {
            Log("Default audio device changed — refreshing audio sessions.");
            RefreshDefaultAudioDevice();
            RefreshAudioSessions();
            RefreshAllChannelStates();
            SendAllChannelStatesToDevice();
        });
        _audioService.AudioDeviceError += msg =>
            ShowWarning("No audio output device found. Audio control is unavailable. Check your Windows sound settings.");
        _audioService.Initialise();
        RefreshDefaultAudioDevice();
        RefreshOutputDevices();

        BuildChannels();
        BuildLogicalChannelComboBox();
        ApplySettingsToUi();
        ApplyTheme();
        ApplySavedWindowSize();
        CleanupOldLogs();
        LogStartupHeader();
        UpdateVersionHeader();
        UpdateSelectedFirmwareBinDisplay();
        ApplyFirstRunWizardSettingsToUi();
        UpdateFirstRunWizardStatus();

        ForceRefreshComPorts("startup", preserveSelection: false);
        RefreshAudioSessions();
        ApplySettingsToChannels();
        SelectChannel(_settings.SelectedChannelIndex);

        _statePollTimer = new System.Threading.Timer(_ =>
        {
            QueueStatePollTick();
        }, null, StatePollMs, StatePollMs);

        _heartbeatTimer = new System.Threading.Timer(_ =>
        {
            SendPingToDevice();
        }, null, HeartbeatMs, HeartbeatMs);

        _audioSessionRefreshTimer = new System.Threading.Timer(_ =>
        {
            QueueAudioRefreshTick();
        }, null, AudioSessionRefreshCheckMs, AudioSessionRefreshCheckMs);

        _comPortRefreshTimer = new System.Threading.Timer(_ =>
        {
            QueueComPortRefreshTick();
        }, null, ComPortRefreshMs, ComPortRefreshMs);


        if (_safeMode)
        {
            SetConnectionStatus("Safe Mode - hardware auto-connect disabled", connected: false);
            Log("Safe mode enabled by --safe argument. Auto-connect, reconnect loop, and audio-control writes are disabled.");
        }

        if (!_settings.FirstRunWizardCompleted && !_safeMode)
        {
            Dispatcher.InvokeAsync(() =>
            {
                if (MainTabs != null && FirstRunWizardTab != null)
                {
                    MainTabs.SelectedItem = FirstRunWizardTab;
                }
            });
        }

        if (_settings.AutoConnectOnLaunch && !_safeMode)
        {
            Dispatcher.InvokeAsync(() =>
            {
                if (!_serialService.IsConnected)
                {
                    TryAutoReconnect(GetAvailableComPorts());
                }
            });
        }

        if (_settings.StartMinimizedToTray && _settings.MinimizeToTray)
        {
            Dispatcher.InvokeAsync(() =>
            {
                HideToTray();
                ShowTrayNotification("PC Volume Controller", "Dashboard started minimized to tray.");
            });
        }

        // Show the corruption dialog after the window is fully rendered, so it has a valid owner.
        Dispatcher.InvokeAsync(ShowPendingSettingsCorruptionDialogIfNeeded);

        // Kick off a background update check ~5 seconds after startup.
        // Delayed so it does not compete with serial connect / audio init.
        _ = Task.Delay(5000).ContinueWith(
            _ => Dispatcher.InvokeAsync(() => _ = RunUpdateCheckAsync(userInitiated: false)),
            TaskScheduler.Default);

        UpdateStatusBar();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        HwndSource? source = PresentationSource.FromVisual(this) as HwndSource;
        source?.AddHook(WndProc);

        RegisterAllHotkeys();

        ApplyTheme();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmDeviceChange)
        {
            int eventType = wParam.ToInt32();

            if (eventType == DbtDeviceArrival || eventType == DbtDeviceRemoveComplete || eventType == DbtDevNodesChanged)
            {
                QueueDebouncedDeviceChangeRefresh($"Windows device-change event 0x{eventType:X}");
            }
        }

        if (msg == WmHotKey)
        {
            HandleHotkeyEvent(wParam.ToInt32());
            handled = true;
            return IntPtr.Zero;
        }

        return IntPtr.Zero;
    }

    private void QueueDebouncedDeviceChangeRefresh(string reason)
    {
        // Windows can report the device-change event before the COM port has
        // actually reappeared. Do not clear remembered-port cooldowns here; the
        // delayed refresh will clear them only after SerialPort.GetPortNames()
        // confirms the remembered controller port is present.
        if (System.Threading.Interlocked.Exchange(ref _deviceChangeRefreshQueued, 1) == 1)
        {
            return;
        }

        Log($"{reason} received. Scheduling one debounced COM-port refresh.");
        QueueDelayedComPortRefresh("Windows device change debounced refresh");
    }

    private void QueueDelayedComPortRefresh(string reason)
    {
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            await System.Threading.Tasks.Task.Delay(DeviceChangeDebounceMs);

            try
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    // Re-check immediately before refreshing. This catches the case
                    // where the device-change burst happened before Windows finished
                    // recreating the COM port.
                    UpdateRememberedControllerPortPresence(reason);
                    UpdateComPortAndConnectionState(forceRefresh: true, reason: reason);
                });
            }
            catch
            {
            }
            finally
            {
                System.Threading.Interlocked.Exchange(ref _deviceChangeRefreshQueued, 0);
            }
        });
    }

    [DllImport("dwmapi.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    // Raise Windows multimedia timer resolution to 1 ms so System.Threading.Timer fires
    // reliably at the requested interval rather than slipping by a full 15.6 ms quantum.
    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint uPeriod);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint uPeriod);

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private const int WmHotKey      = 0x0312;
    private const int HotkeyIdBase  = 0x9000;
    private const int HotkeyIdMasterVolumeUp   = HotkeyIdBase + 0;
    private const int HotkeyIdMasterVolumeDown = HotkeyIdBase + 1;
    private const int HotkeyIdToggleMasterMute = HotkeyIdBase + 2;
    private const int HotkeyIdCycleNextProfile = HotkeyIdBase + 3;
    private const int HotkeyIdShowDashboard    = HotkeyIdBase + 4;

    private static readonly int[] AllHotkeyIds =
    {
        HotkeyIdMasterVolumeUp, HotkeyIdMasterVolumeDown,
        HotkeyIdToggleMasterMute, HotkeyIdCycleNextProfile, HotkeyIdShowDashboard
    };

    private void QueueStatePollTick()
    {
        if (System.Threading.Interlocked.Exchange(ref _statePollBusy, 1) == 1)
        {
            return;
        }

        try
        {
            Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    RefreshAllChannelStates();
                    UpdateControllerPowerStateFromPcActivity();
                    SendStateIfChanged();
                    UpdateDiagnostics();
                }
                catch (Exception ex)
                {
                    Log($"State poll error: {ex.Message}");
                }
                finally
                {
                    System.Threading.Interlocked.Exchange(ref _statePollBusy, 0);
                }
            });
        }
        catch
        {
            System.Threading.Interlocked.Exchange(ref _statePollBusy, 0);
        }
    }

    private void QueueAudioRefreshTick()
    {
        if (System.Threading.Interlocked.Exchange(ref _audioRefreshBusy, 1) == 1)
        {
            return;
        }

        try
        {
            Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    AutoRefreshAudioSessionsIfChanged();
                }
                finally
                {
                    System.Threading.Interlocked.Exchange(ref _audioRefreshBusy, 0);
                }
            });
        }
        catch
        {
            System.Threading.Interlocked.Exchange(ref _audioRefreshBusy, 0);
        }
    }

    private void QueueComPortRefreshTick()
    {
        if (System.Threading.Interlocked.Exchange(ref _comRefreshBusy, 1) == 1)
        {
            return;
        }

        // Enumerate COM ports on the background thread to avoid blocking the UI thread.
        // GetPortNames() can be slow on systems with many COM drivers.
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                string[] ports = SerialService.GetPortNames()
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(NormalizeComPortForSort)
                    .ToArray();

                Dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        RefreshComPortsIfChanged(portsHint: ports);
                    }
                    catch (Exception ex)
                    {
                        Log($"COM refresh error: {ex.Message}");
                    }
                    finally
                    {
                        System.Threading.Interlocked.Exchange(ref _comRefreshBusy, 0);
                    }
                });
            }
            catch (Exception ex)
            {
                Log($"COM port enumeration error: {ex.Message}");
                System.Threading.Interlocked.Exchange(ref _comRefreshBusy, 0);
            }
        });
    }

    private void RequestManualDisconnect(string source)
    {
        _manualDisconnectRequested = true;
        _manualAutoReconnectSuppressionLogged = false;
        _lastManualDisconnectAt = DateTime.Now;
        _lastAutoReconnectAttempt = DateTime.Now;
        _rejectedComPorts.Clear();
        _phantomComPorts.Clear();
        Log($"{source}. Auto-reconnect paused for this dashboard session until Connect/Reconnect is requested.");
        DisconnectSerial();
        SetConnectionStatus("Disconnected - manual reconnect required", connected: false);
    }

    private void RefreshPortsButton_Click(object sender, RoutedEventArgs e)
    {
        CheckConnectedPortStillExists();
        ForceRefreshComPorts("manual refresh", preserveSelection: false);
    }

    private void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_serialService.IsConnected)
        {
            RequestManualDisconnect("Manual disconnect requested");
        }
        else
        {
            _manualDisconnectRequested = false;
            _manualAutoReconnectSuppressionLogged = false;
            ConnectSerial();
        }
    }

    private void RefreshSessionsButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshAudioSessions();
        RefreshAllChannelStates();
        SendAllChannelStatesToDevice();
        SendStateToDevice(force: true);
    }

    private void RefreshOutputDevicesButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshOutputDevices();
    }

    private void OutputDeviceInclude_Changed(object sender, RoutedEventArgs e)
    {
        // Rebuild the cycle list from checked items, preserving order
        _settings.OutputDeviceCycleList = _outputDevices
            .Where(d => d.IncludeInCycle)
            .Select(d => d.DeviceId)
            .ToList();
        SaveSettings();
    }

    private void SaveOledSetupButton_Click(object sender, RoutedEventArgs e)
    {
        FlushUiToSettings();
        UpdateOledBrightnessLabel();
        UpdateOledSleepTimeoutLabel();
        UpdateOledConnectedIdleTimeoutLabel();
        UpdateOledPreviewPanels();
        SendOledSettingsToDevice(logIfNotConnected: true);
        Log("OLED setup saved.");
    }

    private void CloseAppButton_Click(object sender, RoutedEventArgs e)
    {
        ExitApplication();
    }

    private void WizardDetectControllerButton_Click(object sender, RoutedEventArgs e)
    {
        _manualDisconnectRequested = false;
        ForceRefreshComPorts("first-run wizard detect", preserveSelection: false);

        if (_serialService.IsConnected && _esp32HelloReceived)
        {
            Log("First-run wizard detect requested; controller is already connected.");
            UpdateFirstRunWizardStatus();
            return;
        }

        string[] ports = GetAvailableComPorts();
        if (ports.Length == 0)
        {
            SetConnectionStatus("Disconnected - no COM ports found", connected: false);
            Log("First-run wizard detect requested, but no usable COM ports were found.");
            UpdateFirstRunWizardStatus();
            return;
        }

        if (!string.IsNullOrWhiteSpace(_settings.LastComPort) &&
            ports.Contains(_settings.LastComPort, StringComparer.OrdinalIgnoreCase))
        {
            Log($"First-run wizard probing remembered controller port {_settings.LastComPort}.");
            ConnectSerial(_settings.LastComPort, showErrors: false, isAutoReconnect: true);
        }
        else
        {
            Log("First-run wizard scanning available COM ports for controller identity.");
            bool previousScanAll = _settings.ScanAllComPortsIfRememberedMissing;
            _settings.ScanAllComPortsIfRememberedMissing = true;
            TryAutoReconnect(ports);
            _settings.ScanAllComPortsIfRememberedMissing = previousScanAll;
        }

        UpdateFirstRunWizardStatus();
    }

    private void WizardApplyRecommendedButton_Click(object sender, RoutedEventArgs e)
    {
        if (WizardAutoConnectCheckBox != null)
        {
            WizardAutoConnectCheckBox.IsChecked = true;
        }

        if (WizardStartWithWindowsCheckBox != null)
        {
            WizardStartWithWindowsCheckBox.IsChecked = true;
        }

        if (WizardMinimizeToTrayCheckBox != null)
        {
            WizardMinimizeToTrayCheckBox.IsChecked = true;
        }

        if (WizardStartMinimizedCheckBox != null)
        {
            WizardStartMinimizedCheckBox.IsChecked = true;
        }

        if (WizardScanAllCheckBox != null)
        {
            WizardScanAllCheckBox.IsChecked = false;
        }

        WizardSaveSetupButton_Click(sender, e);
    }

    private void WizardSaveSetupButton_Click(object sender, RoutedEventArgs e)
    {
        _settings.AutoConnectOnLaunch = WizardAutoConnectCheckBox?.IsChecked == true;
        _settings.StartWithWindows = WizardStartWithWindowsCheckBox?.IsChecked == true;
        _settings.MinimizeToTray = WizardMinimizeToTrayCheckBox?.IsChecked == true;
        _settings.StartMinimizedToTray = WizardStartMinimizedCheckBox?.IsChecked == true;
        _settings.ScanAllComPortsIfRememberedMissing = WizardScanAllCheckBox?.IsChecked == true;

        ApplySettingsToUi();
        ApplyStartupSetting();
        FlushUiToSettings();
        UpdateFirstRunWizardStatus();
        Log("First-run wizard setup choices saved.");
    }

    private void WizardOpenAudioPageButton_Click(object sender, RoutedEventArgs e)
    {
        if (MainTabs != null)
        {
            MainTabs.SelectedIndex = 0;
        }
    }

    private void WizardCompleteButton_Click(object sender, RoutedEventArgs e)
    {
        _settings.FirstRunWizardCompleted = true;
        FlushUiToSettings();
        UpdateFirstRunWizardStatus();
        Log("First-run wizard marked complete.");

        if (FirstRunWizardTab != null)
        {
            FirstRunWizardTab.Visibility = Visibility.Collapsed;
        }

        if (MainTabs != null)
        {
            MainTabs.SelectedIndex = 0;
        }
    }

    private void EncoderSensitivitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (EncoderSensitivityValueTextBlock == null)
        {
            return;
        }

        int sensitivity = GetEncoderSensitivityPercentFromUi();
        EncoderSensitivityValueTextBlock.Text = $"Sensitivity: {sensitivity}%";
        _settings.EncoderSensitivityPercent = sensitivity;
    }

    private void ChannelButtonActionComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ChannelButtonActionComboBox == null || _channels.Count == 0)
        {
            return;
        }

        SelectedChannel.ButtonAction = GetSelectedChannelButtonActionFromUi();
    }

    private void ChannelLongPressActionComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ChannelLongPressActionComboBox == null || _channels.Count == 0)
        {
            return;
        }

        SelectedChannel.LongPressButtonAction = GetSelectedChannelLongPressActionFromUi();
    }

    private void ChannelDoublePressActionComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ChannelDoublePressActionComboBox == null || _channels.Count == 0)
        {
            return;
        }

        SelectedChannel.DoublePressButtonAction = GetSelectedChannelDoublePressActionFromUi();
    }

    // --- Per-channel OLED mode ---

    private void ChannelOledModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ChannelOledModeComboBox == null || _channels.Count == 0)
        {
            return;
        }

        string mode = GetChannelOledModeFromIndex(ChannelOledModeComboBox.SelectedIndex);
        SelectedChannel.OledDisplayMode = mode;
        SendChannelOledModeToDevice(_selectedChannelIndex);
        FlushUiToSettings();
    }

    // --- Profiles ---

    private void RefreshProfileUi()
    {
        if (ProfileComboBox == null)
        {
            return;
        }

        _profileComboBoxSuppressEvents = true;
        ProfileComboBox.Items.Clear();
        foreach (ProfileEntry profile in _settings.Profiles)
        {
            ProfileComboBox.Items.Add(profile.Name);
        }
        int activeIndex = _settings.Profiles.FindIndex(p => p.Name == _settings.ActiveProfileName);
        ProfileComboBox.SelectedIndex = activeIndex >= 0 ? activeIndex : 0;
        _profileComboBoxSuppressEvents = false;

        if (DeleteProfileButton != null)
        {
            DeleteProfileButton.IsEnabled = _settings.Profiles.Count > 1;
        }

        UpdateStatusBar();
        BuildTrayProfileMenu();
    }

    private void ProfileComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_profileComboBoxSuppressEvents || ProfileComboBox == null)
        {
            return;
        }

        int index = ProfileComboBox.SelectedIndex;
        if (index < 0 || index >= _settings.Profiles.Count)
        {
            return;
        }

        string newName = _settings.Profiles[index].Name;
        if (newName == _settings.ActiveProfileName)
        {
            return;
        }

        SwitchToProfile(newName);
    }

    private void CycleToNextProfile()
    {
        if (_settings.Profiles.Count < 2) return;

        int currentIndex = _settings.Profiles.FindIndex(p => p.Name == _settings.ActiveProfileName);
        int nextIndex = (currentIndex + 1) % _settings.Profiles.Count;
        string nextName = _settings.Profiles[nextIndex].Name;
        SwitchToProfile(nextName);
    }

    private void RegisterAllHotkeys()
    {
        IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        UnregisterAllHotkeys(hwnd);

        RegisterHotkeyIfAssigned(hwnd, HotkeyIdMasterVolumeUp,   _settings.Hotkeys.MasterVolumeUp);
        RegisterHotkeyIfAssigned(hwnd, HotkeyIdMasterVolumeDown, _settings.Hotkeys.MasterVolumeDown);
        RegisterHotkeyIfAssigned(hwnd, HotkeyIdToggleMasterMute, _settings.Hotkeys.ToggleMasterMute);
        RegisterHotkeyIfAssigned(hwnd, HotkeyIdCycleNextProfile, _settings.Hotkeys.CycleNextProfile);
        RegisterHotkeyIfAssigned(hwnd, HotkeyIdShowDashboard,    _settings.Hotkeys.ShowDashboard);
    }

    private static void RegisterHotkeyIfAssigned(IntPtr hwnd, int id, HotkeyBinding binding)
    {
        if (!binding.IsAssigned) return;
        RegisterHotKey(hwnd, id, (uint)binding.Modifiers, (uint)binding.VirtualKey);
    }

    private static void UnregisterAllHotkeys(IntPtr hwnd)
    {
        foreach (int id in AllHotkeyIds)
            UnregisterHotKey(hwnd, id);
    }

    private void HandleHotkeyEvent(int id)
    {
        switch (id)
        {
            case HotkeyIdMasterVolumeUp:
                try
                {
                    EnsureAudioDevice();
                    int cur = GetMasterVolumePercent();
                    int next = Math.Clamp(cur + GetVolumeStepPercent(), 0, 100);
                    _defaultRenderDevice!.AudioEndpointVolume.MasterVolumeLevelScalar = next / 100.0f;
                    if (_defaultRenderDevice.AudioEndpointVolume.Mute && next > 0)
                        _defaultRenderDevice.AudioEndpointVolume.Mute = false;
                    RefreshAllChannelStates();
                    SendAllChannelStatesToDevice();
                }
                catch (Exception ex) { Log($"Hotkey master vol up error: {ex.Message}"); }
                break;

            case HotkeyIdMasterVolumeDown:
                try
                {
                    EnsureAudioDevice();
                    int cur = GetMasterVolumePercent();
                    int next = Math.Clamp(cur - GetVolumeStepPercent(), 0, 100);
                    _defaultRenderDevice!.AudioEndpointVolume.MasterVolumeLevelScalar = next / 100.0f;
                    if (_defaultRenderDevice.AudioEndpointVolume.Mute && next > 0)
                        _defaultRenderDevice.AudioEndpointVolume.Mute = false;
                    RefreshAllChannelStates();
                    SendAllChannelStatesToDevice();
                }
                catch (Exception ex) { Log($"Hotkey master vol down error: {ex.Message}"); }
                break;

            case HotkeyIdToggleMasterMute:
                try
                {
                    EnsureAudioDevice();
                    AudioEndpointVolume epv = _defaultRenderDevice!.AudioEndpointVolume;
                    epv.Mute = !epv.Mute;
                    RefreshAllChannelStates();
                    SendAllChannelStatesToDevice();
                }
                catch (Exception ex) { Log($"Hotkey toggle mute error: {ex.Message}"); }
                break;

            case HotkeyIdCycleNextProfile:
                CycleToNextProfile();
                break;

            case HotkeyIdShowDashboard:
                RestoreFromTray();
                break;
        }
    }

    private void SwitchToProfile(string profileName)
    {
        // Persist the currently live channel state to the profile we're leaving.
        SaveChannelsToSettings();

        _settings.ActiveProfileName = profileName;
        ProfileEntry? profile = _settings.Profiles.FirstOrDefault(p => p.Name == profileName);
        if (profile == null)
        {
            return;
        }

        _settings.Channels = profile.Channels;
        ApplySettingsToChannels();
        RefreshAllChannelStates();
        SendAllChannelStatesToDevice();
        SendAllChannelOledModesToDevice();
        SaveSettings();
        Log($"Switched to profile \"{profileName}\".");
    }

    private void NewProfileButton_Click(object sender, RoutedEventArgs e)
    {
        string? name = ShowInputDialog(this, "New Profile", "Enter a name for the new profile:");
        if (name == null)
        {
            return;
        }

        name = name.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            System.Windows.MessageBox.Show(this, "Profile name cannot be empty.", "New Profile", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_settings.Profiles.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            System.Windows.MessageBox.Show(this, $"A profile named \"{name}\" already exists.", "New Profile", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Save current state to the profile we're leaving.
        SaveChannelsToSettings();

        ProfileEntry newProfile = new() { Name = name, Channels = DashboardSettings.CreateDefaultChannels() };
        _settings.Profiles.Add(newProfile);
        _settings.ActiveProfileName = name;
        _settings.Channels = newProfile.Channels;
        ApplySettingsToChannels();
        RefreshAllChannelStates();
        SendAllChannelStatesToDevice();
        SendAllChannelOledModesToDevice();
        SaveSettings();
        Log($"Created new profile \"{name}\".");
    }

    private void RenameProfileButton_Click(object sender, RoutedEventArgs e)
    {
        string current = _settings.ActiveProfileName;
        string? name = ShowInputDialog(this, "Rename Profile", "Enter a new name for this profile:", current);
        if (name == null)
        {
            return;
        }

        name = name.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            System.Windows.MessageBox.Show(this, "Profile name cannot be empty.", "Rename Profile", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.Equals(name, current, StringComparison.Ordinal))
        {
            return;
        }

        if (_settings.Profiles.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            System.Windows.MessageBox.Show(this, $"A profile named \"{name}\" already exists.", "Rename Profile", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ProfileEntry? profile = _settings.Profiles.FirstOrDefault(p => p.Name == current);
        if (profile == null)
        {
            return;
        }

        profile.Name = name;
        _settings.ActiveProfileName = name;
        RefreshProfileUi();
        SaveSettings();
        Log($"Renamed profile \"{current}\" to \"{name}\".");
    }

    private void DuplicateProfileButton_Click(object sender, RoutedEventArgs e)
    {
        string current = _settings.ActiveProfileName;

        // Auto-generate a unique default name for the duplicate.
        string baseName = current + " Copy";
        string candidateName = baseName;
        int suffix = 2;
        while (_settings.Profiles.Any(p => string.Equals(p.Name, candidateName, StringComparison.OrdinalIgnoreCase)))
        {
            candidateName = $"{baseName} {suffix++}";
        }

        string? name = ShowInputDialog(this, "Duplicate Profile", "Enter a name for the duplicate:", candidateName);
        if (name == null)
        {
            return;
        }

        name = name.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            System.Windows.MessageBox.Show(this, "Profile name cannot be empty.", "Duplicate Profile", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_settings.Profiles.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            System.Windows.MessageBox.Show(this, $"A profile named \"{name}\" already exists.", "Duplicate Profile", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Persist current state before duplicating.
        SaveChannelsToSettings();

        ProfileEntry? source = _settings.Profiles.FirstOrDefault(p => p.Name == current);
        if (source == null)
        {
            return;
        }

        ProfileEntry duplicate = new()
        {
            Name = name,
            Channels = source.Channels.Select(ch => new ChannelSettings
            {
                TargetKey = ch.TargetKey,
                FriendlyName = ch.FriendlyName,
                ButtonAction = ch.ButtonAction,
                LongPressButtonAction = ch.LongPressButtonAction,
                DoublePressButtonAction = ch.DoublePressButtonAction,
                RebindFallback = ch.RebindFallback,
                OledDisplayMode = ch.OledDisplayMode,
                SensitivityPercent = ch.SensitivityPercent
            }).ToArray()
        };

        _settings.Profiles.Add(duplicate);
        _settings.ActiveProfileName = name;
        _settings.Channels = duplicate.Channels;
        ApplySettingsToChannels();
        RefreshAllChannelStates();
        SendAllChannelStatesToDevice();
        SendAllChannelOledModesToDevice();
        SaveSettings();
        Log($"Duplicated profile \"{current}\" as \"{name}\".");
    }

    private void DeleteProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (_settings.Profiles.Count <= 1)
        {
            System.Windows.MessageBox.Show(this, "Cannot delete the last remaining profile.", "Delete Profile", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string current = _settings.ActiveProfileName;
        MessageBoxResult result = System.Windows.MessageBox.Show(this,
            $"Delete profile \"{current}\"? This cannot be undone.",
            "Delete Profile",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        int index = _settings.Profiles.FindIndex(p => p.Name == current);
        _settings.Profiles.RemoveAt(index);

        // Switch to the nearest remaining profile.
        int newIndex = Math.Min(index, _settings.Profiles.Count - 1);
        _settings.ActiveProfileName = _settings.Profiles[newIndex].Name;
        _settings.Channels = _settings.Profiles[newIndex].Channels;
        ApplySettingsToChannels();
        RefreshAllChannelStates();
        SendAllChannelStatesToDevice();
        SendAllChannelOledModesToDevice();
        SaveSettings();
        Log($"Deleted profile \"{current}\".");
    }

    private static string? ShowInputDialog(Window owner, string title, string prompt, string defaultValue = "")
    {
        var dialog = new Window
        {
            Title = title,
            Width = 380,
            Height = 162,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = owner,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false
        };

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Margin = new Thickness(16);

        var label = new TextBlock { Text = prompt, Margin = new Thickness(0, 0, 0, 8) };
        Grid.SetRow(label, 0);
        grid.Children.Add(label);

        var textBox = new System.Windows.Controls.TextBox
        {
            Text = defaultValue,
            Height = 30,
            Margin = new Thickness(0, 0, 0, 12),
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(4, 0, 4, 0)
        };
        Grid.SetRow(textBox, 1);
        grid.Children.Add(textBox);

        var buttonPanel = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right
        };
        var okButton = new System.Windows.Controls.Button { Content = "OK", Width = 80, Height = 30, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancelButton = new System.Windows.Controls.Button { Content = "Cancel", Width = 80, Height = 30, IsCancel = true };

        string? result = null;
        okButton.Click += (_, _) => { result = textBox.Text; dialog.DialogResult = true; };
        cancelButton.Click += (_, _) => { dialog.DialogResult = false; };

        buttonPanel.Children.Add(okButton);
        buttonPanel.Children.Add(cancelButton);
        Grid.SetRow(buttonPanel, 2);
        grid.Children.Add(buttonPanel);

        dialog.Content = grid;
        dialog.Loaded += (_, _) => { textBox.Focus(); textBox.SelectAll(); };

        return dialog.ShowDialog() == true ? result : null;
    }

    // --- Per-channel rebind fallback (merged into Per-Channel Controls) ---

    private void ChannelRebindComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ChannelRebindComboBox == null || _channels.Count == 0)
        {
            return;
        }

        _channels[_selectedChannelIndex].RebindFallback = ChannelRebindComboBox.SelectedIndex == 1
            ? RebindFallbacks.DoNothing
            : RebindFallbacks.ShowInactive;

        RefreshAllChannelStates();
        SendAllChannelStatesToDevice();
        FlushUiToSettings();
    }

    private void ApplyRebindSettingsToUi()
    {
        if (ChannelRebindComboBox == null || _channels.Count == 0)
        {
            return;
        }

        string fb = _channels[_selectedChannelIndex].RebindFallback;
        ChannelRebindComboBox.SelectedIndex = fb == RebindFallbacks.DoNothing ? 1 : 0;
    }

    private void PerChannelSensitivityUseGlobalCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_perChannelSensitivitySuppressEvents) return;
        bool useGlobal = PerChannelSensitivityUseGlobalCheckBox.IsChecked == true;
        PerChannelSensitivityPanel.Visibility = useGlobal ? Visibility.Collapsed : Visibility.Visible;

        if (_selectedChannelIndex >= 0 && _selectedChannelIndex < _settings.Channels.Length)
        {
            _settings.Channels[_selectedChannelIndex].SensitivityPercent = useGlobal ? -1
                : (int)Math.Round(PerChannelSensitivitySlider.Value);
        }
        FlushUiToSettings();
    }

    private void PresetVolume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressPresetCallbacks) return;
        if (sender is not System.Windows.Controls.Slider slider) return;
        object? tagObj = slider.Tag;
        if (tagObj is not string tagStr || !int.TryParse(tagStr, out int presetIndex)) return;

        int vol = (int)Math.Round(slider.Value);

        // Update label
        TextBlock? label = presetIndex switch
        {
            0 => Preset1VolumeValueTextBlock,
            1 => Preset2VolumeValueTextBlock,
            2 => Preset3VolumeValueTextBlock,
            _ => null
        };
        if (label != null) label.Text = $"{vol}%";

        // Save
        SavePreset(presetIndex, volumePercent: vol, name: null);
    }

    private void PresetName_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_suppressPresetCallbacks) return;
        if (sender is not System.Windows.Controls.TextBox tb) return;
        object? tagObj = tb.Tag;
        if (tagObj is not string tagStr || !int.TryParse(tagStr, out int presetIndex)) return;

        SavePreset(presetIndex, volumePercent: null, name: tb.Text.Trim());
    }

    private void SavePreset(int presetIndex, int? volumePercent, string? name)
    {
        int ch = _selectedChannelIndex;
        if (ch < 0 || ch >= _channels.Count) return;
        if (_settings.Channels == null || ch >= _settings.Channels.Length) return;

        VolumePreset[] presets = _settings.Channels[ch].Presets;
        if (presets == null || presetIndex >= presets.Length) return;

        if (volumePercent.HasValue) presets[presetIndex].VolumePercent = volumePercent.Value;
        if (name != null) presets[presetIndex].Name = name;

        SaveSettings();
    }

    private void PerChannelSensitivitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_perChannelSensitivitySuppressEvents) return;
        if (PerChannelSensitivityValueTextBlock == null) return;

        int value = Math.Clamp((int)Math.Round(PerChannelSensitivitySlider.Value), 0, MaxEncoderSensitivityPercent);
        PerChannelSensitivityValueTextBlock.Text = $"{value}%";

        if (PerChannelSensitivityUseGlobalCheckBox.IsChecked != true
            && _selectedChannelIndex >= 0 && _selectedChannelIndex < _settings.Channels.Length)
        {
            _settings.Channels[_selectedChannelIndex].SensitivityPercent = value;
            FlushUiToSettings();
        }
    }

    private void Slider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Slider slider)
        {
            return;
        }

        SetSliderValueFromMouse(slider, e);
        _activeSliderDrag = slider;
        slider.CaptureMouse();
        e.Handled = true;
    }

    private void Slider_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is not Slider slider || _activeSliderDrag != slider || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        SetSliderValueFromMouse(slider, e);
        e.Handled = true;
    }

    private void Slider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Slider slider)
        {
            return;
        }

        if (_activeSliderDrag == slider)
        {
            SetSliderValueFromMouse(slider, e);
            _activeSliderDrag = null;
            slider.ReleaseMouseCapture();
            e.Handled = true;
        }
    }

    private void Slider_LostMouseCapture(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_activeSliderDrag == sender)
        {
            _activeSliderDrag = null;
        }
    }

    private static void SetSliderValueFromMouse(Slider slider, System.Windows.Input.MouseEventArgs e)
    {
        System.Windows.Point clickPosition = e.GetPosition(slider);
        double trackWidth = Math.Max(1.0, slider.ActualWidth);
        double ratio = Math.Clamp(clickPosition.X / trackWidth, 0.0, 1.0);
        double newValue = slider.Minimum + (ratio * (slider.Maximum - slider.Minimum));

        if (slider.IsSnapToTickEnabled && slider.TickFrequency > 0)
        {
            newValue = Math.Round(newValue / slider.TickFrequency) * slider.TickFrequency;
        }

        slider.Value = Math.Clamp(newValue, slider.Minimum, slider.Maximum);
    }

    private void ThemeRadioButton_Checked(object sender, RoutedEventArgs e)
    {
        if (ThemeFollowSystemRadioButton == null ||
            ThemeLightRadioButton == null ||
            ThemeDarkRadioButton == null)
        {
            return;
        }

        _settings.ThemeMode = GetThemeModeFromUi();
        ApplyTheme();
    }

    private void OverlayEnabledCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (OverlayEnabledCheckBox == null) return;
        _settings.OverlayEnabled = OverlayEnabledCheckBox.IsChecked == true;
        SaveSettings();
    }

    private void OverlayPositionComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (OverlayPositionComboBox?.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            _settings.OverlayPosition = tag;
            SaveSettings();
        }
    }

    private void OverlayTimeoutSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (OverlayTimeoutValueTextBlock == null) return;
        _settings.OverlayTimeoutSeconds = OverlayTimeoutSlider.Value;
        OverlayTimeoutValueTextBlock.Text = $"{OverlayTimeoutSlider.Value:F1} s";
        SaveSettings();
    }

    private void LogicalChannelComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LogicalChannelComboBox.SelectedIndex >= 0 && LogicalChannelComboBox.SelectedIndex < ChannelCount)
        {
            SelectChannel(LogicalChannelComboBox.SelectedIndex);
        }
    }

    private void ChannelMappingsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ChannelMappingsListView.SelectedItem is ChannelMappingItem channel)
        {
            SelectChannel(channel.ChannelIndex);
        }
    }

    private void AssignChannelButton_Click(object sender, RoutedEventArgs e)
    {
        if (ChannelTargetComboBox.SelectedItem is not AudioTargetItem target)
        {
            return;
        }

        ChannelMappingItem channel = _channels[_selectedChannelIndex];

        channel.TargetKey = target.Key;
        channel.AssignedLabel = target.Label;
        channel.FriendlyName = ChannelDisplayNameTextBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(channel.FriendlyName))
        {
            channel.FriendlyName = target.Label;
        }

        channel.Status = target.IsActiveOrMaster ? "Active" : "Waiting for app";

        FlushUiToSettings();
        RefreshAudioSessions();
        RefreshAllChannelStates();
        SendAllChannelStatesToDevice();
        SendStateToDevice(force: true);

        Log($"Channel {channel.ChannelNumber} assigned to {target.Label}.");
    }

    private void ChannelDisplayNameTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        int len = ChannelDisplayNameTextBox.Text.Length;
        DisplayNameCharCountTextBlock.Text = $"{len} / 16";
        DisplayNameCharCountTextBlock.Foreground = len >= 16
            ? new WpfSolidColorBrush(WpfColor.FromRgb(0xAA, 0x1F, 0x1F))
            : (WpfSolidColorBrush)FindResource("SecondaryForeground");
    }

    private void SaveDisplayNameButton_Click(object sender, RoutedEventArgs e)
    {
        ChannelMappingItem channel = _channels[_selectedChannelIndex];

        channel.FriendlyName = ChannelDisplayNameTextBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(channel.FriendlyName))
        {
            channel.FriendlyName = channel.AssignedLabel;
        }

        FlushUiToSettings();
        RefreshAllChannelStates();
        SendAllChannelStatesToDevice();
        SendStateToDevice(force: true);

        Log($"Channel {channel.ChannelNumber} display name saved as {channel.DisplayLabel}.");
    }

    private void PreviousChannelButton_Click(object sender, RoutedEventArgs e)
    {
        SelectPreviousChannel();
    }

    private void NextChannelButton_Click(object sender, RoutedEventArgs e)
    {
        SelectNextChannel();
    }

    private void VolumeDownButton_Click(object sender, RoutedEventArgs e)
    {
        ChangeSelectedChannelVolume(-GetVolumeStepPercent());
        RefreshAllChannelStates();
        SendAllChannelStatesToDevice();
        SendStateToDevice(force: true);
    }

    private void VolumeUpButton_Click(object sender, RoutedEventArgs e)
    {
        ChangeSelectedChannelVolume(GetVolumeStepPercent());
        RefreshAllChannelStates();
        SendAllChannelStatesToDevice();
        SendStateToDevice(force: true);
    }

    private void MuteButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleSelectedChannelMute();
        RefreshAllChannelStates();
        SendAllChannelStatesToDevice();
        SendStateToDevice(force: true);
    }

    private void BuildChannels()
    {
        _channels.Clear();

        for (int i = 0; i < ChannelCount; i++)
        {
            _channels.Add(new ChannelMappingItem
            {
                ChannelIndex = i,
                ChannelNumber = i + 1,
                TargetKey = i == 0 ? "MASTER" : string.Empty,
                AssignedLabel = i == 0 ? "Master" : "Unassigned",
                FriendlyName = i == 0 ? "Master" : string.Empty,
                ButtonAction = ChannelButtonActions.ToggleAssignedMute,
                LongPressButtonAction = ChannelButtonActions.NoAction,
                DoublePressButtonAction = ChannelButtonActions.NoAction,
                Status = i == 0 ? "Active" : "Unassigned"
            });
        }
    }

    private void BuildLogicalChannelComboBox()
    {
        LogicalChannelComboBox.Items.Clear();

        for (int i = 0; i < ChannelCount; i++)
        {
            LogicalChannelComboBox.Items.Add($"Channel {i + 1}");
        }
    }

    private void SelectPreviousChannel()
    {
        int index = _selectedChannelIndex - 1;

        if (index < 0)
        {
            index = ChannelCount - 1;
        }

        SelectChannel(index);
    }

    private void SelectNextChannel()
    {
        int index = _selectedChannelIndex + 1;

        if (index >= ChannelCount)
        {
            index = 0;
        }

        SelectChannel(index);
    }

    private bool _selectingChannel;

    private void SelectChannel(int index)
    {
        if (_selectingChannel) return;

        if (index < 0 || index >= ChannelCount)
        {
            index = 0;
        }

        _selectingChannel = true;
        try
        {
        _selectedChannelIndex = index;
        _settings.SelectedChannelIndex = index;
        SaveSettings();

        if (LogicalChannelComboBox.SelectedIndex != index)
        {
            LogicalChannelComboBox.SelectedIndex = index;
        }

        if (ChannelMappingsListView.SelectedItem != _channels[index])
        {
            ChannelMappingsListView.SelectedItem = _channels[index];
        }

        ChannelMappingsListView.ScrollIntoView(_channels[index]);

        ChannelMappingItem channel = _channels[index];
        AudioTargetItem? target = FindTargetByKey(channel.TargetKey);

        if (target != null)
        {
            ChannelTargetComboBox.SelectedItem = target;
        }

        ChannelDisplayNameTextBox.Text = channel.DisplayLabel == "Unassigned" ? string.Empty : channel.DisplayLabel;

        RefreshAllChannelStates();
        UpdateSelectedChannelUi();
        SendStateToDevice(force: true);

        }
        finally
        {
            _selectingChannel = false;
        }
    }

    private ChannelMappingItem SelectedChannel => _channels[_selectedChannelIndex];

    private void RefreshComPortsIfChanged(string[]? portsHint = null)
    {
        UpdateComPortAndConnectionState(forceRefresh: false, reason: "auto refresh", portsHint: portsHint);
    }

    private void UpdateComPortAndConnectionState(bool forceRefresh = false, string reason = "auto refresh", string[]? portsHint = null)
    {
        PrunePhantomComPorts();
        UpdateRememberedControllerPortPresence(reason);

        // Use the pre-enumerated port list if one was provided (e.g. from background enumeration).
        string[] ports = portsHint != null
            ? portsHint.Where(p => !IsPortTemporarilyPhantom(p)).ToArray()
            : GetAvailableComPorts();
        string? connectedPort = _serialService.PortName;
        bool serialOpen = _serialService.IsConnected;
        bool connectedPortStillExists = serialOpen && !string.IsNullOrWhiteSpace(connectedPort) &&
            ports.Any(port => port.Equals(connectedPort, StringComparison.OrdinalIgnoreCase));

        bool portListChanged = !_lastKnownComPorts.SequenceEqual(ports, StringComparer.OrdinalIgnoreCase);
        string currentSnapshot = string.Join("|", ComPortComboBox.Items.Cast<object>().Select(item => item.ToString()));
        string newSnapshot = string.Join("|", ports);

        if (serialOpen && !connectedPortStillExists)
        {
            Log($"Connected COM port {connectedPort} is no longer available. Marking controller disconnected.");
            DisconnectSerial(sendDisconnectCommand: false, preserveLastControllerPort: true, refreshPortsAfterDisconnect: false);
            serialOpen = false;
            connectedPort = null;
            ports = GetAvailableComPorts();
            QueueDebouncedDeviceChangeRefresh("connected port disappeared delayed refresh");
        }
        else if (serialOpen && IsConnectedDeviceTimedOut())
        {
            Log("No valid ESP32 identity/heartbeat received within timeout. Marking port disconnected.");
            if (!_esp32HelloReceived)
            {
                MarkPortRejected(connectedPort, "no valid controller identity received");
            }
            DisconnectSerial(sendDisconnectCommand: false, preserveLastControllerPort: true, refreshPortsAfterDisconnect: false);
            serialOpen = false;
            connectedPort = null;
            ports = GetAvailableComPorts();
            QueueDebouncedDeviceChangeRefresh("controller timeout delayed refresh");
        }

        portListChanged = !_lastKnownComPorts.SequenceEqual(ports, StringComparer.OrdinalIgnoreCase);
        newSnapshot = string.Join("|", ports);
        bool comboNeedsRefresh = forceRefresh || portListChanged || !currentSnapshot.Equals(newSnapshot, StringComparison.OrdinalIgnoreCase);

        if (comboNeedsRefresh)
        {
            ForceRefreshComPorts(reason, preserveSelection: serialOpen, portsOverride: ports);
        }
        else if (!_serialService.IsConnected)
        {
            SetDisconnectedStatusForAvailablePorts(ports);
        }

        if (!serialOpen)
        {
            TryAutoReconnect(ports);
        }

        UpdateDiagnostics();
        UpdateVersionHeader();
    }

    private void ForceRefreshComPorts(string reason, bool preserveSelection, string[]? portsOverride = null)
    {
        string? previousSelection = ComPortComboBox.SelectedItem as string;
        string[] ports = portsOverride ?? GetAvailableComPorts();
        _lastKnownComPorts = ports;

        string? preferred = GetPreferredVisibleComPort(ports, previousSelection, preserveSelection);

        // Aggressive rebuild: the visible dropdown is a pure live view of
        // SerialPort.GetPortNames(). Do not let WPF retain a disconnected
        // port through selection, text search, or an old ItemsSource binding.
        _availableComPorts.Clear();
        foreach (string port in ports)
        {
            _availableComPorts.Add(port);
        }

        ComPortComboBox.SelectedIndex = -1;
        ComPortComboBox.SelectedItem = null;
        ComPortComboBox.SelectedValue = null;
        ComPortComboBox.Text = string.Empty;
        ComPortComboBox.ItemsSource = null;
        ComPortComboBox.Items.Clear();
        ComPortComboBox.ItemsSource = ports.ToList();

        if (!string.IsNullOrWhiteSpace(preferred) &&
            ports.Contains(preferred, StringComparer.OrdinalIgnoreCase))
        {
            string? visiblePort = ports.FirstOrDefault(port =>
                port.Equals(preferred, StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrWhiteSpace(visiblePort))
            {
                ComPortComboBox.SelectedItem = visiblePort;
            }
        }

        if (ports.Length == 0)
        {
            ComPortComboBox.SelectedIndex = -1;
            ComPortComboBox.SelectedItem = null;
            ComPortComboBox.SelectedValue = null;
            ComPortComboBox.Text = string.Empty;
        }

        if (!_serialService.IsConnected)
        {
            SetDisconnectedStatusForAvailablePorts(ports);
        }

        LogComPortRefreshIfChanged(reason, ports, ComPortComboBox.SelectedItem as string);
    }

    private void LogComPortRefreshIfChanged(string reason, string[] ports, string? selectedPort)
    {
        string portSnapshot = ports.Length == 0 ? "none" : string.Join(", ", ports);
        string rememberedPort = string.IsNullOrWhiteSpace(_settings.LastComPort) ? "none" : _settings.LastComPort;
        string selected = string.IsNullOrWhiteSpace(selectedPort) ? "none" : selectedPort;
        string phantomPorts = _phantomComPorts.Count == 0
            ? "none"
            : string.Join(", ", _phantomComPorts.Select(pair => $"{pair.Key} until {pair.Value:HH:mm:ss}"));
        string reasonLower = reason.ToLowerInvariant();

        bool forceLog = reasonLower.Contains("startup") ||
            reasonLower.Contains("manual") ||
            reasonLower.Contains("disconnect") ||
            reasonLower.Contains("device change") ||
            reasonLower.Contains("disappeared") ||
            reasonLower.Contains("error") ||
            reasonLower.Contains("phantom");

        bool changed = !portSnapshot.Equals(_lastLoggedComPortSnapshot, StringComparison.OrdinalIgnoreCase) ||
            !rememberedPort.Equals(_lastLoggedRememberedControllerPort, StringComparison.OrdinalIgnoreCase) ||
            !selected.Equals(_lastLoggedComPortSelection, StringComparison.OrdinalIgnoreCase);

        if (!forceLog && !changed)
        {
            return;
        }

        Log($"COM port refresh ({reason}): actual usable ports = {portSnapshot}; remembered controller port = {rememberedPort}; selected dropdown port = {selected}; phantom/open-failed ports = {phantomPorts}.");

        _lastLoggedComPortSnapshot = portSnapshot;
        _lastLoggedRememberedControllerPort = rememberedPort;
        _lastLoggedComPortSelection = selected;
    }

    private string? GetPreferredVisibleComPort(string[] ports, string? previousSelection, bool preserveSelection)
    {
        if (_serialService.IsConnected &&
            _esp32HelloReceived &&
            !string.IsNullOrWhiteSpace(_serialService.PortName) &&
            ports.Contains(_serialService.PortName, StringComparer.OrdinalIgnoreCase))
        {
            return _serialService.PortName;
        }

        if (preserveSelection &&
            !string.IsNullOrWhiteSpace(previousSelection) &&
            ports.Contains(previousSelection, StringComparer.OrdinalIgnoreCase))
        {
            return previousSelection;
        }

        if (!string.IsNullOrWhiteSpace(_settings.LastComPort) &&
            ports.Contains(_settings.LastComPort, StringComparer.OrdinalIgnoreCase))
        {
            return _settings.LastComPort;
        }

        return ports.Length > 0 ? ports[0] : null;
    }

    private void SetDisconnectedStatusForAvailablePorts(string[] ports)
    {
        if (ports.Length == 0)
        {
            SetConnectionStatus("Disconnected - no COM ports found", connected: false);
        }
        else if (!string.IsNullOrWhiteSpace(_settings.LastComPort) && !ports.Contains(_settings.LastComPort, StringComparer.OrdinalIgnoreCase))
        {
            SetConnectionStatus($"Disconnected - waiting for {_settings.LastComPort}", connected: false);
        }
        else
        {
            SetConnectionStatus($"Disconnected - {ports.Length} actual COM port(s) available", connected: false);
        }

        if (!_esp32Seen)
        {
            EspStatusTextBlock.Text = "No controller connected";
        }
    }

    private string[] GetAvailableComPorts()
    {
        PrunePhantomComPorts();

        return GetRawComPorts()
            .Where(port => !IsPortTemporarilyPhantom(port))
            .ToArray();
    }

    private static string[] GetRawComPorts()
    {
        return SerialService.GetPortNames()
            .Where(port => !string.IsNullOrWhiteSpace(port))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(NormalizeComPortForSort)
            .ToArray();
    }

    private static int NormalizeComPortForSort(string portName)
    {
        string digits = new(portName.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out int value) ? value : int.MaxValue;
    }

    private bool IsPortTemporarilyPhantom(string portName)
    {
        if (!_phantomComPorts.TryGetValue(portName, out DateTime retryAfter))
        {
            return false;
        }

        if (DateTime.Now >= retryAfter)
        {
            _phantomComPorts.Remove(portName);
            Log($"Phantom/open-failed COM-port cooldown expired for {portName}.");
            return false;
        }

        return true;
    }

    private void PrunePhantomComPorts()
    {
        string[] expiredPorts = _phantomComPorts
            .Where(pair => DateTime.Now >= pair.Value)
            .Select(pair => pair.Key)
            .ToArray();

        foreach (string port in expiredPorts)
        {
            _phantomComPorts.Remove(port);
            Log($"Phantom/open-failed COM-port cooldown expired for {port}.");
        }
    }

    private void MarkPortPhantom(string? portName, string reason)
    {
        if (string.IsNullOrWhiteSpace(portName))
        {
            return;
        }

        DateTime retryAfter = DateTime.Now.AddMilliseconds(PhantomPortOpenFailCooldownMs);
        _phantomComPorts[portName] = retryAfter;
        Log($"Marked {portName} as phantom/open-failed: {reason}. Will retry after {retryAfter:HH:mm:ss} ({PhantomPortOpenFailCooldownMs / 1000}s cooldown). This cooldown is cleared only after Windows reports the remembered controller port as present again.");
        ForceRefreshComPorts("phantom/open-failed port hidden", preserveSelection: false);
    }

    private void UpdateRememberedControllerPortPresence(string reason)
    {
        string? rememberedPort = string.IsNullOrWhiteSpace(_settings.LastComPort)
            ? null
            : _settings.LastComPort;

        if (string.IsNullOrWhiteSpace(rememberedPort))
        {
            _rememberedPortMissingSince = null;
            return;
        }

        bool rememberedPortIsPresent = GetRawComPorts()
            .Any(port => port.Equals(rememberedPort, StringComparison.OrdinalIgnoreCase));

        if (!rememberedPortIsPresent)
        {
            _rememberedPortMissingSince ??= DateTime.Now;
            return;
        }

        _rememberedPortMissingSince = null;

        if (_phantomComPorts.Remove(rememberedPort))
        {
            _rememberedPortReadyAfter = DateTime.Now.AddMilliseconds(RememberedPortUsbSettleMs);
            Log($"Remembered controller port {rememberedPort} is present again after {reason}. Cleared open-fail cooldown and waiting {RememberedPortUsbSettleMs}ms before opening it.");
        }
    }

    private static bool IsMissingPortOpenFailure(Exception ex)
    {
        string message = ex.Message ?? string.Empty;

        return ex is FileNotFoundException ||
            message.Contains("could not find file", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("does not exist", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("cannot find", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAccessDeniedOpenFailure(Exception ex)
    {
        string message = ex.Message ?? string.Empty;

        return ex is UnauthorizedAccessException ||
            message.Contains("access denied", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("access to the path", StringComparison.OrdinalIgnoreCase);
    }

    private void CheckConnectedPortStillExists()
    {
        UpdateComPortAndConnectionState(forceRefresh: true, reason: "connected-port check");
    }

    private bool IsConnectedDeviceTimedOut()
    {
        if (!_serialService.IsConnected)
        {
            return false;
        }

        DateTime now = DateTime.Now;

        if (_lastEspMessageTime.HasValue)
        {
            return (now - _lastEspMessageTime.Value).TotalMilliseconds > DeviceMessageTimeoutMs;
        }

        if (_serialConnectedAt.HasValue)
        {
            return (now - _serialConnectedAt.Value).TotalMilliseconds > HelloTimeoutMs;
        }

        return false;
    }

    private void TryAutoReconnect(string[] ports)
    {
        if (_safeMode)
        {
            return;
        }

        if (!_settings.AutoConnectOnLaunch)
        {
            return;
        }

        if (_manualDisconnectRequested)
        {
            SetConnectionStatus("Disconnected - manual reconnect required", connected: false);
            if (!_manualAutoReconnectSuppressionLogged)
            {
                Log("Auto-connect suppressed because the controller was manually disconnected. Use Connect/Reconnect to resume automatic reconnects.");
                _manualAutoReconnectSuppressionLogged = true;
            }
            return;
        }

        if (_serialService.IsConnected)
        {
            return;
        }

        if ((DateTime.Now - _lastAutoReconnectAttempt).TotalMilliseconds < AutoReconnectCooldownMs)
        {
            return;
        }

        PruneRejectedComPorts();
        UpdateRememberedControllerPortPresence("auto reconnect");

        List<string> candidates = new();
        string? rememberedPort = !string.IsNullOrWhiteSpace(_settings.LastComPort) ? _settings.LastComPort : null;
        bool scanAllPorts = string.IsNullOrWhiteSpace(rememberedPort) || _settings.ScanAllComPortsIfRememberedMissing;

        if (!string.IsNullOrWhiteSpace(rememberedPort))
        {
            string? matchingRememberedPort = ports.FirstOrDefault(port =>
                port.Equals(rememberedPort, StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrWhiteSpace(matchingRememberedPort))
            {
                if (DateTime.Now < _rememberedPortReadyAfter)
                {
                    SetConnectionStatus($"Disconnected - waiting for {rememberedPort} to settle", connected: false);
                    if ((DateTime.Now - _lastRememberedPortScanDelayLog).TotalSeconds >= 10)
                    {
                        Log($"Remembered controller port {rememberedPort} is present but still settling after USB re-enumeration; reconnect will retry shortly.");
                        _lastRememberedPortScanDelayLog = DateTime.Now;
                    }
                    return;
                }

                candidates.Add(matchingRememberedPort);
            }
            else
            {
                SetConnectionStatus($"Disconnected - waiting for {rememberedPort}", connected: false);

                if (!scanAllPorts)
                {
                    return;
                }

                TimeSpan missingFor = _rememberedPortMissingSince.HasValue
                    ? DateTime.Now - _rememberedPortMissingSince.Value
                    : TimeSpan.Zero;

                if (missingFor.TotalMilliseconds < RememberedPortMissingBeforeScanAllMs)
                {
                    if ((DateTime.Now - _lastRememberedPortScanDelayLog).TotalSeconds >= 10)
                    {
                        int remainingSeconds = Math.Max(1, (int)Math.Ceiling((RememberedPortMissingBeforeScanAllMs - missingFor.TotalMilliseconds) / 1000.0));
                        Log($"Remembered controller port {rememberedPort} is temporarily missing. Delaying scan-all fallback for {remainingSeconds}s to avoid probing unrelated COM ports during Windows USB churn.");
                        _lastRememberedPortScanDelayLog = DateTime.Now;
                    }
                    return;
                }
            }
        }

        if (scanAllPorts)
        {
            foreach (string port in ports)
            {
                if (!candidates.Contains(port, StringComparer.OrdinalIgnoreCase))
                {
                    candidates.Add(port);
                }
            }
        }

        string? candidate = candidates.FirstOrDefault(port => !IsPortTemporarilyRejected(port));

        if (string.IsNullOrWhiteSpace(candidate))
        {
            return;
        }

        _lastAutoReconnectAttempt = DateTime.Now;
        Log($"Auto-connect probing {candidate} for controller identity.");
        ConnectSerial(candidate, showErrors: false, isAutoReconnect: true);
    }

    private bool IsPortTemporarilyRejected(string portName)
    {
        if (!_rejectedComPorts.TryGetValue(portName, out DateTime retryAfter))
        {
            return false;
        }

        if (DateTime.Now >= retryAfter)
        {
            _rejectedComPorts.Remove(portName);
            Log($"Rejected-port cooldown expired for {portName}.");
            return false;
        }

        return true;
    }

    private void PruneRejectedComPorts()
    {
        string[] expiredPorts = _rejectedComPorts
            .Where(pair => DateTime.Now >= pair.Value)
            .Select(pair => pair.Key)
            .ToArray();

        foreach (string port in expiredPorts)
        {
            _rejectedComPorts.Remove(port);
            Log($"Rejected-port cooldown expired for {port}.");
        }
    }

    private void MarkPortRejected(string? portName, string reason)
    {
        if (string.IsNullOrWhiteSpace(portName))
        {
            return;
        }

        DateTime retryAfter = DateTime.Now.AddMilliseconds(RejectedPortCooldownMs);
        _rejectedComPorts[portName] = retryAfter;
        Log($"Rejected {portName}: {reason}. Will retry after {retryAfter:HH:mm:ss} ({RejectedPortCooldownMs / 60000} min cooldown).");
        ShowTrayNotification("Reconnect failed", $"{portName}: {reason}");
    }

    private void ConnectSerial()
    {
        string? selectedPort = ComPortComboBox.SelectedItem as string;
        ConnectSerial(selectedPort, showErrors: true, isAutoReconnect: false);
    }

    private void ConnectSerial(string? portName, bool showErrors, bool isAutoReconnect)
    {
        if (isAutoReconnect && _manualDisconnectRequested)
        {
            SetConnectionStatus("Disconnected - manual reconnect required", connected: false);
            if (!_manualAutoReconnectSuppressionLogged)
            {
                Log("Ignored queued auto-connect attempt because manual disconnect lockout is active.");
                _manualAutoReconnectSuppressionLogged = true;
            }
            return;
        }

        if (string.IsNullOrWhiteSpace(portName))
        {
            if (showErrors)
            {
                System.Windows.MessageBox.Show(
                    "Select a COM port first.",
                    "No COM Port",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }

            return;
        }

        if (!GetRawComPorts().Contains(portName, StringComparer.OrdinalIgnoreCase))
        {
            Log($"Skipped opening {portName} because Windows does not currently list that COM port.");
            if (showErrors)
            {
                System.Windows.MessageBox.Show(
                    $"{portName} is not currently available. Unplug/replug the controller or select a different COM port.",
                    "COM Port Not Available",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            else
            {
                MarkPortPhantom(portName, "port not present before open");
            }

            return;
        }

        try
        {
            if (_serialService.IsConnected)
            {
                DisconnectSerial(sendDisconnectCommand: false, preserveLastControllerPort: true, refreshPortsAfterDisconnect: false);
            }

            // Wire events before opening so we never miss the first line.
            _serialService.LineReceived  -= DispatchHandleDeviceMessage;
            _serialService.ErrorOccurred -= BeginDisconnectAfterSerialError;
            _serialService.LineReceived  += DispatchHandleDeviceMessage;
            _serialService.ErrorOccurred += BeginDisconnectAfterSerialError;

            _serialService.Open(portName, BaudRate); // throws on failure
            if (!_serialControlLinesDisabledLogged)
            {
                Log("Serial DTR/RTS kept disabled to avoid ESP32-S3 USB reset loops during connect/disconnect.");
                _serialControlLinesDisabledLogged = true;
            }

            _serialConnectedAt = DateTime.Now;
            _activeConnectionState = isAutoReconnect ? "Auto-identifying" : "Identifying";
            ConnectButton.Content = "Disconnect";
            _esp32Seen = false;
            _esp32HelloReceived = false;
            _espFirmwareName = "Unknown";
            _espProtocolVersion = "Unknown";
            _espChannelCount = "Unknown";
            _lastEspMessage = "--";
            _lastEspMessageTime = null;
            SetConnectionStatus($"Identifying controller on {portName}...", connected: false);
            EspStatusTextBlock.Text = "Waiting for controller hello...";
            UpdateVersionHeader();

            Log($"Opened {portName}; waiting for controller identity.");
            RequestHelloFromDevice();
            UpdateDiagnostics();
        }
        catch (Exception ex)
        {
            if (showErrors)
            {
                System.Windows.MessageBox.Show(
                    ex.Message,
                    "Serial Connection Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }

            Log($"Connection error on {portName}: {ex.Message}");
            DisconnectSerial(sendDisconnectCommand: false, preserveLastControllerPort: true, refreshPortsAfterDisconnect: false);

            if (IsMissingPortOpenFailure(ex) || IsAccessDeniedOpenFailure(ex))
            {
                MarkPortPhantom(portName, ex.Message);
            }
            else if (!showErrors)
            {
                MarkPortRejected(portName, $"connection error: {ex.Message}");
            }
        }
    }

    private void DisconnectSerial(bool sendDisconnectCommand = true, bool preserveLastControllerPort = true, bool refreshPortsAfterDisconnect = true)
    {
        bool wasAlreadyDisconnected = !_serialService.IsConnected &&
            !_esp32Seen &&
            !_esp32HelloReceived &&
            _activeConnectionState.Equals("Disconnected", StringComparison.OrdinalIgnoreCase);

        try
        {
            if (sendDisconnectCommand)
            {
                SendDisconnectToDevice();
            }

            _serialService.LineReceived  -= DispatchHandleDeviceMessage;
            _serialService.ErrorOccurred -= BeginDisconnectAfterSerialError;
            _serialService.Close();
        }
        catch
        {
        }

        ConnectButton.Content = "Connect";
        _activeConnectionState = "Disconnected";
        _serialConnectedAt = null;
        _controllerSleepRequested = false;
        _controllerSleepReason = string.Empty;
        SetConnectionStatus("Disconnected", connected: false);
        EspStatusTextBlock.Text = "No controller connected";

        _esp32Seen = false;
        _esp32HelloReceived = false;
        _espFirmwareName = "Unknown";
        _espProtocolVersion = "Unknown";
        _espChannelCount = "Unknown";
        _lastEspMessage = "--";
        _lastEspMessageTime = null;

        if (refreshPortsAfterDisconnect)
        {
            ForceRefreshComPorts("disconnect", preserveSelection: true);
        }
        UpdateVersionHeader();

        if (!wasAlreadyDisconnected)
        {
            Log("Disconnected.");
        }

        UpdateDiagnostics();
    }

    private void SetConnectionStatus(string text, bool connected)
    {
        ConnectionStatusTextBlock.Text = text;

        string key = connected ? "ConnectionGoodForeground" : "ConnectionBadForeground";

        if (Resources[key] is WpfBrush brush)
        {
            ConnectionStatusTextBlock.Foreground = brush;
        }

        if (!text.Equals(_lastLoggedConnectionState, StringComparison.Ordinal))
        {
            string previousState = _lastLoggedConnectionState;
            Log($"Connection state: {text}");
            _lastLoggedConnectionState = text;

            if (!string.IsNullOrWhiteSpace(previousState))
            {
                if (connected)
                {
                    ShowTrayNotification("Controller connected", text);
                }
                else if (previousState.StartsWith("Connected", StringComparison.OrdinalIgnoreCase))
                {
                    ShowTrayNotification("Controller disconnected", text);
                }
            }
        }

        if (AudioEmptyStatePanel != null)
            AudioEmptyStatePanel.Visibility = connected ? Visibility.Collapsed : Visibility.Visible;

        UpdateStatusBar();
    }

    private void UpdateStatusBar()
    {
        if (StatusBarConnectionText == null) return;

        bool connected = _serialService.IsConnected && _esp32HelloReceived;
        StatusBarConnectionText.Text = connected ? "Connected" : "Disconnected";
        StatusBarDot.Fill = connected
            ? (WpfBrush)FindResource("ConnectionGoodForeground")
            : (WpfBrush)FindResource("ConnectionBadForeground");

        StatusBarProfileText.Text = _settings.Profiles.Count > 1
            ? $"Profile: {_settings.ActiveProfileName}"
            : string.Empty;

        string fw = _espProtocolVersion != "Unknown" ? $"Firmware v{_espFirmwareName}" : string.Empty;
        StatusBarFirmwareText.Text = fw;
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        try
        {
            Dispatcher.InvokeAsync(() =>
            {
                if (e.Reason == SessionSwitchReason.SessionLock)
                {
                    _sessionLocked = true;
                    SendControllerSleep("PC_LOCKED");
                }
                else if (e.Reason == SessionSwitchReason.SessionUnlock)
                {
                    _sessionLocked = false;
                    UpdateControllerPowerStateFromPcActivity(forceEvaluate: true);
                }
            });
        }
        catch
        {
        }
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        try
        {
            Dispatcher.InvokeAsync(() =>
            {
                if (e.Mode == PowerModes.Suspend)
                {
                    _systemSuspending = true;
                    SendControllerSleep("PC_SUSPEND");
                }
                else if (e.Mode == PowerModes.Resume)
                {
                    _systemSuspending = false;
                    SendControllerWake("PC_RESUME");
                }
            });
        }
        catch
        {
        }
    }

    private void UpdateControllerPowerStateFromPcActivity(bool forceEvaluate = false)
    {
        if (!_serialService.IsConnected || !_esp32HelloReceived)
        {
            return;
        }

        bool userIdle = GetUserIdleMilliseconds() >= UserIdleSleepMs;
        string reason = _systemSuspending
            ? "PC_SUSPEND"
            : _sessionLocked
                ? "PC_LOCKED"
                : userIdle
                    ? "PC_IDLE"
                    : string.Empty;

        if (userIdle != _lastUserIdleSleepState)
        {
            _lastUserIdleSleepState = userIdle;
            Log(userIdle ? "PC user-idle sleep threshold reached." : "PC user activity detected after idle.");
        }

        if (!string.IsNullOrWhiteSpace(reason))
        {
            SendControllerSleep(reason);
        }
        else if (!userIdle && (_controllerSleepRequested || forceEvaluate))
        {
            SendControllerWake("PC_ACTIVE");
        }
    }

    private void SendControllerSleep(string reason)
    {
        if (!_serialService.IsConnected || !_esp32HelloReceived)
        {
            return;
        }

        if (_controllerSleepRequested && _controllerSleepReason.Equals(reason, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _controllerSleepRequested = true;
        _controllerSleepReason = reason;
        WriteSerialLine($"{ProtocolCommands.Sleep},{MakeProtocolSafeLabel(reason)}", logOutgoing: true);
        EspStatusTextBlock.Text = $"Connected - controller sleeping ({reason})";
        Log($"Controller sleep requested: {reason}");
        UpdateDiagnostics();
    }

    private void SendControllerWake(string reason)
    {
        if (!_serialService.IsConnected || !_esp32HelloReceived)
        {
            _controllerSleepRequested = false;
            _controllerSleepReason = string.Empty;
            return;
        }

        if (!_controllerSleepRequested && !reason.Equals("PC_RESUME", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _controllerSleepRequested = false;
        _controllerSleepReason = string.Empty;
        WriteSerialLine($"{ProtocolCommands.Wake},{MakeProtocolSafeLabel(reason)}", logOutgoing: true);
        EspStatusTextBlock.Text = $"Connected - firmware {_espProtocolVersion} ({_espFirmwareName})";
        Log($"Controller wake requested: {reason}");
        SendAllChannelStatesToDevice();
        SendStateToDevice(force: true);
        UpdateDiagnostics();
    }

    private static uint GetUserIdleMilliseconds()
    {
        LASTINPUTINFO info = new()
        {
            cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>()
        };

        if (!GetLastInputInfo(ref info))
        {
            return 0;
        }

        return unchecked((uint)Environment.TickCount - info.dwTime);
    }

    private void SendPingToDevice()
    {
        if (_serialService.IsConnected && _esp32HelloReceived)
        {
            WriteSerialLine(ProtocolCommands.Ping, logOutgoing: false);
        }
    }

    private void RequestHelloFromDevice()
    {
        WriteSerialLine(ProtocolCommands.HelloQuery, logOutgoing: false);
    }

    private void SendDisconnectToDevice()
    {
        WriteSerialLine(ProtocolCommands.Disconnect, logOutgoing: false);
    }

    private void BeginDisconnectAfterSerialError(string message)
    {
        Log(message);
        ShowWarning($"Serial connection lost: {message} Reconnection will be attempted automatically.");

        try
        {
            Dispatcher.InvokeAsync(DisconnectSerialDueToError);
        }
        catch
        {
        }
    }

    private void ShowWarning(string message)
    {
        Dispatcher.InvokeAsync(() =>
        {
            WarningBannerText.Text = message;
            WarningBanner.Visibility = Visibility.Visible;
        });
    }

    private void DismissWarningButton_Click(object sender, RoutedEventArgs e)
    {
        WarningBanner.Visibility = Visibility.Collapsed;
    }

    // ── Update checker ───────────────────────────────────────────────────────────

    private async Task RunUpdateCheckAsync(bool userInitiated)
    {
        if (userInitiated)
        {
            UpdateCheckerStatusTextBlock.Text = "Checking…";
            CheckForUpdatesButton.IsEnabled = false;
        }

        Log("Checking for updates…");
        UpdateChecker.UpdateResult result = await UpdateChecker.CheckAsync(DashboardVersion);

        _updateCheckerLastChecked = DateTime.Now.ToString("yyyy-MM-dd HH:mm");

        if (result.ErrorMessage != null)
        {
            Log($"Update check failed: {result.ErrorMessage}");
            _updateCheckerStatus = $"Check failed: {result.ErrorMessage}";
        }
        else if (result.UpdateAvailable)
        {
            Log($"Update available: v{result.LatestVersion}");
            _updateCheckerStatus = $"Update available: v{result.LatestVersion}";
            if (!_updateBannerDismissed)
                ShowUpdateBanner(result.LatestVersion, result.ReleaseUrl);
        }
        else
        {
            Log($"Dashboard is up to date (latest: v{result.LatestVersion}).");
            _updateCheckerStatus = $"Up to date (v{result.LatestVersion})";
        }

        UpdateCheckerStatusTextBlock.Text = _updateCheckerStatus;
        UpdateCheckerLastCheckedTextBlock.Text = $"Last checked: {_updateCheckerLastChecked}";

        if (userInitiated)
            CheckForUpdatesButton.IsEnabled = true;
    }

    private void ShowUpdateBanner(string version, string releaseUrl)
    {
        UpdateBannerVersionRun.Text = $"v{version}";
        UpdateBannerLink.NavigateUri = new Uri(releaseUrl);
        UpdateBanner.Visibility = Visibility.Visible;
    }

    private void DismissUpdateBanner_Click(object sender, RoutedEventArgs e)
    {
        _updateBannerDismissed = true;
        UpdateBanner.Visibility = Visibility.Collapsed;
    }

    private async void CheckForUpdatesButton_Click(object sender, RoutedEventArgs e)
    {
        _updateBannerDismissed = false; // allow the banner to re-appear on a manual check
        await RunUpdateCheckAsync(userInitiated: true);
    }

    private void UpdateBannerLink_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
        catch { /* ignore — browser unavailable */ }
        e.Handled = true;
    }

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

    private void DisconnectSerialDueToError()
    {
        DisconnectSerial(sendDisconnectCommand: false);
        ForceRefreshComPorts("disconnect/error", preserveSelection: true);
    }

    /// <summary>
    /// Marshals a LineReceived callback (ThreadPool) onto the UI thread before
    /// calling HandleDeviceMessage, which touches WPF controls.
    /// </summary>
    private void DispatchHandleDeviceMessage(string line)
        => Dispatcher.InvokeAsync(() => HandleDeviceMessage(line));

    private void HandleDeviceMessage(string line)
    {
        // Guard: discard callbacks that fire after the port has been closed.
        if (!_serialService.IsConnected) return;

        AppendDebugConsole("IN", line);
        string[] parts = line.Split(',', StringSplitOptions.TrimEntries);

        if (parts.Length < 1)
        {
            return;
        }

        string command = parts[0].ToUpperInvariant();

        // Update the watchdog timestamp for every message — including pre-HELLO firmware
        // output (I2C scan DBG lines, etc.) — so the connection is not falsely timed out
        // while the firmware is still starting up.
        _lastEspMessage = line;
        _lastEspMessageTime = DateTime.Now;

        if (!_esp32HelloReceived && command != ProtocolCommands.Hello)
        {
            if (!string.Equals(line, ProtocolCommands.Pong, StringComparison.OrdinalIgnoreCase))
            {
                Log($"Ignoring pre-identity serial data from {_serialService.PortName ?? "unknown port"}: {line}");
            }
            return;
        }

        // Normal heartbeat traffic arrives once per second. Keep it out of the log so the log remains useful.
        bool duplicateHelloAfterIdentity = command == ProtocolCommands.Hello &&
            _esp32HelloReceived &&
            _activeConnectionState.Equals("Connected", StringComparison.OrdinalIgnoreCase);

        bool suppressNormalEncoderLog = command == ProtocolCommands.EncoderTurn && !IsAdvancedDebugLoggingEnabled();

        if (!string.Equals(line, ProtocolCommands.Pong, StringComparison.OrdinalIgnoreCase) && !duplicateHelloAfterIdentity && !suppressNormalEncoderLog)
        {
            Log($"ESP32 -> PC: {line}");
        }

        switch (command)
        {
            case ProtocolCommands.Hello:
                HandleHelloMessage(parts, line);
                break;

            case ProtocolCommands.Pong:
                MarkEsp32Seen("heartbeat");
                UpdateDiagnostics();
                break;

            case ProtocolCommands.Debug:
                MarkEsp32Seen("debug");
                Log($"ESP32 debug: {string.Join(",", parts.Skip(1))}");
                break;

            case ProtocolCommands.EncoderTurn:
                MarkEsp32Seen("encoder");
                HandleEncoderMessage(parts);
                break;

            case ProtocolCommands.ButtonLegacy:
            case ProtocolCommands.ButtonShort:
                MarkEsp32Seen("button");
                RegisterHardwareButtonEvent(parts);
                if (_controllerSleepRequested)
                {
                    Log("Ignored button event while controller sleep is active.");
                    break;
                }
                if (_safeMode)
                {
                    Log("Safe mode: button event observed but audio-control action was skipped.");
                    break;
                }
                ApplyShortButtonAction(parts);
                break;

            case ProtocolCommands.ButtonLong:
                MarkEsp32Seen("button long");
                RegisterHardwareButtonEvent(parts);
                if (_controllerSleepRequested)
                {
                    Log("Ignored long-button event while controller sleep is active.");
                    break;
                }
                if (_safeMode)
                {
                    Log("Safe mode: long-button event observed but audio-control action was skipped.");
                    break;
                }
                ApplyLongButtonAction(parts);
                break;

            case ProtocolCommands.ButtonDouble:
                MarkEsp32Seen("button double");
                RegisterHardwareButtonEvent(parts);
                if (_controllerSleepRequested)
                {
                    Log("Ignored double-press event while controller sleep is active.");
                    break;
                }
                if (_safeMode)
                {
                    Log("Safe mode: double-press event observed but audio-control action was skipped.");
                    break;
                }
                ApplyDoubleButtonAction(parts);
                break;

            case ProtocolCommands.Sleeping:
                MarkEsp32Seen("sleep ack");
                if (HardwareTestStatusTextBlock != null)
                {
                    HardwareTestStatusTextBlock.Text = $"Controller sleeping: {string.Join(',', parts.Skip(1))}";
                }
                break;

            case ProtocolCommands.Awake:
                MarkEsp32Seen("wake ack");
                if (HardwareTestStatusTextBlock != null)
                {
                    HardwareTestStatusTextBlock.Text = $"Controller awake: {string.Join(',', parts.Skip(1))}";
                }
                break;

            case ProtocolCommands.OledCfgAck:
                MarkEsp32Seen("oled config ack");
                Log($"ESP32 OLED config applied: {string.Join(',', parts.Skip(1))}");
                break;

            case ProtocolCommands.OledIdleStart:
                MarkEsp32Seen("oled idle start");
                Log($"ESP32 OLED idle started: {string.Join(',', parts.Skip(1))}");
                break;

            case ProtocolCommands.OledIdleEnd:
                MarkEsp32Seen("oled idle end");
                Log($"ESP32 OLED idle ended: {string.Join(',', parts.Skip(1))}");
                break;

            case ProtocolCommands.Error:
                Log($"ESP32 error: {string.Join(',', parts.Skip(1))}");
                break;
        }

        UpdateDiagnostics();
        UpdateVersionHeader();
    }

    private void MarkEsp32Seen(string source)
    {
        _esp32Seen = true;
        _lastEspMessageTime = DateTime.Now;

        if (!_esp32HelloReceived && _serialService.IsConnected)
        {
            EspStatusTextBlock.Text = $"Connected - active, controller hello not received ({source})";
        }
    }

    private void HandleHelloMessage(string[] parts, string rawLine)
    {
        string deviceIdentity = parts.Length > 1 ? parts[1] : "Unknown";
        string protocolVersion = parts.Length > 2 ? parts[2] : "Unknown";
        string channelCount = parts.Length > 3 ? parts[3] : "Unknown";

        if (!deviceIdentity.StartsWith(ExpectedDeviceIdentity, StringComparison.OrdinalIgnoreCase))
        {
            string? badPort = _serialService.PortName;
            Log($"Rejected {badPort}: HELLO identity was '{deviceIdentity}', expected '{ExpectedDeviceIdentity}'.");
            MarkPortRejected(badPort, "wrong device identity");
            DisconnectSerial(sendDisconnectCommand: false, preserveLastControllerPort: true, refreshPortsAfterDisconnect: true);
            return;
        }

        string connectedPort = _serialService.PortName ?? "unknown port";
        bool alreadyConfirmed = _esp32HelloReceived &&
            _activeConnectionState.Equals("Connected", StringComparison.OrdinalIgnoreCase);

        _esp32Seen = true;
        _esp32HelloReceived = true;
        _espFirmwareName = deviceIdentity;
        _espProtocolVersion = protocolVersion;
        _espChannelCount = channelCount;
        _activeConnectionState = "Connected";
        _manualAutoReconnectSuppressionLogged = false;

        SetConnectionStatus($"Connected to {connectedPort}", connected: true);
        EspStatusTextBlock.Text = $"Connected - firmware {_espProtocolVersion} ({_espFirmwareName})";

        _settings.LastComPort = connectedPort;
        SaveSettings();

        if (alreadyConfirmed)
        {
            UpdateVersionHeader();
            return;
        }

        SendAllChannelStatesToDevice();
        SendStateToDevice(force: true);
        SendOledSettingsToDevice(logIfNotConnected: false);
        SendAllChannelOledModesToDevice();
        SendPingToDevice();

        UpdateVersionHeader();

        Log($"ESP32 controller confirmed on {connectedPort}. Identity {_espFirmwareName}, protocol {_espProtocolVersion}, channels {_espChannelCount}.");
    }

    private void RefreshDefaultAudioDevice()
    {
        _audioService.RefreshDefaultDevice();
        _defaultRenderDevice  = _audioService.RenderDevice;
        _defaultCaptureDevice = _audioService.CaptureDevice;

        if (_defaultRenderDevice != null)
        {
            Log($"Default output device: {_defaultRenderDevice.FriendlyName}");
            // Clear any prior audio warning if we now have a device
            Dispatcher.InvokeAsync(() => { if (WarningBanner.Visibility == Visibility.Visible && WarningBannerText.Text.StartsWith("No audio")) WarningBanner.Visibility = Visibility.Collapsed; });
        }
    }

    private void RefreshOutputDevices()
    {
        Task.Run(() =>
        {
            try
            {
                using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
                NAudio.CoreAudioApi.MMDeviceCollection devices = enumerator.EnumerateAudioEndPoints(
                    NAudio.CoreAudioApi.DataFlow.Render,
                    NAudio.CoreAudioApi.DeviceState.Active);

                string? defaultId = null;
                try
                {
                    using var defaultDevice = enumerator.GetDefaultAudioEndpoint(
                        NAudio.CoreAudioApi.DataFlow.Render,
                        NAudio.CoreAudioApi.Role.Multimedia);
                    defaultId = defaultDevice.ID;
                }
                catch { /* no default device */ }

                Dispatcher.InvokeAsync(() =>
                {
                    _outputDevices.Clear();
                    for (int i = 0; i < devices.Count; i++)
                    {
                        var dev = devices[i];
                        bool inCycle = _settings.OutputDeviceCycleList.Contains(dev.ID, StringComparer.OrdinalIgnoreCase);
                        _outputDevices.Add(new OutputDeviceItem
                        {
                            DeviceId = dev.ID,
                            FriendlyName = dev.FriendlyName,
                            IsDefault = string.Equals(dev.ID, defaultId, StringComparison.OrdinalIgnoreCase),
                            IncludeInCycle = inCycle
                        });
                    }
                });
            }
            catch (Exception ex)
            {
                Log($"RefreshOutputDevices error: {ex.Message}");
            }
        });
    }

    private void CycleOutputDevice()
    {
        Task.Run(() =>
        {
            try
            {
                var cycleOrder = _settings.OutputDeviceCycleList;
                if (cycleOrder.Count < 2)
                {
                    Log("CycleOutputDevice: fewer than 2 devices in cycle list — nothing to do.");
                    return;
                }

                using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
                string? currentDefaultId = null;
                try
                {
                    using var def = enumerator.GetDefaultAudioEndpoint(
                        NAudio.CoreAudioApi.DataFlow.Render,
                        NAudio.CoreAudioApi.Role.Multimedia);
                    currentDefaultId = def.ID;
                }
                catch { }

                int currentIndex = cycleOrder.FindIndex(id => string.Equals(id, currentDefaultId, StringComparison.OrdinalIgnoreCase));
                int nextIndex = (currentIndex + 1) % cycleOrder.Count;
                string nextDeviceId = cycleOrder[nextIndex];

                NAudio.CoreAudioApi.MMDevice? nextDevice = null;
                try
                {
                    var activeDevices = enumerator.EnumerateAudioEndPoints(
                        NAudio.CoreAudioApi.DataFlow.Render,
                        NAudio.CoreAudioApi.DeviceState.Active);
                    for (int i = 0; i < activeDevices.Count; i++)
                    {
                        if (string.Equals(activeDevices[i].ID, nextDeviceId, StringComparison.OrdinalIgnoreCase))
                        {
                            nextDevice = activeDevices[i];
                            break;
                        }
                    }
                }
                catch { }

                if (nextDevice == null)
                {
                    Log($"CycleOutputDevice: next device '{nextDeviceId}' not found among active devices.");
                    return;
                }

                try
                {
                    var policy = (IPolicyConfig)new PolicyConfigClient();
                    policy.SetDefaultEndpoint(nextDevice.ID, NAudio.CoreAudioApi.Role.Console);
                    policy.SetDefaultEndpoint(nextDevice.ID, NAudio.CoreAudioApi.Role.Multimedia);
                    policy.SetDefaultEndpoint(nextDevice.ID, NAudio.CoreAudioApi.Role.Communications);
                    Log($"CycleOutputDevice: switched to '{nextDevice.FriendlyName}'.");

                    string shortName = nextDevice.FriendlyName.Length > 24
                        ? nextDevice.FriendlyName[..24] + "…"
                        : nextDevice.FriendlyName;

                    Dispatcher.InvokeAsync(() =>
                    {
                        if (_overlayWindow == null || !_overlayWindow.IsLoaded)
                            _overlayWindow = new VolumeOverlayWindow();

                        PositionOverlayWindow();

                        bool isDark = _settings.ThemeMode == ThemeModes.Dark ||
                                      (_settings.ThemeMode == ThemeModes.FollowSystem && !IsWindowsUsingLightTheme());

                        _overlayWindow.ShowOverlay($"▶  {shortName}", -1, _settings.OverlayTimeoutSeconds, isDark);

                        RefreshOutputDevices();
                        RefreshDefaultAudioDevice();
                    });
                }
                catch (Exception ex)
                {
                    Log($"CycleOutputDevice: failed to set default endpoint: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                Log($"CycleOutputDevice error: {ex.Message}");
            }
        });
    }

    private void RefreshAudioSessions()
    {
        if (System.Threading.Interlocked.Exchange(ref _audioSessionRefreshInProgress, 1) == 1)
        {
            Log("RefreshAudioSessions: skipped (already in progress).");
            return;
        }
        try
        {
            EnsureAudioDevice();

            _audioTargets.Clear();
            _audioTargets.Add(AudioTargetItem.CreateMaster());
            if (_defaultCaptureDevice != null)
                _audioTargets.Add(AudioTargetItem.CreateMic());

            List<AudioSessionControl> sessions = _audioService.GetActiveSessions();

            foreach (AudioSessionControl session in sessions)
            {
                AudioTargetItem? target = TryCreateAudioTargetFromSession(session);

                if (target == null)
                {
                    continue;
                }

                if (_audioTargets.Any(t => t.Key.Equals(target.Key, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                _audioTargets.Add(target);
            }

            EnsureSavedTargetsAppearInTargetList();
            RefreshChannelAssignmentLabels();

            ChannelTargetComboBox.Items.Refresh();
            AudioSessionsListView.Items.Refresh();

            _lastAudioSessionSnapshot = GetAudioSessionSnapshot();

            // Rebuild the lookup cache so FindTargetByKey() is O(1).
            _audioTargetCache.Clear();
            foreach (AudioTargetItem cacheTarget in _audioTargets)
                _audioTargetCache[cacheTarget.Key] = cacheTarget;

            Log($"Loaded {_audioTargets.Count} target(s).");
        }
        catch (Exception ex)
        {
            Log($"Session refresh error: {ex.Message}");
        }
        finally
        {
            System.Threading.Interlocked.Exchange(ref _audioSessionRefreshInProgress, 0);
        }
    }

    private void EnsureSavedTargetsAppearInTargetList()
    {
        string[] savedKeys = _settings.Channels
            .Select(channel => channel.TargetKey)
            .Concat(_channels.Select(channel => channel.TargetKey))
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Where(key => !key.Equals("MASTER", StringComparison.OrdinalIgnoreCase))
            .Where(key => !key.Equals("MIC_INPUT", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (string key in savedKeys)
        {
            bool alreadyExists = _audioTargets.Any(target =>
                target.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

            if (alreadyExists)
            {
                continue;
            }

            string label = MakeDisplayLabelFromTargetKey(key);
            string processName = MakeProcessNameFromTargetKey(key);

            _audioTargets.Add(new AudioTargetItem
            {
                Key = key,
                Label = label,
                ProcessName = processName,
                ProcessId = 0,
                Session = null,
                Volume = 0,
                Muted = true,
                State = "Waiting for app",
                IsMaster = false
            });
        }
    }

    private void AutoRefreshAudioSessionsIfChanged()
    {
        try
        {
            HashSet<string> current = GetCurrentAudioSessionSnapshot();

            if (current.SetEquals(_lastAudioSessionSnapshot))
            {
                return;
            }

            _lastAudioSessionSnapshot = current;

            RefreshAudioSessions();
            RefreshAllChannelStates();
            SendAllChannelStatesToDevice();
            SendStateToDevice(force: true);

            Log("Audio sessions changed. Refreshed session list.");
        }
        catch (Exception ex)
        {
            Log($"Audio session auto-refresh error: {ex.Message}");
        }
    }

    private HashSet<string> GetCurrentAudioSessionSnapshot()
    {
        EnsureAudioDevice();

        List<AudioSessionControl> sessions = _audioService.GetActiveSessions();

        HashSet<string> keys = new(StringComparer.OrdinalIgnoreCase) { "MASTER" };

        foreach (AudioSessionControl session in sessions)
        {
            AudioTargetItem? target = TryCreateAudioTargetFromSession(session);

            if (target != null)
            {
                keys.Add(target.Key);
            }
        }

        foreach (ChannelSettings channel in _settings.Channels)
        {
            if (!string.IsNullOrWhiteSpace(channel.TargetKey))
            {
                keys.Add(channel.TargetKey);
            }
        }

        return keys;
    }

    private HashSet<string> GetAudioSessionSnapshot()
    {
        return _audioTargets
            .Select(t => t.Key)
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private void RefreshAllChannelStates()
    {
        try
        {
        foreach (ChannelMappingItem channel in _channels)
        {
            AudioTargetItem? target = FindTargetByKey(channel.TargetKey);

            if (target == null)
            {
                channel.Volume = 0;
                channel.Muted = true;
                bool hasKey = !string.IsNullOrWhiteSpace(channel.TargetKey);
                channel.IsAppOffline = hasKey;
                if (!hasKey)
                {
                    channel.Status = "Unassigned";
                }
                else if (channel.TargetKey!.StartsWith("PROC:", StringComparison.OrdinalIgnoreCase))
                {
                    channel.Status = channel.RebindFallback == RebindFallbacks.ShowInactive
                        ? "App offline"
                        : "Waiting";
                }
                else
                {
                    channel.Status = "Target unavailable";
                }
                continue;
            }

            if (target.IsMaster)
            {
                channel.Volume = GetMasterVolumePercent();
                channel.Muted = GetMasterMute();
                channel.Status = "Active";
                channel.IsAppOffline = false;
            }
            else if (target.IsMicInput)
            {
                if (_defaultCaptureDevice != null)
                {
                    channel.Volume = Math.Clamp((int)Math.Round(_defaultCaptureDevice.AudioEndpointVolume.MasterVolumeLevelScalar * 100), 0, 100);
                    channel.Muted = _defaultCaptureDevice.AudioEndpointVolume.Mute;
                    channel.Status = "Active";
                    channel.IsAppOffline = false;
                }
                else
                {
                    channel.Volume = 0;
                    channel.Muted = false;
                    channel.Status = "No microphone";
                    channel.IsAppOffline = true;
                }
                continue;
            }
            else
            {
                var sessions = FindSessionsForKey(target.Key).ToList();

                if (sessions.Count == 0)
                {
                    channel.Volume = 0;
                    channel.Muted = true;
                    channel.IsAppOffline = true;
                    channel.Status = channel.RebindFallback == RebindFallbacks.ShowInactive
                        ? "App offline"
                        : "Waiting";
                }
                else
                {
                    channel.Volume = sessions[0].Volume;
                    channel.Muted = sessions[0].Muted;
                    channel.Status = sessions.Count == 1 ? "Active" : $"Active x{sessions.Count}";
                    channel.IsAppOffline = false;
                }
            }
        }

        ChannelMappingsListView.Items.Refresh();
        UpdateSelectedChannelUi();
        UpdateOledPreviewPanels();
        }
        catch (System.Runtime.InteropServices.COMException comEx)
        {
            Log($"Audio session expired in RefreshAllChannelStates (HRESULT 0x{comEx.HResult:X8}) — scheduling refresh.");
            Dispatcher.InvokeAsync(() =>
            {
                RefreshAudioSessions();
                RefreshAllChannelStates();
                SendAllChannelStatesToDevice();
            });
        }
        catch (Exception ex)
        {
            Log($"RefreshAllChannelStates error: {ex.Message}");
        }
    }

    private void ChangeSelectedChannelVolume(int deltaPercent)
    {
        ChangeChannelVolumeWithComHandling(_selectedChannelIndex, deltaPercent);
    }

    private void ChangeChannelVolume(int channelIndex, int deltaPercent)
    {
        if (channelIndex < 0 || channelIndex >= _channels.Count)
        {
            return;
        }

        ChannelMappingItem channel = _channels[channelIndex];
        AudioTargetItem? target = FindTargetByKey(channel.TargetKey);

        if (target == null)
        {
            Log($"Channel {channel.ChannelNumber} is not assigned or target is not active.");
            return;
        }

        if (target.IsMaster)
        {
            EnsureAudioDevice();
            int current = GetMasterVolumePercent();
            int next = Math.Clamp(current + deltaPercent, 0, 100);

            _defaultRenderDevice!.AudioEndpointVolume.MasterVolumeLevelScalar = next / 100.0f;

            if (_defaultRenderDevice.AudioEndpointVolume.Mute && next > 0)
            {
                _defaultRenderDevice.AudioEndpointVolume.Mute = false;
            }

            if (IsAdvancedDebugLoggingEnabled())
            {
                Log($"Channel {channel.ChannelNumber} / Master: {next}%");
            }
            return;
        }

        if (target.IsMicInput)
        {
            if (_defaultCaptureDevice == null) return;
            int current = Math.Clamp((int)Math.Round(_defaultCaptureDevice.AudioEndpointVolume.MasterVolumeLevelScalar * 100), 0, 100);
            int next = Math.Clamp(current + deltaPercent, 0, 100);
            _defaultCaptureDevice.AudioEndpointVolume.MasterVolumeLevelScalar = next / 100.0f;
            if (_defaultCaptureDevice.AudioEndpointVolume.Mute && next > 0)
                _defaultCaptureDevice.AudioEndpointVolume.Mute = false;
            if (IsAdvancedDebugLoggingEnabled())
                Log($"Channel {channel.ChannelNumber} / Mic Input: {next}%");
            return;
        }

        var sessions = FindSessionsForKey(target.Key).ToList();

        if (sessions.Count == 0)
        {
            Log($"No active audio session for {target.Label}.");
            return;
        }

        foreach (AudioTargetItem sessionTarget in sessions)
        {
            if (sessionTarget.Session?.SimpleAudioVolume == null)
            {
                continue;
            }

            SimpleAudioVolume volume = sessionTarget.Session.SimpleAudioVolume;

            int current = Math.Clamp((int)Math.Round(volume.Volume * 100), 0, 100);
            int next = Math.Clamp(current + deltaPercent, 0, 100);

            volume.Volume = next / 100.0f;

            if (volume.Mute && next > 0)
            {
                volume.Mute = false;
            }
        }

        if (IsAdvancedDebugLoggingEnabled())
        {
            Log($"Channel {channel.ChannelNumber} / {target.Label}: volume changed.");
        }
    }

    private void ChangeChannelVolumeWithComHandling(int channelIndex, int deltaPercent)
    {
        try
        {
            ChangeChannelVolume(channelIndex, deltaPercent);
        }
        catch (System.Runtime.InteropServices.COMException comEx)
        {
            Log($"Audio session expired in ChangeChannelVolume (HRESULT 0x{comEx.HResult:X8}) — scheduling refresh.");
            Dispatcher.InvokeAsync(() =>
            {
                RefreshAudioSessions();
                RefreshAllChannelStates();
                SendAllChannelStatesToDevice();
            });
        }
        catch (Exception ex)
        {
            Log($"ChangeChannelVolume error: {ex.Message}");
        }
    }

    private void ToggleSelectedChannelMute()
    {
        ToggleChannelMute(_selectedChannelIndex);
    }

    private void ToggleChannelMute(int channelIndex)
    {
        if (channelIndex < 0 || channelIndex >= _channels.Count)
        {
            return;
        }

        try
        {
        ChannelMappingItem channel = _channels[channelIndex];
        AudioTargetItem? target = FindTargetByKey(channel.TargetKey);

        if (target == null)
        {
            Log($"Channel {channel.ChannelNumber} is not assigned or target is not active.");
            return;
        }

        if (target.IsMaster)
        {
            AudioEndpointVolume endpointVolume = _defaultRenderDevice!.AudioEndpointVolume;
            endpointVolume.Mute = !endpointVolume.Mute;
            Log(endpointVolume.Mute ? "Master muted" : "Master unmuted");
            return;
        }

        if (target.IsMicInput)
        {
            if (_defaultCaptureDevice == null) return;
            AudioEndpointVolume epv = _defaultCaptureDevice.AudioEndpointVolume;
            epv.Mute = !epv.Mute;
            Log(epv.Mute ? "Mic input muted" : "Mic input unmuted");
            return;
        }

        var sessions = FindSessionsForKey(target.Key).ToList();

        if (sessions.Count == 0)
        {
            Log($"No active audio session for {target.Label}.");
            return;
        }

        bool nextMute = !sessions[0].Muted;

        foreach (AudioTargetItem sessionTarget in sessions)
        {
            if (sessionTarget.Session?.SimpleAudioVolume != null)
            {
                sessionTarget.Session.SimpleAudioVolume.Mute = nextMute;
            }
        }

        Log(nextMute ? $"{target.Label} muted" : $"{target.Label} unmuted");
        }
        catch (System.Runtime.InteropServices.COMException comEx)
        {
            Log($"Audio session expired in ToggleChannelMute (HRESULT 0x{comEx.HResult:X8}) — scheduling refresh.");
            Dispatcher.InvokeAsync(() =>
            {
                RefreshAudioSessions();
                RefreshAllChannelStates();
                SendAllChannelStatesToDevice();
            });
        }
        catch (Exception ex)
        {
            Log($"ToggleChannelMute error: {ex.Message}");
        }
    }

    private void ApplyShortButtonAction(string[] parts)
    {
        if (_channels.Count == 0) return;
        int channelIndex = _selectedChannelIndex;

        if (parts.Length > 1 && int.TryParse(parts[1], out int parsedFirmwareChannel) && parsedFirmwareChannel >= 0 && parsedFirmwareChannel < ExpectedChannelCount)
        {
            channelIndex = RemapEncoderChannel(parsedFirmwareChannel);
        }

        string action = _channels[channelIndex].ButtonAction;

        switch (action)
        {
            case ChannelButtonActions.ToggleAssignedMute:
                ToggleChannelMute(channelIndex);
                RefreshAllChannelStates();
                SendAllChannelStatesToDevice();
                SendStateToDevice(force: true);
                break;

            case ChannelButtonActions.NoAction:
                Log($"Channel {channelIndex + 1} short-press action is set to No action.");
                break;

            case ChannelButtonActions.CycleNextProfile:
                CycleToNextProfile();
                break;

            case ChannelButtonActions.CycleOutputDevice:
                CycleOutputDevice();
                break;

            case ChannelButtonActions.ApplyPreset1:
                ApplyVolumePreset(channelIndex, 0);
                break;
            case ChannelButtonActions.ApplyPreset2:
                ApplyVolumePreset(channelIndex, 1);
                break;
            case ChannelButtonActions.ApplyPreset3:
                ApplyVolumePreset(channelIndex, 2);
                break;

            case ChannelButtonActions.SelectNextChannel:
            default:
                SelectNextChannel();
                break;
        }
    }

    private void ApplyLongButtonAction(string[] parts)
    {
        if (_channels.Count == 0) return;
        int channelIndex = _selectedChannelIndex;

        if (parts.Length > 1 && int.TryParse(parts[1], out int parsedFirmwareChannel) && parsedFirmwareChannel >= 0 && parsedFirmwareChannel < ExpectedChannelCount)
        {
            channelIndex = RemapEncoderChannel(parsedFirmwareChannel);
        }

        string action = _channels[channelIndex].LongPressButtonAction;

        switch (action)
        {
            case ChannelButtonActions.ToggleAssignedMute:
                ToggleChannelMute(channelIndex);
                RefreshAllChannelStates();
                SendAllChannelStatesToDevice();
                SendStateToDevice(force: true);
                break;

            case ChannelButtonActions.NoAction:
                Log($"Channel {channelIndex + 1} long-press action is set to No action.");
                break;

            case ChannelButtonActions.CycleNextProfile:
                CycleToNextProfile();
                break;

            case ChannelButtonActions.CycleOutputDevice:
                CycleOutputDevice();
                break;

            case ChannelButtonActions.ApplyPreset1:
                ApplyVolumePreset(channelIndex, 0);
                break;
            case ChannelButtonActions.ApplyPreset2:
                ApplyVolumePreset(channelIndex, 1);
                break;
            case ChannelButtonActions.ApplyPreset3:
                ApplyVolumePreset(channelIndex, 2);
                break;

            default:
                ToggleChannelMute(channelIndex);
                RefreshAllChannelStates();
                SendAllChannelStatesToDevice();
                SendStateToDevice(force: true);
                break;
        }
    }

    private void ApplyDoubleButtonAction(string[] parts)
    {
        if (_channels.Count == 0) return;
        int channelIndex = _selectedChannelIndex;

        if (parts.Length > 1 && int.TryParse(parts[1], out int parsedFirmwareChannel) && parsedFirmwareChannel >= 0 && parsedFirmwareChannel < ExpectedChannelCount)
        {
            channelIndex = RemapEncoderChannel(parsedFirmwareChannel);
        }

        string action = _channels[channelIndex].DoublePressButtonAction;

        switch (action)
        {
            case ChannelButtonActions.ToggleAssignedMute:
                ToggleChannelMute(channelIndex);
                RefreshAllChannelStates();
                SendAllChannelStatesToDevice();
                SendStateToDevice(force: true);
                break;

            case ChannelButtonActions.NoAction:
                Log($"Channel {channelIndex + 1} double-press action is set to No action.");
                break;

            case ChannelButtonActions.CycleNextProfile:
                CycleToNextProfile();
                break;

            case ChannelButtonActions.CycleOutputDevice:
                CycleOutputDevice();
                break;

            case ChannelButtonActions.ApplyPreset1:
                ApplyVolumePreset(channelIndex, 0);
                break;
            case ChannelButtonActions.ApplyPreset2:
                ApplyVolumePreset(channelIndex, 1);
                break;
            case ChannelButtonActions.ApplyPreset3:
                ApplyVolumePreset(channelIndex, 2);
                break;

            default:
                Log($"Channel {channelIndex + 1} double-press action is set to No action.");
                break;
        }
    }

    private void ApplyVolumePreset(int channelIndex, int presetIndex)
    {
        if (channelIndex < 0 || channelIndex >= _channels.Count) return;
        if (_settings.Channels == null || channelIndex >= _settings.Channels.Length) return;

        VolumePreset[] presets = _settings.Channels[channelIndex].Presets;
        if (presets == null || presetIndex >= presets.Length) return;

        VolumePreset preset = presets[presetIndex];
        float normalised = Math.Clamp(preset.VolumePercent / 100f, 0f, 1f);

        if (_settings.VolumeSmoothingEnabled)
        {
            lock (_encoderSmoothingLock)
            {
                _smoothingTargetVolumes[channelIndex] = normalised;
                _smoothingActive[channelIndex] = true;
            }
            EnsureSmoothingTimerRunning();
        }
        else
        {
            SetChannelVolumeAbsolute(channelIndex, normalised);
            if (channelIndex < _channels.Count)
                _channels[channelIndex].Volume = preset.VolumePercent;
        }

        // Show overlay if enabled
        ShowVolumeOverlay(channelIndex, preset.VolumePercent);

        string presetName = string.IsNullOrWhiteSpace(preset.Name) ? $"Preset {presetIndex + 1}" : preset.Name;
        Log($"Channel {channelIndex + 1}: applied {presetName} ({preset.VolumePercent}%).");
    }

    private void UpdateSelectedChannelUi()
    {
        ChannelMappingItem channel = SelectedChannel;

        SelectedTargetTextBlock.Text = $"Channel {channel.ChannelNumber} - {channel.DisplayLabel}";
        SelectedTargetDetailTextBlock.Text = $"{channel.AssignedLabel} / {channel.Status}";
        SelectedVolumeProgressBar.Value = channel.Volume;
        SelectedVolumeTextBlock.Text = $"{channel.Volume}%";
        SelectedMuteTextBlock.Text = channel.Muted ? "Mute: Yes" : "Mute: No";

        if (ChannelButtonActionComboBox != null)
        {
            ChannelButtonActionComboBox.SelectedIndex = GetButtonActionIndex(channel.ButtonAction);
        }

        if (ChannelLongPressActionComboBox != null)
        {
            ChannelLongPressActionComboBox.SelectedIndex = GetLongPressActionIndex(channel.LongPressButtonAction);
        }

        if (ChannelDoublePressActionComboBox != null)
        {
            ChannelDoublePressActionComboBox.SelectedIndex = GetDoublePressActionIndex(channel.DoublePressButtonAction);
        }

        if (ChannelOledModeComboBox != null)
        {
            ChannelOledModeComboBox.SelectedIndex = GetChannelOledModeIndex(channel.OledDisplayMode);
        }

        if (ChannelRebindComboBox != null)
        {
            ChannelRebindComboBox.SelectedIndex = channel.RebindFallback == RebindFallbacks.DoNothing ? 1 : 0;
        }

        // Per-channel sensitivity
        _perChannelSensitivitySuppressEvents = true;
        try
        {
            int index = _selectedChannelIndex;
            if (PerChannelSensitivityUseGlobalCheckBox != null
                && index >= 0 && index < _settings.Channels.Length)
            {
                int chSens = _settings.Channels[index].SensitivityPercent;
                bool useGlobal = chSens < 0;
                PerChannelSensitivityUseGlobalCheckBox.IsChecked = useGlobal;
                PerChannelSensitivityPanel.Visibility = useGlobal ? Visibility.Collapsed : Visibility.Visible;
                if (!useGlobal)
                {
                    PerChannelSensitivitySlider.Value = Math.Clamp(chSens, 0, MaxEncoderSensitivityPercent);
                    PerChannelSensitivityValueTextBlock.Text = $"{Math.Clamp(chSens, 0, MaxEncoderSensitivityPercent)}%";
                }
                else
                {
                    // Show slider at global value when in "use global" mode (informational, slider is hidden anyway)
                    PerChannelSensitivitySlider.Value = Math.Clamp(_settings.EncoderSensitivityPercent, 0, MaxEncoderSensitivityPercent);
                }
            }
        }
        finally
        {
            _perChannelSensitivitySuppressEvents = false;
        }

        // Populate volume presets
        _suppressPresetCallbacks = true;
        try
        {
            int index = _selectedChannelIndex;
            if (_settings.Channels != null && index < _settings.Channels.Length)
            {
                VolumePreset[] presets = _settings.Channels[index].Presets;
                if (presets != null && presets.Length >= 3)
                {
                    Preset1NameTextBox.Text = presets[0].Name;
                    Preset1VolumeSlider.Value = presets[0].VolumePercent;
                    Preset1VolumeValueTextBlock.Text = $"{presets[0].VolumePercent}%";

                    Preset2NameTextBox.Text = presets[1].Name;
                    Preset2VolumeSlider.Value = presets[1].VolumePercent;
                    Preset2VolumeValueTextBlock.Text = $"{presets[1].VolumePercent}%";

                    Preset3NameTextBox.Text = presets[2].Name;
                    Preset3VolumeSlider.Value = presets[2].VolumePercent;
                    Preset3VolumeValueTextBlock.Text = $"{presets[2].VolumePercent}%";
                }
            }
        }
        finally
        {
            _suppressPresetCallbacks = false;
        }
    }

    private void SendStateIfChanged()
    {
        ChannelMappingItem channel = SelectedChannel;

        string label = MakeProtocolSafeLabel(channel.DisplayLabel);
        int volume = channel.Volume;
        bool muted = channel.Muted;

        if (label != _lastSentLabel || volume != _lastSentVolume || muted != _lastSentMute)
        {
            SendAllChannelStatesToDevice();
            SendStateToDevice(force: true);
        }
    }

    private void SendStateToDevice(bool force = false)
    {
        try
        {
            if (!_serialService.IsConnected || !_esp32HelloReceived || _controllerSleepRequested)
            {
                return;
            }

            ChannelMappingItem channel = SelectedChannel;

            string label = MakeProtocolSafeLabel(channel.DisplayLabel);
            int volume = channel.Volume;
            bool mutedBool = channel.Muted;
            int muted = mutedBool ? 1 : 0;

            if (!force && label == _lastSentLabel && volume == _lastSentVolume && mutedBool == _lastSentMute)
            {
                return;
            }

            string message = $"STATE,{channel.ChannelIndex},{label},{volume},{muted}";

            WriteSerialLine(message, logOutgoing: true);

            _lastSentLabel = label;
            _lastSentVolume = volume;
            _lastSentMute = mutedBool;
            _lastStateSent = message;

            UpdateDiagnostics();
        }
        catch (Exception ex)
        {
            Log($"State send error: {ex.Message}");
        }
    }

    private void SendAllChannelStatesToDevice()
    {
        if (!_serialService.IsConnected || !_esp32HelloReceived || _controllerSleepRequested)
        {
            return;
        }

        foreach (ChannelMappingItem channel in _channels)
        {
            string label = MakeProtocolSafeLabel(channel.DisplayLabel);
            string status = MakeProtocolSafeLabel(channel.Status);
            int muted = channel.Muted ? 1 : 0;

            string message = $"{ProtocolCommands.ChannelState},{channel.ChannelIndex},{label},{channel.Volume},{muted},{status}";

            WriteSerialLine(message);
            _lastStateSent = message;
        }

        UpdateDiagnostics();
    }

    private string GetOledDisplayModeProtocolValue()
    {
        return GetDisplayModeFromUi() switch
        {
            DisplayModes.LargeVolume => ProtocolCommands.DisplayModeLargeVolume,
            DisplayModes.MuteStatus => ProtocolCommands.DisplayModeMuteStatus,
            DisplayModes.AppOrDeviceName => ProtocolCommands.DisplayModeAppName,
            DisplayModes.BarPercent => ProtocolCommands.DisplayModeBarPercent,
            _ => ProtocolCommands.DisplayModeAppVolume
        };
    }



    // Converts a dashboard display-mode constant to the firmware protocol string.
    // Empty input → empty output (firmware interprets empty as "use global").
    private static string GetChannelOledModeProtocolValue(string dashboardMode)
    {
        if (string.IsNullOrEmpty(dashboardMode)) return string.Empty;
        return dashboardMode switch
        {
            DisplayModes.LargeVolume     => ProtocolCommands.DisplayModeLargeVolume,
            DisplayModes.MuteStatus      => ProtocolCommands.DisplayModeMuteStatus,
            DisplayModes.AppOrDeviceName => ProtocolCommands.DisplayModeAppName,
            DisplayModes.BarPercent      => ProtocolCommands.DisplayModeBarPercent,
            _                            => ProtocolCommands.DisplayModeAppVolume
        };
    }

    // Gets the per-channel mode index for ChannelOledModeComboBox.
    // Index 0 = Use global default (""), 1-5 = specific modes.
    private static int GetChannelOledModeIndex(string dashboardMode) => dashboardMode switch
    {
        DisplayModes.AppNameAndVolume => 1,
        DisplayModes.LargeVolume      => 2,
        DisplayModes.MuteStatus       => 3,
        DisplayModes.AppOrDeviceName  => 4,
        DisplayModes.BarPercent       => 5,
        _                             => 0   // empty or unrecognised → "Use global default"
    };

    // Gets the dashboard mode constant from a ComboBox index.
    private static string GetChannelOledModeFromIndex(int index) => index switch
    {
        1 => DisplayModes.AppNameAndVolume,
        2 => DisplayModes.LargeVolume,
        3 => DisplayModes.MuteStatus,
        4 => DisplayModes.AppOrDeviceName,
        5 => DisplayModes.BarPercent,
        _ => string.Empty   // 0 or any other = use global default
    };

    private void SendChannelOledModeToDevice(int channelIndex)
    {
        if (!_serialService.IsConnected || !_esp32HelloReceived) return;
        if (channelIndex < 0 || channelIndex >= ChannelCount) return;

        string mode = GetChannelOledModeProtocolValue(_channels[channelIndex].OledDisplayMode);
        string message = $"{ProtocolCommands.DisplayMode},{channelIndex},{mode}";
        WriteSerialLine(message, logOutgoing: true);
    }

    private void SendAllChannelOledModesToDevice()
    {
        if (!_serialService.IsConnected || !_esp32HelloReceived) return;

        for (int i = 0; i < ChannelCount; i++)
        {
            SendChannelOledModeToDevice(i);
        }
    }

    private void SendOledSettingsToDevice(bool logIfNotConnected)
    {
        if (!_serialService.IsConnected || !_esp32HelloReceived)
        {
            if (logIfNotConnected)
            {
                Log("OLED setup saved locally. Connect the controller to apply it to the physical OLED display.");
            }
            return;
        }

        string mode = GetOledDisplayModeProtocolValue();
        int brightness = GetOledBrightnessPercentFromUi();
        int disconnectedTimeout = GetOledSleepTimeoutMinutesFromUi();
        string connectedIdleAction = GetOledConnectedIdleActionProtocolValue();
        int connectedIdleTimeout = GetOledConnectedIdleTimeoutMinutesFromUi();
        int antiBurnIn = IsOledAntiBurnInEnabledFromUi() ? 1 : 0;
        string message = $"{ProtocolCommands.OledConfig},{mode},{brightness},{disconnectedTimeout},{connectedIdleAction},{connectedIdleTimeout},{antiBurnIn}";
        WriteSerialLine(message, logOutgoing: true);
        _lastStateSent = message;
        UpdateDiagnostics();
    }

    private void WriteSerialLine(string message, bool logOutgoing = false)
    {
        AppendDebugConsole("OUT", message);
        _serialService.SendLine(message);
        if (logOutgoing && _serialService.IsConnected)
            Log($"PC -> ESP32: {message}");
    }

    private int GetEncoderSensitivityPercentFromUi()
    {
        if (EncoderSensitivitySlider == null)
        {
            return _settings.EncoderSensitivityPercent;
        }

        return Math.Clamp((int)Math.Round(EncoderSensitivitySlider.Value), 0, MaxEncoderSensitivityPercent);
    }

    private void AccelerationEnabledCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        _settings.AccelerationEnabled = AccelerationEnabledCheckBox?.IsChecked == true;
        SaveSettings();
    }

    private void AccelerationPresetComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AccelerationPresetComboBox == null) return;
        _settings.AccelerationPreset = GetAccelerationPresetFromUi();
        UpdateAccelCustomPanel();
        SaveSettings();
    }

    private void AccelThresholdSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (AccelThresholdSlider == null) return;
        int v = (int)Math.Round(AccelThresholdSlider.Value);
        _settings.AccelThresholdMs = v;
        if (AccelThresholdLabel != null) AccelThresholdLabel.Text = $"{v} ms";
        UpdateAccelPreview();
        SaveSettings();
    }

    private void AccelMaxMultiplierSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (AccelMaxMultiplierSlider == null) return;
        float v = (float)AccelMaxMultiplierSlider.Value;
        _settings.AccelMaxMultiplier = v;
        if (AccelMaxMultiplierLabel != null) AccelMaxMultiplierLabel.Text = $"{v:F1}×";
        UpdateAccelPreview();
        SaveSettings();
    }

    private void AccelCurveSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (AccelCurveSlider == null) return;
        float v = (float)AccelCurveSlider.Value;
        _settings.AccelCurveExponent = v;
        if (AccelCurveLabel != null) AccelCurveLabel.Text = GetCurveShapeLabel(v);
        UpdateAccelPreview();
        SaveSettings();
    }

    private static string GetCurveShapeLabel(float exponent)
    {
        if (exponent < 0.6f) return "Early";
        if (exponent < 0.85f) return "Soft";
        if (exponent < 1.15f) return "Linear";
        if (exponent < 1.7f) return "Late";
        return "Sharp";
    }

    // Shows or hides the custom acceleration panel depending on the selected preset.
    private void UpdateAccelCustomPanel()
    {
        if (AccelCustomPanel == null) return;
        bool isCustom = GetAccelerationPresetFromUi() == AccelerationPresets.Custom;
        AccelCustomPanel.Visibility = isCustom ? Visibility.Visible : Visibility.Collapsed;
        if (isCustom) UpdateAccelPreview();
    }

    // Recomputes and displays the live multiplier preview for the custom acceleration curve.
    private void UpdateAccelPreview()
    {
        if (AccelPreviewLabel == null) return;

        int   threshold = Math.Clamp((int)Math.Round(AccelThresholdSlider?.Value ?? _settings.AccelThresholdMs), 20, 250);
        float maxMult   = Math.Clamp((float)(AccelMaxMultiplierSlider?.Value    ?? _settings.AccelMaxMultiplier), 1.5f, 8.0f);
        float curve     = Math.Clamp((float)(AccelCurveSlider?.Value            ?? _settings.AccelCurveExponent), 0.3f, 2.5f);

        // Representative turning speeds: idle baseline, medium, fast, maximum.
        double[] intervals = { threshold * 1.5, threshold * 0.7, threshold * 0.25, 8.0 };
        string[] speedLabels = { $"Idle (>{threshold} ms)", $"Medium (~{(int)(threshold * 0.7)} ms)", $"Fast (~{(int)(threshold * 0.25)} ms)", "Max (~8 ms)" };

        var parts = new System.Text.StringBuilder();
        for (int i = 0; i < intervals.Length; i++)
        {
            float mult = ComputeCustomAccelMultiplier(intervals[i], threshold, maxMult, curve);
            if (i > 0) parts.Append("   ");
            parts.Append($"{speedLabels[i]}: {mult:F1}×");
        }

        AccelPreviewLabel.Text = parts.ToString();
    }

    private void VolumeSmoothingEnabledCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        _settings.VolumeSmoothingEnabled = VolumeSmoothingEnabledCheckBox?.IsChecked == true;
        if (!_settings.VolumeSmoothingEnabled)
        {
            // Cancel any in-progress smoothing immediately.
            for (int i = 0; i < ExpectedChannelCount; i++) _smoothingActive[i] = false;
            StopSmoothingTimer();
        }
        SaveSettings();
    }

    private void VolumeSmoothingSpeedComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (VolumeSmoothingSpeedComboBox == null) return;
        _settings.VolumeSmoothingSpeed = GetVolumeSmoothingSpeedFromUi();
        SaveSettings();
    }

    private string GetAccelerationPresetFromUi()
    {
        return AccelerationPresetComboBox?.SelectedIndex switch
        {
            0 => AccelerationPresets.Light,
            1 => AccelerationPresets.Medium,
            2 => AccelerationPresets.Aggressive,
            3 => AccelerationPresets.Custom,
            _ => AccelerationPresets.Medium,
        };
    }

    private static int GetAccelerationPresetIndex(string preset)
    {
        return preset switch
        {
            AccelerationPresets.Light      => 0,
            AccelerationPresets.Medium     => 1,
            AccelerationPresets.Aggressive => 2,
            AccelerationPresets.Custom     => 3,
            _                              => 1,
        };
    }

    private string GetVolumeSmoothingSpeedFromUi()
    {
        return VolumeSmoothingSpeedComboBox?.SelectedIndex switch
        {
            0 => SmoothingSpeed.Fast,
            1 => SmoothingSpeed.Normal,
            2 => SmoothingSpeed.Slow,
            _ => SmoothingSpeed.Normal,
        };
    }

    private static int GetSmoothingSpeedIndex(string speed)
    {
        return speed switch
        {
            SmoothingSpeed.Fast   => 0,
            SmoothingSpeed.Normal => 1,
            SmoothingSpeed.Slow   => 2,
            _                     => 1,
        };
    }

    private string GetSelectedChannelButtonActionFromUi()
    {
        return ChannelButtonActionComboBox?.SelectedIndex switch
        {
            1 => ChannelButtonActions.ToggleAssignedMute,
            2 => ChannelButtonActions.NoAction,
            3 => ChannelButtonActions.CycleNextProfile,
            4 => ChannelButtonActions.CycleOutputDevice,
            5 => ChannelButtonActions.ApplyPreset1,
            6 => ChannelButtonActions.ApplyPreset2,
            7 => ChannelButtonActions.ApplyPreset3,
            _ => ChannelButtonActions.SelectNextChannel
        };
    }

    private string GetSelectedChannelLongPressActionFromUi()
    {
        return ChannelLongPressActionComboBox?.SelectedIndex switch
        {
            0 => ChannelButtonActions.ToggleAssignedMute,
            1 => ChannelButtonActions.NoAction,
            2 => ChannelButtonActions.CycleNextProfile,
            3 => ChannelButtonActions.CycleOutputDevice,
            4 => ChannelButtonActions.ApplyPreset1,
            5 => ChannelButtonActions.ApplyPreset2,
            6 => ChannelButtonActions.ApplyPreset3,
            _ => ChannelButtonActions.ToggleAssignedMute
        };
    }

    private static int GetButtonActionIndex(string action)
    {
        return action switch
        {
            ChannelButtonActions.ToggleAssignedMute => 1,
            ChannelButtonActions.NoAction => 2,
            ChannelButtonActions.CycleNextProfile => 3,
            ChannelButtonActions.CycleOutputDevice => 4,
            ChannelButtonActions.ApplyPreset1 => 5,
            ChannelButtonActions.ApplyPreset2 => 6,
            ChannelButtonActions.ApplyPreset3 => 7,
            _ => 0
        };
    }

    private static int GetLongPressActionIndex(string action)
    {
        return action switch
        {
            ChannelButtonActions.ToggleAssignedMute => 0,
            ChannelButtonActions.NoAction => 1,
            ChannelButtonActions.CycleNextProfile => 2,
            ChannelButtonActions.CycleOutputDevice => 3,
            ChannelButtonActions.ApplyPreset1 => 4,
            ChannelButtonActions.ApplyPreset2 => 5,
            ChannelButtonActions.ApplyPreset3 => 6,
            _ => 0
        };
    }

    private string GetSelectedChannelDoublePressActionFromUi()
    {
        return ChannelDoublePressActionComboBox?.SelectedIndex switch
        {
            0 => ChannelButtonActions.ToggleAssignedMute,
            1 => ChannelButtonActions.NoAction,
            2 => ChannelButtonActions.CycleNextProfile,
            3 => ChannelButtonActions.CycleOutputDevice,
            4 => ChannelButtonActions.ApplyPreset1,
            5 => ChannelButtonActions.ApplyPreset2,
            6 => ChannelButtonActions.ApplyPreset3,
            _ => ChannelButtonActions.NoAction
        };
    }

    private static int GetDoublePressActionIndex(string action)
    {
        return action switch
        {
            ChannelButtonActions.ToggleAssignedMute => 0,
            ChannelButtonActions.NoAction => 1,
            ChannelButtonActions.CycleNextProfile => 2,
            ChannelButtonActions.CycleOutputDevice => 3,
            ChannelButtonActions.ApplyPreset1 => 4,
            ChannelButtonActions.ApplyPreset2 => 5,
            ChannelButtonActions.ApplyPreset3 => 6,
            _ => 1
        };
    }

    private static string GetButtonActionDisplayName(string action)
    {
        return action switch
        {
            ChannelButtonActions.ToggleAssignedMute => "Toggle assigned mute",
            ChannelButtonActions.NoAction => "No action",
            ChannelButtonActions.CycleNextProfile => "Next profile",
            ChannelButtonActions.CycleOutputDevice => "Cycle output device",
            ChannelButtonActions.ApplyPreset1 => "Apply preset 1",
            ChannelButtonActions.ApplyPreset2 => "Apply preset 2",
            ChannelButtonActions.ApplyPreset3 => "Apply preset 3",
            _ => "Select next channel"
        };
    }

    private AudioTargetItem? FindTargetByKey(string? key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        _audioTargetCache.TryGetValue(key, out AudioTargetItem? target);
        return target;
    }

    private static string MakeProcessKey(string processName)
    {
        return $"PROC:{processName}";
    }

    private static string MakeDisplayLabelFromTargetKey(string key)
    {
        if (key.Equals("MASTER", StringComparison.OrdinalIgnoreCase))
        {
            return "Master";
        }

        if (key.Equals("MIC_INPUT", StringComparison.OrdinalIgnoreCase))
        {
            return "Microphone Input";
        }

        if (key.StartsWith("PROC:", StringComparison.OrdinalIgnoreCase))
        {
            return key[5..];
        }

        return key;
    }

    private static string MakeProcessNameFromTargetKey(string key)
    {
        if (key.StartsWith("PROC:", StringComparison.OrdinalIgnoreCase))
        {
            return key[5..];
        }

        return key;
    }

    private IEnumerable<AudioTargetItem> FindSessionsForKey(string key)
    {
        if (key.Equals("MASTER", StringComparison.OrdinalIgnoreCase))
        {
            yield return AudioTargetItem.CreateMaster();
            yield break;
        }

        string processName = key.StartsWith("PROC:", StringComparison.OrdinalIgnoreCase) ? key[5..] : key;

        EnsureAudioDevice();

        List<AudioSessionControl> sessions = _audioService.GetActiveSessions();

        foreach (AudioSessionControl session in sessions)
        {
            AudioTargetItem? item = TryCreateAudioTargetFromSession(session, processName);

            if (item != null)
            {
                yield return item;
            }
        }
    }

    private static AudioTargetItem? TryCreateAudioTargetFromSession(
        AudioSessionControl session,
        string? requiredProcessName = null)
    {
        try
        {
            uint pidRaw = session.GetProcessID;

            if (pidRaw == 0 || session.SimpleAudioVolume == null)
            {
                return null;
            }

            using Process process = Process.GetProcessById((int)pidRaw);

            if (!string.IsNullOrWhiteSpace(requiredProcessName) &&
                !process.ProcessName.Equals(requiredProcessName, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return new AudioTargetItem
            {
                Key = MakeProcessKey(process.ProcessName),
                Label = process.ProcessName,
                ProcessName = process.ProcessName,
                ProcessId = (int)pidRaw,
                Session = session,
                Volume = Math.Clamp((int)Math.Round(session.SimpleAudioVolume.Volume * 100), 0, 100),
                Muted = session.SimpleAudioVolume.Mute,
                State = session.State.ToString(),
                IsMaster = false
            };
        }
        catch
        {
            return null;
        }
    }

    private void RefreshChannelAssignmentLabels()
    {
        foreach (ChannelMappingItem channel in _channels)
        {
            if (string.IsNullOrWhiteSpace(channel.TargetKey))
            {
                channel.AssignedLabel = "Unassigned";

                if (string.IsNullOrWhiteSpace(channel.FriendlyName))
                {
                    channel.FriendlyName = string.Empty;
                }

                continue;
            }

            AudioTargetItem? target = FindTargetByKey(channel.TargetKey);

            if (target != null)
            {
                channel.AssignedLabel = target.Label;
            }
            else
            {
                channel.AssignedLabel = MakeDisplayLabelFromTargetKey(channel.TargetKey);
            }

            if (string.IsNullOrWhiteSpace(channel.FriendlyName))
            {
                channel.FriendlyName = channel.AssignedLabel;
            }
        }
    }

    private int GetMasterVolumePercent()
    {
        EnsureAudioDevice();

        float scalar = _defaultRenderDevice!.AudioEndpointVolume.MasterVolumeLevelScalar;
        return Math.Clamp((int)Math.Round(scalar * 100), 0, 100);
    }

    private bool GetMasterMute()
    {
        EnsureAudioDevice();
        return _defaultRenderDevice!.AudioEndpointVolume.Mute;
    }

    private void EnsureAudioDevice()
    {
        if (_defaultRenderDevice == null)
        {
            RefreshDefaultAudioDevice();
        }
    }

    private static string MakeProtocolSafeLabel(string label)
    {
        string cleaned = label.Replace(',', ' ').Trim();

        if (cleaned.Length == 0)
        {
            return "Unknown";
        }

        return cleaned.Length <= 18 ? cleaned : cleaned[..18];
    }

    private static System.Drawing.Icon LoadAppIcon()
    {
        try
        {
            // Load icon from the embedded application resource (Assets/app-icon.ico).
            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
            string icoPath = Path.Combine(exeDir, "Assets", "app-icon.ico");
            if (File.Exists(icoPath))
                return new System.Drawing.Icon(icoPath);
        }
        catch { /* Static context — cannot call Log; fall back to system icon. */ }
        return System.Drawing.SystemIcons.Application;
    }

    private void SetupTrayIcon()
    {
        _trayIcon = new Forms.NotifyIcon
        {
            Icon = LoadAppIcon(),
            Text = "PC Volume Controller",
            Visible = true,
            ContextMenuStrip = new Forms.ContextMenuStrip()
        };

        _trayIcon.ContextMenuStrip.Items.Add("Open Dashboard", null, (_, _) => DispatchUi(RestoreFromTray));
        _trayIcon.ContextMenuStrip.Items.Add("Connect", null, (_, _) => DispatchUi(() =>
        {
            _manualDisconnectRequested = false;
            _manualAutoReconnectSuppressionLogged = false;
            if (!_serialService.IsConnected)
            {
                ConnectSerial();
            }
        }));
        _trayIcon.ContextMenuStrip.Items.Add("Disconnect", null, (_, _) => DispatchUi(() =>
        {
            RequestManualDisconnect("Tray disconnect requested");
        }));
        _trayIcon.ContextMenuStrip.Items.Add("Reconnect", null, (_, _) => DispatchUi(() =>
        {
            _manualDisconnectRequested = false;
            _manualAutoReconnectSuppressionLogged = false;
            Log("Tray reconnect requested.");
            DisconnectSerial(sendDisconnectCommand: false, preserveLastControllerPort: true, refreshPortsAfterDisconnect: true);
            TryAutoReconnect(GetAvailableComPorts());
        }));
        _trayIcon.ContextMenuStrip.Items.Add("Open Log Folder", null, (_, _) => DispatchUi(OpenLogFolder));
        _trayIcon.ContextMenuStrip.Items.Add("Exit", null, (_, _) => DispatchUi(ExitApplication));
        _trayIcon.DoubleClick += (_, _) => DispatchUi(RestoreFromTray);

        BuildTrayProfileMenu();
    }

    private void BuildTrayProfileMenu()
    {
        if (_trayIcon?.ContextMenuStrip == null) return;

        // Remove the old profile menu item if it exists
        if (_trayProfileMenuItem != null)
        {
            _trayIcon.ContextMenuStrip.Items.Remove(_trayProfileMenuItem);
            _trayProfileMenuItem.Dispose();
            _trayProfileMenuItem = null;
        }

        if (_settings.Profiles.Count <= 1) return; // no submenu needed for single profile

        _trayProfileMenuItem = new Forms.ToolStripMenuItem("Switch Profile");

        foreach (ProfileEntry profile in _settings.Profiles)
        {
            string profileName = profile.Name;
            var item = new Forms.ToolStripMenuItem(profileName)
            {
                Checked = profileName == _settings.ActiveProfileName
            };
            item.Click += (_, _) => Dispatcher.InvokeAsync(() => SwitchToProfile(profileName));
            _trayProfileMenuItem.DropDownItems.Add(item);
        }

        // Insert before the last separator/exit item (index 2 = after Open and Connect)
        int insertIndex = Math.Min(2, _trayIcon.ContextMenuStrip.Items.Count);
        _trayIcon.ContextMenuStrip.Items.Insert(insertIndex, _trayProfileMenuItem);
    }

    private void DispatchUi(Action action)
    {
        if (Dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.InvokeAsync(action);
        }
    }

    private void ShowTrayNotification(string title, string message, int timeoutMs = 3000)
    {
        try
        {
            if (_trayIcon == null || !_trayIcon.Visible)
            {
                return;
            }

            _trayIcon.BalloonTipTitle = title;
            _trayIcon.BalloonTipText = message;
            _trayIcon.ShowBalloonTip(timeoutMs);
        }
        catch
        {
        }
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);

        bool minimizeToTray = MinimizeToTrayCheckBox?.IsChecked == true || _settings.MinimizeToTray;

        if (WindowState == WindowState.Minimized && minimizeToTray)
        {
            HideToTray();
        }
    }

    private void HideToTray()
    {
        Hide();
        ShowInTaskbar = false;
    }

    private void RestoreFromTray()
    {
        Show();
        ShowInTaskbar = true;
        WindowState = WindowState.Normal;
        Activate();
    }

    private void ExitApplication()
    {
        _reallyClose = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        FlushUiToSettings();

        bool minimizeToTray = MinimizeToTrayCheckBox?.IsChecked == true || _settings.MinimizeToTray;

        if (!_reallyClose && minimizeToTray)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        _overlayWindow?.HideImmediate();

        base.OnClosing(e);
    }

    private void ApplySettingsToUi()
    {
        AutoConnectCheckBox.IsChecked = _settings.AutoConnectOnLaunch;
        ScanAllComPortsCheckBox.IsChecked = _settings.ScanAllComPortsIfRememberedMissing;
        MinimizeToTrayCheckBox.IsChecked = _settings.MinimizeToTray;
        StartMinimizedToTrayCheckBox.IsChecked = _settings.StartMinimizedToTray;
        StartWithWindowsCheckBox.IsChecked = _settings.StartWithWindows;
        AdvancedDebugLoggingCheckBox.IsChecked = _settings.AdvancedDebugLogging;

        if (DisplayModeComboBox != null)
        {
            DisplayModeComboBox.SelectedIndex = GetDisplayModeIndex(_settings.OledDisplayMode);
        }

        if (OledBrightnessSlider != null)
        {
            OledBrightnessSlider.Value = Math.Clamp(_settings.OledBrightnessPercent, 0, 100);
            UpdateOledBrightnessLabel();
        }

        if (OledSleepTimeoutSlider != null)
        {
            OledSleepTimeoutSlider.Value = Math.Clamp(_settings.OledSleepTimeoutMinutes, 1, 60);
            UpdateOledSleepTimeoutLabel();
        }

        if (OledConnectedIdleActionComboBox != null)
        {
            OledConnectedIdleActionComboBox.SelectedIndex = GetOledConnectedIdleActionComboBoxIndex(_settings.OledConnectedIdleAction);
        }

        if (OledConnectedIdleTimeoutSlider != null)
        {
            OledConnectedIdleTimeoutSlider.Value = Math.Clamp(_settings.OledConnectedIdleTimeoutMinutes, 1, 60);
            UpdateOledConnectedIdleTimeoutLabel();
        }

        if (OledAntiBurnInCheckBox != null)
        {
            OledAntiBurnInCheckBox.IsChecked = _settings.OledAntiBurnInEnabled;
        }

        EncoderSensitivitySlider.Value = Math.Clamp(_settings.EncoderSensitivityPercent, 0, MaxEncoderSensitivityPercent);
        EncoderSensitivityValueTextBlock.Text = $"Sensitivity: {Math.Clamp(_settings.EncoderSensitivityPercent, 0, MaxEncoderSensitivityPercent)}%";

        if (AccelerationEnabledCheckBox != null)
        {
            AccelerationEnabledCheckBox.IsChecked = _settings.AccelerationEnabled;
        }
        if (AccelerationPresetComboBox != null)
        {
            AccelerationPresetComboBox.SelectedIndex = GetAccelerationPresetIndex(_settings.AccelerationPreset);
        }
        if (AccelThresholdSlider != null)
        {
            AccelThresholdSlider.Value = Math.Clamp(_settings.AccelThresholdMs, 20, 250);
            if (AccelThresholdLabel != null) AccelThresholdLabel.Text = $"{_settings.AccelThresholdMs} ms";
        }
        if (AccelMaxMultiplierSlider != null)
        {
            AccelMaxMultiplierSlider.Value = Math.Clamp((double)_settings.AccelMaxMultiplier, 1.5, 8.0);
            if (AccelMaxMultiplierLabel != null) AccelMaxMultiplierLabel.Text = $"{_settings.AccelMaxMultiplier:F1}×";
        }
        if (AccelCurveSlider != null)
        {
            AccelCurveSlider.Value = Math.Clamp((double)_settings.AccelCurveExponent, 0.3, 2.5);
            if (AccelCurveLabel != null) AccelCurveLabel.Text = GetCurveShapeLabel(_settings.AccelCurveExponent);
        }
        UpdateAccelCustomPanel();
        if (VolumeSmoothingEnabledCheckBox != null)
        {
            VolumeSmoothingEnabledCheckBox.IsChecked = _settings.VolumeSmoothingEnabled;
        }
        if (VolumeSmoothingSpeedComboBox != null)
        {
            VolumeSmoothingSpeedComboBox.SelectedIndex = GetSmoothingSpeedIndex(_settings.VolumeSmoothingSpeed);
        }

        ThemeFollowSystemRadioButton.IsChecked = _settings.ThemeMode == ThemeModes.FollowSystem;
        ThemeLightRadioButton.IsChecked = _settings.ThemeMode == ThemeModes.Light;
        ThemeDarkRadioButton.IsChecked = _settings.ThemeMode == ThemeModes.Dark;

        if (OverlayEnabledCheckBox != null)
            OverlayEnabledCheckBox.IsChecked = _settings.OverlayEnabled;

        if (OverlayTimeoutSlider != null)
        {
            OverlayTimeoutSlider.Value = _settings.OverlayTimeoutSeconds;
            if (OverlayTimeoutValueTextBlock != null)
                OverlayTimeoutValueTextBlock.Text = $"{_settings.OverlayTimeoutSeconds:F1} s";
        }

        // Set position combobox — find item by Tag matching OverlayPosition
        if (OverlayPositionComboBox != null)
        {
            foreach (ComboBoxItem item in OverlayPositionComboBox.Items)
            {
                if (item.Tag is string tag && tag == _settings.OverlayPosition)
                {
                    OverlayPositionComboBox.SelectedItem = item;
                    break;
                }
            }
            if (OverlayPositionComboBox.SelectedItem == null && OverlayPositionComboBox.Items.Count > 0)
                OverlayPositionComboBox.SelectedIndex = 4; // default BottomCenter
        }

        UpdateHotkeyLabels();
    }

    private void ApplyFirstRunWizardSettingsToUi()
    {
        if (WizardAutoConnectCheckBox != null)
        {
            WizardAutoConnectCheckBox.IsChecked = _settings.AutoConnectOnLaunch;
        }

        if (WizardStartWithWindowsCheckBox != null)
        {
            WizardStartWithWindowsCheckBox.IsChecked = _settings.StartWithWindows;
        }

        if (WizardMinimizeToTrayCheckBox != null)
        {
            WizardMinimizeToTrayCheckBox.IsChecked = _settings.MinimizeToTray;
        }

        if (WizardStartMinimizedCheckBox != null)
        {
            WizardStartMinimizedCheckBox.IsChecked = _settings.StartMinimizedToTray;
        }

        if (WizardScanAllCheckBox != null)
        {
            WizardScanAllCheckBox.IsChecked = _settings.ScanAllComPortsIfRememberedMissing;
        }

        if (FirstRunWizardTab != null)
        {
            FirstRunWizardTab.Visibility = _settings.FirstRunWizardCompleted
                ? Visibility.Collapsed
                : Visibility.Visible;
        }
    }

    private void ShowFirstRunWizardButton_Click(object sender, RoutedEventArgs e)
    {
        if (FirstRunWizardTab != null)
        {
            FirstRunWizardTab.Visibility = Visibility.Visible;
            MainTabs.SelectedItem = FirstRunWizardTab;
        }
    }

    private void UpdateFirstRunWizardStatus()
    {
        try
        {
            if (WizardStatusTextBlock == null)
            {
                return;
            }

            bool connected = _serialService.IsConnected && _esp32HelloReceived;
            string controllerPort = connected ? (_serialService.PortName ?? "unknown") : string.IsNullOrWhiteSpace(_settings.LastComPort) ? "not remembered yet" : _settings.LastComPort;
            string actualPorts = _lastKnownComPorts.Length == 0 ? "none" : string.Join(", ", _lastKnownComPorts);

            WizardStatusTextBlock.Text = _settings.FirstRunWizardCompleted
                ? "Wizard status: complete"
                : "Wizard status: not completed";

            WizardControllerStatusTextBlock.Text = connected
                ? $"Controller: connected on {controllerPort}"
                : $"Controller: not connected. Remembered controller port: {controllerPort}. Actual COM ports: {actualPorts}";

            WizardFirmwareStatusTextBlock.Text = connected
                ? $"Firmware/protocol: {_espFirmwareName}, v{_espProtocolVersion}, channels {_espChannelCount}"
                : $"Firmware/protocol: waiting for controller. Required protocol: v{RequiredProtocolVersion}+";

            WizardStartupStatusTextBlock.Text = $"Startup: auto-connect {OnOff(_settings.AutoConnectOnLaunch)}, start with Windows {OnOff(_settings.StartWithWindows)}, minimize to tray {OnOff(_settings.MinimizeToTray)}, start minimized {OnOff(_settings.StartMinimizedToTray)}, scan all ports {OnOff(_settings.ScanAllComPortsIfRememberedMissing)}";
        }
        catch
        {
        }
    }

    private static string OnOff(bool value) => value ? "on" : "off";

    private void ApplySavedWindowSize()
    {
        const double defaultWidth = 1120;
        const double defaultHeight = 800;

        double width = _settings.WindowWidth >= defaultWidth
            ? _settings.WindowWidth
            : defaultWidth;

        double height = _settings.WindowHeight >= defaultHeight
            ? _settings.WindowHeight
            : defaultHeight;

        Width = Math.Max(width, MinWidth);
        Height = Math.Max(height, MinHeight);
    }

    private void FlushUiToSettings()
    {
        _settings.SelectedChannelIndex = _selectedChannelIndex;

        if (AutoConnectCheckBox != null) _settings.AutoConnectOnLaunch = AutoConnectCheckBox.IsChecked == true;
        if (ScanAllComPortsCheckBox != null) _settings.ScanAllComPortsIfRememberedMissing = ScanAllComPortsCheckBox.IsChecked == true;
        if (MinimizeToTrayCheckBox != null) _settings.MinimizeToTray = MinimizeToTrayCheckBox.IsChecked == true;
        if (StartMinimizedToTrayCheckBox != null) _settings.StartMinimizedToTray = StartMinimizedToTrayCheckBox.IsChecked == true;
        if (StartWithWindowsCheckBox != null) _settings.StartWithWindows = StartWithWindowsCheckBox.IsChecked == true;
        if (AdvancedDebugLoggingCheckBox != null) _settings.AdvancedDebugLogging = AdvancedDebugLoggingCheckBox.IsChecked == true;
        if (DisplayModeComboBox != null) _settings.OledDisplayMode = GetDisplayModeFromUi();
        if (OledBrightnessSlider != null) _settings.OledBrightnessPercent = GetOledBrightnessPercentFromUi();
        if (OledSleepTimeoutSlider != null) _settings.OledSleepTimeoutMinutes = GetOledSleepTimeoutMinutesFromUi();
        if (OledConnectedIdleActionComboBox != null) _settings.OledConnectedIdleAction = GetOledConnectedIdleActionFromUi();
        if (OledConnectedIdleTimeoutSlider != null) _settings.OledConnectedIdleTimeoutMinutes = GetOledConnectedIdleTimeoutMinutesFromUi();
        if (OledAntiBurnInCheckBox != null) _settings.OledAntiBurnInEnabled = IsOledAntiBurnInEnabledFromUi();
        if (EncoderSensitivitySlider != null) _settings.EncoderSensitivityPercent = GetEncoderSensitivityPercentFromUi();
        if (AccelerationEnabledCheckBox != null) _settings.AccelerationEnabled = AccelerationEnabledCheckBox.IsChecked == true;
        if (AccelerationPresetComboBox != null) _settings.AccelerationPreset = GetAccelerationPresetFromUi();
        if (AccelThresholdSlider != null) _settings.AccelThresholdMs = (int)Math.Round(AccelThresholdSlider.Value);
        if (AccelMaxMultiplierSlider != null) _settings.AccelMaxMultiplier = (float)AccelMaxMultiplierSlider.Value;
        if (AccelCurveSlider != null) _settings.AccelCurveExponent = (float)AccelCurveSlider.Value;
        if (VolumeSmoothingEnabledCheckBox != null) _settings.VolumeSmoothingEnabled = VolumeSmoothingEnabledCheckBox.IsChecked == true;
        if (VolumeSmoothingSpeedComboBox != null) _settings.VolumeSmoothingSpeed = GetVolumeSmoothingSpeedFromUi();
        if (ThemeFollowSystemRadioButton != null) _settings.ThemeMode = GetThemeModeFromUi();

        // Per-channel sensitivity for the currently viewed channel
        if (PerChannelSensitivityUseGlobalCheckBox != null
            && _selectedChannelIndex >= 0 && _selectedChannelIndex < _settings.Channels.Length)
        {
            bool useGlobal = PerChannelSensitivityUseGlobalCheckBox.IsChecked == true;
            _settings.Channels[_selectedChannelIndex].SensitivityPercent = useGlobal ? -1
                : Math.Clamp((int)Math.Round(PerChannelSensitivitySlider?.Value ?? 0), 0, MaxEncoderSensitivityPercent);
        }

        SaveChannelsToSettings();
        SaveCurrentWindowSize();
        SaveSettings();
    }

    private void DisplayModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DisplayModeComboBox == null)
        {
            return;
        }

        _settings.OledDisplayMode = GetDisplayModeFromUi();
        UpdateOledPreviewPanels();
    }

    private void OledBrightnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (OledBrightnessSlider == null)
        {
            return;
        }

        _settings.OledBrightnessPercent = GetOledBrightnessPercentFromUi();
        UpdateOledBrightnessLabel();
        UpdateOledPreviewPanels();
    }

    private void OledSleepTimeoutSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (OledSleepTimeoutSlider == null)
        {
            return;
        }

        _settings.OledSleepTimeoutMinutes = GetOledSleepTimeoutMinutesFromUi();
        UpdateOledSleepTimeoutLabel();
    }

    private void OledConnectedIdleActionComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (OledConnectedIdleActionComboBox == null)
        {
            return;
        }

        _settings.OledConnectedIdleAction = GetOledConnectedIdleActionFromUi();
        UpdateOledPreviewPanels();
    }

    private void OledConnectedIdleTimeoutSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (OledConnectedIdleTimeoutSlider == null)
        {
            return;
        }

        _settings.OledConnectedIdleTimeoutMinutes = GetOledConnectedIdleTimeoutMinutesFromUi();
        UpdateOledConnectedIdleTimeoutLabel();
        UpdateOledPreviewPanels();
    }

    private void OledAntiBurnInCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (OledAntiBurnInCheckBox == null)
        {
            return;
        }

        _settings.OledAntiBurnInEnabled = IsOledAntiBurnInEnabledFromUi();
        UpdateOledPreviewPanels();
    }

    private int GetDisplayModeIndex(string mode)
    {
        return mode switch
        {
            DisplayModes.LargeVolume => 1,
            DisplayModes.MuteStatus => 2,
            DisplayModes.AppOrDeviceName => 3,
            DisplayModes.BarPercent => 4,
            _ => 0
        };
    }

    private string GetDisplayModeFromUi()
    {
        if (DisplayModeComboBox == null)
        {
            return _settings.OledDisplayMode;
        }

        return DisplayModeComboBox.SelectedIndex switch
        {
            1 => DisplayModes.LargeVolume,
            2 => DisplayModes.MuteStatus,
            3 => DisplayModes.AppOrDeviceName,
            4 => DisplayModes.BarPercent,
            _ => DisplayModes.AppNameAndVolume
        };
    }

    private int GetOledBrightnessPercentFromUi()
    {
        if (OledBrightnessSlider == null)
        {
            return _settings.OledBrightnessPercent;
        }

        return Math.Clamp((int)Math.Round(OledBrightnessSlider.Value), 0, 100);
    }

    private int GetOledSleepTimeoutMinutesFromUi()
    {
        if (OledSleepTimeoutSlider == null)
        {
            return _settings.OledSleepTimeoutMinutes;
        }

        return Math.Clamp((int)Math.Round(OledSleepTimeoutSlider.Value), 1, 60);
    }

    private string GetOledConnectedIdleActionFromUi()
    {
        if (OledConnectedIdleActionComboBox == null)
        {
            return _settings.OledConnectedIdleAction;
        }

        return OledConnectedIdleActionComboBox.SelectedIndex switch
        {
            1 => OledIdleActions.DimTo10,
            2 => OledIdleActions.DimTo20,
            3 => OledIdleActions.DimTo30,
            4 => OledIdleActions.DimTo40,
            5 => OledIdleActions.DimTo50,
            6 => OledIdleActions.DimTo60,
            7 => OledIdleActions.DimTo70,
            _ => OledIdleActions.Off
        };
    }

    private int GetOledConnectedIdleActionComboBoxIndex(string action)
    {
        return action switch
        {
            OledIdleActions.DimTo10 => 1,
            OledIdleActions.DimTo20 => 2,
            OledIdleActions.DimTo30 => 3,
            OledIdleActions.DimTo40 => 4,
            OledIdleActions.DimTo50 => 5,
            OledIdleActions.DimTo60 => 6,
            OledIdleActions.DimTo70 => 7,
            _ => 0
        };
    }

    private string GetOledConnectedIdleActionProtocolValue()
    {
        string action = GetOledConnectedIdleActionFromUi();
        if (action.StartsWith("DimTo", StringComparison.OrdinalIgnoreCase) && int.TryParse(action[5..], out int dimPercent))
        {
            dimPercent = Math.Clamp(dimPercent, 10, 70);
            return $"DIM_{dimPercent}";
        }

        return "OFF";
    }

    private int GetOledConnectedIdleTimeoutMinutesFromUi()
    {
        if (OledConnectedIdleTimeoutSlider == null)
        {
            return _settings.OledConnectedIdleTimeoutMinutes;
        }

        return Math.Clamp((int)Math.Round(OledConnectedIdleTimeoutSlider.Value), 1, 60);
    }

    private bool IsOledAntiBurnInEnabledFromUi()
    {
        return OledAntiBurnInCheckBox?.IsChecked ?? _settings.OledAntiBurnInEnabled;
    }

    private void UpdateOledBrightnessLabel()
    {
        if (OledBrightnessValueTextBlock != null)
        {
            OledBrightnessValueTextBlock.Text = $"Brightness: {GetOledBrightnessPercentFromUi()}%";
        }
    }

    private void UpdateOledSleepTimeoutLabel()
    {
        if (OledSleepTimeoutValueTextBlock != null)
        {
            OledSleepTimeoutValueTextBlock.Text = $"Disconnected sleep timeout: {GetOledSleepTimeoutMinutesFromUi()} minute(s)";
        }
    }

    private void UpdateOledConnectedIdleTimeoutLabel()
    {
        if (OledConnectedIdleTimeoutValueTextBlock != null)
        {
            OledConnectedIdleTimeoutValueTextBlock.Text = $"Connected idle timeout: {GetOledConnectedIdleTimeoutMinutesFromUi()} minute(s)";
        }
    }

    private void UpdateOledPreviewPanels()
    {
        try
        {
            TextBlock[] titleBlocks = { OledPreview1Title, OledPreview2Title, OledPreview3Title, OledPreview4Title, OledPreview5Title, OledPreview6Title };
            TextBlock[] line1Blocks = { OledPreview1Line1, OledPreview2Line1, OledPreview3Line1, OledPreview4Line1, OledPreview5Line1, OledPreview6Line1 };
            TextBlock[] line2Blocks = { OledPreview1Line2, OledPreview2Line2, OledPreview3Line2, OledPreview4Line2, OledPreview5Line2, OledPreview6Line2 };
            System.Windows.Controls.ProgressBar[] progressBars = { OledPreview1Progress, OledPreview2Progress, OledPreview3Progress, OledPreview4Progress, OledPreview5Progress, OledPreview6Progress };

            string mode = GetDisplayModeFromUi();
            if (OledPreviewModeTextBlock != null)
            {
                OledPreviewModeTextBlock.Text = $"Preview mode: {GetDisplayModeDisplayName(mode)} | Brightness: {GetOledBrightnessPercentFromUi()}% | Connected idle: {GetOledConnectedIdleTimeoutMinutesFromUi()} min / {GetOledConnectedIdleActionDisplayName(GetOledConnectedIdleActionFromUi())} | Anti-burn-in: {(IsOledAntiBurnInEnabledFromUi() ? "on" : "off")}";
            }

            for (int i = 0; i < Math.Min(_channels.Count, titleBlocks.Length); i++)
            {
                ChannelMappingItem channel = _channels[i];
                string label = string.IsNullOrWhiteSpace(channel.DisplayLabel) ? $"Channel {channel.ChannelNumber}" : channel.DisplayLabel;
                string assigned = string.IsNullOrWhiteSpace(channel.AssignedLabel) ? "Unassigned" : channel.AssignedLabel;
                string mute = channel.Muted ? "MUTED" : "UNMUTED";

                titleBlocks[i].Text = $"OLED {channel.ChannelNumber}";
                progressBars[i].Value = Math.Clamp(channel.Volume, 0, 100);

                switch (mode)
                {
                    case DisplayModes.LargeVolume:
                        line1Blocks[i].Text = $"{channel.Volume}%";
                        line2Blocks[i].Text = label;
                        break;
                    case DisplayModes.MuteStatus:
                        line1Blocks[i].Text = mute;
                        line2Blocks[i].Text = $"{label} {channel.Volume}%";
                        break;
                    case DisplayModes.AppOrDeviceName:
                        line1Blocks[i].Text = label;
                        line2Blocks[i].Text = assigned;
                        break;
                    case DisplayModes.BarPercent:
                        line1Blocks[i].Text = label;
                        line2Blocks[i].Text = $"Volume bar {channel.Volume}%";
                        break;
                    default:
                        line1Blocks[i].Text = label;
                        line2Blocks[i].Text = $"{channel.Volume}% {(channel.Muted ? "Muted" : channel.Status)}";
                        break;
                }
            }
        }
        catch
        {
        }
    }

    private static string GetDisplayModeDisplayName(string mode)
    {
        return mode switch
        {
            DisplayModes.LargeVolume => "Large volume number",
            DisplayModes.MuteStatus => "Mute status",
            DisplayModes.AppOrDeviceName => "App/device name",
            DisplayModes.BarPercent => "Simple bar/percentage view",
            _ => "App name + volume"
        };
    }

    private static string GetOledConnectedIdleActionDisplayName(string action)
    {
        return action switch
        {
            OledIdleActions.DimTo10 => "Dim brightness to 10%",
            OledIdleActions.DimTo20 => "Dim brightness to 20%",
            OledIdleActions.DimTo30 => "Dim brightness to 30%",
            OledIdleActions.DimTo40 => "Dim brightness to 40%",
            OledIdleActions.DimTo50 => "Dim brightness to 50%",
            OledIdleActions.DimTo60 => "Dim brightness to 60%",
            OledIdleActions.DimTo70 => "Dim brightness to 70%",
            _ => "Turn off display"
        };
    }

    private void QueueFullStateSend(string reason)
    {
        if (!_serialService.IsConnected || !_esp32HelloReceived)
        {
            return;
        }

        System.Threading.Interlocked.Exchange(ref _fullStateSendQueued, 1);
        _fullStateSendCoalesceTimer?.Dispose();
        _fullStateSendCoalesceTimer = new System.Threading.Timer(_ =>
        {
            try
            {
                Dispatcher.InvokeAsync(() =>
                {
                    if (System.Threading.Interlocked.Exchange(ref _fullStateSendQueued, 0) == 1)
                    {
                        RefreshAllChannelStates();
                        SendAllChannelStatesToDevice();
                        SendStateToDevice(force: true);
                        if (IsAdvancedDebugLoggingEnabled())
                        {
                            Log($"Coalesced STATE update sent after {reason}.");
                        }
                    }
                });
            }
            catch
            {
                System.Threading.Interlocked.Exchange(ref _fullStateSendQueued, 0);
            }
        }, null, 180, System.Threading.Timeout.Infinite);
    }

    private bool CanSendSleepWakeTestCommand(string commandName)
    {
        DateTime now = DateTime.Now;
        if ((now - _lastSleepWakeTestCommandAt).TotalMilliseconds < 1000)
        {
            Log($"Ignored repeated {commandName} test command; wait a moment before sending another sleep/wake test.");
            return false;
        }

        _lastSleepWakeTestCommandAt = now;
        return true;
    }

    private void ApplyStartupSetting()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run",
                writable: true);

            if (key == null)
            {
                Log("Could not open Windows startup registry key.");
                return;
            }

            if (_settings.StartWithWindows)
            {
                string? exePath = Environment.ProcessPath;

                if (string.IsNullOrWhiteSpace(exePath))
                {
                    exePath = Process.GetCurrentProcess().MainModule?.FileName;
                }

                if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
                {
                    Log("Could not determine app path for startup.");
                    return;
                }

                key.SetValue(StartupRegistryName, $"\"{exePath}\"");
                Log($"Windows startup enabled: {exePath}");
            }
            else
            {
                key.DeleteValue(StartupRegistryName, throwOnMissingValue: false);
                Log("Windows startup disabled.");
            }
        }
        catch (Exception ex)
        {
            Log($"Startup setting error: {ex.Message}");
        }
    }

    private void LoadSettings()
    {
        SettingsRepository.LoadResult result =
            SettingsRepository.Load(ChannelCount, MaxEncoderSensitivityPercent);
        _settings = result.Settings;

        if (result.WasCorrupt)
        {
            _settingsWereCorrupt = true;
            _settingsCorruptBackupRestored = result.LatestBackupPath;
            return;
        }

        Log($"Settings loaded (version {_settings.SettingsVersion}, {_settings.Profiles?.Count ?? 0} profile(s)).");

        // Migrate legacy flat ChannelTargetKeys array → per-channel TargetKey fields.
        if (_settings.ChannelTargetKeys != null && _settings.ChannelTargetKeys.Length == ChannelCount)
        {
            bool channelsLookEmpty = _settings.Channels.All(ch => string.IsNullOrWhiteSpace(ch.TargetKey));
            if (channelsLookEmpty)
            {
                for (int i = 0; i < ChannelCount; i++)
                {
                    _settings.Channels[i].TargetKey = _settings.ChannelTargetKeys[i] ?? string.Empty;
                    _settings.Channels[i].FriendlyName = MakeDisplayLabelFromTargetKey(_settings.Channels[i].TargetKey);
                }
            }
        }

        if (result.MigrationApplied)
            SaveSettings();
    }

    /// <summary>
    /// Shows the settings-corruption recovery dialog if the file was corrupt at startup.
    /// Must be called after the window is visible so it has a valid owner.
    /// </summary>
    private void ShowPendingSettingsCorruptionDialogIfNeeded()
    {
        if (!_settingsWereCorrupt)
        {
            return;
        }

        _settingsWereCorrupt = false;
        string backupPath = _settingsCorruptBackupRestored ?? string.Empty;
        bool hasBackup = File.Exists(backupPath);

        string message = hasBackup
            ? $"Your settings file could not be read (it may be corrupt).\n\n" +
              $"A backup is available from {File.GetLastWriteTime(backupPath):g}.\n\n" +
              $"What would you like to do?"
            : "Your settings file could not be read (it may be corrupt) and no backup was found.\n\n" +
              "The dashboard has started with factory defaults.";

        if (!hasBackup)
        {
            System.Windows.MessageBox.Show(this, message,
                "Settings File Corrupt",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            SaveSettings();
            return;
        }

        // YesNo: Yes = Restore Backup, No = Start Fresh
        MessageBoxResult result = System.Windows.MessageBox.Show(this,
            message + "\n\nClick Yes to restore the backup, or No to start with factory defaults.",
            "Settings File Corrupt",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.Yes);

        // Yes = restore backup, No = start fresh
        if (result == MessageBoxResult.Yes) // Restore backup
        {
            try
            {
                string json = File.ReadAllText(backupPath);
                DashboardSettings? restored = JsonSerializer.Deserialize<DashboardSettings>(json);
                if (restored != null)
                {
                    SettingsRepository.Normalize(restored, ChannelCount, MaxEncoderSensitivityPercent);
                    _settings = restored;
                    File.Copy(backupPath, SettingsRepository.GetPath(), overwrite: true);
                    ApplySettingsToUi();
                    ApplySettingsToChannels();
                    ApplyTheme();
                    Log("Settings restored from backup: " + backupPath);
                    System.Windows.MessageBox.Show(this,
                        "Settings restored successfully from backup.",
                        "Settings Restored",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return;
                }
            }
            catch (Exception ex)
            {
                Log($"Backup restore failed: {ex.Message}");
                System.Windows.MessageBox.Show(this,
                    $"Backup restore failed: {ex.Message}\n\nStarting with factory defaults.",
                    "Restore Failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        // No or restore failed — keep factory defaults and save them
        SaveSettings();
        Log("Settings were corrupt. Started with factory defaults.");
    }

    private void ApplySettingsToChannels()
    {
        if (_settings.Channels.Length != ChannelCount)
        {
            _settings.Channels = DashboardSettings.CreateDefaultChannels();
        }

        for (int i = 0; i < ChannelCount; i++)
        {
            _channels[i].TargetKey = _settings.Channels[i].TargetKey ?? string.Empty;
            _channels[i].FriendlyName = _settings.Channels[i].FriendlyName ?? string.Empty;
            // Default for ButtonAction is ToggleAssignedMute (not NoAction) to match intended out-of-box behaviour.
            _channels[i].ButtonAction = ChannelButtonActions.IsValid(_settings.Channels[i].ButtonAction) ? _settings.Channels[i].ButtonAction : ChannelButtonActions.ToggleAssignedMute;
            _channels[i].LongPressButtonAction = ChannelButtonActions.IsValidLongPressAction(_settings.Channels[i].LongPressButtonAction) ? _settings.Channels[i].LongPressButtonAction : ChannelButtonActions.NoAction;
            _channels[i].DoublePressButtonAction = ChannelButtonActions.IsValidDoublePressAction(_settings.Channels[i].DoublePressButtonAction) ? _settings.Channels[i].DoublePressButtonAction : ChannelButtonActions.NoAction;
            _channels[i].RebindFallback = RebindFallbacks.IsValid(_settings.Channels[i].RebindFallback) ? _settings.Channels[i].RebindFallback : RebindFallbacks.ShowInactive;
            _channels[i].OledDisplayMode = DisplayModes.IsValidChannelMode(_settings.Channels[i].OledDisplayMode) ? _settings.Channels[i].OledDisplayMode : string.Empty;
        }

        RefreshChannelAssignmentLabels();
        RefreshAllChannelStates();
        ApplyRebindSettingsToUi();
        UpdateSelectedChannelUi(); // refreshes per-channel sensitivity controls for the selected channel
        RefreshProfileUi();
    }

    // Removed: SaveSettingsFromCurrentState() — replaced by FlushUiToSettings()

    private void SaveChannelsToSettings()
    {
        ChannelSettings[] previous = _settings.Channels;

        // IMPORTANT: SensitivityPercent is set in _settings.Channels[i] by FlushUiToSettings()
        // BEFORE this method is called. Preserve it by copying from 'previous' — do NOT move
        // this call above the SensitivityPercent assignment in FlushUiToSettings().
        _settings.Channels = _channels.Select((channel, i) => new ChannelSettings
        {
            TargetKey    = channel.TargetKey,
            FriendlyName = channel.FriendlyName,
            ButtonAction          = ChannelButtonActions.IsValid(channel.ButtonAction)                     ? channel.ButtonAction                     : ChannelButtonActions.ToggleAssignedMute,
            LongPressButtonAction = ChannelButtonActions.IsValidLongPressAction(channel.LongPressButtonAction)  ? channel.LongPressButtonAction  : ChannelButtonActions.NoAction,
            DoublePressButtonAction = ChannelButtonActions.IsValidDoublePressAction(channel.DoublePressButtonAction) ? channel.DoublePressButtonAction : ChannelButtonActions.NoAction,
            RebindFallback  = RebindFallbacks.IsValid(channel.RebindFallback) ? channel.RebindFallback : RebindFallbacks.ShowInactive,
            OledDisplayMode = DisplayModes.IsValidChannelMode(channel.OledDisplayMode) ? channel.OledDisplayMode : string.Empty,
            // Preserve fields that live only in ChannelSettings, not in ChannelMappingItem.
            SensitivityPercent = (i < previous.Length) ? previous[i].SensitivityPercent : -1,
            Presets = (i < previous.Length && previous[i].Presets != null)
                ? previous[i].Presets
                : new[] { new VolumePreset { Name = "", VolumePercent = 25 }, new VolumePreset { Name = "", VolumePercent = 50 }, new VolumePreset { Name = "", VolumePercent = 75 } },
        }).ToArray();

        _settings.ChannelTargetKeys = _settings.Channels.Select(channel => channel.TargetKey).ToArray();

        // Keep the active profile's Channels in sync with the live channel state.
        if (_settings.Profiles != null)
        {
            ProfileEntry? activeProfile = _settings.Profiles.FirstOrDefault(p => p.Name == _settings.ActiveProfileName);
            if (activeProfile != null)
            {
                activeProfile.Channels = _settings.Channels;
            }
        }
    }

    private void SaveCurrentWindowSize()
    {
        double widthToSave;
        double heightToSave;

        if (WindowState == WindowState.Normal)
        {
            widthToSave = Width;
            heightToSave = Height;
        }
        else
        {
            widthToSave = RestoreBounds.Width;
            heightToSave = RestoreBounds.Height;
        }

        if (widthToSave >= MinWidth && heightToSave >= MinHeight)
        {
            _settings.WindowWidth = widthToSave;
            _settings.WindowHeight = heightToSave;
        }
    }

    private void SaveSettings() => SettingsRepository.Save(_settings, Log);

    private string GetThemeModeFromUi()
    {
        if (ThemeLightRadioButton.IsChecked == true)
        {
            return ThemeModes.Light;
        }

        if (ThemeDarkRadioButton.IsChecked == true)
        {
            return ThemeModes.Dark;
        }

        return ThemeModes.FollowSystem;
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

    private static bool IsEspProtocolCompatible(string reportedProtocol)
    {
        if (string.IsNullOrWhiteSpace(reportedProtocol) || reportedProtocol.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return CompareVersionParts(reportedProtocol, RequiredProtocolVersion) >= 0;
    }

    private static int CompareVersionParts(string left, string right)
    {
        int[] leftParts = ParseVersionParts(left);
        int[] rightParts = ParseVersionParts(right);
        int count = Math.Max(leftParts.Length, rightParts.Length);

        for (int i = 0; i < count; i++)
        {
            int leftValue = i < leftParts.Length ? leftParts[i] : 0;
            int rightValue = i < rightParts.Length ? rightParts[i] : 0;

            if (leftValue != rightValue)
            {
                return leftValue.CompareTo(rightValue);
            }
        }

        return 0;
    }

    private static int[] ParseVersionParts(string version)
    {
        string cleaned = version.Trim().TrimStart('v', 'V');
        StringBuilder builder = new();

        foreach (char c in cleaned)
        {
            builder.Append(char.IsDigit(c) ? c : '.');
        }

        return builder.ToString()
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => int.TryParse(part, out int value) ? value : 0)
            .ToArray();
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
            ? new WpfSolidColorBrush(WpfColor.FromRgb(30, 30, 30))
            : new WpfSolidColorBrush(WpfColor.FromRgb(255, 255, 255));

        WpfBrush cardBackground = dark
            ? new WpfSolidColorBrush(WpfColor.FromRgb(43, 43, 43))
            : new WpfSolidColorBrush(WpfColor.FromRgb(255, 255, 255));

        WpfBrush appForeground = dark
            ? new WpfSolidColorBrush(WpfColor.FromRgb(245, 245, 245))
            : new WpfSolidColorBrush(WpfColor.FromRgb(17, 17, 17));

        WpfBrush secondaryForeground = dark
            ? new WpfSolidColorBrush(WpfColor.FromRgb(190, 190, 190))
            : new WpfSolidColorBrush(WpfColor.FromRgb(100, 100, 100));

        WpfBrush cardBorder = dark
            ? new WpfSolidColorBrush(WpfColor.FromRgb(85, 85, 85))
            : new WpfSolidColorBrush(WpfColor.FromRgb(221, 221, 221));

        WpfBrush buttonBackground = dark
            ? new WpfSolidColorBrush(WpfColor.FromRgb(48, 48, 52))
            : new WpfSolidColorBrush(WpfColor.FromRgb(238, 238, 238));

        WpfBrush buttonHoverBackground = dark
            ? new WpfSolidColorBrush(WpfColor.FromRgb(56, 56, 60))
            : new WpfSolidColorBrush(WpfColor.FromRgb(226, 226, 226));

        WpfBrush buttonPressedBackground = dark
            ? new WpfSolidColorBrush(WpfColor.FromRgb(42, 42, 46))
            : new WpfSolidColorBrush(WpfColor.FromRgb(214, 214, 214));

        WpfBrush buttonForeground = dark
            ? new WpfSolidColorBrush(WpfColor.FromRgb(245, 245, 245))
            : new WpfSolidColorBrush(WpfColor.FromRgb(17, 17, 17));

        WpfBrush buttonBorder = dark
            ? new WpfSolidColorBrush(WpfColor.FromRgb(95, 95, 100))
            : new WpfSolidColorBrush(WpfColor.FromRgb(136, 136, 136));

        WpfBrush listBackground = dark
            ? new WpfSolidColorBrush(WpfColor.FromRgb(35, 35, 35))
            : new WpfSolidColorBrush(WpfColor.FromRgb(255, 255, 255));

        WpfBrush headerBackground = dark
            ? new WpfSolidColorBrush(WpfColor.FromRgb(52, 52, 52))
            : new WpfSolidColorBrush(WpfColor.FromRgb(242, 242, 242));

        WpfBrush selectedBackground = dark
            ? new WpfSolidColorBrush(WpfColor.FromRgb(58, 78, 96))
            : new WpfSolidColorBrush(WpfColor.FromRgb(221, 238, 255));

        WpfBrush selectedForeground = dark
            ? new WpfSolidColorBrush(WpfColor.FromRgb(255, 255, 255))
            : new WpfSolidColorBrush(WpfColor.FromRgb(17, 17, 17));

        WpfBrush inputBackground = dark
            ? new WpfSolidColorBrush(WpfColor.FromRgb(50, 50, 54))
            : new WpfSolidColorBrush(WpfColor.FromRgb(255, 255, 255));

        WpfBrush popupBackground = dark
            ? new WpfSolidColorBrush(WpfColor.FromRgb(45, 45, 48))
            : new WpfSolidColorBrush(WpfColor.FromRgb(255, 255, 255));

        WpfBrush tabSelectedBackground = dark
            ? new WpfSolidColorBrush(WpfColor.FromRgb(58, 58, 62))
            : new WpfSolidColorBrush(WpfColor.FromRgb(255, 255, 255));

        WpfBrush tabSelectedForeground = dark
            ? new WpfSolidColorBrush(WpfColor.FromRgb(245, 245, 245))
            : new WpfSolidColorBrush(WpfColor.FromRgb(17, 17, 17));

        WpfBrush tabUnselectedBackground = dark
            ? new WpfSolidColorBrush(WpfColor.FromRgb(43, 43, 43))
            : new WpfSolidColorBrush(WpfColor.FromRgb(233, 233, 233));

        WpfBrush tabUnselectedForeground = dark
            ? new WpfSolidColorBrush(WpfColor.FromRgb(230, 230, 230))
            : new WpfSolidColorBrush(WpfColor.FromRgb(17, 17, 17));

        WpfBrush connectionGood = dark
            ? new WpfSolidColorBrush(WpfColor.FromRgb(95, 220, 120))
            : new WpfSolidColorBrush(WpfColor.FromRgb(0, 120, 20));

        WpfBrush previewSurfaceBackground = dark
            ? new WpfSolidColorBrush(WpfColor.FromRgb(37, 37, 40))
            : new WpfSolidColorBrush(WpfColor.FromRgb(247, 247, 247));

        WpfBrush previewSurfaceBorder = dark
            ? new WpfSolidColorBrush(WpfColor.FromRgb(78, 78, 82))
            : new WpfSolidColorBrush(WpfColor.FromRgb(200, 200, 200));

        WpfBrush previewEncoderOuterFill = dark
            ? new WpfSolidColorBrush(WpfColor.FromRgb(70, 70, 74))
            : new WpfSolidColorBrush(WpfColor.FromRgb(239, 239, 239));

        WpfBrush previewEncoderInnerFill = dark
            ? new WpfSolidColorBrush(WpfColor.FromRgb(92, 92, 96))
            : new WpfSolidColorBrush(WpfColor.FromRgb(250, 250, 250));

        WpfBrush previewEncoderStroke = dark
            ? new WpfSolidColorBrush(WpfColor.FromRgb(205, 205, 205))
            : new WpfSolidColorBrush(WpfColor.FromRgb(68, 68, 68));

        WpfBrush previewEncoderActiveBackground = dark
            ? new WpfSolidColorBrush(WpfColor.FromRgb(58, 78, 96))
            : new WpfSolidColorBrush(WpfColor.FromRgb(221, 238, 255));

        WpfBrush connectionBad = dark
            ? new WpfSolidColorBrush(WpfColor.FromRgb(255, 120, 120))
            : new WpfSolidColorBrush(WpfColor.FromRgb(170, 31, 31));

        WpfBrush warning = dark
            ? new WpfSolidColorBrush(WpfColor.FromRgb(255, 190, 100))
            : new WpfSolidColorBrush(WpfColor.FromRgb(176, 96, 0));

        WpfBrush warningBannerBackground = dark
            ? new WpfSolidColorBrush(WpfColor.FromRgb(0x3D, 0x2E, 0x00))
            : new WpfSolidColorBrush(WpfColor.FromRgb(0xFF, 0xF3, 0xCD));

        WpfBrush warningBannerBorder = dark
            ? new WpfSolidColorBrush(WpfColor.FromRgb(0x6B, 0x4F, 0x00))
            : new WpfSolidColorBrush(WpfColor.FromRgb(0xFF, 0xEE, 0xBA));

        WpfBrush warningBannerForeground = dark
            ? new WpfSolidColorBrush(WpfColor.FromRgb(0xFF, 0xD9, 0x66))
            : new WpfSolidColorBrush(WpfColor.FromRgb(0x85, 0x64, 0x04));

        // Update banner — blue info style
        WpfBrush updateBannerBackground = dark
            ? new WpfSolidColorBrush(WpfColor.FromRgb(0x1A, 0x2E, 0x3D))
            : new WpfSolidColorBrush(WpfColor.FromRgb(0xE8, 0xF4, 0xFD));

        WpfBrush updateBannerBorder = dark
            ? new WpfSolidColorBrush(WpfColor.FromRgb(0x2A, 0x4A, 0x6D))
            : new WpfSolidColorBrush(WpfColor.FromRgb(0xBE, 0xE3, 0xF8));

        WpfBrush updateBannerForeground = dark
            ? new WpfSolidColorBrush(WpfColor.FromRgb(0x90, 0xCA, 0xF9))
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

    private void ClearDebugConsoleButton_Click(object sender, RoutedEventArgs e)
    {
        _debugConsoleLines.Clear();
        DebugConsoleTextBox?.Clear();
    }

    private void CopyDebugConsoleButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Windows.Clipboard.SetText(DebugConsoleTextBox?.Text ?? string.Empty);
            Log("Copied debug console to clipboard.");
        }
        catch (Exception ex)
        {
            Log($"Could not copy debug console: {ex.Message}");
        }
    }

    private void SaveDebugSnapshotButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string file = Path.Combine(GetLogDirectory(), $"debug-snapshot-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            File.WriteAllText(file, DebugConsoleTextBox?.Text ?? string.Empty);
            Log($"Saved debug snapshot: {file}");
        }
        catch (Exception ex)
        {
            Log($"Could not save debug snapshot: {ex.Message}");
        }
    }

    private void OpenCurrentLogFileButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(GetLogDirectory());
            if (!File.Exists(_logPath))
            {
                File.WriteAllText(_logPath, string.Empty);
            }
            Process.Start(new ProcessStartInfo { FileName = _logPath, UseShellExecute = true })?.Dispose();
            Log($"Opened current log file: {_logPath}");
        }
        catch (Exception ex)
        {
            Log($"Failed to open current log file: {ex.Message}");
            System.Windows.MessageBox.Show(this, $"Could not open the current log file.\n\n{ex.Message}", "Open Current Log", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void CopyLogFolderPathButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Windows.Clipboard.SetText(GetLogDirectory());
            Log("Copied log folder path to clipboard.");
        }
        catch (Exception ex)
        {
            Log($"Failed to copy log folder path: {ex.Message}");
        }
    }

    private void ExportDiagnosticsZipButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string exportFolder = Path.Combine(GetLogDirectory(), "exports");
            Directory.CreateDirectory(exportFolder);
            string zipPath = Path.Combine(exportFolder, $"pc-volume-controller-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip");

            using FileStream zipStream = File.Create(zipPath);
            using ZipArchive archive = new(zipStream, ZipArchiveMode.Create);
            HashSet<string> addedEntries = new(StringComparer.OrdinalIgnoreCase);

            AddTextToZip(archive, "diagnostics-summary.txt", BuildDiagnosticsSummary());
            addedEntries.Add("diagnostics-summary.txt");

            string settingsPath = SettingsRepository.GetPath();
            if (File.Exists(settingsPath))
            {
                string entryName = "settings.json";
                archive.CreateEntryFromFile(settingsPath, entryName);
                addedEntries.Add(entryName);
            }

            if (File.Exists(_logPath))
            {
                string entryName = ToZipEntryPath("logs", Path.GetFileName(_logPath));
                archive.CreateEntryFromFile(_logPath, entryName);
                addedEntries.Add(entryName);
            }

            foreach (string logFile in Directory.GetFiles(GetLogDirectory(), "dashboard-*.log").OrderByDescending(File.GetLastWriteTime).Take(10))
            {
                string entryName = ToZipEntryPath("logs", Path.GetFileName(logFile));
                if (addedEntries.Add(entryName))
                {
                    archive.CreateEntryFromFile(logFile, entryName);
                }
            }

            Log($"Exported diagnostics zip: {zipPath}");
            Process.Start(new ProcessStartInfo { FileName = exportFolder, UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            Log($"Diagnostics export failed: {ex.Message}");
            System.Windows.MessageBox.Show(this, $"Diagnostics export failed.\n\n{ex.Message}", "Export Diagnostics", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void AddTextToZip(ZipArchive archive, string entryName, string text)
    {
        ZipArchiveEntry entry = archive.CreateEntry(entryName);
        using StreamWriter writer = new(entry.Open());
        writer.Write(text);
    }

    private static string ToZipEntryPath(params string[] parts)
    {
        return string.Join("/", parts.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private string BuildDiagnosticsSummary()
    {
        StringBuilder sb = new();
        sb.AppendLine($"Dashboard version: v{DashboardVersion}");
        sb.AppendLine($"Required ESP32 protocol: v{RequiredProtocolVersion}");
        sb.AppendLine($"Connected ESP32 firmware: {_espFirmwareName}");
        sb.AppendLine($"Connected ESP32 protocol: {_espProtocolVersion}");
        sb.AppendLine($"Connected ESP32 channels: {_espChannelCount}");
        sb.AppendLine($"Active COM port: {(_serialService.IsConnected ? _serialService.PortName : "none")}");
        sb.AppendLine($"Remembered controller port: {(string.IsNullOrWhiteSpace(_settings.LastComPort) ? "none" : _settings.LastComPort)}");
        string[] ports = GetAvailableComPorts();
        sb.AppendLine($"Actual COM ports: {(ports.Length == 0 ? "none" : string.Join(", ", ports))}");
        sb.AppendLine($"Auto-connect: {_settings.AutoConnectOnLaunch}");
        sb.AppendLine($"Scan all if remembered missing: {_settings.ScanAllComPortsIfRememberedMissing}");
        sb.AppendLine($"Manual disconnect requested: {_manualDisconnectRequested}");
        sb.AppendLine($"Safe mode: {_safeMode}");
        sb.AppendLine($"Active log: {_logPath}");
        sb.AppendLine($"Settings path: {SettingsRepository.GetPath()}");
        return sb.ToString();
    }


    private void ExportSetupButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            FlushUiToSettings();
            Microsoft.Win32.SaveFileDialog dialog = new()
            {
                Title = "Export PC Volume Controller setup",
                Filter = "JSON setup file (*.json)|*.json|All files (*.*)|*.*",
                FileName = $"PcVolumeController-setup-v{DashboardVersion}-{DateTime.Now:yyyyMMdd-HHmmss}.json"
            };

            if (dialog.ShowDialog(this) != true)
            {
                return;
            }

            string json = JsonSerializer.Serialize(_settings, JsonWriteOptions);
            File.WriteAllText(dialog.FileName, json);
            Log($"Exported setup to: {dialog.FileName}");
            System.Windows.MessageBox.Show(this, "Setup exported successfully.", "Export Setup", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Log($"Setup export failed: {ex.Message}");
            System.Windows.MessageBox.Show(this, $"Setup export failed.\n\n{ex.Message}", "Export Setup", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ImportSetupButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Microsoft.Win32.OpenFileDialog dialog = new()
            {
                Title = "Import PC Volume Controller setup",
                Filter = "JSON setup file (*.json)|*.json|All files (*.*)|*.*"
            };

            if (dialog.ShowDialog(this) != true)
            {
                return;
            }

            string json = File.ReadAllText(dialog.FileName);
            DashboardSettings imported = JsonSerializer.Deserialize<DashboardSettings>(json) ?? throw new InvalidDataException("The selected file did not contain valid setup data.");
            SettingsRepository.Normalize(imported, ChannelCount, MaxEncoderSensitivityPercent);

            MessageBoxResult result = System.Windows.MessageBox.Show(this,
                "Import this setup? A backup of the current setup will be created first.",
                "Import Setup",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes)
            {
                return;
            }

            BackupCurrentSettingsFile("before-import");
            _settings = imported;
            SaveSettings();
            ApplySettingsToUi();
            ApplyStartupSetting();
            ApplyTheme();
            BuildChannels();
            BuildLogicalChannelComboBox();
            ApplySettingsToChannels();
            SelectChannel(Math.Clamp(_settings.SelectedChannelIndex, 0, ChannelCount - 1));
            ForceRefreshComPorts("setup import", preserveSelection: false);
            RefreshAudioSessions();
            RefreshAllChannelStates();
            QueueFullStateSend("setup import");
            Log($"Imported setup from: {dialog.FileName}");
        }
        catch (Exception ex)
        {
            Log($"Setup import failed: {ex.Message}");
            System.Windows.MessageBox.Show(this, $"Setup import failed.\n\n{ex.Message}", "Import Setup", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void BackupCurrentSettingsFile(string reason) =>
        SettingsRepository.Backup(reason, Log);

    private void ClearRememberedControllerButton_Click(object sender, RoutedEventArgs e)
    {
        _settings.LastComPort = string.Empty;
        _rejectedComPorts.Clear();
        _phantomComPorts.Clear();
        SaveSettings();
        ForceRefreshComPorts("clear remembered controller", preserveSelection: false);
        Log("Cleared remembered controller COM port and temporary COM-port cooldowns.");
    }

    private void AboutButton_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new AboutDialog(DashboardVersion, _espFirmwareName, _espProtocolVersion)
        {
            Owner = this
        };
        dlg.ShowDialog();
    }

    private void FactoryResetSetupButton_Click(object sender, RoutedEventArgs e)
    {
        MessageBoxResult result = System.Windows.MessageBox.Show(this, "Reset setup to defaults? This clears channel mappings, remembered controller port, startup settings, and reconnect preferences.", "Factory Reset Setup", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        BackupCurrentSettingsFile("before-factory-reset");
        _settings = DashboardSettings.CreateDefault();
        SaveSettings();
        ApplySettingsToUi();
        ApplySettingsToChannels();
        ForceRefreshComPorts("factory reset setup", preserveSelection: false);
        QueueFullStateSend("factory reset setup");
        Log("Factory reset setup completed.");
    }

    private void RegisterHardwareButtonEvent(string[] parts)
    {
        try
        {
            int channel = 0;
            if (parts.Length > 1)
            {
                int.TryParse(parts[1], out channel);
            }
            if (channel < 0 || channel >= _hardwareButtonSeen.Length)
            {
                channel = 0;
            }
            _hardwareButtonSeen[channel] = true;
            UpdateHardwareTestSummary($"Button {channel + 1}: pressed");
        }
        catch
        {
        }
    }

    private void UpdateHardwareTestSummary(string latestEvent)
    {
        if (HardwareTestStatusTextBlock == null)
        {
            return;
        }
        StringBuilder sb = new();
        sb.AppendLine($"Latest event: {latestEvent}");
        sb.AppendLine();
        for (int i = 0; i < ExpectedChannelCount; i++)
        {
            sb.AppendLine($"Channel {i + 1}: encoder count {_hardwareEncoderCounts[i]}, button seen {(_hardwareButtonSeen[i] ? "yes" : "no")}");
        }
        HardwareTestStatusTextBlock.Text = sb.ToString();
    }

    private void ResetHardwareTestButton_Click(object sender, RoutedEventArgs e)
    {
        Array.Clear(_hardwareEncoderCounts, 0, _hardwareEncoderCounts.Length);
        Array.Clear(_hardwareButtonSeen, 0, _hardwareButtonSeen.Length);
        UpdateHardwareTestSummary("Reset");
        Log("Hardware test counters reset.");
    }

    private void SendHardwareTestPatternButton_Click(object sender, RoutedEventArgs e)
    {
        WriteSerialLine(ProtocolCommands.TestDisplay, logOutgoing: true);
    }

    private void SendShowOledIdentButton_Click(object sender, RoutedEventArgs e)
    {
        WriteSerialLine(ProtocolCommands.ShowIdent, logOutgoing: true);
    }

    private void SendHardwareSleepButton_Click(object sender, RoutedEventArgs e)
    {
        if (CanSendSleepWakeTestCommand("SLEEP"))
        {
            WriteSerialLine($"{ProtocolCommands.Sleep},TEST", logOutgoing: true);
        }
    }

    private void SendHardwareWakeButton_Click(object sender, RoutedEventArgs e)
    {
        if (CanSendSleepWakeTestCommand("WAKE"))
        {
            WriteSerialLine($"{ProtocolCommands.Wake},TEST", logOutgoing: true);
        }
    }

    private void SelectFirmwareBinButton_Click(object sender, RoutedEventArgs e)
    {
        Microsoft.Win32.OpenFileDialog dialog = new()
        {
            Title = "Select ESP32 firmware .bin file",
            Filter = "ESP32 firmware binary (*.bin)|*.bin|All files (*.*)|*.*"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        _selectedFirmwareBinPath = dialog.FileName;
        UpdateSelectedFirmwareBinDisplay();
        Log($"Selected firmware .bin file: {_selectedFirmwareBinPath}");
    }

    private async void FlashFirmwareButton_Click(object sender, RoutedEventArgs e)
    {
        await RunFirmwareFlashAsync();
    }

    private async Task RunFirmwareFlashAsync()
    {
        FirmwareFlashOutputTextBox.Text = string.Empty;
        if (FlashFirmwareButton != null) FlashFirmwareButton.IsEnabled = false;

        string? firmwareBin = ResolveFirmwareBinPath();
        if (string.IsNullOrWhiteSpace(firmwareBin) || !File.Exists(firmwareBin))
        {
            AppendFirmwareFlashOutput("No firmware .bin file selected or bundled.");
            AppendFirmwareFlashOutput("Use Select .bin File, or add a compiled firmware binary to firmware_bin/.");
            Log("Firmware flash cancelled: no firmware .bin file available.");
            return;
        }

        string esptoolPath = ResolveEsptoolPath();
        if (string.IsNullOrWhiteSpace(esptoolPath) || !File.Exists(esptoolPath))
        {
            AppendFirmwareFlashOutput("esptool was not found.");
            AppendFirmwareFlashOutput("Expected location: tools/esptool.exe");
            AppendFirmwareFlashOutput("See tools/esptool_setup_instructions.txt for setup instructions.");
            Log("Firmware flash cancelled: tools/esptool.exe was not found.");
            return;
        }

        string port = _settings.LastComPort;
        if (_serialService.IsConnected)
        {
            port = _serialService.PortName ?? port;
            Log("Firmware flash requested. Disconnecting dashboard serial first.");
            DisconnectSerial(sendDisconnectCommand: false, preserveLastControllerPort: true, refreshPortsAfterDisconnect: true);
        }

        if (string.IsNullOrWhiteSpace(port))
        {
            port = ComPortComboBox.SelectedItem as string ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(port))
        {
            AppendFirmwareFlashOutput("No COM port is selected or remembered for flashing.");
            Log("Firmware flash cancelled: no COM port available.");
            return;
        }

        _manualDisconnectRequested = true;
        AppendFirmwareFlashOutput($"Flashing {Path.GetFileName(firmwareBin)} to {port}.");
        AppendFirmwareFlashOutput("Auto-reconnect is paused while flashing.");
        Log($"Starting firmware flash. Port={port}, Bin={firmwareBin}, Tool={esptoolPath}");

        try
        {
            ProcessStartInfo psi = new()
            {
                FileName = esptoolPath,
                Arguments = $"--chip esp32s3 --port {port} --baud 921600 write_flash 0x10000 \"{firmwareBin}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using Process process = new() { StartInfo = psi, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, args) => { if (args.Data != null) Dispatcher.InvokeAsync(() => AppendFirmwareFlashOutput(args.Data)); };
            process.ErrorDataReceived += (_, args) => { if (args.Data != null) Dispatcher.InvokeAsync(() => AppendFirmwareFlashOutput(args.Data)); };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync();

            AppendFirmwareFlashOutput(process.ExitCode == 0 ? "Firmware flash completed." : $"Firmware flash failed with exit code {process.ExitCode}.");
            Log($"Firmware flash process exited with code {process.ExitCode}.");
            ShowTrayNotification(
                process.ExitCode == 0 ? "Firmware update complete" : "Firmware update failed",
                process.ExitCode == 0 ? "ESP32 flashing completed successfully." : $"ESP32 flashing failed with exit code {process.ExitCode}.");

            if (process.ExitCode == 0)
            {
                AppendFirmwareFlashOutput("Waiting for controller to reboot, then reconnecting...");
                _manualDisconnectRequested = false;
                _ = Dispatcher.InvokeAsync(() => TryAutoReconnect(GetAvailableComPorts()));
            }
        }
        catch (Exception ex)
        {
            AppendFirmwareFlashOutput($"Firmware flash error: {ex.Message}");
            Log($"Firmware flash error: {ex.Message}");
            ShowTrayNotification("Firmware update failed", ex.Message);
        }
        finally
        {
            if (FlashFirmwareButton != null) FlashFirmwareButton.IsEnabled = true;
        }
    }

    private string? ResolveFirmwareBinPath()
    {
        if (!string.IsNullOrWhiteSpace(_selectedFirmwareBinPath) && File.Exists(_selectedFirmwareBinPath))
        {
            return _selectedFirmwareBinPath;
        }

        string firmwareDirectory = Path.Combine(AppContext.BaseDirectory, "firmware_bin");
        if (!Directory.Exists(firmwareDirectory))
        {
            firmwareDirectory = Path.Combine(Environment.CurrentDirectory, "firmware_bin");
        }

        if (!Directory.Exists(firmwareDirectory))
        {
            return null;
        }

        return Directory.GetFiles(firmwareDirectory, "*.bin", SearchOption.TopDirectoryOnly)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    private string ResolveEsptoolPath()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "tools", "esptool.exe");
        if (File.Exists(path))
        {
            return path;
        }

        return Path.Combine(Environment.CurrentDirectory, "tools", "esptool.exe");
    }

    private void UpdateSelectedFirmwareBinDisplay()
    {
        if (SelectedFirmwareBinTextBlock == null)
        {
            return;
        }

        SelectedFirmwareBinTextBlock.Text = string.IsNullOrWhiteSpace(_selectedFirmwareBinPath)
            ? "Selected .bin file: none"
            : $"Selected .bin file: {_selectedFirmwareBinPath}";
    }

    private void AppendFirmwareFlashOutput(string line)
    {
        if (FirmwareFlashOutputTextBox != null)
        {
            FirmwareFlashOutputTextBox.AppendText($"{DateTime.Now:HH:mm:ss}  {line}{Environment.NewLine}");
            FirmwareFlashOutputTextBox.ScrollToEnd();
        }
    }

    private void OpenLogFolderButton_Click(object sender, RoutedEventArgs e)
    {
        OpenLogFolder();
    }

    private void OpenLogFolder()
    {
        try
        {
            string logDirectory = GetLogDirectory();
            Directory.CreateDirectory(logDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = logDirectory,
                UseShellExecute = true
            })?.Dispose();
            Log($"Opened log folder: {logDirectory}");
        }
        catch (Exception ex)
        {
            Log($"Failed to open log folder: {ex.Message}");
            System.Windows.MessageBox.Show(this, $"Could not open the log folder.\n\n{ex.Message}", "Open Log Folder", MessageBoxButton.OK, MessageBoxImage.Warning);
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

            if (FirmwareSourceTextBlock != null)
            {
                string firmwareDir = System.IO.Path.Combine(
                    System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? string.Empty,
                    $"Computer_Volume_Controller_v{RequiredProtocolVersion}");
                bool exists = System.IO.Directory.Exists(firmwareDir);
                FirmwareSourceTextBlock.Text = $"Bundled firmware source: Computer_Volume_Controller_v{RequiredProtocolVersion}" +
                                               (exists ? string.Empty : " (not found in build directory)");
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

    // ── Global Hotkey UI handlers ────────────────────────────────────────────────

    private void UpdateHotkeyLabels()
    {
        if (HotkeyMasterVolUpTextBlock    != null) HotkeyMasterVolUpTextBlock.Text    = _settings.Hotkeys.MasterVolumeUp.ToDisplayString();
        if (HotkeyMasterVolDownTextBlock  != null) HotkeyMasterVolDownTextBlock.Text  = _settings.Hotkeys.MasterVolumeDown.ToDisplayString();
        if (HotkeyToggleMasterMuteTextBlock != null) HotkeyToggleMasterMuteTextBlock.Text = _settings.Hotkeys.ToggleMasterMute.ToDisplayString();
        if (HotkeyCycleNextProfileTextBlock != null) HotkeyCycleNextProfileTextBlock.Text = _settings.Hotkeys.CycleNextProfile.ToDisplayString();
        if (HotkeyShowDashboardTextBlock  != null) HotkeyShowDashboardTextBlock.Text  = _settings.Hotkeys.ShowDashboard.ToDisplayString();
    }

    private void SetHotkeyBinding(string actionName, HotkeyBinding current, Action<HotkeyBinding> apply)
    {
        var dialog = new HotkeyPickerDialog(actionName, current) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Result != null)
        {
            apply(dialog.Result);
            FlushUiToSettings();
            UpdateHotkeyLabels();
            RegisterAllHotkeys();
        }
    }

    private void HotkeyMasterVolUp_Set(object sender, RoutedEventArgs e)    => SetHotkeyBinding("Master Volume Up",   _settings.Hotkeys.MasterVolumeUp,   b => _settings.Hotkeys.MasterVolumeUp   = b);
    private void HotkeyMasterVolDown_Set(object sender, RoutedEventArgs e)  => SetHotkeyBinding("Master Volume Down", _settings.Hotkeys.MasterVolumeDown, b => _settings.Hotkeys.MasterVolumeDown = b);
    private void HotkeyToggleMasterMute_Set(object sender, RoutedEventArgs e) => SetHotkeyBinding("Toggle Master Mute", _settings.Hotkeys.ToggleMasterMute, b => _settings.Hotkeys.ToggleMasterMute = b);
    private void HotkeyCycleNextProfile_Set(object sender, RoutedEventArgs e) => SetHotkeyBinding("Next Profile",       _settings.Hotkeys.CycleNextProfile, b => _settings.Hotkeys.CycleNextProfile = b);
    private void HotkeyShowDashboard_Set(object sender, RoutedEventArgs e)  => SetHotkeyBinding("Show Dashboard",      _settings.Hotkeys.ShowDashboard,    b => _settings.Hotkeys.ShowDashboard    = b);

    private void ClearHotkeyBinding(Action<HotkeyBinding> apply)
    {
        apply(new HotkeyBinding { Enabled = false });
        FlushUiToSettings();
        UpdateHotkeyLabels();
        RegisterAllHotkeys();
    }

    private void HotkeyMasterVolUp_Clear(object sender, RoutedEventArgs e)    => ClearHotkeyBinding(b => _settings.Hotkeys.MasterVolumeUp   = b);
    private void HotkeyMasterVolDown_Clear(object sender, RoutedEventArgs e)  => ClearHotkeyBinding(b => _settings.Hotkeys.MasterVolumeDown = b);
    private void HotkeyToggleMasterMute_Clear(object sender, RoutedEventArgs e) => ClearHotkeyBinding(b => _settings.Hotkeys.ToggleMasterMute = b);
    private void HotkeyCycleNextProfile_Clear(object sender, RoutedEventArgs e) => ClearHotkeyBinding(b => _settings.Hotkeys.CycleNextProfile = b);
    private void HotkeyShowDashboard_Clear(object sender, RoutedEventArgs e)  => ClearHotkeyBinding(b => _settings.Hotkeys.ShowDashboard    = b);

    protected override void OnClosed(EventArgs e)
    {
        _statePollTimer?.Dispose();
        _heartbeatTimer?.Dispose();
        _audioSessionRefreshTimer?.Dispose();
        _comPortRefreshTimer?.Dispose();
        _fullStateSendCoalesceTimer?.Dispose();
        _smoothingTimer?.Dispose();

        for (int i = 0; i < _encoderCoalesceTimers.Length; i++)
        {
            _encoderCoalesceTimers[i]?.Dispose();
        }

        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;

        DisconnectSerial();

        _audioService.Dispose();
        _defaultRenderDevice  = null;
        _defaultCaptureDevice = null;

        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        timeEndPeriod(1);

        IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
            UnregisterAllHotkeys(hwnd);

        base.OnClosed(e);
    }

}

public static class ThemeModes
{
    public const string FollowSystem = "FollowSystem";
    public const string Light = "Light";
    public const string Dark = "Dark";
}

public static class ChannelButtonActions
{
    public const string SelectNextChannel = "SelectNextChannel";
    public const string ToggleAssignedMute = "ToggleAssignedMute";
    public const string NoAction = "NoAction";
    public const string CycleNextProfile = "CycleNextProfile";
    public const string CycleOutputDevice = "CycleOutputDevice";
    public const string ApplyPreset1 = "ApplyPreset1";
    public const string ApplyPreset2 = "ApplyPreset2";
    public const string ApplyPreset3 = "ApplyPreset3";

    public static bool IsValid(string? action)
    {
        return action is SelectNextChannel or ToggleAssignedMute or NoAction or CycleNextProfile or CycleOutputDevice
            or ApplyPreset1 or ApplyPreset2 or ApplyPreset3;
    }

    // Long press and double press only support ToggleAssignedMute, NoAction, CycleNextProfile, CycleOutputDevice, and ApplyPreset*.
    public static bool IsValidLongPressAction(string? action)
    {
        return action is ToggleAssignedMute or NoAction or CycleNextProfile or CycleOutputDevice
            or ApplyPreset1 or ApplyPreset2 or ApplyPreset3;
    }

    public static bool IsValidDoublePressAction(string? action)
    {
        return action is ToggleAssignedMute or NoAction or CycleNextProfile or CycleOutputDevice
            or ApplyPreset1 or ApplyPreset2 or ApplyPreset3;
    }
}

public sealed class HotkeyBinding
{
    public bool Enabled { get; set; }
    public int Modifiers { get; set; }   // MOD_ALT=1, MOD_CONTROL=2, MOD_SHIFT=4, MOD_WIN=8
    public int VirtualKey { get; set; }  // Win32 virtual-key code; 0 = unassigned

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsAssigned => Enabled && VirtualKey != 0;

    public string ToDisplayString()
    {
        if (!IsAssigned) return "(unassigned)";
        var parts = new System.Collections.Generic.List<string>();
        if ((Modifiers & 8) != 0) parts.Add("Win");
        if ((Modifiers & 2) != 0) parts.Add("Ctrl");
        if ((Modifiers & 4) != 0) parts.Add("Shift");
        if ((Modifiers & 1) != 0) parts.Add("Alt");
        string keyName = ((Forms.Keys)VirtualKey).ToString();
        parts.Add(keyName);
        return string.Join("+", parts);
    }
}

public sealed class HotkeySettings
{
    public HotkeyBinding MasterVolumeUp   { get; set; } = new();
    public HotkeyBinding MasterVolumeDown { get; set; } = new();
    public HotkeyBinding ToggleMasterMute { get; set; } = new();
    public HotkeyBinding CycleNextProfile { get; set; } = new();
    public HotkeyBinding ShowDashboard    { get; set; } = new();
}

public static class OledIdleActions
{
    public const string Off = "Off";
    public const string DimTo10 = "DimTo10";
    public const string DimTo20 = "DimTo20";
    public const string DimTo30 = "DimTo30";
    public const string DimTo40 = "DimTo40";
    public const string DimTo50 = "DimTo50";
    public const string DimTo60 = "DimTo60";
    public const string DimTo70 = "DimTo70";

    public static bool IsValid(string action)
    {
        return action is Off or DimTo10 or DimTo20 or DimTo30 or DimTo40 or DimTo50 or DimTo60 or DimTo70;
    }
}

/// <summary>
/// All serial protocol command strings exchanged between the dashboard and firmware.
/// Centralising them here means a protocol change only requires editing this one class.
/// </summary>
public static class ProtocolCommands
{
    // ── Inbound (ESP32 → dashboard) ──────────────────────────────────────────
    public const string Hello         = "HELLO";
    public const string Pong          = "PONG";
    public const string Debug         = "DBG";
    public const string EncoderTurn   = "ENC";
    public const string ButtonLegacy  = "BTN";
    public const string ButtonShort   = "BTN_SHORT";
    public const string ButtonLong    = "BTN_LONG";
    public const string ButtonDouble  = "BTN_DOUBLE";
    public const string Sleeping      = "SLEEPING";
    public const string Awake         = "AWAKE";
    public const string OledCfgAck    = "OLEDCFG_ACK";
    public const string OledIdleStart = "OLED_IDLE_START";
    public const string OledIdleEnd   = "OLED_IDLE_END";
    public const string Error         = "ERR";

    // ── Outbound (dashboard → ESP32) ─────────────────────────────────────────
    public const string Sleep         = "SLEEP";
    public const string Wake          = "WAKE";
    public const string Ping          = "PING";
    public const string HelloQuery    = "HELLO?";
    public const string Disconnect    = "DISCONNECT";
    public const string ChannelState  = "CHSTATE";
    public const string DisplayMode   = "DISPMODE";
    public const string OledConfig    = "OLEDCFG";
    public const string TestDisplay   = "TEST_DISPLAY";
    public const string ShowIdent     = "SHOW_IDENT";

    // ── Display mode protocol values ─────────────────────────────────────────
    public const string DisplayModeAppVolume   = "APP_VOLUME";
    public const string DisplayModeLargeVolume = "LARGE_VOLUME";
    public const string DisplayModeMuteStatus  = "MUTE_STATUS";
    public const string DisplayModeAppName     = "APP_OR_DEVICE_NAME";
    public const string DisplayModeBarPercent  = "BAR_PERCENT";
}

public static class DisplayModes
{
    public const string AppNameAndVolume = "AppNameAndVolume";
    public const string LargeVolume = "LargeVolume";
    public const string MuteStatus = "MuteStatus";
    public const string AppOrDeviceName = "AppOrDeviceName";
    public const string BarPercent = "BarPercent";

    /// <summary>
    /// Returns true if <paramref name="mode"/> is a valid per-channel OLED display mode.
    /// Empty string is valid — it means "inherit the global default".
    /// </summary>
    public static bool IsValidChannelMode(string? mode)
    {
        if (string.IsNullOrEmpty(mode)) return true;
        return mode is AppNameAndVolume or LargeVolume or MuteStatus or AppOrDeviceName or BarPercent;
    }
}

public static class AccelerationPresets
{
    public const string None = "None";
    public const string Light = "Light";
    public const string Medium = "Medium";
    public const string Aggressive = "Aggressive";
    public const string Custom = "Custom";

    public static bool IsValid(string? v) => v is None or Light or Medium or Aggressive or Custom;
}

public static class SmoothingSpeed
{
    public const string Fast = "Fast";
    public const string Normal = "Normal";
    public const string Slow = "Slow";

    public static bool IsValid(string? v) => v is Fast or Normal or Slow;
}

/// <summary>
/// A named set of channel assignments that can be switched at runtime.
/// Profiles store the six channel settings; all other dashboard settings are global.
/// </summary>
public sealed class ProfileEntry
{
    public string Name { get; set; } = "Default";
    public ChannelSettings[] Channels { get; set; } = DashboardSettings.CreateDefaultChannels();
}

public sealed class DashboardSettings
{
    // Incremented whenever a migration runs in NormalizeSettings so future
    // migrations can be gated on the previous version number.
    public int SettingsVersion { get; set; } = 0;

    public string LastComPort { get; set; } = string.Empty;
    public bool AutoConnectOnLaunch { get; set; } = true;
    public bool FirstRunWizardCompleted { get; set; } = true;
    public bool ScanAllComPortsIfRememberedMissing { get; set; } = true;
    public bool MinimizeToTray { get; set; }
    public bool StartMinimizedToTray { get; set; }
    public bool StartWithWindows { get; set; }
    public bool AdvancedDebugLogging { get; set; }
    public int SelectedChannelIndex { get; set; }

    public int EncoderSensitivityPercent { get; set; } = 50;

    // Encoder Feel — acceleration and smoothing (added v2.16)
    public bool AccelerationEnabled { get; set; } = false;
    public string AccelerationPreset { get; set; } = AccelerationPresets.Medium;
    public bool VolumeSmoothingEnabled { get; set; } = false;
    public string VolumeSmoothingSpeed { get; set; } = SmoothingSpeed.Normal;

    // Custom acceleration curve (used when AccelerationPreset == "Custom")
    // AccelThresholdMs   — encoder interval (ms) below which full acceleration applies;
    //                      higher = boost activates at slower turning speeds.
    // AccelMaxMultiplier — step multiplier at maximum speed (interval ≈ 0 ms).
    // AccelCurveExponent — shape of the ramp; < 1 = early kick-in, 1 = linear, > 1 = late.
    public int   AccelThresholdMs     { get; set; } = 150;
    public float AccelMaxMultiplier   { get; set; } = 8.0f;
    public float AccelCurveExponent   { get; set; } = 0.5f;

    public string ThemeMode { get; set; } = ThemeModes.FollowSystem;
    public string OledDisplayMode { get; set; } = DisplayModes.AppNameAndVolume;
    public int OledBrightnessPercent { get; set; } = 100;
    public int OledSleepTimeoutMinutes { get; set; } = 2;
    public string OledConnectedIdleAction { get; set; } = OledIdleActions.DimTo30;
    public int OledConnectedIdleTimeoutMinutes { get; set; } = 10;
    public bool OledAntiBurnInEnabled { get; set; } = true;

    public double WindowWidth { get; set; } = 1120;
    public double WindowHeight { get; set; } = 800;

    public ChannelSettings[] Channels { get; set; } = CreateDefaultChannels();

    public string[] ChannelTargetKeys { get; set; } = Array.Empty<string>();

    // Named profiles (v2.25+). Each profile stores its own set of 6 channel settings.
    // The active profile's Channels array is always mirrored into the Channels property
    // above so older code paths continue to work unchanged.
    public List<ProfileEntry> Profiles { get; set; } = new();
    public string ActiveProfileName { get; set; } = string.Empty;

    // Global system hotkeys (v2.29+).
    public HotkeySettings Hotkeys { get; set; } = new HotkeySettings();

    // On-screen volume overlay (v2.34+).
    public bool OverlayEnabled { get; set; } = true;
    public string OverlayPosition { get; set; } = "BottomCenter";
    public double OverlayTimeoutSeconds { get; set; } = 2.5;

    // Output device cycle list (v2.35+). Device IDs included in the cycle, in order.
    public List<string> OutputDeviceCycleList { get; set; } = new();

    public static DashboardSettings CreateDefault()
    {
        return new DashboardSettings
        {
            SettingsVersion = 1,
            AutoConnectOnLaunch = true,
            FirstRunWizardCompleted = true,
            ScanAllComPortsIfRememberedMissing = true,
            Channels = CreateDefaultChannels(),
            ChannelTargetKeys = CreateDefaultChannels().Select(channel => channel.TargetKey).ToArray()
        };
    }

    public static ChannelSettings[] CreateDefaultChannels()
    {
        return new[]
        {
            new ChannelSettings { TargetKey = "MASTER",     FriendlyName = "Master",  ButtonAction = ChannelButtonActions.ToggleAssignedMute, LongPressButtonAction = ChannelButtonActions.NoAction, DoublePressButtonAction = ChannelButtonActions.NoAction },
            new ChannelSettings { TargetKey = "PROC:chrome", FriendlyName = "Browser", ButtonAction = ChannelButtonActions.ToggleAssignedMute, LongPressButtonAction = ChannelButtonActions.NoAction, DoublePressButtonAction = ChannelButtonActions.NoAction },
            new ChannelSettings { TargetKey = "PROC:Spotify", FriendlyName = "Music",  ButtonAction = ChannelButtonActions.ToggleAssignedMute, LongPressButtonAction = ChannelButtonActions.NoAction, DoublePressButtonAction = ChannelButtonActions.NoAction },
            new ChannelSettings { TargetKey = "PROC:Discord", FriendlyName = "Discord", ButtonAction = ChannelButtonActions.ToggleAssignedMute, LongPressButtonAction = ChannelButtonActions.NoAction, DoublePressButtonAction = ChannelButtonActions.NoAction },
            new ChannelSettings { TargetKey = "",             FriendlyName = "",        ButtonAction = ChannelButtonActions.ToggleAssignedMute, LongPressButtonAction = ChannelButtonActions.NoAction, DoublePressButtonAction = ChannelButtonActions.NoAction },
            new ChannelSettings { TargetKey = "",             FriendlyName = "",        ButtonAction = ChannelButtonActions.ToggleAssignedMute, LongPressButtonAction = ChannelButtonActions.NoAction, DoublePressButtonAction = ChannelButtonActions.NoAction }
        };
    }
}

public sealed class ChannelSettings
{
    public string TargetKey { get; set; } = string.Empty;
    public string FriendlyName { get; set; } = string.Empty;
    public string ButtonAction { get; set; } = ChannelButtonActions.ToggleAssignedMute;
    public string LongPressButtonAction { get; set; } = ChannelButtonActions.NoAction;
    public string DoublePressButtonAction { get; set; } = ChannelButtonActions.NoAction;

    // What to do when the assigned app is not running.
    // ShowInactive: grey out channel row + OLED shows "App offline"
    // DoNothing:    channel stays silently inactive (previous behaviour)
    public string RebindFallback { get; set; } = RebindFallbacks.ShowInactive;

    // Per-channel OLED display mode.
    // Empty string = inherit the global mode from OLED Setup.
    // Non-empty = override with this specific mode for this channel.
    public string OledDisplayMode { get; set; } = string.Empty;

    // Per-channel encoder sensitivity override.
    // -1 = inherit the global EncoderSensitivityPercent.
    // 0–500 = use this value for this channel only.
    public int SensitivityPercent { get; set; } = -1;

    // Per-channel volume presets. Index 0 = Preset 1, 1 = Preset 2, 2 = Preset 3.
    public VolumePreset[] Presets { get; set; } = new[]
    {
        new VolumePreset { Name = "", VolumePercent = 25 },
        new VolumePreset { Name = "", VolumePercent = 50 },
        new VolumePreset { Name = "", VolumePercent = 75 },
    };
}

public sealed class VolumePreset
{
    public string Name { get; set; } = "";
    public int VolumePercent { get; set; } = 50;
}

public sealed class AudioTargetItem
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string ProcessName { get; set; } = string.Empty;
    public int ProcessId { get; set; }
    public AudioSessionControl? Session { get; set; }
    public int Volume { get; set; }
    public bool Muted { get; set; }
    public string State { get; set; } = string.Empty;
    public bool IsMaster { get; set; }
    public bool IsMicInput { get; set; }

    public bool IsActiveOrMaster => IsMaster || IsMicInput || Session != null;

    public string VolumeDisplay => $"{Volume}%";
    public string MuteDisplay => Muted ? "Yes" : "No";

    public override string ToString()
    {
        return Label;
    }

    public static AudioTargetItem CreateMaster()
    {
        return new AudioTargetItem
        {
            Key = "MASTER",
            Label = "Master",
            ProcessName = "Windows",
            ProcessId = 0,
            Volume = 0,
            Muted = false,
            State = "Active",
            IsMaster = true
        };
    }

    public static AudioTargetItem CreateMic()
    {
        return new AudioTargetItem
        {
            Key = "MIC_INPUT",
            Label = "Microphone Input",
            ProcessName = string.Empty,
            ProcessId = 0,
            Session = null,
            Volume = 0,
            Muted = false,
            State = "Active",
            IsMaster = false,
            IsMicInput = true
        };
    }
}

public sealed class ChannelMappingItem
{
    public int ChannelIndex { get; set; }
    public int ChannelNumber { get; set; }
    public string TargetKey { get; set; } = string.Empty;
    public string AssignedLabel { get; set; } = "Unassigned";
    public string FriendlyName { get; set; } = string.Empty;
    public string ButtonAction { get; set; } = ChannelButtonActions.ToggleAssignedMute;
    public string LongPressButtonAction { get; set; } = ChannelButtonActions.NoAction;
    public string DoublePressButtonAction { get; set; } = ChannelButtonActions.NoAction;
    public int Volume { get; set; }
    public bool Muted { get; set; } = true;
    public string Status { get; set; } = "Unassigned";

    // True when the channel has a PROC: assignment but the app is not currently running.
    // Used by the ListView ItemContainerStyle to grey out the row.
    public bool IsAppOffline { get; set; }

    // Fallback behaviour when the assigned app is not running.
    public string RebindFallback { get; set; } = RebindFallbacks.ShowInactive;

    // Per-channel OLED display mode override. Empty = inherit global mode.
    public string OledDisplayMode { get; set; } = string.Empty;

    public string ChannelDisplay => ChannelNumber.ToString();

    public string DisplayLabel
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(FriendlyName))
            {
                return FriendlyName;
            }

            if (!string.IsNullOrWhiteSpace(AssignedLabel))
            {
                return AssignedLabel;
            }

            return "Unassigned";
        }
    }

    public string VolumeDisplay => $"{Volume}%";
    public string MuteDisplay => Muted ? "Yes" : "No";
}

public static class RebindFallbacks
{
    public const string ShowInactive = "ShowInactive";
    public const string DoNothing    = "DoNothing";

    public static bool IsValid(string? v) => v is ShowInactive or DoNothing;
}

public class OutputDeviceItem : System.ComponentModel.INotifyPropertyChanged
{
    public string DeviceId { get; set; } = "";
    public string FriendlyName { get; set; } = "";
    public bool IsDefault { get; set; }

    /// <summary>Shows a check mark when this device is the current Windows default output.</summary>
    public string DefaultIndicator => IsDefault ? "✓" : "";

    private bool _includeInCycle;
    public bool IncludeInCycle
    {
        get => _includeInCycle;
        set { _includeInCycle = value; PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IncludeInCycle))); }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}

[System.Runtime.InteropServices.ComImport]
[System.Runtime.InteropServices.Guid("f8679f50-850a-41cf-9c72-430f290290c8")]
[System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPolicyConfig
{
    [System.Runtime.InteropServices.PreserveSig] int GetMixFormat(string pszDeviceName, IntPtr ppFormat);
    [System.Runtime.InteropServices.PreserveSig] int GetDeviceFormat(string pszDeviceName, bool bDefault, IntPtr ppFormat);
    [System.Runtime.InteropServices.PreserveSig] int ResetDeviceFormat(string pszDeviceName);
    [System.Runtime.InteropServices.PreserveSig] int SetDeviceFormat(string pszDeviceName, IntPtr pEndpointFormat, IntPtr MixFormat);
    [System.Runtime.InteropServices.PreserveSig] int GetProcessingPeriod(string pszDeviceName, bool bDefault, IntPtr pmftDefaultPeriod, IntPtr pmftMinimumPeriod);
    [System.Runtime.InteropServices.PreserveSig] int SetProcessingPeriod(string pszDeviceName, IntPtr pmftPeriod);
    [System.Runtime.InteropServices.PreserveSig] int GetShareMode(string pszDeviceName, IntPtr pMode);
    [System.Runtime.InteropServices.PreserveSig] int SetShareMode(string pszDeviceName, IntPtr mode);
    [System.Runtime.InteropServices.PreserveSig] int GetPropertyValue(string pszDeviceName, bool bFxStore, IntPtr pKey, IntPtr pv);
    [System.Runtime.InteropServices.PreserveSig] int SetPropertyValue(string pszDeviceName, bool bFxStore, IntPtr pKey, IntPtr pv);
    [System.Runtime.InteropServices.PreserveSig] int SetDefaultEndpoint(string pszDeviceName, NAudio.CoreAudioApi.Role role);
    [System.Runtime.InteropServices.PreserveSig] int SetEndpointVisibility(string pszDeviceName, bool bVisible);
}

[System.Runtime.InteropServices.ComImport]
[System.Runtime.InteropServices.Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
internal class PolicyConfigClient { }
