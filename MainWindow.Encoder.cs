// MainWindow.Encoder.cs — Encoder input processing: debounce, coalescing, reverse-guard,
// acceleration, smoothing, and per-channel sensitivity calculation.
// Extracted from MainWindow.xaml.cs in v2.41. All fields remain in MainWindow.xaml.cs.

using System;
using System.Threading;
using System.Windows.Controls;
using NAudio.CoreAudioApi;
using ThreadingTimer = System.Threading.Timer;
using WpfBrush = System.Windows.Media.Brush;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfDispatcherTimer = System.Windows.Threading.DispatcherTimer;

namespace PcVolumeControllerDashboard;

public partial class MainWindow
{
    // ── Entry point — called by the serial message dispatcher ───────────────────

    private void HandleEncoderMessage(string[] parts)
    {
        if (_channels.Count == 0) return;
        RegisterHardwareEncoderEvent(parts);

        if (_controllerSleepRequested)
        {
            if (IsAdvancedDebugLoggingEnabled())
                Log("Ignored encoder event while controller sleep is active.");
            return;
        }

        if (parts.Length < 3)
            return;

        if (!int.TryParse(parts[1], out int firmwareChannel) || firmwareChannel < 0 || firmwareChannel >= ExpectedChannelCount)
            return;

        int espChannel = RemapEncoderChannel(firmwareChannel);

        if (!int.TryParse(parts[2], out int delta) || delta == 0)
            return;

        if (_safeMode)
        {
            if (IsAdvancedDebugLoggingEnabled())
                Log("Safe mode: encoder event observed but audio-control write was skipped.");
            return;
        }

        QueueSmoothedEncoderDelta(espChannel, delta);
    }

    // Translates a firmware encoder/button channel index to the corresponding
    // dashboard channel index using the EncoderChannelRemap table.
    private static int RemapEncoderChannel(int firmwareChannel)
    {
        if (firmwareChannel >= 0 && firmwareChannel < EncoderChannelRemap.Length)
            return EncoderChannelRemap[firmwareChannel];
        return firmwareChannel;
    }

    // ── Debounce, coalescing, reverse-guard ──────────────────────────────────────

    private void QueueSmoothedEncoderDelta(int encoderChannel, int rawDelta)
    {
        int direction = Math.Sign(rawDelta);
        if (direction == 0)
            return;

        DateTime now = DateTime.Now;

        // --- DIAGNOSTIC: bypass all debounce, coalescing, and reverse-guard.
        // EncoderDebounceDisabled is a compile-time constant. One branch of this if/else is
        // always dead code — the pragma suppresses CS0162 for whichever branch that is.
        // Normal operation: EncoderDebounceDisabled=false → diagnostic body is dead code.
        // Hardware analysis: flip to true → normal debounce body becomes dead code.
#pragma warning disable CS0162 // Intentional — one branch is always dead (controlled by const)
        if (EncoderDebounceDisabled)
        {
            double rawIntervalMs;
            bool isReverse;
            lock (_encoderSmoothingLock)
            {
                rawIntervalMs = _encoderLastRawEventAt[encoderChannel] == default
                    ? -1
                    : (now - _encoderLastRawEventAt[encoderChannel]).TotalMilliseconds;
                _encoderLastRawEventAt[encoderChannel] = now;
                isReverse = _encoderLastDirection[encoderChannel] != 0
                         && direction != _encoderLastDirection[encoderChannel];
                _encoderLastDirection[encoderChannel] = direction;
            }
            string rawIntervalStr = rawIntervalMs < 0 ? "first" : $"{rawIntervalMs:F1}ms";
            Log($"[RAW] Encoder {encoderChannel + 1}: delta={rawDelta} interval={rawIntervalStr}{(isReverse ? " DIRECTION-CHANGE" : "")}");
            BeginApplySmoothedEncoderDelta(encoderChannel, rawDelta);
            return;
        }
#pragma warning restore CS0162
        // --- end diagnostic ---

        lock (_encoderSmoothingLock)
        {
            int lastDirection = _encoderLastDirection[encoderChannel];

            if (lastDirection != 0 && direction != lastDirection)
            {
                TimeSpan sinceLastApplied = now - _encoderLastAppliedAt[encoderChannel];
                bool insideReverseGuard = sinceLastApplied.TotalMilliseconds < EncoderReverseGuardMs;

                if (insideReverseGuard)
                {
                    if (_encoderReverseCandidateDirection[encoderChannel] != direction ||
                        (now - _encoderReverseCandidateStartedAt[encoderChannel]).TotalMilliseconds > EncoderReverseGuardMs)
                    {
                        _encoderReverseCandidateDirection[encoderChannel] = direction;
                        _encoderReverseCandidateCount[encoderChannel] = 1;
                        _encoderReverseCandidateStartedAt[encoderChannel] = now;

                        if (IsAdvancedDebugLoggingEnabled())
                            Log($"Encoder {encoderChannel + 1}: ignored isolated reverse step {rawDelta}.");

                        return;
                    }

                    _encoderReverseCandidateCount[encoderChannel]++;

                    if (_encoderReverseCandidateCount[encoderChannel] < EncoderReverseConfirmEvents)
                    {
                        if (IsAdvancedDebugLoggingEnabled())
                            Log($"Encoder {encoderChannel + 1}: waiting for reverse confirmation ({_encoderReverseCandidateCount[encoderChannel]}/{EncoderReverseConfirmEvents}).");

                        return;
                    }
                }
            }
            else
            {
                _encoderReverseCandidateDirection[encoderChannel] = 0;
                _encoderReverseCandidateCount[encoderChannel] = 0;
            }

            _encoderPendingDeltas[encoderChannel] = Math.Clamp(
                _encoderPendingDeltas[encoderChannel] + rawDelta,
                -EncoderMaxCoalescedDelta,
                EncoderMaxCoalescedDelta);

            int delayMs = GetEncoderApplyDelayMsLocked(encoderChannel, now);

            if (delayMs <= 0)
            {
                int deltaToApply = TakePendingEncoderDeltaLocked(encoderChannel, now, direction);
                BeginApplySmoothedEncoderDelta(encoderChannel, deltaToApply);
                return;
            }

            ScheduleEncoderCoalesceTimerLocked(encoderChannel, delayMs);
        }
    }

