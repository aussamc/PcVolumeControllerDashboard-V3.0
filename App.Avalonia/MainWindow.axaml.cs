using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Styling;
using PcVolumeControllerDashboard.App.Services;
using PcVolumeControllerDashboard.Core;

namespace PcVolumeControllerDashboard.App;

public partial class MainWindow : Window
{
    // Shipping dashboard version (bumped to 3.2 with this first ported tab).
    private const string DashboardVersion = "3.2";
    private const string RequiredProtocolVersion = "2.24";

    private readonly SettingsService? _settingsService;
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

    public MainWindow(SettingsService settingsService, SerialService serial) : this()
    {
        _settingsService = settingsService;
        _settings = settingsService.Settings;
        _ = serial; // wiring proven in PR 1; serial orchestration ports later

        WireSliders();
        ApplySettingsToUi();
        _initializing = false;
    }

    private void Save() => _settingsService?.Save();

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
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                Process.Start(new ProcessStartInfo("xdg-open", $"\"{path}\"") { UseShellExecute = false });
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                Process.Start(new ProcessStartInfo("open", $"\"{path}\"") { UseShellExecute = false });
        }
        catch { /* best-effort: ignore if no file manager is available */ }
    }

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
