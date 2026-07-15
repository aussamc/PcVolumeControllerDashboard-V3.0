using Avalonia.Controls;
using Avalonia.Interactivity;
using PcVolumeControllerDashboard.App.Services;
using PcVolumeControllerDashboard.Core;

namespace PcVolumeControllerDashboard.App;

/// <summary>
/// Wizard page exposing the Style Settings (Follow-system / Light / Dark). Mirrors the
/// dashboard's Style Settings: applies the chosen theme live (same variant mapping as
/// MainWindow.ApplyTheme) and persists <see cref="DashboardSettings.ThemeMode"/> on change.
/// </summary>
public partial class ThemeWizardPage : UserControl, IWizardPage
{
    private readonly SettingsService? _settings;

    /// <summary>Guards the initial preselect so it doesn't re-persist the saved value.</summary>
    private bool _initializing;

    public ThemeWizardPage()
    {
        InitializeComponent();
        WireRadios();
    }

    public ThemeWizardPage(SettingsService settings) : this()
    {
        _settings = settings;
        PreselectFromSettings();
    }

    public string Title => "Choose a theme";

    public void OnShow() { }

    public void OnLeave() { }

    /// <summary>Subscribes the three radios to a single change handler.</summary>
    private void WireRadios()
    {
        FollowSystemRadio.IsCheckedChanged += ThemeRadio_Changed;
        LightRadio.IsCheckedChanged += ThemeRadio_Changed;
        DarkRadio.IsCheckedChanged += ThemeRadio_Changed;
    }

    /// <summary>Checks the radio matching the saved <see cref="DashboardSettings.ThemeMode"/>.</summary>
    private void PreselectFromSettings()
    {
        if (_settings is null)
        {
            return;
        }

        _initializing = true;
        try
        {
            switch (_settings.Settings.ThemeMode)
            {
                case ThemeModes.Light:
                    LightRadio.IsChecked = true;
                    break;
                case ThemeModes.Dark:
                    DarkRadio.IsChecked = true;
                    break;
                default:
                    FollowSystemRadio.IsChecked = true;
                    break;
            }
        }
        finally
        {
            _initializing = false;
        }
    }

    /// <summary>Applies the newly-picked theme live and persists it.</summary>
    private void ThemeRadio_Changed(object? sender, RoutedEventArgs e)
    {
        if (_initializing || _settings is null)
        {
            return;
        }

        // Only react to the radio becoming checked (the outgoing radio also fires).
        if (sender is not RadioButton { IsChecked: true } radio)
        {
            return;
        }

        var mode = radio == LightRadio
            ? ThemeModes.Light
            : radio == DarkRadio
                ? ThemeModes.Dark
                : ThemeModes.FollowSystem;

        _settings.Settings.ThemeMode = mode;
        ApplyTheme(mode);
        _settings.Save();
    }

    /// <summary>Applies a theme mode to the running app (mirrors MainWindow.ApplyTheme).</summary>
    private static void ApplyTheme(string mode)
    {
        if (Avalonia.Application.Current is null)
        {
            return;
        }

        Avalonia.Application.Current.RequestedThemeVariant = mode switch
        {
            ThemeModes.Light => Avalonia.Styling.ThemeVariant.Light,
            ThemeModes.Dark => Avalonia.Styling.ThemeVariant.Dark,
            _ => Avalonia.Styling.ThemeVariant.Default, // follow system
        };
    }
}