    private int GetEncoderApplyDelayMsLocked(int encoderChannel, DateTime now)
    {
        DateTime lastApplied = _encoderLastAppliedAt[encoderChannel];

        if (lastApplied == default)
            return 0;

        int elapsedMs = (int)(now - lastApplied).TotalMilliseconds;
        return Math.Max(0, EncoderApplyIntervalMs - elapsedMs);
    }

    private int TakePendingEncoderDeltaLocked(int encoderChannel, DateTime now, int directionHint)
    {
        int delta = _encoderPendingDeltas[encoderChannel];
        _encoderPendingDeltas[encoderChannel] = 0;
        _encoderLastAppliedAt[encoderChannel] = now;

        if (delta != 0)
            _encoderLastDirection[encoderChannel] = Math.Sign(delta);
        else if (directionHint != 0)
            _encoderLastDirection[encoderChannel] = directionHint;

        _encoderReverseCandidateDirection[encoderChannel] = 0;
        _encoderReverseCandidateCount[encoderChannel] = 0;

        return delta;
    }

    private void ScheduleEncoderCoalesceTimerLocked(int encoderChannel, int delayMs)
    {
        _encoderCoalesceTimers[encoderChannel]?.Dispose();
        _encoderCoalesceTimers[encoderChannel] = new ThreadingTimer(_ =>
        {
            int deltaToApply;

            lock (_encoderSmoothingLock)
            {
                deltaToApply = TakePendingEncoderDeltaLocked(encoderChannel, DateTime.Now, 0);
                _encoderCoalesceTimers[encoderChannel]?.Dispose();
                _encoderCoalesceTimers[encoderChannel] = null;
            }

            if (deltaToApply != 0)
            {
                try
                {
                    Dispatcher.InvokeAsync(() => ApplySmoothedEncoderDelta(encoderChannel, deltaToApply));
                }
                catch
                {
                }
            }
        }, null, delayMs, Timeout.Infinite);
    }

    // ── Apply delta → acceleration → volume write ────────────────────────────────

    private void BeginApplySmoothedEncoderDelta(int encoderChannel, int deltaToApply)
    {
        if (deltaToApply == 0)
            return;

        ApplySmoothedEncoderDelta(encoderChannel, deltaToApply);
    }

