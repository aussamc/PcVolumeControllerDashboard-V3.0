using System;
using System.Collections.Generic;
using Avalonia.Threading;
using PcVolumeControllerDashboard.Core;

namespace PcVolumeControllerDashboard.App.Services;

/// <summary>
/// One channel's live state as the host knows it, ready to be pushed to the
/// device. The host (Audio tab poll) computes these from the audio backend; this
/// service formats them into CHSTATE/STATE protocol lines.
/// </summary>
public readonly record struct ChannelLiveState(int Index, string Label, int Volume, bool Muted, string Status);

/// <summary>
/// Pushes PC → ESP32 state so the physical OLEDs and display reflect live audio:
/// per-channel <c>CHSTATE</c> (drives every OLED), <c>STATE</c> for the selected
/// channel, the global <c>OLEDCFG</c>, and per-channel <c>DISPMODE</c>. Mirrors
/// the WPF host's SendAllChannelStatesToDevice / SendStateToDevice /
/// SendOledSettingsToDevice / SendAllChannelOledModesToDevice.
///
/// Change detection (per-channel + the selected STATE) means the Audio tab poll
/// can call <see cref="PushChannelStates"/> every tick without flooding the
/// serial link — only genuinely changed lines are written. On (re)connect the
/// OLED config and per-channel modes are pushed immediately and the change
/// tracking is reset so the next poll re-sends all channel states fresh.
/// </summary>
public sealed class DeviceStateService : IDisposable
{
    private const int ExpectedChannelCount = 6;

    private readonly SerialConnectionService _connection;
    private readonly SettingsService _settings;
    private readonly LogService _log;

    private readonly string?[] _lastChState = new string?[ExpectedChannelCount];
    private string? _lastState;

    // While the controller is asleep (PC locked/suspended/idle — see SleepWakeService)
    // its OLEDs are blanked by the firmware; suppress STATE/CHSTATE pushes so the poll
    // doesn't wake the screen back up. Mirrors the WPF host's _controllerSleepRequested
    // guard on SendStateToDevice / SendAllChannelStatesToDevice. Toggled on the UI
    // thread (same thread as the push).
    private bool _asleep;

    public DeviceStateService(SerialConnectionService connection, SettingsService settings, LogService log)
    {
        _connection = connection;
        _settings = settings;
        _log = log;
        _connection.StateChanged += OnConnectionStateChanged;
    }

    private void OnConnectionStateChanged(SerialConnectionState state)
    {
        // Reset synchronously so any channel-state push that the host posts on
        // connect (and every push after a disconnect) starts from a clean slate
        // and re-sends all channels. Doing this before the posted config push
        // below keeps the ordering: reset → channel states → OLED config.
        ResetChangeTracking();

        if (state != SerialConnectionState.Connected) return;

        // Push the device configuration on connect. Channel states follow from the
        // Audio tab's poll (and an immediate refresh the host triggers). Keepalive
        // PINGs are owned by the connection monitor. Marshal to the UI thread —
        // StateChanged can fire on the serial read thread and the settings are read
        // on the UI thread.
        Dispatcher.UIThread.Post(() =>
        {
            PushOledConfig();
            PushAllChannelOledModes();
        });
    }

    /// <summary>
    /// Pushes per-channel <c>CHSTATE</c> for every supplied channel and a single
    /// <c>STATE</c> for the selected channel, writing only lines that changed
    /// since the last push.
    /// </summary>
    public void PushChannelStates(IReadOnlyList<ChannelLiveState> states, int selectedIndex)
    {
        if (_connection.State != SerialConnectionState.Connected) return;
        if (_asleep) return; // controller OLEDs are blanked while asleep — don't wake them

        ChannelLiveState? selected = null;

        foreach (ChannelLiveState s in states)
        {
            if (s.Index < 0 || s.Index >= ExpectedChannelCount) continue;
            if (s.Index == selectedIndex) selected = s;

            string label = ProtocolMapping.MakeProtocolSafeLabel(s.Label);
            string status = ProtocolMapping.MakeProtocolSafeLabel(s.Status);
            int muted = s.Muted ? 1 : 0;
            string msg = $"{ProtocolCommands.ChannelState},{s.Index},{label},{s.Volume},{muted},{status}";

            if (msg == _lastChState[s.Index]) continue;
            if (_connection.SendLine(msg))
                _lastChState[s.Index] = msg;
        }

        // STATE for the selected channel (the WPF host's "active channel" line).
        if (selected is { } sel)
        {
            string label = ProtocolMapping.MakeProtocolSafeLabel(sel.Label);
            int muted = sel.Muted ? 1 : 0;
            string msg = $"{ProtocolCommands.State},{sel.Index},{label},{sel.Volume},{muted}";
            if (msg != _lastState && _connection.SendLine(msg))
                _lastState = msg;
        }
    }

    /// <summary>Pushes the global OLED configuration (<c>OLEDCFG</c>).</summary>
    public void PushOledConfig()
    {
        if (_connection.State != SerialConnectionState.Connected) return;

        DashboardSettings s = _settings.Settings;
        string mode = ProtocolMapping.DisplayModeToProtocol(s.OledDisplayMode);
        string idle = ProtocolMapping.IdleActionToProtocol(s.OledConnectedIdleAction);
        int anti = s.OledAntiBurnInEnabled ? 1 : 0;
        string msg = $"{ProtocolCommands.OledConfig},{mode},{s.OledBrightnessPercent}," +
                     $"{s.OledSleepTimeoutMinutes},{idle},{s.OledConnectedIdleTimeoutMinutes},{anti}";
        _connection.SendLine(msg, log: true);
    }

    /// <summary>Pushes each channel's per-channel OLED display mode (<c>DISPMODE</c>).</summary>
    public void PushAllChannelOledModes()
    {
        if (_connection.State != SerialConnectionState.Connected) return;

        ChannelSettings[] channels = _settings.Settings.Channels;
        for (int i = 0; i < channels.Length && i < ExpectedChannelCount; i++)
        {
            string mode = ProtocolMapping.ChannelDisplayModeToProtocol(channels[i].OledDisplayMode);
            _connection.SendLine($"{ProtocolCommands.DisplayMode},{i},{mode}", log: true);
        }
    }

    /// <summary>
    /// Clears change tracking so the next channel-state push re-sends every channel.
    /// Used to redraw the OLEDs after a screen-hijacking command (SHOW_IDENT /
    /// TEST_DISPLAY), which the firmware leaves frozen until new state arrives.
    /// </summary>
    public void ForceResend() => ResetChangeTracking();

    /// <summary>
    /// Sets whether the controller is asleep. While asleep, <see cref="PushChannelStates"/>
    /// suppresses STATE/CHSTATE so the poll doesn't wake the blanked OLEDs. On wake
    /// (<paramref name="asleep"/> = <c>false</c>) change tracking is reset so the next
    /// push re-sends every channel and the OLEDs repaint from live state. Called from
    /// <see cref="SleepWakeService"/> on the UI thread.
    /// </summary>
    public void SetControllerAsleep(bool asleep)
    {
        if (_asleep == asleep) return;
        _asleep = asleep;
        if (!asleep) ResetChangeTracking();
    }

    private void ResetChangeTracking()
    {
        Array.Clear(_lastChState);
        _lastState = null;
    }

    public void Dispose() => _connection.StateChanged -= OnConnectionStateChanged;
}
