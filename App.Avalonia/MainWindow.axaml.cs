using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using PcVolumeControllerDashboard.App.Oled;
using PcVolumeControllerDashboard.App.Services;
using PcVolumeControllerDashboard.Core;
using PcVolumeControllerDashboard.Core.Audio;

namespace PcVolumeControllerDashboard.App;

public partial class MainWindow : Window
{
    // Shipping dashboard version (bumped per Avalonia-tab milestone).
    private const string DashboardVersion = "3.4";
    private const string RequiredProtocolVersion = "2.24";

    private readonly SettingsService? _settingsService;
    private IAudioBackend? _audioBackend;
    private SerialConnectionService? _connection;
    private readonly ObservableCollection<ChannelRow> _channelRows = new();
    private DispatcherTimer? _channelPollTimer;
    private DashboardSettings _settings = DashboardSettings.CreateDefault();

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

    public MainWindow(SettingsService settingsService, IAudioBackend audioBackend, SerialConnectionService connection) : this()
    {
        _settingsService = settingsService;
        _settings = settingsService.Settings;
        _audioBackend = audioBackend;
        _connection = connection;

        WireSliders();
        ApplySettingsToUi();
        _initializing = false;

        InitAudioTab();
    }

    private void Save() => _settingsService?.Save();

    // ── Audio tab ───────────────────────────────────────────────────────────────

    private void InitAudioTab()
    {
        for (int i = 0; i < _settings.Channels.Length; i++)
            _channelRows.Add(new ChannelRow { Channel = i + 1 });
        ChannelGrid.ItemsSource = _channelRows;

        RefreshTargets();
        RefreshChannelStates();

        // Connection status, updated live.
        UpdateConnectionStatus();
        if (_connection != null)
            _connection.StateChanged += _ => Dispatcher.UIThread.Post(UpdateConnectionStatus);

        // Poll live channel state (volume/mute/status) ~2x/sec, matching the WPF host.
        _channelPollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _channelPollTimer.Tick += (_, _) => RefreshChannelStates();
        _channelPollTimer.Start();
    }

    private void UpdateConnectionStatus()
    {
        ConnectionStatusText.Text = _connection?.State switch
        {
            SerialConnectionState.Connected =>
                $"Connected — protocol {_connection.Protocol}, chip {(_connection.ConnectedChipId is { Length: > 0 } c ? c : "(none)")}",
            SerialConnectionState.Identifying => "Identifying controller…",
            _ => "Disconnected",
        };
    }

    private void RefreshTargets()
    {
        var targets = (_audioBackend?.GetAvailableTargets() ?? (IReadOnlyList<AudioTarget>)Array.Empty<AudioTarget>()).ToList();
        object? previous = TargetCombo.SelectedItem;
        TargetCombo.ItemsSource = targets;
        // Preserve selection by key where possible.
        if (previous is AudioTarget prev)
            TargetCombo.SelectedItem = targets.FirstOrDefault(t => t.Key == prev.Key);
    }

    private void RefreshChannelStates()
    {
        ChannelSettings[] channels = _settings.Channels;
        for (int i = 0; i < _channelRows.Count && i < channels.Length; i++)
        {
            ChannelRow row = _channelRows[i];
            ChannelSettings ch = channels[i];

            row.DisplayName = string.IsNullOrWhiteSpace(ch.FriendlyName) ? $"Channel {i + 1}" : ch.FriendlyName;

            string key = ch.TargetKey;
            if (string.IsNullOrWhiteSpace(key))
            {
                row.AssignedLabel = "Unassigned";
                row.VolumeDisplay = "—";
                row.MuteDisplay = "—";
                row.Status = "Unassigned";
                continue;
            }

            row.AssignedLabel = LabelForKey(key);

            float vol = _audioBackend?.GetVolumeByKey(key) ?? -1f;
            if (vol < 0f)
            {
                row.VolumeDisplay = "—";
                row.MuteDisplay = "—";
                row.Status = key.StartsWith("PROC:", StringComparison.OrdinalIgnoreCase) ? "App offline" : "Unavailable";
            }
            else
            {
                row.VolumeDisplay = $"{Math.Clamp((int)Math.Round(vol * 100), 0, 100)}%";
                row.MuteDisplay = (_audioBackend?.GetMuteByKey(key) ?? false) ? "Yes" : "No";
                row.Status = "Active";
            }
        }
    }

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
        OledBrightnessSlider.GetObservable(Slider.ValueProperty).Subscribe(new AnonymousObserver(_ => OnOledBrightnessChanged()));
        OledSleepTimeoutSlider.GetObservable(Slider.ValueProperty).Subscribe(new AnonymousObserver(_ => OnOledSleepTimeoutChanged()));
        OledConnectedIdleTimeoutSlider.GetObservable(Slider.ValueProperty).Subscribe(new AnonymousObserver(_ => OnOledConnectedIdleTimeoutChanged()));
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