    private void ApplySmoothedEncoderDelta(int encoderChannel, int smoothedDelta)
    {
        if (_controllerSleepRequested || _safeMode || smoothedDelta == 0)
            return;

        // Measure time since the previous apply for this channel (used by acceleration).
        DateTime now = DateTime.Now;
        bool isFirstEvent = _accelPrevApplyAt[encoderChannel] == default;
        double intervalMs = isFirstEvent
            ? double.MaxValue
            : (now - _accelPrevApplyAt[encoderChannel]).TotalMilliseconds;
        _accelPrevApplyAt[encoderChannel] = now;

        int baseStep = GetVolumeStepPercentForChannel(encoderChannel);
        int step = _settings.AccelerationEnabled
            ? GetAcceleratedStep(baseStep, intervalMs)
            : baseStep;

        int deltaPercent = smoothedDelta * step;

        if (IsAdvancedDebugLoggingEnabled())
        {
            string intervalStr = isFirstEvent ? "first event" : $"{intervalMs:F0}ms";
            Log($"Encoder {encoderChannel + 1}: delta {smoothedDelta}, step {step}%, total {deltaPercent}% (interval {intervalStr}).");
        }

        // Determine whether to use the smoothing path.
        // If smoothing is enabled but the channel is currently unavailable (volume read
        // returns -1), fall back to the direct-write path so the encoder is never a no-op.
        bool useSmoothingPath = false;

        if (_settings.VolumeSmoothingEnabled)
        {
            float deltaNorm = deltaPercent / 100f;
            (float limMin, float limMax) = GetChannelVolumeLimitsNormalized(encoderChannel);

            if (_smoothingActive[encoderChannel])
            {
                // Extend the in-flight target without re-reading from WASAPI.
                _smoothingTargetVolumes[encoderChannel] = Math.Clamp(
                    _smoothingTargetVolumes[encoderChannel] + deltaNorm, limMin, limMax);
                useSmoothingPath = true;
            }
            else
            {
                float current = GetChannelCurrentVolumeNormalized(encoderChannel);
                if (current >= 0f)
                {
                    // Channel is available — start smooth interpolation from actual volume.
                    _smoothingCurrentVolumes[encoderChannel] = Math.Clamp(current, limMin, limMax);
                    _smoothingTargetVolumes[encoderChannel] = Math.Clamp(current + deltaNorm, limMin, limMax);
                    useSmoothingPath = true;
                }
                // current < 0 → channel unassigned or unavailable; fall through to direct path.
            }

            if (useSmoothingPath)
            {
                _smoothingActive[encoderChannel] = true;
                EnsureSmoothingTimerRunning();

                int overlayVol = (int)Math.Round(_smoothingTargetVolumes[encoderChannel] * 100);
                ShowVolumeOverlay(encoderChannel, overlayVol);

                // Run the first tick inline for immediate audio response.
                SmoothingTick();
                return;
            }
        }

        // Direct (non-smoothed) path — also used as fallback when the channel is unavailable.
        ChangeChannelVolumeWithComHandling(encoderChannel, deltaPercent);
        RefreshAllChannelStates();
        SendAllChannelStatesToDevice();
        SendStateToDevice(force: true);

        {
            float directVol = GetChannelCurrentVolumeNormalized(encoderChannel);
            if (directVol >= 0f)
                ShowVolumeOverlay(encoderChannel, (int)Math.Round(directVol * 100));
        }
    }

    // ── Acceleration ─────────────────────────────────────────────────────────────

