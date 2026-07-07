using System;
using System.Runtime.InteropServices;
using Avalonia.Threading;
using PcVolumeControllerDashboard.App.Audio;
using PcVolumeControllerDashboard.Core;
using PcVolumeControllerDashboard.Core.Audio;

namespace PcVolumeControllerDashboard.App.Services;

/// <summary>
/// A volume/mute change for a channel, for the on-screen volume overlay.
/// <paramref name="Muted"/> is the target's current mute state.
/// <paramref name="MuteToggle"/> is true only when this change originated from a
/// mute toggle (button action / master-mute hotkey), which drives the overlay's
/// dedicated mute layout (speaker glyph + Muted/Unmuted text, bar hidden). A plain
/// volume change while a target happens to be muted leaves it false so the normal
/// volume-bar view is shown, matching the WPF host.
/// </summary>
public readonly record struct VolumeOverlayInfo(
    int ChannelIndex, string Label, int VolumePercent, bool Muted, bool MuteToggle = false);

/// <summary>
/// The audio half of the runtime backbone: maps inbound controller events to
/// audio operations. Encoder turns adjust the assigned channel's volume — with
/// optional acceleration (faster turns take bigger steps) and EMA smoothing
/// (the volume eases toward its target over ~16 ms ticks) per the Encoder Feel
/// settings. Button presses (short/long/double) run the channel's configured
/// action.
///
/// Everything runs on the UI thread (events are marshalled there) to match the
/// WPF host's WASAPI COM affinity, which also means no locking is needed — all
/// per-channel state below is touched on the UI thread only. The feel math lives
/// in Core <see cref="EncoderMath"/>; this class supplies live settings and the
/// audio writes.
/// </summary>
public sealed class ChannelRuntime : IDisposable
{
    private const int ExpectedChannelCount = 6;
    private const int BaseVolumeStepPercent = 2;
    private const int MaxVolumeStepPercent = 25;
    private const int MaxEncoderSensitivityPercent = 500;
    private const int SmoothingTickMs = 16;            // ~60 Hz
    private const float SmoothingSnapThreshold = 0.002f; // snap when within 0.2 %
    private static readonly int[] EncoderChannelRemap = { 0, 1, 2, 3, 4, 5 };

    private readonly SerialConnectionService _connection;
    private readonly IAudioBackend _audio;
    private readonly SettingsService _settings;
    private readonly LogService _log;

    // Per-channel acceleration timing (Environment.TickCount64 of the last apply).
    private readonly long[] _accelPrevApplyAt = new long[ExpectedChannelCount];

    // Per-channel EMA smoothing state (normalised 0–1).
    private readonly bool[] _smoothingActive = new bool[ExpectedChannelCount];
    private readonly float[] _smoothingCurrent = new float[ExpectedChannelCount];
    private readonly float[] _smoothingTarget = new float[ExpectedChannelCount];
    // The resolved target key each channel is smoothing toward (the pool entry that
    // was live when smoothing started) — stable for the duration of the ramp.
    private readonly string[] _smoothingKey = new string[ExpectedChannelCount];
    private DispatcherTimer? _smoothingTimer;

    /// <summary>
    /// Raised on the UI thread when a channel's volume or mute changes from the
    /// controller (encoder turn, preset, mute toggle). Drives the on-screen volume
    /// overlay.
    /// </summary>
    public event Action<VolumeOverlayInfo>? VolumeChanged;

    public ChannelRuntime(SerialConnectionService connection, IAudioBackend audio, SettingsService settings, LogService log)
    {
        _connection = connection;
        _audio = audio;
        _settings = settings;
        _log = log;
        _connection.MessageReceived += OnDeviceMessage;
    }

    private void RaiseVolume(int index, ChannelSettings channel, int percent, bool muted, bool muteToggle = false)
    {
        string label = string.IsNullOrWhiteSpace(channel.FriendlyName) ? $"Channel {index + 1}" : channel.FriendlyName;
        try { VolumeChanged?.Invoke(new VolumeOverlayInfo(index, label, Math.Clamp(percent, 0, 100), muted, muteToggle)); }
        catch { /* overlay is best-effort */ }
    }

