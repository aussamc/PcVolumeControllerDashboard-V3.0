using System;
using Avalonia.Threading;
using PcVolumeControllerDashboard.Core;
using PcVolumeControllerDashboard.Core.Audio;

namespace PcVolumeControllerDashboard.App.Services;

/// <summary>
/// The audio half of the runtime backbone: maps inbound controller events to
/// audio operations. An encoder turn adjusts the assigned channel's volume; a
/// short button press runs the channel's button action (toggle-mute for now).
///
/// Audio writes are marshalled to the UI thread to match the WPF host's WASAPI
/// COM affinity. Step sizing mirrors the host's sensitivity formula; encoder
/// acceleration/smoothing and the non-toggle button actions are later refinements.
/// </summary>
public sealed class ChannelRuntime : IDisposable
{
    private const int ExpectedChannelCount = 6;
    private const int BaseVolumeStepPercent = 2;
    private const int MaxVolumeStepPercent = 25;
    private const int MaxEncoderSensitivityPercent = 500;
    private static readonly int[] EncoderChannelRemap = { 0, 1, 2, 3, 4, 5 };

    private readonly SerialConnectionService _connection;
    private readonly IAudioBackend _audio;
    private readonly SettingsService _settings;
    private readonly LogService _log;

    public ChannelRuntime(SerialConnectionService connection, IAudioBackend audio, SettingsService settings, LogService log)
    {
        _connection = connection;
        _audio = audio;
        _settings = settings;
        _log = log;
        _connection.MessageReceived += OnDeviceMessage;
    }

    // MessageReceived fires on a background thread; marshal audio writes to the UI thread.
    private void OnDeviceMessage(DeviceMessage msg)
    {
        switch (msg.Kind)
        {
            case DeviceMessageKind.EncoderTurn:
                Dispatcher.UIThread.Post(() => HandleEncoder(msg.Channel, msg.Delta));
                break;
            case DeviceMessageKind.ButtonShort:
                Dispatcher.UIThread.Post(() => HandleButtonShort(msg.Channel));
                break;
        }
    }

    private void HandleEncoder(int firmwareChannel, int delta)
    {
        int direction = Math.Sign(delta);
        if (direction == 0) return;
        if (!TryResolveChannel(firmwareChannel, out int index, out ChannelSettings channel)) return;
        if (string.IsNullOrWhiteSpace(channel.TargetKey)) return;

        int sensitivity = channel.SensitivityPercent >= 0
            ? channel.SensitivityPercent
            : _settings.Settings.EncoderSensitivityPercent;
        int step = StepFromSensitivity(sensitivity);

        int result = _audio.AdjustVolumeByKey(channel.TargetKey, direction * step, channel.MinVolumePercent, channel.MaxVolumePercent);
        if (result >= 0 && _settings.Settings.AdvancedDebugLogging)
            _log.Log($"Ch{index + 1} {channel.TargetKey}: {result}%");
    }

    private void HandleButtonShort(int firmwareChannel)
    {
        if (!TryResolveChannel(firmwareChannel, out int index, out ChannelSettings channel)) return;
        if (string.IsNullOrWhiteSpace(channel.TargetKey)) return;

        if (channel.ButtonAction == ChannelButtonActions.ToggleAssignedMute)
        {
            bool? muted = _audio.ToggleMuteByKey(channel.TargetKey);
            if (muted != null)
                _log.Log($"Ch{index + 1} {channel.TargetKey}: {(muted.Value ? "muted" : "unmuted")}");
        }
        else
        {
            _log.Log($"Ch{index + 1} button action '{channel.ButtonAction}' not yet ported to Avalonia.");
        }
    }

    private bool TryResolveChannel(int firmwareChannel, out int index, out ChannelSettings channel)
    {
        index = -1;
        channel = null!;
        if (firmwareChannel < 0 || firmwareChannel >= ExpectedChannelCount) return false;

        index = firmwareChannel < EncoderChannelRemap.Length ? EncoderChannelRemap[firmwareChannel] : firmwareChannel;

        ChannelSettings[] channels = _settings.Settings.Channels;
        if (index < 0 || index >= channels.Length) return false;

        channel = channels[index];
        return true;
    }

    private static int StepFromSensitivity(int sensitivityPercent)
    {
        int s = Math.Clamp(sensitivityPercent, 0, MaxEncoderSensitivityPercent);
        if (s <= 0) return 1;
        int step = (int)Math.Round(BaseVolumeStepPercent * (s / 50.0));
        return Math.Clamp(step, 1, MaxVolumeStepPercent);
    }

    public void Dispose() => _connection.MessageReceived -= OnDeviceMessage;
}