    // Returns a volume step scaled up when the encoder is turned quickly.
    private int GetAcceleratedStep(int baseStep, double intervalMs)
    {
        // Custom preset: continuous formula using three tunable parameters.
        //   speedFactor = how close to the threshold (0 = at/above threshold, 1 = instantaneous)
        //   curved      = speedFactor shaped by the curve exponent
        //   multiplier  = 1× (slow) → AccelMaxMultiplier× (fast)
        if (_settings.AccelerationPreset == AccelerationPresets.Custom)
        {
            float threshold  = Math.Max(1f, _settings.AccelThresholdMs);
            float sf         = (float)Math.Clamp((threshold - intervalMs) / threshold, 0.0, 1.0);
            float curved     = MathF.Pow(sf, Math.Max(0.1f, _settings.AccelCurveExponent));
            float multiplier = 1.0f + (_settings.AccelMaxMultiplier - 1.0f) * curved;
            return Math.Clamp((int)Math.Round(baseStep * multiplier), 1, MaxVolumeStepPercent);
        }

        // Fixed presets: step-function multipliers for Light / Medium / Aggressive.
        int intMultiplier = _settings.AccelerationPreset switch
        {
            AccelerationPresets.Light      => intervalMs < 80  ? 2 : 1,
            AccelerationPresets.Medium     => intervalMs < 60  ? 3 : intervalMs < 100 ? 2 : 1,
            AccelerationPresets.Aggressive => intervalMs < 50  ? 4 : intervalMs < 70  ? 3 : intervalMs < 110 ? 2 : 1,
            _                              => 1,
        };
        return Math.Clamp(baseStep * intMultiplier, 1, MaxVolumeStepPercent);
    }

    // Computes the custom-preset multiplier at a given interval for display in the preview.
    private float ComputeCustomAccelMultiplier(double intervalMs, int thresholdMs, float maxMult, float curveExp)
    {
        float threshold = Math.Max(1f, thresholdMs);
        float sf        = (float)Math.Clamp((threshold - intervalMs) / threshold, 0.0, 1.0);
        float curved    = MathF.Pow(sf, Math.Max(0.1f, curveExp));
        return 1.0f + (maxMult - 1.0f) * curved;
    }

    // ── Smoothing (EMA) ───────────────────────────────────────────────────────────

    // EMA alpha per tick.  Formula: after N ticks, remaining error = (1-alpha)^N.
    //   Fast   (alpha 0.50): ~97 % converged in  5 ticks × 16 ms = ~80 ms
    //   Normal (alpha 0.35): ~97 % converged in  8 ticks × 16 ms = ~128 ms
    //   Slow   (alpha 0.22): ~97 % converged in 13 ticks × 16 ms = ~208 ms
    private float GetSmoothingAlpha()
    {
        return _settings.VolumeSmoothingSpeed switch
        {
            SmoothingSpeed.Fast => 0.50f,
            SmoothingSpeed.Slow => 0.22f,
            _                   => 0.35f,  // Normal
        };
    }

    // Returns the normalized volume (0.0–1.0) read directly from the active audio backend.
    // Returns -1 if the channel is unassigned or its session is unavailable.
    private float GetChannelCurrentVolumeNormalized(int channelIndex)
    {
        if (channelIndex < 0 || channelIndex >= _channels.Count)
            return -1f;

        ChannelMappingItem channel = _channels[channelIndex];
        AudioTargetItem? target = FindTargetByKey(channel.TargetKey);

        if (target == null)
            return -1f;

        try
        {
            // ── VoiceMeeter path ──────────────────────────────────────────
            if (target.IsVoiceMeeter)
            {
                if (_voiceMeeterService == null || !_voiceMeeterService.IsAvailable) return -1f;
                return _voiceMeeterService.GetVolumeByKey(target.Key);
            }

            // ── WASAPI path ───────────────────────────────────────────────
            if (target.IsMaster)
            {
                EnsureAudioDevice();
                return Math.Clamp(_defaultRenderDevice!.AudioEndpointVolume.MasterVolumeLevelScalar, 0f, 1f);
            }

            if (target.IsMicInput)
            {
                if (_defaultCaptureDevice == null) return -1f;
                return Math.Clamp(_defaultCaptureDevice.AudioEndpointVolume.MasterVolumeLevelScalar, 0f, 1f);
            }

            var sessions = FindSessionsForKey(target.Key).ToList();

            if (sessions.Count == 0)
                return -1f;

            SimpleAudioVolume? vol = sessions[0].Session?.SimpleAudioVolume;
            return vol == null ? -1f : Math.Clamp(vol.Volume, 0f, 1f);
        }
        catch
        {
            return -1f;
        }
    }