    // MessageReceived fires on a background thread; marshal everything to the UI thread.
    private void OnDeviceMessage(DeviceMessage msg)
    {
        switch (msg.Kind)
        {
            case DeviceMessageKind.EncoderTurn:
                Dispatcher.UIThread.Post(() => HandleEncoder(msg.Channel, msg.Delta));
                break;
            case DeviceMessageKind.ButtonShort:
                Dispatcher.UIThread.Post(() => HandleButton(msg.Channel, ButtonPress.Short));
                break;
            case DeviceMessageKind.ButtonLong:
                Dispatcher.UIThread.Post(() => HandleButton(msg.Channel, ButtonPress.Long));
                break;
            case DeviceMessageKind.ButtonDouble:
                Dispatcher.UIThread.Post(() => HandleButton(msg.Channel, ButtonPress.Double));
                break;
        }
    }

    // ── Encoder ───────────────────────────────────────────────────────────────

    private void HandleEncoder(int firmwareChannel, int delta)
    {
        if (delta == 0) return;
        if (!TryResolveChannel(firmwareChannel, out int index, out ChannelSettings channel)) return;

        string key = ChannelTargets.ResolveActiveKey(channel, _audio);
        if (string.IsNullOrWhiteSpace(key)) return;

        DashboardSettings s = _settings.Settings;

        // Interval since this channel's previous apply drives acceleration.
        long now = Environment.TickCount64;
        double intervalMs = _accelPrevApplyAt[index] == 0 ? double.MaxValue : now - _accelPrevApplyAt[index];
        _accelPrevApplyAt[index] = now;

        int baseStep = EncoderMath.StepFromSensitivity(
            EffectiveSensitivity(channel), BaseVolumeStepPercent, MaxVolumeStepPercent, MaxEncoderSensitivityPercent);

        int step = s.AccelerationEnabled
            ? EncoderMath.GetAcceleratedStep(baseStep, intervalMs, s.AccelerationPreset,
                s.AccelThresholdMs, s.AccelMaxMultiplier, s.AccelCurveExponent, MaxVolumeStepPercent)
            : baseStep;

        int deltaPercent = delta * step;

        // Apply to the turned channel (shows the overlay), then propagate the same
        // delta to any channels sharing its link group (ganged-pot behaviour) —
        // those don't pop their own overlay. Each channel resolves its own pool key.
        ApplyVolumeDelta(index, channel, key, deltaPercent, step, showOverlay: true);

        foreach (int linked in GetLinkedChannelIndices(index))
        {
            ChannelSettings lc = _settings.Settings.Channels[linked];
            string lkey = ChannelTargets.ResolveActiveKey(lc, _audio);
            if (string.IsNullOrWhiteSpace(lkey)) continue;
            ApplyVolumeDelta(linked, lc, lkey, deltaPercent, step, showOverlay: false);
        }
    }

    // Applies a (signed) percentage delta to one channel's resolved target key via
    // the smoothing path (when enabled and the key is available) or a direct write.
    private void ApplyVolumeDelta(int index, ChannelSettings channel, string key, int deltaPercent, int step, bool showOverlay)
    {
        DashboardSettings s = _settings.Settings;

        if (s.VolumeSmoothingEnabled && TryStartOrExtendSmoothing(index, channel, key, deltaPercent / 100f))
        {
            EnsureSmoothingTimer();
            if (showOverlay)
            {
                // The source channel's inline tick advances every active channel
                // (linked ones included) one step for immediate response; linked
                // channels otherwise start on the next timer tick.
                SmoothingTick();
                RaiseVolume(index, channel, (int)Math.Round(_smoothingTarget[index] * 100),
                    _audio.GetMuteByKey(key) ?? false);
            }
            return;
        }

        int result = _audio.AdjustVolumeByKey(key, deltaPercent, channel.MinVolumePercent, channel.MaxVolumePercent);
        if (result >= 0)
        {
            if (showOverlay)
                RaiseVolume(index, channel, result, _audio.GetMuteByKey(key) ?? false);
            if (s.AdvancedDebugLogging)
                _log.Log($"Ch{index + 1} {key}: {result}% (step {step}%)");
        }
    }

    /// <summary>
    /// Indices of the other channels sharing <paramref name="sourceIndex"/>'s
    /// non-empty link group (ganged volume). Empty group = not linked.
    /// </summary>
    private IEnumerable<int> GetLinkedChannelIndices(int sourceIndex)
    {
        ChannelSettings[] channels = _settings.Settings.Channels;
        if (sourceIndex < 0 || sourceIndex >= channels.Length) yield break;

        string group = channels[sourceIndex].LinkedGroupId;
        if (string.IsNullOrEmpty(group)) yield break;

        for (int i = 0; i < channels.Length; i++)
            if (i != sourceIndex && string.Equals(channels[i].LinkedGroupId, group, StringComparison.Ordinal))
                yield return i;
    }

