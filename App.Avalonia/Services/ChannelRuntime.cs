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
/// debounce/coalescing + a reverse-direction guard ahead of that (see the
/// "Encoder debounce" region below), then optional acceleration (faster turns
/// take bigger steps) and EMA smoothing (the volume eases toward its target
/// over ~16 ms ticks) per the Encoder Feel settings. Button presses
/// (short/long/double) run the channel's configured action.
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

    // Encoder debounce/coalescing/reverse-guard tuning — mirrors WPF's
    // MainWindow.xaml.cs constants of the same name (MainWindow.Encoder.cs).
    private const int EncoderApplyIntervalMs = 25;     // min spacing between applied steps per channel
    private const int EncoderReverseGuardMs = 140;     // window an isolated direction reversal must repeat in
    private const int EncoderReverseConfirmEvents = 2; // raw events needed to confirm a reversal inside the guard
    private const int EncoderMaxCoalescedDelta = 5;    // clamp on the buffered (not-yet-applied) raw delta sum

    private readonly SerialConnectionService _connection;
    private readonly IAudioBackend _audio;
    private readonly SettingsService _settings;
    private readonly LogService _log;

    // --safe diagnostic launch: observe controller events but skip every audio-control
    // write, so a misbehaving setup can be inspected without the encoders/buttons
    // changing volumes. Mirrors the WPF host's _safeMode guards (MainWindow.Encoder.cs,
    // MainWindow.Serial.cs). Reads/state display are unaffected.
    private readonly bool _safeMode;

    // Per-channel acceleration timing (Environment.TickCount64 of the last apply).
    private readonly long[] _accelPrevApplyAt = new long[ExpectedChannelCount];

    // Per-channel encoder debounce/coalescing/reverse-guard state.
    private readonly int[] _encoderPendingDeltas = new int[ExpectedChannelCount];
    private readonly long[] _encoderLastAppliedAt = new long[ExpectedChannelCount];
    private readonly int[] _encoderLastDirection = new int[ExpectedChannelCount];
    private readonly int[] _encoderReverseCandidateDirection = new int[ExpectedChannelCount];
    private readonly int[] _encoderReverseCandidateCount = new int[ExpectedChannelCount];
    private readonly long[] _encoderReverseCandidateStartedAt = new long[ExpectedChannelCount];
    private readonly DispatcherTimer?[] _encoderCoalesceTimers = new DispatcherTimer?[ExpectedChannelCount];

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

    public ChannelRuntime(SerialConnectionService connection, IAudioBackend audio, SettingsService settings, LogService log, StartupOptions startup)
    {
        _connection = connection;
        _audio = audio;
        _settings = settings;
        _log = log;
        _safeMode = startup.SafeMode;
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
        if (_safeMode)
        {
            _log.Debug("Safe mode: encoder event observed but the audio write was skipped.", "Audio");
            return;
        }
        if (!TryResolveChannel(firmwareChannel, out int index, out _)) return;

        QueueEncoderDelta(index, delta);
    }

    // ── Encoder debounce / coalescing / reverse-guard ───────────────────────────
    //
    // A fast physical turn can fire many raw ENC deltas within a few ms, and a
    // bouncy encoder can report a spurious isolated direction reversal mid-turn.
    // Applying every raw delta immediately would mean, on Linux, one `wpctl
    // set-volume` process spawn per message (Platform.Linux/PipeWireAudioBackend.cs)
    // — a bouncy burst could spawn far more processes than intended. Mirrors WPF's
    // QueueSmoothedEncoderDelta (MainWindow.Encoder.cs): same-direction deltas are
    // coalesced and applied at most once every EncoderApplyIntervalMs; an isolated
    // reversal only takes effect if it repeats within EncoderReverseGuardMs
    // (EncoderReverseConfirmEvents raw events needed to confirm it), otherwise it's
    // dropped as encoder bounce.
    //
    // Unlike WPF (background Timer + lock, since raw ENC handling there isn't
    // UI-thread-marshalled up front), this runs entirely on the UI thread — ENC
    // messages are already posted to Dispatcher.UIThread in OnDeviceMessage — so a
    // plain DispatcherTimer is used per channel and no locking is needed.
    private void QueueEncoderDelta(int index, int rawDelta)
    {
        int direction = Math.Sign(rawDelta);
        if (direction == 0) return;

        long now = Environment.TickCount64;
        int lastDirection = _encoderLastDirection[index];

        if (lastDirection != 0 && direction != lastDirection)
        {
            long sinceLastApplied = now - _encoderLastAppliedAt[index];
            bool insideReverseGuard = sinceLastApplied < EncoderReverseGuardMs;

            if (insideReverseGuard)
            {
                if (_encoderReverseCandidateDirection[index] != direction ||
                    now - _encoderReverseCandidateStartedAt[index] > EncoderReverseGuardMs)
                {
                    _encoderReverseCandidateDirection[index] = direction;
                    _encoderReverseCandidateCount[index] = 1;
                    _encoderReverseCandidateStartedAt[index] = now;
                    return; // ignore isolated reverse step
                }

                _encoderReverseCandidateCount[index]++;
                if (_encoderReverseCandidateCount[index] < EncoderReverseConfirmEvents)
                    return; // still waiting for reverse confirmation
            }
        }
        else
        {
            _encoderReverseCandidateDirection[index] = 0;
            _encoderReverseCandidateCount[index] = 0;
        }

        _encoderPendingDeltas[index] = Math.Clamp(
            _encoderPendingDeltas[index] + rawDelta, -EncoderMaxCoalescedDelta, EncoderMaxCoalescedDelta);

        int delayMs = GetEncoderApplyDelayMs(index, now);
        if (delayMs <= 0)
        {
            int deltaToApply = TakePendingEncoderDelta(index, now);
            if (deltaToApply != 0) ApplyResolvedEncoderDelta(index, deltaToApply);
            return;
        }

        ScheduleEncoderCoalesceTimer(index, delayMs);
    }

    // Milliseconds remaining before this channel's next applied step is allowed;
    // 0 if it's never been applied yet or the interval has already elapsed.
    private int GetEncoderApplyDelayMs(int index, long now)
    {
        long lastApplied = _encoderLastAppliedAt[index];
        if (lastApplied == 0) return 0;

        long elapsedMs = now - lastApplied;
        return (int)Math.Max(0, EncoderApplyIntervalMs - elapsedMs);
    }

    // Clears and returns the buffered delta for a channel, stamping the apply time
    // and (when non-zero) the applied direction, and resetting the reverse-guard
    // candidate — mirrors WPF's TakePendingEncoderDeltaLocked.
    private int TakePendingEncoderDelta(int index, long now)
    {
        int delta = _encoderPendingDeltas[index];
        _encoderPendingDeltas[index] = 0;
        _encoderLastAppliedAt[index] = now;

        if (delta != 0)
            _encoderLastDirection[index] = Math.Sign(delta);

        _encoderReverseCandidateDirection[index] = 0;
        _encoderReverseCandidateCount[index] = 0;

        return delta;
    }

    // Schedules (replacing any prior pending timer for this channel) a one-shot
    // apply of whatever delta is buffered once EncoderApplyIntervalMs has elapsed
    // since the last apply — coalescing any further raw deltas that arrive first.
    private void ScheduleEncoderCoalesceTimer(int index, int delayMs)
    {
        _encoderCoalesceTimers[index]?.Stop();

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(delayMs) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _encoderCoalesceTimers[index] = null;

            int deltaToApply = TakePendingEncoderDelta(index, Environment.TickCount64);
            if (deltaToApply != 0) ApplyResolvedEncoderDelta(index, deltaToApply);
        };
        _encoderCoalesceTimers[index] = timer;
        timer.Start();
    }

    // Applies a debounced/coalesced delta: resolves the channel/key fresh (settings
    // may have changed while a delta sat in the buffer), then runs acceleration and
    // the audio write exactly as before — this is the body that used to live
    // directly in HandleEncoder prior to the debounce layer above.
    private void ApplyResolvedEncoderDelta(int index, int delta)
    {
        ChannelSettings[] channels = _settings.Settings.Channels;
        if (index < 0 || index >= channels.Length) return;
        ChannelSettings channel = channels[index];

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
        //
        // NOTE (R1, PARITY_FIX_BACKLOG.md): this ganging is UNCONDITIONAL — it does
        // NOT check Volume Smoothing like WPF's ApplySmoothedEncoderDelta does.
        // That's intentional: WPF only gangs inside its smoothing-enabled branch,
        // which silently breaks linked channels when smoothing is off — a
        // pre-existing WPF bug this port deliberately does not reproduce. Do not
        // make this conditional on VolumeSmoothingEnabled.
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
            _log.Debug($"Ch{index + 1} {key}: {result}% (step {step}%)", "Audio");
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
        if (_safeMode)
        {
            _log.Log($"Safe mode: {press.ToString().ToLowerInvariant()}-press observed but the audio action was skipped.");
            return;
        }
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
        // Debug logging: record every button-press -> action mapping (not just the
        // NoAction case), so a "my button does nothing / the wrong thing" report shows
        // what the firmware press actually dispatched to.
        _log.Debug($"Ch{index + 1} {press.ToString().ToLowerInvariant()}-press -> {action}", "Audio");

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
                // The entry-log above already records this under advanced logging.
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

        for (int i = 0; i < _encoderCoalesceTimers.Length; i++)
        {
            _encoderCoalesceTimers[i]?.Stop();
            _encoderCoalesceTimers[i] = null;
        }
    }
}
