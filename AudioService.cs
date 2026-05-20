using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace PcVolumeControllerDashboard;

/// <summary>
/// Owns the NAudio MMDeviceEnumerator, default render/capture devices,
/// and provides WASAPI get/set operations on AudioTargetItems.
/// Does not know about channels, channel mappings, or UI.
/// </summary>
internal sealed class AudioService : IDisposable
{
    private MMDeviceEnumerator? _enumerator;
    private MMDevice? _renderDevice;
    private MMDevice? _captureDevice;
    private AudioDeviceListener? _listener;

    // ─────────────────────────────────────────── state ──

    public MMDevice? RenderDevice  => _renderDevice;
    public MMDevice? CaptureDevice => _captureDevice;

    // ─────────────────────────────────────────── events ──

    /// <summary>Fired when the default audio device changes.</summary>
    public event Action? DefaultDeviceChanged;

    /// <summary>Fired when the audio device is unavailable (error on init/refresh).</summary>
    public event Action<string>? AudioDeviceError;

    // ─────────────────────────────────────────── init ──

    public void Initialise()
    {
        _enumerator = new MMDeviceEnumerator();
        _listener   = new AudioDeviceListener(() =>
        {
            DefaultDeviceChanged?.Invoke();
        });
        _enumerator.RegisterEndpointNotificationCallback(_listener);
        RefreshDefaultDevice();
    }

    // ─────────────────────────────────────────── device refresh ──

    /// <summary>
    /// Re-queries the default render and capture devices.
    /// Call this when DefaultDeviceChanged fires or on startup.
    /// </summary>
    public void RefreshDefaultDevice()
    {
        try
        {
            _renderDevice?.Dispose();
            _renderDevice = _enumerator!.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }
        catch (Exception ex)
        {
            _renderDevice = null;
            AudioDeviceError?.Invoke(ex.Message);
        }

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

    // ─────────────────────────────────────────── session enumeration ──

    /// <summary>
    /// Enumerates all active audio sessions on the default render device.
    /// Returns an empty list if no device is available.
    /// </summary>
    public List<AudioSessionControl> GetActiveSessions()
    {
        var result = new List<AudioSessionControl>();
        if (_renderDevice == null) return result;

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

        return result;
    }

    // ─────────────────────────────────────────── volume / mute ──

    /// <summary>Sets the volume (0..1) on the given audio target. Returns false if the target is unavailable.</summary>
    public bool SetVolume(AudioTargetItem target, float volume)
    {
        float v = Math.Clamp(volume, 0f, 1f);
        try
        {
            if (target.IsMaster)
            {
                if (_renderDevice == null) return false;
                _renderDevice.AudioEndpointVolume.MasterVolumeLevelScalar = v;
                if (_renderDevice.AudioEndpointVolume.Mute && v > 0f)
                    _renderDevice.AudioEndpointVolume.Mute = false;
                return true;
            }

            if (target.IsMicInput)
            {
                if (_captureDevice == null) return false;
                _captureDevice.AudioEndpointVolume.MasterVolumeLevelScalar = v;
                if (_captureDevice.AudioEndpointVolume.Mute && v > 0f)
                    _captureDevice.AudioEndpointVolume.Mute = false;
                return true;
            }

            // Per-session
            SimpleAudioVolume? vol = target.Session?.SimpleAudioVolume;
            if (vol == null) return false;
            vol.Volume = v;
            if (vol.Mute && v > 0f) vol.Mute = false;
            return true;
        }
        catch { return false; }
    }

    /// <summary>Gets the current volume (0..1) for the given target. Returns -1 on failure.</summary>
    public float GetVolume(AudioTargetItem target)
    {
        try
        {
            if (target.IsMaster)
                return _renderDevice?.AudioEndpointVolume.MasterVolumeLevelScalar ?? -1f;
            if (target.IsMicInput)
                return _captureDevice?.AudioEndpointVolume.MasterVolumeLevelScalar ?? -1f;
            return target.Session?.SimpleAudioVolume?.Volume ?? -1f;
        }
        catch { return -1f; }
    }

    /// <summary>Sets the mute state on the given target.</summary>
    public bool SetMute(AudioTargetItem target, bool mute)
    {
        try
        {
            if (target.IsMaster)
            {
                if (_renderDevice == null) return false;
                _renderDevice.AudioEndpointVolume.Mute = mute;
                return true;
            }
            if (target.IsMicInput)
            {
                if (_captureDevice == null) return false;
                _captureDevice.AudioEndpointVolume.Mute = mute;
                return true;
            }
            SimpleAudioVolume? vol = target.Session?.SimpleAudioVolume;
            if (vol == null) return false;
            vol.Mute = mute;
            return true;
        }
        catch { return false; }
    }

    /// <summary>Gets the mute state of the given target. Returns null on failure.</summary>
    public bool? GetMute(AudioTargetItem target)
    {
        try
        {
            if (target.IsMaster)
                return _renderDevice?.AudioEndpointVolume.Mute;
            if (target.IsMicInput)
                return _captureDevice?.AudioEndpointVolume.Mute;
            return target.Session?.SimpleAudioVolume?.Mute;
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