    /// <summary>
    /// Sets/extends the smoothing target for a channel by <paramref name="deltaNorm"/>.
    /// Returns false (so the caller uses the direct path) when the channel's
    /// current volume can't be read to seed the interpolation.
    /// </summary>
    private bool TryStartOrExtendSmoothing(int index, ChannelSettings channel, string key, float deltaNorm)
    {
        (float lo, float hi) = LimitsNormalized(channel);

        if (_smoothingActive[index])
        {
            // Extend the in-flight target without re-reading the backend.
            _smoothingTarget[index] = Math.Clamp(_smoothingTarget[index] + deltaNorm, lo, hi);
            return true;
        }

        float current = _audio.GetVolumeByKey(key);
        if (current < 0f) return false; // unavailable → caller falls back to direct write

        _smoothingCurrent[index] = Math.Clamp(current, lo, hi);
        _smoothingTarget[index] = Math.Clamp(current + deltaNorm, lo, hi);
        _smoothingKey[index] = key;
        _smoothingActive[index] = true;
        return true;
    }

    private void EnsureSmoothingTimer()
    {
        if (_smoothingTimer != null) return;
        _smoothingTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(SmoothingTickMs) };
        _smoothingTimer.Tick += (_, _) => SmoothingTick();
        _smoothingTimer.Start();
    }

    private void StopSmoothingTimer()
    {
        _smoothingTimer?.Stop();
        _smoothingTimer = null;
    }

    // Advances every active channel one EMA step toward its target and writes the
    // absolute volume, working in normalised float space to avoid integer-percent
    // quantisation. Stops the timer once nothing is animating.
    private void SmoothingTick()
    {
        float alpha = EncoderMath.GetSmoothingAlpha(_settings.Settings.VolumeSmoothingSpeed);
        bool anyActive = false;

        for (int ch = 0; ch < ExpectedChannelCount; ch++)
        {
            if (!_smoothingActive[ch]) continue;

            float target = _smoothingTarget[ch];
            float diff = target - _smoothingCurrent[ch];
            float next;

            if (Math.Abs(diff) < SmoothingSnapThreshold)
            {
                next = target;
                _smoothingActive[ch] = false;
            }
            else
            {
                next = EncoderMath.EmaStep(_smoothingCurrent[ch], target, alpha);
                anyActive = true;
            }

            _smoothingCurrent[ch] = next;

            // Write to the key captured when this channel's ramp started (honours pools).
            if (!string.IsNullOrWhiteSpace(_smoothingKey[ch]))
                _audio.SetVolumeByKey(_smoothingKey[ch], next);
        }

        if (!anyActive) StopSmoothingTimer();
    }

    // ── Buttons ───────────────────────────────────────────────────────────────

    private enum ButtonPress { Short, Long, Double }

    private void HandleButton(int firmwareChannel, ButtonPress press)
    {
        if (!TryResolveChannel(firmwareChannel, out int index, out ChannelSettings channel)) return;

        string action = press switch
        {
            ButtonPress.Long => channel.LongPressButtonAction,
            ButtonPress.Double => channel.DoublePressButtonAction,
            _ => channel.ButtonAction,
        };

        ExecuteButtonAction(index, channel, action, press);
    }

    private void ExecuteButtonAction(int index, ChannelSettings channel, string action, ButtonPress press)
    {
        switch (action)
        {
            case ChannelButtonActions.ToggleAssignedMute:
            {
                string muteKey = ChannelTargets.ResolveActiveKey(channel, _audio);
                if (string.IsNullOrWhiteSpace(muteKey)) return;
                bool? muted = _audio.ToggleMuteByKey(muteKey);
                if (muted != null)
                {
                    _log.Log($"Ch{index + 1} {muteKey}: {(muted.Value ? "muted" : "unmuted")}");
                    float v = _audio.GetVolumeByKey(muteKey);
                    RaiseVolume(index, channel, v < 0f ? 0 : (int)Math.Round(v * 100), muted.Value, muteToggle: true);
                }
                break;
            }

            case ChannelButtonActions.ApplyPreset1: ApplyPreset(index, channel, 0); break;
            case ChannelButtonActions.ApplyPreset2: ApplyPreset(index, channel, 1); break;
            case ChannelButtonActions.ApplyPreset3: ApplyPreset(index, channel, 2); break;

            case ChannelButtonActions.MediaPlayPause: SendMediaKey(VkMediaPlayPause); break;
            case ChannelButtonActions.MediaNextTrack: SendMediaKey(VkMediaNextTrack); break;
            case ChannelButtonActions.MediaPrevTrack: SendMediaKey(VkMediaPrevTrack); break;
            case ChannelButtonActions.MediaStop:      SendMediaKey(VkMediaStop);      break;

            case ChannelButtonActions.NoAction:
                if (_settings.Settings.AdvancedDebugLogging)
                    _log.Log($"Ch{index + 1} {press.ToString().ToLowerInvariant()}-press: No action.");
                break;

            // Named profiles are descoped from the Avalonia port; a legacy settings
            // file might still carry this action, so handle it as a no-op.
            case ChannelButtonActions.CycleNextProfile:
                _log.Log($"Ch{index + 1}: 'Cycle profile' is not supported (named profiles are not part of this app).");
                break;

            // Need subsystems not yet ported to Avalonia (output-device cycling,
            // channel-selection UI). Logged so the behaviour is visible.
            case ChannelButtonActions.CycleOutputDevice:
            case ChannelButtonActions.SelectNextChannel:
                _log.Log($"Ch{index + 1} button action '{action}' not yet ported to Avalonia.");
                break;

            default:
                _log.Log($"Ch{index + 1} unknown button action '{action}'.");
                break;
        }
    }

    private void ApplyPreset(int index, ChannelSettings channel, int presetIndex)
    {
        string key = ChannelTargets.ResolveActiveKey(channel, _audio);
        if (string.IsNullOrWhiteSpace(key)) return;
        VolumePreset[] presets = channel.Presets;
        if (presets == null || presetIndex < 0 || presetIndex >= presets.Length) return;

        VolumePreset preset = presets[presetIndex];
        float target = Math.Clamp(preset.VolumePercent / 100f, 0f, 1f);

        if (_settings.Settings.VolumeSmoothingEnabled)
        {
            if (!_smoothingActive[index])
            {
                float current = _audio.GetVolumeByKey(key);
                _smoothingCurrent[index] = current >= 0f ? current : target;
            }
            _smoothingTarget[index] = target;
            _smoothingKey[index] = key;
            _smoothingActive[index] = true;
            EnsureSmoothingTimer();
            SmoothingTick();
        }
        else
        {
            _audio.SetVolumeByKey(key, target);
        }

        string presetName = string.IsNullOrWhiteSpace(preset.Name) ? $"Preset {presetIndex + 1}" : preset.Name;
        _log.Log($"Ch{index + 1} {key}: applied {presetName} ({preset.VolumePercent}%).");
        RaiseVolume(index, channel, preset.VolumePercent, _audio.GetMuteByKey(key) ?? false);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private int EffectiveSensitivity(ChannelSettings channel) =>
        channel.SensitivityPercent >= 0 ? channel.SensitivityPercent : _settings.Settings.EncoderSensitivityPercent;

    private static (float min, float max) LimitsNormalized(ChannelSettings channel) =>
        (channel.MinVolumePercent / 100f, channel.MaxVolumePercent / 100f);

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

    // ── Media keys (Windows; no-op elsewhere) ─────────────────────────────────

    private const byte VkMediaNextTrack = 0xB0;
    private const byte VkMediaPrevTrack = 0xB1;
    private const byte VkMediaStop      = 0xB2;
    private const byte VkMediaPlayPause = 0xB3;

#if WINDOWS
    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    private const uint KeyEventExtendedKey = 0x0001;
    private const uint KeyEventKeyUp = 0x0002;

    private static void SendMediaKey(byte vk)
    {
        keybd_event(vk, 0, KeyEventExtendedKey, UIntPtr.Zero);
        keybd_event(vk, 0, KeyEventExtendedKey | KeyEventKeyUp, UIntPtr.Zero);
    }
#else
    private static void SendMediaKey(byte vk) { /* media keys are Windows-only for now */ }
#endif

    public void Dispose()
    {
        _connection.MessageReceived -= OnDeviceMessage;
        StopSmoothingTimer();
    }
}
