using System;
using Avalonia.Controls;
using Avalonia.Layout;
using PcVolumeControllerDashboard.App.Services;

namespace PcVolumeControllerDashboard.App;

/// <summary>
/// Wizard page mirroring the Setup tab's Application Setup checkboxes.
///
/// <para><paramref name="condensed"/> = true renders only the two personal-preference
/// toggles (Start at login / Start minimized to tray) for the Quick stream; false renders
/// the full app-setup checkbox set plus the anti-burn-in disclaimer for Advanced.</para>
///
/// <para>This is a save-on-change page: each toggle writes straight into
/// <see cref="SettingsService.Settings"/> and calls <see cref="SettingsService.Save"/>
/// (mirroring <c>MainWindow.AppSetupCheckBox_Changed</c>), so <see cref="OnShow"/> /
/// <see cref="OnLeave"/> have nothing to do. Changing "Start at login" also re-applies the
/// HKCU Run entry via <see cref="Platform.WindowsGlue.ApplyRunOnStartup"/> (no-op off
/// Windows).</para>
/// </summary>
public partial class AppSetupWizardPage : UserControl, IWizardPage
{
    private readonly SettingsService? _settings;
    private readonly bool _condensed;

    // Guards the change handlers while we seed each CheckBox from settings, so building the
    // UI doesn't immediately re-save the values we just loaded.
    private bool _initializing;

    public AppSetupWizardPage()
    {
        InitializeComponent();
    }

    public AppSetupWizardPage(SettingsService settings, bool condensed) : this()
    {
        _settings = settings;
        _condensed = condensed;
        BuildUi();
    }

    public string Title => "Application setup";

    public void OnShow() { }

    public void OnLeave() { }

    private void BuildUi()
    {
        if (_settings == null) return; // designer / parameterless path

        _initializing = true;

        Root.Children.Add(new TextBlock
        {
            Classes = { "muted" },
            Margin = new Avalonia.Thickness(0, 0, 0, 4),
            Text = _condensed
                ? "A couple of preferences — you can change these any time in Setup."
                : "Choose how the app behaves. You can change any of these later in Setup.",
        });

        if (_condensed)
        {
            // Quick stream — just the two personal-preference toggles.
            AddCheckBox("Start at login",
                () => _settings.Settings.StartWithWindows,
                v => _settings.Settings.StartWithWindows = v);
            AddCheckBox("Start minimized to tray",
                () => _settings.Settings.StartMinimizedToTray,
                v => _settings.Settings.StartMinimizedToTray = v);
        }
        else
        {
            // Advanced stream — full app-setup set (Advanced debug logging lives on the
            // Debug tab now, so it is intentionally excluded here).
            AddCheckBox("Auto-connect on launch",
                () => _settings.Settings.AutoConnectOnLaunch,
                v => _settings.Settings.AutoConnectOnLaunch = v);
            AddCheckBox("Scan all COM ports if the remembered controller is missing",
                () => _settings.Settings.ScanAllComPortsIfRememberedMissing,
                v => _settings.Settings.ScanAllComPortsIfRememberedMissing = v);
            AddCheckBox("Minimize to tray",
                () => _settings.Settings.MinimizeToTray,
                v => _settings.Settings.MinimizeToTray = v);
            AddCheckBox("Start minimized to tray",
                () => _settings.Settings.StartMinimizedToTray,
                v => _settings.Settings.StartMinimizedToTray = v);
            AddCheckBox("Start at login",
                () => _settings.Settings.StartWithWindows,
                v => _settings.Settings.StartWithWindows = v);
            AddCheckBox("Show tray notifications",
                () => _settings.Settings.TrayNotificationsEnabled,
                v => _settings.Settings.TrayNotificationsEnabled = v);

            // Note: the anti-burn-in pixel-shift toggle lives on the wizard's
            // "Check the displays" page (it's a display control, and that page shows in
            // both streams), not here.
        }

        _initializing = false;
    }

    /// <summary>
    /// Adds a labelled CheckBox to <c>Root</c>, seeds it from <paramref name="get"/> before
    /// wiring the change handler, and persists via <paramref name="set"/> + Save on change.
    /// </summary>
    private void AddCheckBox(string label, Func<bool> get, Action<bool> set, double topMargin = 0)
    {
        var box = new CheckBox
        {
            Content = label,
            IsChecked = get(),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Avalonia.Thickness(0, topMargin, 0, 0),
        };
        box.IsCheckedChanged += (_, _) =>
        {
            if (_initializing || _settings == null) return;
            set(box.IsChecked == true);
            _settings.Save();
            // Idempotent; re-applying the run-on-login registry entry for any app-setup
            // change matches MainWindow and keeps the "Start at login" toggle honest.
            Platform.WindowsGlue.ApplyRunOnStartup(_settings.Settings.StartWithWindows, userInitiated: true);
        };
        Root.Children.Add(box);
    }
}
