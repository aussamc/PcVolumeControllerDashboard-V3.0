using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using PcVolumeControllerDashboard.App.Oled;
using PcVolumeControllerDashboard.App.Services;
using PcVolumeControllerDashboard.Core;
using PcVolumeControllerDashboard.Core.Audio;

namespace PcVolumeControllerDashboard.App;

public partial class MainWindow : Window
{
    // Shipping dashboard version (bumped per Avalonia-tab milestone).
    // Single source of truth lives in AppInfo.Version so the wizard can reuse it.
    private const string DashboardVersion = AppInfo.Version;
    private const string RequiredProtocolVersion = "2.24";

    private readonly SettingsService? _settingsService;
    private IAudioBackend? _audioBackend;
    private SerialConnectionService? _connection;
    private DeviceStateService? _deviceState;
    // Overlay controller — used to show a live preview while adjusting appearance.
    private readonly VolumeOverlayController? _overlay;
    // Auto-update checker (v3.19) — drives the update banner + Skip/Remind actions.
    private readonly UpdateOrchestrator? _updateOrchestrator;
    private readonly ObservableCollection<ChannelRow> _channelRows = new();
    private DispatcherTimer? _channelPollTimer;
    // Assignable-target discovery (Q2): the picker is re-enumerated on a slow cadence
    // (and on the backend's TargetsChanged event) so a newly-launched app appears
    // without a manual Refresh. The 50ms channel poll is far too frequent to
    // re-enumerate sessions, so this runs on its own timer with change detection.
    private DispatcherTimer? _targetRefreshTimer;
    private HashSet<string> _lastTargetKeys = new(StringComparer.OrdinalIgnoreCase);
    private const int TargetRefreshIntervalMs = 2500;
    // Latest live per-channel state from the poll; drives the OLED previews so they
    // track the hardware instead of showing static samples.
    private List<ChannelLiveState> _lastLive = new();
    private DashboardSettings _settings = DashboardSettings.CreateDefault();

    // --safe diagnostic launch (N1): shows a banner and reflects that auto-connect /
    // audio writes are disabled. The actual suppression lives in the runtime services.
    private bool _safeMode;

    // Binding-init-order settings-wipe guard: control-change events fire while
    // ApplySettingsToUi() is populating the UI from settings (and when slider
    // observables emit their initial value). Without this flag those events would
    // immediately write the just-loaded values back — and, mid-construction, can
    // clobber settings with control defaults. Every handler early-returns while
    // this is true. (Replicates the v2.61.2 WPF fix in Avalonia's lifecycle.)
    private bool _initializing = true;

    // Parameterless ctor for the XAML runtime loader / designer.
    public MainWindow()
    {
        InitializeComponent();
    }

    public MainWindow(SettingsService settingsService, IAudioBackend audioBackend, SerialConnectionService connection, DeviceStateService deviceState, VolumeOverlayController overlay, UpdateOrchestrator updateOrchestrator, StartupOptions startup) : this()
    {
        _settingsService = settingsService;
        _settings = settingsService.Settings;
        _audioBackend = audioBackend;
        _connection = connection;
        _deviceState = deviceState;
        _overlay = overlay;
        _updateOrchestrator = updateOrchestrator;
        _forceDebugTab = startup.ForceDebugTab;
        _safeMode = startup.SafeMode;

        WireSliders();
        ApplySettingsToUi();
        _initializing = false;

        InitAudioTab();
        InitChannelDetail();
        InitDebugTab();

        // Auto-update banner (v3.19): show it now if a check already found an update
        // before this window existed, and stay subscribed for later checks.
        _updateOrchestrator.UpdateAvailable += OnUpdateAvailable;
        if (_updateOrchestrator.Pending is { } pending)
            ShowUpdateBanner(pending);
    }

    private void Save() => _settingsService?.Save();

    // ── Minimise-to-tray window behaviour ─────────────────────────────────────
    // Mirrors the WPF host: with "minimise to tray" on, closing or minimising the
    // window hides it (the tray icon keeps the app running); with it off, closing
    // exits the app. Tray "Exit" sets _reallyClose to bypass the hide guard.

    private bool _reallyClose;

