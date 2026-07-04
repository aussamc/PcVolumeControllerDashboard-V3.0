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
    private const string DashboardVersion = "3.10";
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

    private readonly ObservableCollection<AudioTarget> _audioTargets = new();
    private readonly ObservableCollection<ChannelPoolItem> _channelPoolItems = new();
    private readonly Dictionary<string, AudioTarget> _audioTargetCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Collections.ObjectModel.ObservableCollection<OutputDeviceItem> _outputDevices = new();
    private int _audioSessionRefreshInProgress; // used as bool via Interlocked
    private readonly ObservableCollection<ChannelMappingItem> _channels = new();
    private readonly ObservableCollection<string> _availableComPorts = new();
    private readonly object _logFileLock = new();

    private readonly SerialService _serialService = new();

    // Active audio backend behind the neutral Core.Audio seam. Rebuilt by
    // InitAudioBackend() whenever the backend mode (WASAPI / VoiceMeeter) changes.
    // The backend owns all NAudio / VoiceMeeter handles; the host only ever
    // addresses targets by key.
    private IAudioBackend? _audioBackend;
    private bool _voiceMeeterBannerVisible;
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
    private string _connectedDeviceChipId = string.Empty;   // chip ID from current HELLO (empty if not reported)
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
        ChannelPoolItemsControl.ItemsSource = _channelPoolItems;
        OutputDevicesListView.ItemsSource = _outputDevices;

        LoadSettings();
        SetupTrayIcon();
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        // Persist settings when Windows is shutting down, restarting, or logging off.
        // The app is normally terminated by the OS in these cases without a graceful
        // window close, so OnClosing never runs and any in-memory-only settings would
        // otherwise be lost.  SessionEnding gives us a final chance to flush them.
        SystemEvents.SessionEnding += OnSessionEnding;

        InitAudioBackend();
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

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const uint KEYEVENTF_KEYUP       = 0x0002;
    private const byte VK_MEDIA_NEXT_TRACK  = 0xB0;
    private const byte VK_MEDIA_PREV_TRACK  = 0xB1;
    private const byte VK_MEDIA_STOP        = 0xB2;
    private const byte VK_MEDIA_PLAY_PAUSE  = 0xB3;

    /// <summary>
    /// Sends a media key press + release via <c>keybd_event</c>.
    /// Media virtual keys require the EXTENDEDKEY flag.
    /// </summary>
    private static void SendMediaKey(byte vk)
    {
        keybd_event(vk, 0, KEYEVENTF_EXTENDEDKEY,               UIntPtr.Zero);
        keybd_event(vk, 0, KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    private const int WmHotKey      = 0x0312;
    private const int HotkeyIdBase  = 0x9000;
    private const int HotkeyIdMasterVolumeUp   = HotkeyIdBase + 0;
    private const int HotkeyIdMasterVolumeDown = HotkeyIdBase + 1;
    private const int HotkeyIdToggleMasterMute = HotkeyIdBase + 2;
    private const int HotkeyIdCycleNextProfile = HotkeyIdBase + 3;
    private const int HotkeyIdShowDashboard    = HotkeyIdBase + 4;

    // Per-channel mute hotkeys: IDs 5–10 (one per channel, 6 channels max).
    private const int HotkeyIdChannelMuteBase = HotkeyIdBase + 5;

    private static readonly int[] AllHotkeyIds =
    {
        HotkeyIdMasterVolumeUp, HotkeyIdMasterVolumeDown,
        HotkeyIdToggleMasterMute, HotkeyIdCycleNextProfile, HotkeyIdShowDashboard,
        HotkeyIdChannelMuteBase + 0, HotkeyIdChannelMuteBase + 1,
        HotkeyIdChannelMuteBase + 2, HotkeyIdChannelMuteBase + 3,
        HotkeyIdChannelMuteBase + 4, HotkeyIdChannelMuteBase + 5,
    };

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

        // Apply wizard audio backend choice (if step 3 radio buttons are present).
        string newMode = (WizardVoiceMeeterRadioButton?.IsChecked == true)
            ? AudioBackendModes.VoiceMeeter
            : AudioBackendModes.Wasapi;
        if (newMode != _settings.AudioBackendMode)
            SwitchAudioBackendMode(newMode);

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
                TargetKeys = ch.TargetKeys != null ? new List<string>(ch.TargetKeys) : new List<string>(),
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

    private bool _volumeLimitsSuppressEvents;
    private bool _linkGroupSuppressEvents;

    private void ChannelMinVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_volumeLimitsSuppressEvents) return;
        if (ChannelMinVolumeValueTextBlock == null) return;

        int min = Math.Clamp((int)Math.Round(ChannelMinVolumeSlider.Value), 0, 100);

        // Enforce min ≤ max: pull max up if needed.
        if (ChannelMaxVolumeSlider != null && min > (int)Math.Round(ChannelMaxVolumeSlider.Value))
        {
            _volumeLimitsSuppressEvents = true;
            ChannelMaxVolumeSlider.Value = min;
            if (ChannelMaxVolumeValueTextBlock != null)
                ChannelMaxVolumeValueTextBlock.Text = $"{min}%";
            _volumeLimitsSuppressEvents = false;
        }

        ChannelMinVolumeValueTextBlock.Text = $"{min}%";
        SaveChannelVolumeLimits();
    }

    private void ChannelMaxVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_volumeLimitsSuppressEvents) return;
        if (ChannelMaxVolumeValueTextBlock == null) return;

        int max = Math.Clamp((int)Math.Round(ChannelMaxVolumeSlider.Value), 0, 100);

        // Enforce min ≤ max: pull min down if needed.
        if (ChannelMinVolumeSlider != null && max < (int)Math.Round(ChannelMinVolumeSlider.Value))
        {
            _volumeLimitsSuppressEvents = true;
            ChannelMinVolumeSlider.Value = max;
            if (ChannelMinVolumeValueTextBlock != null)
                ChannelMinVolumeValueTextBlock.Text = $"{max}%";
            _volumeLimitsSuppressEvents = false;
        }

        ChannelMaxVolumeValueTextBlock.Text = $"{max}%";
        SaveChannelVolumeLimits();
    }

    private void SaveChannelVolumeLimits()
    {
        if (_selectedChannelIndex < 0 || _selectedChannelIndex >= _settings.Channels.Length) return;
        _settings.Channels[_selectedChannelIndex].MinVolumePercent = Math.Clamp((int)Math.Round(ChannelMinVolumeSlider.Value), 0, 100);
        _settings.Channels[_selectedChannelIndex].MaxVolumePercent = Math.Clamp((int)Math.Round(ChannelMaxVolumeSlider.Value), 0, 100);
        FlushUiToSettings();
    }

    private void ChannelLinkGroup_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_linkGroupSuppressEvents) return;
        SaveChannelLinkGroup();
    }

    private void ChannelLinkGroup_Clear(object sender, RoutedEventArgs e)
    {
        _linkGroupSuppressEvents = true;
        ChannelLinkGroupTextBox.Text = string.Empty;
        _linkGroupSuppressEvents = false;
        SaveChannelLinkGroup();
    }

    private void SaveChannelLinkGroup()
    {
        if (_selectedChannelIndex < 0 || _selectedChannelIndex >= _settings.Channels.Length) return;
        _settings.Channels[_selectedChannelIndex].LinkedGroupId =
            ChannelLinkGroupTextBox.Text.Trim();
        FlushUiToSettings();
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
        if (ChannelTargetComboBox.SelectedItem is not AudioTarget target)
            return;

        ChannelMappingItem channel = _channels[_selectedChannelIndex];

        // Replace pool with just this one app.
        channel.TargetKeys = new List<string> { target.Key };
        channel.TargetKey  = target.Key;
        channel.AssignedLabel = target.Label;
        channel.FriendlyName = ChannelDisplayNameTextBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(channel.FriendlyName))
            channel.FriendlyName = target.Label;

        channel.Status = target.IsActiveOrMaster ? "Active" : "Waiting for app";

        UpdateChannelPoolUi();
        FlushUiToSettings();
        RefreshAudioSessions();
        RefreshAllChannelStates();
        SendAllChannelStatesToDevice();
        SendStateToDevice(force: true);

        Log($"Channel {channel.ChannelNumber} assigned to {target.Label}.");
    }

    private void AddToPoolButton_Click(object sender, RoutedEventArgs e)
    {
        if (ChannelTargetComboBox.SelectedItem is not AudioTarget target)
            return;

        ChannelMappingItem channel = _channels[_selectedChannelIndex];

        // Reject MASTER and MIC_INPUT — they don't make sense in a pool.
        if (target.IsMaster || target.IsMicInput)
        {
            System.Windows.MessageBox.Show(
                "Master and Microphone Input targets cannot be added to an app pool.\nUse Assign instead.",
                "Cannot add to pool",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
            return;
        }

        if (channel.TargetKeys.Contains(target.Key, StringComparer.OrdinalIgnoreCase))
        {
            Log($"Channel {channel.ChannelNumber}: {target.Label} is already in the pool.");
            return;
        }

        // If the channel has no assignment yet, treat Add as Assign.
        if (channel.TargetKeys.Count == 0)
        {
            channel.TargetKey  = target.Key;
            channel.AssignedLabel = target.Label;
            if (string.IsNullOrWhiteSpace(channel.FriendlyName))
                channel.FriendlyName = target.Label;
        }

        channel.TargetKeys.Add(target.Key);

        UpdateChannelPoolUi();
        FlushUiToSettings();
        RefreshAudioSessions();
        RefreshAllChannelStates();
        SendAllChannelStatesToDevice();
        SendStateToDevice(force: true);

        Log($"Channel {channel.ChannelNumber}: added {target.Label} to app pool (pool size: {channel.TargetKeys.Count}).");
    }

    private void RemoveFromChannelPool_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button btn || btn.Tag is not string key)
            return;

        ChannelMappingItem channel = _channels[_selectedChannelIndex];
        bool removed = channel.TargetKeys.Remove(key);
        if (!removed) return;

        // Keep TargetKey in sync.
        channel.TargetKey = channel.TargetKeys.FirstOrDefault() ?? string.Empty;
        if (channel.TargetKeys.Count == 0)
        {
            channel.AssignedLabel = "Unassigned";
            channel.FriendlyName  = string.Empty;
            channel.Status        = "Unassigned";
        }

        UpdateChannelPoolUi();
        FlushUiToSettings();
        RefreshAllChannelStates();
        SendAllChannelStatesToDevice();
        SendStateToDevice(force: true);

        Log($"Channel {channel.ChannelNumber}: removed {key} from app pool.");
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
        AudioTarget? target = FindTargetByKey(channel.TargetKey);

        if (target != null)
        {
            ChannelTargetComboBox.SelectedItem = target;
        }

        ChannelDisplayNameTextBox.Text = channel.DisplayLabel == "Unassigned" ? string.Empty : channel.DisplayLabel;

        RefreshAllChannelStates();
        UpdateSelectedChannelUi();
        UpdateChannelPoolUi();
        SendStateToDevice(force: true);

        }
        finally
        {
            _selectingChannel = false;
        }
    }

    private ChannelMappingItem SelectedChannel => _channels[_selectedChannelIndex];

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

        if (result.NoReleasesPublished)
        {
            Log("Update check: no releases have been published yet.");
            _updateCheckerStatus = "No releases published yet";
        }
        else if (result.ErrorMessage != null)
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

    private void RefreshDefaultAudioDevice()
    {
        // The backend owns device handles and re-queries lazily; just drop its
        // cached session list so the next read reflects the new default device.
        _audioBackend?.InvalidateCache();

        // In WASAPI mode, warn when there is no usable render endpoint (master
        // volume unreadable). VoiceMeeter mode has its own offline banner.
        bool wasapiMode = _settings.AudioBackendMode != AudioBackendModes.VoiceMeeter;
        bool renderAvailable = !wasapiMode || (_audioBackend?.GetVolumeByKey("MASTER") ?? -1f) >= 0f;

        if (renderAvailable)
        {
            // Clear any prior audio warning now that we have a device.
            Dispatcher.InvokeAsync(() => { if (WarningBanner.Visibility == Visibility.Visible && WarningBannerText.Text.StartsWith("No audio")) WarningBanner.Visibility = Visibility.Collapsed; });
        }
        else
        {
            ShowWarning("No audio output device found. Audio control is unavailable. Check your Windows sound settings.");
        }
    }

    private void RefreshOutputDevices()
    {
        // Run synchronously on the UI (STA) thread.  Device name enumeration
        // takes <20 ms and the COM objects from MMDeviceEnumerator must be
        // accessed from the same apartment they were created in.  The previous
        // Task.Run + InvokeAsync pattern disposed the enumerator before the
        // Dispatcher callback ran, leaving the COM collection in a bad state.
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
        }
        catch (Exception ex)
        {
            Log($"RefreshOutputDevices error: {ex.Message}");
        }
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
            _audioTargets.Clear();

            // Both backends enumerate dynamically behind the neutral seam: the
            // WASAPI backend supplies Master/Mic + per-session targets (with label
            // disambiguation done internally); the VoiceMeeter backend supplies
            // strips/buses. The host just consumes the resulting AudioTarget list.
            foreach (AudioTarget target in _audioBackend?.GetAvailableTargets() ?? (IReadOnlyList<AudioTarget>)Array.Empty<AudioTarget>())
                _audioTargets.Add(target);

            EnsureSavedTargetsAppearInTargetList();
            Log($"{_audioBackend?.BackendName ?? "Audio"}: loaded {_audioTargets.Count} target(s).");

            RefreshChannelAssignmentLabels();

            ChannelTargetComboBox.Items.Refresh();
            AudioSessionsListView.Items.Refresh();

            _lastAudioSessionSnapshot = GetAudioSessionSnapshot();

            // Rebuild the lookup cache so FindTargetByKey() is O(1).
            // When multiple sessions share the same process-name key (e.g. two
            // browser windows both map to PROC:chrome), keep the first entry so
            // the cache result is stable across refreshes.
            _audioTargetCache.Clear();
            foreach (AudioTarget cacheTarget in _audioTargets)
            {
                if (!_audioTargetCache.ContainsKey(cacheTarget.Key))
                    _audioTargetCache[cacheTarget.Key] = cacheTarget;
            }
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
            .SelectMany(ch => {
                var keys = new List<string>();
                if (!string.IsNullOrWhiteSpace(ch.TargetKey)) keys.Add(ch.TargetKey);
                if (ch.TargetKeys != null) keys.AddRange(ch.TargetKeys);
                return keys;
            })
            .Concat(_channels.SelectMany(ch => ch.TargetKeys ?? Enumerable.Empty<string>()))
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

            _audioTargets.Add(new AudioTarget
            {
                Key = key,
                Label = label,
                ProcessName = processName,
                ProcessId = 0,
                IsLive = false,
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
        HashSet<string> keys = new(StringComparer.OrdinalIgnoreCase);

        // Live targets from the backend (Master/Mic + per-app sessions in WASAPI
        // mode; strips/buses in VoiceMeeter mode). This mirrors what
        // RefreshAudioSessions() populates into _audioTargets, so this snapshot
        // and GetAudioSessionSnapshot() agree for the same hardware state.
        foreach (AudioTarget target in _audioBackend?.GetAvailableTargets() ?? (IReadOnlyList<AudioTarget>)Array.Empty<AudioTarget>())
            keys.Add(target.Key);

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

    /// <summary>
    /// For a multi-app pool channel, returns the key of the first pool entry
    /// that currently has active audio sessions. Falls back to the first entry
    /// if nothing is running so the channel always has a meaningful target.
    /// Returns channel.TargetKey directly for single-app channels.
    /// </summary>
    private string ResolveActiveTargetKey(ChannelMappingItem channel)
    {
        if (channel.TargetKeys.Count <= 1)
            return channel.TargetKey;

        // Find first pool entry that has a live session.
        foreach (string key in channel.TargetKeys)
        {
            if (string.IsNullOrWhiteSpace(key)) continue;
            if (key.Equals("MASTER", StringComparison.OrdinalIgnoreCase)) return key;
            if (key.Equals("MIC_INPUT", StringComparison.OrdinalIgnoreCase)) return key;
            if (KeyHasLiveTarget(key)) return key;
        }

        // None running — return first pool entry so the channel shows "waiting" for it.
        return channel.TargetKeys[0];
    }

    private void RefreshAllChannelStates()
    {
        try
        {
        foreach (ChannelMappingItem channel in _channels)
        {
            // For multi-app pool channels, find whichever app is currently running.
            string effectiveKey = ResolveActiveTargetKey(channel);
            if (channel.TargetKeys.Count > 1)
                channel.TargetKey = effectiveKey; // keep in sync for volume/mute operations
            AudioTarget? target = FindTargetByKey(effectiveKey);

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
                float micVol = _audioBackend?.GetVolumeByKey("MIC_INPUT") ?? -1f;
                if (micVol >= 0f)
                {
                    channel.Volume = Math.Clamp((int)Math.Round(micVol * 100), 0, 100);
                    channel.Muted = _audioBackend?.GetMuteByKey("MIC_INPUT") ?? false;
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
                // Per-app / VoiceMeeter target: read live volume/mute by key.
                float vol = _audioBackend?.GetVolumeByKey(target.Key) ?? -1f;

                if (vol < 0f)
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
                    // "Active xN" reflects how many live streams share this key
                    // (e.g. multiple browser windows), from the last enumeration.
                    int liveCount = _audioTargets.Count(t =>
                        t.IsLive && t.Key.Equals(target.Key, StringComparison.OrdinalIgnoreCase));

                    channel.Volume = Math.Clamp((int)Math.Round(vol * 100), 0, 100);
                    channel.Muted = _audioBackend?.GetMuteByKey(target.Key) ?? false;
                    channel.Status = liveCount <= 1 ? "Active" : $"Active x{liveCount}";
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

    private void ChangeChannelVolume(int channelIndex, int deltaPercent, bool propagate = true)
    {
        if (channelIndex < 0 || channelIndex >= _channels.Count)
        {
            return;
        }

        ChannelMappingItem channel = _channels[channelIndex];
        AudioTarget? target = FindTargetByKey(channel.TargetKey);

        if (target == null)
        {
            Log($"Channel {channel.ChannelNumber} is not assigned or target is not active.");
            return;
        }

        // Per-channel volume limits.
        (float limMinNorm, float limMaxNorm) = GetChannelVolumeLimitsNormalized(channelIndex);
        int limMin = (int)Math.Round(limMinNorm * 100);
        int limMax = (int)Math.Round(limMaxNorm * 100);

        // The backend applies the delta per underlying target (per-session for
        // WASAPI), clamps to the channel limits, and returns the representative
        // new percent (−1 if nothing is assignable / running).
        int result = _audioBackend?.AdjustVolumeByKey(target.Key, deltaPercent, limMin, limMax) ?? -1;

        if (result < 0)
        {
            Log($"No active audio session for {target.Label}.");
            return;
        }

        if (IsAdvancedDebugLoggingEnabled())
        {
            Log($"Channel {channel.ChannelNumber} / {target.Label}: {result}%");
        }

        // Propagate the same delta to all channels in the same link group.
        // propagate=false prevents the recursive calls from re-propagating.
        if (propagate)
        {
            foreach (int linkedIndex in GetLinkedChannelIndices(channelIndex))
            {
                ChangeChannelVolume(linkedIndex, deltaPercent, propagate: false);
            }
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
        AudioTarget? target = FindTargetByKey(channel.TargetKey);

        if (target == null)
        {
            Log($"Channel {channel.ChannelNumber} is not assigned or target is not active.");
            return;
        }

        // The backend toggles every underlying target for the key (master/mic
        // endpoint, per-session, or VoiceMeeter) and returns the new mute state,
        // or null if nothing is assignable / running.
        bool? next = _audioBackend?.ToggleMuteByKey(target.Key);

        if (next == null)
        {
            Log($"No active audio session for {target.Label}.");
            return;
        }

        Log(next.Value ? $"{target.Label} muted" : $"{target.Label} unmuted");
        ShowMuteOverlay(channelIndex, next.Value);
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

            case ChannelButtonActions.MediaPlayPause:
                SendMediaKey(VK_MEDIA_PLAY_PAUSE);
                break;
            case ChannelButtonActions.MediaNextTrack:
                SendMediaKey(VK_MEDIA_NEXT_TRACK);
                break;
            case ChannelButtonActions.MediaPrevTrack:
                SendMediaKey(VK_MEDIA_PREV_TRACK);
                break;
            case ChannelButtonActions.MediaStop:
                SendMediaKey(VK_MEDIA_STOP);
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

            case ChannelButtonActions.MediaPlayPause:
                SendMediaKey(VK_MEDIA_PLAY_PAUSE);
                break;
            case ChannelButtonActions.MediaNextTrack:
                SendMediaKey(VK_MEDIA_NEXT_TRACK);
                break;
            case ChannelButtonActions.MediaPrevTrack:
                SendMediaKey(VK_MEDIA_PREV_TRACK);
                break;
            case ChannelButtonActions.MediaStop:
                SendMediaKey(VK_MEDIA_STOP);
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

            case ChannelButtonActions.MediaPlayPause:
                SendMediaKey(VK_MEDIA_PLAY_PAUSE);
                break;
            case ChannelButtonActions.MediaNextTrack:
                SendMediaKey(VK_MEDIA_NEXT_TRACK);
                break;
            case ChannelButtonActions.MediaPrevTrack:
                SendMediaKey(VK_MEDIA_PREV_TRACK);
                break;
            case ChannelButtonActions.MediaStop:
                SendMediaKey(VK_MEDIA_STOP);
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

    private void UpdateChannelPoolUi()
    {
        _channelPoolItems.Clear();
        if (_selectedChannelIndex < 0 || _selectedChannelIndex >= _channels.Count)
        {
            if (ChannelPoolBorder != null) ChannelPoolBorder.Visibility = Visibility.Collapsed;
            return;
        }

        ChannelMappingItem channel = _channels[_selectedChannelIndex];
        foreach (string key in channel.TargetKeys)
        {
            string label = _audioTargetCache.TryGetValue(key, out AudioTarget? cached)
                ? cached.Label
                : MakeDisplayLabelFromTargetKey(key);
            _channelPoolItems.Add(new ChannelPoolItem { Key = key, Label = label });
        }

        if (ChannelPoolBorder != null)
            ChannelPoolBorder.Visibility = _channelPoolItems.Count > 0
                ? Visibility.Visible : Visibility.Collapsed;
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

        // Per-channel mute hotkey label
        UpdateChannelMuteHotkeyLabel();

        // Populate volume limits
        _volumeLimitsSuppressEvents = true;
        try
        {
            int index = _selectedChannelIndex;
            if (_settings.Channels != null && index >= 0 && index < _settings.Channels.Length)
            {
                int minVol = Math.Clamp(_settings.Channels[index].MinVolumePercent, 0, 100);
                int maxVol = Math.Clamp(_settings.Channels[index].MaxVolumePercent, 0, 100);
                if (ChannelMinVolumeSlider != null)
                {
                    ChannelMinVolumeSlider.Value = minVol;
                    if (ChannelMinVolumeValueTextBlock != null)
                        ChannelMinVolumeValueTextBlock.Text = $"{minVol}%";
                }
                if (ChannelMaxVolumeSlider != null)
                {
                    ChannelMaxVolumeSlider.Value = maxVol;
                    if (ChannelMaxVolumeValueTextBlock != null)
                        ChannelMaxVolumeValueTextBlock.Text = $"{maxVol}%";
                }
            }
        }
        finally
        {
            _volumeLimitsSuppressEvents = false;
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

        // Populate channel link group
        _linkGroupSuppressEvents = true;
        try
        {
            int index = _selectedChannelIndex;
            ChannelLinkGroupTextBox.Text =
                (_settings.Channels != null && index < _settings.Channels.Length)
                    ? (_settings.Channels[index].LinkedGroupId ?? string.Empty)
                    : string.Empty;
        }
        finally
        {
            _linkGroupSuppressEvents = false;
        }

        UpdateChannelPoolUi();
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
            8 => ChannelButtonActions.MediaPlayPause,
            9 => ChannelButtonActions.MediaNextTrack,
            10 => ChannelButtonActions.MediaPrevTrack,
            11 => ChannelButtonActions.MediaStop,
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
            7 => ChannelButtonActions.MediaPlayPause,
            8 => ChannelButtonActions.MediaNextTrack,
            9 => ChannelButtonActions.MediaPrevTrack,
            10 => ChannelButtonActions.MediaStop,
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
            ChannelButtonActions.MediaPlayPause => 8,
            ChannelButtonActions.MediaNextTrack => 9,
            ChannelButtonActions.MediaPrevTrack => 10,
            ChannelButtonActions.MediaStop => 11,
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
            ChannelButtonActions.MediaPlayPause => 7,
            ChannelButtonActions.MediaNextTrack => 8,
            ChannelButtonActions.MediaPrevTrack => 9,
            ChannelButtonActions.MediaStop => 10,
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
            7 => ChannelButtonActions.MediaPlayPause,
            8 => ChannelButtonActions.MediaNextTrack,
            9 => ChannelButtonActions.MediaPrevTrack,
            10 => ChannelButtonActions.MediaStop,
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
            ChannelButtonActions.MediaPlayPause => 7,
            ChannelButtonActions.MediaNextTrack => 8,
            ChannelButtonActions.MediaPrevTrack => 9,
            ChannelButtonActions.MediaStop => 10,
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
            ChannelButtonActions.MediaPlayPause => "Play / Pause",
            ChannelButtonActions.MediaNextTrack => "Next track",
            ChannelButtonActions.MediaPrevTrack => "Previous track",
            ChannelButtonActions.MediaStop => "Stop",
            _ => "Select next channel"
        };
    }

    private AudioTarget? FindTargetByKey(string? key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        _audioTargetCache.TryGetValue(key, out AudioTarget? target);
        return target;
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

        if (VoiceMeeterBackend.IsVoiceMeeterKey(key))
        {
            return VoiceMeeterBackend.MakeDisplayLabel(key);
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

    /// <summary>True if the key currently resolves to at least one live target.</summary>
    private bool KeyHasLiveTarget(string key) => (_audioBackend?.GetVolumeByKey(key) ?? -1f) >= 0f;

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

            // Multi-app pool: show active app label or pool summary.
            if (channel.TargetKeys.Count > 1)
            {
                // Try to show the currently active app.
                string activeKey = ResolveActiveTargetKey(channel);
                AudioTarget? activeTarget = FindTargetByKey(activeKey);
                if (activeTarget != null && (activeTarget.IsActiveOrMaster || KeyHasLiveTarget(activeKey)))
                {
                    channel.AssignedLabel = activeTarget.Label;
                }
                else
                {
                    // Show pool summary: first two names + count
                    var labels = channel.TargetKeys
                        .Select(k => _audioTargetCache.TryGetValue(k, out AudioTarget? t) ? t.Label : MakeDisplayLabelFromTargetKey(k))
                        .ToList();
                    channel.AssignedLabel = labels.Count <= 2
                        ? string.Join(" / ", labels)
                        : $"{labels[0]} / +{labels.Count - 1} more";
                }
                continue;
            }

            AudioTarget? target = FindTargetByKey(channel.TargetKey);

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
        float scalar = _audioBackend?.GetVolumeByKey("MASTER") ?? -1f;
        return scalar < 0f ? 0 : Math.Clamp((int)Math.Round(scalar * 100), 0, 100);
    }

    private bool GetMasterMute() => _audioBackend?.GetMuteByKey("MASTER") ?? false;

    private static string MakeProtocolSafeLabel(string label)
    {
        string cleaned = label.Replace(',', ' ').Trim();

        if (cleaned.Length == 0)
        {
            return "Unknown";
        }

        return cleaned.Length <= 18 ? cleaned : cleaned[..18];
    }

    private void ApplySettingsToUi()
    {
        SetOutputDevicesExpanded(_settings.OutputDevicesExpanded);

        AutoConnectCheckBox.IsChecked = _settings.AutoConnectOnLaunch;
        ScanAllComPortsCheckBox.IsChecked = _settings.ScanAllComPortsIfRememberedMissing;
        UpdatePairedControllerIdLabel();

        // Audio backend selector
        if (WasapiRadioButton != null)
            WasapiRadioButton.IsChecked = _settings.AudioBackendMode == AudioBackendModes.Wasapi;
        if (VoiceMeeterRadioButton != null)
            VoiceMeeterRadioButton.IsChecked = _settings.AudioBackendMode == AudioBackendModes.VoiceMeeter;
        UpdateVoiceMeeterStatus();
        MinimizeToTrayCheckBox.IsChecked = _settings.MinimizeToTray;
        StartMinimizedToTrayCheckBox.IsChecked = _settings.StartMinimizedToTray;
        StartWithWindowsCheckBox.IsChecked = _settings.StartWithWindows;
        AdvancedDebugLoggingCheckBox.IsChecked = _settings.AdvancedDebugLogging;
        if (TrayNotificationsCheckBox != null)
            TrayNotificationsCheckBox.IsChecked = _settings.TrayNotificationsEnabled;

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

        // Audio backend step
        if (WizardWasapiRadioButton != null)
            WizardWasapiRadioButton.IsChecked = _settings.AudioBackendMode == AudioBackendModes.Wasapi;
        if (WizardVoiceMeeterRadioButton != null)
            WizardVoiceMeeterRadioButton.IsChecked = _settings.AudioBackendMode == AudioBackendModes.VoiceMeeter;

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

    // ── Controller pairing / chip-ID mismatch ────────────────────────────────────

    /// <summary>
    /// Shows the chip-ID mismatch warning banner with the supplied message.
    /// Called from the serial thread via Dispatcher.
    /// </summary>
    private void ShowChipIdMismatchBanner(string connectedChipId)
    {
        if (ChipIdMismatchBanner == null) return;
        if (ChipIdMismatchMessageTextBlock != null)
            ChipIdMismatchMessageTextBlock.Text =
                $"Connected controller (chip ID: {connectedChipId}) differs from the paired controller " +
                $"(chip ID: {_settings.LastDeviceChipId}). Click \"Forget & re-pair\" to accept this device, " +
                "or \"Dismiss\" to suppress this warning for the session.";
        ChipIdMismatchBanner.Visibility = Visibility.Visible;
    }

    private void HideChipIdMismatchBanner()
    {
        if (ChipIdMismatchBanner != null)
            ChipIdMismatchBanner.Visibility = Visibility.Collapsed;
    }

    private void ChipIdMismatch_ForgetAndRepair(object sender, RoutedEventArgs e)
    {
        _settings.LastDeviceChipId = _connectedDeviceChipId;
        SaveSettings();
        HideChipIdMismatchBanner();
        UpdatePairedControllerIdLabel();
        Log($"Controller re-paired to chip ID {_connectedDeviceChipId}.");
    }

    private void ChipIdMismatch_Dismiss(object sender, RoutedEventArgs e) => HideChipIdMismatchBanner();

    private void ForgetControllerButton_Click(object sender, RoutedEventArgs e)
    {
        _settings.LastDeviceChipId = string.Empty;
        SaveSettings();
        HideChipIdMismatchBanner();
        UpdatePairedControllerIdLabel();
        Log("Paired controller forgotten. Next connection will auto-pair.");
    }

    private void UpdatePairedControllerIdLabel()
    {
        if (PairedControllerIdTextBlock == null) return;
        PairedControllerIdTextBlock.Text = string.IsNullOrEmpty(_settings.LastDeviceChipId)
            ? "Paired controller: (none)"
            : $"Paired controller: chip ID {_settings.LastDeviceChipId}";
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
        const double defaultWidth = 1300;
        const double defaultHeight = 900;

        double width = _settings.WindowWidth >= defaultWidth
            ? _settings.WindowWidth
            : defaultWidth;

        double height = _settings.WindowHeight >= defaultHeight
            ? _settings.WindowHeight
            : defaultHeight;

        Width = Math.Max(width, MinWidth);
        Height = Math.Max(height, MinHeight);

        ApplySavedSplitterRatio();
    }

    /// <summary>
    /// Applies the persisted Audio-tab splitter ratio to the two column definitions
    /// so the Channel Mapping / Selected Channel split matches where the user left it.
    /// </summary>
    private void ApplySavedSplitterRatio()
    {
        if (AudioSplitGrid == null) return;
        double ratio = Math.Clamp(_settings.AudioSplitterRatio, 0.1, 0.9);
        AudioSplitGrid.ColumnDefinitions[0].Width = new GridLength(ratio, GridUnitType.Star);
        AudioSplitGrid.ColumnDefinitions[2].Width = new GridLength(1.0 - ratio, GridUnitType.Star);
    }

    /// <summary>
    /// Reads the current column widths and stores the left-panel fraction back into settings.
    /// </summary>
    private void SaveSplitterRatio()
    {
        if (AudioSplitGrid == null) return;
        double left  = AudioSplitGrid.ColumnDefinitions[0].ActualWidth;
        double right = AudioSplitGrid.ColumnDefinitions[2].ActualWidth;
        double total = left + right;
        if (total > 0)
            _settings.AudioSplitterRatio = left / total;
    }

    private void AudioPanelSplitter_DragCompleted(object sender,
        System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        SaveSplitterRatio();
        SaveSettings();
    }

    private void OutputDevicesToggleButton_Click(object sender, RoutedEventArgs e)
    {
        bool expand = OutputDevicesContent.Visibility != Visibility.Visible;
        SetOutputDevicesExpanded(expand);
        _settings.OutputDevicesExpanded = expand;
        SaveSettings();
    }

    private void SetOutputDevicesExpanded(bool expanded)
    {
        OutputDevicesContent.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        OutputDevicesToggleButton.Content = expanded ? "▼" : "▶";
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
        if (TrayNotificationsCheckBox != null) _settings.TrayNotificationsEnabled = TrayNotificationsCheckBox.IsChecked == true;
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
        SendOledSettingsToDevice(logIfNotConnected: false);
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
        SendOledSettingsToDevice(logIfNotConnected: false);
    }

    private void OledSleepTimeoutSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (OledSleepTimeoutSlider == null)
        {
            return;
        }

        _settings.OledSleepTimeoutMinutes = GetOledSleepTimeoutMinutesFromUi();
        UpdateOledSleepTimeoutLabel();
        SendOledSettingsToDevice(logIfNotConnected: false);
    }

    private void OledConnectedIdleActionComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (OledConnectedIdleActionComboBox == null)
        {
            return;
        }

        _settings.OledConnectedIdleAction = GetOledConnectedIdleActionFromUi();
        UpdateOledPreviewPanels();
        SendOledSettingsToDevice(logIfNotConnected: false);
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
        SendOledSettingsToDevice(logIfNotConnected: false);
    }

    private void OledAntiBurnInCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (OledAntiBurnInCheckBox == null)
        {
            return;
        }

        _settings.OledAntiBurnInEnabled = IsOledAntiBurnInEnabledFromUi();
        UpdateOledPreviewPanels();
        SendOledSettingsToDevice(logIfNotConnected: false);
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
            System.Windows.Controls.Image[] previewImages =
            {
                OledPreview1Image, OledPreview2Image, OledPreview3Image,
                OledPreview4Image, OledPreview5Image, OledPreview6Image
            };
            TextBlock[] titleBlocks =
            {
                OledPreview1Title, OledPreview2Title, OledPreview3Title,
                OledPreview4Title, OledPreview5Title, OledPreview6Title
            };

            string globalMode = GetDisplayModeFromUi();

            if (OledPreviewModeTextBlock != null)
            {
                OledPreviewModeTextBlock.Text =
                    $"Preview mode: {GetDisplayModeDisplayName(globalMode)} | " +
                    $"Brightness: {GetOledBrightnessPercentFromUi()}% | " +
                    $"Connected idle: {GetOledConnectedIdleTimeoutMinutesFromUi()} min / " +
                    $"{GetOledConnectedIdleActionDisplayName(GetOledConnectedIdleActionFromUi())} | " +
                    $"Anti-burn-in: {(IsOledAntiBurnInEnabledFromUi() ? "on" : "off")}";
            }

            for (int i = 0; i < Math.Min(_channels.Count, previewImages.Length); i++)
            {
                ChannelMappingItem ch = _channels[i];

                // Resolve per-channel mode override (same logic as firmware)
                string mode = !string.IsNullOrEmpty(ch.OledDisplayMode)
                    ? ch.OledDisplayMode
                    : globalMode;

                string label  = ch.DisplayLabel;
                int    volume = Math.Clamp(ch.Volume, 0, 100);
                bool   muted  = ch.Muted;
                string status = ch.Status;

                titleBlocks[i].Text = $"OLED {ch.ChannelNumber}";

                var renderer = new OledRenderer();
                switch (mode)
                {
                    case DisplayModes.LargeVolume:
                        renderer.RenderLargeVolume(label, volume, muted);
                        break;
                    case DisplayModes.MuteStatus:
                        renderer.RenderMuteStatus(label, volume, muted);
                        break;
                    case DisplayModes.AppOrDeviceName:
                        renderer.RenderAppOrDeviceName(ch.ChannelNumber, label, status, volume);
                        break;
                    case DisplayModes.BarPercent:
                        renderer.RenderBarPercent(label, volume, muted);
                        break;
                    default:
                        renderer.RenderAppVolume(label, volume, muted, status);
                        break;
                }

                previewImages[i].Source = renderer.ToWriteableBitmap();
            }
        }
        catch
        {
            // Swallow — preview failure must never crash the app.
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

    // ── VoiceMeeter backend ─────────────────────────────────────────────────────

    /// <summary>
    /// (Re)builds the active audio backend for the current backend mode and wires
    /// its events. Safe to call multiple times — disposes any existing instance
    /// first. WASAPI mode uses <see cref="WasapiAudioBackend"/>; VoiceMeeter mode
    /// uses <see cref="VoiceMeeterBackend"/>; both implement the neutral seam.
    /// </summary>
    private void InitAudioBackend()
    {
        if (_audioBackend != null)
        {
            _audioBackend.AvailabilityChanged -= OnBackendAvailabilityChanged;
            _audioBackend.TargetsChanged      -= OnBackendTargetsChanged;
            _audioBackend.Dispose();
        }

        _audioBackend = _settings.AudioBackendMode == AudioBackendModes.VoiceMeeter
            ? new VoiceMeeterBackend(Log)
            : new WasapiAudioBackend(Log);

        _audioBackend.AvailabilityChanged += OnBackendAvailabilityChanged;
        _audioBackend.TargetsChanged      += OnBackendTargetsChanged;
        _audioBackend.Initialise();

        UpdateVoiceMeeterBanner();
        UpdateVoiceMeeterStatus();
    }

    /// <summary>
    /// Called on any thread when the backend's availability flips (e.g. VoiceMeeter
    /// goes online/offline). Marshals to the UI thread, updates the banner, and
    /// refreshes targets.
    /// </summary>
    private void OnBackendAvailabilityChanged()
    {
        Dispatcher.InvokeAsync(() =>
        {
            UpdateVoiceMeeterBanner();
            UpdateVoiceMeeterStatus();
            RefreshAudioSessions();
            RefreshAllChannelStates();
            SendAllChannelStatesToDevice();
        });
    }

    /// <summary>
    /// Called on any thread when the backend's target set may have changed
    /// (default output device switched, app started/stopped streaming). Marshals
    /// to the UI thread and refreshes. Replaces the old DefaultDeviceChanged wiring.
    /// </summary>
    private void OnBackendTargetsChanged()
    {
        Dispatcher.InvokeAsync(() =>
        {
            Log("Audio targets changed — refreshing audio sessions.");
            RefreshDefaultAudioDevice();
            RefreshAudioSessions();
            RefreshAllChannelStates();
            SendAllChannelStatesToDevice();
        });
    }

    /// <summary>Shows or hides the "VoiceMeeter offline" warning banner.</summary>
    private void UpdateVoiceMeeterBanner()
    {
        bool isVmMode = _settings.AudioBackendMode == AudioBackendModes.VoiceMeeter;
        bool offline  = isVmMode && (_audioBackend == null || !_audioBackend.IsAvailable);
        bool show     = offline;

        if (_voiceMeeterBannerVisible == show) return;
        _voiceMeeterBannerVisible = show;

        if (VoiceMeeterOfflineBanner != null)
            VoiceMeeterOfflineBanner.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Refreshes the VoiceMeeter status label in the Setup tab.</summary>
    private void UpdateVoiceMeeterStatus()
    {
        if (VoiceMeeterStatusTextBlock == null) return;

        if (_settings.AudioBackendMode != AudioBackendModes.VoiceMeeter)
        {
            VoiceMeeterStatusTextBlock.Text = "Not active";
            return;
        }

        if (_audioBackend == null)
        {
            VoiceMeeterStatusTextBlock.Text = "Not initialised";
            return;
        }

        if (!_audioBackend.IsAvailable)
        {
            VoiceMeeterStatusTextBlock.Text = "VoiceMeeter: not running — please start VoiceMeeter.";
            return;
        }

        VoiceMeeterStatusTextBlock.Text = "VoiceMeeter: running";
    }

    /// <summary>
    /// Handles the user clicking "Apply" on the Audio Backend section in Setup.
    /// Warns, backs up settings, clears all profiles' assignments, switches mode, saves.
    /// </summary>
    private void AudioBackendApplyButton_Click(object sender, RoutedEventArgs e)
    {
        string newMode = (VoiceMeeterRadioButton?.IsChecked == true)
            ? AudioBackendModes.VoiceMeeter
            : AudioBackendModes.Wasapi;

        if (newMode == _settings.AudioBackendMode) return;

        string modeName = newMode == AudioBackendModes.VoiceMeeter ? "VoiceMeeter" : "WASAPI";

        var result = System.Windows.MessageBox.Show(this,
            $"Switching to {modeName} mode will clear all channel assignments in every profile " +
            "and cannot be undone without restoring a backup.\n\n" +
            "A backup of your current settings will be saved automatically.\n\n" +
            "Do you want to continue?",
            "Switch Audio Backend",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes) return;

        SwitchAudioBackendMode(newMode);
    }

    /// <summary>
    /// Backs up settings, clears all profile channel assignments, sets the new backend
    /// mode, reinitialises the backend, and refreshes the UI.
    /// </summary>
    private void SwitchAudioBackendMode(string newMode)
    {
        Log($"Switching audio backend from {_settings.AudioBackendMode} to {newMode}.");

        // Backup current settings before any destructive change.
        SettingsRepository.Backup($"pre-backend-switch-to-{newMode.ToLowerInvariant()}", Log);

        // Clear all channel assignments in every profile.
        if (_settings.Profiles != null)
        {
            foreach (ProfileEntry profile in _settings.Profiles)
            {
                if (profile.Channels == null) continue;
                foreach (ChannelSettings ch in profile.Channels)
                {
                    ch.TargetKey    = string.Empty;
                    ch.FriendlyName = string.Empty;
                }
            }
        }

        // Clear the active channel array too (it's a reference to the active profile).
        foreach (ChannelSettings ch in _settings.Channels)
        {
            ch.TargetKey    = string.Empty;
            ch.FriendlyName = string.Empty;
        }

        _settings.AudioBackendMode = newMode;
        SaveSettings();

        // Reinitialise backend (rebuilds WASAPI or VoiceMeeter for the new mode).
        InitAudioBackend();

        // Rebuild channels and refresh the UI.
        BuildChannels();
        ApplySettingsToChannels();
        RefreshAudioSessions();
        ApplySettingsToUi();
        RefreshAllChannelStates();
        SendAllChannelStatesToDevice();

        Log($"Audio backend switched to {newMode}. All channel assignments cleared.");
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
            _channels[i].TargetKeys = _settings.Channels[i].TargetKeys != null
                ? new List<string>(_settings.Channels[i].TargetKeys)
                : new List<string>();
            // Sync TargetKey from pool when only one entry
            if (_channels[i].TargetKeys.Count == 1)
                _channels[i].TargetKey = _channels[i].TargetKeys[0];
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
            TargetKey    = channel.TargetKeys?.FirstOrDefault() ?? channel.TargetKey,
            TargetKeys   = new List<string>(channel.TargetKeys ?? new List<string>()),
            FriendlyName = channel.FriendlyName,
            ButtonAction          = ChannelButtonActions.IsValid(channel.ButtonAction)                     ? channel.ButtonAction                     : ChannelButtonActions.ToggleAssignedMute,
            LongPressButtonAction = ChannelButtonActions.IsValidLongPressAction(channel.LongPressButtonAction)  ? channel.LongPressButtonAction  : ChannelButtonActions.NoAction,
            DoublePressButtonAction = ChannelButtonActions.IsValidDoublePressAction(channel.DoublePressButtonAction) ? channel.DoublePressButtonAction : ChannelButtonActions.NoAction,
            RebindFallback  = RebindFallbacks.IsValid(channel.RebindFallback) ? channel.RebindFallback : RebindFallbacks.ShowInactive,
            OledDisplayMode = DisplayModes.IsValidChannelMode(channel.OledDisplayMode) ? channel.OledDisplayMode : string.Empty,
            // Preserve fields that live only in ChannelSettings, not in ChannelMappingItem.
            SensitivityPercent = (i < previous.Length) ? previous[i].SensitivityPercent : -1,
            MinVolumePercent   = (i < previous.Length) ? previous[i].MinVolumePercent   : 0,
            MaxVolumePercent   = (i < previous.Length) ? previous[i].MaxVolumePercent   : 100,
            MuteHotkey         = (i < previous.Length) ? previous[i].MuteHotkey         : new HotkeyBinding(),
            Presets = (i < previous.Length && previous[i].Presets != null)
                ? previous[i].Presets
                : new[] { new VolumePreset { Name = "", VolumePercent = 25 }, new VolumePreset { Name = "", VolumePercent = 50 }, new VolumePreset { Name = "", VolumePercent = 75 } },
            LinkedGroupId  = (i < previous.Length) ? previous[i].LinkedGroupId  : string.Empty,
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

        SaveSplitterRatio();
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
        sb.AppendLine($"Connected ESP32 chip ID: {(string.IsNullOrEmpty(_connectedDeviceChipId) ? "not reported" : _connectedDeviceChipId)}");
        sb.AppendLine($"Paired controller chip ID: {(string.IsNullOrWhiteSpace(_settings.LastDeviceChipId) ? "none (not yet paired)" : _settings.LastDeviceChipId)}");
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

    // ── Per-channel mute hotkey UI handlers ──────────────────────────────────────

    private void ChannelMuteHotkey_Set(object sender, RoutedEventArgs e)
    {
        if (_selectedChannelIndex < 0 || _selectedChannelIndex >= _settings.Channels.Length) return;
        int ch = _selectedChannelIndex;
        string label = _channels.Count > ch ? $"Mute Channel {_channels[ch].ChannelNumber}" : $"Mute Channel {ch + 1}";
        SetHotkeyBinding(label, _settings.Channels[ch].MuteHotkey, b =>
        {
            _settings.Channels[ch].MuteHotkey = b;
            UpdateChannelMuteHotkeyLabel();
        });
    }

    private void ChannelMuteHotkey_Clear(object sender, RoutedEventArgs e)
    {
        if (_selectedChannelIndex < 0 || _selectedChannelIndex >= _settings.Channels.Length) return;
        _settings.Channels[_selectedChannelIndex].MuteHotkey = new HotkeyBinding { Enabled = false };
        UpdateChannelMuteHotkeyLabel();
        FlushUiToSettings();
        RegisterAllHotkeys();
    }

    private void UpdateChannelMuteHotkeyLabel()
    {
        if (ChannelMuteHotkeyTextBlock == null) return;
        if (_selectedChannelIndex < 0 || _selectedChannelIndex >= _settings.Channels.Length)
        {
            ChannelMuteHotkeyTextBlock.Text = "(unassigned)";
            return;
        }
        ChannelMuteHotkeyTextBlock.Text = _settings.Channels[_selectedChannelIndex].MuteHotkey.ToDisplayString();
    }

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
        SystemEvents.SessionEnding -= OnSessionEnding;

        DisconnectSerial();

        if (_audioBackend != null)
        {
            _audioBackend.AvailabilityChanged -= OnBackendAvailabilityChanged;
            _audioBackend.TargetsChanged      -= OnBackendTargetsChanged;
            _audioBackend.Dispose();
            _audioBackend = null;
        }

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

/// <summary>One entry in a channel's multi-app pool, used to bind the pool chip list.</summary>
public sealed class ChannelPoolItem
{
    public string Key   { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
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

    // Runtime mirror of ChannelSettings.TargetKeys.
    public List<string> TargetKeys { get; set; } = new();

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
