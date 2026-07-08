using System;
using Avalonia.Threading;
using PcVolumeControllerDashboard.Core;
#if WINDOWS
using PcVolumeControllerDashboard.Platform.Windows;
#endif

namespace PcVolumeControllerDashboard.App.Services;

/// <summary>
/// Consumer of the <see cref="IPcActivityMonitor"/> seam: on PC lock/suspend/idle it
/// sends <c>SLEEP</c> to the controller and suppresses OLED state pushes; on resume it
/// sends <c>WAKE</c> and repaints the OLEDs from live state. The Avalonia counterpart
/// of the WPF host's SendControllerSleep / SendControllerWake (<c>MainWindow.Serial.cs</c>).
///
/// The per-OS monitor is chosen the same way as the audio backend / global hotkeys:
/// the real Windows implementation on the <c>-windows</c> TFM, a no-op
/// <see cref="NullPcActivityMonitor"/> elsewhere (Linux/Wayland has no cross-desktop
/// idle/lock API, so there's nothing to drive it — the controller uses its own
/// firmware idle timeout there).
/// </summary>
public sealed class SleepWakeService : IDisposable
{
    private readonly IPcActivityMonitor _monitor;
    private readonly SerialConnectionService _connection;
    private readonly DeviceStateService _deviceState;
    private readonly LogService _log;

    // Touched only on the UI thread (all handlers marshal there).
    private bool _asleep;

    public SleepWakeService(SerialConnectionService connection, DeviceStateService deviceState, LogService log)
    {
        _connection = connection;
        _deviceState = deviceState;
        _log = log;

        _monitor = CreatePlatformMonitor();
        _monitor.SleepRequested += OnSleepRequested;
        _monitor.WakeRequested += OnWakeRequested;
        _connection.StateChanged += OnConnectionStateChanged;
        _monitor.Start();
    }

    private static IPcActivityMonitor CreatePlatformMonitor()
    {
#if WINDOWS
        return new WindowsPcActivityMonitor();
#else
        return new NullPcActivityMonitor();
#endif
    }

    // Monitor events can arrive on an OS-event / timer thread — marshal to the UI
    // thread so the serial write and the _asleep flag stay on the same thread as the
    // channel-state poll (which also writes to the port).
    private void OnSleepRequested(string reason) => Dispatcher.UIThread.Post(() => ApplySleep(reason));

    private void OnWakeRequested(string reason) => Dispatcher.UIThread.Post(() => ApplyWake(reason));

    private void ApplySleep(string reason)
    {
        if (_connection.State != SerialConnectionState.Connected)
        {
            // Nothing to sleep; keep our bookkeeping consistent.
            _asleep = false;
            _deviceState.SetControllerAsleep(false);
            return;
        }

        if (_asleep) return; // reason de-dup is handled by the monitor
        _asleep = true;

        // Suppress OLED state pushes BEFORE sending SLEEP so no CHSTATE from the poll
        // races the firmware straight back to an awake screen.
        _deviceState.SetControllerAsleep(true);

        string safe = ProtocolMapping.MakeProtocolSafeLabel(reason);
        _connection.SendLine($"{ProtocolCommands.Sleep},{safe}", log: true);
        _log.Log($"Controller sleep requested: {reason}");
    }

    private void ApplyWake(string reason)
    {
        if (!_asleep) return;
        _asleep = false;

        if (_connection.State == SerialConnectionState.Connected)
        {
            string safe = ProtocolMapping.MakeProtocolSafeLabel(reason);
            _connection.SendLine($"{ProtocolCommands.Wake},{safe}", log: true);
            _log.Log($"Controller wake requested: {reason}");
        }

        // Re-enable pushes and force a full resend so the OLEDs repaint from live
        // state on the next poll tick (the firmware leaves them frozen while asleep).
        _deviceState.SetControllerAsleep(false);
    }

    // If the controller drops while we think it's asleep, forget the sleep state: a
    // reconnected (rebooted) controller is awake and needs a full state resend, which
    // clearing suppression here guarantees.
    private void OnConnectionStateChanged(SerialConnectionState state)
    {
        if (state == SerialConnectionState.Connected || !_asleep) return;
        _asleep = false;
        _deviceState.SetControllerAsleep(false);
    }

    public void Dispose()
    {
        _monitor.SleepRequested -= OnSleepRequested;
        _monitor.WakeRequested -= OnWakeRequested;
        _connection.StateChanged -= OnConnectionStateChanged;
        _monitor.Dispose();
    }
}
