using Avalonia.Controls;
using PcVolumeControllerDashboard.App.Services;

namespace PcVolumeControllerDashboard.App;

/// <summary>
/// Wizard page for the software-update preferences (v3.18). STUB — the real UI is filled
/// in by the v3.18 page agent. Do not change the ctor signature; the wizard host depends
/// on it. Binds the AutoCheckForUpdates / AutoApplyUpdates settings and offers a
/// "Check now" button via <see cref="UpdateCheckService"/>. Ships as check-only until the
/// v3.19 updater engine lands.
/// </summary>
public partial class UpdatePrefsWizardPage : UserControl, IWizardPage
{
    private readonly SettingsService? _settings;
    private readonly UpdateCheckService? _updater;
    private readonly string _currentVersion = AppInfo.Version;

    public UpdatePrefsWizardPage()
    {
        InitializeComponent();
    }

    public UpdatePrefsWizardPage(SettingsService settings, UpdateCheckService updater, string currentVersion) : this()
    {
        _settings = settings;
        _updater = updater;
        _currentVersion = currentVersion;
    }

    public string Title => "Software updates";

    public void OnShow() { }

    public void OnLeave() { }
}