    /// <summary>Lets the tray "Exit" command close the window for real.</summary>
    public void AllowClose() => _reallyClose = true;

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_reallyClose && _settings.MinimizeToTray)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        base.OnClosing(e);

        // A user close with minimise-to-tray off must exit the app (the lifetime is
        // OnExplicitShutdown). Shutdown() closes every window, which re-invokes this
        // OnClosing — so set _reallyClose *first*: without it the re-entrant call
        // takes this same branch and calls Shutdown() again, recursing until the
        // stack overflows. (The tray "Exit" command sets it via AllowClose() for the
        // same reason.)
        if (!_reallyClose &&
            Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _reallyClose = true;
            desktop.Shutdown();
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == WindowStateProperty &&
            WindowState == WindowState.Minimized &&
            _settings.MinimizeToTray)
        {
            HideToTray();
        }
    }

    private void HideToTray()
    {
        Hide();
        ShowInTaskbar = false;
    }

    // ── Audio tab ───────────────────────────────────────────────────────────────

    private void InitAudioTab()
    {
        for (int i = 0; i < _settings.Channels.Length; i++)
            _channelRows.Add(new ChannelRow { Channel = i + 1 });
        ChannelGrid.ItemsSource = _channelRows;

        SafeModeBanner.IsVisible = _safeMode;
        PopulatePorts();

        RefreshTargets();
        RefreshChannelStates();

        // Connection status, updated live. On connect, push channel state right
        // away (the DeviceStateService pushes OLED config itself) so the OLEDs
        // populate without waiting for the next poll tick.
        UpdateConnectionStatus();
        if (_connection != null)
            _connection.StateChanged += s => Dispatcher.UIThread.Post(() =>
            {
                UpdateConnectionStatus();
                if (s == SerialConnectionState.Connected)
                {
                    UpdatePairedControllerLabel(); // chip ID is auto-paired on connect
                    RefreshChannelStates();
                }
            });

        // Poll live channel state ~20x/sec so the physical OLEDs track a volume-
        // smoothing ramp smoothly (CHSTATE change-detection keeps it quiet when
        // idle). The OLED-tab preview render is gated to the OLED tab so the faster
        // poll stays cheap. 50ms sits just under the firmware's single-channel I2C
        // OLED redraw ceiling (~30Hz at 400kHz); pushing faster only queues CHSTATE.
        _channelPollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _channelPollTimer.Tick += (_, _) => RefreshChannelStates();
        _channelPollTimer.Start();

        // Q2: auto-discover newly-launched apps in the target picker. A slow timer
        // catches new app sessions (which raise no OS notification), and the backend's
        // TargetsChanged event gives an instant refresh on a default-device switch.
        // Both funnel through the change-detecting AutoRefreshTargetsIfChanged so an
        // unchanged target set never rebuilds the combo.
        _targetRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(TargetRefreshIntervalMs) };
        _targetRefreshTimer.Tick += (_, _) => AutoRefreshTargetsIfChanged();
        _targetRefreshTimer.Start();
        if (_audioBackend != null)
            _audioBackend.TargetsChanged += () => Dispatcher.UIThread.Post(AutoRefreshTargetsIfChanged);
    }

    private void UpdateConnectionStatus()
    {
        SerialConnectionState state = _connection?.State ?? SerialConnectionState.Disconnected;

        // A connected controller passes the (protocol-only) identity check but can
        // still report a different channel count than the dashboard expects (Q6) —
        // e.g. older/newer hardware revisions. Flag it the same way an incompatible-
        // protocol rejection is flagged, so both diagnosable causes of "channels
        // aren't behaving as expected" are visible from the same line.
        bool channelMismatch = state == SerialConnectionState.Connected && _connection != null &&
            _connection.ConnectedChannelCount != SerialConnectionService.ExpectedChannelCount;

        // ── Audio tab: a simple at-a-glance state word only (no protocol/chip). ──
        string simpleState = state switch
        {
            SerialConnectionState.Connected => "Connected",
            SerialConnectionState.Identifying => "Connecting…",
            SerialConnectionState.Incompatible => "Incompatible controller",
            _ => "Disconnected",
        };
        ConnectionStatusText.Text = simpleState;

        // Shared status dot: green = connected, amber = connecting, red = a problem
        // (incompatible firmware or a channel-count mismatch), grey = idle.
        IBrush dot = state switch
        {
            SerialConnectionState.Connected when channelMismatch => Brushes.OrangeRed,
            SerialConnectionState.Connected => Brushes.LimeGreen,
            SerialConnectionState.Identifying => Brushes.Orange,
            SerialConnectionState.Incompatible => Brushes.OrangeRed,
            _ => Brushes.Gray,
        };
        ConnectionStatusDot.Fill = dot;
        SetupConnStatusDot.Fill = dot;

        // Colour the Audio state word as a warning only when something's wrong.
        if (state == SerialConnectionState.Incompatible || channelMismatch)
            ConnectionStatusText.Foreground = Brushes.OrangeRed;
        else
            ConnectionStatusText.ClearValue(TextBlock.ForegroundProperty);

        // ── Setup tab: the full detail — state, protocol, chip ID, + a warning. ──
        SetupConnStateText.Text = simpleState;
        if (state == SerialConnectionState.Incompatible || channelMismatch)
            SetupConnStateText.Foreground = Brushes.OrangeRed;
        else
            SetupConnStateText.ClearValue(TextBlock.ForegroundProperty);

        bool haveConn = state == SerialConnectionState.Connected && _connection != null;
        SetupConnProtocolText.Text = haveConn ? $"{_connection!.Protocol}" : "—";
        SetupConnChipText.Text =
            haveConn && _connection!.ConnectedChipId is { Length: > 0 } chip ? chip : "—";

        if (channelMismatch)
        {
            SetupConnWarningText.Text =
                $"Controller reports {_connection!.ConnectedChannelCount} channel(s), " +
                $"expected {SerialConnectionService.ExpectedChannelCount}; some channels won't function.";
            SetupConnWarningText.IsVisible = true;
        }
        else if (state == SerialConnectionState.Incompatible)
        {
            SetupConnWarningText.Text = IncompatibleControllerMessage();
            SetupConnWarningText.IsVisible = true;
        }
        else
        {
            SetupConnWarningText.IsVisible = false;
        }

        // Reconnect makes sense while disconnected or stuck on incompatible firmware
        // (e.g. after a re-flash); Disconnect while linked/scanning/incompatible.
        ReconnectButton.IsEnabled = _connection != null &&
            state is SerialConnectionState.Disconnected or SerialConnectionState.Incompatible;
        DisconnectButton.IsEnabled = _connection != null && state != SerialConnectionState.Disconnected;
    }

    /// <summary>
    /// Builds the user-facing warning for a recognised controller whose firmware is
    /// too old to connect (the strict handshake rejects it — standing rule #5).
    /// </summary>
    private string IncompatibleControllerMessage()
    {
        IncompatibleControllerInfo? info = _connection?.IncompatibleController;
        if (info is null)
            return "Incompatible controller firmware — update required.";

        return $"Incompatible controller on {info.Port}: firmware reports protocol " +
               $"{info.Protocol} ({info.ChannelCount} channels), but this app requires " +
               $"protocol {SerialConnectionService.MinProtocol}+. Update the controller firmware.";
    }

    private void ReconnectButton_Click(object? sender, RoutedEventArgs e) => _connection?.Reconnect();

    private void DisconnectButton_Click(object? sender, RoutedEventArgs e) => _connection?.Disconnect();

    // ── Manual per-port picker (N2) ──────────────────────────────────────────────

    /// <summary>
    /// Fills the port picker with the currently-available serial ports, preserving the
    /// current selection where possible. Refreshed when the dropdown is opened so it
    /// always reflects what's plugged in without a background timer.
    /// </summary>
    private void PopulatePorts()
    {
        object? previous = PortCombo.SelectedItem;
        string[] ports = SerialService.GetPortNames();
        PortCombo.ItemsSource = ports;
        if (previous is string prev && ports.Contains(prev, StringComparer.OrdinalIgnoreCase))
            PortCombo.SelectedItem = prev;
        else if (PortCombo.SelectedItem == null && ports.Length > 0)
            PortCombo.SelectedIndex = 0;
    }

    private void PortCombo_DropDownOpened(object? sender, EventArgs e) => PopulatePorts();

    /// <summary>Connects to the specifically-chosen port, bypassing auto-detect
    /// (<see cref="SerialConnectionService.Connect(string)"/>).</summary>
    private void ConnectToPort_Click(object? sender, RoutedEventArgs e)
    {
        if (PortCombo.SelectedItem is string port && !string.IsNullOrWhiteSpace(port))
            _connection?.Connect(port);
    }

    /// <summary>
    /// Repopulates the target pickers from the given target list (or a fresh
    /// enumeration when <paramref name="fetched"/> is null), preserving each combo's
    /// selection by key, and records the target-key set so the Q2 auto-refresh can
    /// tell when the set has actually changed.
    /// </summary>
    private void RefreshTargets(IReadOnlyList<AudioTarget>? fetched = null)
    {
        var targets = (fetched ?? _audioBackend?.GetAvailableTargets() ?? (IReadOnlyList<AudioTarget>)Array.Empty<AudioTarget>()).ToList();
        object? previous = TargetCombo.SelectedItem;
        TargetCombo.ItemsSource = targets;
        // Preserve selection by key where possible.
        if (previous is AudioTarget prev)
            TargetCombo.SelectedItem = targets.FirstOrDefault(t => t.Key == prev.Key);

        // The per-channel-detail pool picker draws from the same target list.
        object? prevPool = DetailPoolCombo.SelectedItem;
        DetailPoolCombo.ItemsSource = targets;
        if (prevPool is AudioTarget pp)
            DetailPoolCombo.SelectedItem = targets.FirstOrDefault(t => t.Key == pp.Key);

        _lastTargetKeys = new HashSet<string>(targets.Select(t => t.Key), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Q2: re-enumerates the assignable targets and refreshes the pickers only when the
    /// set of keys has changed since the last refresh, so a newly-launched (or closed)
    /// app appears in the dropdown without a manual Refresh — but an unchanged set never
    /// rebuilds the combo (which would disrupt an open dropdown or a live selection).
    /// Runs off a slow timer (new app sessions raise no OS notification) and the
    /// backend's TargetsChanged event (default-device switch). WASAPI's 100ms session
    /// cache means the periodic enumeration is fresh without forcing InvalidateCache.
    /// </summary>
    private void AutoRefreshTargetsIfChanged()
    {
        if (_audioBackend == null) return;
        IReadOnlyList<AudioTarget> targets = _audioBackend.GetAvailableTargets();
        var keys = new HashSet<string>(targets.Select(t => t.Key), StringComparer.OrdinalIgnoreCase);
        if (keys.SetEquals(_lastTargetKeys)) return;

        RefreshTargets(targets); // updates the pickers and _lastTargetKeys
        RefreshChannelStates();
    }

    private void RefreshChannelStates()
    {
        ChannelSettings[] channels = _settings.Channels;
        var liveStates = new List<ChannelLiveState>(_channelRows.Count);

        for (int i = 0; i < _channelRows.Count && i < channels.Length; i++)
        {
            ChannelRow row = _channelRows[i];
            ChannelSettings ch = channels[i];

            row.DisplayName = string.IsNullOrWhiteSpace(ch.FriendlyName) ? $"Channel {i + 1}" : ch.FriendlyName;

            int volumePercent = 0;
            bool muted = false;
            string status;

            // Effective target key (a pool resolves to its first live entry).
            string key = _audioBackend != null ? Audio.ChannelTargets.ResolveActiveKey(ch, _audioBackend) : ch.TargetKey;
            if (!Audio.ChannelTargets.HasTarget(ch) || string.IsNullOrWhiteSpace(key))
            {
                row.AssignedLabel = "Unassigned";
                row.VolumeDisplay = "—";
                row.MuteDisplay = "—";
                row.Status = status = "Unassigned";
            }
            else
            {
                row.AssignedLabel = Audio.ChannelTargets.UsesPool(ch) ? $"{LabelForKey(key)}  (pool)" : LabelForKey(key);

                float vol = _audioBackend?.GetVolumeByKey(key) ?? -1f;
                if (vol < 0f)
                {
                    row.VolumeDisplay = "—";
                    row.MuteDisplay = "—";
                    row.Status = status = key.StartsWith("PROC:", StringComparison.OrdinalIgnoreCase) ? "App offline" : "Unavailable";
                }
                else
                {
                    volumePercent = Math.Clamp((int)Math.Round(vol * 100), 0, 100);
                    muted = _audioBackend?.GetMuteByKey(key) ?? false;
                    row.VolumeDisplay = $"{volumePercent}%";
                    row.MuteDisplay = muted ? "Yes" : "No";
                    row.Status = status = "Active";
                }
            }

            liveStates.Add(new ChannelLiveState(i, row.DisplayName, volumePercent, muted, status));
        }

        // Push live state to the controller so the physical OLEDs/display update.
        // The service applies change detection, so calling it every poll is cheap.
        _deviceState?.PushChannelStates(liveStates, ChannelGrid.SelectedIndex);

        // Keep the OLED-tab previews in sync with the live hardware state, but only
        // render them while the OLED tab is actually showing (the poll runs ~10x/sec).
        _lastLive = liveStates;
        if (IsOledTabSelected())
            RenderOledPreviews();
    }

    private bool IsOledTabSelected() =>
        (MainTabs.SelectedItem as TabItem)?.Header as string == "OLED Setup";

    private void AssignTarget_Click(object? sender, RoutedEventArgs e)
    {
        int index = ChannelGrid.SelectedIndex;
        if (index < 0 || index >= _settings.Channels.Length) return;
        if (TargetCombo.SelectedItem is not AudioTarget target) return;

        ChannelSettings ch = _settings.Channels[index];
        ch.TargetKey = target.Key;
        if (string.IsNullOrWhiteSpace(ch.FriendlyName))
            ch.FriendlyName = LabelForKey(target.Key);
        Save();
        RefreshChannelStates();
    }

    private void ClearTarget_Click(object? sender, RoutedEventArgs e)
    {
        int index = ChannelGrid.SelectedIndex;
        if (index < 0 || index >= _settings.Channels.Length) return;

        _settings.Channels[index].TargetKey = string.Empty;
        Save();
        RefreshChannelStates();
    }

    private void RefreshTargets_Click(object? sender, RoutedEventArgs e)
    {
        _audioBackend?.InvalidateCache();
        RefreshTargets();
        RefreshChannelStates();
    }

    /// <summary>Human-readable label for a target key (cross-platform; no Platform.Windows dependency).</summary>
    private static string LabelForKey(string key)
    {
        if (key.Equals("MASTER", StringComparison.OrdinalIgnoreCase)) return "Master";
        if (key.Equals("MIC_INPUT", StringComparison.OrdinalIgnoreCase)) return "Microphone Input";
        if (key.StartsWith("PROC:", StringComparison.OrdinalIgnoreCase)) return key[5..];
        if (key.StartsWith("VM_STRIP:", StringComparison.OrdinalIgnoreCase) && int.TryParse(key[9..], out int s)) return $"Strip {s + 1}";
        if (key.StartsWith("VM_BUS:", StringComparison.OrdinalIgnoreCase) && int.TryParse(key[7..], out int b)) return $"Bus {b + 1}";
        return key;
    }

    // ── Slider wiring ───────────────────────────────────────────────────────
    // Subscribe via the property observable (fires once now, guarded by
    // _initializing) so we don't depend on a specific RangeBase event signature.

    private void WireSliders()
    {
        EncoderSensitivitySlider.GetObservable(Slider.ValueProperty).Subscribe(new AnonymousObserver(_ => OnSensitivityChanged()));
        AccelThresholdSlider.GetObservable(Slider.ValueProperty).Subscribe(new AnonymousObserver(_ => OnAccelThresholdChanged()));
        AccelMaxMultiplierSlider.GetObservable(Slider.ValueProperty).Subscribe(new AnonymousObserver(_ => OnAccelMaxMultiplierChanged()));
        AccelCurveSlider.GetObservable(Slider.ValueProperty).Subscribe(new AnonymousObserver(_ => OnAccelCurveChanged()));
        OverlayTimeoutSlider.GetObservable(Slider.ValueProperty).Subscribe(new AnonymousObserver(_ => OnOverlayTimeoutChanged()));
        OverlayOpacitySlider.GetObservable(Slider.ValueProperty).Subscribe(new AnonymousObserver(_ => OnOverlayOpacityChanged()));
        OverlayScaleSlider.GetObservable(Slider.ValueProperty).Subscribe(new AnonymousObserver(_ => OnOverlayScaleChanged()));
        OledBrightnessSlider.GetObservable(Slider.ValueProperty).Subscribe(new AnonymousObserver(_ => OnOledBrightnessChanged()));
        OledSleepTimeoutSlider.GetObservable(Slider.ValueProperty).Subscribe(new AnonymousObserver(_ => OnOledSleepTimeoutChanged()));
        OledConnectedIdleTimeoutSlider.GetObservable(Slider.ValueProperty).Subscribe(new AnonymousObserver(_ => OnOledConnectedIdleTimeoutChanged()));
        DetailSensSlider.GetObservable(Slider.ValueProperty).Subscribe(new AnonymousObserver(_ => OnDetailSensChanged()));
    }

    /// <summary>Minimal IObserver&lt;double&gt; that forwards OnNext to an action.</summary>
    private sealed class AnonymousObserver : IObserver<double>
    {
        private readonly Action<double> _onNext;
        public AnonymousObserver(Action<double> onNext) => _onNext = onNext;
        public void OnNext(double value) => _onNext(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }

    // ── Apply settings → UI ───────────────────────────────────────────────────

    private void ApplySettingsToUi()
    {
        DashboardVersionText.Text   = $"Dashboard version: {DashboardVersion}";
        ExpectedProtocolText.Text   = $"Required controller protocol: {RequiredProtocolVersion}";
        SettingsPathText.Text       = $"Settings file: {SettingsService.SettingsPath}";

        AutoConnectCheckBox.IsChecked          = _settings.AutoConnectOnLaunch;
        ScanAllComPortsCheckBox.IsChecked      = _settings.ScanAllComPortsIfRememberedMissing;
        MinimizeToTrayCheckBox.IsChecked       = _settings.MinimizeToTray;
        StartMinimizedToTrayCheckBox.IsChecked = _settings.StartMinimizedToTray;
        StartWithWindowsCheckBox.IsChecked     = _settings.StartWithWindows;
        AdvancedDebugLoggingCheckBox.IsChecked = _settings.AdvancedDebugLogging;
        TrayNotificationsCheckBox.IsChecked    = _settings.TrayNotificationsEnabled;
        AdvancedDebugFeaturesCheckBox.IsChecked = _settings.AdvancedDebugFeatures;
        UpdateDebugTabVisibility();

        UpdatePairedControllerLabel();
        UpdateHotkeyLabels();

        WasapiRadioButton.IsChecked      = _settings.AudioBackendMode != AudioBackendModes.VoiceMeeter;
        VoiceMeeterRadioButton.IsChecked = _settings.AudioBackendMode == AudioBackendModes.VoiceMeeter;
        UpdateAudioBackendStatus();

        EncoderSensitivitySlider.Value = Math.Clamp(_settings.EncoderSensitivityPercent, 0, 500);
        UpdateSensitivityLabel();

        AccelerationEnabledCheckBox.IsChecked    = _settings.AccelerationEnabled;
        AccelerationPresetComboBox.SelectedIndex = PresetToIndex(_settings.AccelerationPreset);
        AccelThresholdSlider.Value     = Math.Clamp(_settings.AccelThresholdMs, 20, 250);
        AccelMaxMultiplierSlider.Value = Math.Clamp(_settings.AccelMaxMultiplier, 1.5f, 8.0f);
        AccelCurveSlider.Value         = Math.Clamp(_settings.AccelCurveExponent, 0.3f, 2.5f);
        UpdateAccelLabels();
        UpdateAccelCustomPanelVisibility();

        VolumeSmoothingEnabledCheckBox.IsChecked   = _settings.VolumeSmoothingEnabled;
        VolumeSmoothingSpeedComboBox.SelectedIndex = SmoothingToIndex(_settings.VolumeSmoothingSpeed);

        ThemeFollowSystemRadioButton.IsChecked = _settings.ThemeMode == ThemeModes.FollowSystem;
        ThemeLightRadioButton.IsChecked        = _settings.ThemeMode == ThemeModes.Light;
        ThemeDarkRadioButton.IsChecked         = _settings.ThemeMode == ThemeModes.Dark;
        ApplyTheme(_settings.ThemeMode);

        OverlayEnabledCheckBox.IsChecked   = _settings.OverlayEnabled;
        OverlayPositionComboBox.SelectedIndex = PositionToIndex(_settings.OverlayPosition);
        OverlayTimeoutSlider.Value = Math.Clamp(_settings.OverlayTimeoutSeconds, 1, 8);
        UpdateOverlayTimeoutLabel();
        OverlayOpacitySlider.Value = Math.Clamp(_settings.OverlayOpacity * 100, 30, 100);
        UpdateOverlayOpacityLabel();
        OverlayScaleSlider.Value = Math.Clamp(_settings.OverlayScale * 100, 75, 150);
        UpdateOverlayScaleLabel();
        OverlayAllScreensCheckBox.IsChecked = _settings.OverlayAllScreens;

        // OLED Setup
        DisplayModeComboBox.SelectedIndex = DisplayModeToIndex(_settings.OledDisplayMode);
        OledBrightnessSlider.Value = Math.Clamp(_settings.OledBrightnessPercent, 0, 100);
        UpdateOledBrightnessLabel();
        OledSleepTimeoutSlider.Value = Math.Clamp(_settings.OledSleepTimeoutMinutes, 1, 60);
        UpdateOledSleepTimeoutLabel();
        OledConnectedIdleActionComboBox.SelectedIndex = IdleActionToIndex(_settings.OledConnectedIdleAction);
        OledConnectedIdleTimeoutSlider.Value = Math.Clamp(_settings.OledConnectedIdleTimeoutMinutes, 1, 60);
        UpdateOledConnectedIdleTimeoutLabel();
        OledAntiBurnInCheckBox.IsChecked = _settings.OledAntiBurnInEnabled;
        RenderOledPreviews();
    }

    // ── Application Setup ─────────────────────────────────────────────────────

    private void AppSetupCheckBox_Changed(object? sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _settings.AutoConnectOnLaunch                = AutoConnectCheckBox.IsChecked == true;
        _settings.ScanAllComPortsIfRememberedMissing = ScanAllComPortsCheckBox.IsChecked == true;
        _settings.MinimizeToTray                     = MinimizeToTrayCheckBox.IsChecked == true;
        _settings.StartMinimizedToTray               = StartMinimizedToTrayCheckBox.IsChecked == true;
        _settings.StartWithWindows                   = StartWithWindowsCheckBox.IsChecked == true;
        _settings.AdvancedDebugLogging               = AdvancedDebugLoggingCheckBox.IsChecked == true;
        _settings.TrayNotificationsEnabled           = TrayNotificationsCheckBox.IsChecked == true;
        _settings.AdvancedDebugFeatures              = AdvancedDebugFeaturesCheckBox.IsChecked == true;
        Save();

        // Show/hide the Debug tab live to match the toggle (--debug keeps it shown).
        UpdateDebugTabVisibility();

        // Apply the run-on-login registry entry to match the toggle (Windows; no-op
        // elsewhere). Idempotent, so running it for any app-setup change is fine.
        Platform.WindowsGlue.ApplyRunOnStartup(_settings.StartWithWindows);
    }

    private async void ForgetControllerButton_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_settings.LastDeviceChipId)) return; // nothing paired

        bool ok = await Dialogs.ConfirmAsync(this, "Forget controller",
            "Forget the paired controller? The next controller that connects will be paired automatically.");
        if (!ok) return;

        _settings.LastDeviceChipId = string.Empty;
        Save();
        UpdatePairedControllerLabel();
    }

    private async void RunSetupWizardButton_Click(object? sender, RoutedEventArgs e)
    {
        var wizard = App.Services.GetService<FirstRunWizard>();
        if (wizard == null) return;

        // Pause the channel-state poll while the wizard is open so its OLED identify
        // screens aren't immediately overwritten by CHSTATE pushes; resume on close.
        _channelPollTimer?.Stop();
        try
        {
            await wizard.ShowDialog(this);
        }
        finally
        {
            _channelPollTimer?.Start();
            _deviceState?.ForceResend(); // redraw the OLEDs from live state after identify

            // Reflect any channel assignments the wizard made.
            RefreshChannelStates();
            LoadChannelDetail(ChannelGrid.SelectedIndex >= 0 ? ChannelGrid.SelectedIndex : 0);
        }
    }

    // ── Audio backend ─────────────────────────────────────────────────────────

    private void AudioBackendRadioButton_Changed(object? sender, RoutedEventArgs e)
    {
        if (_initializing) return;

        string mode = VoiceMeeterRadioButton.IsChecked == true
            ? AudioBackendModes.VoiceMeeter
            : AudioBackendModes.Wasapi;
        if (mode == _settings.AudioBackendMode) return; // dedupe the paired check/uncheck events

        _settings.AudioBackendMode = mode;
        Save();

        (_audioBackend as Audio.SwitchableAudioBackend)?.SwitchTo(mode);
        RefreshTargets();
        RefreshChannelStates();
        UpdateAudioBackendStatus();
    }

    private void UpdateAudioBackendStatus()
    {
        int targets = _audioBackend?.GetAvailableTargets().Count ?? 0;
        string name = _audioBackend?.BackendName ?? "None";
        AudioBackendStatusText.Text = $"Active: {name} — {targets} target(s).";
    }

    private void UpdatePairedControllerLabel() =>
        PairedControllerText.Text = string.IsNullOrEmpty(_settings.LastDeviceChipId)
            ? "Paired controller: (none)"
            : $"Paired controller: chip ID {_settings.LastDeviceChipId}";

    // ── Encoder Sensitivity ────────────────────────────────────────────────────

    private void OnSensitivityChanged()
    {
        UpdateSensitivityLabel();
        if (_initializing) return;
        _settings.EncoderSensitivityPercent = (int)Math.Round(EncoderSensitivitySlider.Value);
        Save();
    }

    private void UpdateSensitivityLabel() =>
        EncoderSensitivityValueText.Text = $"{(int)Math.Round(EncoderSensitivitySlider.Value)}%";

    // ── Encoder Feel ───────────────────────────────────────────────────────────

    private void EncoderFeelCheckBox_Changed(object? sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _settings.AccelerationEnabled   = AccelerationEnabledCheckBox.IsChecked == true;
        _settings.VolumeSmoothingEnabled = VolumeSmoothingEnabledCheckBox.IsChecked == true;
        Save();
    }

    private void AccelerationPresetComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        UpdateAccelCustomPanelVisibility();
        if (_initializing) return;
        _settings.AccelerationPreset = IndexToPreset(AccelerationPresetComboBox.SelectedIndex);
        Save();
    }

    private void VolumeSmoothingSpeedComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        _settings.VolumeSmoothingSpeed = IndexToSmoothing(VolumeSmoothingSpeedComboBox.SelectedIndex);
        Save();
    }

    private void OnAccelThresholdChanged()
    {
        UpdateAccelLabels();
        if (_initializing) return;
        _settings.AccelThresholdMs = (int)Math.Round(AccelThresholdSlider.Value);
        Save();
    }

    private void OnAccelMaxMultiplierChanged()
    {
        UpdateAccelLabels();
        if (_initializing) return;
        _settings.AccelMaxMultiplier = (float)AccelMaxMultiplierSlider.Value;
        Save();
    }

    private void OnAccelCurveChanged()
    {
        UpdateAccelLabels();
        if (_initializing) return;
        _settings.AccelCurveExponent = (float)AccelCurveSlider.Value;
        Save();
    }

    private void UpdateAccelLabels()
    {
        AccelThresholdLabel.Text     = $"{(int)Math.Round(AccelThresholdSlider.Value)} ms";
        AccelMaxMultiplierLabel.Text = $"{AccelMaxMultiplierSlider.Value:0.0}x";
        double c = AccelCurveSlider.Value;
        AccelCurveLabel.Text = c < 0.8 ? "Early" : c <= 1.2 ? "Linear" : "Late";
    }

    private void UpdateAccelCustomPanelVisibility() =>
        AccelCustomPanel.IsVisible = AccelerationPresetComboBox.SelectedIndex == 3; // Custom

    // ── Style Settings ─────────────────────────────────────────────────────────

    private void ThemeRadioButton_Changed(object? sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        string mode = ThemeDarkRadioButton.IsChecked == true ? ThemeModes.Dark
                    : ThemeLightRadioButton.IsChecked == true ? ThemeModes.Light
                    : ThemeModes.FollowSystem;
        _settings.ThemeMode = mode;
        ApplyTheme(mode);
        Save();
    }

    private static void ApplyTheme(string mode)
    {
        if (Application.Current is null) return;
        Application.Current.RequestedThemeVariant = mode switch
        {
            ThemeModes.Light => ThemeVariant.Light,
            ThemeModes.Dark  => ThemeVariant.Dark,
            _                => ThemeVariant.Default, // follow system
        };
    }

    // ── Volume Overlay ─────────────────────────────────────────────────────────

    private void OverlayCheckBox_Changed(object? sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _settings.OverlayEnabled = OverlayEnabledCheckBox.IsChecked == true;
        Save();
    }

    private void OverlayPositionComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        if (OverlayPositionComboBox.SelectedItem is ComboBoxItem { Tag: string tag })
        {
            _settings.OverlayPosition = tag;
            Save();
            _overlay?.ShowPreview();
        }
    }

    private void OnOverlayTimeoutChanged()
    {
        UpdateOverlayTimeoutLabel();
        if (_initializing) return;
        _settings.OverlayTimeoutSeconds = Math.Round(OverlayTimeoutSlider.Value, 1);
        Save();
    }

    private void UpdateOverlayTimeoutLabel() =>
        OverlayTimeoutValueText.Text = $"{OverlayTimeoutSlider.Value:0.0} s";

    private void OnOverlayOpacityChanged()
    {
        UpdateOverlayOpacityLabel();
        if (_initializing) return;
        _settings.OverlayOpacity = Math.Round(OverlayOpacitySlider.Value / 100.0, 2);
        Save();
        _overlay?.ShowPreview();
    }

    private void UpdateOverlayOpacityLabel() =>
        OverlayOpacityValueText.Text = $"{OverlayOpacitySlider.Value:0}%";

    private void OnOverlayScaleChanged()
    {
        UpdateOverlayScaleLabel();
        if (_initializing) return;
        _settings.OverlayScale = Math.Round(OverlayScaleSlider.Value / 100.0, 2);
        Save();
        _overlay?.ShowPreview();
    }

    private void UpdateOverlayScaleLabel() =>
        OverlayScaleValueText.Text = $"{OverlayScaleSlider.Value:0}%";

    private void OverlayAllScreensCheckBox_Changed(object? sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _settings.OverlayAllScreens = OverlayAllScreensCheckBox.IsChecked == true;
        Save();
        _overlay?.ShowPreview();
    }

    private void OverlayPreviewButton_Click(object? sender, RoutedEventArgs e) =>
        _overlay?.ShowPreview();

    // ── Maintenance ────────────────────────────────────────────────────────────

    private async void FactoryResetButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_settingsService == null) return;

        bool ok = await Dialogs.ConfirmAsync(this, "Factory reset",
            "Reset all settings to their defaults? This clears your channel assignments, " +
            "encoder/OLED preferences, and paired controller. This cannot be undone.");
        if (!ok) return;

        _settingsService.Reset();
        _settings = _settingsService.Settings;

        // Re-bind the UI from the fresh defaults, guarded so the programmatic
        // control updates don't re-trigger saves.
        _initializing = true;
        ApplySettingsToUi();
        _initializing = false;
        LoadChannelDetail(0);

        // Bindings were wiped — drop the registered global hotkeys to match.
        App.Services.GetService<GlobalHotkeyManager>()?.SyncFromSettings();
    }

    private static readonly FilePickerFileType JsonSettingsType =
        new("Dashboard settings (JSON)") { Patterns = new[] { "*.json" } };

    private async void ExportSettingsButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_settingsService == null) return;
        TopLevel? top = TopLevel.GetTopLevel(this);
        if (top is null) return;

        IStorageFile? file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export settings",
            SuggestedFileName = $"pcvc-settings-{DateTime.Now:yyyyMMdd-HHmmss}.json",
            DefaultExtension = "json",
            FileTypeChoices = new[] { JsonSettingsType },
        });
        if (file is null) return;

        try
        {
            _settingsService.ExportTo(file.Path.LocalPath);
            ShowSettingsIoStatus($"Exported to {file.Name}.");
        }
        catch (Exception ex)
        {
            ShowSettingsIoStatus($"Export failed: {ex.Message}");
        }
    }

    private async void ImportSettingsButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_settingsService == null) return;
        TopLevel? top = TopLevel.GetTopLevel(this);
        if (top is null) return;

        IReadOnlyList<IStorageFile> files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import settings",
            AllowMultiple = false,
            FileTypeFilter = new[] { JsonSettingsType },
        });
        if (files.Count == 0) return;

        bool ok = await Dialogs.ConfirmAsync(this, "Import settings",
            "Replace your current settings with the imported file? Your current settings will be overwritten.");
        if (!ok) return;

        if (_settingsService.ImportFrom(files[0].Path.LocalPath))
        {
            _settings = _settingsService.Settings;
            _initializing = true;
            ApplySettingsToUi();
            _initializing = false;
            RefreshTargets();
            RefreshChannelStates();
            LoadChannelDetail(ChannelGrid.SelectedIndex >= 0 ? ChannelGrid.SelectedIndex : 0);
            _deviceState?.PushOledConfig();
            _deviceState?.PushAllChannelOledModes();
            App.Services.GetService<GlobalHotkeyManager>()?.SyncFromSettings();
            ShowSettingsIoStatus("Settings imported.");
        }
        else
        {
            await Dialogs.ShowAsync(this, "Import failed",
                "That file could not be read as a valid settings file.");
        }
    }

    private void ShowSettingsIoStatus(string message)
    {
        SettingsIoStatusText.Text = message;
        SettingsIoStatusText.IsVisible = true;
    }

    // ── App info buttons ───────────────────────────────────────────────────────

    private const string ProjectUrl = "https://github.com/aussamc/PcVolumeControllerDashboard-V3.0";

    private async void AboutButton_Click(object? sender, RoutedEventArgs e)
    {
        string controller = _connection?.State == SerialConnectionState.Connected
            ? $"Connected controller: protocol {_connection.Protocol}, chip {(_connection.ConnectedChipId is { Length: > 0 } c ? c : "(none)")}"
            : "Controller: not connected";

        string info =
            $"PC Volume Controller Dashboard\n" +
            $"Version {DashboardVersion} (Avalonia)\n" +
            $"Required controller protocol: {RequiredProtocolVersion}\n\n" +
            $"{controller}\n\n" +
            "A cross-platform dashboard for the PC Volume Controller hardware.\n" +
            $"{ProjectUrl}";

        await Dialogs.ShowAboutAsync(this, "About", info, ProjectUrl);
    }

    // ── Software updates ─────────────────────────────────────────────────────────

    private string? _latestReleaseUrl;

    private async void CheckUpdatesButton_Click(object? sender, RoutedEventArgs e)
    {
        var service = App.Services.GetService<UpdateCheckService>();
        if (service == null) return;

        CheckUpdatesButton.IsEnabled = false;
        ViewReleaseButton.IsVisible = false;
        // Reset the install affordances for a fresh check (unless a download is in flight).
        if (!_downloading)
        {
            UpdateInstallButton.IsVisible = false;
            UpdateInstallProgress.IsVisible = false;
        }
        ShowUpdateStatus("Checking for updates…");

        UpdateCheckResult result = await service.CheckAsync(DashboardVersion);

        if (result.ErrorMessage != null)
        {
            ShowUpdateStatus($"Couldn't check for updates: {result.ErrorMessage}");
        }
        else if (result.NoReleasesPublished)
        {
            ShowUpdateStatus("No releases have been published yet.");
        }
        else if (result.UpdateAvailable)
        {
            _latestReleaseUrl = result.ReleaseUrl;
            ViewReleaseButton.IsVisible = true;
            ShowUpdateStatus($"Update available: version {result.LatestVersion} (you have {DashboardVersion}).");

            // Offer an in-app Download & install button right here in the Software Updates
            // box (parity with the auto-check banner). PrepareInstall resolves the asset for
            // this platform and reveals the button; if there's no installable package
            // (e.g. macOS) or we're in --safe mode, the button stays hidden and the user
            // falls back to "View release".
            var info = new UpdateAvailableInfo
            {
                LatestVersion = result.LatestVersion,
                ReleaseUrl = result.ReleaseUrl,
                Assets = result.Assets,
            };
            if (!PrepareInstall(info))
                ShowUpdateStatus($"Update available: version {result.LatestVersion} (you have {DashboardVersion}). " +
                                 "No installable package for your platform — use “View release”.");
        }
        else
        {
            ShowUpdateStatus($"You're up to date (version {DashboardVersion}).");
        }

        CheckUpdatesButton.IsEnabled = true;
    }

    private void ViewReleaseButton_Click(object? sender, RoutedEventArgs e)
    {
        Dialogs.OpenUrl(_latestReleaseUrl ?? ProjectUrl);
    }

    // ── Auto-update banner (v3.19) ───────────────────────────────────────────────

    private UpdateAvailableInfo? _bannerUpdate;
    private ReleaseAsset? _bannerAsset;             // the asset picked for this platform, or null
    private UpdatePlatform _bannerPlatform;
    private string? _downloadedPath;                // verified file on disk for _downloadedVersion
    private string? _downloadedVersion;
    private bool _downloading;

    // Raised by UpdateOrchestrator on the UI thread; Post keeps us safe even if a future
    // caller raises it off-thread.
    private void OnUpdateAvailable(UpdateAvailableInfo info) =>
        Dispatcher.UIThread.Post(() => ShowUpdateBanner(info));

    private void ShowUpdateBanner(UpdateAvailableInfo info)
    {
        UpdateBannerText.Text = $"Version {info.LatestVersion} is available — you have {DashboardVersion}.";
        UpdateBanner.IsVisible = true;

        // No installable asset (macOS / missing) or safe mode → offer only "View release".
        if (!PrepareInstall(info))
            return;

        // "Automatically download & install updates": pre-download in the background so the
        // user just clicks "Install now". Launching the installer is always an explicit
        // click — we never run it unprompted.
        bool alreadyDownloaded = _downloadedPath != null && _downloadedVersion == info.LatestVersion;
        if (_settings.AutoApplyUpdates && !_downloading && !alreadyDownloaded)
            _ = DownloadUpdateAsync(applyWhenDone: false);
    }

    // Resolves the download asset for `info` on this platform and wires the shared install
    // affordances — the banner button AND the Software Updates box button — to match.
    // Returns false (both install buttons hidden) when there's no installable package for
    // this platform or we're in --safe mode; the caller falls back to "View release".
    private bool PrepareInstall(UpdateAvailableInfo info)
    {
        _bannerUpdate = info;
        _bannerPlatform = UpdateInstaller.DetectPlatform();
        _bannerAsset = UpdateAssetSelector.Select(info.Assets, _bannerPlatform);

        bool canInstall = _bannerAsset != null && !_safeMode;
        UpdateBannerInstallButton.IsVisible = canInstall;
        UpdateInstallButton.IsVisible = canInstall;
        if (!canInstall)
            return false;

        if (_downloadedPath != null && _downloadedVersion == info.LatestVersion)
            // Already fetched this version (e.g. auto-download finished) → one-click install.
            SetInstallContent("Install now", enabled: true);
        else
            SetInstallContent("Download & install", enabled: !_downloading);

        return true;
    }

    // The banner and the Software Updates box share one install flow, so their buttons and
    // progress text always show the same state regardless of which one the user clicked.
    private void SetInstallContent(string content, bool enabled)
    {
        UpdateBannerInstallButton.Content = content;
        UpdateBannerInstallButton.IsEnabled = enabled;
        UpdateInstallButton.Content = content;
        UpdateInstallButton.IsEnabled = enabled;
    }

    private void SetInstallEnabled(bool enabled)
    {
        UpdateBannerInstallButton.IsEnabled = enabled;
        UpdateInstallButton.IsEnabled = enabled;
    }

    private void SetInstallProgress(string text)
    {
        UpdateBannerProgress.IsVisible = true;
        UpdateBannerProgress.Text = text;
        UpdateInstallProgress.IsVisible = true;
        UpdateInstallProgress.Text = text;
    }

    private async void UpdateBannerInstall_Click(object? sender, RoutedEventArgs e) => await InstallClickedAsync();

    private async void UpdateInstall_Click(object? sender, RoutedEventArgs e) => await InstallClickedAsync();

    private async System.Threading.Tasks.Task InstallClickedAsync()
    {
        if (_bannerAsset == null || _bannerUpdate == null)
            return;

        // A verified download already exists for this version → launch it straight away.
        if (_downloadedPath != null && _downloadedVersion == _bannerUpdate.LatestVersion)
        {
            ApplyDownloadedUpdate();
            return;
        }
        await DownloadUpdateAsync(applyWhenDone: true);
    }

    private async System.Threading.Tasks.Task DownloadUpdateAsync(bool applyWhenDone)
    {
        if (_bannerAsset == null || _bannerUpdate == null || _downloading)
            return;
        var installer = App.Services.GetService<UpdateInstaller>();
        if (installer == null)
            return;

        _downloading = true;
        string version = _bannerUpdate.LatestVersion;
        SetInstallEnabled(false);
        SetInstallProgress("Starting download…");

        var progress = new Progress<double>(p => SetInstallProgress($"Downloading… {p * 100:0}%"));
        UpdateDownloadResult result = await installer.DownloadAsync(_bannerAsset, progress);
        _downloading = false;

        if (!result.Success)
        {
            SetInstallProgress($"Download failed: {result.ErrorMessage} Use “View release” to download it manually.");
            SetInstallContent("Retry download", enabled: true);
            return;
        }

        _downloadedPath = result.FilePath;
        _downloadedVersion = version;
        SetInstallProgress("Downloaded and verified.");
        SetInstallContent("Install now", enabled: true);

        if (applyWhenDone)
            ApplyDownloadedUpdate();
    }

    private void ApplyDownloadedUpdate()
    {
        var installer = App.Services.GetService<UpdateInstaller>();
        if (installer == null || _downloadedPath == null)
            return;

        SetInstallProgress("Launching the installer…");

        if (installer.Apply(_downloadedPath, _bannerPlatform))
        {
            // The installer/AppImage will replace the running files — exit cleanly,
            // bypassing the minimise-to-tray close guard.
            AllowClose();
            (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
        }
        else
        {
            SetInstallProgress("The update was handed to your system package installer.");
        }
    }

    private void UpdateBannerView_Click(object? sender, RoutedEventArgs e) =>
        Dialogs.OpenUrl(_bannerUpdate?.ReleaseUrl ?? ProjectUrl);

    private void UpdateBannerSkip_Click(object? sender, RoutedEventArgs e)
    {
        if (_bannerUpdate != null)
            _updateOrchestrator?.SkipVersion(_bannerUpdate.LatestVersion);
        UpdateBanner.IsVisible = false;
    }

    private void UpdateBannerDismiss_Click(object? sender, RoutedEventArgs e)
    {
        _updateOrchestrator?.DismissPending();
        UpdateBanner.IsVisible = false;
    }

    private void ShowUpdateStatus(string message)
    {
        UpdateStatusText.Text = message;
        UpdateStatusText.IsVisible = true;
    }

    private void OpenLogFolderButton_Click(object? sender, RoutedEventArgs e)
    {
        string dir = Path.GetDirectoryName(SettingsService.SettingsPath) ?? string.Empty;
        string logs = Path.Combine(dir, "logs");
        Directory.CreateDirectory(logs);
        OpenInFileManager(logs);
    }

    private async void CopySettingsPathButton_Click(object? sender, RoutedEventArgs e)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard != null)
            await clipboard.SetTextAsync(SettingsService.SettingsPath);
    }

    private void ExportDiagnosticsButton_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            string configDir = Path.GetDirectoryName(SettingsService.SettingsPath) ?? string.Empty;
            string logsDir = Path.Combine(configDir, "logs");
            string outDir = Path.Combine(configDir, "diagnostics");
            string zipPath = Path.Combine(outDir, $"diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip");

            string info =
                $"PC Volume Controller Dashboard diagnostics\r\n" +
                $"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\r\n" +
                $"Dashboard version: {DashboardVersion} (Avalonia)\r\n" +
                $"Required protocol: {RequiredProtocolVersion}\r\n" +
                $"OS: {RuntimeInformation.OSDescription}\r\n" +
                $"Architecture: {RuntimeInformation.OSArchitecture}\r\n" +
                $"Connection: {_connection?.State.ToString() ?? "n/a"}\r\n" +
                $"Controller: protocol {_connection?.Protocol ?? "n/a"}, chip {_connection?.ConnectedChipId ?? "n/a"}\r\n";

            DiagnosticsExporter.Create(zipPath, SettingsService.SettingsPath, logsDir, info);

            DiagnosticsStatusText.Text = $"Saved {Path.GetFileName(zipPath)} — opening folder…";
            DiagnosticsStatusText.IsVisible = true;
            OpenInFileManager(outDir);
        }
        catch (Exception ex)
        {
            DiagnosticsStatusText.Text = $"Diagnostics export failed: {ex.Message}";
            DiagnosticsStatusText.IsVisible = true;
        }
    }

    private static void OpenInFileManager(string path)
    {
        try
        {
            // Use ArgumentList so the launcher receives the path as a single,
            // unquoted argument — manual quoting would be passed through literally
            // (e.g. xdg-open would look for a path including the quote characters).
            var psi = new ProcessStartInfo { UseShellExecute = false };
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                psi.FileName = "explorer.exe";
                psi.UseShellExecute = true;
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                psi.FileName = "open";
            }
            else
            {
                psi.FileName = "xdg-open";
            }
            psi.ArgumentList.Add(path);
            Process.Start(psi);
        }
        catch { /* best-effort: ignore if no file manager is available */ }
    }

    // ── OLED Setup ─────────────────────────────────────────────────────────────

    private void DisplayModeComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        _settings.OledDisplayMode = IndexToDisplayMode(DisplayModeComboBox.SelectedIndex);
        Save();
        RenderOledPreviews();
        _deviceState?.PushOledConfig();
    }

    private void OledConnectedIdleActionComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        _settings.OledConnectedIdleAction = IndexToIdleAction(OledConnectedIdleActionComboBox.SelectedIndex);
        Save();
        _deviceState?.PushOledConfig();
    }

    private void OledAntiBurnInCheckBox_Changed(object? sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _settings.OledAntiBurnInEnabled = OledAntiBurnInCheckBox.IsChecked == true;
        Save();
        _deviceState?.PushOledConfig();
        RenderOledPreviews();  // reflect the shift (or its removal) in the preview immediately
    }

    private void OnOledBrightnessChanged()
    {
        UpdateOledBrightnessLabel();
        if (_initializing) return;
        _settings.OledBrightnessPercent = (int)Math.Round(OledBrightnessSlider.Value);
        Save();
        _deviceState?.PushOledConfig();
    }

    private void OnOledSleepTimeoutChanged()
    {
        UpdateOledSleepTimeoutLabel();
        if (_initializing) return;
        _settings.OledSleepTimeoutMinutes = (int)Math.Round(OledSleepTimeoutSlider.Value);
        Save();
        _deviceState?.PushOledConfig();
    }

    private void OnOledConnectedIdleTimeoutChanged()
    {
        UpdateOledConnectedIdleTimeoutLabel();
        if (_initializing) return;
        _settings.OledConnectedIdleTimeoutMinutes = (int)Math.Round(OledConnectedIdleTimeoutSlider.Value);
        Save();
        _deviceState?.PushOledConfig();
    }

    private void UpdateOledBrightnessLabel() =>
        OledBrightnessValueText.Text = $"{(int)Math.Round(OledBrightnessSlider.Value)}%";

    private void UpdateOledSleepTimeoutLabel() =>
        OledSleepTimeoutValueText.Text = $"{(int)Math.Round(OledSleepTimeoutSlider.Value)} minute(s)";

    private void UpdateOledConnectedIdleTimeoutLabel() =>
        OledConnectedIdleTimeoutValueText.Text = $"{(int)Math.Round(OledConnectedIdleTimeoutSlider.Value)} minute(s)";

    /// <summary>
    /// Renders the six OLED previews from the Core <see cref="OledRenderer"/> using
    /// each channel's live volume/mute/status (from the poll) and its per-channel
    /// OLED mode override (falling back to the global mode). Refreshed each poll so
    /// the previews track the hardware.
    /// </summary>
    private void RenderOledPreviews()
    {
        string globalMode = _settings.OledDisplayMode;
        OledPreviewModeText.Text = $"Preview mode: {DisplayModeName(globalMode)}";

        var images = new[]
        {
            OledPreview1Image, OledPreview2Image, OledPreview3Image,
            OledPreview4Image, OledPreview5Image, OledPreview6Image,
        };

        for (int i = 0; i < images.Length; i++)
        {
            // Live state from the latest poll, if available; otherwise a sensible idle default.
            string label;
            int vol;
            bool muted;
            string status;
            if (i < _lastLive.Count)
            {
                ChannelLiveState s = _lastLive[i];
                label = s.Label;
                vol = s.Volume;
                muted = s.Muted;
                status = s.Status;
            }
            else
            {
                label = i < _settings.Channels.Length && !string.IsNullOrWhiteSpace(_settings.Channels[i].FriendlyName)
                    ? _settings.Channels[i].FriendlyName
                    : $"Channel {i + 1}";
                vol = 0;
                muted = false;
                status = "—";
            }

            // Per-channel OLED mode override, else the global mode.
            string mode = i < _settings.Channels.Length && !string.IsNullOrEmpty(_settings.Channels[i].OledDisplayMode)
                ? _settings.Channels[i].OledDisplayMode
                : globalMode;

            var renderer = new OledRenderer();
            switch (mode)
            {
                case DisplayModes.LargeVolume:     renderer.RenderLargeVolume(label, vol, muted); break;
                case DisplayModes.MuteStatus:      renderer.RenderMuteStatus(label, vol, muted); break;
                case DisplayModes.AppOrDeviceName: renderer.RenderAppOrDeviceName(i + 1, label, status, vol); break;
                case DisplayModes.BarPercent:      renderer.RenderBarPercent(label, vol, muted); break;
                default:                           renderer.RenderAppVolume(label, vol, muted, status); break;
            }

            // Mirror the controller's anti-burn-in shift so the preview matches the
            // device. The poll re-renders ~10x/sec while this tab shows, so the offset
            // drifts over time just like the hardware.
            if (_settings.OledAntiBurnInEnabled)
                renderer.ApplyDisplayOffset(AntiBurnPreviewOffset());

            images[i].Source = OledImage.Build(renderer);
        }
    }

    /// <summary>
    /// The current anti-burn-in vertical offset (0..3px), mirroring the firmware's
    /// cadence of <c>(millis() / 30000) % 4</c> but on the PC wall-clock. The device
    /// and PC clocks aren't phase-synced, so this reproduces the same shifting
    /// behaviour rather than the exact same pixel at the exact same instant.
    /// </summary>
    private static int AntiBurnPreviewOffset() =>
        (int)((DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 30000L) % 4L);

    private static string DisplayModeName(string mode) => mode switch
    {
        DisplayModes.LargeVolume     => "Large volume number",
        DisplayModes.MuteStatus      => "Mute status",
        DisplayModes.AppOrDeviceName => "App/device name",
        DisplayModes.BarPercent      => "Simple bar/percentage view",
        _                            => "App name + volume",
    };

    private static int DisplayModeToIndex(string mode) => mode switch
    {
        DisplayModes.LargeVolume     => 1,
        DisplayModes.MuteStatus      => 2,
        DisplayModes.AppOrDeviceName => 3,
        DisplayModes.BarPercent      => 4,
        _                            => 0,
    };

    private static string IndexToDisplayMode(int index) => index switch
    {
        1 => DisplayModes.LargeVolume,
        2 => DisplayModes.MuteStatus,
        3 => DisplayModes.AppOrDeviceName,
        4 => DisplayModes.BarPercent,
        _ => DisplayModes.AppNameAndVolume,
    };

    private static int IdleActionToIndex(string action) => action switch
    {
        OledIdleActions.DimTo10 => 1,
        OledIdleActions.DimTo20 => 2,
        OledIdleActions.DimTo30 => 3,
        OledIdleActions.DimTo40 => 4,
        OledIdleActions.DimTo50 => 5,
        OledIdleActions.DimTo60 => 6,
        OledIdleActions.DimTo70 => 7,
        _                       => 0,
    };

    private static string IndexToIdleAction(int index) => index switch
    {
        1 => OledIdleActions.DimTo10,
        2 => OledIdleActions.DimTo20,
        3 => OledIdleActions.DimTo30,
        4 => OledIdleActions.DimTo40,
        5 => OledIdleActions.DimTo50,
        6 => OledIdleActions.DimTo60,
        7 => OledIdleActions.DimTo70,
        _ => OledIdleActions.Off,
    };

    // ── Index ↔ constant mapping ────────────────────────────────────────────────

    private static int PresetToIndex(string preset) => preset switch
    {
        AccelerationPresets.Light      => 0,
        AccelerationPresets.Medium     => 1,
        AccelerationPresets.Aggressive => 2,
        AccelerationPresets.Custom     => 3,
        _                              => 1,
    };

    private static string IndexToPreset(int index) => index switch
    {
        0 => AccelerationPresets.Light,
        2 => AccelerationPresets.Aggressive,
        3 => AccelerationPresets.Custom,
        _ => AccelerationPresets.Medium,
    };

    private static int SmoothingToIndex(string speed) => speed switch
    {
        SmoothingSpeed.Fast => 0,
        SmoothingSpeed.Slow => 2,
        _                   => 1,
    };

    private static string IndexToSmoothing(int index) => index switch
    {
        0 => SmoothingSpeed.Fast,
        2 => SmoothingSpeed.Slow,
        _ => SmoothingSpeed.Normal,
    };

    private static int PositionToIndex(string position) => position switch
    {
        "TopLeft"      => 0,
        "TopCenter"    => 1,
        "TopRight"     => 2,
        "BottomLeft"   => 3,
        "BottomCenter" => 4,
        "BottomRight"  => 5,
        _              => 4,
    };
}