        UpdatePairedControllerLabel();

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
        Save();
    }

    private void ForgetControllerButton_Click(object? sender, RoutedEventArgs e)
    {
        _settings.LastDeviceChipId = string.Empty;
        Save();
        UpdatePairedControllerLabel();
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
        EncoderSensitivityValueText.Text = $"Sensitivity: {(int)Math.Round(EncoderSensitivitySlider.Value)}%";

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

    // ── Maintenance ────────────────────────────────────────────────────────────

    private void FactoryResetButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_settingsService == null) return;

        _settingsService.Reset();
        _settings = _settingsService.Settings;

        // Re-bind the UI from the fresh defaults, guarded so the programmatic
        // control updates don't re-trigger saves.
        _initializing = true;
        ApplySettingsToUi();
        _initializing = false;
    }

    // ── App info buttons ───────────────────────────────────────────────────────

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
    }

    private void OledConnectedIdleActionComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        _settings.OledConnectedIdleAction = IndexToIdleAction(OledConnectedIdleActionComboBox.SelectedIndex);
        Save();
    }

    private void OledAntiBurnInCheckBox_Changed(object? sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _settings.OledAntiBurnInEnabled = OledAntiBurnInCheckBox.IsChecked == true;
        Save();
    }

    private void OnOledBrightnessChanged()
    {
        UpdateOledBrightnessLabel();
        if (_initializing) return;
        _settings.OledBrightnessPercent = (int)Math.Round(OledBrightnessSlider.Value);
        Save();
    }

    private void OnOledSleepTimeoutChanged()
    {
        UpdateOledSleepTimeoutLabel();
        if (_initializing) return;
        _settings.OledSleepTimeoutMinutes = (int)Math.Round(OledSleepTimeoutSlider.Value);
        Save();
    }

    private void OnOledConnectedIdleTimeoutChanged()
    {
        UpdateOledConnectedIdleTimeoutLabel();
        if (_initializing) return;
        _settings.OledConnectedIdleTimeoutMinutes = (int)Math.Round(OledConnectedIdleTimeoutSlider.Value);
        Save();
    }

    private void UpdateOledBrightnessLabel() =>
        OledBrightnessValueText.Text = $"Brightness: {(int)Math.Round(OledBrightnessSlider.Value)}%";

    private void UpdateOledSleepTimeoutLabel() =>
        OledSleepTimeoutValueText.Text = $"Disconnected sleep timeout: {(int)Math.Round(OledSleepTimeoutSlider.Value)} minute(s)";

    private void UpdateOledConnectedIdleTimeoutLabel() =>
        OledConnectedIdleTimeoutValueText.Text = $"Connected idle timeout: {(int)Math.Round(OledConnectedIdleTimeoutSlider.Value)} minute(s)";

    /// <summary>
    /// Renders the six OLED previews from the Core <see cref="OledRenderer"/> using
    /// each channel's saved name and sample volumes, driven by the selected display
    /// mode. Live device/channel data ports with the serial layer.
    /// </summary>
    private void RenderOledPreviews()
    {
        string mode = _settings.OledDisplayMode;
        OledPreviewModeText.Text = $"Preview mode: {DisplayModeName(mode)}";

        var images = new[]
        {
            OledPreview1Image, OledPreview2Image, OledPreview3Image,
            OledPreview4Image, OledPreview5Image, OledPreview6Image,
        };
        int[] sampleVolumes = { 60, 45, 80, 30, 50, 70 };

        for (int i = 0; i < images.Length; i++)
        {
            string label = i < _settings.Channels.Length && !string.IsNullOrWhiteSpace(_settings.Channels[i].FriendlyName)
                ? _settings.Channels[i].FriendlyName
                : $"Channel {i + 1}";
            int vol = sampleVolumes[i];

            var renderer = new OledRenderer();
            switch (mode)
            {
                case DisplayModes.LargeVolume:     renderer.RenderLargeVolume(label, vol, muted: false); break;
                case DisplayModes.MuteStatus:      renderer.RenderMuteStatus(label, vol, muted: false); break;
                case DisplayModes.AppOrDeviceName: renderer.RenderAppOrDeviceName(i + 1, label, "Active", vol); break;
                case DisplayModes.BarPercent:      renderer.RenderBarPercent(label, vol, muted: false); break;
                default:                           renderer.RenderAppVolume(label, vol, muted: false, "Active"); break;
            }

            images[i].Source = OledImage.Build(renderer);
        }
    }

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
