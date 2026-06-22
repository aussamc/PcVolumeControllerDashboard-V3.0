using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using PcVolumeControllerDashboard.Core.Audio;

namespace PcVolumeControllerDashboard.Platform.Windows;

/// <summary>
/// Windows WASAPI audio backend (NAudio). Implements the neutral
/// <see cref="IAudioBackend"/> for the default render (master) and capture
/// (microphone) endpoints plus per-application sessions.
///
/// Absorbs the WPF host's former <c>AudioService</c> (device enumeration, the
/// 100 ms session cache, COM-pointer lifetime management) together with the
/// host's session→target resolution, label disambiguation and master/mic
/// endpoint handling, so the live <c>AudioSessionControl</c> handles never leave
/// this assembly — callers address everything by <see cref="AudioTarget.Key"/>.
///
/// Keys: <c>MASTER</c>, <c>MIC_INPUT</c>, <c>PROC:&lt;processName&gt;</c>.
/// A <c>PROC:</c> key can map to several live sessions (e.g. two browser
/// windows); volume/mute operations apply to every matching session.
/// </summary>
public sealed class WasapiAudioBackend : IAudioBackend
{
    private MMDeviceEnumerator? _enumerator;
    private MMDevice? _renderDevice;
    private MMDevice? _captureDevice;
    private AudioDeviceListener? _listener;
    private bool _isAvailable;

    private List<AudioSessionControl> _sessionCache = new();
    private DateTime _sessionCacheExpiry = DateTime.MinValue;
    private const int SessionCacheTtlMs = 100;

    // Render-endpoint devices enumerated for the current session list. Held alive
    // because the AudioSessionControl objects borrow COM pointers from each
    // device's AudioSessionManager. Disposed at the start of the next enumeration.
    private readonly List<MMDevice> _enumeratedRenderDevices = new();

    private readonly Action<string> _log;

    public string BackendName => "WASAPI";

    public bool IsAvailable => _isAvailable;

    public event Action? AvailabilityChanged;

    public event Action? TargetsChanged;

    /// <param name="logger">Optional diagnostics delegate; may be invoked on any thread.</param>
    public WasapiAudioBackend(Action<string>? logger = null)
    {
        _log = logger ?? (_ => { });
    }

    // ─────────────────────────────────────────── init ──

    public void Initialise()
    {
        _enumerator = new MMDeviceEnumerator();
        _listener = new AudioDeviceListener(() =>
        {
            // Default device changed: re-query and tell the host to refresh.
            RefreshDefaultDevice();
            try { TargetsChanged?.Invoke(); } catch { }
        });
        _enumerator.RegisterEndpointNotificationCallback(_listener);
        RefreshDefaultDevice();
        SetAvailability(true);
    }

    private void SetAvailability(bool available)
    {
        if (_isAvailable == available) return;
        _isAvailable = available;
        try { AvailabilityChanged?.Invoke(); } catch { }
    }

    // ─────────────────────────────────────────── device refresh ──

    /// <summary>Re-queries the default render and capture devices.</summary>
    private void RefreshDefaultDevice()
    {
        try
        {
            _renderDevice?.Dispose();
            _renderDevice = _enumerator!.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }
        catch (Exception ex)
        {
            _renderDevice = null;
            _log($"WasapiAudioBackend: render device unavailable: {ex.Message}");
        }

        InvalidateCache();

        try
        {
            _captureDevice?.Dispose();
            _captureDevice = _enumerator!.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
        }
        catch
        {
            _captureDevice = null;
        }
    }

    public void InvalidateCache()
    {
        _sessionCache = new List<AudioSessionControl>();
        _sessionCacheExpiry = DateTime.MinValue;
    }

    // ─────────────────────────────────────────── session enumeration ──

