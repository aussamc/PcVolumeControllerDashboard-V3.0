using System;
using System.Collections.Generic;
using PcVolumeControllerDashboard.Core.Audio;

namespace PcVolumeControllerDashboard.App.Audio;

/// <summary>
/// An <see cref="IAudioBackend"/> that wraps a swappable inner backend so the
/// audio backend (WASAPI ↔ VoiceMeeter) can change at runtime without rebuilding
/// the DI graph. Consumers (ChannelRuntime, DeviceStateService, the UI) hold this
/// stable wrapper; <see cref="SwitchTo"/> rebuilds the inner backend and re-raises
/// <see cref="TargetsChanged"/> so the host re-enumerates.
/// </summary>
public sealed class SwitchableAudioBackend : IAudioBackend
{
    private readonly Func<string, IAudioBackend> _factory;
    private readonly Action<string>? _log;
    private IAudioBackend _inner;

    public string CurrentMode { get; private set; }

    public event Action? AvailabilityChanged;
    public event Action? TargetsChanged;

    public SwitchableAudioBackend(Func<string, IAudioBackend> factory, string mode, Action<string>? log = null)
    {
        _factory = factory;
        _log = log;
        CurrentMode = mode;
        _inner = _factory(mode);
        Wire(_inner);
    }

    public string BackendName => _inner.BackendName;
    public bool IsAvailable => _inner.IsAvailable;

    public void Initialise() => _inner.Initialise();

    /// <summary>
    /// Rebuilds the inner backend for <paramref name="mode"/> (no-op if unchanged).
    /// On a build/initialise failure it falls back to a null backend so the app
    /// stays responsive rather than throwing into the UI.
    /// </summary>
    public void SwitchTo(string mode)
    {
        if (string.Equals(mode, CurrentMode, StringComparison.OrdinalIgnoreCase))
            return;

        Unwire(_inner);
        try { _inner.Dispose(); } catch { /* ignore disposal faults */ }

        IAudioBackend next;
        try
        {
            next = _factory(mode);
            next.Initialise();
            _log?.Invoke($"Audio backend switched to {mode} ({next.BackendName}).");
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Audio backend '{mode}' failed to start ({ex.Message}); using null backend.");
            next = new NullAudioBackend();
            try { next.Initialise(); } catch { }
        }

        _inner = next;
        CurrentMode = mode;
        Wire(_inner);

        AvailabilityChanged?.Invoke();
        TargetsChanged?.Invoke();
    }

    private void Wire(IAudioBackend backend)
    {
        backend.AvailabilityChanged += OnInnerAvailabilityChanged;
        backend.TargetsChanged += OnInnerTargetsChanged;
    }

    private void Unwire(IAudioBackend backend)
    {
        backend.AvailabilityChanged -= OnInnerAvailabilityChanged;
        backend.TargetsChanged -= OnInnerTargetsChanged;
    }

    private void OnInnerAvailabilityChanged() => AvailabilityChanged?.Invoke();
    private void OnInnerTargetsChanged() => TargetsChanged?.Invoke();

    // ── Delegated operations ─────────────────────────────────────────────────
    public IReadOnlyList<AudioTarget> GetAvailableTargets() => _inner.GetAvailableTargets();
    public float GetVolumeByKey(string key) => _inner.GetVolumeByKey(key);
    public bool IsKeyActive(string key) => _inner.IsKeyActive(key);
    public bool SetVolumeByKey(string key, float normalizedVolume) => _inner.SetVolumeByKey(key, normalizedVolume);
    public int AdjustVolumeByKey(string key, int deltaPercent, int minPercent, int maxPercent) =>
        _inner.AdjustVolumeByKey(key, deltaPercent, minPercent, maxPercent);
    public bool? GetMuteByKey(string key) => _inner.GetMuteByKey(key);
    public bool SetMuteByKey(string key, bool mute) => _inner.SetMuteByKey(key, mute);
    public bool? ToggleMuteByKey(string key) => _inner.ToggleMuteByKey(key);
    public void InvalidateCache() => _inner.InvalidateCache();

    public void Dispose()
    {
        Unwire(_inner);
        try { _inner.Dispose(); } catch { }
    }
}
