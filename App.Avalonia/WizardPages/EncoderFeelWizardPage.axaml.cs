using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PcVolumeControllerDashboard.App.Services;
using PcVolumeControllerDashboard.Core;

namespace PcVolumeControllerDashboard.App;

/// <summary>
/// Advanced-stream wizard page for the Encoder Feel settings (sensitivity, acceleration,
/// smoothing) with a live "Try it" affordance (item 13). Scrolling the wheel over the
/// Try-it panel (or the −/+ buttons) emits synthetic detents that run through the same
/// Core <see cref="EncoderMath"/> the real encoder path uses, driving a sandboxed
/// <see cref="VolumeOverlay"/> demo — no audio backend write. Controls persist to settings
/// on change (mirroring the Setup tab), so the demo always reflects the current settings.
///
/// The wizard's Custom acceleration curve isn't exposed here (kept simple — Setup owns it);
/// picking a preset here just writes Light/Medium/Aggressive.
/// </summary>
public partial class EncoderFeelWizardPage : UserControl, IWizardPage
{
    private const int BaseVolumeStepPercent = 2;
    private const int MaxVolumeStepPercent = 25;
    private const int MaxSensitivityPercent = 500;
    private const int SmoothingTickMs = 16;

    private readonly SettingsService? _settings;

    // Guards the seed-from-settings pass so wiring the controls doesn't re-save.
    private bool _initializing;

    // Sandboxed demo state (0–100). _target is where the last detent landed; _current
    // eases toward it when smoothing is on.
    private double _demoTarget = 50;
    private double _demoCurrent = 50;
    private long _lastDetentTick;
    private DispatcherTimer? _smoothTimer;
    private VolumeOverlay? _demoOverlay;

    public EncoderFeelWizardPage()
    {
        InitializeComponent();
    }

    public EncoderFeelWizardPage(SettingsService settings) : this()
    {
        _settings = settings;
        SeedAndWire();

        // Close the demo overlay when the wizard window closes (covers the X-close path
        // that never fires OnLeave).
        AttachedToVisualTree += (_, _) =>
        {
            if (this.FindAncestorOfType<Window>() is { } window)
                window.Closed += (_, _) => { _smoothTimer?.Stop(); _demoOverlay?.Close(); };
        };
    }

    public string Title => "Encoder feel";

    public void OnShow()
    {
        // Reset the demo to a neutral midpoint each time the page is shown.
        _smoothTimer?.Stop();
        _demoTarget = _demoCurrent = 50;
        _lastDetentTick = 0;
        DemoValue.Text = "50%";
    }

    public void OnLeave()
    {
        _smoothTimer?.Stop();
        _demoOverlay?.HideNow();
    }

    // ── Seed + wire controls ──────────────────────────────────────────────────

    private void SeedAndWire()
    {
        if (_settings == null) return;
        DashboardSettings s = _settings.Settings;

        _initializing = true;

        SensSlider.Value = Math.Clamp(s.EncoderSensitivityPercent, 0, MaxSensitivityPercent);
        UpdateSensLabel();

        AccelCheck.IsChecked = s.AccelerationEnabled;
        AccelPresetCombo.SelectedIndex = PresetToIndex(s.AccelerationPreset);
        AccelPresetRow.IsEnabled = s.AccelerationEnabled;

        SmoothCheck.IsChecked = s.VolumeSmoothingEnabled;
        SmoothSpeedCombo.SelectedIndex = SpeedToIndex(s.VolumeSmoothingSpeed);
        SmoothSpeedRow.IsEnabled = s.VolumeSmoothingEnabled;

        // Subscribe after seeding. The slider observable fires once synchronously here,
        // but _initializing is still true so it's ignored.
        SensSlider.GetObservable(Slider.ValueProperty).Subscribe(new ValueObserver(_ => OnSensChanged()));
        AccelCheck.IsCheckedChanged += (_, _) => OnAccelToggled();
        AccelPresetCombo.SelectionChanged += (_, _) => OnAccelPresetChanged();
        SmoothCheck.IsCheckedChanged += (_, _) => OnSmoothToggled();
        SmoothSpeedCombo.SelectionChanged += (_, _) => OnSmoothSpeedChanged();

        TryItArea.PointerWheelChanged += OnTryItWheel;
        DemoDownButton.Click += (_, _) => Detent(-1);
        DemoUpButton.Click += (_, _) => Detent(+1);

        _initializing = false;
    }

    private void OnSensChanged()
    {
        UpdateSensLabel();
        if (_initializing || _settings == null) return;
        _settings.Settings.EncoderSensitivityPercent = (int)Math.Round(SensSlider.Value);
        _settings.Save();
    }

    private void OnAccelToggled()
    {
        if (_initializing || _settings == null) return;
        bool on = AccelCheck.IsChecked == true;
        _settings.Settings.AccelerationEnabled = on;
        AccelPresetRow.IsEnabled = on;
        _settings.Save();
    }