    /// <summary>
    /// Enumerates active audio sessions across <b>every</b> active render
    /// endpoint (not just the default device), so apps rendering to a non-default
    /// output are still seen. Cached for <see cref="SessionCacheTtlMs"/> ms to
    /// avoid hammering <c>RefreshSessions()</c> during volume smoothing.
    /// </summary>
    private List<AudioSessionControl> GetActiveSessions()
    {
        if (DateTime.Now < _sessionCacheExpiry && _sessionCache.Count > 0)
            return _sessionCache;

        var result = new List<AudioSessionControl>();

        DisposeEnumeratedRenderDevices();

        if (_enumerator != null)
        {
            try
            {
                MMDeviceCollection renderDevices =
                    _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);

                for (int d = 0; d < renderDevices.Count; d++)
                {
                    MMDevice device = renderDevices[d];
                    _enumeratedRenderDevices.Add(device);

                    try
                    {
                        AudioSessionManager mgr = device.AudioSessionManager;
                        mgr.RefreshSessions();
                        SessionCollection sessions = mgr.Sessions;
                        if (sessions != null)
                        {
                            for (int i = 0; i < sessions.Count; i++)
                                result.Add(sessions[i]);
                        }
                    }
                    catch { /* skip this endpoint, keep enumerating the rest */ }
                }
            }
            catch { /* fall back to default-device-only enumeration below */ }
        }

        if (result.Count == 0 && _renderDevice != null)
        {
            try
            {
                AudioSessionManager mgr = _renderDevice.AudioSessionManager;
                mgr.RefreshSessions();
                SessionCollection sessions = mgr.Sessions;
                if (sessions != null)
                {
                    for (int i = 0; i < sessions.Count; i++)
                        result.Add(sessions[i]);
                }
            }
            catch { /* returns partial list */ }
        }

