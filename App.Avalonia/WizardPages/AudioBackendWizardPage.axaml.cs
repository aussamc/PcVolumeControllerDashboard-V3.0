using Avalonia.Controls;
using PcVolumeControllerDashboard.App.Services;
using PcVolumeControllerDashboard.Core.Audio;

namespace PcVolumeControllerDashboard.App;

/// <summary>
/// Advanced-stream-only wizard page for choosing the audio backend
/// (WASAPI vs VoiceMeeter). STUB — the real UI is filled in by the v3.18 page agent.
/// Do not change the ctor signature; the wizard host depends on it. Persists
/// AudioBackendMode and switches the live SwitchableAudioBackend to match.
/// </summary>
public partial class AudioBackendWizardPage : UserControl, IWizardPage
{
    private readonly SettingsService? _settings;
    private readonly IAudioBackend? _audio;

    public AudioBackendWizardPage()
    {
        InitializeComponent();
    }

    public AudioBackendWizardPage(SettingsService settings, IAudioBackend audio) : this()
    {
        _settings = settings;
        _audio = audio;
    }

    public string Title => "Audio backend";

    public void OnShow() { }

    public void OnLeave() { }
}