    // Writes a normalized volume directly to the active audio backend, bypassing
    // the integer-percent round-trip used by ChangeChannelVolume.
    // Used exclusively by the smoothing path.
    private void SetChannelVolumeAbsolute(int channelIndex, float volumeNormalized)
    {
        if (channelIndex < 0 || channelIndex >= _channels.Count)
            return;

        ChannelMappingItem channel = _channels[channelIndex];
        AudioTargetItem? target = FindTargetByKey(channel.TargetKey);

        if (target == null)
            return;

        float v = Math.Clamp(volumeNormalized, 0f, 1f);

        try
        {
            // ── VoiceMeeter path ──────────────────────────────────────────
            if (target.IsVoiceMeeter)
            {
                _voiceMeeterService?.SetVolumeByKey(target.Key, v);
                return;
            }

            // ── WASAPI path ───────────────────────────────────────────────
            if (target.IsMaster)
            {
                EnsureAudioDevice();
                _defaultRenderDevice!.AudioEndpointVolume.MasterVolumeLevelScalar = v;

                if (_defaultRenderDevice.AudioEndpointVolume.Mute && v > 0f)
                    _defaultRenderDevice.AudioEndpointVolume.Mute = false;

                return;
            }

            if (target.IsMicInput)
            {
                if (_defaultCaptureDevice == null) return;
                _defaultCaptureDevice.AudioEndpointVolume.MasterVolumeLevelScalar = v;
                if (_defaultCaptureDevice.AudioEndpointVolume.Mute && v > 0f)
                    _defaultCaptureDevice.AudioEndpointVolume.Mute = false;
                return;
            }

            foreach (AudioTargetItem sessionTarget in FindSessionsForKey(target.Key))
            {
                SimpleAudioVolume? vol = sessionTarget.Session?.SimpleAudioVolume;

                if (vol == null)
                    continue;

                vol.Volume = v;

                if (vol.Mute && v > 0f)
                    vol.Mute = false;
            }
        }
        catch (System.Runtime.InteropServices.COMException comEx)
        {
            Log($"Audio session expired in SetChannelVolumeAbsolute (HRESULT 0x{comEx.HResult:X8}) — scheduling refresh.");
            Dispatcher.InvokeAsync(() =>
            {
                RefreshAudioSessions();
                RefreshAllChannelStates();
                SendAllChannelStatesToDevice();
            });
        }
        catch (Exception ex)
        {
            Log($"SetChannelVolumeAbsolute error: {ex.Message}");
        }
    }

    private void EnsureSmoothingTimerRunning()
    {
        if (_smoothingTimer == null)
        {
            _smoothingTimer = new ThreadingTimer(_ =>
            {
                try { Dispatcher.InvokeAsync(SmoothingTick); }
                catch (Exception ex) { Log($"Smoothing timer dispatch error: {ex.Message}"); }
            }, null, SmoothingTickMs, SmoothingTickMs);
        }
    }

    private void StopSmoothingTimer()
    {
        _smoothingTimer?.Dispose();
        _smoothingTimer = null;
    }

    // Called on the UI thread every SmoothingTickMs (~16 ms / 60 Hz).
    //
    // Each active channel advances one EMA step:
    //   next = current + alpha * (target - current)
    //
    // Working entirely in normalized float space (0.0–1.0) avoids the quantisation
    // artefacts that appear when intermediate volumes are rounded to integer percent.
    // SetChannelVolumeAbsolute writes the float directly to WASAPI — no conversion.
    //
    // IMPORTANT: we do NOT call RefreshAllChannelStates() here.  That method reads
    // sessions[0].Volume — a cached integer from AudioTargetItem — which is only
    // refreshed every 500 ms by StatePollTick.  Using it would overwrite the smooth
    // interpolated value we just computed with a stale snapshot, collapsing the
    // animation back to a step function on the main page and OLED preview.
    // Instead, _channels[ch].Volume is updated directly from _smoothingCurrentVolumes
    // so both the channel list and the OLED preview animate in lock-step with the audio.
    private void SmoothingTick()
    {
        bool anyActive = false;
        bool stateChanged = false;
        float alpha = GetSmoothingAlpha();

        for (int ch = 0; ch < ExpectedChannelCount; ch++)
        {
            if (!_smoothingActive[ch])
                continue;

            float target  = _smoothingTargetVolumes[ch];
            float current = _smoothingCurrentVolumes[ch];
            float diff    = target - current;
            float next;

            if (Math.Abs(diff) < SmoothingSnapThreshold)
            {
                // Within snap threshold — write the exact target and stop.
                next = target;
                _smoothingActive[ch] = false;
            }
            else
            {
                // EMA: exponentially approach target.
                next = current + alpha * diff;
                anyActive = true;
            }

            _smoothingCurrentVolumes[ch] = next;
            SetChannelVolumeAbsolute(ch, next);

            // Mirror the smooth float into the channel's integer display volume so
            // the channel list volume bar and the OLED preview panel both animate
            // frame-by-frame rather than snapping once per StatePollTick.
            if (ch < _channels.Count)
                _channels[ch].Volume = (int)Math.Round(next * 100);

            stateChanged = true;
        }

        if (stateChanged)
        {
            // Refresh only the visual layer — not RefreshAllChannelStates (see above).
            ChannelMappingsListView.Items.Refresh();
            UpdateSelectedChannelUi();
            UpdateOledPreviewPanels();

            SendAllChannelStatesToDevice();
            SendStateToDevice(force: true);
        }

        if (!anyActive)
            StopSmoothingTimer();
    }