    private void OnAccelPresetChanged()
    {
        if (_initializing || _settings == null) return;
        _settings.Settings.AccelerationPreset = IndexToPreset(AccelPresetCombo.SelectedIndex);
        _settings.Save();
    }

    private void OnSmoothToggled()
    {
        if (_initializing || _settings == null) return;
        bool on = SmoothCheck.IsChecked == true;
        _settings.Settings.VolumeSmoothingEnabled = on;
        SmoothSpeedRow.IsEnabled = on;
        _settings.Save();
    }

    private void OnSmoothSpeedChanged()
    {
        if (_initializing || _settings == null) return;
        _settings.Settings.VolumeSmoothingSpeed = IndexToSpeed(SmoothSpeedCombo.SelectedIndex);
        _settings.Save();
    }

    private void UpdateSensLabel() => SensValue.Text = $"{(int)Math.Round(SensSlider.Value)}%";

    // ── Demo pipeline ─────────────────────────────────────────────────────────

    private void OnTryItWheel(object? sender, PointerWheelEventArgs e)
    {
        int dir = e.Delta.Y > 0 ? 1 : e.Delta.Y < 0 ? -1 : 0;
        if (dir != 0) Detent(dir);
        e.Handled = true; // don't scroll the wizard while feeling the encoder
    }

    /// <summary>One synthetic encoder detent through the real feel math (no audio write).</summary>
    private void Detent(int dir)
    {
        if (_settings == null || dir == 0) return;
        DashboardSettings s = _settings.Settings;

        long now = Environment.TickCount64;
        double intervalMs = _lastDetentTick == 0 ? 1000 : Math.Max(1, now - _lastDetentTick);
        _lastDetentTick = now;

        int step = EncoderMath.StepFromSensitivity(
            s.EncoderSensitivityPercent, BaseVolumeStepPercent, MaxVolumeStepPercent, MaxSensitivityPercent);

        if (s.AccelerationEnabled)
        {
            step = EncoderMath.GetAcceleratedStep(
                step, intervalMs, s.AccelerationPreset,
                s.AccelThresholdMs, s.AccelMaxMultiplier, s.AccelCurveExponent, MaxVolumeStepPercent);
        }

        _demoTarget = Math.Clamp(_demoTarget + dir * step, 0, 100);

        if (s.VolumeSmoothingEnabled)
        {
            _smoothTimer ??= CreateSmoothTimer();
            if (!_smoothTimer.IsEnabled) _smoothTimer.Start();
        }
        else
        {
            _smoothTimer?.Stop();
            _demoCurrent = _demoTarget;
        }

        ShowDemo();
    }

    private DispatcherTimer CreateSmoothTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(SmoothingTickMs) };
        timer.Tick += (_, _) =>
        {
            float alpha = EncoderMath.GetSmoothingAlpha(_settings?.Settings.VolumeSmoothingSpeed ?? SmoothingSpeed.Normal);
            _demoCurrent = EncoderMath.EmaStep((float)_demoCurrent, (float)_demoTarget, alpha);
            if (Math.Abs(_demoCurrent - _demoTarget) < 0.2)
            {
                _demoCurrent = _demoTarget;
                _smoothTimer?.Stop();
            }
            ShowDemo();
        };
        return timer;
    }

    private void ShowDemo()
    {
        if (_settings == null) return;
        int pct = (int)Math.Round(_demoCurrent);
        DemoValue.Text = $"{pct}%";

        DashboardSettings s = _settings.Settings;
        _demoOverlay ??= new VolumeOverlay();
        _demoOverlay.ShowVolume(
            new VolumeOverlayInfo(0, "Try it", pct, false, false),
            s.OverlayPosition, s.OverlayTimeoutSeconds, s.OverlayOpacity, s.OverlayScale);
    }

    // ── Preset / speed mapping (combo index ↔ setting string) ─────────────────

    private static int PresetToIndex(string preset) => preset switch
    {
        AccelerationPresets.Light => 0,
        AccelerationPresets.Aggressive => 2,
        _ => 1, // Medium (also the fallback for None/Custom, which the wizard doesn't expose)
    };

    private static string IndexToPreset(int index) => index switch
    {
        0 => AccelerationPresets.Light,
        2 => AccelerationPresets.Aggressive,
        _ => AccelerationPresets.Medium,
    };

    private static int SpeedToIndex(string speed) => speed switch
    {
        SmoothingSpeed.Slow => 0,
        SmoothingSpeed.Fast => 2,
        _ => 1, // Normal
    };

    private static string IndexToSpeed(int index) => index switch
    {
        0 => SmoothingSpeed.Slow,
        2 => SmoothingSpeed.Fast,
        _ => SmoothingSpeed.Normal,
    };

    /// <summary>Minimal IObserver&lt;double&gt; forwarding OnNext (mirrors MainWindow's slider wiring).</summary>
    private sealed class ValueObserver : IObserver<double>
    {
        private readonly Action<double> _onNext;
        public ValueObserver(Action<double> onNext) => _onNext = onNext;
        public void OnNext(double value) => _onNext(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
