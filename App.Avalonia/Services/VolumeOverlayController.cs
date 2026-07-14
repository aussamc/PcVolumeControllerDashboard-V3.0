using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Platform;

namespace PcVolumeControllerDashboard.App.Services;

/// <summary>
/// Bridges <see cref="ChannelRuntime.VolumeChanged"/> to the on-screen
/// <see cref="VolumeOverlay"/> window(s), honouring the overlay
/// enable/position/timeout/opacity/scale settings. Windows are created lazily on the
/// first change and reused. With "show on all screens" (O4) on, one overlay is shown
/// per monitor; otherwise a single overlay is shown on the primary screen. Lives for
/// the app lifetime so the overlay works even while the main window is hidden to tray.
/// </summary>
public sealed class VolumeOverlayController : IDisposable
{
    private readonly ChannelRuntime _runtime;
    private readonly GlobalHotkeyManager _hotkeys;
    private readonly SettingsService _settings;
    // Pool of overlay windows. Index 0 is always the primary-screen overlay; extra
    // entries mirror onto other monitors when all-screens is enabled. Surplus windows
    // are hidden (not destroyed) so the pool can grow/shrink as monitors/settings change.
    private readonly List<VolumeOverlay> _overlays = new();

    public VolumeOverlayController(ChannelRuntime runtime, GlobalHotkeyManager hotkeys, SettingsService settings)
    {
        _runtime = runtime;
        _hotkeys = hotkeys;
        _settings = settings;
        // Both the controller (encoder/preset/mute) and the global master hotkeys
        // funnel through the same overlay so any volume change the app makes shows it.
        _runtime.VolumeChanged += OnVolumeChanged;
        _hotkeys.VolumeChanged += OnVolumeChanged;
    }

    // Raised on the UI thread by ChannelRuntime, so it's safe to touch the window here.
    private void OnVolumeChanged(VolumeOverlayInfo info)
    {
        if (!_settings.Settings.OverlayEnabled) return;
        Present(info);
    }

    /// <summary>
    /// Shows a sample overlay reflecting the current appearance settings, so the user
    /// can see position/opacity/scale/all-screens while adjusting them. Ignores the
    /// enable toggle (the preview is useful even before the overlay is turned on).
    /// </summary>
    public void ShowPreview()
        => Present(new VolumeOverlayInfo(-1, "Preview", 65, false, false));

    private void Present(VolumeOverlayInfo info)
    {
        var s = _settings.Settings;

        // Ensure the primary overlay exists and show it there first — this also
        // populates its Screens list, which we need to enumerate the other monitors.
        if (_overlays.Count == 0) _overlays.Add(new VolumeOverlay());
        _overlays[0].ShowVolume(info, s.OverlayPosition, s.OverlayTimeoutSeconds, s.OverlayOpacity, s.OverlayScale);

        int used = 1;
        if (s.OverlayAllScreens && _overlays[0].Screens is { } screens)
        {
            Screen? primary = screens.Primary ?? screens.All.FirstOrDefault();
            foreach (Screen sc in screens.All)
            {
                // Skip the primary — overlay[0] already covers it. Compare by bounds:
                // Screens.Primary can be a different instance than its match in All.
                if (primary is not null && sc.Bounds == primary.Bounds) continue;

                if (used >= _overlays.Count) _overlays.Add(new VolumeOverlay());
                _overlays[used].ShowVolume(info, s.OverlayPosition, s.OverlayTimeoutSeconds, s.OverlayOpacity, s.OverlayScale, sc);
                used++;
            }
        }

        // Retire any overlays beyond the ones used this time (all-screens turned off,
        // or a monitor was unplugged).
        for (int i = used; i < _overlays.Count; i++) _overlays[i].HideNow();
    }

    public void Dispose()
    {
        _runtime.VolumeChanged -= OnVolumeChanged;
        _hotkeys.VolumeChanged -= OnVolumeChanged;
    }
}
