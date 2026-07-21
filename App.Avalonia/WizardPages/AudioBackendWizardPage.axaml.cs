using Avalonia.Controls;
using Avalonia.Interactivity;
using PcVolumeControllerDashboard.App.Audio;
using PcVolumeControllerDashboard.App.Services;
using PcVolumeControllerDashboard.Core;
using PcVolumeControllerDashboard.Core.Audio;

namespace PcVolumeControllerDashboard.App;

/// <summary>
/// Advanced-stream-only wizard page for choosing the audio backend
/// (WASAPI vs VoiceMeeter). Mirrors the dashboard Setup tab: persists
/// AudioBackendMode and switches the live SwitchableAudioBackend to match.
/// </summary>
public partial class AudioBackendWizardPage : UserControl, IWizardPage
{
    private readonly SettingsService? _settings;
    private readonly IAudioBackend? _audio;

    /// <summary>Guards the initial radio preselect from firing the change logic.</summary>
    private bool _initializing;

    public AudioBackendWizardPage()
    {
        InitializeComponent();
        ApplyPlatformLabels();
    }

    /// <summary>
    /// Names the radios after the backend this OS actually uses, and disables the
    /// VoiceMeeter option where its Windows-only API doesn't exist. Disabled rather
    /// than hidden so the page doesn't silently change shape across platforms —
    /// VoiceMeeterNoteText says why it's greyed out.
    /// </summary>
    private void ApplyPlatformLabels()
    {
        ChoiceDescriptionText.Text = AudioBackendLabels.ChoiceDescription;
        WasapiRadio.Content        = AudioBackendLabels.SystemBackend;
        VoiceMeeterRadio.Content   = "VoiceMeeter";
        VoiceMeeterNoteText.Text   = AudioBackendLabels.VoiceMeeterNote;

        VoiceMeeterRadio.IsEnabled = AudioBackendLabels.VoiceMeeterSupported;
    }

    public AudioBackendWizardPage(SettingsService settings, IAudioBackend audio) : this()
    {
        _settings = settings;
        _audio = audio;
        PreselectFromSettings();
    }

    public string Title => "Audio backend";

    public void OnShow() => UpdateStatus();

    public void OnLeave() { }

    /// <summary>Selects the radio matching the saved backend mode without triggering a save/switch.</summary>
    private void PreselectFromSettings()
    {
        if (_settings == null)
        {
            return;
        }

        _initializing = true;
        try
        {
            // A settings file written on Windows (or carried over by import) can name
            // VoiceMeeter on a machine that has no VoiceMeeter. The factory already
            // ignores the mode off-Windows, so the backend is correct either way —
            // this just stops the UI claiming a backend that isn't running.
            bool voiceMeeter = _settings.Settings.AudioBackendMode == AudioBackendModes.VoiceMeeter
                               && AudioBackendLabels.VoiceMeeterSupported;
            VoiceMeeterRadio.IsChecked = voiceMeeter;
            WasapiRadio.IsChecked = !voiceMeeter;
        }
        finally
        {
            _initializing = false;
        }

        UpdateStatus();
    }

    private void AudioBackendRadio_Changed(object? sender, RoutedEventArgs e)
    {
        if (_initializing || _settings == null)
        {
            return;
        }

        string mode = VoiceMeeterRadio.IsChecked == true
            ? AudioBackendModes.VoiceMeeter
            : AudioBackendModes.Wasapi;

        // Dedupe the paired check/uncheck events a radio group raises per toggle.
        if (mode == _settings.Settings.AudioBackendMode)
        {
            return;
        }

        _settings.Settings.AudioBackendMode = mode;
        _settings.Save();
        (_audio as SwitchableAudioBackend)?.SwitchTo(mode);
        UpdateStatus();
    }

    /// <summary>Refreshes the muted status line with the live backend name and target count.</summary>
    private void UpdateStatus()
    {
        StatusText.Text =
            $"Active: {_audio?.BackendName ?? "None"} — {_audio?.GetAvailableTargets().Count ?? 0} target(s).";
    }
}
