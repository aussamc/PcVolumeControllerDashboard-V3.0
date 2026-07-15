using Avalonia.Controls;
using PcVolumeControllerDashboard.App.Services;

namespace PcVolumeControllerDashboard.App;

/// <summary>
/// Wizard page mirroring the Setup tab's Application Setup checkboxes.
/// STUB — the real UI is filled in by the v3.18 page agent. Do not change the ctor
/// signatures; the wizard host depends on them.
///
/// <para><paramref name="condensed"/> = true renders only the two personal-preference
/// toggles (Start at login / Start minimized to tray) for the Quick stream; false renders
/// the full app-setup checkbox set plus the anti-burn-in disclaimer for Advanced.</para>
/// </summary>
public partial class AppSetupWizardPage : UserControl, IWizardPage
{
    private readonly SettingsService? _settings;
    private readonly bool _condensed;

    public AppSetupWizardPage()
    {
        InitializeComponent();
    }

    public AppSetupWizardPage(SettingsService settings, bool condensed) : this()
    {
        _settings = settings;
        _condensed = condensed;
    }

    public string Title => "Application setup";

    public void OnShow() { }

    public void OnLeave() { }
}
