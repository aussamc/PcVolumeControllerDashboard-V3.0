using System;

namespace PcVolumeControllerDashboard.App.Services;

/// <summary>
/// Bridges <see cref="ChannelRuntime.VolumeChanged"/> to the on-screen
/// <see cref="VolumeOverlay"/> window, honouring the overlay enable/position/timeout
/// settings. The window is created lazily on the first change and reused. Lives for
/// the app lifetime so the overlay works even while the main window is hidden to tray.
/// </summary>
public sealed class VolumeOverlayController : IDisposable
{
    private readonly ChannelRuntime _runtime;
    private readonly SettingsService _settings;
    private VolumeOverlay? _overlay;

    public VolumeOverlayController(ChannelRuntime runtime, SettingsService settings)
    {
        _runtime = runtime;
        _settings = settings;
        _runtime.VolumeChanged += OnVolumeChanged;
    }

    // Raised on the UI thread by ChannelRuntime, so it's safe to touch the window here.
    private void OnVolumeChanged(VolumeOverlayInfo info)
    {
        var s = _settings.Settings;
        if (!s.OverlayEnabled) return;

        _overlay ??= new VolumeOverlay();
        _overlay.ShowVolume(info, s.OverlayPosition, s.OverlayTimeoutSeconds);
    }

    public void Dispose() => _runtime.VolumeChanged -= OnVolumeChanged;
}
