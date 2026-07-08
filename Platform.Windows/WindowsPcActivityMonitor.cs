using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;
using PcVolumeControllerDashboard.Core;

namespace PcVolumeControllerDashboard.Platform.Windows;

/// <summary>
/// Windows <see cref="IPcActivityMonitor"/>: the Avalonia counterpart of the WPF
/// host's OnSessionSwitch / OnPowerModeChanged / UpdateControllerPowerStateFromPcActivity
/// (<c>MainWindow.Serial.cs</c>). Subscribes to <c>SystemEvents.SessionSwitch</c>
/// (screen lock/unlock) and <c>SystemEvents.PowerModeChanged</c> (suspend/resume),
/// and polls <c>GetLastInputInfo</c> on a timer for user idle. It combines those into
/// a single "should the controller sleep" verdict and raises
/// <see cref="SleepRequested"/> / <see cref="WakeRequested"/> only on a transition
/// (with reason de-duplication), so the consumer just forwards SLEEP/WAKE to serial.
/// </summary>
public sealed class WindowsPcActivityMonitor : IPcActivityMonitor
{
    // Idle threshold before sleeping the controller on inactivity. Mirrors the WPF
    // host's UserIdleSleepMs (MainWindow.xaml.cs) — 10 minutes.
    private const int UserIdleSleepMs = 10 * 60 * 1000;

    // How often to re-evaluate user-idle state (lock/suspend are event-driven and
    // evaluated immediately; only idle needs polling).
    private const int PollIntervalMs = 2000;

    public event Action<string>? SleepRequested;
    public event Action<string>? WakeRequested;

    private readonly object _gate = new();
    private Timer? _idleTimer;
    private bool _started;
    private bool _sessionLocked;
    private bool _systemSuspending;
    private string _currentSleepReason = string.Empty; // "" == awake

    public void Start()
    {
        lock (_gate)
        {
            if (_started) return;
            _started = true;
            SystemEvents.SessionSwitch += OnSessionSwitch;
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            _idleTimer = new Timer(_ => Evaluate(), null, PollIntervalMs, PollIntervalMs);
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (!_started) return;
            _started = false;
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            _idleTimer?.Dispose();
            _idleTimer = null;
        }
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionLock)
            lock (_gate) _sessionLocked = true;
        else if (e.Reason == SessionSwitchReason.SessionUnlock)
            lock (_gate) _sessionLocked = false;
        else
            return;
        Evaluate();
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend)
            lock (_gate) _systemSuspending = true;
        else if (e.Mode == PowerModes.Resume)
            lock (_gate) _systemSuspending = false;
        else
            return;
        Evaluate();
    }

    /// <summary>
    /// Recomputes the combined sleep verdict and raises a transition event if it
    /// changed. Mirrors WPF's UpdateControllerPowerStateFromPcActivity priority:
    /// suspend &gt; lock &gt; idle. Events are raised outside the lock so a consumer
    /// callback can't deadlock against the OS-event/timer threads.
    /// </summary>
    private void Evaluate()
    {
        string? raiseSleep = null;
        string? raiseWake = null;

        lock (_gate)
        {
            if (!_started) return;

            bool userIdle = GetUserIdleMilliseconds() >= UserIdleSleepMs;
            string reason = _systemSuspending ? "PC_SUSPEND"
                : _sessionLocked ? "PC_LOCKED"
                : userIdle ? "PC_IDLE"
                : string.Empty;

            if (reason.Length > 0)
            {
                if (!string.Equals(reason, _currentSleepReason, StringComparison.Ordinal))
                {
                    _currentSleepReason = reason;
                    raiseSleep = reason;
                }
            }
            else if (_currentSleepReason.Length > 0)
            {
                _currentSleepReason = string.Empty;
                raiseWake = "PC_ACTIVE";
            }
        }

        if (raiseSleep is not null)
        {
            try { SleepRequested?.Invoke(raiseSleep); } catch { /* best-effort */ }
        }
        else if (raiseWake is not null)
        {
            try { WakeRequested?.Invoke(raiseWake); } catch { /* best-effort */ }
        }
    }

    private static uint GetUserIdleMilliseconds()
    {
        LASTINPUTINFO info = new() { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info)) return 0;
        return unchecked((uint)Environment.TickCount - info.dwTime);
    }

    public void Dispose() => Stop();

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);
}