    // ── Sensitivity / step calculation ────────────────────────────────────────────

    private int GetVolumeStepPercent() =>
        GetVolumeStepPercentFromSensitivity(_settings.EncoderSensitivityPercent);

    private int GetVolumeStepPercentForChannel(int channelIndex)
    {
        if (channelIndex >= 0 && channelIndex < _settings.Channels.Length)
        {
            int perChannel = _settings.Channels[channelIndex].SensitivityPercent;
            if (perChannel >= 0)
                return GetVolumeStepPercentFromSensitivity(perChannel);
        }
        return GetVolumeStepPercent(); // fall back to global
    }

    /// <summary>
    /// Returns the per-channel volume limits as a normalized [0.0, 1.0] range.
    /// Falls back to the full range (0, 1) when the channel index is out of bounds.
    /// </summary>
    private (float min, float max) GetChannelVolumeLimitsNormalized(int channelIndex)
    {
        if (channelIndex >= 0 && channelIndex < _settings.Channels.Length)
        {
            float min = _settings.Channels[channelIndex].MinVolumePercent / 100f;
            float max = _settings.Channels[channelIndex].MaxVolumePercent / 100f;
            return (min, max);
        }
        return (0f, 1f);
    }

    private int GetVolumeStepPercentFromSensitivity(int sensitivityPercent)
    {
        int sensitivity = Math.Clamp(sensitivityPercent, 0, MaxEncoderSensitivityPercent);

        if (sensitivity <= 0)
            return 1;

        double multiplier = sensitivity / 50.0;
        int step = (int)Math.Round(BaseVolumeStepPercent * multiplier);

        return Math.Clamp(step, 1, MaxVolumeStepPercent);
    }

    // ── Hardware diagnostics / preview ────────────────────────────────────────────

    private void RegisterHardwareEncoderEvent(string[] parts)
    {
        try
        {
            if (parts.Length < 3 || !int.TryParse(parts[1], out int channel) || !int.TryParse(parts[2], out int delta))
                return;
            if (channel < 0 || channel >= _hardwareEncoderCounts.Length)
                return;
            _hardwareEncoderCounts[channel] += delta;
            HighlightEncoderPreview(channel);
            UpdateHardwareTestSummary($"Encoder {channel + 1}: {(delta > 0 ? "+" : "")}{delta}");
        }
        catch
        {
        }
    }

    private void HighlightEncoderPreview(int channel)
    {
        try
        {
            Border?[] borders =
            {
                EncoderPreview1Highlight,
                EncoderPreview2Highlight,
                EncoderPreview3Highlight,
                EncoderPreview4Highlight,
                EncoderPreview5Highlight,
                EncoderPreview6Highlight
            };

            if (channel < 0 || channel >= borders.Length || borders[channel] == null)
                return;

            Border border = borders[channel]!;
            border.Background = Resources["PreviewEncoderActiveBackground"] as WpfBrush;
            border.BorderBrush = Resources["SelectedBackground"] as WpfBrush;

            if (_encoderHighlightTimers[channel] == null)
            {
                WpfDispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(180) };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    border.Background = WpfBrushes.Transparent;
                    border.BorderBrush = Resources["CardBorder"] as WpfBrush;
                };
                _encoderHighlightTimers[channel] = timer;
            }

            _encoderHighlightTimers[channel]!.Stop();
            _encoderHighlightTimers[channel]!.Start();
        }
        catch
        {
        }
    }
}
