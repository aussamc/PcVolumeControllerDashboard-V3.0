using Avalonia.Controls;
using PcVolumeControllerDashboard.App.Services;

namespace PcVolumeControllerDashboard.App;

/// <summary>
/// Wizard page exposing the Style Settings (Follow-system / Light / Dark).
/// STUB — the real UI is filled in by the v3.18 page agent. Do not change the ctor
/// signature; the wizard host depends on it. Applies the theme live on change (reuse the
/// same variant mapping MainWindow.ApplyTheme uses) and persists ThemeMode.
/// </summary>
public partial class ThemeWizardPage : UserControl, IWizardPage
{
    private readonly SettingsService? _settings;

    public ThemeWizardPage()
    {
        InitializeComponent();
    }

    public ThemeWizardPage(SettingsService settings) : this()
    {
        _settings = settings;
    }

    public string Title => "Choose a theme";

    public void OnShow() { }

    public void OnLeave() { }
}
