using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using PcVolumeControllerDashboard.App.Services;

namespace PcVolumeControllerDashboard.App;

/// <summary>
/// Wizard page for the software-update preferences (v3.18). Binds the
/// AutoCheckForUpdates / AutoApplyUpdates settings and offers a "Check now" button via
/// <see cref="UpdateCheckService"/>. As of v3.19 both preferences are live: the checker
/// runs in the background and, with auto-apply on, updates pre-download for one-click
/// install from the dashboard's update banner (see UpdateOrchestrator / UpdateInstaller).
/// </summary>
public partial class UpdatePrefsWizardPage : UserControl, IWizardPage
{
    private readonly SettingsService? _settings;
    private readonly UpdateCheckService? _updater;
    private readonly string _currentVersion = AppInfo.Version;

    /// <summary>Guards checkbox handlers while preselecting from settings so they don't re-save.</summary>
    private bool _initializing;

    public UpdatePrefsWizardPage()
    {
        InitializeComponent();
    }

    public UpdatePrefsWizardPage(SettingsService settings, UpdateCheckService updater, string currentVersion) : this()
    {
        _settings = settings;
        _updater = updater;
        _currentVersion = currentVersion;

        // Preselect the checkboxes from the current settings, guarded so the change
        // handlers don't immediately persist the defaults back over loaded values.
        if (_settings != null)
        {
            _initializing = true;
            AutoCheckBox.IsChecked = _settings.Settings.AutoCheckForUpdates;
            AutoApplyBox.IsChecked = _settings.Settings.AutoApplyUpdates;
            _initializing = false;
        }
    }

    public string Title => "Software updates";

    private void OnAutoCheckChanged(object? sender, RoutedEventArgs e)
    {
        if (_initializing || _settings == null)
        {
            return;
        }

        _settings.Settings.AutoCheckForUpdates = AutoCheckBox.IsChecked ?? false;
        _settings.Save();
    }

    private void OnAutoApplyChanged(object? sender, RoutedEventArgs e)
    {
        if (_initializing || _settings == null)
        {
            return;
        }

        _settings.Settings.AutoApplyUpdates = AutoApplyBox.IsChecked ?? false;
        _settings.Save();
    }

    private async void OnCheckNowClicked(object? sender, RoutedEventArgs e)
    {
        if (_updater == null)
        {
            return;
        }

        CheckNowButton.IsEnabled = false;
        StatusText.Text = "Checking…";
        try
        {
            var result = await _updater.CheckAsync(_currentVersion);
            if (result.ErrorMessage != null)
            {
                StatusText.Text = $"Couldn't check: {result.ErrorMessage}";
            }
            else if (result.NoReleasesPublished)
            {
                StatusText.Text = "No releases published yet.";
            }
            else if (result.UpdateAvailable)
            {
                StatusText.Text = $"Update available: version {result.LatestVersion} (you have {_currentVersion}).";
            }
            else
            {
                StatusText.Text = $"You're up to date (version {_currentVersion}).";
            }
        }
        finally
        {
            CheckNowButton.IsEnabled = true;
        }
    }

    public void OnShow() { }

    public void OnLeave() { }
}
