using System;
using System.Runtime.InteropServices;
using PcVolumeControllerDashboard.Core;
#if WINDOWS
using PcVolumeControllerDashboard.App.Platform;
#else
using PcVolumeControllerDashboard.Platform.Linux;
#endif

namespace PcVolumeControllerDashboard.App.Services;

/// <summary>
/// Host coordinator for desktop notifications (parity item F6): picks the per-OS
/// <see cref="INotificationService"/> impl, gates every notification on the user's
/// "tray notifications" setting, and mirrors the WPF host's <c>ShowTrayNotification</c>
/// call sites — controller connect / disconnect (from the connection state stream) and
/// "started minimised to tray" (from app startup). This is what finally makes the
/// long-inert <c>TrayNotificationsEnabled</c> checkbox do something on the Avalonia host.
///
/// The platform impl is chosen the same way as the audio backend and activity monitor:
/// a WinRT toast on the Windows TFM (<c>#if WINDOWS</c>), <c>notify-send</c> on Linux
/// (runtime OS check on the shared non-Windows TFM), and Core's
/// <see cref="NullNotificationService"/> on macOS (deferred).
///
/// Notifications are best-effort: <see cref="Notify"/> swallows and logs any failure so
/// a missing/blocked mechanism never disrupts the connection flow.
/// </summary>
public sealed class NotificationService : IDisposable
{
    private readonly INotificationService _notifier;
    private readonly SerialConnectionService _connection;
    private readonly SettingsService _settings;
    private readonly LogService _log;

    // Tracks the connected/not-connected edge so connect fires once on entering
    // Connected and disconnect fires once on leaving it — not on every Identifying↔
    // Disconnected bounce during a background reconnect scan. Touched only from
    // StateChanged (serialised by the connection).
    private bool _wasConnected;

    public NotificationService(SerialConnectionService connection, SettingsService settings, LogService log)
    {
        _connection = connection;
        _settings = settings;
        _log = log;
        _notifier = CreatePlatformNotifier();
        _connection.StateChanged += OnConnectionStateChanged;
    }

    private static INotificationService CreatePlatformNotifier()
    {
#if WINDOWS
        return new WindowsToastNotificationService();
#else
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return new LinuxNotificationService();
        return new NullNotificationService(); // macOS deferred
#endif
    }

    private void OnConnectionStateChanged(SerialConnectionState state)
    {
        if (state == SerialConnectionState.Connected && !_wasConnected)
        {
            _wasConnected = true;
            Notify("Controller connected",
                NotificationMessages.Connected(_connection.Protocol, _connection.ConnectedChipId));
        }
        else if (state != SerialConnectionState.Connected && _wasConnected)
        {
            _wasConnected = false;
            Notify("Controller disconnected", NotificationMessages.Disconnected());
        }
    }

    /// <summary>Fires the "started minimised to tray" notification (app-startup call site).</summary>
    public void NotifyStartedMinimized() =>
        Notify("PC Volume Controller", "Dashboard started minimised to tray.");

    /// <summary>Fires the "update available" notification (v3.19 auto-updater call site).</summary>
    public void NotifyUpdateAvailable(string version) =>
        Notify("Update available", NotificationMessages.UpdateAvailable(version));

    // Gate on the user setting, then show best-effort. The notifier impls (toast /
    // notify-send) are safe to call off the UI thread, so no marshalling is needed.
    private void Notify(string title, string message)
    {
        if (!_settings.Settings.TrayNotificationsEnabled) return;
        try { _notifier.Show(title, message); }
        catch (Exception ex) { _log.Log($"Notification failed: {ex.Message}"); }
    }

    public void Dispose() => _connection.StateChanged -= OnConnectionStateChanged;
}
