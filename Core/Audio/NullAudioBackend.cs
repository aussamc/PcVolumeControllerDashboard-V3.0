namespace PcVolumeControllerDashboard.Core.Audio;

/// <summary>
/// No-op <see cref="IAudioBackend"/> used on platforms that don't yet have a real
/// backend (e.g. Linux/macOS before their platform layers land), and as a safe
/// default so hosts never hold a null backend. Reports unavailable, enumerates
/// nothing, and fails all operations without throwing.
/// </summary>
public sealed class NullAudioBackend : IAudioBackend
{
    public string BackendName => "None";

    public bool IsAvailable => false;

    public event Action? AvailabilityChanged { add { } remove { } }
    public event Action? TargetsChanged { add { } remove { } }

    public void Initialise() { }

    public IReadOnlyList<AudioTarget> GetAvailableTargets() => Array.Empty<AudioTarget>();

    public float GetVolumeByKey(string key) => -1f;
    public bool SetVolumeByKey(string key, float normalizedVolume) => false;
    public int AdjustVolumeByKey(string key, int deltaPercent, int minPercent, int maxPercent) => -1;
    public bool? GetMuteByKey(string key) => null;
    public bool SetMuteByKey(string key, bool mute) => false;
    public bool? ToggleMuteByKey(string key) => null;

    public void InvalidateCache() { }

    public void Dispose() { }
}