        _sessionCache = result;
        _sessionCacheExpiry = DateTime.Now.AddMilliseconds(SessionCacheTtlMs);
        return result;
    }

    private void DisposeEnumeratedRenderDevices()
    {
        foreach (MMDevice device in _enumeratedRenderDevices)
        {
            try { device.Dispose(); } catch { }
        }
        _enumeratedRenderDevices.Clear();
    }

    // ─────────────────────────────────────────── target enumeration ──

    public IReadOnlyList<AudioTarget> GetAvailableTargets()
    {
        var targets = new List<AudioTarget>();

        targets.Add(AudioTarget.CreateMaster());
        if (_captureDevice != null)
            targets.Add(AudioTarget.CreateMic());

        // Collect all enumerable sessions, then disambiguate labels when the same
        // process produces more than one session: first instance keeps the bare
        // name ("chrome"), subsequent ones are suffixed ("chrome (2)", …).
        var sessionTargets = new List<AudioTarget>();
        foreach (AudioSessionControl session in GetActiveSessions())
        {
            AudioTarget? target = TryCreateTargetFromSession(session);
            if (target != null)
                sessionTargets.Add(target);
        }

        var processNameCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (AudioTarget t in sessionTargets)
        {
            processNameCounts.TryGetValue(t.ProcessName, out int seen);
            processNameCounts[t.ProcessName] = seen + 1;
            if (seen > 0)
                t.Label = $"{t.ProcessName} ({seen + 1})";
        }

        targets.AddRange(sessionTargets);
        return targets;
    }

    private static AudioTarget? TryCreateTargetFromSession(
        AudioSessionControl session,
        string? requiredProcessName = null)
    {
        try
        {
            uint pidRaw = session.GetProcessID;
            if (pidRaw == 0 || session.SimpleAudioVolume == null)
                return null;

            using Process process = Process.GetProcessById((int)pidRaw);

            if (!string.IsNullOrWhiteSpace(requiredProcessName) &&
                !process.ProcessName.Equals(requiredProcessName, StringComparison.OrdinalIgnoreCase))
                return null;

            return new AudioTarget
            {
                Key         = $"PROC:{process.ProcessName}",
                Label       = process.ProcessName,
                ProcessName = process.ProcessName,
                ProcessId   = (int)pidRaw,
                IsLive      = true,
                Volume      = Math.Clamp((int)Math.Round(session.SimpleAudioVolume.Volume * 100), 0, 100),
                Muted       = session.SimpleAudioVolume.Mute,
                State       = session.State.ToString(),
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Returns the live sessions whose process matches a PROC: key.</summary>
    private IEnumerable<AudioSessionControl> ResolveSessions(string key)
    {
        string processName = key.StartsWith("PROC:", StringComparison.OrdinalIgnoreCase) ? key[5..] : key;

        foreach (AudioSessionControl session in GetActiveSessions())
        {
            bool matches;
            try
            {
                uint pidRaw = session.GetProcessID;
                if (pidRaw == 0 || session.SimpleAudioVolume == null) continue;
                using Process process = Process.GetProcessById((int)pidRaw);
                matches = process.ProcessName.Equals(processName, StringComparison.OrdinalIgnoreCase);
            }
            catch { continue; }

            if (matches) yield return session;
        }
    }

    // ─────────────────────────────────────────── volume / mute ──

    private static bool IsMasterKey(string key) => key.Equals("MASTER", StringComparison.OrdinalIgnoreCase);
    private static bool IsMicKey(string key) => key.Equals("MIC_INPUT", StringComparison.OrdinalIgnoreCase);

    public float GetVolumeByKey(string key)
    {
        try
        {
            if (IsMasterKey(key))
                return _renderDevice?.AudioEndpointVolume.MasterVolumeLevelScalar ?? -1f;
            if (IsMicKey(key))
                return _captureDevice?.AudioEndpointVolume.MasterVolumeLevelScalar ?? -1f;

            foreach (AudioSessionControl session in ResolveSessions(key))
            {
                SimpleAudioVolume? vol = session.SimpleAudioVolume;
                if (vol != null) return vol.Volume;
            }
            return -1f;
        }
        catch { return -1f; }
    }

    public bool SetVolumeByKey(string key, float normalizedVolume)
    {
        float v = Math.Clamp(normalizedVolume, 0f, 1f);
        try
        {
            if (IsMasterKey(key))
            {
                if (_renderDevice == null) return false;
                _renderDevice.AudioEndpointVolume.MasterVolumeLevelScalar = v;
                if (_renderDevice.AudioEndpointVolume.Mute && v > 0f)
                    _renderDevice.AudioEndpointVolume.Mute = false;
                return true;
            }
            if (IsMicKey(key))
            {
                if (_captureDevice == null) return false;
                _captureDevice.AudioEndpointVolume.MasterVolumeLevelScalar = v;
                if (_captureDevice.AudioEndpointVolume.Mute && v > 0f)
                    _captureDevice.AudioEndpointVolume.Mute = false;
                return true;
            }

            bool any = false;
            foreach (AudioSessionControl session in ResolveSessions(key))
            {
                SimpleAudioVolume? vol = session.SimpleAudioVolume;
                if (vol == null) continue;
                vol.Volume = v;
                if (vol.Mute && v > 0f) vol.Mute = false;
                any = true;
            }
            return any;
        }
        catch { return false; }
    }

    public int AdjustVolumeByKey(string key, int deltaPercent, int minPercent, int maxPercent)
    {
        try
        {
            if (IsMasterKey(key))
            {
                if (_renderDevice == null) return -1;
                AudioEndpointVolume epv = _renderDevice.AudioEndpointVolume;
                int current = Math.Clamp((int)Math.Round(epv.MasterVolumeLevelScalar * 100), 0, 100);
                int next = Math.Clamp(current + deltaPercent, minPercent, maxPercent);
                epv.MasterVolumeLevelScalar = next / 100.0f;
                if (epv.Mute && next > 0) epv.Mute = false;
                return next;
            }
            if (IsMicKey(key))
            {
                if (_captureDevice == null) return -1;
                AudioEndpointVolume epv = _captureDevice.AudioEndpointVolume;
                int current = Math.Clamp((int)Math.Round(epv.MasterVolumeLevelScalar * 100), 0, 100);
                int next = Math.Clamp(current + deltaPercent, minPercent, maxPercent);
                epv.MasterVolumeLevelScalar = next / 100.0f;
                if (epv.Mute && next > 0) epv.Mute = false;
                return next;
            }

            // Per-session: apply the delta to each matching session independently
            // (matching the WPF host). Report the first session's new value.
            int representative = -1;
            foreach (AudioSessionControl session in ResolveSessions(key))
            {
                SimpleAudioVolume? vol = session.SimpleAudioVolume;
                if (vol == null) continue;

                int current = Math.Clamp((int)Math.Round(vol.Volume * 100), 0, 100);
                int next = Math.Clamp(current + deltaPercent, minPercent, maxPercent);
                vol.Volume = next / 100.0f;
                if (vol.Mute && next > 0) vol.Mute = false;

                if (representative < 0) representative = next;
            }
            return representative;
        }
        catch { return -1; }
    }

    public bool? GetMuteByKey(string key)
    {
        try
        {
            if (IsMasterKey(key))
                return _renderDevice?.AudioEndpointVolume.Mute;
            if (IsMicKey(key))
                return _captureDevice?.AudioEndpointVolume.Mute;

            foreach (AudioSessionControl session in ResolveSessions(key))
            {
                SimpleAudioVolume? vol = session.SimpleAudioVolume;
                if (vol != null) return vol.Mute;
            }
            return null;
        }
        catch { return null; }
    }

    public bool SetMuteByKey(string key, bool mute)
    {
        try
        {
            if (IsMasterKey(key))
            {
                if (_renderDevice == null) return false;
                _renderDevice.AudioEndpointVolume.Mute = mute;
                return true;
            }
            if (IsMicKey(key))
            {
                if (_captureDevice == null) return false;
                _captureDevice.AudioEndpointVolume.Mute = mute;
                return true;
            }

            bool any = false;
            foreach (AudioSessionControl session in ResolveSessions(key))
            {
                SimpleAudioVolume? vol = session.SimpleAudioVolume;
                if (vol == null) continue;
                vol.Mute = mute;
                any = true;
            }
            return any;
        }
        catch { return false; }
    }

    public bool? ToggleMuteByKey(string key)
    {
        try
        {
            if (IsMasterKey(key))
            {
                if (_renderDevice == null) return null;
                AudioEndpointVolume epv = _renderDevice.AudioEndpointVolume;
                epv.Mute = !epv.Mute;
                return epv.Mute;
            }
            if (IsMicKey(key))
            {
                if (_captureDevice == null) return null;
                AudioEndpointVolume epv = _captureDevice.AudioEndpointVolume;
                epv.Mute = !epv.Mute;
                return epv.Mute;
            }

            // First matching session decides the new state; apply it to all.
            var sessions = ResolveSessions(key).ToList();
            if (sessions.Count == 0) return null;

            bool? first = sessions[0].SimpleAudioVolume?.Mute;
            bool next = !(first ?? false);
            foreach (AudioSessionControl session in sessions)
            {
                SimpleAudioVolume? vol = session.SimpleAudioVolume;
                if (vol != null) vol.Mute = next;
            }
            return next;
        }
        catch { return null; }
    }

    // ─────────────────────────────────────────── IDisposable ──

    public void Dispose()
    {
        if (_listener != null && _enumerator != null)
        {
            try { _enumerator.UnregisterEndpointNotificationCallback(_listener); } catch { }
        }
        DisposeEnumeratedRenderDevices();
        _renderDevice?.Dispose();
        _captureDevice?.Dispose();
        _enumerator?.Dispose();
        _renderDevice  = null;
        _captureDevice = null;
        _enumerator    = null;
    }

    // ─────────────────────────────────────────── nested: device listener ──

    private sealed class AudioDeviceListener : IMMNotificationClient
    {
        private readonly Action _onChange;
        public AudioDeviceListener(Action onChange) => _onChange = onChange;

        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            if ((flow == DataFlow.Render && role == Role.Multimedia) ||
                (flow == DataFlow.Capture && role == Role.Communications))
                _onChange();
        }

        public void OnDeviceAdded(string pwstrDeviceId)       { }
        public void OnDeviceRemoved(string deviceId)          { }
        public void OnDeviceStateChanged(string deviceId, DeviceState newState) { }
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
    }
}
